using LuckyTower;
using LuckyTower.LevelLayers;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LuckyTowerCoop.Coop
{
    /// <summary>
    /// Жизненный цикл коопа: Rewired, второй игрок в каждой сцене (перенесённый из прошлой сцены
    /// или новый), поддержка состояния.
    /// </summary>
    public class CoopManager : MonoBehaviour
    {
        /// <summary>P2 перенесён из прошлой сцены (DontDestroyOnLoad) и ждёт размещения рядом с P1.</summary>
        public static bool CarryP2;

        private float m_readySince = -1f;
        private bool m_handledThisScene;

        private void Start()
        {
            SceneManager.activeSceneChanged += (from, to) =>
            {
                m_handledThisScene = false;
                CoopDifficulty.OnSceneChanged();
                m_readySince = -1f;
                Plugin.Log.LogInfo($"[Coop] scene -> {to.name}");
            };
        }

        private void Update()
        {
            if (!CoopConfig.Enabled.Value)
            {
                return;
            }

            CoopInput.Tick();

            // клиент онлайна: персонажи — марионетки хоста, их создаёт NetClient
            if (Net.NetSession.IsClient)
            {
                CoopPlayers.Enforce();
                return;
            }

            var gm = GameManager.InstanceNoWarnings;
            if (!CoopPlayers.IsInitialized(gm) || !SceneAllowed(gm.SceneName))
            {
                m_readySince = -1f;
                CoopPlayers.Enforce();
                return;
            }

            if (!m_handledThisScene)
            {
                var p1 = CoopPlayers.P1;
                if (p1 != null && !p1.Stats.Dead)
                {
                    if (m_readySince < 0f)
                    {
                        m_readySince = Time.time;
                    }
                    if (Time.time - m_readySince >= CoopConfig.SpawnDelay.Value)
                    {
                        m_handledThisScene = true;
                        PlaceOrSpawnP2(p1);
                    }
                }
            }
            else if (Net.NetSession.IsHost && Net.NetSession.PeerReady && CoopPlayers.P2 == null && !CoopLife.SomeoneDead)
            {
                // онлайн: напарник подключился посреди сцены — создаём его персонажа
                var p1 = CoopPlayers.P1;
                if (p1 != null && !p1.Stats.Dead && !gm.InEnterDoor)
                {
                    CoopPlayers.SpawnP2(p1.transform.position + Vector3.right * 0.8f);
                }
            }

            CoopPlayers.Enforce();
        }

        private static void PlaceOrSpawnP2(LuckyTower.CharacterSystem.Player p1)
        {
            var pos = p1.transform.position + Vector3.right * 0.8f;
            var p2 = CoopPlayers.P2;
            if (CarryP2 && p2 != null && !p2.Stats.Dead)
            {
                CarryP2 = false;
                SceneManager.MoveGameObjectToScene(p2.gameObject, SceneManager.GetActiveScene());
                p2.transform.position = pos;
                p2.Components.Rigidbody.linearVelocity = Vector2.zero;
                CoopTransitions.ForceUnhide(p2);
                int layer = LayerManager.CurrentPlayerLayer;
                if (LayerManager.LayersReady && layer >= 0)
                {
                    LayerManager.MoveToLayer(p2.gameObject, layer);
                }
                Plugin.Log.LogInfo($"[Coop] second player carried over into {SceneManager.GetActiveScene().name} at {pos}");
                return;
            }

            CarryP2 = false;
            // онлайн-хост: второй персонаж — это напарник, без него не создаём
            bool allowed = Net.NetSession.IsHost ? Net.NetSession.PeerReady : CoopConfig.AutoSpawnP2.Value;
            if (allowed && p2 == null)
            {
                CoopPlayers.SpawnP2(pos);
            }
        }

        private static bool SceneAllowed(GameData.SceneName scene)
        {
            switch (scene)
            {
                case GameData.SceneName.Master:
                    return true;
                case GameData.SceneName.Village:
                    return CoopConfig.SpawnInVillage.Value;
                default:
                    return false;
            }
        }
    }
}
