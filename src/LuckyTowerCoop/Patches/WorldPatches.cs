using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LuckyTower.Items;
using LuckyTowerCoop.Net;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Клиент-зеркало, мир: ловушки, рычаги, сундуки, полы, спавнеры монстров у клиента не живут своей жизнью —
    /// их состояние (аниматор, положение, активность) приходит от хоста. Глушим их Update и физические колбэки,
    /// а также урон по повреждаемым объектам (удар клиента по рычагу решает хост).
    /// </summary>
    [HarmonyPatch]
    internal static class WorldSuppressPatches
    {
        private static readonly string[] s_methods =
        {
            "Update", "FixedUpdate", "LateUpdate",
            "OnTriggerEnter2D", "OnTriggerExit2D", "OnTriggerStay2D",
            "OnCollisionEnter2D", "OnCollisionExit2D", "OnCollisionStay2D",
        };

        // не-Trap классы с собственной логикой мира
        private static readonly string[] s_extraTypes =
        {
            "LuckyTower.Traps.Lever", "LuckyTower.Traps.PuzzleLever", "LuckyTower.Traps.LightsOutLever", "LuckyTower.Traps.PuzzleLeverManager",
            "TreasureChest", "ChestSpawner", "GooOnFloor", "GooOnFloorTrigger", "Damagable",
            "MonsterInGapTrigger", "MonsterInWallTrigger", "MonsterInGap", "MonsterInWall",
            "DungeonEndCrumblingStep", "WeightedStepParent", "GamblerWeightedStep", "TriggerCounter", "LightsOutTrigger",
            "CrusherManager", "CrusherManagerSemi", "MultiDismantle", "UseTrapSpot",
            "BalconyBird", "BalconyFakeDoor", "BalconyDropTrigger", "CompanionBalconyTrap",
            "LuckyTower.Evelius.EveliusController",
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var asm = typeof(Trap).Assembly;
            var types = new HashSet<Type>();
            foreach (var t in asm.GetTypes())
            {
                // SpecialItem (лампа, зелья) — предметы, FlyingBehaviour (мыши) — уже глушатся отдельно
                if (typeof(Trap).IsAssignableFrom(t) && !typeof(FlyingBehaviour).IsAssignableFrom(t) && !typeof(SpecialItem).IsAssignableFrom(t))
                {
                    types.Add(t);
                }
            }
            foreach (var n in s_extraTypes)
            {
                var t = asm.GetType(n) ?? AccessTools.TypeByName(n);
                if (t != null)
                {
                    types.Add(t);
                }
            }
            int count = 0;
            foreach (var t in types)
            {
                foreach (var name in s_methods)
                {
                    var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (m != null && !m.IsAbstract && m.GetMethodBody() != null)
                    {
                        count++;
                        yield return m;
                    }
                }
            }
            Plugin.Log.LogInfo($"[World] client-side suppression: {types.Count} types, {count} methods");
        }

        private static bool Prefix() => !NetSession.IsClient;
    }

    /// <summary>Повреждаемые объекты мира (рычаги, сундуки, бочки): урон считает только хост.</summary>
    [HarmonyPatch(typeof(Damagable))]
    internal static class DamagableClientPatches
    {
        [HarmonyPatch(nameof(Damagable.GetDamaged), typeof(int))]
        [HarmonyPrefix]
        private static bool GetDamaged1() => !NetSession.IsClient;

        [HarmonyPatch(nameof(Damagable.GetDamaged), typeof(int), typeof(float), typeof(bool))]
        [HarmonyPrefix]
        private static bool GetDamaged3() => !NetSession.IsClient;
    }
}