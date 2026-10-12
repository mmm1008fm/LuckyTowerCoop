using HarmonyLib;
using LuckyTower;
using LuckyTower.CharacterSystem;
using LuckyTower.Village;
using LuckyTowerCoop.Net;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Клиент-зеркало: собственная симуляция мира выключена — всё делает хост.
    /// Разрешено только то, что применяет сеть (флаги NetClient.Applying*).
    /// </summary>
    [HarmonyPatch]
    internal static class NetClientPatches
    {
        private static bool Mirror => NetSession.IsClient;

        // --- персонажи и ИИ ---
        [HarmonyPatch(typeof(GamePlayer), "Update")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool PlayerUpdate() => !Mirror;

        [HarmonyPatch(typeof(GamePlayer), "FixedUpdate")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool PlayerFixedUpdate() => !Mirror;

        [HarmonyPatch(typeof(Enemy), "Update")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool EnemyUpdate() => !Mirror;

        [HarmonyPatch(typeof(Enemy), "FixedUpdate")]
        [HarmonyPrefix]
        private static bool EnemyFixedUpdate() => !Mirror;

        [HarmonyPatch(typeof(NPC), "Update")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool NpcUpdate() => !Mirror;

        [HarmonyPatch(typeof(NPC), "FixedUpdate")]
        [HarmonyPrefix]
        private static bool NpcFixedUpdate() => !Mirror;

        [HarmonyPatch(typeof(FlyingBehaviour), "Update")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool FlyingUpdate() => !Mirror;

        // --- урон и смерть: только события хоста ---
        [HarmonyPatch(typeof(CharacterAction), nameof(CharacterAction.GetDamaged))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool GetDamaged() => !Mirror;

        [HarmonyPatch(typeof(CharacterAction), nameof(CharacterAction.Die))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Die() => !Mirror || NetClient.ApplyingDeath;

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.ShowDeathUI))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool ShowDeathUI() => !Mirror || NetClient.ApplyingGameOver;

        // --- журнал лута: у клиента врагов создаёт сеть, без привязки к этажу — журнал ведёт хост ---
        [HarmonyPatch(typeof(LuckyTower.Items.LootLog), nameof(LuckyTower.Items.LootLog.AddLootableObjectToLootLog))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool LootLog() => !Mirror;

        // --- переходы: комнату переключает сообщение хоста ---
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.EnterDoor))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool EnterDoor() => !Mirror;

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.EnterDoorSide))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool EnterDoorSide() => !Mirror;

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.EnterNextSegment))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool EnterNextSegment() => !Mirror;

        [HarmonyPatch(typeof(StartNewSegmentTrigger), "OnTriggerEnter2D")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool SegmentTrigger() => !Mirror;

        [HarmonyPatch(typeof(EnterDoorSideTrigger), "OnTriggerEnter2D")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool SideTrigger() => !Mirror;

        // --- сцены: грузит только сеть; выход в меню завершает сессию ---
        [HarmonyPatch(typeof(SceneLoader), nameof(SceneLoader.LoadSceneByName))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool LoadScene(VillageManager.LoadScene loadScene)
        {
            if (!Mirror || NetClient.ApplyingScene)
            {
                return true;
            }
            if (loadScene == VillageManager.LoadScene.MainMenu)
            {
                NetSession.Instance.Stop("клиент вышел в меню");
                return true;
            }
            return false;
        }

        // --- сейв клиента не трогаем ---
        [HarmonyPatch(typeof(GameData), nameof(GameData.Save))]
        [HarmonyPrefix]
        private static bool GameDataSave() => !Mirror;

        [HarmonyPatch(typeof(VillageData), nameof(VillageData.Save))]
        [HarmonyPrefix]
        private static bool VillageDataSave() => !Mirror;

        [HarmonyPatch(typeof(AchievementData), nameof(AchievementData.Save))]
        [HarmonyPrefix]
        private static bool AchievementSave() => !Mirror;

        [HarmonyPatch(typeof(CharacterUtilities), nameof(CharacterUtilities.SetPlayerInvetory))]
        [HarmonyPrefix]
        private static bool LoadInventory() => !Mirror;

        // --- тот же сид забега, что у хоста ---
        private static readonly AccessTools.FieldRef<SegmentManager, bool> s_useSeed = AccessTools.FieldRefAccess<SegmentManager, bool>("m_useSeed");
        private static readonly AccessTools.FieldRef<SegmentManager, int> s_seed = AccessTools.FieldRefAccess<SegmentManager, int>("m_seed");

        [HarmonyPatch(typeof(SegmentManager), nameof(SegmentManager.ResetSeed))]
        [HarmonyPrefix]
        private static void ResetSeed(SegmentManager __instance)
        {
            if (Mirror && NetClient.RunSeed != 0)
            {
                s_useSeed(__instance) = true;
                s_seed(__instance) = NetClient.RunSeed;
            }
        }
    }

    /// <summary>Хост: события, которые нужно отправить клиенту.</summary>
    [HarmonyPatch]
    internal static class NetHostPatches
    {
        private static readonly AccessTools.FieldRef<CharacterAction, Character> s_character =
            AccessTools.FieldRefAccess<CharacterAction, Character>("m_character");

        [HarmonyPatch(typeof(CharacterAction), nameof(CharacterAction.Die))]
        [HarmonyPrefix]
        private static void DiePrefix(CharacterAction __instance, out int __state)
        {
            var host = NetHost.Current;
            var c = s_character(__instance);
            __state = host != null && c != null && !c.Stats.Dead ? host.IdOf(c) : 0;
        }

        [HarmonyPatch(typeof(CharacterAction), nameof(CharacterAction.Die))]
        [HarmonyPostfix]
        private static void DiePostfix(CharacterAction __instance, int __state)
        {
            if (__state == 0)
            {
                return;
            }
            var c = s_character(__instance);
            if (c != null && c.Stats.Dead)
            {
                NetHost.Current?.SendDeath(__state);
            }
        }

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.ShowDeathUI))]
        [HarmonyPostfix]
        private static void ShowDeathUI(bool __runOriginal)
        {
            if (__runOriginal)
            {
                NetHost.Current?.SendGameOver();
            }
        }
    }
}
