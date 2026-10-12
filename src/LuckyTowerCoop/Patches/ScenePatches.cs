using HarmonyLib;
using LuckyTower;
using LuckyTower.Village;
using LuckyTowerCoop.Coop;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Scene transition through a door (tower <-> village): the game carries P1 over via DontDestroyOnLoad,
    /// and we likewise carry the living P2 along with their items. Exit to menu/new run goes through DestroyAll -
    /// then P2 is destroyed together with P1.
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
