using System.Collections.Generic;
using System.Linq;
using LiteNetLib.Utils;
using LuckyTower;
using LuckyTower.CharacterSystem;
using LuckyTower.LevelLayers;
using LuckyTower.RoomGeneration;
using LuckyTower.Village;
using LuckyTowerCoop.Coop;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Net
{
    /// <summary>
    /// Host: the game runs as in local co-op (the client's character is the second player, its input arrives over the network),
    /// and the world state is broadcast to the client.
    /// </summary>
    public sealed class NetHost
    {
        private readonly NetSession m_s;
        private readonly NetInputSource m_input = new NetInputSource();
        private readonly Dictionary<int, NetEntity> m_byId = new Dictionary<int, NetEntity>();
        private readonly Dictionary<GameObject, NetEntity> m_byGo = new Dictionary<GameObject, NetEntity>();

        private GameManager m_gm;
        private int m_runId;
        private bool m_sceneSent;
        private bool m_sceneCounted;
        private bool m_remoteKilledByDisconnect;
        private bool m_clientSceneReady;
        private Room m_room;
        private int m_nextRuntimeId = NetPaths.RuntimeIdStart;
        private float m_nextSnap;
        private float m_nextScan;
        private float m_nextState;
        private readonly string[] m_seatState = new string[2];
        private string m_uiState = "";
        private GamePlayer m_frozenRemote;

        public bool RemoteKilledByDisconnect => m_remoteKilledByDisconnect;

        internal static NetHost Current => NetSession.IsHost && NetSession.PeerReady ? NetSession.Instance.Host : null;

        internal NetHost(NetSession s)
        {
            m_s = s;
        }

        /// <summary>Seat character: A - the human on the host, B - the human client.</summary>
        internal static GamePlayer SeatChar(Seat seat) => seat == CoopPlayers.MainSeat ? CoopPlayers.P1 : CoopPlayers.P2;

        // ================= session lifecycle =================

        internal void OnClientReady(NetDataReader hello, bool rejoin)
        {
            string clientScene = hello.GetString();
            int clientRun = hello.GetInt();
            string clientRoom = hello.GetString();
            Plugin.Log.LogInfo($"[NetHost] client state: scene={clientScene} run={clientRun} room={clientRoom} (host run={m_runId})");

            m_input.Reset();
            var sys = CoopInput.P2Input;
            if (sys != null)
            {
                InputOverrides.Add(sys.id, m_input);
            }
            CoopInput.MarkDirty();
            UnfreezeRemote();
            if (m_remoteKilledByDisconnect)
            {
                // on a drop the partner character died (lies as a corpse with items) - comes back fresh, without items
                m_remoteKilledByDisconnect = false;
                CoopLife.ClearDeath();
                Plugin.Log.LogInfo("[NetHost] partner is back - a fresh character will spawn");
            }

            m_clientSceneReady = false;
            m_sceneSent = false; // Tick will send SceneLoad
            m_seatState[0] = m_seatState[1] = null;
            m_uiState = "";
        }

        internal void OnPeerLost()
        {
            m_clientSceneReady = false;
            m_input.Reset();
            var remote = SeatChar(Seat.B);
            if (remote == null || remote.Stats.Dead)
            {
                return; // was dead - stays dead, resurrection continues after the return
            }
            var hostChar = SeatChar(Seat.A);
            if (hostChar != null && !hostChar.Stats.Dead)
            {
                // the partner "dies": the corpse with items can be looted; on return appears again naked
                CoopTransitions.ForceUnhide(remote);
                remote.Actions.Die();
                m_remoteKilledByDisconnect = true;
                Plugin.Log.LogInfo("[NetHost] partner disconnected - their character dropped dead (lootable)");
            }
            else
            {
                // the host itself is dead: the partner's death would end the run - keep it frozen
                FreezeRemote();
            }
        }

        internal void OnPeerGone()
        {
            // reconnect window expired: the client's character stays frozen, the seat is free for a new join
        }

        internal void OnSessionEnded()
        {
            InputOverrides.Remove(m_input);
            CoopInput.MarkDirty();
            if (CoopPlayers.P2 != null)
            {
                CoopPlayers.Despawn();
            }
            m_frozenRemote = null;
        }

        private void FreezeRemote()
        {
            var remote = SeatChar(Seat.B);
            if (remote == null || remote.Stats.Dead)
            {
                return;
            }
            remote.Utilities.Freeze();
            remote.Stats.InputAllowed = false;
            m_frozenRemote = remote;
        }

        private void UnfreezeRemote()
        {
            if (m_frozenRemote != null && !m_frozenRemote.Stats.Dead)
            {
                m_frozenRemote.Utilities.UnFreeze();
                m_frozenRemote.Stats.InputAllowed = true;
            }
            m_frozenRemote = null;
        }

        // ================= messages =================

        internal void OnMessage(Msg type, NetDataReader r)
        {
            switch (type)
            {
                case Msg.Input:
                    m_input.OnPacket(r);
                    break;
                case Msg.RestartRequest:
                    r.GetString();
                    RestartAskedAt = Time.unscaledTime;
                    Plugin.Log.LogInfo("[NetHost] client asks to restart the run");
                    break;
                case Msg.GiveCoins:
                {
                    int ask = r.GetInt();
                    var from = SeatChar(Seat.B);
                    var to = SeatChar(Seat.A);
                    int moved = CoopGold.Transfer(from, to, Mathf.Clamp(ask, 0, 1000));
                    Plugin.Log.LogInfo($"[NetHost] client gives coins: asked {ask}, moved {moved}");
                    break;
                }
                case Msg.ResyncRequest:
                    string reason = r.GetString();
                    Plugin.Log.LogInfo($"[NetHost] client asks resync: {reason}");
                    ForceResync();
                    break;
                case Msg.SceneReady:
                    int run = r.GetInt();
                    if (run == m_runId)
                    {
                        m_clientSceneReady = true;
                        Plugin.Log.LogInfo($"[NetHost] client scene ready (run {run})");
                        SendRoomFull();
                    }
                    break;
            }
        }

        // ================= frame =================

        internal void Tick()
        {
            var gm = GameManager.InstanceNoWarnings;
            if (gm != m_gm)
            {
                m_gm = gm;
                m_room = null;
                m_sceneSent = false;
                m_sceneCounted = false;
                ClearEntities();
            }
            if (!CoopPlayers.IsInitialized(gm))
            {
                return;
            }
            if (!m_sceneCounted)
            {
                m_sceneCounted = true;
                m_runId++;
                s_origin.Clear(); // new scene - old objects were destroyed
            }
            if (!m_sceneSent)
            {
                SendSceneLoad();
            }
            if (!m_clientSceneReady)
            {
                return;
            }
            if (gm.CurrentRoom != m_room)
            {
                SendRoomFull();
            }

            float now = Time.unscaledTime;
            if (now >= m_nextScan)
            {
                m_nextScan = now + 0.5f;
                Scan();
            }
            // the schedule accumulates from the previous deadline, not from "now": otherwise at 60 frames/s half the frames
            // were skipped and snapshots came unevenly, 30-45 times per second
            if (now >= m_nextSeatSnap - 0.002f)
            {
                m_nextSeatSnap = NetConst.NextTick(m_nextSeatSnap, now, NetConst.SeatSnapshotInterval);
                SendSeatSnapshot();
            }
            if (now >= m_nextSnap - 0.002f)
            {
                m_nextSnap = NetConst.NextTick(m_nextSnap, now, NetConst.SnapshotInterval);
                SendAttachments();
                SendSnapshot();
                SendLooks(now);
            }
            if (now >= m_nextState)
            {
                m_nextState = now + 0.25f;
                SendSeatStates();
                SendUi();
            }
            if (now >= m_nextCheck)
            {
                m_nextCheck = now + 5f;
                SendRoomCheck();
            }
        }

        private float m_nextCheck;
        private float m_nextSeatSnap;
        internal int m_registerBatches;
        private float m_lastResync = -10f;

        /// <summary>Full resync: the scene (if the client isn't ready) or the whole room (seats -> room -> entities).</summary>
        public void ForceResync()
        {
            if (Time.unscaledTime - m_lastResync < 2f)
            {
                return;
            }
            m_lastResync = Time.unscaledTime;
            if (!m_clientSceneReady)
            {
                m_sceneSent = false; // Tick will send SceneLoad again
                return;
            }
            m_seatState[0] = m_seatState[1] = null;
            m_uiState = "";
            SendRoomFull();
            Plugin.Log.LogInfo("[NetHost] full resync sent");
        }

        /// <summary>Room checksum: the client compares it and, on mismatch, requests a full sync.</summary>
        private void SendRoomCheck()
        {
            if (m_room == null)
            {
                return;
            }
            Scan(); // first send fresh registrations/removals, otherwise the sum would diverge for no reason
            uint hash = 0;
            int count = 0;
            foreach (var e in m_byId.Values)
            {
                if (e.Alive)
                {
                    hash ^= (uint)e.Id * 2654435761u;
                    count++;
                }
            }
            var w = m_s.Begin(Msg.RoomCheck);
            w.Put(m_room.name);
            w.Put(count);
            w.Put(hash);
            m_s.Send(w, Delivery.Reliable);
        }

        // ================= scene and room =================

        private static VillageManager.LoadScene LoadSceneOf(GameData.SceneName s)
        {
            switch (s)
            {
                case GameData.SceneName.Master:
                    return VillageManager.LoadScene.MasterScene;
                case GameData.SceneName.Village:
                    return VillageManager.LoadScene.VillageRegular;
                case GameData.SceneName.FinalTower:
                    return VillageManager.LoadScene.FinalTower;
                case GameData.SceneName.Tutorial:
                    return VillageManager.LoadScene.Tutorial;
                default:
                    return VillageManager.LoadScene.None;
            }
        }

        private void SendSceneLoad()
        {
            var gm = GameManager.Instance;
            var w = m_s.Begin(Msg.SceneLoad);
            w.Put(m_runId);
            w.Put(SceneManager.GetActiveScene().name);
            w.Put((int)LoadSceneOf(gm.SceneName));
            w.Put(SegmentManager.Instance != null ? SegmentManager.Instance.UsedSeed : 0);
            w.Put(DataManager.Instance.GameData != null ? (int)DataManager.Instance.GameData.Difficulty : 0);
            // host village: the client sees its state (houses, residents), and tower generation doesn't diverge because of progress
            byte[] village = new byte[0];
            try
            {
                if (DataManager.Instance.VillageData != null)
                {
                    village = MessagePack.MessagePackSerializer.Serialize(DataManager.Instance.VillageData);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[NetHost] village serialize: {e.Message}");
            }
            w.PutBytesWithLength(village);
            m_s.Send(w, Delivery.Reliable);
            m_sceneSent = true;
            m_clientSceneReady = false;
            Plugin.Log.LogInfo($"[NetHost] scene load sent: {SceneManager.GetActiveScene().name} run={m_runId} seed={(SegmentManager.Instance != null ? SegmentManager.Instance.UsedSeed : 0)}");
        }

        private void SendRoomFull()
        {
            var gm = GameManager.Instance;
            var room = gm.CurrentRoom;
            if (room == null)
            {
                return;
            }
            m_room = room;
            ClearEntities();

            var w = m_s.Begin(Msg.RoomEnter);
            w.Put(room.name);
            w.Put(room.SegmentID);
            w.Put(room.FloorID);
            w.Put(LayerManager.CurrentPlayerLayer);
            foreach (var seat in new[] { Seat.A, Seat.B })
            {
                var c = SeatChar(seat);
                w.Put(c != null);
                if (c != null)
                {
                    w.Put(c.transform.position.x);
                    w.Put(c.transform.position.y);
                }
            }
            foreach (var kv in s_origin)
            {
                if (kv.Key != null && !kv.Value.StartsWith(room.name + "|") && kv.Key.transform.IsChildOf(room.transform))
                {
                    NetPaths.MarkRuntime(kv.Key);
                }
            }
            var inactive = NetPaths.InactiveHashes(room.transform, room.name);
            w.Put((ushort)Mathf.Min(inactive.Count, ushort.MaxValue));
            for (int i = 0; i < inactive.Count && i < ushort.MaxValue; i++)
            {
                w.Put(inactive[i]);
            }
            m_s.Send(w, Delivery.Reliable);
            Plugin.Log.LogInfo($"[NetHost] room -> {room.name} (seg {room.SegmentID} floor {room.FloorID}, {inactive.Count} inactive objects)");

            // seat states first (the client learns about changed characters and marks its corpses), then entities
            SendSeatStates(force: true);
            Scan();
        }

        // ================= entities =================

        private void ClearEntities()
        {
            m_byId.Clear();
            m_byGo.Clear();
            s_corpseTag.Clear();
        }

        private void Scan()
        {
            if (m_room == null)
            {
                return;
            }
            var added = new List<NetEntity>();
            var roomT = m_room.transform;
            foreach (var ch in m_room.GetComponentsInChildren<Character>(true))
            {
                if (ch is GamePlayer)
                {
                    continue;
                }
                Register(ch.gameObject, EntityKind.Character, roomT, added);
            }
            // world: traps, levers, chests, trap doors, floors, moving parts - everything with an animator/physics/trap logic
            foreach (var go in WorldObjects(m_room))
            {
                Register(go, EntityKind.Other, roomT, added);
            }
            // bats, bees: not characters (traps), but they fly and animate
            foreach (var fb in m_room.GetComponentsInChildren<FlyingBehaviour>(true))
            {
                Register(fb.gameObject, EntityKind.Other, roomT, added);
            }
            // companions that came from other rooms (following the player) but lie outside this room's hierarchy
            var nearBounds = m_room.CompositeCollider != null ? m_room.CompositeCollider.bounds : new Bounds(roomT.position, Vector3.one * 30f);
            nearBounds.Expand(new Vector3(2f, 4f, 1000f));
            foreach (var kv in s_origin)
            {
                var go = kv.Key;
                if (go != null && go.activeInHierarchy && !m_byGo.ContainsKey(go) && !go.transform.IsChildOf(roomT) && nearBounds.Contains(go.transform.position))
                {
                    Register(go, EntityKind.Character, null, added);
                }
            }
            foreach (var it in m_room.GetComponentsInChildren<PickupItem>(true))
            {
                if (it.GetComponent<Character>() != null)
                {
                    continue;
                }
                Register(it.gameObject, EntityKind.Item, roomT, added);
            }
            // items in players' hands (could have been picked up before entering the room)
            foreach (var seat in new[] { Seat.A, Seat.B })
            {
                var c = SeatChar(seat);
                if (c == null)
                {
                    continue;
                }
                foreach (var held in c.GetComponentsInChildren<PickupItem>(false))
                {
                    if (held.gameObject != c.gameObject && !m_byGo.ContainsKey(held.gameObject))
                    {
                        Register(held.gameObject, EntityKind.Item, null, added);
                    }
                }
            }
            // player corpses in the room: the client matches them with its puppet corpses by instance tag
            var roomBounds = m_room.CompositeCollider != null ? m_room.CompositeCollider.bounds : new Bounds(m_room.transform.position, Vector3.one * 30f);
            roomBounds.Expand(new Vector3(2f, 20f, 1000f));
            foreach (var pl in Object.FindObjectsByType<GamePlayer>(FindObjectsSortMode.None))
            {
                if (pl.Stats.Dead && !m_byGo.ContainsKey(pl.gameObject) && roomBounds.Contains(pl.transform.position))
                {
                    Register(pl.gameObject, EntityKind.Character, null, added);
                    s_corpseTag[pl.gameObject] = pl.GetInstanceID();
                }
            }

            // items lying in the room outside its hierarchy (dropped from a corpse, thrown)
            var bounds = m_room.CompositeCollider != null ? m_room.CompositeCollider.bounds : new Bounds(m_room.transform.position, Vector3.one * 30f);
            bounds.Expand(new Vector3(2f, 2f, 1000f));
            foreach (var it in Object.FindObjectsByType<PickupItem>(FindObjectsSortMode.None))
            {
                if (m_byGo.ContainsKey(it.gameObject) || it.transform.IsChildOf(roomT) || it.GetComponent<Character>() != null
                    || it.GetComponentInParent<Character>() != null || !bounds.Contains(it.transform.position)
                    || !ItemCatalog.Has(GameManager.Instance.ItemLibrary, it.ItemName))
                {
                    continue; // spells and projectiles can't be created on the client - they aren't in the item library
                }
                Register(it.gameObject, EntityKind.Item, null, added);
            }
            if (added.Count > 0)
            {
                SendRegister(added);
            }

            List<int> gone = null;
            foreach (var e in m_byId.Values)
            {
                if (!e.Alive)
                {
                    (gone ??= new List<int>()).Add(e.Id);
                }
            }
            if (gone != null)
            {
                foreach (int id in gone)
                {
                    m_byId.Remove(id);
                    var w = m_s.Begin(Msg.EntityDespawn);
                    w.Put(id);
                    m_s.Send(w, Delivery.Reliable);
                }
                foreach (var key in m_byGo.Keys.Where(k => k == null).ToList())
                {
                    m_byGo.Remove(key);
                }
            }
        }

        private static readonly List<GameObject> s_world = new List<GameObject>(256);
        private static readonly HashSet<GameObject> s_worldSeen = new HashSet<GameObject>();

        private static bool IsLeftover(GameObject go)
        {
            if (go.name.StartsWith("leftover", System.StringComparison.OrdinalIgnoreCase) || go.name.EndsWith("LeftOvers", System.StringComparison.OrdinalIgnoreCase)
                || go.name.IndexOf("ArmorDestroyed", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            for (var t = go.transform.parent; t != null; t = t.parent)
            {
                if (t.name == "PlantRemains" || t.name.EndsWith("LeftOvers", System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Room objects with a life of their own (animator, physics, trap, lever, chest, damageable) - except characters and items.</summary>
        private static List<GameObject> WorldObjects(Room room)
        {
            s_world.Clear();
            s_worldSeen.Clear();
            void Add(Component c)
            {
                if (c == null)
                {
                    return;
                }
                var go = c.gameObject;
                if (!s_worldSeen.Add(go) || go == room.gameObject)
                {
                    return;
                }
                if (go.GetComponentInParent<Character>(true) != null || go.GetComponentInParent<PickupItem>(true) != null || go.GetComponentInParent<FlyingBehaviour>(true) != null)
                {
                    return;
                }
                // character shadows ("2dShadow") are moved to the room root but follow their own character themselves
                if (go.GetComponentInParent<TwoDShadow>(true) != null || go.GetComponent<DestoryDust>() != null
                    || go.name.StartsWith("fx_", System.StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                // armor debris / plant remains (cosmetic, fall off the map and aren't found on the client)
                if (IsLeftover(go))
                {
                    return;
                }
                s_world.Add(go);
            }
            foreach (var a in room.GetComponentsInChildren<Animator>(true)) { Add(a); }
            foreach (var r in room.GetComponentsInChildren<Rigidbody2D>(true)) { Add(r); }
            foreach (var t in room.GetComponentsInChildren<Trap>(true)) { Add(t); }
            foreach (var l in room.GetComponentsInChildren<LuckyTower.Traps.Lever>(true)) { Add(l); }
            foreach (var d in room.GetComponentsInChildren<Damagable>(true)) { Add(d); }
            foreach (var ch in room.GetComponentsInChildren<TreasureChest>(true)) { Add(ch); }
            return s_world;
        }

        private void Register(GameObject go, EntityKind kind, Transform roomT, List<NetEntity> added)
        {
            if (m_byGo.ContainsKey(go) || (kind != EntityKind.Character && IsLeftover(go)))
            {
                return; // armor/chest/barrel debris - cosmetic: falls off the map and isn't found on the client
            }
            // extra monster (difficulty): the client doesn't have it - the client copies its own source puppet
            if (CoopDifficulty.TryGetCloneSource(go, out var source))
            {
                if (!m_byGo.ContainsKey(source))
                {
                    Register(source, kind, roomT, added);
                }
                if (m_byGo.TryGetValue(source, out var srcEntity))
                {
                    var clone = NetEntity.For(m_nextRuntimeId++, go, kind);
                    m_byId[clone.Id] = clone;
                    m_byGo[go] = clone;
                    added.Add(clone);
                    s_cloneOf[clone] = srcEntity.Id;
                    return;
                }
            }
            string path = roomT != null ? NetPaths.PathOf(go.transform, roomT) : null;
            // the character is remembered by the room where it was first seen: a companion that followed the player into another room
            // stayed at its old place on the client - the client will find it there and move it
            string origin = null;
            if (kind == EntityKind.Character)
            {
                if (s_origin.TryGetValue(go, out var known))
                {
                    if (path == null || known != m_room.name + "|" + path)
                    {
                        origin = known;
                        path = null;
                    }
                }
                else if (path != null)
                {
                    s_origin[go] = m_room.name + "|" + path;
                }
            }
            int id;
            if (path != null)
            {
                id = NetPaths.IdOf(m_room.name, path);
                if (m_byId.ContainsKey(id))
                {
                    id = m_nextRuntimeId++;
                    path = null;
                }
            }
            else
            {
                id = m_nextRuntimeId++;
            }
            var e = NetEntity.For(id, go, kind);
            e.SpawnedByNet = path == null && origin == null; // empty path - the client creates it by item name
            if (origin != null)
            {
                s_originOf[e] = origin;
            }
            m_byId[id] = e;
            m_byGo[go] = e;
            added.Add(e);
            e.SentTime = -10f;
            if (path != null)
            {
                s_pathOf[e] = path;
            }
        }

        private static readonly Dictionary<NetEntity, string> s_pathOf = new Dictionary<NetEntity, string>();
        private static readonly Dictionary<GameObject, int> s_corpseTag = new Dictionary<GameObject, int>();
        private static readonly Dictionary<NetEntity, int> s_cloneOf = new Dictionary<NetEntity, int>();
        private static readonly Dictionary<GameObject, string> s_origin = new Dictionary<GameObject, string>();
        private static readonly Dictionary<NetEntity, string> s_originOf = new Dictionary<NetEntity, string>();

        private void SendRegister(List<NetEntity> list)
        {
            for (int start = 0; start < list.Count; start += 20)
            {
                int n = Mathf.Min(20, list.Count - start);
                var w = m_s.Begin(Msg.EntityRegister);
                w.Put((ushort)n);
                for (int i = start; i < start + n; i++)
                {
                    var e = list[i];
                    s_pathOf.TryGetValue(e, out var path);
                    w.Put(e.Id);
                    w.Put((byte)e.Kind);
                    w.Put(path ?? "");
                    w.Put(s_cloneOf.TryGetValue(e, out var cloneSrc) ? "#clone:" + cloneSrc
                        : s_corpseTag.TryGetValue(e.Go, out var corpseTag) ? "#corpse:" + corpseTag
                        : s_originOf.TryGetValue(e, out var origin) ? "#origin:" + origin
                        : e.Item != null ? e.Item.ItemName ?? "" : "");
                    w.Put(e.T.position.x);
                    w.Put(e.T.position.y);
                    w.Put(e.Go.activeSelf);
                }
                m_s.Send(w, Delivery.Reliable);
                m_registerBatches++;
                if (w.Length > 1400)
                {
                    Plugin.Log.LogInfo($"[NetHost] register batch #{m_registerBatches}: {n} entries, {w.Length} bytes");
                }
            }
            foreach (var e in list)
            {
                s_pathOf.Remove(e);
                s_cloneOf.Remove(e);
                s_originOf.Remove(e);
            }
        }

        // ================= snapshots =================

        private bool m_stampLogged;

        private NetDataWriter BeginSnapshot(out int countPos, bool physicsStamp = false)
        {
            var w = m_s.Begin(Msg.Snapshot);
            // host time for the client's interpolation buffer (real, not game time). Characters move in physics steps
            // (50 per second) but there are 60 frames: a frame timestamp gave snapshot pairs with the same position and "freezes" on the client.
            // So the player snapshot is stamped with the time of the last physics step.
            w.Put(physicsStamp ? Time.fixedUnscaledTime : Time.unscaledTime);
            w.Put(LayerManager.CurrentPlayerLayer);
            w.Put(m_input.LastSeq); // latency measurement on the client: the last applied input packet
            countPos = w.Length;
            w.Put((ushort)0);
            return w;
        }

        private static void PatchCount(NetDataWriter w, int pos, int count)
        {
            w.Data[pos] = (byte)count;
            w.Data[pos + 1] = (byte)(count >> 8);
        }

        private void SendSnapshot()
        {
            var w = BeginSnapshot(out int countPos);
            int count = 0;
            float now = Time.unscaledTime;

            void Flush()
            {
                PatchCount(w, countPos, count);
                m_s.Send(w, Delivery.Unreliable);
                w = BeginSnapshot(out countPos);
                count = 0;
            }

            foreach (var e in m_byId.Values)
            {
                if (!e.Alive)
                {
                    continue;
                }
                bool active = e.Go.activeInHierarchy;
                if (!string.IsNullOrEmpty(e.AttachKey) && active == e.SentActive)
                {
                    continue; // in hand: on the client it hangs on the same bone
                }
                var pos = e.T.position;
                float rot = e.T.eulerAngles.z;
                bool moved = (pos - e.SentPos).sqrMagnitude > 0.00004f || Mathf.Abs(Mathf.DeltaAngle(rot, e.SentRot)) > 0.5f;
                bool character = e.Kind == EntityKind.Character || e.Flying;
                bool animated = e.Anim != null && e.Kind != EntityKind.Item;
                bool refresh = now - e.SentTime > (character ? 0.25f : 1.5f);
                bool audioChanged = e.Audio != null && e.Audio.isPlaying != e.SentAudio;
                if (!moved && !refresh && active == e.SentActive && !audioChanged)
                {
                    continue;
                }
                e.SentPos = pos;
                e.SentRot = rot;
                e.SentTime = now;
                e.SentActive = active;
                m_entryScratch.Reset();
                WriteEntry(m_entryScratch, e.Id, e.T, active, animated && active ? e.Anim : null, e, null, e.Character);
                if (count > 0 && w.Length + m_entryScratch.Length > NetConst.MaxPacket)
                {
                    Flush(); // LiteNetLib: an unreliable packet is at most 1023 bytes
                }
                w.Put(m_entryScratch.Data, 0, m_entryScratch.Length);
                count++;
            }
            if (count > 0)
            {
                PatchCount(w, countPos, count);
                m_s.Send(w, Delivery.Unreliable);
            }
        }

        /// <summary>Player characters: 60 Hz, with velocity (the client extrapolates between snapshots).</summary>
        private void SendSeatSnapshot()
        {
            var rbA = SeatChar(Seat.A)?.Components.Rigidbody;
            bool physicsStamp = rbA != null && rbA.interpolation == RigidbodyInterpolation2D.None;
            if (!m_stampLogged && rbA != null)
            {
                m_stampLogged = true;
                Plugin.Log.LogInfo($"[NetHost] seat snapshots stamped with {(physicsStamp ? "physics step time (rigidbody not interpolated)" : "frame time (rigidbody interpolated)")}");
            }
            var w = BeginSnapshot(out int countPos, physicsStamp);
            int count = 0;
            foreach (var seat in new[] { Seat.A, Seat.B })
            {
                var c = SeatChar(seat);
                if (c == null)
                {
                    continue;
                }
                var anim = c.Components.Animator != null ? c.Components.Animator.AnimatorComponent : null;
                // "active" for a seat = graphics visible (waiting at a door hides the character)
                bool visible = !CoopVisibility.IsHidden(c);
                WriteEntry(w, seat == Seat.A ? NetPaths.SeatA : NetPaths.SeatB, c.transform, visible, anim, m_seatCache[(int)seat], c.Components.Rigidbody);
                count++;
            }
            if (count > 0)
            {
                PatchCount(w, countPos, count);
                m_s.Send(w, Delivery.Unreliable);
            }
        }

        private readonly NetDataWriter m_animScratch = new NetDataWriter(true, 512);
        private readonly NetDataWriter m_entryScratch = new NetDataWriter(true, 768);
        private readonly NetEntity[] m_seatCache = { new NetEntity(), new NetEntity() };

        /// <summary>The animator is sent only if its state changed (or every 0.5 s as a safeguard).</summary>
        private void WriteEntry(NetDataWriter w, int id, Transform t, bool active, Animator anim, NetEntity cache, Rigidbody2D rb = null, Character ch = null)
        {
            bool sendAnim = false;
            if (anim != null)
            {
                m_animScratch.Reset();
                uint sig = NetAnim.Write(m_animScratch, anim);
                float now = Time.unscaledTime;
                if (sig != cache.AnimSig || now - cache.AnimSentTime > (cache.Kind == EntityKind.Other ? 2f : 0.5f))
                {
                    sendAnim = true;
                    cache.AnimSig = sig;
                    cache.AnimSentTime = now;
                }
            }
            w.Put(id);
            w.Put(t.position.x);
            w.Put(t.position.y);
            w.Put(t.eulerAngles.z);
            byte flags = 0;
            if (t.localScale.x < 0f)
            {
                flags |= 1;
            }
            if (active)
            {
                flags |= 2;
            }
            if (sendAnim)
            {
                flags |= 4;
            }
            bool sendVel = rb != null && rb.simulated;
            if (sendVel)
            {
                flags |= 8;
            }
            byte hp = 255;
            bool sendHp = ch != null && ch.Stats != null && !ch.Stats.Dead && ch.Stats.MaximumHealth > 0;
            if (sendHp)
            {
                hp = (byte)Mathf.Clamp(Mathf.RoundToInt(255f * ch.Stats.Health / ch.Stats.MaximumHealth), 0, 255);
                flags |= 16;
            }
            if (cache.Audio != null)
            {
                cache.SentAudio = cache.Audio.isPlaying;
                flags |= 64; // sound bit present
                if (cache.SentAudio)
                {
                    flags |= 32;
                }
            }
            w.Put(flags);
            if (sendVel)
            {
                var v = rb.linearVelocity;
                w.Put(v.x);
                w.Put(v.y);
            }
            if (sendHp)
            {
                w.Put(hp);
            }
            if (sendAnim)
            {
                w.Put(m_animScratch.Data, 0, m_animScratch.Length);
            }
        }

        // ================= players, interface, events =================

        private void SendSeatStates(bool force = false)
        {
            foreach (var seat in new[] { Seat.A, Seat.B })
            {
                var c = SeatChar(seat);
                int i = (int)seat;
                var s = c != null ? c.Stats : null;
                string armor = s?.CurrentArmor != null ? s.CurrentArmor.name : "";
                string helmet = s?.CurrentHelmet != null ? s.CurrentHelmet.name : "";
                bool sack = s?.CurrentGoldSack != null;
                int gold = sack ? s.CurrentGoldSack.CurrentGold : 0;
                int tag = c != null ? c.GetInstanceID() : 0;
                bool dead = c == null || s.Dead;
                var teeth = s?.TeethInventory;
                string teethKey = teeth == null ? "" : string.Join(",", teeth.Select(t => $"{(int)t.Color}:{t.Position}:{t.CurrentHits}"));
                int form = c != null ? (int)c.CharacterName : 0;
                int total = s != null ? s.CurrentGoldCount() : 0;
                string key = $"{tag}|{dead}|{armor}|{helmet}|{sack}|{gold}|{teethKey}|{form}|{total}";
                if (!force && key == m_seatState[i])
                {
                    continue;
                }
                m_seatState[i] = key;
                var w = m_s.Begin(Msg.PlayerState);
                w.Put((byte)seat);
                w.Put(tag);
                w.Put(dead);
                w.Put(armor);
                w.Put(helmet);
                w.Put(sack);
                w.Put(gold);
                if (c != null)
                {
                    w.Put(c.transform.position.x);
                    w.Put(c.transform.position.y);
                }
                else
                {
                    w.Put(0f);
                    w.Put(0f);
                }
                // teeth are Wanst's HP (gaps in the smile)
                int nTeeth = teeth != null ? Mathf.Min(teeth.Count, 16) : 0;
                w.Put((byte)nTeeth);
                for (int t = 0; t < nTeeth; t++)
                {
                    w.Put((byte)teeth[t].Color);
                    w.Put((byte)teeth[t].Position);
                    w.Put((byte)Mathf.Clamp(teeth[t].MaxHits, 0, 255));
                    w.Put((byte)Mathf.Clamp(teeth[t].CurrentHits, 0, 255));
                }
                w.Put((byte)form); // body form (Wanst/Monster/Evelius...): the client creates the puppet from the same prefab
                w.Put(total);
                m_s.Send(w, Delivery.Reliable);
            }
        }

        private readonly uint[] m_lookSig = new uint[2];
        private readonly NetDataWriter m_lookScratch = new NetDataWriter(true, 1024);
        private readonly float[] m_lookTime = { -10f, -10f };

        /// <summary>Visibility of seat character parts (back view at a door, pouch, teeth) - on change and every 2 s.</summary>
        private void SendLooks(float now)
        {
            foreach (var seat in new[] { Seat.A, Seat.B })
            {
                var c = SeatChar(seat);
                int i = (int)seat;
                if (c == null)
                {
                    continue;
                }
                m_lookScratch.Reset();
                uint sig = NetLook.Write(m_lookScratch, c);
                if (sig == m_lookSig[i] && now - m_lookTime[i] < 2f)
                {
                    continue;
                }
                m_lookSig[i] = sig;
                m_lookTime[i] = now;
                var w = m_s.Begin(Msg.Look);
                w.Put((byte)seat);
                w.Put(m_lookScratch.Data, 0, m_lookScratch.Length);
                m_s.Send(w, Delivery.Reliable);
            }
        }

        /// <summary>Items in hands/on back: which seat and bone they're attached to (on the client the item attaches to the same puppet bone).</summary>
        private void SendAttachments()
        {
            var a = SeatChar(Seat.A);
            var b = SeatChar(Seat.B);
            foreach (var e in m_byId.Values)
            {
                if (!e.Alive || e.Kind != EntityKind.Item)
                {
                    continue;
                }
                int seatId = 0;
                Transform holder = null;
                for (var t = e.T.parent; t != null; t = t.parent)
                {
                    if (a != null && t == a.transform)
                    {
                        seatId = NetPaths.SeatA;
                        holder = t;
                        break;
                    }
                    if (b != null && t == b.transform)
                    {
                        seatId = NetPaths.SeatB;
                        holder = t;
                        break;
                    }
                }
                string path = seatId != 0 ? (e.T.parent == holder ? "" : NetPaths.PathOf(e.T.parent, holder) ?? "") : "";
                string key = seatId != 0 ? seatId + "|" + path : "";
                var lp = e.T.localPosition;
                bool poseChanged = seatId != 0 && (lp - e.AttachSentLocal).sqrMagnitude > 0.0004f;
                if (key == (e.AttachKey ?? "") && !poseChanged)
                {
                    continue;
                }
                e.AttachKey = key;
                e.AttachSentLocal = lp;
                var w = m_s.Begin(Msg.Attach);
                w.Put(e.Id);
                w.Put(seatId);
                w.Put(path);
                var lr = e.T.localRotation;
                var ls = e.T.localScale;
                w.Put(lp.x);
                w.Put(lp.y);
                w.Put(lp.z);
                w.Put(lr.x);
                w.Put(lr.y);
                w.Put(lr.z);
                w.Put(lr.w);
                w.Put(ls.x);
                w.Put(ls.y);
                w.Put(ls.z);
                m_s.Send(w, Delivery.Reliable);
            }
        }

        private static readonly List<(Vector3 pos, string text)> s_labels = new List<(Vector3, string)>();

        private void SendUi()
        {
            CoopTransitions.GetLabels(s_labels);
            int kills = CoopLife.SomeoneDead ? CoopLife.Kills : -1;
            string key = kills + ";" + string.Join(";", s_labels.Select(l => $"{l.pos.x:F1},{l.pos.y:F1},{l.text}"));
            if (key == m_uiState)
            {
                return;
            }
            m_uiState = key;
            var w = m_s.Begin(Msg.UiState);
            w.Put(kills);
            w.Put(CoopConfig.KillsToRevive.Value);
            w.Put(CoopConfig.ReviveOnNewFloor.Value);
            w.Put((byte)s_labels.Count);
            foreach (var l in s_labels)
            {
                w.Put(l.pos.x);
                w.Put(l.pos.y);
                w.Put(l.text);
            }
            m_s.Send(w, Delivery.Reliable);
        }

        /// <summary>Die prefix: the ID of the dying one (seat or entity).</summary>
        internal int IdOf(Character c)
        {
            if (c == null)
            {
                return 0;
            }
            if (c == SeatChar(Seat.A))
            {
                return NetPaths.SeatA;
            }
            if (c == SeatChar(Seat.B))
            {
                return NetPaths.SeatB;
            }
            return m_byGo.TryGetValue(c.gameObject, out var e) ? e.Id : 0;
        }

        internal void SendDeath(int id)
        {
            if (id == 0)
            {
                return;
            }
            var w = m_s.Begin(Msg.EntityDeath);
            w.Put(id);
            m_s.Send(w, Delivery.Reliable);
        }

        internal void SendGameOver()
        {
            m_s.Send(m_s.Begin(Msg.GameOver), Delivery.Reliable);
        }

        /// <summary>ID of a registered object by transform (itself or the nearest parent); 0 - not registered.</summary>
        internal int IdOfTransform(Transform t)
        {
            for (var cur = t; cur != null; cur = cur.parent)
            {
                var ch = cur.GetComponent<Character>();
                if (ch != null)
                {
                    int sid = IdOf(ch);
                    if (sid != 0)
                    {
                        return sid;
                    }
                }
                if (m_byGo.TryGetValue(cur.gameObject, out var e))
                {
                    return e.Id;
                }
            }
            return 0;
        }

        /// <summary>Selection window on the client's character: open (state, buttons) / close - the client shows the same on its side.</summary>
        internal void SendWindow(bool open, TalkState state, bool canBeClosed, bool follow, float delay, Transform target, Vector2 offset, string ov1, string ov2, string ov3)
        {
            if (!NetSession.PeerReady)
            {
                return;
            }
            var w = m_s.Begin(Msg.Window);
            w.Put(open);
            if (open)
            {
                w.Put((byte)state);
                w.Put(canBeClosed);
                w.Put(follow);
                w.Put(delay);
                int targetId = target != null ? IdOfTransform(target) : 0;
                w.Put(targetId);
                var tp = target != null ? target.position : Vector3.zero;
                w.Put(tp.x);
                w.Put(tp.y);
                w.Put(offset.x);
                w.Put(offset.y);
                w.Put(ov1 ?? "");
                w.Put(ov2 ?? "");
                w.Put(ov3 ?? "");
            }
            m_s.Send(w, Delivery.Reliable);
        }

        /// <summary>"Lights out" trap: darkness turned on/off - the client repeats it on its copy of the trap.</summary>
        internal void SendLights(LuckyTower.Traps.TrapLightsOut trap, bool off, bool instant)
        {
            if (trap == null || !NetSession.PeerReady)
            {
                return;
            }
            if (!m_byGo.TryGetValue(trap.gameObject, out var e))
            {
                Scan();
                if (!m_byGo.TryGetValue(trap.gameObject, out e))
                {
                    Plugin.Log.LogWarning($"[NetHost] lights-out trap {trap.name} is not a registered entity");
                    return;
                }
            }
            var w = m_s.Begin(Msg.LightsOut);
            w.Put(e.Id);
            w.Put(off);
            w.Put(instant);
            m_s.Send(w, Delivery.Reliable);
        }

        /// <summary>Tower explosion (powder keg): the client plays the same cutscene, death arrives the usual way.</summary>
        internal void SendTowerExplosion()
        {
            if (!NetSession.PeerReady)
            {
                return;
            }
            var w = m_s.Begin(Msg.TowerExplosion);
            m_s.Send(w, Delivery.Reliable);
            Plugin.Log.LogInfo("[NetHost] tower explosion sent");
        }

        internal void SendTalk(Character speaker, string key, bool dead, bool endless)
        {
            int id = IdOf(speaker);
            if (id == 0 || string.IsNullOrEmpty(key))
            {
                return;
            }
            var w = m_s.Begin(Msg.Talk);
            w.Put(id);
            w.Put(key);
            w.Put(dead);
            w.Put(endless);
            m_s.Send(w, Delivery.Reliable);
        }
        /// <summary>The client asked to start over (the host menu shows a question until answered).</summary>
        public float RestartAskedAt { get; set; } = -100f;

        /// <summary>The host agreed: a normal run restart, the client gets SceneLoad.</summary>
        public void RestartRun()
        {
            RestartAskedAt = -100f;
            // the pause menu's LoadNewGame works only while the menu is open; the run restart is like the game's restart command
            Time.timeScale = 1f;
            SceneLoader.LoadMasterScene();
        }

        internal NetEntity EntityById(int id) => m_byId.TryGetValue(id, out var e) ? e : null;

        internal string DebugList()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var e in m_byId.Values)
            {
                sb.AppendLine($"{e.Id,11} {e.Kind,-9} {(e.Alive ? e.Go.name : "<destroyed>"),-34} pos={(e.Alive ? e.T.position.ToString("F2") : "-")} active={(e.Alive && e.Go.activeInHierarchy)}{(e.HasTarget ? $" target={e.TargetPos:F2}" : "")}");
            }
            return sb.Length > 0 ? sb.ToString() : "no entities";
        }
    }
}
