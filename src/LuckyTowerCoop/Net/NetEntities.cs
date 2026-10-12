using System.Collections.Generic;
using System.Text;
using LiteNetLib.Utils;
using LuckyTower;
using LuckyTower.CharacterSystem;
using UnityEngine;

namespace LuckyTowerCoop.Net
{
    internal enum EntityKind : byte
    {
        Other = 0,
        Character = 1,
        Item = 2,
        Seat = 3,
    }

    /// <summary>Синхронизируемый объект (общий для хоста и клиента).</summary>
    internal sealed class NetEntity
    {
        /// <summary>Описание для dev-команды ent: путь, активность по цепочке родителей.</summary>
        public string Describe(Transform room)
        {
            if (!Alive)
            {
                return $"{Id} <destroyed>";
            }
            var sb = new System.Text.StringBuilder();
            sb.Append($"{Id} {Kind} {Go.name} pos={T.position:F2} activeSelf={Go.activeSelf} inHierarchy={Go.activeInHierarchy}");
            if (room != null)
            {
                sb.Append($" path={NetPaths.Pretty(NetPaths.PathOf(T, room))}");
            }
            var chain = new System.Collections.Generic.List<string>();
            for (var p = T.parent; p != null && p != room; p = p.parent)
            {
                chain.Add(p.name + (p.gameObject.activeSelf ? "" : "[off]"));
            }
            chain.Reverse();
            sb.Append(" parents=" + string.Join(" > ", chain));
            return sb.ToString();
        }

        public int Id;
        public EntityKind Kind;
        public GameObject Go;
        public Transform T;
        public Rigidbody2D Rb;
        public Animator Anim;
        public Character Character;
        public PickupItem Item;
        public bool SpawnedByNet;
        /// <summary>Летающий враг (FlyingBehaviour — не Character, но с аниматором).</summary>
        public bool Flying;

        // прикрепление к персонажу (предмет в руке): хост — что отправили; клиент — куда прикрепить
        public string AttachKey;
        public Vector3 AttachSentLocal;
        public bool Attached;
        public bool AttachPending;
        public int AttachSeat;
        public string AttachPath;
        public Vector3 AttachLocalPos;
        public Quaternion AttachLocalRot = Quaternion.identity;
        public Vector3 AttachLocalScale = Vector3.one;

        // клиент: тело погибшего — без своей физики, повторяет хоста
        public bool CorpseFrozen;

        // хост: что отправляли последним
        public Vector3 SentPos;
        public float SentRot;
        public float SentTime = -10f;
        public bool SentActive;
        public bool SentAudio;

        // клиент: цель интерполяции
        public bool HasTarget;
        public Vector3 TargetPos;
        public float TargetRot;
        public bool TargetFlip;
        public int StateMismatchFrames;
        public byte HealthByte = 255; // здоровье 0..255 от максимума (визуал урона у клиента)
        public AudioSource Audio;     // мир: зацикленный звук (барабанщики) — играет/нет
        public Vector2 Vel;      // клиент: скорость из снапшота (экстраполяция)
        public float SnapTime;   // клиент: когда пришёл снапшот

        // хост: последняя отправленная сигнатура аниматора
        public uint AnimSig;
        public float AnimSentTime = -10f;

        public bool Alive => Go != null;

        public static NetEntity For(int id, GameObject go, EntityKind kind)
        {
            var e = new NetEntity { Id = id, Go = go, T = go.transform, Kind = kind };
            e.Rb = go.GetComponent<Rigidbody2D>();
            e.Character = go.GetComponent<Character>();
            e.Item = go.GetComponent<PickupItem>();
            if (e.Character != null && e.Character.Components != null && e.Character.Components.Animator != null)
            {
                e.Anim = e.Character.Components.Animator.AnimatorComponent;
            }
            if (e.Anim == null)
            {
                e.Anim = go.GetComponentInChildren<Animator>();
            }
            e.Flying = go.GetComponent<FlyingBehaviour>() != null;
            if (kind == EntityKind.Other)
            {
                e.Audio = go.GetComponent<AudioSource>();
            }
            return e;
        }
    }

    /// <summary>Стабильные ID объектов комнаты: путь в иерархии от корня комнаты («имя#номер среди одноимённых»).</summary>
    internal static class NetPaths
    {
        public const int SeatA = 1;
        public const int SeatB = 2;
        public const int RuntimeIdStart = 1000;

        public static string PathOf(Transform t, Transform root)
        {
            var parts = new List<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                parts.Add(Segment(cur));
                cur = cur.parent;
            }
            if (cur != root)
            {
                return null;
            }
            parts.Reverse();
            return string.Join(Sep, parts);
        }

        // имена объектов игры содержат «/» («Window 6/4») и «#» («Door #1») — разделители пути берём служебные
        public const string Sep = "\u001f";
        private const char SepChar = '\u001f';
        private const char DupChar = '\u001e';

        private static string Segment(Transform t)
        {
            int k = 0;
            var parent = t.parent;
            if (parent != null)
            {
                for (int i = 0; i < parent.childCount; i++)
                {
                    var c = parent.GetChild(i);
                    if (c == t)
                    {
                        break;
                    }
                    if (c.name == t.name)
                    {
                        k++;
                    }
                }
            }
            return k == 0 ? t.name : t.name + DupChar + k;
        }

        /// <summary>Путь для логов: разделители заменены на читаемые.</summary>
        public static string Pretty(string path) => path == null ? "" : path.Replace(SepChar, '/').Replace(DupChar, '#');

        public static Transform Find(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            var cur = root;
            foreach (var seg in path.Split(SepChar))
            {
                string name = seg;
                int k = 0;
                int hash = seg.LastIndexOf(DupChar);
                if (hash > 0 && int.TryParse(seg.Substring(hash + 1), out var kk))
                {
                    name = seg.Substring(0, hash);
                    k = kk;
                }
                Transform next = null;
                for (int i = 0; i < cur.childCount; i++)
                {
                    var c = cur.GetChild(i);
                    if (c.name != name)
                    {
                        continue;
                    }
                    if (k-- == 0)
                    {
                        next = c;
                        break;
                    }
                }
                if (next == null)
                {
                    return null;
                }
                cur = next;
            }
            return cur;
        }

        /// <summary>FNV-1a, со сдвигом в свою область (не пересекается с местами и рантайм-ID).</summary>
        public static int IdOf(string roomName, string path)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char ch in roomName)
                {
                    h = (h ^ ch) * 16777619;
                }
                h = (h ^ '|') * 16777619;
                foreach (char ch in path)
                {
                    h = (h ^ ch) * 16777619;
                }
                return (int)(h | 0x40000000u) & 0x7FFFFFFF;
            }
        }

        // объекты, добавленные в комнату во время игры (копии врагов, предметы от сети): не входят в маску,
        // иначе у хоста и клиента разъезжается порядок обхода
        private static readonly HashSet<GameObject> s_runtime = new HashSet<GameObject>();
        private static readonly List<Transform> s_walk = new List<Transform>(2048);

        public static void MarkRuntime(GameObject go)
        {
            if (go != null)
            {
                s_runtime.Add(go);
            }
        }

        public static bool IsRuntime(GameObject go) => go != null && s_runtime.Contains(go);

        public static void ClearRuntime() => s_runtime.Clear();

        private static List<Transform> Walk(Transform root)
        {
            s_walk.Clear();
            WalkInto(root);
            return s_walk;
        }

        private static void WalkInto(Transform t)
        {
            if (s_runtime.Contains(t.gameObject))
            {
                return;
            }
            s_walk.Add(t);
            for (int i = 0; i < t.childCount; i++)
            {
                WalkInto(t.GetChild(i));
            }
        }

        /// <summary>Хэши путей выключенных объектов комнаты (без рантайм-объектов): клиент выравнивает рандомные декорации по именам, а не по порядку.</summary>
        public static List<int> InactiveHashes(Transform root, string roomName)
        {
            var all = Walk(root);
            var list = new List<int>();
            for (int i = 1; i < all.Count; i++)
            {
                if (!all[i].gameObject.activeSelf)
                {
                    list.Add(IdOf(roomName, PathOf(all[i], root) ?? all[i].name));
                }
            }
            return list;
        }

        /// <summary>Применить: всё, чего нет в списке выключенных, — включить; что есть — выключить. Возвращает число изменений.</summary>
        public static int ApplyInactive(Transform root, string roomName, HashSet<int> inactive)
        {
            var all = Walk(root);
            int changed = 0;
            // копия: SetActive меняет иерархию обхода (OnEnable может создавать объекты)
            var snapshot = new List<Transform>(all);
            for (int i = 1; i < snapshot.Count; i++)
            {
                var t = snapshot[i];
                if (t == null)
                {
                    continue;
                }
                bool want = !inactive.Contains(IdOf(roomName, PathOf(t, root) ?? t.name));
                if (t.gameObject.activeSelf != want)
                {
                    t.gameObject.SetActive(want);
                    changed++;
                }
            }
            return changed;
        }

        /// <summary>Маска activeSelf иерархии комнаты (без рантайм-объектов) в порядке обхода (выравнивание рандомайзеров на клиенте).</summary>
        public static byte[] ActiveMask(Transform root, out int count)
        {
            var all = Walk(root);
            count = all.Count;
            var bytes = new byte[(count + 7) / 8];
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].gameObject.activeSelf)
                {
                    bytes[i >> 3] |= (byte)(1 << (i & 7));
                }
            }
            return bytes;
        }

        public static int ApplyActiveMask(Transform root, int count, byte[] bytes)
        {
            var all = Walk(root);
            if (all.Count != count || bytes == null || bytes.Length < (count + 7) / 8)
            {
                return -1;
            }
            int changed = 0;
            for (int i = 1; i < all.Count; i++)
            {
                bool want = (bytes[i >> 3] & (1 << (i & 7))) != 0;
                if (all[i].gameObject.activeSelf != want)
                {
                    all[i].gameObject.SetActive(want);
                    changed++;
                }
            }
            return changed;
        }
    }

    /// <summary>
    /// Видимость частей персонажа. Вид со спины у двери, кошель на поясе, дырки зубов и т. п. игра переключает кодом,
    /// который у клиента для марионеток не выполняется, — поэтому хост шлёт для каждого рендера персонажа
    /// (кроме предметов в руках) хэш пути от корня персонажа и два флага: «включён», «объект активен».
    /// </summary>
    internal static class NetLook
    {
        private sealed class Cache
        {
            public readonly List<Renderer> List = new List<Renderer>(256);
            public readonly List<int> Hashes = new List<int>(256);
            public readonly Dictionary<int, Renderer> ByHash = new Dictionary<int, Renderer>(256);
            public float Time = -10f;
        }

        private static readonly Dictionary<Character, Cache> s_cache = new Dictionary<Character, Cache>();

        private static Cache Get(Character c, bool forceRebuild = false)
        {
            if (!s_cache.TryGetValue(c, out var k))
            {
                if (s_cache.Count > 16)
                {
                    s_cache.Clear();
                }
                k = new Cache();
                s_cache[c] = k;
                forceRebuild = true;
            }
            if (forceRebuild || UnityEngine.Time.unscaledTime - k.Time > 2f)
            {
                k.List.Clear();
                k.Hashes.Clear();
                k.ByHash.Clear();
                foreach (var r in c.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.GetComponentInParent<PickupItem>(true) != null)
                    {
                        continue;
                    }
                    int h = NetPaths.IdOf("look", NetPaths.PathOf(r.transform, c.transform) ?? r.name);
                    if (k.ByHash.ContainsKey(h))
                    {
                        continue;
                    }
                    k.List.Add(r);
                    k.Hashes.Add(h);
                    k.ByHash[h] = r;
                }
                k.Time = UnityEngine.Time.unscaledTime;
            }
            return k;
        }

        /// <summary>Хост: записать состояние; возвращает сигнатуру (менялось ли).</summary>
        public static uint Write(LiteNetLib.Utils.NetDataWriter w, Character c)
        {
            var k = Get(c);
            uint sig = 2166136261u;
            w.Put((ushort)k.List.Count);
            for (int i = 0; i < k.List.Count; i++)
            {
                var r = k.List[i];
                if (r == null)
                {
                    w.Put(k.Hashes[i]);
                    w.Put((byte)0);
                    continue;
                }
                byte flags = (byte)((r.enabled ? 1 : 0) | (r.gameObject.activeSelf ? 2 : 0));
                w.Put(k.Hashes[i]);
                w.Put(flags);
                unchecked
                {
                    sig = (sig ^ (uint)(k.Hashes[i] + flags)) * 16777619u;
                }
            }
            return sig;
        }

        /// <summary>Клиент: применить; возвращает число изменённых частей.</summary>
        public static int Apply(Character c, LiteNetLib.Utils.NetDataReader r)
        {
            var k = Get(c);
            var gfx = c.Components != null && c.Components.Animator != null ? c.Components.Animator.gameObject : null;
            int n = r.GetUShort();
            int changed = 0;
            for (int i = 0; i < n; i++)
            {
                int h = r.GetInt();
                byte flags = r.GetByte();
                if (!k.ByHash.TryGetValue(h, out var rend) || rend == null)
                {
                    continue;
                }
                bool en = (flags & 1) != 0;
                bool act = (flags & 2) != 0;
                if (rend.enabled != en)
                {
                    rend.enabled = en;
                    changed++;
                }
                var go = rend.gameObject;
                if (go.activeSelf != act && go != gfx && go != c.gameObject)
                {
                    go.SetActive(act);
                    changed++;
                }
            }
            return changed;
        }
    }
    /// <summary>Есть ли предмет в библиотеке игры (её GetItem пишет ошибку в лог на каждый промах — заклинания и снаряды туда не входят).</summary>
    internal static class ItemCatalog
    {
        private static readonly HarmonyLib.AccessTools.FieldRef<ItemLibrary, Dictionary<int, PickupItem>> s_dict =
            HarmonyLib.AccessTools.FieldRefAccess<ItemLibrary, Dictionary<int, PickupItem>>("m_itemDictionary");

        public static bool Has(ItemLibrary lib, string name)
        {
            if (lib == null || string.IsNullOrEmpty(name))
            {
                return false;
            }
            var d = s_dict(lib);
            return d != null && d.ContainsKey(name.ToLower().GetHashCode());
        }
    }

    /// <summary>Снимок и применение состояния Animator: состояния слоёв, веса, параметры.</summary>
    internal static class NetAnim
    {
        // a.parameters создаёт новый массив на каждый вызов — кэшируем по контроллеру
        private static readonly Dictionary<RuntimeAnimatorController, AnimatorControllerParameter[]> s_params = new Dictionary<RuntimeAnimatorController, AnimatorControllerParameter[]>();
        private static readonly int[] s_hashes = new int[8];
        private static readonly float[] s_times = new float[8];
        private static readonly float[] s_weights = new float[8];

        private static AnimatorControllerParameter[] Params(Animator a)
        {
            var ctrl = a.runtimeAnimatorController;
            if (!s_params.TryGetValue(ctrl, out var ps) || ps == null)
            {
                ps = a.parameters;
                s_params[ctrl] = ps;
            }
            return ps;
        }

        private static uint Mix(uint h, int v)
        {
            unchecked
            {
                return (h ^ (uint)v) * 16777619u;
            }
        }

        /// <summary>Пишет снимок; возвращает сигнатуру без времени анимации (менялось ли состояние).</summary>
        public static uint Write(NetDataWriter w, Animator a)
        {
            if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null)
            {
                w.Put((byte)0);
                return 0;
            }
            uint sig = 2166136261u;
            int layers = Mathf.Min(a.layerCount, 8);
            w.Put((byte)layers);
            for (int i = 0; i < layers; i++)
            {
                var st = a.GetCurrentAnimatorStateInfo(i);
                byte weight = (byte)Mathf.RoundToInt(a.GetLayerWeight(i) * 255f);
                w.Put(st.fullPathHash);
                w.Put(Mathf.Repeat(st.normalizedTime, 1f));
                w.Put(weight);
                sig = Mix(Mix(sig, st.fullPathHash), weight);
            }
            var ps = Params(a);
            int n = Mathf.Min(ps.Length, 255);
            w.Put((byte)n);
            ulong bools = 0;
            int boolBit = 0;
            int floats = 0, ints = 0;
            for (int i = 0; i < n; i++)
            {
                switch (ps[i].type)
                {
                    case AnimatorControllerParameterType.Bool:
                    case AnimatorControllerParameterType.Trigger:
                        if (boolBit < 64 && a.GetBool(ps[i].nameHash))
                        {
                            bools |= 1ul << boolBit;
                        }
                        boolBit++;
                        break;
                    case AnimatorControllerParameterType.Float:
                        floats++;
                        break;
                    case AnimatorControllerParameterType.Int:
                        ints++;
                        break;
                }
            }
            w.Put(bools);
            sig = Mix(Mix(sig, (int)bools), (int)(bools >> 32));
            w.Put((byte)floats);
            w.Put((byte)ints);
            for (int i = 0; i < n; i++)
            {
                var p = ps[i];
                if (p.type == AnimatorControllerParameterType.Float)
                {
                    float v = a.GetFloat(p.nameHash);
                    w.Put(v);
                    sig = Mix(sig, Mathf.RoundToInt(v * 100f));
                }
                else if (p.type == AnimatorControllerParameterType.Int)
                {
                    int v = a.GetInteger(p.nameHash);
                    w.Put((short)v);
                    sig = Mix(sig, v);
                }
            }
            return sig;
        }

        /// <summary>Применить снимок (или только вычитать, если a == null).</summary>
        public static void Read(NetDataReader r, Animator a, NetEntity e)
        {
            int layers = r.GetByte();
            if (layers == 0)
            {
                return;
            }
            for (int i = 0; i < layers; i++)
            {
                s_hashes[i] = r.GetInt();
                s_times[i] = r.GetFloat();
                s_weights[i] = r.GetByte() / 255f;
            }
            int n = r.GetByte();
            ulong bools = r.GetULong();
            int floatCount = r.GetByte();
            int intCount = r.GetByte();
            // значения float/int идут в порядке параметров контроллера
            bool ok = a != null && a.isActiveAndEnabled && a.runtimeAnimatorController != null;
            AnimatorControllerParameter[] ps = ok ? Params(a) : null;
            if (ok && ps.Length != n)
            {
                ok = false; // разные контроллеры — только вычитываем
            }
            if (!ok)
            {
                r.SkipBytes(floatCount * 4 + intCount * 2);
                return;
            }
            int boolBit = 0;
            for (int i = 0; i < n; i++)
            {
                var p = ps[i];
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Bool:
                        if (boolBit < 64)
                        {
                            a.SetBool(p.nameHash, (bools & (1ul << boolBit)) != 0);
                        }
                        boolBit++;
                        break;
                    case AnimatorControllerParameterType.Trigger:
                        if (boolBit < 64 && (bools & (1ul << boolBit)) != 0)
                        {
                            a.SetTrigger(p.nameHash);
                        }
                        else
                        {
                            a.ResetTrigger(p.nameHash);
                        }
                        boolBit++;
                        break;
                    case AnimatorControllerParameterType.Float:
                        a.SetFloat(p.nameHash, r.GetFloat());
                        break;
                    case AnimatorControllerParameterType.Int:
                        a.SetInteger(p.nameHash, r.GetShort());
                        break;
                }
            }
            bool mismatch = false;
            for (int i = 0; i < layers && i < a.layerCount; i++)
            {
                a.SetLayerWeight(i, s_weights[i]);
                if (a.GetCurrentAnimatorStateInfo(i).fullPathHash != s_hashes[i] && !a.IsInTransition(i))
                {
                    mismatch = true;
                }
            }
            // состояние расходится два снапшота подряд — принудительно ставим как у хоста
            e.StateMismatchFrames = mismatch ? e.StateMismatchFrames + 1 : 0;
            if (e.StateMismatchFrames >= 2)
            {
                e.StateMismatchFrames = 0;
                for (int i = 0; i < layers && i < a.layerCount; i++)
                {
                    if (s_hashes[i] != 0 && a.GetCurrentAnimatorStateInfo(i).fullPathHash != s_hashes[i] && a.HasState(i, s_hashes[i]))
                    {
                        a.Play(s_hashes[i], i, s_times[i]);
                    }
                }
            }
        }
        public static string Describe(Animator a)
        {
            if (a == null)
            {
                return "-";
            }
            var sb = new StringBuilder();
            for (int i = 0; i < a.layerCount; i++)
            {
                sb.Append(a.GetCurrentAnimatorStateInfo(i).shortNameHash).Append(' ');
            }
            return sb.ToString();
        }
    }
}
