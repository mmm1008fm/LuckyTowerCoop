using BepInEx.Configuration;

namespace LuckyTowerCoop
{
    public enum P2DeviceMode
    {
        /// <summary>P1: клавиатура (+ остальные геймпады), P2: последний подключённый геймпад.</summary>
        Gamepad,
        /// <summary>Оба с клавиатуры: P2 получает отдельную раскладку.</summary>
        Keyboard,
    }

    internal static class CoopConfig
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> ShowNames;

        public static ConfigEntry<bool> AutoSpawnP2;
        public static ConfigEntry<bool> SpawnInVillage;
        public static ConfigEntry<float> SpawnDelay;

        public static ConfigEntry<P2DeviceMode> P2Device;

        public static ConfigEntry<float> LeashSeconds;

        public static ConfigEntry<int> KillsToRevive;
        public static ConfigEntry<bool> ReviveOnNewFloor;

        public static ConfigEntry<float> EnemyHealthMultiplier;
        public static ConfigEntry<float> ExtraEnemies;

        public static ConfigEntry<LuckyTowerCoop.Net.NetTransportKind> NetTransport;
        public static ConfigEntry<int> NetPort;
        public static ConfigEntry<string> NetJoinAddress;
        public static ConfigEntry<bool> NetPauseOnDisconnect;
        public static ConfigEntry<float> NetReconnectSeconds;
        public static ConfigEntry<UnityEngine.KeyCode> NetMenuKey;
        public static ConfigEntry<bool> NetPredict;

        public static ConfigEntry<bool> DevServer;

        /// <summary>Отладочный сервер: из конфига или параметром --coop-devserver.</summary>
        public static bool DevEnabled => DevServer.Value || LuckyTowerCoop.Net.NetLaunch.DevServer;
        public static ConfigEntry<int> DevPort;
        public static ConfigEntry<bool> DevForceWindowed;
        public static ConfigEntry<bool> DevRunInBackground;

        public static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("General", "Enabled", true, "Включить кооп.");
            ShowNames = cfg.Bind("General", "ShowNames", true, "Подписи над персонажами (онлайн: «Вы» и имя напарника, локально: 1 и 2).");

            AutoSpawnP2 = cfg.Bind("Spawn", "AutoSpawnP2", true, "Автоматически создавать второго игрока в башне/деревне.");
            SpawnInVillage = cfg.Bind("Spawn", "SpawnInVillage", true, "Создавать второго игрока и в деревне.");
            SpawnDelay = cfg.Bind("Spawn", "SpawnDelaySeconds", 1.5f, "Задержка после загрузки сцены перед появлением P2.");

            P2Device = cfg.Bind("Input", "P2Device", P2DeviceMode.Gamepad, "Чем управляется второй игрок: Gamepad или Keyboard (оба с клавиатуры).");

            LeashSeconds = cfg.Bind("Camera", "LeashSeconds", 3f, "Через сколько секунд за кадром P2 телепортируется к P1 (0 — выключено).");

            KillsToRevive = cfg.Bind("Revive", "KillsToRevive", 5, new ConfigDescription("Сколько монстров должен убить живой игрок, чтобы воскресить напарника.", new AcceptableValueRange<int>(1, 100)));
            ReviveOnNewFloor = cfg.Bind("Revive", "ReviveOnNewFloor", true, "Воскрешать напарника при переходе на новый этаж, даже если убийств не хватает.");

            EnemyHealthMultiplier = cfg.Bind("Difficulty", "EnemyHealthMultiplier", 1f, new ConfigDescription("Множитель здоровья врагов (1 — как в игре, 2 — вдвое больше ударов).", new AcceptableValueRange<float>(0.25f, 10f)));
            ExtraEnemies = cfg.Bind("Difficulty", "ExtraEnemies", 0f, new ConfigDescription("Сколько в среднем дополнительных копий у каждого обычного монстра (0 — выкл, 0.5 — +50%, 1 — вдвое больше).", new AcceptableValueRange<float>(0f, 3f)));

            NetTransport = cfg.Bind("Net", "Transport", LuckyTowerCoop.Net.NetTransportKind.Steam, "Steam — через Spacewar (лобби друзей), Direct — по IP (LAN/Tailscale).");
            NetPort = cfg.Bind("Net", "DirectPort", 27015, "UDP-порт хоста для Direct.");
            NetJoinAddress = cfg.Bind("Net", "DirectJoinAddress", "127.0.0.1", "Последний адрес для подключения по Direct.");
            NetPauseOnDisconnect = cfg.Bind("Net", "PauseOnDisconnect", true, "Хост ставит игру на паузу, пока напарник переподключается.");
            NetReconnectSeconds = cfg.Bind("Net", "AutoContinueSeconds", 0f, "0 — ждать отключившегося напарника на паузе, пока хост сам не решит; >0 — через столько секунд снять паузу (вернуться он сможет всё равно).");
            NetMenuKey = cfg.Bind("Net", "MenuKey", UnityEngine.KeyCode.F7, "Клавиша меню онлайна.");
            NetPredict = cfg.Bind("Net", "ClientPrediction", true, "Клиент: свой персонаж начинает идти сразу по нажатию, не дожидаясь ответа хоста (предсказание движения).");

            DevServer = cfg.Bind("Dev", "CommandServer", false, "UDP-сервер отладочных команд на 127.0.0.1 (для разработки).");
            DevPort = cfg.Bind("Dev", "CommandPort", 47800, "Порт отладочного сервера.");
            DevForceWindowed = cfg.Bind("Dev", "ForceWindowed", false, "Принудительно окно 1280x720 (для разработки).");
            DevRunInBackground = cfg.Bind("Dev", "RunInBackground", false, "Не ставить игру на паузу без фокуса (для разработки).");
        }
    }
}
