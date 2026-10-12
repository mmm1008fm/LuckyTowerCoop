using HarmonyLib;
using LuckyTower.UI;
using LuckyTowerCoop.Net;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// «Начать заново» в меню паузы у клиента ломало сессию: локальная загрузка сцены заглушена, а остальное срабатывало.
    /// Теперь клиент отправляет хосту просьбу, а хост решает (окно в F7); сам перезапуск клиент получит как SceneLoad.
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