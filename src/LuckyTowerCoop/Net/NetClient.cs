using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiteNetLib.Utils;
using LuckyTower;
using LuckyTower.CharacterSystem;
using LuckyTower.LevelLayers;
using LuckyTower.RoomGeneration;
using LuckyTower.Village;
using LuckyTowerCoop.Coop;
using UnityEngine;
using UnityEngine.SceneManagement;
using GamePlayer = LuckyTower.CharacterSystem.Player;
using Object = UnityEngine.Object;

namespace LuckyTowerCoop.Net
{
    /// <summary>
    /// Клиент-зеркало: загружает сцену хоста с тем же сидом, ничего не симулирует сам,
    /// все персонажи/враги/предметы повторяют снапшоты хоста. Свой ввод уходит хосту.
    /// Свой персонаж (место B) — GameManager.Player, персонаж хоста (место A) — второй игрок.
    /// </summary>
    public sealed class NetClient
    {
        private readonly NetSession m_s;
        private readonly NetInputSender m_sender = new NetInputSender();
        private readonly Dictionary<int, NetEntity> m_byId = new Dictionary<int, NetEntity>();
        private readonly NetEntity[] m_seatEntity = new NetEntity[2];

        // буфер интерполяции: оценка времени хоста и задержка показа (подстраивается под джиттер сети)
        private readonly NetClock m_clock = new NetClock();
        private float m_delaySeat = 0.05f;
        private float m_delayWorld = 0.08f;
        private readonly NetClock.RenderTrack m_trackSeat = new NetClock.RenderTrack();
        private readonly NetClock.RenderTrack m_trackWorld = new NetClock.RenderTrack();

        public float ShowDelayMs => (CoopConfig.NetInterp.Value ? m_delaySeat : 0f) * 1000f;

        public float JitterMs => m_clock.Jitter * 1000f;

        internal string QualityReport() => NetQuality.Report(CoopConfig.NetInterp.Value ? "buffer" : "legacy", ShowDelayMs, JitterMs);
        private readonly string[] m_seatTeeth = new string[2];
        private readonly List<NetEntity> m_pendingAttach = new List<NetEntity>();
        private static readonly Dictionary<string, GameObject> s_originGo = new Dictionary<string, GameObject>();
        private readonly int[] m_seatTag = new int[2];
        private readonly bool[] m_seatKnown = new bool[2];
        private readonly string[] m_seatVisual = new string[2];
        private readonly NetEntity m_dummy = new NetEntity();
        private readonly HashSet<int> m_unresolved = new HashSet<int>();
        private float m_unknownSince = -1f;
        private float m_lastResyncRequest = -10f;

        /// <summary>Попросить хоста прислать комнату целиком (не чаще раза в 3 с).</summary>
        /// <summary>Просьба к хосту начать заново (меню паузы клиента). Ответ приходит как обычная загрузка сцены.</summary>
        public void RequestRestart()
        {
            var w = m_s.Begin(Msg.RestartRequest);
            w.Put(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            m_s.Send(w, Delivery.Reliable);
            RestartRequestedAt = Time.unscaledTime;
            Plugin.Log.LogInfo("[NetClient] restart requested from host");
        }

        /// <summary>Просьба хосту: передать напарнику (хосту) N монет моего персонажа.</summary>
        public void RequestGiveCoins(int amount)
        {
            var w = m_s.Begin(Msg.GiveCoins);
            w.Put(amount);
            m_s.Send(w, Delivery.Reliable);
            Plugin.Log.LogInfo($"[NetClient] give coins requested: {amount}");
        }

        /// <summary>Когда клиент последний раз просил рестарт (для подсказки на экране).</summary>
        public float RestartRequestedAt { get; private set; } = -100f;

        public void RequestResync(string reason)
        {
            if (Time.unscaledTime - m_lastResyncRequest < 3f)
            {
                return;
            }
            m_lastResyncRequest = Time.unscaledTime;
            Plugin.Log.LogInfo($"[NetClient] resync requested: {reason}");
            var w = m_s.Begin(Msg.ResyncRequest);
            w.Put(reason);
            m_s.Send(w, Delivery.Reliable);
        }
        private static readonly Dictionary<int, GameObject> s_corpseByTag = new Dictionary<int, GameObject>();
        private static readonly HashSet<GameObject> s_corpseMapped = new HashSet<GameObject>();

        private int m_runId = -1;
        private bool m_awaitingScene;
        private string m_targetScene;
        private PendingRoom m_pendingRoom;
        private bool m_ensuringSegments;

        /// <summary>Сид забега хоста (для SegmentManager.ResetSeed).</summary>
        public static int RunSeed { get; private set; }

        // флаги «это делает сеть» для патчей подавления
        public static bool ApplyingDeath;
        public static bool ApplyingScene;
        public static bool ApplyingGameOver;

        // интерфейс хоста
        public static readonly List<(Vector3 pos, string text)> HostLabels = new List<(Vector3, string)>();
        public static int ReviveKills = -1;
        public static int ReviveNeed;
        public static bool ReviveOnFloor;

        private sealed class PendingRoom
        {
            public string Name;
            public int Segment;
            public int Layer;
            public bool HasA;
            public Vector2 PosA;
            public bool HasB;
            public Vector2 PosB;
            public HashSet<int> Inactive = new HashSet<int>();
        }

        internal NetClient(NetSession s)
        {
            m_s = s;
        }

        private static GamePlayer SeatPuppet(Seat seat) => seat == Seat.B ? CoopPlayers.P1 : CoopPlayers.P2;

        // ================= сессия =================

        internal void WriteHelloState(NetDataWriter w)
        {
            w.Put(SceneManager.GetActiveScene().name);
            w.Put(m_runId);
            var room = GameManager.InstanceNoWarnings != null ? GameManager.InstanceNoWarnings.CurrentRoom : null;
            w.Put(room != null ? room.name : "");
        }

        internal void OnWelcome()
        {
            CoopInput.MarkDirty();
        }

        internal void OnPeerLost()
        {
        }

        private static VillageData s_ownVillage;
        private static GameData.DifficultyMode? s_ownDifficulty;

        internal void OnSessionEnded()
        {
            var dm = DataManager.Instance;
            if (dm != null && s_ownVillage != null)
            {
                dm.VillageData = s_ownVillage;
            }
            if (dm != null && dm.GameData != null && s_ownDifficulty != null)
            {
                dm.GameData.Difficulty = s_ownDifficulty.Value;
            }
            s_ownVillage = null;
            s_ownDifficulty = null;
            m_byId.Clear();
            HostLabels.Clear();
            ReviveKills = -1;
            RunSeed = 0;
            CoopInput.MarkDirty();
            // мир клиента — марионетки без хоста; возвращаемся в меню
            if (SceneManager.GetActiveScene().name != "MainMenu")
            {
                ApplyingScene = true;
                try
                {
                    SceneLoader.LoadMainMenuScene();
                }
                finally
                {
                    ApplyingScene = false;
                }
            }
        }

        // ================= сообщения =================

        internal void OnMessage(Msg type, NetDataReader r)
        {
            switch (type)
            {
                case Msg.SceneLoad:
                    OnSceneLoad(r);
                    break;
                case Msg.RoomEnter:
                    OnRoomEnter(r);
                    break;
                case Msg.EntityRegister:
                    OnRegister(r);
                    break;
                case Msg.Snapshot:
                    OnSnapshot(r);
                    break;
                case Msg.EntityDeath:
                    OnDeath(r.GetInt());
                    break;
                case Msg.EntityDespawn:
                    OnDespawn(r.GetInt());
                    break;
                case Msg.PlayerState:
                    OnPlayerState(r);
                    break;
                case Msg.UiState:
                    OnUi(r);
                    break;
                case Msg.GameOver:
                    OnGameOver();
                    break;
                case Msg.RoomCheck:
                    OnRoomCheck(r);
                    break;
                case Msg.Talk:
                    OnTalk(r);
                    break;
                case Msg.Attach:
                    OnAttach(r);
                    break;
                case Msg.Look:
                    OnLook(r);
                    break;
                case Msg.LightsOut:
                    OnLightsOut(r);
                    break;
                case Msg.TowerExplosion:
                    OnTowerExplosion();
                    break;
                case Msg.Window:
                    OnWindow(r);
                    break;
            }
        }

        // ================= окно выбора =================

        /// <summary>Окно открывается по команде хоста (локально у клиента окна не открываются: выбор делает хост по пересланному вводу).</summary>
        internal static bool ApplyingWindow;

        private Transform m_windowAnchor;

        private void OnWindow(NetDataReader r)
        {
            bool open = r.GetBool();
            var own = CoopPlayers.P1; // свой персонаж (у клиента место B — главный)
            if (own == null || own.Components == null || own.Components.InteractionWindow == null)
            {
                if (open)
                {
                    r.GetByte(); r.GetBool(); r.GetBool(); r.GetFloat(); r.GetInt(); r.GetFloat(); r.GetFloat(); r.GetFloat(); r.GetFloat(); r.GetString(); r.GetString(); r.GetString();
                }
                return;
            }
            var win = own.Components.InteractionWindow;
            ApplyingWindow = true;
            try
            {
                if (!open)
                {
                    win.CloseWindow();
                    Plugin.Log.LogInfo("[NetClient] window closed");
                    return;
                }
                var state = (TalkState)r.GetByte();
                bool canBeClosed = r.GetBool();
                bool follow = r.GetBool();
                float delay = r.GetFloat();
                int targetId = r.GetInt();
                var tpos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
                var offset = new Vector2(r.GetFloat(), r.GetFloat());
                string ov1 = r.GetString();
                string ov2 = r.GetString();
                string ov3 = r.GetString();
                Transform target = null;
                if (targetId == NetPaths.SeatA || targetId == NetPaths.SeatB)
                {
                    var p = SeatPuppet(targetId == NetPaths.SeatA ? Seat.A : Seat.B);
                    target = p != null ? p.transform : null;
                }
                else if (targetId != 0 && m_byId.TryGetValue(targetId, out var e) && e.Alive)
                {
                    target = e.T;
                }
                if (target == null && tpos != Vector3.zero)
                {
                    // объект хоста у нас не найден — ставим окно в ту же точку мира
                    if (m_windowAnchor == null)
                    {
                        m_windowAnchor = new GameObject("CoopWindowAnchor").transform;
                    }
                    m_windowAnchor.position = tpos;
                    target = m_windowAnchor;
                }
                win.OpenWindow(null, null, null, state, delay, true, follow, canBeClosed, target, offset,
                    string.IsNullOrEmpty(ov1) ? null : ov1, string.IsNullOrEmpty(ov2) ? null : ov2, string.IsNullOrEmpty(ov3) ? null : ov3);
                Plugin.Log.LogInfo($"[NetClient] window {state} target={(target != null ? target.name : "-")} open={win.IsOpen}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[NetClient] window: {ex.GetBaseException().Message}");
            }
            finally
            {
                ApplyingWindow = false;
            }
        }

        // ================= свет =================

        /// <summary>Применяется состояние «свет погас» от хоста (локальные вызовы ловушки на клиенте заглушены).</summary>
        internal static bool ApplyingLights;

        private static readonly System.Reflection.MethodInfo s_lightsOut = AccessTools.Method(typeof(LuckyTower.Traps.TrapLightsOut), "LightsOut");
        private static readonly System.Reflection.MethodInfo s_lightsOn = AccessTools.Method(typeof(LuckyTower.Traps.TrapLightsOut), "OnPlayerExits");
        private static readonly System.Reflection.MethodInfo s_lightsInitialize = AccessTools.Method(typeof(LuckyTower.Traps.TrapLightsOut), "Initialize");
        private static readonly AccessTools.FieldRef<LuckyTower.Traps.TrapLightsOut, bool> s_lightsInit = AccessTools.FieldRefAccess<LuckyTower.Traps.TrapLightsOut, bool>("m_initialized");

        // ================= взрыв башни =================

        /// <summary>Идёт ролик взрыва башни от хоста: локальный DestroyTower у клиента не запускается, ролик показывает сам клиент.</summary>
        internal static bool TowerExploding { get; private set; }

        private void OnTowerExplosion()
        {
            if (TowerExploding)
            {
                return;
            }
            Plugin.Log.LogInfo("[NetClient] tower explosion");
            m_s.StartCoroutine(TowerExplosionRoutine());
        }

        private static IEnumerator TowerExplosionRoutine()
        {
            var cm = CameraManager.Instance;
            var gm = GameManager.InstanceNoWarnings;
            if (cm == null || gm == null || cm.PlayingVideo)
            {
                yield break;
            }
            TowerExploding = true;
            try
            {
                // персонажи не двигаются, пока идёт ролик (как у хоста)
                foreach (var p in new[] { CoopPlayers.P1, CoopPlayers.P2 })
                {
                    if (p != null && p.Stats != null)
                    {
                        p.Stats.InputAllowed = false;
                        p.Stats.AllowMovement = false;
                    }
                }
                try
                {
                    // звук как у хоста: музыка и звуки башни глохнут
                    gm.MasterMixer.GetFloat("SounddesignVolume", out var vol);
                    PlayerPrefs.SetFloat("soundDesignVolume", vol);
                    AudioManager.Instance.StopMusic();
                    gm.MasterMixer.SetFloat("SounddesignVolume", -80f);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[NetClient] explosion audio: {e.Message}");
                }
                var vp = cm.MainVideoPlayer;
                if (vp == null || gm.VideoLibrary == null || gm.VideoLibrary.ExplodingTower == null)
                {
                    yield break;
                }
                cm.PlayingVideo = true;
                vp.clip = gm.VideoLibrary.ExplodingTower;
                vp.renderMode = UnityEngine.Video.VideoRenderMode.CameraNearPlane;
                vp.Prepare();
                float until = Time.realtimeSinceStartup + 10f;
                while (!vp.isPrepared && Time.realtimeSinceStartup < until)
                {
                    yield return null;
                }
                vp.Play();
                Plugin.Log.LogInfo("[NetClient] tower explosion video playing");
                until = Time.realtimeSinceStartup + 90f;
                while (vp.isPlaying && Time.realtimeSinceStartup < until)
                {
                    yield return null;
                }
                Plugin.Log.LogInfo("[NetClient] tower explosion video finished");
                cm.PlayingVideo = false;
            }
            finally
            {
                TowerExploding = false;
            }
        }

        private void OnLightsOut(NetDataReader r)
        {
            int id = r.GetInt();
            bool off = r.GetBool();
            bool instant = r.GetBool();
            if (!m_byId.TryGetValue(id, out var e) || !e.Alive)
            {
                return;
            }
            var trap = e.Go.GetComponent<LuckyTower.Traps.TrapLightsOut>();
            if (trap == null)
            {
                return;
            }
            ApplyingLights = true;
            try
            {
                // у клиента триггер ловушки заглушен, поэтому её Initialize (список светильников, плоскость темноты) зовём сами
                if (!s_lightsInit(trap))
                {
                    s_lightsInitialize?.Invoke(trap, null);
                }
                if (off)
                {
                    s_lightsOut?.Invoke(trap, null);
                }
                else
                {
                    s_lightsOn?.Invoke(trap, new object[] { !instant });
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[NetClient] lights {(off ? "out" : "on")}: {ex.GetBaseException().Message}");
            }
            finally
            {
                ApplyingLights = false;
            }
        }

        // ================= видимость частей персонажей =================

        private readonly byte[][] m_look = new byte[2][];
        private readonly int[] m_lookBad = new int[2];
        private readonly NetDataReader m_lookReader = new NetDataReader();
        private static readonly int s_enterDoorParam = Animator.StringToHash("EnterDoor");

        private void OnLook(NetDataReader r)
        {
            int i = r.GetByte();
            if (i < 0 || i > 1)
            {
                return;
            }
            m_look[i] = r.GetRemainingBytes();
            ApplyLook(i);
        }

        private void ApplyLook(int i)
        {
            var puppet = SeatPuppet((Seat)i);
            if (puppet == null || m_look[i] == null || CoopVisibility.IsHidden(puppet))
            {
                return; // спрятан у двери — применим, когда покажется
            }
            m_lookReader.SetSource(m_look[i]);
            int changed = NetLook.Apply(puppet, m_lookReader);
            var anim = puppet.Components.Animator != null ? puppet.Components.Animator.AnimatorComponent : null;
            // сильно разошлось (застрял «вид со спины»): аниматор запомнил неверный исходный вид и каждый кадр его возвращает —
            // выставив части как у хоста, заставляем его запомнить исходный вид заново (не во время входа в дверь)
            m_lookBad[i] = changed >= 12 ? m_lookBad[i] + 1 : 0;
            if (m_lookBad[i] >= 2 && anim != null && anim.isActiveAndEnabled && !anim.GetBool(s_enterDoorParam))
            {
                m_lookBad[i] = 0;
                anim.Rebind();
                m_lookReader.SetSource(m_look[i]);
                NetLook.Apply(puppet, m_lookReader);
                Plugin.Log.LogInfo($"[NetClient] seat {(Seat)i}: {changed} parts differed from host — animator defaults rebound");
            }
        }

        // ================= предметы в руках =================

        private void OnAttach(NetDataReader r)
        {
            int id = r.GetInt();
            int seatId = r.GetInt();
            string path = r.GetString();
            var lp = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            var lr = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
            var ls = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            if (!m_byId.TryGetValue(id, out var e) || !e.Alive)
            {
                return;
            }
            e.AttachSeat = seatId;
            e.AttachPath = path;
            e.AttachLocalPos = lp;
            e.AttachLocalRot = lr;
            e.AttachLocalScale = ls;
            e.AttachPending = true;
            if (!TryAttach(e) && !m_pendingAttach.Contains(e))
            {
                m_pendingAttach.Add(e);
            }
        }

        /// <summary>Прикрепить предмет к той же кости марионетки, что у хоста (false — марионетки ещё нет, повторим).</summary>
        private bool TryAttach(NetEntity e)
        {
            if (!e.AttachPending || !e.Alive)
            {
                return true;
            }
            if (e.AttachSeat == 0)
            {
                if (e.Attached)
                {
                    var room = GameManager.InstanceNoWarnings != null ? GameManager.InstanceNoWarnings.CurrentRoom : null;
                    e.T.SetParent(room != null ? room.transform : null, true);
                    e.Attached = false;
                    e.TargetPos = e.T.position;
                    e.TargetRot = e.T.eulerAngles.z;
                    e.TargetFlip = e.T.lossyScale.x < 0f;
                    e.HasTarget = true;
                }
                e.AttachPending = false;
                return true;
            }
            var puppet = SeatPuppet(e.AttachSeat == NetPaths.SeatA ? Seat.A : Seat.B);
            if (puppet == null)
            {
                return false;
            }
            var parent = string.IsNullOrEmpty(e.AttachPath) ? puppet.transform : NetPaths.Find(puppet.transform, e.AttachPath);
            if (parent == null)
            {
                return false;
            }
            e.T.SetParent(parent, false);
            e.T.localPosition = e.AttachLocalPos;
            e.T.localRotation = e.AttachLocalRot;
            e.T.localScale = e.AttachLocalScale;
            e.Attached = true;
            e.AttachPending = false;
            return true;
        }

        private void RetryAttachments()
        {
            for (int k = m_pendingAttach.Count - 1; k >= 0; k--)
            {
                var e = m_pendingAttach[k];
                if (!e.Alive || !e.AttachPending || TryAttach(e))
                {
                    m_pendingAttach.RemoveAt(k);
                }
            }
        }

        // ================= реплики =================

        /// <summary>Проигрывается реплика, присланная хостом (остальные на клиенте заглушены).</summary>
        internal static bool ApplyingTalk;

        private static readonly System.Reflection.MethodInfo s_talkRoutine = AccessTools.Method(typeof(DialogueManager), "TalkRoutine");

        private Character CharOf(int id)
        {
            if (id == NetPaths.SeatA || id == NetPaths.SeatB)
            {
                return SeatPuppet(id == NetPaths.SeatA ? Seat.A : Seat.B);
            }
            return m_byId.TryGetValue(id, out var e) && e.Alive ? e.Character : null;
        }

        private void OnTalk(NetDataReader r)
        {
            int id = r.GetInt();
            string key = r.GetString();
            bool dead = r.GetBool();
            bool endless = r.GetBool();
            var c = CharOf(id);
            var gm = GameManager.InstanceNoWarnings;
            if (c == null || c.Components == null || c.Components.AudioComponents == null || gm == null || gm.LocaDatabase == null || s_talkRoutine == null)
            {
                return;
            }
            var dm = c.Components.DialogueManager;
            if (dm == null || !dm.isActiveAndEnabled)
            {
                dm = gm.Player != null ? gm.Player.Components.DialogueManager : null;
            }
            if (dm == null || !dm.isActiveAndEnabled)
            {
                return;
            }
            var element = gm.LocaDatabase.GetTextElement(key);
            if (element.Key == null)
            {
                return;
            }
            ApplyingTalk = true;
            try
            {
                var routine = (IEnumerator)s_talkRoutine.Invoke(dm, new object[] { element, c.Components.AudioComponents, dead, endless });
                dm.StartCoroutine(routine);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetClient] talk {key}: {e.GetBaseException().Message}");
            }
            finally
            {
                ApplyingTalk = false;
            }
        }

        private void OnRoomCheck(NetDataReader r)
        {
            string room = r.GetString();
            int count = r.GetInt();
            uint hash = r.GetUInt();
            if (m_awaitingScene || m_pendingRoom != null || m_ensuringSegments)
            {
                return;
            }
            var gm = GameManager.InstanceNoWarnings;
            string mine = gm != null && gm.CurrentRoom != null ? gm.CurrentRoom.name : "";
            uint myHash = 0;
            int myCount = 0;
            foreach (var e in m_byId.Values)
            {
                myHash ^= (uint)e.Id * 2654435761u;
                myCount++;
            }
            foreach (int id in m_unresolved)
            {
                myHash ^= (uint)id * 2654435761u;
                myCount++;
            }
            if (mine != room)
            {
                RequestResync($"room {mine} != host {room}");
            }
            else if (myCount != count || myHash != hash)
            {
                RequestResync($"entities {myCount} != host {count}");
            }
        }

        private void OnSceneLoad(NetDataReader r)
        {
            int runId = r.GetInt();
            string sceneName = r.GetString();
            var loadScene = (VillageManager.LoadScene)r.GetInt();
            int seed = r.GetInt();
            int difficulty = r.GetInt();
            byte[] village = r.GetBytesWithLength();

            RunSeed = seed;
            var dm = DataManager.Instance;
            if (dm != null && dm.GameData != null)
            {
                if (s_ownDifficulty == null)
                {
                    s_ownDifficulty = dm.GameData.Difficulty;
                }
                dm.GameData.Difficulty = (GameData.DifficultyMode)difficulty;
            }
            if (dm != null && village.Length > 0)
            {
                try
                {
                    var hostVillage = MessagePack.MessagePackSerializer.Deserialize<VillageData>(village);
                    if (s_ownVillage == null)
                    {
                        s_ownVillage = dm.VillageData; // своя деревня вернётся после сессии (сейв клиента не пишется)
                    }
                    dm.VillageData = hostVillage;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[NetClient] village data: {e.Message}");
                }
            }
            var gm = GameManager.InstanceNoWarnings;
            bool same = m_runId == runId && SceneManager.GetActiveScene().name == sceneName && CoopPlayers.IsInitialized(gm);
            m_runId = runId;
            m_byId.Clear();
            m_pendingRoom = null;
            if (!same)
            {
                s_originGo.Clear();
            }
            Plugin.Log.LogInfo($"[NetClient] scene {sceneName} run={runId} seed={seed} diff={difficulty} ({(same ? "already here" : "loading")})");
            if (same)
            {
                SendSceneReady();
                return;
            }
            if (loadScene == VillageManager.LoadScene.None)
            {
                Plugin.Log.LogWarning($"[NetClient] host scene {sceneName} is not supported online");
                return;
            }
            m_awaitingScene = true;
            m_targetScene = sceneName;
            ApplyingScene = true;
            try
            {
                if (gm != null)
                {
                    gm.DestroyAll();
                }
                SceneLoader.LoadSceneByName(loadScene);
            }
            finally
            {
                ApplyingScene = false;
            }
        }

        private void SendSceneReady()
        {
            var w = m_s.Begin(Msg.SceneReady);
            w.Put(m_runId);
            m_s.Send(w, Delivery.Reliable);
        }

        // ================= комната =================

        private void OnRoomEnter(NetDataReader r)
        {
            var p = new PendingRoom
            {
                Name = r.GetString(),
                Segment = r.GetInt(),
            };
            r.GetInt(); // floor
            p.Layer = r.GetInt();
            p.HasA = r.GetBool();
            if (p.HasA)
            {
                p.PosA = new Vector2(r.GetFloat(), r.GetFloat());
            }
            p.HasB = r.GetBool();
            if (p.HasB)
            {
                p.PosB = new Vector2(r.GetFloat(), r.GetFloat());
            }
            int nInactive = r.GetUShort();
            p.Inactive = new HashSet<int>();
            for (int i = 0; i < nInactive; i++)
            {
                p.Inactive.Add(r.GetInt());
            }
            // полная (пере)синхронизация: объекты, которые клиент создавал сам, будут пересозданы по регистрации
            foreach (var e in m_byId.Values)
            {
                if ((e.SpawnedByNet || e.Attached) && e.Alive)
                {
                    Object.Destroy(e.Go); // предметы в руках хост пришлёт заново и они снова прикрепятся
                }
            }
            m_byId.Clear();
            m_unresolved.Clear();
            m_pendingAttach.Clear();
            m_pendingRegs.Clear();
            m_unknownSince = -1f;

            // сразу после перезапуска забега GameManager новой сцены может ещё не существовать — применим комнату в Tick
            if (!CoopPlayers.IsInitialized(GameManager.InstanceNoWarnings))
            {
                m_pendingRoom = p;
                return;
            }
            var room = FindRoom(p.Name, p.Segment);
            if (room == null)
            {
                m_pendingRoom = p;
                if (!m_ensuringSegments)
                {
                    m_s.StartCoroutine(EnsureSegments(p.Segment));
                }
                return;
            }
            ApplyRoom(room, p);
        }

        private static readonly Dictionary<string, GameObject> s_prefabs = new Dictionary<string, GameObject>();

        /// <summary>Префаб (ассет, не объект сцены) по имени: враги/мыши, которых спавнеры создают во время игры.</summary>
        private static GameObject FindPrefab(string name)
        {
            if (s_prefabs.TryGetValue(name, out var cached))
            {
                return cached;
            }
            GameObject found = null;
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go.name == name && !go.scene.IsValid() && go.transform.parent == null
                    && (go.GetComponent<Character>() != null || go.GetComponent<FlyingBehaviour>() != null || go.GetComponent<Animator>() != null || go.GetComponent<Rigidbody2D>() != null))
                {
                    found = go;
                    break;
                }
            }
            s_prefabs[name] = found; // промах тоже запоминаем: поиск по всем ассетам дорогой
            return found;
        }

        private static GameObject SpawnFromPrefab(Room room, string path, Vector3 pos)
        {
            int slash = path.LastIndexOf(NetPaths.Sep, StringComparison.Ordinal);
            string leaf = slash >= 0 ? path.Substring(slash + 1) : path;
            int hash = leaf.LastIndexOf('\u001e');
            if (hash > 0)
            {
                leaf = leaf.Substring(0, hash);
            }
            string baseName = leaf.Replace("(Clone)", "").Trim();
            var prefab = FindPrefab(baseName);
            if (prefab == null)
            {
                return null;
            }
            var parent = slash > 0 ? NetPaths.Find(room.transform, path.Substring(0, slash)) : null;
            var go = Object.Instantiate(prefab, pos, Quaternion.identity, parent != null ? parent : room.transform);
            go.name = leaf;
            NetPaths.MarkRuntime(go);
            Plugin.Log.LogInfo($"[NetClient] spawned {leaf} from prefab (host created it at runtime)");
            return go;
        }

        /// <summary>Персонаж, впервые увиденный хостом в комнате «имя|путь» (спутник мог уйти оттуда за игроком).</summary>
        private static GameObject FindByOrigin(string key)
        {
            if (s_originGo.TryGetValue(key, out var go) && go != null)
            {
                return go;
            }
            int bar = key.IndexOf('|');
            var src = bar > 0 ? FindRoom(key.Substring(0, bar)) : null;
            var t = src != null ? NetPaths.Find(src.transform, key.Substring(bar + 1)) : null;
            if (t == null)
            {
                return null;
            }
            s_originGo[key] = t.gameObject;
            return t.gameObject;
        }

        /// <summary>
        /// Комната по имени. Имена в пещерах повторяются между кластерами («Cave 6» ×3) — тогда выбираем по номеру сегмента хоста
        /// (иначе клиент вставал в комнату другого кластера: «Cave» хост=32 объекта, клиент=15).
        /// </summary>
        private static Room FindRoom(string name, int segment = int.MinValue)
        {
            Room first = null;
            foreach (var room in Object.FindObjectsByType<Room>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (room.name != name)
                {
                    continue;
                }
                if (segment == int.MinValue || room.SegmentID == segment)
                {
                    return room;
                }
                if (first == null)
                {
                    first = room;
                }
            }
            return first;
        }

        /// <summary>Подключение посреди забега: догенерировать сегменты до комнаты хоста (тот же сид).</summary>
        private IEnumerator EnsureSegments(int targetSegment)
        {
            m_ensuringSegments = true;
            var sm = SegmentManager.Instance;
            for (int seg = 1; seg <= targetSegment && sm != null && m_pendingRoom != null; seg++)
            {
                if (FindRoom(m_pendingRoom.Name, m_pendingRoom.Segment) != null)
                {
                    break;
                }
                bool done = false;
                Plugin.Log.LogInfo($"[NetClient] generating segment {seg} to reach {m_pendingRoom.Name}");
                // у хоста сегмент N создаётся, когда игрок входит в выходную комнату сегмента N−1 (SegmentID = N−1)
                if (seg >= 2)
                {
                    SyncPlayerSegment(GameManager.InstanceNoWarnings, seg - 1);
                }
                sm.CreateNextSegment((SegmentManager.TowerSegment)seg, () => done = true);
                float t = 0f;
                while (!done && t < 15f)
                {
                    t += Time.unscaledDeltaTime;
                    yield return null;
                }
            }
            m_ensuringSegments = false;
            if (m_pendingRoom != null)
            {
                var room = FindRoom(m_pendingRoom.Name, m_pendingRoom.Segment);
                if (room != null)
                {
                    ApplyRoom(room, m_pendingRoom);
                }
                else
                {
                    Plugin.Log.LogError($"[NetClient] room {m_pendingRoom.Name} not found — generation differs from host");
                }
                m_pendingRoom = null;
            }
        }

        /// <summary>
        /// Генерация сегмента читает Player.Stats.SegmentID (имена комнат «S0N_…», тип пола, списки комнат для телепорта),
        /// а игра выставляет его только при проходе через дверь. Клиент входит в комнаты по сообщению хоста — без этого его
        /// значение отставало, и сегмент 3 получал другие имена комнат («S02_…» вместо «S03_…» → «комната не найдена»).
        /// </summary>
        private static void SyncPlayerSegment(GameManager gm, int segmentId)
        {
            var player = gm != null ? gm.Player : null;
            if (player != null && player.Stats != null && player.Stats.SegmentID != segmentId)
            {
                Plugin.Log.LogInfo($"[NetClient] player segment {player.Stats.SegmentID} -> {segmentId}");
                player.Stats.SegmentID = segmentId;
            }
        }

        private static readonly Func<Room, int> s_depthLayers =AccessTools.MethodDelegate<Func<Room, int>>(AccessTools.PropertyGetter(typeof(Room), "DepthLayers"));

        private void ApplyRoom(Room room, PendingRoom p)
        {
            var gm = GameManager.InstanceNoWarnings;
            if (gm == null)
            {
                m_pendingRoom = p;
                return;
            }
            m_eveliusCheckAt = Time.unscaledTime + 3f;
            SyncPlayerSegment(gm, room.SegmentID);
            if (gm.CurrentRoom != room)
            {
                try
                {
                    var prev = gm.CurrentRoom;
                    if (prev != null)
                    {
                        prev.ExitRoom();
                    }
                    gm.PreviousRoom = prev;
                    gm.CurrentRoom = room;
                    room.gameObject.SetActive(true);
                    room.EnterRoom();
                    if (!room.FetchLayers)
                    {
                        LayerManager.FinishedRoom(s_depthLayers(room));
                    }
                    CameraManager.Instance.SetConfinerCollider(room.CompositeCollider);
                    gm.RemoveFog();
                    gm.PrepareFog();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[NetClient] room switch error: {e}");
                }
            }
            LayerManager.CurrentPlayerLayer = p.Layer;
            int changed = 0;
            try
            {
                changed = NetPaths.ApplyInactive(room.transform, room.name, p.Inactive);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetClient] room {room.name}: active alignment failed: {e.Message}");
            }

            EnsureSeatPuppets(p.HasA ? (Vector3)p.PosA : (Vector3?)null);
            if (p.HasA)
            {
                Teleport(SeatPuppet(Seat.A), p.PosA);
            }
            if (p.HasB)
            {
                Teleport(SeatPuppet(Seat.B), p.PosB);
            }
            var own = SeatPuppet(Seat.B) ?? SeatPuppet(Seat.A);
            if (own != null && own.Components.CameraFocusPoint != null)
            {
                var f = own.Components.CameraFocusPoint.position;
                CameraManager.Instance.ForcePosition(new Vector3(f.x, f.y, -10f));
            }
            Plugin.Log.LogInfo($"[NetClient] room -> {room.name} (active fixes: {changed})");
        }

        private void Teleport(GamePlayer p, Vector2 pos)
        {
            if (p == null)
            {
                return;
            }
            p.transform.position = new Vector3(pos.x, pos.y, p.transform.position.z);
            var e = SeatEntity(p == SeatPuppet(Seat.A) ? Seat.A : Seat.B);
            if (e != null)
            {
                e.TargetPos = p.transform.position;
                e.HasTarget = true;
            }
        }

        // ================= сущности =================

        private sealed class Pending
        {
            public int Id;
            public EntityKind Kind;
            public string Path;
            public string ItemName;
            public Vector3 Pos;
            public bool Active;
            public float Until;
        }

        private readonly Dictionary<int, Pending> m_pendingRegs = new Dictionary<int, Pending>();
        private float m_nextPendingRetry;

        internal int m_registerBatches;

        // Эвелиус в хабе: у клиента его Start не выполняется (не самоуничтожается по положению марионетки),
        // поэтому через пару секунд после входа сверяем с хостом — если хост его не зарегистрировал, у хоста его нет
        private float m_eveliusCheckAt = -1f;

        private void CheckOrphanEvelius(Room room)
        {
            if (m_eveliusCheckAt < 0f || Time.unscaledTime < m_eveliusCheckAt || room == null)
            {
                return;
            }
            m_eveliusCheckAt = -1f;
            foreach (var ev in room.GetComponentsInChildren<LuckyTower.Evelius.EveliusController>(true))
            {
                bool known = false;
                foreach (var e in m_byId.Values)
                {
                    if (e.Alive && e.Go == ev.gameObject)
                    {
                        known = true;
                        break;
                    }
                }
                if (!known)
                {
                    Plugin.Log.LogInfo($"[NetClient] Evelius {ev.name}: not registered by host — removing here too");
                    Object.Destroy(ev.gameObject);
                }
            }
        }

        private void OnRegister(NetDataReader r)
        {
            int n = r.GetUShort();
            m_registerBatches++;
            var gm = GameManager.InstanceNoWarnings;
            var room = gm != null ? gm.CurrentRoom : null;
            int unresolved = 0;
            var unresolvedNames = new List<string>();
            for (int i = 0; i < n; i++)
            {
                int id = r.GetInt();
                var kind = (EntityKind)r.GetByte();
                string path = r.GetString();
                string itemName = r.GetString();
                var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
                bool active = r.GetBool();
                if (room == null)
                {
                    continue;
                }
                if (!TryRegister(gm, room, id, kind, path, itemName, pos, active))
                {
                    unresolved++;
                    m_unresolved.Add(id);
                    if (unresolvedNames.Count < 6)
                    {
                        unresolvedNames.Add(path.Length > 0 ? NetPaths.Pretty(path) : itemName);
                    }
                    if (path.Length > 0)
                    {
                        // игра могла ещё не создать этот объект (платформа дверей в хабе) — повторим несколько секунд
                        m_pendingRegs[id] = new Pending { Id = id, Kind = kind, Path = path, ItemName = itemName, Pos = pos, Active = active, Until = Time.unscaledTime + 8f };
                    }
                }
            }
            if (unresolved > 0)
            {
                Plugin.Log.LogWarning($"[NetClient] {unresolved} of {n} host objects not found here: {string.Join("; ", unresolvedNames)}");
            }
        }

        private void RetryPendingRegistrations()
        {
            if (m_pendingRegs.Count == 0 || Time.unscaledTime < m_nextPendingRetry)
            {
                return;
            }
            m_nextPendingRetry = Time.unscaledTime + 0.5f;
            var gm = GameManager.InstanceNoWarnings;
            var room = gm != null ? gm.CurrentRoom : null;
            if (room == null)
            {
                return;
            }
            List<int> done = null;
            int resolved = 0;
            foreach (var p in m_pendingRegs.Values)
            {
                bool ok = !m_unresolved.Contains(p.Id) || TryRegister(gm, room, p.Id, p.Kind, p.Path, p.ItemName, p.Pos, p.Active);
                if (ok)
                {
                    resolved++;
                }
                if (ok || Time.unscaledTime > p.Until)
                {
                    (done ??= new List<int>()).Add(p.Id);
                }
            }
            if (done != null)
            {
                foreach (int id in done)
                {
                    m_pendingRegs.Remove(id);
                }
            }
            if (resolved > 0)
            {
                Plugin.Log.LogInfo($"[NetClient] {resolved} host objects resolved on retry");
            }
        }

        /// <summary>Найти/создать объект для записи хоста и сделать его марионеткой. false — объекта нет (пока).</summary>
        private bool TryRegister(GameManager gm, Room room, int id, EntityKind kind, string path, string itemName, Vector3 pos, bool active)
        {
            {

                GameObject go = null;
                bool spawned = false;
                if (path.Length > 0)
                {
                    var t = NetPaths.Find(room.transform, path);
                    go = t != null ? t.gameObject : null;
                    if (go != null && kind == EntityKind.Character && !s_originGo.ContainsKey(room.name + "|" + path))
                    {
                        s_originGo[room.name + "|" + path] = go; // если персонаж потом уйдёт за игроком в другую комнату
                    }
                }
                if (go == null && itemName.StartsWith("#origin:"))
                {
                    go = FindByOrigin(itemName.Substring(8));
                    if (go != null && !go.transform.IsChildOf(room.transform))
                    {
                        go.transform.SetParent(room.transform, true);
                        NetPaths.MarkRuntime(go);
                    }
                }
                if (go == null && itemName.StartsWith("#clone:") && int.TryParse(itemName.Substring(7), out var srcId)
                    && m_byId.TryGetValue(srcId, out var src) && src.Alive)
                {
                    go = Object.Instantiate(src.Go, pos, src.T.rotation, src.T.parent);
                    go.name = src.Go.name + "+";
                    NetPaths.MarkRuntime(go);
                    spawned = true;
                }
                if (go == null && itemName.StartsWith("#corpse:") && int.TryParse(itemName.Substring(8), out var corpseTag))
                {
                    if (s_corpseByTag.TryGetValue(corpseTag, out go) && go != null)
                    {
                        s_corpseMapped.Add(go);
                        FreezeBody(go, go.transform.position); // положение трупа дальше ведёт хост
                    }
                }
                if (go == null && kind == EntityKind.Item && itemName.Length > 0 && !itemName.StartsWith("#") && ItemCatalog.Has(gm.ItemLibrary, itemName))
                {
                    var prefab = gm.ItemLibrary.GetItem(itemName);
                    if (prefab != null)
                    {
                        go = Object.Instantiate(prefab, pos, Quaternion.identity, room.transform).gameObject;
                        NetPaths.MarkRuntime(go);
                        spawned = true;
                    }
                }
                if (go == null && path.Length > 0 && (kind == EntityKind.Character || kind == EntityKind.Other))
                {
                    // создан у хоста во время игры (спавнер, призыв): у нас его нет — создаём из того же префаба
                    go = SpawnFromPrefab(room, path, pos);
                    spawned = go != null;
                }
                if (go == null)
                {
                    return false;
                }
                m_unresolved.Remove(id);
                var e = NetEntity.For(id, go, kind);
                e.SpawnedByNet = spawned;
                MakePuppet(e);
                if (go.activeSelf != active)
                {
                    go.SetActive(active);
                }
                e.TargetPos = new Vector3(pos.x, pos.y, go.transform.position.z);
                e.TargetRot = go.transform.eulerAngles.z;
                e.TargetFlip = go.transform.localScale.x < 0f;
                e.HasTarget = true;
                m_byId[id] = e;
                return true;
            }
        }

        private static void MakePuppet(NetEntity e)
        {
            if (e.Rb != null)
            {
                e.Rb.simulated = false;
            }
            // события анимации дёргают логику (урон, смерть, бросок мусора из окна со своим рандомом) — у марионеток глушим;
            // всё, что хост создаёт по событиям, приходит регистрацией
            if (e.Anim != null)
            {
                e.Anim.fireEvents = false;
            }
        }

        private static readonly AccessTools.FieldRef<CharacterStats, int> s_healthField = AccessTools.FieldRefAccess<CharacterStats, int>("health");

        /// <summary>Урон по врагу виден у клиента: здоровье ставим напрямую (без событий) и зовём штатную перекраску.</summary>
        private static void ApplyHealthVisual(NetEntity e, byte hp)
        {
            var c = e.Character;
            if (c == null || c.Stats == null || c.Stats.Dead || c.Actions == null)
            {
                return;
            }
            try
            {
                int max = Mathf.Max(1, c.Stats.MaximumHealth);
                s_healthField(c.Stats) = Mathf.Clamp(Mathf.RoundToInt(max * hp / 255f), 0, max);
                if (hp < 255)
                {
                    c.Actions.ApplyDamageToRendering();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[NetClient] damage visual {c.name}: {ex.Message}");
            }
        }

        private NetEntity SeatEntity(Seat seat)
        {
            var puppet = SeatPuppet(seat);
            var e = m_seatEntity[(int)seat];
            if (puppet == null)
            {
                return null;
            }
            if (e == null || e.Go != puppet.gameObject)
            {
                e = NetEntity.For(seat == Seat.A ? NetPaths.SeatA : NetPaths.SeatB, puppet.gameObject, EntityKind.Seat);
                if (e.Anim != null)
                {
                    // хост на время дверей прячет графику; выключаясь посреди анимации «вид со спины», аниматор оставлял бы
                    // её значения как исходные — персонаж навсегда «лысый и голый». При выключении возвращаем исходный вид.
                    e.Anim.writeDefaultValuesOnDisable = true;
                }
                e.TargetPos = puppet.transform.position;
                e.TargetFlip = puppet.transform.localScale.x < 0f;
                m_seatEntity[(int)seat] = e;
            }
            return e;
        }

        private void OnSnapshot(NetDataReader r)
        {
            float hostTime = r.GetFloat(); // время хоста (реальное): по нему буфер интерполяции
            m_clock.OnSample(hostTime);
            bool seatPacket = false;
            int layer = r.GetInt();
            ushort ackSeq = r.GetUShort();
            m_sender.OnAck(ackSeq);
            int n = r.GetUShort();
            if (layer != LayerManager.CurrentPlayerLayer && layer >= 0)
            {
                LayerManager.CurrentPlayerLayer = layer;
            }
            bool unknown = false;
            for (int i = 0; i < n; i++)
            {
                int id = r.GetInt();
                float x = r.GetFloat();
                float y = r.GetFloat();
                float rot = r.GetFloat();
                byte flags = r.GetByte();
                Vector2 vel = Vector2.zero;
                if ((flags & 8) != 0)
                {
                    vel = new Vector2(r.GetFloat(), r.GetFloat());
                }
                byte hp = 255;
                if ((flags & 16) != 0)
                {
                    hp = r.GetByte();
                }
                if (id == NetPaths.SeatA && !seatPacket)
                {
                    seatPacket = true;
                    NetQuality.OnSeatPacket();
                }
                NetEntity e = id == NetPaths.SeatA ? SeatEntity(Seat.A) : id == NetPaths.SeatB ? SeatEntity(Seat.B) : (m_byId.TryGetValue(id, out var found) ? found : null);
                if (e == null && id != NetPaths.SeatA && id != NetPaths.SeatB && !m_unresolved.Contains(id))
                {
                    unknown = true;
                }
                bool alive = e != null && e.Alive;
                if ((flags & 4) != 0)
                {
                    NetAnim.Read(r, alive ? e.Anim : null, alive ? e : m_dummy);
                }
                if (!alive)
                {
                    continue;
                }
                if (e.Attached)
                {
                    bool attachedActive = (flags & 2) != 0;
                    if (e.Go.activeSelf != attachedActive)
                    {
                        e.Go.SetActive(attachedActive);
                    }
                    continue; // висит на кости марионетки
                }
                e.TargetPos = new Vector3(x, y, e.T.position.z);
                e.TargetRot = rot;
                e.TargetFlip = (flags & 1) != 0;
                e.HasTarget = true;
                e.Vel = vel;
                e.SnapTime = Time.unscaledTime;
                if (id == NetPaths.SeatB)
                {
                    Reconcile(ackSeq, x);
                }
                if (CoopConfig.NetInterp.Value)
                {
                    (e.Interp ??= new InterpBuffer()).Add(hostTime, e.TargetPos, rot, e.TargetFlip, vel,
                        e.Kind == EntityKind.Seat ? NetConst.SeatSnapshotInterval : NetConst.SnapshotInterval);
                }
                if ((flags & 16) != 0 && hp != e.HealthByte)
                {
                    e.HealthByte = hp;
                    ApplyHealthVisual(e, hp);
                }
                if ((flags & 64) != 0 && e.Audio != null)
                {
                    bool playing = (flags & 32) != 0;
                    if (playing && !e.Audio.isPlaying)
                    {
                        e.Audio.Play();
                    }
                    else if (!playing && e.Audio.isPlaying)
                    {
                        e.Audio.Stop();
                    }
                }
                bool active = (flags & 2) != 0;
                if (e.Kind == EntityKind.Seat)
                {
                    // персонаж ждёт у двери (у хоста спрятан) — прячем графику и здесь
                    if (e.Character != null && CoopVisibility.IsHidden(e.Character) == active)
                    {
                        CoopVisibility.SetHidden(e.Character, !active);
                        if (active)
                        {
                            ApplyLook(id == NetPaths.SeatA ? 0 : 1);
                        }
                    }
                }
                else if (e.Go.activeSelf != active)
                {
                    e.Go.SetActive(active);
                }
            }
            // хост шлёт объекты, о которых мы не знаем (потерялась регистрация) — дольше 1.5 с просим комнату целиком
            if (!unknown || m_awaitingScene || m_pendingRoom != null)
            {
                m_unknownSince = -1f;
            }
            else if (m_unknownSince < 0f)
            {
                m_unknownSince = Time.unscaledTime;
            }
            else if (Time.unscaledTime - m_unknownSince > 1.5f)
            {
                m_unknownSince = -1f;
                RequestResync("unknown entities in snapshot");
            }
        }

        private void OnDeath(int id)
        {
            Character c = null;
            int tag = 0;
            if (id == NetPaths.SeatA || id == NetPaths.SeatB)
            {
                c = SeatPuppet(id == NetPaths.SeatA ? Seat.A : Seat.B);
                tag = m_seatTag[id == NetPaths.SeatA ? 0 : 1];
            }
            else if (m_byId.TryGetValue(id, out var e) && e.Alive)
            {
                c = e.Character;
            }
            if (c == null || c.Stats.Dead)
            {
                return;
            }
            KillPuppet(c, tag);
        }

        /// <summary>Смерть марионетки по событию хоста; тело остаётся на месте (физики у марионеток нет).</summary>
        internal static void KillPuppet(Character c, int hostTag = 0)
        {
            var pos = c.transform.position;
            // труп игра собирает событием анимации и физикой — на время смерти возвращаем их марионетке
            var anim = c.Components.Animator != null ? c.Components.Animator.AnimatorComponent : null;
            if (anim != null)
            {
                anim.fireEvents = true;
            }
            if (c.Components.Rigidbody != null)
            {
                c.Components.Rigidbody.simulated = true;
            }
            ApplyingDeath = true;
            try
            {
                c.Actions.Die();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[NetClient] die {c.name}: {ex.Message}");
            }
            finally
            {
                ApplyingDeath = false;
            }
            if (hostTag != 0)
            {
                s_corpseByTag[hostTag] = c.gameObject;
            }
            NetSession.Instance.StartCoroutine(CorpseWatch(c.gameObject, pos));
        }

        /// <summary>Если тело провалилось сквозь пол — возвращаем на место смерти и замораживаем.</summary>
        private static IEnumerator CorpseWatch(GameObject go, Vector3 pos)
        {
            float t = 0f;
            while (t < 2f)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            if (go != null && !s_corpseMapped.Contains(go) && go.transform.position.y < pos.y - 3f)
            {
                Plugin.Log.LogInfo($"[NetClient] corpse {go.name} fell through — restoring");
                FreezeBody(go, pos);
            }
        }

        private static void FreezeBody(GameObject go, Vector3 pos)
        {
            if (go == null)
            {
                return;
            }
            foreach (var rb in go.GetComponentsInChildren<Rigidbody2D>(true))
            {
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.bodyType = RigidbodyType2D.Kinematic;
            }
            go.transform.position = pos;
        }

        private int m_despawns;

        private void OnDespawn(int id)
        {
            m_despawns++;
            m_unresolved.Remove(id);
            if (m_byId.TryGetValue(id, out var e))
            {
                m_byId.Remove(id);
                if (e.Alive)
                {
                    Object.Destroy(e.Go);
                }
            }
        }

        // ================= игроки =================

        /// <summary>Марионетки обоих мест: свой персонаж — GameManager.Player, персонаж хоста — второй.</summary>
        private void EnsureSeatPuppets(Vector3? hostPos)
        {
            var main = CoopPlayers.P1;
            if (CoopPlayers.P2 == null && main != null)
            {
                CoopPlayers.SpawnP2(hostPos ?? main.transform.position + Vector3.right * 0.8f);
            }
            foreach (var p in new[] { CoopPlayers.P1, CoopPlayers.P2 })
            {
                if (p == null)
                {
                    continue;
                }
                if (p.Stats.InputAllowed)
                {
                    p.Stats.InputAllowed = false; // игра пишет стек вызовов на каждый вызов сеттера — только при смене
                }
                if (p.Components.Rigidbody != null && p.Components.Rigidbody.simulated && !p.Stats.Dead)
                {
                    p.Components.Rigidbody.simulated = false;
                }
                var a = p.Components.Animator != null ? p.Components.Animator.AnimatorComponent : null;
                if (a != null)
                {
                    a.fireEvents = false;
                }
            }
        }

        private void OnPlayerState(NetDataReader r)
        {
            var seat = (Seat)r.GetByte();
            int tag = r.GetInt();
            bool dead = r.GetBool();
            string armor = r.GetString();
            string helmet = r.GetString();
            bool sack = r.GetBool();
            int gold = r.GetInt();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
            int nTeeth = r.GetByte();
            var teeth = new List<WanstTooth>(nTeeth);
            for (int t = 0; t < nTeeth; t++)
            {
                var color = (WanstTooth.ToothColor)r.GetByte();
                int position = r.GetByte();
                int maxHits = r.GetByte();
                int hits = r.GetByte();
                teeth.Add(new WanstTooth(color, position, maxHits) { CurrentHits = hits });
            }
            var form = (LocaDatabase.Characters)r.GetByte();
            int i = (int)seat;
            CoopGold.SeatTotals[i] = r.GetInt();

            var puppet = SeatPuppet(seat);
            // у хоста персонаж этого места сменился (воскрешение, возвращение после обрыва):
            // прежний у нас становится трупом, для живого нового — новая марионетка
            if (m_seatKnown[i] && tag != m_seatTag[i])
            {
                if (puppet != null && !puppet.Stats.Dead)
                {
                    KillPuppet(puppet, m_seatTag[i]);
                }
                if (!dead)
                {
                    puppet = RespawnPuppet(seat, pos, form);
                }
                m_seatVisual[i] = null;
            }
            m_seatKnown[i] = true;
            m_seatTag[i] = tag;
            if (puppet == null || dead)
            {
                return;
            }

            // ХП (зубы): дырки в улыбке как у хоста
            string teethKey = string.Join(",", teeth.Select(t => $"{(int)t.Color}:{t.Position}:{t.CurrentHits}"));
            if (teethKey != m_seatTeeth[i])
            {
                m_seatTeeth[i] = teethKey;
                try
                {
                    puppet.Stats.TeethInventory = teeth;
                    puppet.Stats.ColorInTeeth();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[NetClient] teeth: {e.Message}");
                }
            }

            string visual = $"{armor}|{helmet}|{sack}|{gold}";
            if (visual == m_seatVisual[i])
            {
                return;
            }
            // первая установка (новая марионетка): облик применяем принудительно — у клона шаблона броня «числится»,
            // но анимации волос/трусов не подключены, и персонаж выглядит лысым и голым
            bool first = m_seatVisual[i] == null;
            m_seatVisual[i] = visual;
            try
            {
                var anim = puppet.Components.Animator;
                var armorSo = FindAsset<ArmorItem>(armor);
                if (armorSo != null && (first || puppet.Stats.CurrentArmor != armorSo))
                {
                    puppet.Stats.CurrentArmor = armorSo;
                    anim.SwitchArmorAnimation(armorSo, false);
                }
                var helmetSo = FindAsset<HelmetItem>(helmet);
                if (helmetSo != null && (first || puppet.Stats.CurrentHelmet != helmetSo))
                {
                    puppet.Stats.CurrentHelmet = helmetSo;
                    anim.SwitchHelmetAnimation(helmetSo, false);
                }
                if (sack)
                {
                    anim.ActivateGoldSack();
                    anim.ChangeGoldSackSprite(gold);
                }
                else
                {
                    anim.DeactivateGoldSack();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetClient] equipment visuals: {e.Message}");
            }
        }

        private GamePlayer RespawnPuppet(Seat seat, Vector3 pos, LocaDatabase.Characters form = LocaDatabase.Characters.Wanst)
        {
            Plugin.Log.LogInfo($"[NetClient] seat {seat} respawned on host — new puppet");
            if (seat == Seat.A)
            {
                CoopPlayers.DetachSecondAsCorpse();
                var p = CoopPlayers.SpawnP2(pos, PrefabForForm(form));
                EnsureSeatPuppets(null);
                return p;
            }
            // своё место: новый персонаж становится главным, а персонаж хоста остаётся вторым
            var hostPuppet = CoopPlayers.P2;
            CoopPlayers.DetachSecondAsCorpse();
            var mine = CoopPlayers.SpawnP2(pos, PrefabForForm(form));
            if (mine == null)
            {
                return null;
            }
            CoopPlayers.PromoteSecondToMain();
            if (hostPuppet != null)
            {
                CoopPlayers.AdoptAsSecond(hostPuppet);
            }
            EnsureSeatPuppets(null);
            return mine;
        }

        private static readonly Dictionary<LocaDatabase.Characters, GamePlayer> s_formPrefabs = new Dictionary<LocaDatabase.Characters, GamePlayer>();

        /// <summary>Префаб игрока для формы тела (монстр после зелья, Эвелиус…); null — обычный Ванст.</summary>
        private static GamePlayer PrefabForForm(LocaDatabase.Characters form)
        {
            if (form == LocaDatabase.Characters.Wanst || form == LocaDatabase.Characters.None)
            {
                return null;
            }
            if (s_formPrefabs.TryGetValue(form, out var cached) && cached != null)
            {
                return cached;
            }
            GamePlayer found = null;
            foreach (var p in Resources.FindObjectsOfTypeAll<GamePlayer>())
            {
                if (!p.gameObject.scene.IsValid() && p.transform.parent == null && p.CharacterName == form)
                {
                    found = p;
                    break;
                }
            }
            if (found != null)
            {
                s_formPrefabs[form] = found;
                Plugin.Log.LogInfo($"[NetClient] body prefab for {form}: {found.name}");
            }
            else
            {
                Plugin.Log.LogWarning($"[NetClient] no player prefab for form {form} — using Wanst");
            }
            return found;
        }
        private static readonly Dictionary<string, Object> s_assets = new Dictionary<string, Object>();

        private static T FindAsset<T>(string name) where T : Object
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            string key = typeof(T).Name + ":" + name;
            if (s_assets.TryGetValue(key, out var cached) && cached != null)
            {
                return (T)cached;
            }
            var found = Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(a => a.name == name);
            if (found != null)
            {
                s_assets[key] = found;
            }
            return found;
        }

        private void OnUi(NetDataReader r)
        {
            ReviveKills = r.GetInt();
            ReviveNeed = r.GetInt();
            ReviveOnFloor = r.GetBool();
            int n = r.GetByte();
            HostLabels.Clear();
            for (int i = 0; i < n; i++)
            {
                HostLabels.Add((new Vector3(r.GetFloat(), r.GetFloat(), 0f), r.GetString()));
            }
        }

        private void OnGameOver()
        {
            var gm = GameManager.InstanceNoWarnings;
            if (gm == null)
            {
                return;
            }
            ApplyingGameOver = true;
            try
            {
                gm.ShowDeathUI(1f, true);
            }
            finally
            {
                ApplyingGameOver = false;
            }
        }

        // ================= кадр =================

        internal void Tick(bool connected)
        {
            var gm = GameManager.InstanceNoWarnings;
            if (m_awaitingScene && connected && CoopPlayers.IsInitialized(gm) && gm.Player != null && SceneManager.GetActiveScene().name == m_targetScene)
            {
                m_awaitingScene = false;
                EnsureSeatPuppets(null);
                SendSceneReady();
                Plugin.Log.LogInfo($"[NetClient] scene ready: {m_targetScene}");
            }
            if (connected && !m_awaitingScene && CoopPlayers.IsInitialized(gm) && m_pendingRoom != null && !m_ensuringSegments)
            {
                var pending = m_pendingRoom;
                var room = FindRoom(pending.Name, pending.Segment);
                if (room != null)
                {
                    m_pendingRoom = null;
                    ApplyRoom(room, pending);
                }
                else
                {
                    m_s.StartCoroutine(EnsureSegments(pending.Segment));
                }
            }
            if (connected && !m_awaitingScene && CoopPlayers.IsInitialized(gm))
            {
                m_sender.Update(m_s);
                UpdateCameraZones(gm);
                RetryAttachments();
                RetryPendingRegistrations();
                CheckOrphanEvelius(gm.CurrentRoom);
                UpdateViewTarget(gm);
            }
            Interpolate();
        }

        // ---------- обзор: свой персонаж погиб — камера, туман и свет игры идут за напарником ----------

        private void UpdateViewTarget(GameManager gm)
        {
            var own = CoopPlayers.P1;
            var partner = CoopPlayers.P2;
            var desired = own;
            if (own != null && own.Stats.Dead && partner != null && !partner.Stats.Dead && partner.isActiveAndEnabled)
            {
                desired = partner;
            }
            if (desired != null && Patches.TargetSwap.CurrentOf(gm) != desired)
            {
                Patches.TargetSwap.SetCurrent(gm, desired);
                Plugin.Log.LogInfo($"[NetClient] view target -> {(desired == own ? "own character" : "partner")}");
            }
        }

        // ---------- зоны камеры: у марионеток нет физики, триггеры проверяем сами по своему персонажу ----------

        private static readonly System.Reflection.MethodInfo s_zoneEnter = AccessTools.Method(typeof(SnapCameraToCollider), "OnTriggerEnter2D");
        private static readonly System.Reflection.MethodInfo s_zoneExit = AccessTools.Method(typeof(SnapCameraToCollider), "OnTriggerExit2D");
        private readonly HashSet<SnapCameraToCollider> m_zonesInside = new HashSet<SnapCameraToCollider>();
        private Room m_zonesRoom;
        private SnapCameraToCollider[] m_zones = new SnapCameraToCollider[0];
        private float m_nextZoneCheck;

        private void UpdateCameraZones(GameManager gm)
        {
            if (Time.unscaledTime < m_nextZoneCheck)
            {
                return;
            }
            m_nextZoneCheck = Time.unscaledTime + 0.15f;
            var own = CoopPlayers.P1;
            if (own == null || own.Components.Collider == null || gm.CurrentRoom == null)
            {
                return;
            }
            if (m_zonesRoom != gm.CurrentRoom)
            {
                m_zonesRoom = gm.CurrentRoom;
                m_zones = Object.FindObjectsByType<SnapCameraToCollider>(FindObjectsSortMode.None);
                m_zonesInside.Clear();
            }
            var point = (Vector2)own.Components.Collider.bounds.center;
            foreach (var zone in m_zones)
            {
                if (zone == null || !zone.isActiveAndEnabled)
                {
                    continue;
                }
                var col = zone.GetComponent<Collider2D>();
                bool inside = col != null && col.enabled && col.OverlapPoint(point);
                bool was = m_zonesInside.Contains(zone);
                if (inside == was)
                {
                    continue;
                }
                try
                {
                    if (inside)
                    {
                        m_zonesInside.Add(zone);
                        s_zoneEnter.Invoke(zone, new object[] { own.Components.Collider });
                    }
                    else
                    {
                        m_zonesInside.Remove(zone);
                        s_zoneExit.Invoke(zone, new object[] { own.Components.Collider });
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[NetClient] camera zone: {e.InnerException?.Message ?? e.Message}");
                }
            }
        }

        private void Interpolate()
        {
            float dt = Time.unscaledDeltaTime;
            bool buffered = CoopConfig.NetInterp.Value && m_clock.Ready;
            float rtSeat = 0f, rtWorld = 0f;
            if (buffered)
            {
                // показываем мир в прошлом на «интервал снапшотов + джиттер»: тогда почти всегда есть два снапшота вокруг
                // момента показа. Задержка меняется плавно, чтобы время показа не прыгало.
                float jit = Mathf.Min(m_clock.Jitter, 0.25f);
                m_delaySeat = NetConst.SeatSnapshotInterval + jit + 0.008f;
                m_delayWorld = NetConst.SnapshotInterval + jit + 0.012f;
                rtSeat = m_trackSeat.Time(m_clock, m_delaySeat, dt);
                rtWorld = m_trackWorld.Time(m_clock, m_delayWorld, dt);
            }
            float k = 1f - Mathf.Exp(-15f * dt);
            foreach (var e in m_byId.Values)
            {
                if (!buffered || e.Interp == null || !ApplyBuffered(e, rtWorld, 0.1f, out _, out _))
                {
                    Apply(e, k);
                }
            }
            // персонажи игроков: снапшоты 60 Гц
            float kSeat = 1f - Mathf.Exp(-30f * dt);
            for (int i = 0; i < 2; i++)
            {
                var e = m_seatEntity[i];
                if (e == null)
                {
                    continue;
                }
                if (i == (int)Seat.B)
                {
                    // свой персонаж: минимальная задержка — предсказание или последний снапшот (буфер тут только мешал бы)
                    if (!(CoopConfig.NetPredict.Value && PredictOwn(e, kSeat)))
                    {
                        Apply(e, kSeat);
                    }
                    continue;
                }
                // напарник
                if (buffered && e.Interp != null && ApplyBuffered(e, rtSeat, 0.1f, out bool extrapolated, out var shownVel))
                {
                    NetQuality.OnPartnerFrame(e.T.position, shownVel, dt, extrapolated);
                }
                else
                {
                    Apply(e, kSeat);
                    NetQuality.OnPartnerFrame(e.T.position, e.Vel, dt, false);
                }
            }
        }

        /// <summary>Поза из буфера интерполяции в момент времени хоста rt. false — буфер пуст.</summary>
        private static bool ApplyBuffered(NetEntity e, float rt, float maxExtrapolate, out bool extrapolated, out Vector2 velocity)
        {
            extrapolated = false;
            velocity = Vector2.zero;
            if (!e.HasTarget || !e.Alive || e.Attached)
            {
                return true; // нечего двигать — и старому пути тоже
            }
            FreezeCorpse(e);
            if (!e.Interp.Sample(rt, maxExtrapolate, out var pos, out var rot, out var flip, out extrapolated, out velocity))
            {
                return false;
            }
            var t = e.T;
            pos.z = t.position.z;
            t.position = pos;
            var euler = t.eulerAngles;
            if (Mathf.Abs(Mathf.DeltaAngle(euler.z, rot)) > 0.01f)
            {
                euler.z = rot;
                t.eulerAngles = euler;
            }
            var scale = t.localScale;
            if ((scale.x < 0f) != flip)
            {
                scale.x = -scale.x;
                t.localScale = scale;
            }
            return true;
        }

        // ---------- предсказание: свой персонаж идёт по нажатию сразу, хост догоняет ----------

        private static readonly int[] s_predMask = new int[1];

        // сверка предсказания: где мы показывали своего персонажа, когда отправили нажатие №N, и где он у хоста,
        // когда хост это нажатие применил. Разница — настоящая ошибка предсказания (без отставания на пинг).
        private readonly ushort[] m_predSeq = new ushort[64];
        private readonly float[] m_predX = new float[64];
        private readonly bool[] m_predValid = new bool[64];
        private float m_predShift;
        private float m_predActiveAt = -10f;
        private float m_lastMoveInputAt = -10f;

        private void Reconcile(ushort ack, float hostX)
        {
            if (!CoopConfig.NetPredict.Value || Time.unscaledTime - m_predActiveAt > 0.2f)
            {
                return;
            }
            int i = ack & 63;
            if (!m_predValid[i] || m_predSeq[i] != ack)
            {
                return;
            }
            m_predValid[i] = false; // одно нажатие сверяем один раз
            float err = hostX - m_predX[i];
            NetQuality.OnReconcile(err);
            float shift = Mathf.Abs(err) > 1.5f ? err : err * 0.2f; // мелкое — плавно, большое (толчок, телепорт) — сразу
            if (Mathf.Abs(err) > 1.5f)
            {
                NetQuality.OnCorrectionSnap();
            }
            if (Mathf.Abs(shift) < 0.0005f)
            {
                return;
            }
            m_predShift += shift;
            for (int k = 0; k < 64; k++)
            {
                if (m_predValid[k] && (short)(m_predSeq[k] - ack) > 0)
                {
                    m_predX[k] += shift; // более поздние предсказания уже учли бы эту поправку
                }
            }
        }

        /// <summary>Горизонтальное движение своего персонажа — от локального ввода; вертикаль и всё остальное — от хоста.</summary>
        private bool PredictOwn(NetEntity e, float k)
        {
            var own = e.Character as GamePlayer;
            if (own == null || !e.HasTarget || !e.Alive || own.Stats.Dead || CoopVisibility.IsHidden(own))
            {
                return false;
            }
            var gm = GameManager.InstanceNoWarnings;
            if (gm == null || gm.InEnterDoor || (own.Components.InteractionWindow != null && own.Components.InteractionWindow.IsOpen))
            {
                return false;
            }
            var input = CoopInput.P1Input;
            if (input == null)
            {
                return false;
            }
            float dir = input.GetButton(23) ? 1f : input.GetButton(2) ? -1f : 0f;
            float dt = Time.unscaledDeltaTime;
            var t = e.T;
            var pos = t.position;
            if (Time.unscaledTime - m_predActiveAt > 0.2f)
            {
                // предсказание только что включилось (после двери, смерти, окна) — старая история не годится
                System.Array.Clear(m_predValid, 0, m_predValid.Length);
                m_predShift = 0f;
            }
            m_predActiveAt = Time.unscaledTime;
            pos.x += m_predShift;
            m_predShift = 0f;
            var host = e.TargetPos + (Vector3)(e.Vel * Mathf.Min(Time.unscaledTime - e.SnapTime, 0.1f));
            float speed = Mathf.Abs(e.Vel.x) > 0.5f ? Mathf.Abs(e.Vel.x) : own.Stats.MovementSpeed * 2f * own.Stats.MovementSpeedMultiplier;
            float rtt = Mathf.Clamp(m_sender.AppliedLagMs / 1000f, 0.03f, 1f);
            // хост показывает нас с отставанием на пинг: допустимое расхождение растёт с пингом
            float allowed = speed * (rtt + 0.2f) + 1.5f;
            if ((host - pos).sqrMagnitude > allowed * allowed)
            {
                NetQuality.OnCorrectionSnap();
                t.position = host; // разошлись сильнее, чем объясняет пинг (телепорт, дверь) — верим хосту
                System.Array.Clear(m_predValid, 0, m_predValid.Length);
                return true;
            }
            if (dir != 0f)
            {
                m_lastMoveInputAt = Time.unscaledTime;
            }
            if (dir != 0f && speed > 0.1f)
            {
                float step = dir * speed * dt;
                if (!Blocked(own, dir, Mathf.Abs(step) + 0.05f))
                {
                    pos.x += step;
                }
                float lead = pos.x - host.x;
                float maxLead = speed * (rtt + 0.1f) + 0.3f;
                if (Mathf.Abs(lead) > maxLead)
                {
                    pos.x = host.x + Mathf.Sign(lead) * maxLead;
                }
                var scale = t.localScale;
                if ((scale.x < 0f) != (dir < 0f))
                {
                    scale.x = -scale.x;
                    t.localScale = scale;
                }
            }
            else
            {
                // отпустили: хост ещё идёт пинг-времени и остановится там, где мы уже стоим. Сверка подправит;
                // к хосту тянемся, только когда он точно остановился
                if (Time.unscaledTime - m_lastMoveInputAt > rtt + 0.3f)
                {
                    pos.x = Mathf.Lerp(pos.x, host.x, k);
                }
                var scale = t.localScale;
                if ((scale.x < 0f) != e.TargetFlip && Time.unscaledTime - m_lastMoveInputAt > rtt + 0.1f)
                {
                    scale.x = -scale.x;
                    t.localScale = scale;
                }
            }
            pos.y = Mathf.Lerp(pos.y, host.y, k);
            t.position = pos;
            ushort sent = m_sender.LastSentSeq;
            m_predSeq[sent & 63] = sent;
            m_predX[sent & 63] = pos.x;
            m_predValid[sent & 63] = true;
            var euler = t.eulerAngles;
            if (Mathf.Abs(Mathf.DeltaAngle(euler.z, e.TargetRot)) > 0.1f)
            {
                euler.z = Mathf.LerpAngle(euler.z, e.TargetRot, k);
                t.eulerAngles = euler;
            }
            return true;
        }

        /// <summary>Стена/край впереди (по слоям столкновений персонажа) — не предсказываем шаг в неё.</summary>
        private static bool Blocked(GamePlayer own, float dir, float distance)
        {
            var col = own.Components != null ? own.Components.Collider as BoxCollider2D : null;
            if (col == null)
            {
                return false;
            }
            var b = col.bounds;
            var size = new Vector2(b.size.x * 0.9f, b.size.y * 0.8f);
            int mask = Physics2D.GetLayerCollisionMask(own.gameObject.layer) & ~(1 << own.gameObject.layer);
            var hit = Physics2D.BoxCast(b.center, size, 0f, new Vector2(dir, 0f), distance, mask);
            return hit.collider != null && !hit.collider.isTrigger && hit.collider.GetComponentInParent<Character>() == null;
        }

        /// <summary>Задержка «мой ввод → применён хостом → снапшот у меня», мс (0 — ещё не измерено).</summary>
        public float InputLagMs => m_sender.AppliedLagMs;

        private static void FreezeCorpse(NetEntity e)
        {
            if (!e.CorpseFrozen && e.Kind == EntityKind.Character && e.Character != null && e.Character.Stats.Dead)
            {
                // тело врага повторяет хоста: своя физика трупа у клиента уводила его в сторону
                e.CorpseFrozen = true;
                foreach (var rb in e.Go.GetComponentsInChildren<Rigidbody2D>(true))
                {
                    rb.simulated = false;
                }
            }
        }

        private static void Apply(NetEntity e, float k)
        {
            if (!e.HasTarget || !e.Alive || e.Attached)
            {
                return;
            }
            FreezeCorpse(e);
            var t = e.T;
            var pos = t.position;
            var target = e.TargetPos;
            if (e.Kind == EntityKind.Seat && e.Vel.sqrMagnitude > 0.01f)
            {
                float age = Mathf.Min(Time.unscaledTime - e.SnapTime, 0.1f); // не дальше 100 мс: иначе уедем в стену
                target += (Vector3)(e.Vel * age);
            }
            t.position = (target - pos).sqrMagnitude > 9f ? target : Vector3.Lerp(pos, target, k);
            var euler = t.eulerAngles;
            if (Mathf.Abs(Mathf.DeltaAngle(euler.z, e.TargetRot)) > 0.1f)
            {
                euler.z = Mathf.LerpAngle(euler.z, e.TargetRot, k);
                t.eulerAngles = euler;
            }
            var scale = t.localScale;
            if ((scale.x < 0f) != e.TargetFlip)
            {
                scale.x = -scale.x;
                t.localScale = scale;
            }
        }
        /// <summary>Отладка: «забыть» N сущностей (как будто регистрация потерялась) — проверка автоматической ресинхронизации.</summary>
        internal string DebugDesync(int n)
        {
            var ids = new List<int>(m_byId.Keys);
            int removed = 0;
            for (int i = 0; i < ids.Count && removed < n; i++)
            {
                m_byId.Remove(ids[i]);
                removed++;
            }
            return $"forgot {removed} entities ({m_byId.Count} left)";
        }

        internal NetEntity EntityById(int id) => m_byId.TryGetValue(id, out var e) ? e : null;

        internal string DebugList()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# known={m_byId.Count} unresolved={m_unresolved.Count} pendingRegs={m_pendingRegs.Count} despawns={m_despawns} batches={m_registerBatches} pendingRoom={(m_pendingRoom != null ? m_pendingRoom.Name : "-")} ensuring={m_ensuringSegments}");
            foreach (var e in m_byId.Values)
            {
                sb.AppendLine($"{e.Id,11} {e.Kind,-9} {(e.Alive ? e.Go.name : "<destroyed>"),-34} pos={(e.Alive ? e.T.position.ToString("F2") : "-")} active={(e.Alive && e.Go.activeInHierarchy)}{(e.HasTarget ? $" target={e.TargetPos:F2}" : "")}{(e.Attached ? $" ATTACHED to {e.T.parent.name}" : e.AttachPending ? " attach-pending" : "")}");
            }
            return sb.Length > 0 ? sb.ToString() : "no entities";
        }
    }
}
