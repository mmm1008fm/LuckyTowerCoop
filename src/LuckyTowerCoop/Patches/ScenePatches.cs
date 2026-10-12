using HarmonyLib;
using LuckyTower;
using LuckyTower.Village;
using LuckyTowerCoop.Coop;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Переход между сценами через дверь (башня ↔ деревня): игра переносит P1 через DontDestroyOnLoad,
    /// мы так же переносим живого P2 вместе с вещами. Выход в меню/новый забег идёт через DestroyAll —
    /// тогда P2 уничтожается вместе с P1.
    /// </summary>
    [HarmonyPatch]
    internal static class ScenePatches
    {
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.DestroyAll))]
        [HarmonyPostfix]
        private static void DestroyAll()
        {
            CoopPlayers.Despawn();
            CoopManager.CarryP2 = false;
        }

        [HarmonyPatch(typeof(SceneLoader), nameof(SceneLoader.LoadSceneByName))]
        [HarmonyPrefix]
        private static void LoadSceneByName(VillageManager.LoadScene loadScene)
        {
            var p2 = CoopPlayers.P2;
            if (p2 == null || p2.Stats.Dead)
            {
                return;
            }
            UnityEngine.Object.DontDestroyOnLoad(p2.gameObject);
            CoopManager.CarryP2 = true;
            Plugin.Log.LogInfo($"[Coop] carrying second player to {loadScene}");
        }
    }
}
