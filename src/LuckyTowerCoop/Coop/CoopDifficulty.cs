using System.Collections.Generic;
using LuckyTower.CharacterSystem;
using LuckyTower.RoomGeneration;
using UnityEngine;

namespace LuckyTowerCoop.Coop
{
    /// <summary>
    /// Two-player difficulty: enemy health multiplier and extra copies of regular monsters.
    /// Applied once, on first entry to a room (on the host / in local play; the online client receives the result over the network).
    /// </summary>
    internal static class CoopDifficulty
    {
        private static readonly HashSet<int> s_processedRooms = new HashSet<int>();
        private static readonly Dictionary<GameObject, GameObject> s_cloneSource = new Dictionary<GameObject, GameObject>();
        private static readonly System.Random s_rng = new System.Random(); // leave UnityEngine.Random alone (tower generation)

        public static bool TryGetCloneSource(GameObject clone, out GameObject source)
        {
            source = null;
            return clone != null && s_cloneSource.TryGetValue(clone, out source) && source != null;
        }

        public static void OnRoomEntered(Room room)
        {
            if (room == null || Net.NetSession.IsClient || !s_processedRooms.Add(room.GetInstanceID()))
            {
                return;
            }
            float hp = CoopConfig.EnemyHealthMultiplier.Value;
            float extra = CoopConfig.ExtraEnemies.Value;
            if (Mathf.Approximately(hp, 1f) && extra <= 0f)
            {
                return;
            }

            int scaled = 0, cloned = 0;
            var enemies = room.GetComponentsInChildren<Enemy>(false);
            foreach (var e in enemies)
            {
                if (e == null || e.Stats.Dead || e.Stats.CharacterType != CharacterTypes.Enemy)
                {
                    continue;
                }
                if (!Mathf.Approximately(hp, 1f))
                {
                    Scale(e, hp);
                    scaled++;
                }
                // clone regular monsters only (patrol/special enemies have room bindings)
                if (extra > 0f && e.GetType() == typeof(Enemy) && e.Stats.EnemyType == CharacterStats.EnemyTypes.Monster)
                {
                    int copies = Mathf.FloorToInt(extra) + (s_rng.NextDouble() < extra - Mathf.Floor(extra) ? 1 : 0);
                    for (int i = 0; i < copies; i++)
                    {
                        var offset = new Vector3((float)(s_rng.NextDouble() * 2.4 - 1.2), 0.2f, 0f);
                        var go = Object.Instantiate(e.gameObject, e.transform.position + offset, e.transform.rotation, e.transform.parent);
                        go.name = e.gameObject.name + "+";
                        s_cloneSource[go] = e.gameObject; // the clone already inherited the health (the original is scaled above)
                        Net.NetPaths.MarkRuntime(go);
                        cloned++;
                    }
                }
            }
            if (scaled + cloned > 0)
            {
                Plugin.Log.LogInfo($"[Difficulty] {room.name}: health x{hp} for {scaled}, +{cloned} extra monsters");
            }
        }

        private static void Scale(Character c, float mult)
        {
            int hp = Mathf.Max(1, Mathf.RoundToInt(c.Stats.Health * mult));
            c.Stats.MaximumHealth = hp;
            c.Stats.Health = hp;
        }

        public static void OnSceneChanged()
        {
            s_processedRooms.Clear();
            s_cloneSource.Clear();
            Net.NetPaths.ClearRuntime();
        }
    }
}
