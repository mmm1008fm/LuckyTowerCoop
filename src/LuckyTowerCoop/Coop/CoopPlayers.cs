using System;
using System.Collections.Generic;
using HarmonyLib;
using LuckyTower;
using LuckyTower.CharacterSystem;
using LuckyTower.LevelLayers;
using UnityEngine;
using GamePlayer = LuckyTower.CharacterSystem.Player;
using Object = UnityEngine.Object;

namespace LuckyTowerCoop.Coop
{
    /// <summary>Marker of the second character (added before OnEnable).</summary>
    public class CoopPlayerTag : MonoBehaviour
    {
    }

    /// <summary>
    /// A player "seat" = a set of devices. A - Rewired Player0 (keyboard/gamepads of P1), B - the Rewired system player (P2 gamepad).
    /// The game's main character (GameManager.Player) can belong to either seat: after the main one dies
    /// the second becomes the main one, and the revived one appears as the second with the dead one's devices.
    /// </summary>
    internal enum Seat
    {
        A = 0,
        B = 1,
    }

    /// <summary>Registry of co-op characters: main (P1 = GameManager.Player) and second (P2 = ours), spawning, promotion to main.</summary>
    internal static class CoopPlayers
    {
        private static readonly AccessTools.FieldRef<GameManager, GamePlayer> s_playerPrefab =
            AccessTools.FieldRefAccess<GameManager, GamePlayer>("m_playerPrefab");

        private static readonly AccessTools.FieldRef<GameManager, bool> s_gmInitialized =
            AccessTools.FieldRefAccess<GameManager, bool>("m_initialized");

        private static readonly AccessTools.FieldRef<CharacterStats, CommandPattern> s_commandPattern =
            AccessTools.FieldRefAccess<CharacterStats, CommandPattern>("commandPattern");

        private static readonly Action<GamePlayer, Rewired.Player> s_setPlayerInput =
            AccessTools.MethodDelegate<Action<GamePlayer, Rewired.Player>>(AccessTools.PropertySetter(typeof(GamePlayer), nameof(GamePlayer.PlayerInput)));

        private static readonly System.Reflection.MethodInfo s_gmUpdatePlayer = AccessTools.Method(typeof(GameManager), "UpdatePlayer");

        private static readonly Dictionary<CommandPattern, GamePlayer> s_patternOwners = new Dictionary<CommandPattern, GamePlayer>();

        /// <summary>Grows on any change to the set of players (death, promotion, spawn).</summary>
        public static int Generation { get; private set; }

        /// <summary>The seat that controls the main character.</summary>
        public static Seat MainSeat { get; private set; } = Seat.A;

        public static Seat SecondSeat => MainSeat == Seat.A ? Seat.B : Seat.A;

        /// <summary>The seat's character (A - host/player 1, B - client/player 2); null if that character is absent right now (dead).</summary>
        public static GamePlayer CharOfSeat(Seat seat) => seat == MainSeat ? P1 : P2;

        public static Rewired.Player InputOf(Seat seat) => seat == Seat.A ? CoopInput.P1Input : CoopInput.P2Input;

        /// <summary>The second character (ours).</summary>
        public static GamePlayer P2 { get; private set; }

        private static GamePlayer s_main;

        /// <summary>
        /// The game's main character. Stored separately from GameManager.Player because during an AI update
        /// we temporarily swap GameManager.Player for the enemy's target.
        /// </summary>
        public static GamePlayer P1
        {
            get
            {
                if (s_main != null && s_main != P2)
                {
                    return s_main;
                }
                var gm = GameManager.InstanceNoWarnings;
                var p = gm != null ? gm.Player : null;
                return p != null && p != P2 ? p : null;
            }
        }

        /// <summary>Pick up the game's own change of main character (outside AI swaps).</summary>
        public static void SyncMainFromGame()
        {
            if (Patches.TargetSwap.InSwap)
            {
                return;
            }
            var gm = GameManager.InstanceNoWarnings;
            var p = gm != null ? gm.Player : null;
            if (p != null && p != s_main && p != P2)
            {
                s_main = p;
            }
        }

        public static bool IsInitialized(GameManager gm) => gm != null && s_gmInitialized(gm);

        public static bool IsCoopPlayer(Character c) => c != null && (c == P1 || c == P2);

        public static bool IsP2Collider(Collider2D collider)
        {
            if (P2 == null || collider == null)
            {
                return false;
            }
            var rb = collider.attachedRigidbody;
            var go = rb != null ? rb.gameObject : collider.gameObject;
            return go == P2.gameObject || collider.GetComponentInParent<GamePlayer>() == P2;
        }

        /// <summary>P2 exists, is alive and is in the world (not hidden in a door).</summary>
        public static bool IsP2ActiveInWorld => P2 != null && P2.isActiveAndEnabled && !P2.Stats.Dead && !CoopTransitions.IsWaiting(P2);

        public static bool TryGetPatternOwner(CommandPattern pattern, out GamePlayer owner)
        {
            owner = null;
            return s_patternOwners.Count > 0 && s_patternOwners.TryGetValue(pattern, out owner) && owner != null;
        }

        /// <summary>The game created a new main character (new run, respawn bottle) - seat A controls it again.</summary>
        public static void OnGameSpawnedMain()
        {
            if (MainSeat != Seat.A)
            {
                Plugin.Log.LogInfo("[Coop] game spawned a new main player - seat A controls main again");
            }
            MainSeat = Seat.A;
            Generation++;
            var gm = GameManager.InstanceNoWarnings;
            if (gm != null && gm.Player != null)
            {
                s_main = gm.Player;
            }
        }

        public static GamePlayer SpawnP2(Vector3 position, GamePlayer prefabOverride = null)
        {
            var gm = GameManager.Instance;
            var prefab = prefabOverride != null ? prefabOverride : (gm != null ? s_playerPrefab(gm) : null);
            if (prefab == null)
            {
                Plugin.Log.LogError("[Coop] m_playerPrefab not found - second player not created");
                return null;
            }

            if (P2 != null)
            {
                Despawn();
            }

            // Create the clone disabled so it can be marked and given its own CommandPattern before Awake/OnEnable
            // (the original is a shared ScriptableObject; Character.Initialize resets it).
            var prefabGo = prefab.gameObject;
            bool wasActive = prefabGo.activeSelf;
            prefabGo.SetActive(false);
            GamePlayer p2;
            try
            {
                p2 = Object.Instantiate(prefab, position, Quaternion.identity);
            }
            finally
            {
                prefabGo.SetActive(wasActive);
            }

            p2.gameObject.name = $"{(prefabOverride != null ? prefabOverride.name : "VonWanst")} (seat {SecondSeat})";
            p2.gameObject.AddComponent<CoopPlayerTag>();

            var pattern = s_commandPattern(p2.Stats);
            if (pattern != null)
            {
                var clone = Object.Instantiate(pattern);
                clone.name = pattern.name + " (coop)";
                s_commandPattern(p2.Stats) = clone;
                s_patternOwners[clone] = p2;
            }
            else
            {
                Plugin.Log.LogWarning("[Coop] The prefab has no CommandPattern");
            }

            Generation++;
            P2 = p2;
            p2.gameObject.SetActive(true); // Awake -> Initialize, OnEnable -> RegisterAsP2

            int layer = LayerManager.CurrentPlayerLayer;
            p2.GameplayInit(); // resets the static CurrentPlayerLayer to 0
            LayerManager.CurrentPlayerLayer = layer;
            if (LayerManager.LayersReady && layer >= 0)
            {
                LayerManager.MoveToLayer(p2.gameObject, layer);
            }

            CoopLife.OnSecondSpawned();
            Plugin.Log.LogInfo($"[Coop] second player spawned at {position}, seat={SecondSeat}, rewired={(p2.PlayerInput != null ? p2.PlayerInput.id : -1)}, layer={layer}");
            return p2;
        }

        public static void Despawn()
        {
            if (P2 != null)
            {
                Object.Destroy(P2.gameObject);
                Plugin.Log.LogInfo("[Coop] second player despawned");
            }
            P2 = null;
        }

        /// <summary>The second died: the body stays lying there, it is no longer P2.</summary>
        public static void DetachSecondAsCorpse()
        {
            if (P2 == null)
            {
                return;
            }
            Generation++;
            var corpse = P2;
            P2 = null;
            var tag = corpse.GetComponent<CoopPlayerTag>();
            if (tag != null)
            {
                Object.Destroy(tag);
            }
            if (GamePlayer.VonWansts[1] == corpse)
            {
                GamePlayer.VonWansts[1] = null;
            }
        }

        /// <summary>The main one died, the second is alive - the second becomes the game's main character.</summary>
        public static void PromoteSecondToMain()
        {
            var gm = GameManager.Instance;
            var newMain = P2;
            var oldMain = gm.Player;
            if (newMain == null)
            {
                return;
            }

            Generation++;
            MainSeat = SecondSeat;
            P2 = null;
            var tag = newMain.GetComponent<CoopPlayerTag>();
            if (tag != null)
            {
                Object.Destroy(tag);
            }

            gm.Player = newMain;
            s_main = newMain;
            for (int i = 0; i < GamePlayer.VonWansts.Length; i++)
            {
                if (GamePlayer.VonWansts[i] == oldMain || GamePlayer.VonWansts[i] == newMain)
                {
                    GamePlayer.VonWansts[i] = null;
                }
            }
            GamePlayer.VonWansts[0] = newMain;
            newMain.Stats.PlayerNumber = 1;
            if (oldMain != null)
            {
                oldMain.Stats.PlayerNumber = 0;
            }

            var cam = CoopCamera.CameraManagerSilent;
            if (cam != null && cam.VirtualCamera != null)
            {
                cam.VirtualCamera.Follow = newMain.Components.CameraFocusPoint;
                cam.cinemachineCameraValues.FollowTarget = newMain.Components.CameraFocusPoint;
            }
            if (UberFogSystem.Instance != null)
            {
                UberFogSystem.Instance.PlayerOccluder = newMain.Components.UberFogPlayerOccluder;
            }
            try
            {
                s_gmUpdatePlayer.Invoke(gm, null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Coop] GameManager.UpdatePlayer failed: {e.InnerException?.Message ?? e.Message}");
            }

            Plugin.Log.LogInfo($"[Coop] main player died - '{newMain.name}' (seat {MainSeat}) is now the main player");
        }

        /// <summary>Make an already existing character the second (client: the host puppet after recreating their own).</summary>
        public static void AdoptAsSecond(GamePlayer p)
        {
            if (p == null)
            {
                return;
            }
            Generation++;
            P2 = p;
            if (p.GetComponent<CoopPlayerTag>() == null)
            {
                p.gameObject.AddComponent<CoopPlayerTag>();
            }
            RegisterAsP2(p);
        }

        /// <summary>Called from the Player.OnEnable postfix for the second character.</summary>
        public static void RegisterAsP2(GamePlayer p2)
        {
            var p1 = P1;
            for (int i = 0; i < GamePlayer.VonWansts.Length; i++)
            {
                if (GamePlayer.VonWansts[i] == p2)
                {
                    GamePlayer.VonWansts[i] = null;
                }
            }
            if (GamePlayer.VonWansts[0] == null && p1 != null)
            {
                GamePlayer.VonWansts[0] = p1;
            }
            GamePlayer.VonWansts[1] = p2;
            p2.Stats.PlayerNumber = 2;

            var input = InputOf(SecondSeat);
            if (input != null)
            {
                s_setPlayerInput(p2, input);
            }
        }

        /// <summary>Every frame: the game reassigns PlayerInput to Rewired player 0 on scene change etc.</summary>
        public static void Enforce()
        {
            SyncMainFromGame();
            var main = P1;
            if (main != null && MainSeat != Seat.A)
            {
                var mainInput = InputOf(MainSeat);
                if (mainInput != null && main.PlayerInput != mainInput)
                {
                    s_setPlayerInput(main, mainInput);
                }
            }

            if (P2 == null)
            {
                return;
            }
            var input = InputOf(SecondSeat);
            if (input != null && P2.PlayerInput != input)
            {
                s_setPlayerInput(P2, input);
            }
            if (P2.isActiveAndEnabled && !P2.Stats.Dead && GamePlayer.VonWansts[1] != P2)
            {
                RegisterAsP2(P2);
            }
        }

        public static void TeleportP2ToP1(string reason)
        {
            var p1 = P1;
            if (p1 == null || P2 == null)
            {
                return;
            }
            float side = p1.transform.localScale.x >= 0f ? -0.7f : 0.7f; // behind P1
            var pos = p1.transform.position + new Vector3(side, 0.05f, 0f);
            P2.Components.Rigidbody.linearVelocity = Vector2.zero;
            P2.transform.position = pos;
            if (LayerManager.LayersReady && LayerManager.CurrentPlayerLayer >= 0)
            {
                LayerManager.MoveToLayer(P2.gameObject, LayerManager.CurrentPlayerLayer);
            }
            Plugin.Log.LogInfo($"[Coop] P2 teleported to P1 ({reason}) -> {pos}");
        }

        /// <summary>The nearest living co-op character to a point (with hysteresis in favor of prefer).</summary>
        public static GamePlayer NearestAlive(Vector3 from, GamePlayer prefer = null)
        {
            var a = P1;
            var b = IsP2ActiveInWorld ? P2 : null;
            bool aOk = a != null && !a.Stats.Dead && !CoopTransitions.IsWaiting(a);
            bool bOk = b != null;
            if (!aOk)
            {
                return bOk ? b : a;
            }
            if (!bOk)
            {
                return a;
            }
            float da = Vector2.Distance(from, a.transform.position);
            float db = Vector2.Distance(from, b.transform.position);
            if (prefer == a)
            {
                da -= 1f;
            }
            else if (prefer == b)
            {
                db -= 1f;
            }
            return db < da ? b : a;
        }
    }
}
