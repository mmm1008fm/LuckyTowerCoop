using BepInEx.Configuration;

namespace LuckyTowerCoop
{
    public enum P2DeviceMode
    {
        /// <summary>P1: keyboard (+ the other gamepads), P2: the last connected gamepad.</summary>
        Gamepad,
        /// <summary>Both on keyboard: P2 gets a separate layout.</summary>
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
        public static ConfigEntry<bool> NetInterp;

        public static ConfigEntry<bool> DevServer;

        /// <summary>Debug server: from the config or the --coop-devserver parameter.</summary>
        public static bool DevEnabled => DevServer.Value || LuckyTowerCoop.Net.NetLaunch.DevServer;
        public static ConfigEntry<int> DevPort;
        public static ConfigEntry<bool> DevForceWindowed;
        public static ConfigEntry<bool> DevRunInBackground;

        public static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("General", "Enabled", true, "Enable co-op.");
            ShowNames = cfg.Bind("General", "ShowNames", true, "Name tags above characters (online: 'You' and the partner's name; local: 1 and 2).");

            AutoSpawnP2 = cfg.Bind("Spawn", "AutoSpawnP2", true, "Automatically spawn the second player in the tower/village.");
            SpawnInVillage = cfg.Bind("Spawn", "SpawnInVillage", true, "Also spawn the second player in the village.");
            SpawnDelay = cfg.Bind("Spawn", "SpawnDelaySeconds", 1.5f, "Delay after the scene loads before P2 appears.");

            P2Device = cfg.Bind("Input", "P2Device", P2DeviceMode.Gamepad, "What controls the second player: Gamepad or Keyboard (both on keyboard).");

            LeashSeconds = cfg.Bind("Camera", "LeashSeconds", 3f, "Seconds off-screen before P2 is teleported to P1 (0 = disabled).");

            KillsToRevive = cfg.Bind("Revive", "KillsToRevive", 5, new ConfigDescription("How many monsters the living player must kill to revive their partner.", new AcceptableValueRange<int>(1, 100)));
            ReviveOnNewFloor = cfg.Bind("Revive", "ReviveOnNewFloor", true, "Revive the partner on reaching a new floor even if the kill count isn't met.");

            EnemyHealthMultiplier = cfg.Bind("Difficulty", "EnemyHealthMultiplier", 1f, new ConfigDescription("Enemy health multiplier (1 = vanilla, 2 = twice as many hits).", new AcceptableValueRange<float>(0.25f, 10f)));
            ExtraEnemies = cfg.Bind("Difficulty", "ExtraEnemies", 0f, new ConfigDescription("Average number of extra copies of each regular monster (0 = off, 0.5 = +50%, 1 = twice as many).", new AcceptableValueRange<float>(0f, 3f)));

            NetTransport = cfg.Bind("Net", "Transport", LuckyTowerCoop.Net.NetTransportKind.Steam, "Steam: via Spacewar (friends' lobbies); Direct: by IP (LAN/Tailscale).");
            NetPort = cfg.Bind("Net", "DirectPort", 27015, "Host UDP port for Direct.");
            NetJoinAddress = cfg.Bind("Net", "DirectJoinAddress", "127.0.0.1", "Last address used to connect via Direct.");
            NetPauseOnDisconnect = cfg.Bind("Net", "PauseOnDisconnect", true, "The host pauses the game while the partner reconnects.");
            NetReconnectSeconds = cfg.Bind("Net", "AutoContinueSeconds", 0f, "0 = wait paused for the disconnected partner until the host decides; >0 = unpause after this many seconds (the partner can still rejoin).");
            NetMenuKey = cfg.Bind("Net", "MenuKey", UnityEngine.KeyCode.F7, "Online menu key.");
            NetInterp = cfg.Bind("Net", "InterpolationBuffer", true, "Client: show the world slightly in the past and move it between host snapshots over time (smooth on an uneven network). Off = legacy catch-up to the latest snapshot.");
            NetPredict = cfg.Bind("Net", "ClientPrediction", true, "Client: your own character starts moving immediately on key press without waiting for the host (movement prediction).");

            DevServer = cfg.Bind("Dev", "CommandServer", false, "UDP debug command server on 127.0.0.1 (for development).");
            DevPort = cfg.Bind("Dev", "CommandPort", 47800, "Debug server port.");
            DevForceWindowed = cfg.Bind("Dev", "ForceWindowed", false, "Force a 1280x720 window (for development).");
            DevRunInBackground = cfg.Bind("Dev", "RunInBackground", false, "Don't pause the game when unfocused (for development).");
        }
    }
}
