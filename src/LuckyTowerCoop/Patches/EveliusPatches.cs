using HarmonyLib;
using LuckyTower.Evelius;
using LuckyTowerCoop.Net;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Evelius (the evil mage in hubs) - the host decides: appearance, mood, hunting, chests. His logic does not run on the client
    /// (Update/triggers are suppressed by the WorldSuppressPatches list), and Start keeps him from self-destructing based on the player puppet's position.
    /// Position/animation arrive like any world object; lines go through Talk/Window.
    /// </summary>
    [HarmonyPatch(typeof(EveliusController), "Start")]
    internal static class EveliusStartPatch
    {
        private static bool Prefix() => !NetSession.IsClient;
    }
}