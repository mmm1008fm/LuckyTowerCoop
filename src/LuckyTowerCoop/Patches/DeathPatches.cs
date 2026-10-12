using System;
using System.Collections;
using HarmonyLib;
using LuckyTower;
using LuckyTower.CharacterSystem;
using LuckyTower.Combat;
using LuckyTower.Village;
using LuckyTowerCoop.Coop;

namespace LuckyTowerCoop.Patches
{
    /// <summary>Смерть одного игрока при живом напарнике не заканчивает забег.</summary>
    [HarmonyPatch]
    internal static class DeathPatches
    {
        private static readonly AccessTools.FieldRef<CharacterAction, Character> s_character =
            AccessTools.FieldRefAccess<CharacterAction, Character>("m_character");

        private static bool s_savedEndgame;

        [HarmonyPatch(typeof(CharacterAction), nameof(CharacterAction.Die))]
        [HarmonyPrefix]
        private static void DiePrefix(CharacterAction __instance, out bool __state)
        {
            var c = s_character(__instance);
            __state = CoopLife.BeginDeath(c);
            if (__state)
            {
                s_savedEndgame = DataManager.Instance.EndgameMode;
            }
        }

        [HarmonyPatch(typeof(CharacterAction), nameof(CharacterAction.Die))]
        [HarmonyFinalizer]
        private static Exception DieFinalizer(CharacterAction __instance, bool __state, Exception __exception)
        {
            if (__state)
            {
                DataManager.Instance.EndgameMode = s_savedEndgame;
                CoopLife.EndDeath(s_character(__instance));
            }
            return __exception;
        }

        // --- глушим «конец забега», пока жив кто-то из игроков ---

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.ShowDeathUI))]
        [HarmonyPrefix]
        private static bool ShowDeathUI()
        {
            if (CoopLife.DyingInCoop != null || (CoopLife.SomeoneDead && CoopPlayers.P1 != null && !CoopPlayers.P1.Stats.Dead))
            {
                Plugin.Log.LogInfo("[Life] game over suppressed — partner alive");
                return false;
            }
            return true;
        }

        // клиент: конец забега решает хост (придёт GameOver/SceneLoad) — свои похороны/очистку боя не запускаем
        private static bool Client => Net.NetSession.IsClient;

        [HarmonyPatch(typeof(VillageManager), nameof(VillageManager.FuneralCutScene))]
        [HarmonyPrefix]
        private static bool FuneralCutScene() => CoopLife.DyingInCoop == null && !Client;

        [HarmonyPatch(typeof(CharacterAction), "HackyFuneral")]
        [HarmonyPostfix]
        private static void HackyFuneral(ref IEnumerator __result)
        {
            if (CoopLife.DyingInCoop != null || Client)
            {
                __result = Empty();
            }
        }

        private static IEnumerator Empty()
        {
            yield break;
        }

        [HarmonyPatch(typeof(AudioManager), nameof(AudioManager.StopMusic))]
        [HarmonyPrefix]
        private static bool StopMusic() => CoopLife.DyingInCoop == null && !Client;

        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.ClearEnemies))]
        [HarmonyPrefix]
        private static bool ClearEnemies() => CoopLife.DyingInCoop == null && !Client;

        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.RemoveAllCompanions))]
        [HarmonyPrefix]
        private static bool RemoveAllCompanions() => CoopLife.DyingInCoop == null && !Client;

        // --- счётчик убийств ---

        [HarmonyPatch(typeof(AchievementManager), nameof(AchievementManager.TrackDeath))]
        [HarmonyPostfix]
        private static void TrackDeath(Character character, IDamageSource damageSource) => CoopLife.OnCharacterKilled(character, damageSource);

        // --- игра сама создала главного персонажа (новый забег / бутылка) ---

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.SpawnPlayer))]
        [HarmonyPostfix]
        private static void SpawnPlayer() => CoopPlayers.OnGameSpawnedMain();
    }
}
