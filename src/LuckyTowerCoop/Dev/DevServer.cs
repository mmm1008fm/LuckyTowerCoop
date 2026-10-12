using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LuckyTower;
using LuckyTowerCoop.Coop;
using UnityEngine;
using UnityEngine.SceneManagement;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Dev
{
    /// <summary>
    /// Отладочные команды по UDP 127.0.0.1 (только при Dev.CommandServer=true).
    /// Позволяет автоматически проверять мод: нажатия за P1/P2, спавн, загрузка сцен, скриншоты.
    /// </summary>
    public class DevServer : MonoBehaviour
    {
        private readonly ConcurrentQueue<(string cmd, IPEndPoint from)> m_queue = new ConcurrentQueue<(string, IPEndPoint)>();
        private UdpClient m_udp;
        private Thread m_thread;
        private volatile bool m_running;

        private readonly ScriptedButtonSource[] m_scripted = { new ScriptedButtonSource(), new ScriptedButtonSource() };
        private float m_windowCheckAt = -1f;

        public static int Port { get; private set; }

        private static bool s_god;
        private readonly float?[] m_walkTarget = new float?[2];
        private readonly float[] m_walkDeadline = new float[2];

        private void Start()
        {
            if (CoopConfig.DevRunInBackground.Value)
            {
                Application.runInBackground = true;
            }
            SceneManager.activeSceneChanged += (_, __) => m_windowCheckAt = Time.realtimeSinceStartup + 1.5f;
            m_windowCheckAt = Time.realtimeSinceStartup + 1.5f;

            if (!CoopConfig.DevEnabled)
            {
                return;
            }
            // Несколько копий игры (тест онлайна на одном ПК): занимаем первый свободный порт
            for (int port = CoopConfig.DevPort.Value; port < CoopConfig.DevPort.Value + 4; port++)
            {
                try
                {
                    m_udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                    Port = port;
                    break;
                }
                catch (SocketException)
                {
                }
            }
            if (m_udp == null)
            {
                Plugin.Log.LogError("[Dev] cannot start command server: all ports busy");
                return;
            }
            m_running = true;
            m_thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "CoopDevServer" };
            m_thread.Start();
            Plugin.Log.LogInfo($"[Dev] command server on 127.0.0.1:{Port}");
        }

        private void OnDestroy()
        {
            m_running = false;
            m_udp?.Close();
        }

        private void ReceiveLoop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (m_running)
            {
                try
                {
                    var data = m_udp.Receive(ref any);
                    m_queue.Enqueue((Encoding.UTF8.GetString(data).Trim(), any));
                }
                catch (Exception)
                {
                    if (!m_running)
                    {
                        return;
                    }
                }
            }
        }

        private void Update()
        {
            if (CoopConfig.DevForceWindowed.Value && m_windowCheckAt > 0f && Time.realtimeSinceStartup >= m_windowCheckAt)
            {
                m_windowCheckAt = -1f;
                if (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width != 1280)
                {
                    Screen.SetResolution(1280, 720, FullScreenMode.Windowed);
                }
            }

            // Скриптовый ввод привязываем к актуальным id Rewired-игроков
            var p1 = CoopInput.P1Input;
            var p2 = CoopInput.P2Input;
            if (p1 != null)
            {
                InputOverrides.Add(p1.id, m_scripted[0]);
            }
            if (p2 != null)
            {
                InputOverrides.Add(p2.id, m_scripted[1]);
            }

            if (s_god)
            {
                foreach (var w in GamePlayer.VonWansts)
                {
                    if (w != null && !w.Stats.Dead)
                    {
                        w.Stats.IsInvulnerable = true;
                    }
                }
            }

            for (int i = 0; i < 2; i++)
            {
                if (m_walkTarget[i] == null)
                {
                    continue;
                }
                var w = GamePlayer.VonWansts[i];
                float dx = m_walkTarget[i].Value - (w != null ? w.transform.position.x : 0f);
                if (w == null || Mathf.Abs(dx) < 0.15f || Time.realtimeSinceStartup > m_walkDeadline[i])
                {
                    m_walkTarget[i] = null;
                    m_scripted[i].Release(2);
                    m_scripted[i].Release(23);
                    continue;
                }
                m_scripted[i].Release(dx < 0f ? 23 : 2);
                m_scripted[i].Hold(dx < 0f ? 2 : 23, 0.1f);
            }

            while (m_queue.TryDequeue(out var item))
            {
                string reply;
                try
                {
                    reply = Execute(item.cmd);
                }
                catch (Exception e)
                {
                    reply = "ERROR " + e;
                }
                Plugin.Log.LogDebug($"[Dev] > {item.cmd}");
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(reply);
                    m_udp.Send(bytes, Math.Min(bytes.Length, 60000), item.from);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Dev] reply failed: {e.Message}");
                }
            }
        }

        private string Execute(string line)
        {
            var args = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (args.Length == 0)
            {
                return "empty";
            }
            var gm = GameManager.InstanceNoWarnings;

            switch (args[0].ToLowerInvariant())
            {
                case "ping":
                    return $"pong scene={SceneManager.GetActiveScene().name} t={Time.time:F1} frame={Time.frameCount} port={Port} pid={System.Diagnostics.Process.GetCurrentProcess().Id}";

                case "dump":
                    return CoopProbe.Dump("dev");

                case "maps":
                    return CoopProbe.DumpMaps();

                case "log":
                    // log [count] [filter]
                    return LogBuffer.Tail(args.Length > 1 ? int.Parse(args[1]) : 40, args.Length > 2 ? string.Join(" ", args, 2, args.Length - 2) : null);

                case "shot":
                {
                    string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "shots");
                    Directory.CreateDirectory(dir);
                    string name = args.Length > 1 ? args[1] : DateTime.Now.ToString("HHmmss");
                    string path = Path.Combine(dir, name + ".png");
                    ScreenCapture.CaptureScreenshot(path);
                    return path;
                }

                case "load":
                    switch (args.Length > 1 ? args[1] : "")
                    {
                        case "tower":
                            SceneLoader.LoadMasterScene();
                            return "loading MasterScene";
                        case "village":
                            SceneLoader.LoadVillageRegular();
                            return "loading VillageScene";
                case "menu":
                            SceneLoader.LoadMainMenuScene();
                            return "loading MainMenu";
                        default:
                            return "usage: load tower|village|menu";
                    }

                case "net":
                {
                    // net host direct|steam | net join <ip> [port] | net joinsteam <lobby> | net stop | net drop | net status
                    var s = Net.NetSession.Instance;
                    string sub = args.Length > 1 ? args[1] : "status";
                    switch (sub)
                    {
                        case "host":
                            s.StartHost(args.Length > 2 && args[2] == "steam" ? Net.NetTransportKind.Steam : Net.NetTransportKind.Direct);
                            break;
                        case "join":
                            s.StartJoinDirect(args.Length > 2 ? args[2] : "127.0.0.1", args.Length > 3 ? int.Parse(args[3]) : CoopConfig.NetPort.Value);
                            break;
                        case "joinsteam":
                            s.StartJoinSteam(args.Length > 2 ? ulong.Parse(args[2]) : 0);
                            break;
                        case "stop":
                            s.Stop("dev");
                            break;
                        case "drop":
                            // имитация обрыва связи без Bye
                            s.Transport?.Disconnect();
                            break;
                        case "continue":
                            s.ContinueAlone();
                            break;
                        case "restart":
                            s.Client?.RequestRestart();
                            break;
                        case "restartok":
                            s.Host?.RestartRun();
                            break;
                        case "resync":
                            if (s.Host != null)
                            {
                                s.Host.ForceResync();
                            }
                            else
                            {
                                s.Client?.RequestResync("dev");
                            }
                            break;
                        case "sim":
                            // net sim <мс в одну сторону> — имитация пинга на Direct (ставить на обеих копиях)
                            if (s.Transport is Net.LnlTransport lnl)
                            {
                                lnl.SimulateLatency(args.Length > 2 ? int.Parse(args[2]) : 25);
                            }
                            break;
                        case "desync":
                            return s.Client != null ? s.Client.DebugDesync(args.Length > 2 ? int.Parse(args[2]) : 3) : "client only";
                    }
                    return $"role={Net.NetSession.Role} state={s.CurrentState} status='{s.StatusText}' transport='{s.Transport?.Status}' ping={s.Transport?.PingMs} lag={(s.Client != null ? s.Client.InputLagMs.ToString("F0") : "-")} regBatches={(s.Host != null ? s.Host.m_registerBatches : s.Client != null ? s.Client.m_registerBatches : 0)} timeScale={Time.timeScale} kbOut={s.KbOut:F1} kbIn={s.KbIn:F1}{(s.Transport is Net.SteamSidecarTransport st && st.Stats.Valid ? " steam: " + st.Stats.Short() : "")}";
                }

                case "free":
                {
                    // снять замки движения/ввода с обоих игроков (автотест: бочка с порохом, прерванные катсцены)
                    int n = 0;
                    foreach (var w in GamePlayer.VonWansts)
                    {
                        if (w == null) continue;
                        w.Stats.AllowMovement = true; w.Stats.InputAllowed = true; w.Utilities.AllowFalling = true; w.Utilities.UnFreeze(); n++;
                    }
                    if (CameraManager.Instance != null) { CameraManager.Instance.PlayingVideo = false; }
                    return $"freed {n}";
                }

                case "unlock":
                {
                    // поднять решётки всех дверей текущей комнаты (запертые комнаты с рычагами/дробилками в автотесте)
                    var room = gm != null ? gm.CurrentRoom : null;
                    if (room == null)
                    {
                        return "no room";
                    }
                    int n = 0;
                    foreach (var d in room.GetComponentsInChildren<LuckyTower.RoomGeneration.Door>(true))
                    {
                        try { d.PortcullisDown(false); d.enabled = true; n++; } catch { }
                    }
                    return $"unlocked {n} doors";
                }

                case "rooms":
                {
                    // все комнаты башни (сверка генерации хост/клиент)
                    var list = new System.Collections.Generic.List<string>();
                    foreach (var r in UnityEngine.Object.FindObjectsByType<LuckyTower.RoomGeneration.Room>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    {
                        list.Add($"{r.name} [{r.RoomType}] seg={r.SegmentID} active={r.gameObject.activeSelf}");
                    }
                    list.Sort(string.CompareOrdinal);
                    return list.Count > 0 ? string.Join("\n", list) : "no rooms";
                }

                case "ent":
                {
                    // ent <id> — путь и активность сущности по цепочке родителей
                    var s = Net.NetSession.Instance;
                    if (args.Length < 2 || !int.TryParse(args[1], out var entId))
                    {
                        return "usage: ent <id>";
                    }
                    var ent = s.Host != null ? s.Host.EntityById(entId) : s.Client?.EntityById(entId);
                    return ent == null ? "unknown id" : ent.Describe(gm != null && gm.CurrentRoom != null ? gm.CurrentRoom.transform : null);
                }

                case "netents":
                {
                    var s = Net.NetSession.Instance;
                    return s.Host != null ? s.Host.DebugList() : s.Client != null ? s.Client.DebugList() : "no session";
                }

                case "village":
                {
                    // village | village house N
                    var v = DataManager.Instance?.VillageData;
                    if (v == null)
                    {
                        return "no village data";
                    }
                    if (args.Length > 2 && args[1] == "house")
                    {
                        v.HouseLevel = int.Parse(args[2]);
                    }
                    return $"HouseLevel={v.HouseLevel} SpawnThrone={v.SpawnThrone} difficulty={DataManager.Instance.GameData?.Difficulty}";
                }

                case "god":
                    s_god = args.Length < 2 || args[1] != "off";
                    return $"god={s_god}";

                case "p2kb":
                    return CoopInput.EnsureKeyboardMapDebug();

                case "loadkeep":
                    // как переход через дверь со сменой сцены: без DestroyAll
                    SceneLoader.LoadSceneByName(args.Length > 1 && args[1] == "village" ? LuckyTower.Village.VillageManager.LoadScene.VillageRegular : LuckyTower.Village.VillageManager.LoadScene.MasterScene);
                    return "loading (keep players)";

                case "spawn":
                {
                    var p1 = CoopPlayers.P1;
                    if (p1 == null)
                    {
                        return "no P1";
                    }
                    var p2 = CoopPlayers.SpawnP2(p1.transform.position + Vector3.right * 0.8f);
                    return p2 != null ? $"spawned {p2.name} slot={p2.Stats.PlayerNumber}" : "spawn failed";
                }

                case "despawn":
                    CoopPlayers.Despawn();
                    return "ok";

                case "hold":
                case "tap":
                {
                    // hold p1|p2 action [seconds] ; tap p1|p2 action
                    if (args.Length < 3)
                    {
                        return "usage: hold p1|p2 <action> [sec] | tap p1|p2 <action>";
                    }
                    int idx = args[1] == "p2" ? 1 : 0;
                    if (!ActionNames.Map.TryGetValue(args[2].ToLowerInvariant(), out int actionId))
                    {
                        return "unknown action; known: " + string.Join(",", ActionNames.Map.Keys);
                    }
                    float sec = args[0] == "tap" ? 0.12f : (args.Length > 3 ? float.Parse(args[3], CultureInfo.InvariantCulture) : 0.5f);
                    m_scripted[idx].Hold(actionId, sec);
                    return $"{args[1]} {args[2]}({actionId}) for {sec:F2}s";
                }

                case "release":
                    m_scripted[args.Length > 1 && args[1] == "p2" ? 1 : 0].ReleaseAll();
                    return "ok";

                case "where":
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < 2; i++)
                    {
                        var w = GamePlayer.VonWansts[i];
                        if (w != null)
                        {
                            sb.AppendLine($"P{i + 1} {w.name} pos={w.transform.position} hp={w.Stats.Health} dead={w.Stats.Dead} vel={w.Components.Rigidbody.linearVelocity} dir={w.Stats.Direction} gfx={(CoopVisibility.IsHidden(w) ? "off" : "on")} teeth={w.Stats.TeethCount} input={w.Stats.InputAllowed} move={w.Stats.AllowMovement} speed={w.Stats.MovementSpeedMultiplier:F2} door={w.Stats.CommandPattern?.CanEnterDoor} target={w.Stats.HasMovementTarget}");
                        }
                    }
                    if (gm != null && gm.CurrentRoom != null)
                    {
                        sb.AppendLine($"room={gm.CurrentRoom.name}");
                    }
                    return sb.ToString();
                }

                case "doors":
                {
                    var sb = new StringBuilder();
                    foreach (var d in FindObjectsByType<LuckyTower.RoomGeneration.Door>(FindObjectsSortMode.None))
                    {
                        if (!d.isActiveAndEnabled)
                        {
                            continue;
                        }
                        string Safe(Func<string> f)
                        {
                            try
                            {
                                return f();
                            }
                            catch
                            {
                                return "?";
                            }
                        }
                        sb.AppendLine($"{d.name} center={(d.DoorCenter != null ? d.DoorCenter.position : d.transform.position)} room={Safe(() => d.Room != null ? d.Room.name : "-")} " +
                                      $"target={Safe(() => d.TargetRoom != null ? d.TargetRoom.name : "-")} scene={d.LoadSceneName} enabled={d.enabled} opened={d.Opened} lock={d.LockOnEnable} current={Safe(() => (gm != null && d.Room == gm.CurrentRoom).ToString())} path={Safe(() => d.Room != null ? Net.NetPaths.Pretty(Net.NetPaths.PathOf(d.transform, d.Room.transform)) : "-")}");
                    }
                    return sb.Length > 0 ? sb.ToString() : "no doors";
                }

                case "walkto":
                {
                    // walkto p1|p2 x
                    int idx = args[1] == "p2" ? 1 : 0;
                    m_walkTarget[idx] = float.Parse(args[2], CultureInfo.InvariantCulture);
                    m_walkDeadline[idx] = Time.realtimeSinceStartup + 10f;
                    return $"{args[1]} walking to x={m_walkTarget[idx]}";
                }

                case "kill":
                {
                    var target = args.Length > 1 && args[1] == "p2" ? (GamePlayer)CoopPlayers.P2 : CoopPlayers.P1;
                    if (target == null)
                    {
                        return "no such player";
                    }
                    target.Actions.Die();
                    return $"killed {target.name}";
                }

                case "items":
                {
                    var sb = new StringBuilder();
                    foreach (var it in FindObjectsByType<PickupItem>(FindObjectsSortMode.None))
                    {
                        if (!it.isActiveAndEnabled || (gm != null && gm.CurrentRoom != null && Vector2.Distance(it.transform.position, gm.CurrentRoom.transform.position) > 25f))
                        {
                            continue;
                        }
                        sb.AppendLine($"{it.ItemName} pos={it.transform.position} parent={(it.transform.parent != null ? it.transform.parent.name : "-")}");
                    }
                    return sb.Length > 0 ? sb.ToString() : "no items nearby";
                }

                case "spawnitem":
                {
                    // spawnitem <Name> [p1|p2] [dx]
                    var who = args.Length > 2 && args[2] == "p2" ? (GamePlayer)CoopPlayers.P2 : CoopPlayers.P1;
                    var prefab = gm != null ? gm.ItemLibrary.GetItem(args[1]) : null;
                    if (who == null || prefab == null)
                    {
                        return "no player or unknown item";
                    }
                    float dx = args.Length > 3 ? float.Parse(args[3], CultureInfo.InvariantCulture) : 0.6f;
                    var it = Instantiate(prefab, who.transform.position + new Vector3(dx, 0.5f, 0f), Quaternion.identity, gm.CurrentRoom.transform);
                    return $"spawned {it.name} at {it.transform.position}";
                }

                case "exits":
                {
                    // боковые проходы и люки текущей комнаты (для автотеста маршрута)
                    var sb = new StringBuilder();
                    var room = gm != null ? gm.CurrentRoom : null;
                    if (room == null)
                    {
                        return "no room";
                    }
                    foreach (var s in room.GetComponentsInChildren<EnterDoorSideTrigger>(false))
                    {
                        string target = "?";
                        try { target = s.TargetRoom != null ? s.TargetRoom.name : "null"; } catch { }
                        sb.AppendLine($"Side {s.name} center=({s.transform.position.x:F2}, {s.transform.position.y:F2}) target={target}");
                    }
                    foreach (var s in room.GetComponentsInChildren<StartNewSegmentTrigger>(false))
                    {
                        string target = "?";
                        try { target = s.TargetRoom != null ? s.TargetRoom.name : "null"; } catch { }
                        sb.AppendLine($"Hatch {s.name} center=({s.transform.position.x:F2}, {s.transform.position.y:F2}) target={target}");
                    }
                    return sb.Length > 0 ? sb.ToString() : "no exits";
                }

                case "killall":
                {
                    // убить всех врагов текущей комнаты (проверка синхронизации трупов)
                    var room = gm != null ? gm.CurrentRoom : null;
                    if (room == null)
                    {
                        return "no room";
                    }
                    int n = 0;
                    foreach (var en in room.GetComponentsInChildren<LuckyTower.CharacterSystem.Enemy>(false))
                    {
                        if (en.Stats.Dead)
                        {
                            continue;
                        }
                        try
                        {
                            en.Actions.Die();
                            n++;
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogWarning($"[Dev] killall {en.name}: {e.Message}");
                        }
                    }
                    return $"killed {n}";
                }

                case "transform":
                {
                    // transform p1|p2 monster|wanst|evelius — превращение (проверка смены тела)
                    var w = GamePlayer.VonWansts[args.Length > 1 && args[1] == "p2" ? 1 : 0];
                    if (w == null)
                    {
                        return "no player";
                    }
                    var enumType = HarmonyLib.AccessTools.TypeByName("TransformTo") ?? HarmonyLib.AccessTools.TypeByName("LuckyTower.CharacterSystem.TransformTo") ?? HarmonyLib.AccessTools.Inner(typeof(LuckyTower.CharacterSystem.CharacterAction), "TransformTo");
                    if (enumType == null)
                    {
                        return "TransformTo enum not found";
                    }
                    string name = args.Length > 2 ? args[2] : "monster";
                    object target = Enum.Parse(enumType, name.Substring(0, 1).ToUpperInvariant() + name.Substring(1), true);
                    HarmonyLib.AccessTools.Method(typeof(LuckyTower.CharacterSystem.CharacterAction), "Transform", new[] { enumType }).Invoke(w.Actions, new[] { target });
                    return $"{w.name} transforming to {target}";
                }

                case "ls":
                {
                    // ls <путь от корня комнаты> — дети объекта (сверка иерархии хост/клиент); без пути — корень
                    var room = gm != null ? gm.CurrentRoom : null;
                    if (room == null)
                    {
                        return "no room";
                    }
                    string path = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "";
                    // имена могут содержать «/» («Window 6/4»): тогда разделитель пути — «|»
                    path = (path.Contains("|") ? path.Replace('|', '\u001f') : path.Replace('/', '\u001f')).Replace('#', '\u001e');
                    var t = path.Length == 0 ? room.transform : Net.NetPaths.Find(room.transform, path);
                    if (t == null)
                    {
                        // ищем самый глубокий существующий префикс
                        var parts = path.Split('\u001f');
                        for (int cut = parts.Length - 1; cut >= 1 && t == null; cut--)
                        {
                            t = Net.NetPaths.Find(room.transform, string.Join(Net.NetPaths.Sep, parts, 0, cut));
                            if (t != null)
                            {
                                path = Net.NetPaths.Pretty(string.Join(Net.NetPaths.Sep, parts, 0, cut)) + "   (deepest existing)";
                            }
                        }
                        if (t == null)
                        {
                            return "not found";
                        }
                    }
                    var sb = new StringBuilder($"{Net.NetPaths.Pretty(path)}: {t.childCount} children\n");
                    for (int i = 0; i < t.childCount && i < 60; i++)
                    {
                        var ch = t.GetChild(i);
                        sb.Append($"  {ch.name}{(ch.gameObject.activeSelf ? "" : " (off)")} [{ch.childCount}]\n");
                    }
                    return sb.ToString();
                }

                case "roomstat":
                {
                    // размер иерархии текущей комнаты и её верхние дети (сверка генерации хост/клиент)
                    var room = gm != null ? gm.CurrentRoom : null;
                    if (room == null)
                    {
                        return "no room";
                    }
                    var all = room.GetComponentsInChildren<Transform>(true);
                    var sb = new StringBuilder($"{room.name}: {all.Length} transforms, {room.transform.childCount} top children\n");
                    for (int i = 0; i < room.transform.childCount && i < 40; i++)
                    {
                        var ch = room.transform.GetChild(i);
                        sb.Append($"  {ch.name}[{ch.GetComponentsInChildren<Transform>(true).Length}]{(ch.gameObject.activeSelf ? "" : " (off)")}\n");
                    }
                    return sb.ToString();
                }

                case "cam":
                {
                    // кто для мода P1/P2, кто для игры «игрок», куда смотрит камера
                    string D(GamePlayer p) => p == null ? "null" : $"{p.name}#{p.GetInstanceID()} dead={p.Stats.Dead} hidden={CoopVisibility.IsHidden(p)} pos={p.transform.position:F1}";
                    var cm = CoopCamera.CameraManagerSilent;
                    var follow = cm != null && cm.VirtualCamera != null ? cm.VirtualCamera.Follow : null;
                    return $"P1: {D(CoopPlayers.P1)}\nP2: {D(CoopPlayers.P2)}\ngm.Player: {D(gm != null ? gm.Player : null)}\nVonWansts[0]: {D(GamePlayer.VonWansts[0])}\nVonWansts[1]: {D(GamePlayer.VonWansts[1])}\n" +
                           $"camTarget={(CoopCamera.Target != null ? CoopCamera.Target.position.ToString("F1") : "-")} follow={(follow != null ? follow.name + "/" + (follow.GetComponentInParent<GamePlayer>()?.name ?? "-") : "null")} cam={(cm != null && cm.MainCamera != null ? cm.MainCamera.transform.position.ToString("F1") : "-")}";
                }

                case "traps":
                {
                    // ловушки текущей комнаты: тип, имя, позиция, активность
                    var sb = new StringBuilder();
                    var room = gm != null ? gm.CurrentRoom : null;
                    if (room == null)
                    {
                        return "no room";
                    }
                    foreach (var t in room.GetComponentsInChildren<Trap>(true))
                    {
                        if (t is LuckyTower.Items.SpecialItem)
                        {
                            continue;
                        }
                        sb.AppendLine($"{t.GetType().Name} {t.name} pos={t.transform.position} active={t.gameObject.activeInHierarchy} enabled={t.enabled} cleared={t.Cleared}");
                    }
                    return sb.Length > 0 ? sb.ToString() : "no traps";
                }

                case "npcs":
                {
                    // персонажи-неврaги текущей комнаты (для теста окна диалога)
                    var sb = new StringBuilder();
                    var room = gm != null ? gm.CurrentRoom : null;
                    foreach (var ch in FindObjectsByType<LuckyTower.CharacterSystem.Character>(FindObjectsSortMode.None))
                    {
                        if (ch is GamePlayer || ch.Stats == null || ch.Stats.CharacterType == LuckyTower.CharacterSystem.CharacterTypes.Enemy || !ch.isActiveAndEnabled)
                        {
                            continue;
                        }
                        if (room != null && Vector2.Distance(ch.transform.position, room.transform.position) > 40f)
                        {
                            continue;
                        }
                        sb.AppendLine($"{ch.name} type={ch.Stats.CharacterType} pos={ch.transform.position} dialogue={(ch.GetComponentInChildren<DialogueTrigger>(true) != null)}");
                    }
                    return sb.Length > 0 ? sb.ToString() : "no npcs";
                }

                case "lights":
                {
                    // lights — состояние ловушек «свет погас» в текущей комнате; lights out|on — вызвать (хост)
                    var room = gm != null ? gm.CurrentRoom : null;
                    if (room == null)
                    {
                        return "no room";
                    }
                    var traps = room.GetComponentsInChildren<LuckyTower.Traps.TrapLightsOut>(true);
                    if (traps.Length == 0)
                    {
                        return "no lights-out trap in room";
                    }
                    var sb = new StringBuilder();
                    var outField = HarmonyLib.AccessTools.Field(typeof(LuckyTower.Traps.TrapLightsOut), "m_lightsOut");
                    var planeField = HarmonyLib.AccessTools.Field(typeof(LuckyTower.Traps.TrapLightsOut), "lightsOutPlane");
                    foreach (var t in traps)
                    {
                        if (args.Length > 1 && args[1] == "out")
                        {
                            HarmonyLib.AccessTools.Method(typeof(LuckyTower.Traps.TrapLightsOut), "LightsOut").Invoke(t, null);
                        }
                        else if (args.Length > 1 && args[1] == "on")
                        {
                            HarmonyLib.AccessTools.Method(typeof(LuckyTower.Traps.TrapLightsOut), "OnPlayerExits").Invoke(t, new object[] { false });
                        }
                        var plane = planeField?.GetValue(t) as GameObject;
                        sb.AppendLine($"{t.name}: lightsOut={outField?.GetValue(t)} plane={(plane != null ? plane.activeSelf.ToString() : "null")} enabled={t.enabled}");
                    }
                    return sb.ToString();
                }

                case "rend":
                {
                    // rend <слот 1-4> — все рендеры персонажа: включён ли, активен ли объект, порядок отрисовки
                    var w = GamePlayer.VonWansts[args.Length > 1 ? int.Parse(args[1]) - 1 : 0];
                    if (w == null)
                    {
                        return "no player";
                    }
                    var sb = new StringBuilder($"{w.name} scale={w.transform.localScale} hidden={CoopVisibility.IsHidden(w)}\n");
                    foreach (var r in w.GetComponentsInChildren<Renderer>(true))
                    {
                        var sr = r as SpriteRenderer;
                        sb.AppendLine($"  {(r.enabled ? "+" : "-")}{(r.gameObject.activeInHierarchy ? "A" : "a")} {r.name} order={r.sortingOrder} z={r.transform.position.z:F2}{(sr != null ? $" sprite={(sr.sprite != null ? sr.sprite.name : "null")} a={sr.color.a:F1}" : "")}");
                    }
                    return sb.ToString();
                }

                case "tp":
                {
                    // tp p1|p2 x y — переставить персонажа (автотест маршрута по комнатам)
                    var w = GamePlayer.VonWansts[args.Length > 1 && args[1] == "p2" ? 1 : 0];
                    if (w == null || args.Length < 4)
                    {
                        return "usage: tp p1|p2 x y";
                    }
                    var to = new Vector3(float.Parse(args[2], CultureInfo.InvariantCulture), float.Parse(args[3], CultureInfo.InvariantCulture), w.transform.position.z);
                    w.Components.Rigidbody.linearVelocity = Vector2.zero;
                    w.transform.position = to;
                    return $"{w.name} -> {to}";
                }

                case "hurt":
                {
                    // hurt p1|p2 — выбить зуб (проверка передачи ХП)
                    var w = GamePlayer.VonWansts[args.Length > 1 && args[1] == "p2" ? 1 : 0];
                    if (w == null)
                    {
                        return "no player";
                    }
                    var teeth = w.Stats.TeethInventory;
                    if (teeth.Count > 0)
                    {
                        teeth.RemoveAt(teeth.Count - 1);
                    }
                    w.Stats.ColorInTeeth();
                    return $"{w.name} teeth={w.Stats.TeethCount}";
                }

                case "menu":
                {
                    // menu <0|1|2> — открыть вкладку меню F7, menu off — закрыть
                    var menu = FindFirstObjectByType<Net.NetMenu>();
                    if (menu == null)
                    {
                        return "no menu";
                    }
                    menu.DevShow(args.Length > 1 && int.TryParse(args[1], out int tab) ? tab : -1);
                    return "ok";
                }

                case "anim":
                {
                    // anim [номер слота 1-4]: все аниматоры персонажа — слои, состояния, ненулевые параметры
                    var sb = new StringBuilder();
                    for (int i = 0; i < GamePlayer.VonWansts.Length; i++)
                    {
                        var pl = GamePlayer.VonWansts[i];
                        if (pl == null || (args.Length > 1 && args[1] != (i + 1).ToString()))
                        {
                            continue;
                        }
                        sb.AppendLine($"== slot {i + 1} {pl.name}");
                        foreach (var an in pl.GetComponentsInChildren<Animator>(true))
                        {
                            sb.Append($"  [{an.name}] en={an.isActiveAndEnabled} layers={an.layerCount}:");
                            for (int l = 0; l < an.layerCount; l++)
                            {
                                var st = an.GetCurrentAnimatorStateInfo(l);
                                sb.Append($" L{l}={st.shortNameHash}/{an.GetLayerWeight(l):F1}/{Mathf.Repeat(st.normalizedTime, 1f):F2}{(an.IsInTransition(l) ? "*" : "")}");
                            }
                            sb.AppendLine();
                            if (an.runtimeAnimatorController == null)
                            {
                                continue;
                            }
                            sb.Append("    params:");
                            foreach (var p in an.parameters)
                            {
                                switch (p.type)
                                {
                                    case AnimatorControllerParameterType.Bool:
                                        if (an.GetBool(p.nameHash))
                                        {
                                            sb.Append($" {p.name}");
                                        }
                                        break;
                                    case AnimatorControllerParameterType.Float:
                                        float fv = an.GetFloat(p.nameHash);
                                        if (Mathf.Abs(fv) > 0.001f)
                                        {
                                            sb.Append($" {p.name}={fv:F2}");
                                        }
                                        break;
                                    case AnimatorControllerParameterType.Int:
                                        int iv = an.GetInteger(p.nameHash);
                                        if (iv != 0)
                                        {
                                            sb.Append($" {p.name}={iv}");
                                        }
                                        break;
                                }
                            }
                            sb.AppendLine($" (total {an.parameters.Length})");
                        }
                    }
                    return sb.Length > 0 ? sb.ToString() : "no players";
                }

                case "hands":
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < 2; i++)
                    {
                        var w = GamePlayer.VonWansts[i];
                        if (w == null)
                        {
                            continue;
                        }
                        var s = w.Stats;
                        sb.AppendLine($"P{i + 1} main={(s.HasWeapon ? s.WeaponItem.ItemName : "-")} second={(s.ItemInSecondHand != null ? s.ItemInSecondHand.ItemName : "-")} " +
                                      $"belt={(s.CurrentGoldSack != null ? s.CurrentGoldSack.CurrentGold.ToString() : "-")} gold={s.CurrentGoldCount()} armor={(s.CurrentArmor != null ? s.CurrentArmor.name : "-")}");
                    }
                    return sb.ToString();
                }

                case "addkills":
                    CoopLife.DevAddKills(args.Length > 1 ? int.Parse(args[1]) : 1);
                    return "ok";

                case "enemies":
                {
                    var sb = new StringBuilder();
                    if (gm == null || gm.CurrentRoom == null)
                    {
                        return "no room";
                    }
                    foreach (var c in gm.CurrentRoom.GetComponentsInChildren<LuckyTower.CharacterSystem.Character>(false))
                    {
                        if (c.Stats.CharacterType != LuckyTower.CharacterSystem.CharacterTypes.Enemy && !c.Stats.Evil)
                        {
                            continue;
                        }
                        sb.AppendLine($"{c.name} pos={c.transform.position} hp={c.Stats.Health} dead={c.Stats.Dead} dir={c.Stats.Direction} active={c.isActiveAndEnabled}");
                    }
                    return sb.Length > 0 ? sb.ToString() : "no enemies nearby";
                }

                case "timescale":
                    Time.timeScale = float.Parse(args[1], CultureInfo.InvariantCulture);
                    return $"timeScale={Time.timeScale}";

                case "window":
                    Screen.SetResolution(1280, 720, FullScreenMode.Windowed);
                    return "windowed 1280x720";

                default:
                    return "unknown command: " + args[0];
            }
        }
    }
}
