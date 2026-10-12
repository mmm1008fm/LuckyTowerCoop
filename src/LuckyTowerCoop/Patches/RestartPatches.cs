using HarmonyLib;
using LuckyTower.UI;
using LuckyTowerCoop.Net;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// "Start over" in the pause menu broke the client's session: local scene loading is suppressed, but everything else fired.
    /// Now the client sends the host a request and the host decides (window in F7); the client receives the restart itself as SceneLoad.
    /// </summary>
    [HarmonyPatch(typeof(MainMenuHandler))]
    internal static class RestartPatches
    {
        [HarmonyPatch(nameof(MainMenuHandler.LoadNewGame))]
        [HarmonyPrefix]
        private static bool LoadNewGame()
        {
            if (!NetSession.IsClient)
            {
                return true;
            }
            NetSession.Instance?.Client?.RequestRestart();
            return false;
        }
    }
}