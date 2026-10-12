using System.Linq;
using Rewired;
using UnityEngine;

namespace LuckyTowerCoop.Coop
{
    /// <summary>
    /// Ввод P2 через Rewired.
    /// В игре настроен один Rewired-игрок, а InputManager инициализируется раньше BepInEx,
    /// поэтому P2 получает системного игрока Rewired: он уже существует, игрой почти не используется,
    /// и ему можно назначить геймпад и загрузить раскладки без пересоздания Rewired
    /// (ResetAll сломал бы сохранённые игрой ссылки на Rewired.Player).
    /// </summary>
    internal static class CoopInput
    {
        private const int CategoryDefault = 0;
        private const int CategoryMenu = 1;
        private const int CategoryInGame = 2;
        private const int LayoutDefault = 0;

        private static bool s_hooked;
        private static bool s_dirty = true;

        public static Player P1Input => ReInput.isReady && ReInput.players.playerCount > 0 ? ReInput.players.Players[0] : null;

        public static Player P2Input
        {
            get
            {
                if (!ReInput.isReady)
                {
                    return null;
                }
                return ReInput.players.playerCount > 1 ? ReInput.players.Players[1] : ReInput.players.GetSystemPlayer();
            }
        }

        public static void Tick()
        {
            if (!ReInput.isReady)
            {
                s_hooked = false;
                return;
            }

            if (!s_hooked)
            {
                s_hooked = true;
                s_dirty = true;
                ReInput.ControllerConnectedEvent += _ => s_dirty = true;
                ReInput.ControllerDisconnectedEvent += _ => s_dirty = true;
                Plugin.Log.LogInfo($"[Input] Rewired ready, players: {ReInput.players.playerCount}, P2 uses '{P2Input?.name}' (id {P2Input?.id})");
            }

            if (s_dirty)
            {
                s_dirty = false;
                AssignControllers();
            }

            // Игра (менеджер раскладок, загрузка сохранённых раскладок) может убрать карты у системного игрока — восстанавливаем
            if (Time.unscaledTime >= s_nextMapCheck)
            {
                s_nextMapCheck = Time.unscaledTime + 1f;
                EnsureP2Maps();
            }
        }

        private static float s_nextMapCheck;

        private static void EnsureP2Maps()
        {
            var p2 = P2Input;
            if (p2 == null)
            {
                return;
            }
            if (Net.NetSession.Role != Net.NetRole.None)
            {
                // онлайн на одном ПК: игра может вернуть своему игроку «чужие» устройства — следим
                if (Net.NetLaunch.Active && !OnlineDevicesOk())
                {
                    s_dirty = true;
                }
                return;
            }
            if (CoopConfig.P2Device.Value == P2DeviceMode.Keyboard)
            {
                if (p2.controllers.maps.GetMap(ControllerType.Keyboard, 0, CategoryInGame, LayoutDefault) == null)
                {
                    EnsureKeyboardMap(p2);
                }
                return;
            }
            foreach (var joy in p2.controllers.Joysticks)
            {
                if (p2.controllers.maps.GetMap(ControllerType.Joystick, joy.id, CategoryInGame, LayoutDefault) == null)
                {
                    EnsureJoystickMaps(p2, joy);
                    Plugin.Log.LogInfo($"[Input] P2 joystick maps restored for {joy.name}");
                }
            }
        }

        public static void MarkDirty() => s_dirty = true;

        /// <summary>
        /// Какие устройства нужны своему игроку в онлайне. Обычно — все. Две копии на одном ПК (--coop-device):
        /// клавиатура и мышь — только у окна в фокусе; геймпад — у копии в режиме gamepad всегда, в режиме all — только в фокусе.
        /// </summary>
        private static void WantedOnline(out bool keyboard, out bool pads)
        {
            if (!Net.NetLaunch.Active)
            {
                keyboard = pads = true;
                return;
            }
            var mode = Net.NetLaunch.EffectiveDevice();
            bool focused = Application.isFocused;
            keyboard = focused && mode != Net.NetLaunch.DeviceMode.Gamepad;
            pads = mode == Net.NetLaunch.DeviceMode.Gamepad || (mode == Net.NetLaunch.DeviceMode.All && focused);
        }

        private static bool OnlineDevicesOk()
        {
            var p1 = P1Input;
            if (p1 == null)
            {
                return true;
            }
            WantedOnline(out bool keyboard, out bool pads);
            return p1.controllers.hasKeyboard == keyboard && p1.controllers.joystickCount == (pads ? ReInput.controllers.joystickCount : 0);
        }

        private static void AssignControllers()
        {
            var p1 = P1Input;
            var p2 = P2Input;
            if (p1 == null || p2 == null)
            {
                return;
            }

            var joysticks = ReInput.controllers.Joysticks;
            if (Net.NetSession.Role != Net.NetRole.None)
            {
                // онлайн: локальные устройства — своему игроку; второй персонаж на хосте управляется по сети.
                WantedOnline(out bool keyboard, out bool pads);
                foreach (var j in joysticks)
                {
                    if (pads)
                    {
                        p1.controllers.AddController(j, removeFromOtherPlayers: true);
                    }
                    else
                    {
                        p1.controllers.RemoveController(j);
                    }
                }
                p1.controllers.hasKeyboard = keyboard;
                if (Net.NetLaunch.Active)
                {
                    p1.controllers.hasMouse = keyboard;
                    // Rewired без фокуса глушит и подмешанный сетевой ввод второго персонажа — фокус отслеживаем сами (см. выше)
                    ReInput.configuration.ignoreInputWhenAppNotInFocus = false;
                }
                RemoveKeyboardMap(p2);
                Plugin.Log.LogInfo($"[Input] online{(Net.NetLaunch.Active ? $" ({Net.NetLaunch.EffectiveDevice()}, focus={Application.isFocused})" : "")}: keyboard={keyboard}, gamepads=[{string.Join(", ", p1.controllers.Joysticks.Select(j => j.name))}] -> local player");
                return;
            }
            if (CoopConfig.P2Device.Value == P2DeviceMode.Gamepad)
            {
                // P2 — последний подключённый геймпад, остальные — P1.
                var p2Joy = joysticks.Count > 0 ? joysticks[joysticks.Count - 1] : null;
                foreach (var j in joysticks)
                {
                    (j == p2Joy ? p2 : p1).controllers.AddController(j, removeFromOtherPlayers: true);
                }
                if (p2Joy != null)
                {
                    EnsureJoystickMaps(p2, p2Joy);
                }
                RemoveKeyboardMap(p2);
            }
            else
            {
                foreach (var j in joysticks)
                {
                    p1.controllers.AddController(j, removeFromOtherPlayers: true);
                }
                p2.controllers.hasKeyboard = true;
                EnsureKeyboardMap(p2);
            }

            Plugin.Log.LogInfo($"[Input] mode={CoopConfig.P2Device.Value} | P1 kb={p1.controllers.hasKeyboard} joy=[{string.Join(", ", p1.controllers.Joysticks.Select(j => j.name))}] | " +
                               $"P2('{p2.name}') joy=[{string.Join(", ", p2.controllers.Joysticks.Select(j => j.name))}]");
        }

        private static void EnsureJoystickMaps(Player p, Joystick joy)
        {
            foreach (int category in new[] { CategoryDefault, CategoryMenu, CategoryInGame })
            {
                if (p.controllers.maps.GetMap(ControllerType.Joystick, joy.id, category, LayoutDefault) == null)
                {
                    p.controllers.maps.LoadMap(ControllerType.Joystick, joy.id, category, LayoutDefault, true);
                }
            }
            p.controllers.maps.SetMapsEnabled(true, ControllerType.Joystick, CategoryInGame);
        }

        // Раскладка P2 для режима «оба с клавиатуры». P1 должен играть в схеме «клавиатура + мышь» (WASD).
        private static readonly (int action, KeyCode key)[] s_p2Keys =
        {
            (2, KeyCode.LeftArrow), (23, KeyCode.RightArrow), (21, KeyCode.UpArrow), (22, KeyCode.DownArrow),
            (3, KeyCode.K), (3, KeyCode.Keypad0),
            (6, KeyCode.J), (6, KeyCode.Keypad1),
            (8, KeyCode.L), (8, KeyCode.Keypad2),
            (7, KeyCode.U), (7, KeyCode.Keypad3),
            (9, KeyCode.I), (9, KeyCode.Keypad4),
            (10, KeyCode.O), (10, KeyCode.Keypad5),
            (26, KeyCode.P), (26, KeyCode.Keypad6),
            (13, KeyCode.Alpha7), (14, KeyCode.Alpha8), (15, KeyCode.Alpha9),
        };

        public static string EnsureKeyboardMapDebug() { var p = P2Input; EnsureKeyboardMap(p); var m = p.controllers.maps.GetMap(ControllerType.Keyboard, 0, CategoryInGame, LayoutDefault); return m == null ? "map NULL" : $"map id={m.id} elements={m.AllMaps.Count()} enabled={m.enabled}; all kb maps={p.controllers.maps.GetAllMaps(ControllerType.Keyboard).Count()}"; }

        private static void EnsureKeyboardMap(Player p)
        {
            var map = p.controllers.maps.GetMap(ControllerType.Keyboard, 0, CategoryInGame, LayoutDefault);
            if (map == null)
            {
                p.controllers.maps.AddEmptyMap(ControllerType.Keyboard, 0, CategoryInGame, LayoutDefault);
                map = p.controllers.maps.GetMap(ControllerType.Keyboard, 0, CategoryInGame, LayoutDefault);
            }
            if (map == null)
            {
                Plugin.Log.LogError("[Input] не удалось создать клавиатурную раскладку P2");
                return;
            }
            map.ClearElementMaps();
            foreach (var (action, key) in s_p2Keys)
            {
                map.CreateElementMap(action, Pole.Positive, key, ModifierKeyFlags.None);
            }
            map.enabled = true;
            Plugin.Log.LogInfo($"[Input] P2 keyboard map: {map.AllMaps.Count()} bindings");
        }

        private static void RemoveKeyboardMap(Player p)
        {
            var map = p.controllers.maps.GetMap(ControllerType.Keyboard, 0, CategoryInGame, LayoutDefault);
            if (map != null)
            {
                p.controllers.maps.RemoveMap(ControllerType.Keyboard, 0, map.id);
            }
        }
    }
}
