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
    /// Хост: игра идёт как в локальном коопе (персонаж клиента — второй игрок, его ввод приходит по сети),
    /// а состояние мира транслируется клиенту.
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

        /// <summary>Персонаж места: A — человек на хосте, B — человек-клиент.</summary>
        internal static GamePlayer SeatChar(Seat seat) => seat == CoopPlayers.MainSeat ? CoopPlayers.P1 : CoopPlayers.P2;

        // ================= жизненный цикл сессии =================

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
                // при обрыве персонаж напарника умер (лежит трупом с вещами) — вернётся свежим, без вещей
                m_remoteKilledByDisconnect = false;
                CoopLife.ClearDeath();
                Plugin.Log.LogInfo("[NetHost] partner is back — a fresh character will spawn");
            }

            m_clientSceneReady = false;
            m_sceneSent = false; // Tick отправит SceneLoad
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
                return; // был мёртв — остаётся мёртвым, воскрешение продолжится после возвращения
            }
            var hostChar = SeatChar(Seat.A);
            if (hostChar != null && !hostChar.Stats.Dead)
            {
                // напарник «умирает»: труп с вещами можно облутать; при возвращении появится заново голым
                CoopTransitions.ForceUnhide(remote);
                remote.Actions.Die();
                m_remoteKilledByDisconnect = true;
                Plugin.Log.LogInfo("[NetHost] partner disconnected — their character dropped dead (lootable)");
            }
            else
            {
                // хост сам мёртв: смерть напарника закончила бы забег — держим его замороженным
                FreezeRemote();
            }
        }

        internal void OnPeerGone()
        {
            // окно переподключения истекло: персонаж клиента остаётся замороженным, место свободно для нового входа
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

        // ================= сообщения =================

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

        // ================= кадр =================

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
                s_origin.Clear(); // новая сцена — старые объекты уничтожены
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
            // расписание копится от прошлого срока, а не от «сейчас»: иначе при 60 кадрах/с половина кадров
            // пропускалась и снапшоты шли неровно, 30–45 раз в секунду
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

        /// <summary>Полная пересинхронизация: сцена (если клиент не готов) или комната целиком (места → комната → сущности).</summary>
        public void ForceResync()
        {
            if (Time.unscaledTime - m_lastResync < 2f)
            {
                return;
            }
            m_lastResync = Time.unscaledTime;
            if (!m_clientSceneReady)
            {
                m_sceneSent = false; // Tick заново отправит SceneLoad
                return;
            }
            m_seatState[0] = m_seatState[1] = null;
            m_uiState = "";
            SendRoomFull();
            Plugin.Log.LogInfo("[NetHost] full resync sent");
        }

        /// <summary>Контрольная сумма комнаты: клиент сверяет и при расхождении просит пересинхронизацию.</summary>
        private void SendRoomCheck()
        {
            if (m_room == null)
            {
                return;
            }
            Scan(); // сначала отправить свежие регистрации/удаления, иначе сумма разойдётся на ровном месте
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

        // ================= сцена и комната =================

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
            // деревня хоста: клиент видит её состояние (дома, жителей), а генерация башни не расходится из-за прогресса
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

            // сначала состояния мест (клиент узнаёт о сменившихся персонажах и помечает свои трупы), потом сущности
            SendSeatStates(force: true);
            Scan();
        }

        // ================= сущности =================

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
            // мир: ловушки, рычаги, сундуки, двери-ловушки, полы, движущиеся части — всё с аниматором/физикой/логикой ловушки
            foreach (var go in WorldObjects(m_room))
            {
                Register(go, EntityKind.Other, roomT, added);
            }
            // летучие мыши, пчёлы: не персонажи (ловушки), но летают и анимируются
            foreach (var fb in m_room.GetComponentsInChildren<FlyingBehaviour>(true))
            {
                Register(fb.gameObject, EntityKind.Other, roomT, added);
            }
            // спутники, пришедшие из других комнат (идут за игроком), но лежащие вне иерархии этой комнаты
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
            // предметы в руках игроков (могли попасть в руки до входа в комнату)
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
            // трупы игроков в комнате: клиент сопоставит их со своими трупами марионеток по метке экземпляра
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

            // предметы, лежащие в комнате вне её иерархии (выпавшие из трупа, брошенные)
            var bounds = m_room.CompositeCollider != null ? m_room.CompositeCollider.bounds : new Bounds(m_room.transform.position, Vector3.one * 30f);
            bounds.Expand(new Vector3(2f, 2f, 1000f));
            foreach (var it in Object.FindObjectsByType<PickupItem>(FindObjectsSortMode.None))
            {
                if (m_byGo.ContainsKey(it.gameObject) || it.transform.IsChildOf(roomT) || it.GetComponent<Character>() != null
                    || it.GetComponentInParent<Character>() != null || !bounds.Contains(it.transform.position)
                    || !ItemCatalog.Has(GameManager.Instance.ItemLibrary, it.ItemName))
                {
                    continue; // заклинания и снаряды клиенту не создать — их нет в библиотеке предметов
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

        /// <summary>Объекты комнаты, у которых есть своя жизнь (аниматор, физика, ловушка, рычаг, сундук, повреждаемое) — кроме персонажей и предметов.</summary>
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
                // тени персонажей («2dShadow») вынесены в корень комнаты, но ведутся за своим персонажем сами
                if (go.GetComponentInParent<TwoDShadow>(true) != null || go.GetComponent<DestoryDust>() != null
                    || go.name.StartsWith("fx_", System.StringComparison.OrdinalIgnoreCase))
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
            if (m_byGo.ContainsKey(go))
            {
                return;
            }
            // лишний монстр (сложность): у клиента его нет — клиент копирует свою марионетку-источник
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
            // персонаж запоминается по комнате, где его увидели впервые: спутник, перешедший за игроком в другую комнату,
            // у клиента остался на старом месте — клиент найдёт его там и перенесёт
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
            e.SpawnedByNet = path == null && origin == null; // путь пустой — клиент создаёт по имени предмета
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

        // ================= снапшоты =================

        private bool m_stampLogged;

        private NetDataWriter BeginSnapshot(out int countPos, bool physicsStamp = false)
        {
            var w = m_s.Begin(Msg.Snapshot);
            // время хоста для буфера интерполяции у клиента (реальное, не игровое). Персонажи двигаются шагами физики
            // (50 в секунду), а кадров 60: метка кадра давала пары снапшотов с одной позицией и «замирания» у клиента.
            // Поэтому снапшот игроков метим временем последнего шага физики.
            w.Put(physicsStamp ? Time.fixedUnscaledTime : Time.unscaledTime);
            w.Put(LayerManager.CurrentPlayerLayer);
            w.Put(m_input.LastSeq); // замер задержки у клиента: последний применённый пакет ввода
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
                    continue; // в руке: у клиента висит на той же кости
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
                    Flush(); // LiteNetLib: ненадёжный пакет не больше 1023 байт
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

        /// <summary>Персонажи игроков: 60 Гц, со скоростью (клиент экстраполирует между снапшотами).</summary>
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
                // «активен» для места = графика видна (ожидание у двери прячет персонажа)
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

        /// <summary>Аниматор уходит, только если его состояние поменялось (или раз в 0.5 с для подстраховки).</summary>
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
                flags |= 64; // есть бит звука
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

        // ================= игроки, интерфейс, события =================

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
                string key = $"{tag}|{dead}|{armor}|{helmet}|{sack}|{gold}|{teethKey}|{form}";
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
                // зубы — это ХП Ванста (дырки в улыбке)
                int nTeeth = teeth != null ? Mathf.Min(teeth.Count, 16) : 0;
                w.Put((byte)nTeeth);
                for (int t = 0; t < nTeeth; t++)
                {
                    w.Put((byte)teeth[t].Color);
                    w.Put((byte)teeth[t].Position);
                    w.Put((byte)Mathf.Clamp(teeth[t].MaxHits, 0, 255));
                    w.Put((byte)Mathf.Clamp(teeth[t].CurrentHits, 0, 255));
                }
                w.Put((byte)form); // форма тела (Wanst/Monster/Evelius…): клиент создаёт марионетку из того же префаба
                m_s.Send(w, Delivery.Reliable);
            }
        }

        private readonly uint[] m_lookSig = new uint[2];
        private readonly NetDataWriter m_lookScratch = new NetDataWriter(true, 1024);
        private readonly float[] m_lookTime = { -10f, -10f };

        /// <summary>Видимость частей персонажей мест (вид со спины у двери, кошель, зубы) — при изменении и раз в 2 с.</summary>
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

        /// <summary>Предметы в руках/на спине: к какому месту и кости прикреплены (у клиента предмет крепится к той же кости марионетки).</summary>
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

        /// <summary>Префикс Die: какой ID у умирающего (место или сущность).</summary>
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

        /// <summary>ID зарегистрированного объекта по трансформу (сам или ближайший родитель); 0 — не зарегистрирован.</summary>
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

        /// <summary>Окно выбора у персонажа клиента: открыть (state, кнопки) / закрыть — клиент показывает такое же у себя.</summary>
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

        /// <summary>Ловушка «свет погас»: темнота включилась/выключилась — клиент повторяет на своей копии ловушки.</summary>
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
        /// <summary>Клиент просил начать заново (меню хоста показывает вопрос, пока не ответили).</summary>
        public float RestartAskedAt { get; set; } = -100f;

        /// <summary>Хост согласился: обычный перезапуск забега, клиент получит SceneLoad.</summary>
        public void RestartRun()
        {
            RestartAskedAt = -100f;
            // LoadNewGame меню паузы работает только при открытом меню; перезапуск забега — как у игровой команды restart
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
