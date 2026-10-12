using LuckyTower;
using LuckyTower.CharacterSystem;
using LuckyTower.Combat;
using UnityEngine;
using UnityEngine.SceneManagement;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Coop
{
    /// <summary>
    /// Смерть и воскрешение: пока жив напарник, смерть одного не заканчивает забег.
    /// Погибший воскресает, когда живой убьёт KillsToRevive монстров (или на новом этаже, если включено).
    /// Если погибли оба — обычный game over игры.
    /// </summary>
    public class CoopLife : MonoBehaviour
    {
        private static bool s_someoneDead;
        private static int s_kills;
        private static int s_segmentAtDeath;
        private static int s_floorAtDeath;

        /// <summary>Смерть игрока в кооп-контексте (напарник жив): глушим game over и очистку боя.</summary>
        public static Character DyingInCoop { get; private set; }

        public static bool SomeoneDead => s_someoneDead;

        public static int Kills => s_kills;

        private GUIStyle m_style;
        private int m_styleHeight;

        private void Start()
        {
            SceneManager.activeSceneChanged += (_, __) => s_someoneDead = false;
        }

        // ---------- вызовы из патчей ----------

        /// <summary>Префикс CharacterAction.Die. true — смерть в кооп-контексте.</summary>
        public static bool BeginDeath(Character dying)
        {
            if (Net.NetSession.IsClient || dying == null || dying.Stats.Dead || !(dying is GamePlayer player) || !CoopPlayers.IsCoopPlayer(player))
            {
                return false;
            }
            var partner = player == CoopPlayers.P1 ? (GamePlayer)CoopPlayers.P2 : CoopPlayers.P1;
            if (partner == null || partner.Stats.Dead || !partner.gameObject.activeInHierarchy)
            {
                return false; // напарника нет — обычная смерть игры
            }
            DyingInCoop = dying;
            Plugin.Log.LogInfo($"[Life] '{dying.name}' dies, partner '{partner.name}' is alive — run continues");
            return true;
        }

        /// <summary>Постфикс CharacterAction.Die (тело уже создано).</summary>
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

        /// <summary>Постфикс AchievementManager.TrackDeath — счётчик убийств.</summary>
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

        /// <summary>Меню: воскресить напарника, не дожидаясь убийств (сработает в ближайшем кадре, как обычное воскрешение).</summary>
        public static void ReviveNow()
        {
            if (s_someoneDead)
            {
                s_kills = Mathf.Max(s_kills, CoopConfig.KillsToRevive.Value);
            }
        }

        /// <summary>Сбросить ожидание воскрешения (онлайн: напарник вернулся после обрыва — появится заново).</summary>
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

        // ---------- воскрешение ----------

        private void Update()
        {
            if (!s_someoneDead || Net.NetSession.IsClient)
            {
                return;
            }
            // онлайн: отключившегося напарника не воскрешаем, пока он не вернётся
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
            // онлайн: напарник отключён — он вернётся при переподключении, счётчик не показываем
            if (Net.NetSession.IsHost && !Net.NetSession.PeerReady)
            {
                return;
            }
            if (m_style == null || m_styleHeight != Screen.height)
            {
                m_styleHeight = Screen.height;
                m_style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(Screen.height / 30f), fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter };
            }
            string text = $"Воскрешение напарника: {s_kills}/{CoopConfig.KillsToRevive.Value} убийств" + (CoopConfig.ReviveOnNewFloor.Value ? " или новый этаж" : "");
            var rect = new Rect(0f, Screen.height * 0.06f, Screen.width, 60f);
            m_style.normal.textColor = Color.black;
            GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, m_style);
            m_style.normal.textColor = new Color(1f, 0.85f, 0.3f);
            GUI.Label(rect, text, m_style);
        }
    }
}
