using LuckyTower;
using LuckyTower.CharacterSystem;
using LuckyTower.Combat;
using UnityEngine;
using UnityEngine.SceneManagement;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Coop
{
    /// <summary>
    /// Death and revival: while the partner is alive, one death does not end the run.
    /// The fallen player revives when the living one kills KillsToRevive monsters (or on a new floor, if enabled).
    /// If both die - the game's normal game over.
    /// </summary>
    public class CoopLife : MonoBehaviour
    {
        private static bool s_someoneDead;
        private static int s_kills;
        private static int s_segmentAtDeath;
        private static int s_floorAtDeath;

        /// <summary>A player's death in a co-op context (partner alive): suppress game over and combat cleanup.</summary>
        public static Character DyingInCoop { get; private set; }

        public static bool SomeoneDead => s_someoneDead;

        public static int Kills => s_kills;

        private GUIStyle m_style;
        private int m_styleHeight;

        private void Start()
        {
            SceneManager.activeSceneChanged += (_, __) => s_someoneDead = false;
        }

        // ---------- calls from patches ----------

        /// <summary>Prefix of CharacterAction.Die. true - death in a co-op context.</summary>
        public static bool BeginDeath(Character dying)
        {
            if (Net.NetSession.IsClient || dying == null || dying.Stats.Dead || !(dying is GamePlayer player) || !CoopPlayers.IsCoopPlayer(player))
            {
                return false;
            }
            var partner = player == CoopPlayers.P1 ? (GamePlayer)CoopPlayers.P2 : CoopPlayers.P1;
            if (partner == null || partner.Stats.Dead || !partner.gameObject.activeInHierarchy)
            {
                return false; // no partner - a normal game death
            }
            DyingInCoop = dying;
            Plugin.Log.LogInfo($"[Life] '{dying.name}' dies, partner '{partner.name}' is alive - run continues");
            return true;
        }

        /// <summary>Postfix of CharacterAction.Die (the body has already been created).</summary>
        public static void EndDeath(Character dying)
        {
            if (DyingInCoop != dying)
            {
                return;
            }
            DyingInCoop = null;

            if (dying == CoopPlayers.P2)
            {
                CoopPlayers.DetachSecondAsCorpse();
            }
            else if (dying == CoopPlayers.P1)
            {
                CoopPlayers.PromoteSecondToMain();
            }

            s_someoneDead = true;
            s_kills = 0;
            var room = GameManager.InstanceNoWarnings?.CurrentRoom;
            s_segmentAtDeath = room != null ? room.SegmentID : -1;
            s_floorAtDeath = room != null ? room.FloorID : -1;
            Plugin.Log.LogInfo($"[Life] waiting for revive: {CoopConfig.KillsToRevive.Value} kills" + (CoopConfig.ReviveOnNewFloor.Value ? " or a new floor" : ""));
        }

        /// <summary>Postfix of AchievementManager.TrackDeath - kill counter.</summary>
        public static void OnCharacterKilled(Character victim, IDamageSource source)
        {
            if (!s_someoneDead || victim == null)
            {
                return;
            }
            if (victim.Stats.CharacterType != CharacterTypes.Enemy && !victim.Stats.Evil)
            {
                return;
            }
            var killer = source?.DamagingCharacter;
            if (killer == null || !CoopPlayers.IsCoopPlayer(killer))
            {
                return;
            }
            s_kills++;
            Plugin.Log.LogInfo($"[Life] kill {s_kills}/{CoopConfig.KillsToRevive.Value} ({victim.name})");
        }

        public static void DevAddKills(int n) => s_kills += n;

        /// <summary>Menu: revive the partner without waiting for kills (takes effect next frame, like a normal revival).</summary>
        public static void ReviveNow()
        {
            if (s_someoneDead)
            {
                s_kills = Mathf.Max(s_kills, CoopConfig.KillsToRevive.Value);
            }
        }

        /// <summary>Reset the revive wait (online: the partner came back after a drop - will spawn again).</summary>
        public static void ClearDeath()
        {
            s_someoneDead = false;
            s_kills = 0;
        }

        public static void OnSecondSpawned()
        {
            s_someoneDead = false;
            s_kills = 0;
        }

        // ---------- revival ----------

        private void Update()
        {
            if (!s_someoneDead || Net.NetSession.IsClient)
            {
                return;
            }
            // online: do not revive a disconnected partner until they return
            if (Net.NetSession.IsHost && !Net.NetSession.PeerReady)
            {
                return;
            }
            var gm = GameManager.InstanceNoWarnings;
            var main = CoopPlayers.P1;
            if (gm == null || main == null || main.Stats.Dead || CoopPlayers.P2 != null)
            {
                return;
            }
            if (gm.InEnterDoor || CoopTransitions.IsWaiting(main))
            {
                return;
            }

            bool byKills = s_kills >= CoopConfig.KillsToRevive.Value;
            var room = gm.CurrentRoom;
            bool byFloor = CoopConfig.ReviveOnNewFloor.Value && room != null && (room.SegmentID != s_segmentAtDeath || room.FloorID != s_floorAtDeath);
            if (!byKills && !byFloor)
            {
                return;
            }

            Plugin.Log.LogInfo($"[Life] revive ({(byKills ? "kills" : "new floor")})");
            CoopPlayers.SpawnP2(main.transform.position + (main.transform.localScale.x >= 0f ? Vector3.left : Vector3.right) * 0.8f);
        }

        private void OnGUI()
        {
            if (!s_someoneDead || CoopPlayers.P1 == null || CoopPlayers.P1.Stats.Dead)
            {
                return;
            }
            // online: partner is disconnected - they will return on reconnect, do not show the counter
            if (Net.NetSession.IsHost && !Net.NetSession.PeerReady)
            {
                return;
            }
            if (m_style == null || m_styleHeight != Screen.height)
            {
                m_styleHeight = Screen.height;
                m_style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(Screen.height / 30f), fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter };
            }
            string text = $"Partner revive: {s_kills}/{CoopConfig.KillsToRevive.Value} kills" + (CoopConfig.ReviveOnNewFloor.Value ? " or new floor" : "");
            var rect = new Rect(0f, Screen.height * 0.06f, Screen.width, 60f);
            m_style.normal.textColor = Color.black;
            GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, m_style);
            m_style.normal.textColor = new Color(1f, 0.85f, 0.3f);
            GUI.Label(rect, text, m_style);
        }
    }
}
