using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using LiteNetLib;
using Steamworks;

namespace CoopNet
{
    /// <summary>
    /// Steam-транспорт коопа Lucky Tower: Spacewar (AppID 480, steam_appid.txt рядом с exe).
    /// Лобби только для друзей, P2P через SteamNetworkingMessages (NAT/relay Valve).
    /// Канал 0 — данные игры, канал 1 — служебные сообщения транспорта.
    /// С плагином общается по LiteNetLib на 127.0.0.1 (порт в --ipc).
    /// </summary>
    internal static class Program
    {
        private const byte IpcData = 1;
        private const byte IpcText = 2;
        private const string IpcKey = "LTCOOP-IPC";
        private const string LobbyKey = "ltcoop";
        private const int DataChannel = 0;
        private const int ControlChannel = 1;

        private static NetManager s_ipc;
        private static NetPeer s_plugin;
        private static bool s_quit;

        private static CSteamID s_lobby = CSteamID.Nil;
        private static bool s_isHost;
        private static SteamNetworkingIdentity s_remote;
        private static bool s_hasRemote;
        private static bool s_remoteConnected;
        private static DateTime s_lastRemoteMsg;
        private static DateTime s_lastPing;

        private static CallResult<LobbyCreated_t> s_lobbyCreated;
        private static CallResult<LobbyEnter_t> s_lobbyEnter;
        private static Callback<SteamNetworkingMessagesSessionRequest_t> s_sessionRequest;
        private static Callback<SteamNetworkingMessagesSessionFailed_t> s_sessionFailed;
        private static Callback<LobbyChatUpdate_t> s_lobbyChatUpdate;
        private static Callback<GameLobbyJoinRequested_t> s_joinRequested;

        private static readonly IntPtr[] s_msgs = new IntPtr[64];

        // Без запроса точного таймера Windows будит процесс раз в 15,6 мс: Thread.Sleep(1) в цикле и тик LiteNetLib
        // (UpdateTime=1) на деле длились 15,6 мс, и каждый пакет ждал до 2 таких тиков в каждом CoopNet.
        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint ms);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint ms);

        private static int Main(string[] args)
        {
            int ipcPort = 0;
            int parentPid = 0;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--ipc")
                {
                    int.TryParse(args[i + 1], out ipcPort);
                }
                else if (args[i] == "--parent")
                {
                    int.TryParse(args[i + 1], out parentPid);
                }
            }
            if (ipcPort == 0)
            {
                Console.Error.WriteLine("usage: CoopNet --ipc <port> [--parent <pid>]");
                return 2;
            }

            try { timeBeginPeriod(1); } catch { }

            // IPC сначала — чтобы сообщить плагину об ошибке Steam
            var listener = new EventBasedNetListener();
            s_ipc = new NetManager(listener) { AutoRecycle = true, UnsyncedEvents = false, DisconnectTimeout = 15000, UpdateTime = 1 };
            listener.PeerConnectedEvent += p => s_plugin = p;
            listener.PeerDisconnectedEvent += (p, i) => s_quit = true;
            listener.NetworkReceiveEvent += (p, r, ch, m) => OnIpc(r.RawData, r.Position, r.AvailableBytes);
            s_ipc.Start();
            s_ipc.Connect("127.0.0.1", ipcPort, IpcKey);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (s_plugin == null && DateTime.UtcNow < deadline)
            {
                s_ipc.PollEvents();
                Thread.Sleep(10);
            }
            if (s_plugin == null)
            {
                return 3;
            }
            {
                // самопроверка таймера: сколько на деле длится Sleep(1) (без timeBeginPeriod было 15,6 мс)
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(1);
                }
                Event($"log timer: Sleep(1) = {sw.Elapsed.TotalMilliseconds / 20:F1} ms");
            }

            bool steamOk;
            try
            {
                steamOk = SteamAPI.Init();
            }
            catch (Exception e)
            {
                Event("error steam_api64.dll: " + e.Message);
                steamOk = false;
            }
            if (!steamOk)
            {
                Event("error Steam не запущен или не удалось инициализировать Spacewar (AppID 480)");
                Flush();
                return 4;
            }

            SteamNetworkingUtils.InitRelayNetworkAccess();
            EnableDirectP2P();
            s_lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            s_lobbyEnter = CallResult<LobbyEnter_t>.Create(OnLobbyEnter);
            s_sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            s_sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
            s_lobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            s_joinRequested = Callback<GameLobbyJoinRequested_t>.Create(r => JoinLobby(r.m_steamIDLobby));

            Event($"ready {SteamFriends.GetPersonaName()}");
            Event($"log steam ok: {SteamFriends.GetPersonaName()} ({SteamUser.GetSteamID()})");

            while (!s_quit)
            {
                SteamAPI.RunCallbacks();
                s_ipc.PollEvents();
                PumpSteamMessages();
                Tick();
                if (parentPid != 0 && !IsAlive(parentPid))
                {
                    break;
                }
                Thread.Sleep(1);
            }

            Leave();
            try { timeEndPeriod(1); } catch { }
            SteamAPI.Shutdown();
            s_ipc.Stop();
            return 0;
        }

        /// <summary>Разрешаем прямое P2P-соединение (ICE) со всеми, не только через ретрансляторы Valve — ниже пинг, если NAT пропускает.</summary>
        private static void EnableDirectP2P()
        {
            int value = Constants.k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_All;
            var handle = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                bool ok = SteamNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    handle.AddrOfPinnedObject());
                Event($"log direct P2P (ICE) {(ok ? "enabled" : "not available")}");
            }
            finally
            {
                handle.Free();
            }
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                return !Process.GetProcessById(pid).HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        // ---------------- IPC ----------------

        private static void Event(string text)
        {
            if (s_plugin == null)
            {
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(text);
            var buf = new byte[bytes.Length + 1];
            buf[0] = IpcText;
            Buffer.BlockCopy(bytes, 0, buf, 1, bytes.Length);
            s_plugin.Send(buf, 0, buf.Length, 0, DeliveryMethod.ReliableOrdered);
        }

        private static void Flush()
        {
            for (int i = 0; i < 20; i++)
            {
                s_ipc.PollEvents();
                Thread.Sleep(10);
            }
        }

        private static void OnIpc(byte[] data, int offset, int length)
        {
            if (length < 1)
            {
                return;
            }
            if (data[offset] == IpcData && length >= 2)
            {
                bool reliable = data[offset + 1] == 1;
                SendToRemote(data, offset + 2, length - 2, reliable, DataChannel);
                return;
            }
            if (data[offset] != IpcText)
            {
                return;
            }
            string text = Encoding.UTF8.GetString(data, offset + 1, length - 1);
            int sp = text.IndexOf(' ');
            string cmd = sp < 0 ? text : text.Substring(0, sp);
            string arg = sp < 0 ? "" : text.Substring(sp + 1);
            switch (cmd)
            {
                case "host":
                    s_isHost = true;
                    s_lobbyCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2));
                    break;
                case "join":
                    if (ulong.TryParse(arg, out var id))
                    {
                        JoinLobby(new CSteamID(id));
                    }
                    break;
                case "friends":
                    SendFriends();
                    break;
                case "invite":
                    if (ulong.TryParse(arg, out var friend) && s_lobby.IsValid())
                    {
                        SteamMatchmaking.InviteUserToLobby(s_lobby, new CSteamID(friend));
                        Event($"log invited {friend}");
                    }
                    break;
                case "leave":
                    Leave();
            try { timeEndPeriod(1); } catch { }
                    break;
                case "quit":
                    s_quit = true;
                    break;
            }
        }

        // ---------------- лобби ----------------

        private static void OnLobbyCreated(LobbyCreated_t r, bool ioFailure)
        {
            if (ioFailure || r.m_eResult != EResult.k_EResultOK)
            {
                Event($"error не удалось создать лобби: {r.m_eResult}");
                return;
            }
            s_lobby = new CSteamID(r.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(s_lobby, LobbyKey, "1");
            SteamMatchmaking.SetLobbyData(s_lobby, "host", SteamFriends.GetPersonaName());
            SteamMatchmaking.SetLobbyJoinable(s_lobby, true);
            SteamFriends.SetRichPresence("status", "Lucky Tower Ultimate — кооп (хост)");
            Event($"lobby {s_lobby.m_SteamID}");
        }

        private static void JoinLobby(CSteamID lobby)
        {
            s_isHost = false;
            if (s_lobby.IsValid() && s_lobby != lobby)
            {
                SteamMatchmaking.LeaveLobby(s_lobby);
            }
            s_lobbyEnter.Set(SteamMatchmaking.JoinLobby(lobby));
        }

        private static void OnLobbyEnter(LobbyEnter_t r, bool ioFailure)
        {
            if (ioFailure || r.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Event($"error не удалось войти в лобби ({(EChatRoomEnterResponse)r.m_EChatRoomEnterResponse})");
                return;
            }
            s_lobby = new CSteamID(r.m_ulSteamIDLobby);
            Event($"lobby {s_lobby.m_SteamID}");
            if (s_isHost)
            {
                return;
            }
            var owner = SteamMatchmaking.GetLobbyOwner(s_lobby);
            if (owner == SteamUser.GetSteamID())
            {
                Event("error это ваше собственное лобби");
                return;
            }
            SetRemote(owner);
            SteamFriends.SetRichPresence("status", "Lucky Tower Ultimate — кооп");
            SendControl("connect");
        }

        private static void OnLobbyChatUpdate(LobbyChatUpdate_t r)
        {
            if (r.m_ulSteamIDLobby != s_lobby.m_SteamID || !s_hasRemote)
            {
                return;
            }
            const uint leftMask = (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
                                         | EChatMemberStateChange.k_EChatMemberStateChangeKicked | EChatMemberStateChange.k_EChatMemberStateChangeBanned);
            if (r.m_ulSteamIDUserChanged == s_remote.GetSteamID().m_SteamID && (r.m_rgfChatMemberStateChange & leftMask) != 0)
            {
                RemoteLost("друг вышел из лобби");
            }
        }

        private static void Leave()
        {
            if (s_hasRemote)
            {
                SendControl("bye");
                SteamNetworkingMessages.CloseSessionWithUser(ref s_remote);
            }
            if (s_lobby.IsValid())
            {
                SteamMatchmaking.LeaveLobby(s_lobby);
            }
            s_lobby = CSteamID.Nil;
            s_hasRemote = false;
            s_remoteConnected = false;
        }

        private static bool IsLobbyMember(CSteamID user)
        {
            if (!s_lobby.IsValid())
            {
                return false;
            }
            int n = SteamMatchmaking.GetNumLobbyMembers(s_lobby);
            for (int i = 0; i < n; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(s_lobby, i) == user)
                {
                    return true;
                }
            }
            return false;
        }

        private static void SendFriends()
        {
            var lobbies = new StringBuilder();
            var online = new StringBuilder();
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < count; i++)
            {
                var f = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                string name = SteamFriends.GetFriendPersonaName(f).Replace('\t', ' ').Replace('\n', ' ');
                if (SteamFriends.GetFriendPersonaState(f) != EPersonaState.k_EPersonaStateOffline)
                {
                    online.Append(online.Length > 0 ? "\n" : "").Append(f.m_SteamID).Append('\t').Append(name);
                }
                if (SteamFriends.GetFriendGamePlayed(f, out var info) && info.m_gameID.AppID() == new AppId_t(480) && info.m_steamIDLobby.IsValid())
                {
                    lobbies.Append(lobbies.Length > 0 ? "\n" : "").Append(info.m_steamIDLobby.m_SteamID).Append('\t').Append(name);
                }
            }
            Event("friends " + lobbies);
            Event("online " + online);
        }

        // ---------------- P2P ----------------

        private static void SetRemote(CSteamID id)
        {
            s_remote = new SteamNetworkingIdentity();
            s_remote.SetSteamID(id);
            s_hasRemote = true;
            s_lastRemoteMsg = DateTime.UtcNow;
        }

        private static void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t r)
        {
            var who = r.m_identityRemote.GetSteamID();
            if (s_isHost && IsLobbyMember(who))
            {
                var identity = r.m_identityRemote;
                SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
                Event($"log accepted session from {who}");
            }
            else if (!s_isHost && s_hasRemote && who == s_remote.GetSteamID())
            {
                var identity = r.m_identityRemote;
                SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
            }
            else
            {
                Event($"log rejected session from {who}");
            }
        }

        private static void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t r)
        {
            if (s_hasRemote && r.m_info.m_identityRemote.GetSteamID() == s_remote.GetSteamID())
            {
                RemoteLost($"сессия Steam прервана ({(ESteamNetConnectionEnd)r.m_info.m_eEndReason})");
            }
        }

        private static void RemoteLost(string reason)
        {
            if (s_remoteConnected)
            {
                s_remoteConnected = false;
                Event("peer-disconnected " + reason);
            }
        }

        private static unsafe void SendToRemote(byte[] data, int offset, int length, bool reliable, int channel)
        {
            if (!s_hasRemote)
            {
                return;
            }
            int flags = (reliable ? Constants.k_nSteamNetworkingSend_ReliableNoNagle : Constants.k_nSteamNetworkingSend_UnreliableNoNagle)
                        | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
            fixed (byte* p = &data[offset])
            {
                SteamNetworkingMessages.SendMessageToUser(ref s_remote, (IntPtr)p, (uint)length, flags, channel);
            }
        }

        private static void SendControl(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            SendToRemote(bytes, 0, bytes.Length, true, ControlChannel);
        }

        private static void PumpSteamMessages()
        {
            // служебный канал
            int n = SteamNetworkingMessages.ReceiveMessagesOnChannel(ControlChannel, s_msgs, s_msgs.Length);
            for (int i = 0; i < n; i++)
            {
                var msg = SteamNetworkingMessage_t.FromIntPtr(s_msgs[i]);
                var sender = msg.m_identityPeer.GetSteamID();
                var bytes = new byte[msg.m_cbSize];
                Marshal.Copy(msg.m_pData, bytes, 0, msg.m_cbSize);
                SteamNetworkingMessage_t.Release(s_msgs[i]);
                OnControl(sender, Encoding.UTF8.GetString(bytes));
            }

            // данные игры
            n = SteamNetworkingMessages.ReceiveMessagesOnChannel(DataChannel, s_msgs, s_msgs.Length);
            for (int i = 0; i < n; i++)
            {
                var msg = SteamNetworkingMessage_t.FromIntPtr(s_msgs[i]);
                bool fromRemote = s_hasRemote && msg.m_identityPeer.GetSteamID() == s_remote.GetSteamID();
                if (fromRemote && s_plugin != null)
                {
                    s_lastRemoteMsg = DateTime.UtcNow;
                    var buf = new byte[msg.m_cbSize + 1];
                    buf[0] = IpcData;
                    Marshal.Copy(msg.m_pData, buf, 1, msg.m_cbSize);
                    // ненадёжное (снапшоты, ввод) остаётся ненадёжным: устаревшее не должно вставать в очередь
                    bool reliable = (msg.m_nFlags & Constants.k_nSteamNetworkingSend_Reliable) != 0;
                    bool fits = buf.Length <= s_plugin.GetMaxSinglePacketSize(DeliveryMethod.Unreliable);
                    s_plugin.Send(buf, 0, buf.Length, 0, reliable || !fits ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
                }
                SteamNetworkingMessage_t.Release(s_msgs[i]);
            }
            if (n > 0)
            {
                s_ipc.TriggerUpdate(); // отправить в игру сейчас, а не на следующем тике LiteNetLib
            }
        }

        private static void OnControl(CSteamID sender, string text)
        {
            switch (text)
            {
                case "connect":
                    // хост: друг вошёл в лобби и стучится
                    if (s_isHost && IsLobbyMember(sender))
                    {
                        SetRemote(sender);
                        SendControl("accept");
                        if (!s_remoteConnected)
                        {
                            s_remoteConnected = true;
                            Event($"peer-connected {SteamFriends.GetFriendPersonaName(sender)}");
                        }
                    }
                    break;
                case "accept":
                    if (!s_isHost && s_hasRemote && sender == s_remote.GetSteamID() && !s_remoteConnected)
                    {
                        s_remoteConnected = true;
                        Event($"peer-connected {SteamFriends.GetFriendPersonaName(sender)}");
                    }
                    break;
                case "bye":
                    if (s_hasRemote && sender == s_remote.GetSteamID())
                    {
                        RemoteLost("друг отключился");
                    }
                    break;
                case "ping":
                    if (s_hasRemote && sender == s_remote.GetSteamID())
                    {
                        s_lastRemoteMsg = DateTime.UtcNow;
                    }
                    break;
            }
        }

        private static void Tick()
        {
            var now = DateTime.UtcNow;
            // клиент: пока не приняли — стучимся раз в секунду
            if (!s_isHost && s_hasRemote && !s_remoteConnected && (now - s_lastPing).TotalSeconds > 1)
            {
                s_lastPing = now;
                SendControl("connect");
            }
            if (s_remoteConnected && (now - s_lastPing).TotalSeconds > 1)
            {
                s_lastPing = now;
                SendControl("ping");
                if (SteamNetworkingMessages.GetSessionConnectionInfo(ref s_remote, out var info, out var status) != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None)
                {
                    Event($"ping {status.m_nPing}");
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    bool relayed = (info.m_nFlags & Constants.k_nSteamNetworkConnectionInfoFlags_Relayed) != 0;
                    long queueUs = (long)status.m_usecQueueTime;
                    // ping qLocal qRemote outB/s inB/s rateB/s pendingUnrel pendingRel unackedRel queueUs relayed
                    string stats = string.Format(inv, "{0} {1:F3} {2:F3} {3:F0} {4:F0} {5} {6} {7} {8} {9} {10}",
                        status.m_nPing, status.m_flConnectionQualityLocal, status.m_flConnectionQualityRemote,
                        status.m_flOutBytesPerSec, status.m_flInBytesPerSec, status.m_nSendRateBytesPerSecond,
                        status.m_cbPendingUnreliable, status.m_cbPendingReliable, status.m_cbSentUnackedReliable, queueUs, relayed ? 1 : 0);
                    Event("stats " + stats);
                }
            }
        }
    }
}
