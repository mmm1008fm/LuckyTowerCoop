using System.Collections;
using HarmonyLib;
using LuckyTower;
using LuckyTower.CharacterSystem;
using LuckyTowerCoop.Net;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Реплики персонажей (субтитры над головой, голос, анимация «Talking»).
    /// Хост: каждую начатую реплику пересылает клиенту (кто говорит + ключ текста).
    /// Клиент: свои реплики не запускает (иначе персонаж «говорит» сам по себе и дёргается против снапшотов хоста),
    /// проигрывает только присланные хостом.
    /// </summary>
    [HarmonyPatch(typeof(DialogueManager))]
    internal static class DialoguePatches
    {
        private static readonly AccessTools.FieldRef<DialogueManager, Character> s_character =
            AccessTools.FieldRefAccess<DialogueManager, Character>("m_character");

        private static IEnumerator Nothing()
        {
            yield break;
        }

        [HarmonyPatch("TalkRoutine")]
        [HarmonyPrefix]
        private static bool TalkRoutine(DialogueManager __instance, LocaDatabase.TextElement element, CharacterAudioComponents character, bool dead, bool endless, ref IEnumerator __result)
        {
            if (NetSession.IsClient)
            {
                if (NetClient.ApplyingTalk)
                {
                    return true;
                }
                __result = Nothing();
                return false;
            }
            var host = NetSession.Instance?.Host;
            if (host != null && NetSession.PeerReady && element.Key != null)
            {
                var speaker = character != null && character.Animator != null ? character.Animator.Character : s_character(__instance);
                host.SendTalk(speaker, element.Key, dead, endless);
            }
            return true;
        }

        [HarmonyPatch(nameof(DialogueManager.PlayDialogue))]
        [HarmonyPrefix]
        private static bool PlayDialogue(ref bool __result)
        {
            if (NetSession.IsClient)
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPatch("StartIdleRoutine")]
        [HarmonyPrefix]
        private static bool StartIdleRoutine() => !NetSession.IsClient;
    }
}
