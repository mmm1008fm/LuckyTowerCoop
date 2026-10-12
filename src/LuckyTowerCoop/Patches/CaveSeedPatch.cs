using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LuckyTower;
using LuckyTower.RoomGeneration;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Caves after the tower: the game shuffles rooms with an unseeded System.Random, so host and client got different caves.
    /// We replace `new System.Random()` with `new System.Random(run seed)`.
    /// </summary>
    [HarmonyPatch]
    internal static class CaveSeedPatch
    {
        private static readonly ConstructorInfo s_ctorEmpty = AccessTools.Constructor(typeof(System.Random), Type.EmptyTypes);
        private static readonly ConstructorInfo s_ctorSeed = AccessTools.Constructor(typeof(System.Random), new[] { typeof(int) });

        private static MethodBase TargetMethod() => AccessTools.Method(typeof(RoomGenerator), "GenerateCave");

        public static int Seed() => (SegmentManager.Instance != null ? SegmentManager.Instance.UsedSeed : 0) ^ 0x5CA7E;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int n = 0;
            foreach (var ci in instructions)
            {
                if (ci.opcode == OpCodes.Newobj && ci.operand is ConstructorInfo c && c == s_ctorEmpty)
                {
                    n++;
                    // labels/blocks stay on the original instruction - replace it in place
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(CaveSeedPatch), nameof(Seed));
                    yield return ci;
                    yield return new CodeInstruction(OpCodes.Newobj, s_ctorSeed);
                    continue;
                }
                yield return ci;
            }
            Plugin.Log.LogInfo($"[Gen] cave generator: seeded {n} System.Random");
        }
    }
}