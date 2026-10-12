using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LuckyTower;
using LuckyTower.RoomGeneration;
using LuckyTowerCoop.Net;
using UnityEngine;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Случайные декорации и ловушки (банки, табуреты, бомбы, монстры в окнах) игра выбирает общим генератором случайных
    /// чисел при появлении объекта. Хост и клиент дёргают его в разном порядке, поэтому выбор расходится.
    /// В онлайне перед решением каждого рандомайзера генераторы перезапускаются сидом, зависящим только от самого объекта
    /// (имя комнаты + путь в ней + сид забега) — одинаковым на обеих машинах.
    /// </summary>
    [HarmonyPatch]
    internal static class RandomizerPatches
    {
        private static readonly string[] s_types =
        {
            "EnableRandomObject", "LinkEnableRandomObjects", "SpriteRandomizer", "LuckyTower.Items.SpawnRandomObject", "SpawnRandomItem",
            "LuckyTower.CharacterSystem.MonsterRandomizer", "ColorRandomizer", "ScaleRandomizer", "TransformRandomizer", "randomRotation",
            "RandomAnimationSpeed", "PivotParallaxRandomizer", "CompanionRandomizer", "ChestSpawner",
            "LuckyTower.RoomGeneration.BalconyGenerator", // внешняя стена башни: ряды окон, мусор, монстры — всё в Start()
            "BalconyWindManager", // таймеры ветра (Awake)
        };

        private static readonly string[] s_entries = { "Awake", "Start", "OnEnable" };

        private static readonly FieldInfo s_threadAwareGlobal = AccessTools.Field(AccessTools.TypeByName("Weighted_Randomizer.ThreadAwareRandom"), "_global");

        private static IEnumerable<MethodBase> TargetMethods()
        {
            int n = 0;
            foreach (var name in s_types)
            {
                var t = AccessTools.TypeByName(name);
                if (t == null)
                {
                    Plugin.Log.LogWarning($"[Random] type not found: {name}");
                    continue;
                }
                foreach (var entry in s_entries)
                {
                    var m = t.GetMethod(entry, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (m != null && m.GetMethodBody() != null)
                    {
                        n++;
                        yield return m;
                    }
                }
            }
            Plugin.Log.LogInfo($"[Random] randomizer entry points seeded deterministically: {n}");
        }

        internal struct Saved
        {
            public bool Set;
            public UnityEngine.Random.State Unity;
            public object Global;
        }

        private static void Prefix(MonoBehaviour __instance, out Saved __state)
        {
            __state = default;
            if (NetSession.Role == NetRole.None || __instance == null)
            {
                return;
            }
            // состояние общих генераторов сохраняем: генерация башни (та же корутина, другие кадры) не должна зависеть от нас
            __state.Set = true;
            __state.Unity = UnityEngine.Random.state;
            __state.Global = s_threadAwareGlobal?.GetValue(null);
            int seed = StableSeed(__instance);
            UnityEngine.Random.InitState(seed);
            if (s_threadAwareGlobal != null)
            {
                s_threadAwareGlobal.SetValue(null, new System.Random(seed));
            }
        }

        private static void Postfix(Saved __state)
        {
            if (!__state.Set)
            {
                return;
            }
            UnityEngine.Random.state = __state.Unity;
            if (s_threadAwareGlobal != null && __state.Global != null)
            {
                s_threadAwareGlobal.SetValue(null, __state.Global);
            }
        }

        private static int StableSeed(Component c)
        {
            var room = c.GetComponentInParent<Room>(true);
            string path;
            string scope;
            if (room != null)
            {
                scope = room.name;
                path = NetPaths.PathOf(c.transform, room.transform) ?? c.name;
            }
            else
            {
                scope = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                path = NetPaths.PathOf(c.transform, c.transform.root) ?? c.name;
                path = c.transform.root.name + "/" + path;
            }
            int run = SegmentManager.Instance != null ? SegmentManager.Instance.UsedSeed : 0;
            return NetPaths.IdOf(scope, path + "#" + c.GetType().Name) ^ run;
        }
    }
}