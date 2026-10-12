using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LuckyTowerCoop.Coop;
using LuckyTowerCoop.Dev;

namespace LuckyTowerCoop
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "local.luckytower.coop";
        public const string Name = "LuckyTowerCoop";
        public const string Version = "0.3.7";

        internal static ManualLogSource Log;
        internal static Harmony Harmony;

        private void Awake()
        {
            Log = Logger;
            CoopConfig.Bind(Config);
            Net.NetLaunch.Parse();
            if (CoopConfig.DevEnabled)
            {
                BepInEx.Logging.Logger.Listeners.Add(LogBuffer.Instance);
            }

            Harmony = new Harmony(Guid);
            Harmony.PatchAll(typeof(Plugin).Assembly);
            foreach (var m in Harmony.GetPatchedMethods())
            {
                Log.LogDebug($"patched: {m.DeclaringType?.FullName}.{m.Name}");
            }

            gameObject.AddComponent<CoopManager>();
            gameObject.AddComponent<CoopCamera>();
            gameObject.AddComponent<CoopTransitions>();
            gameObject.AddComponent<CoopLife>();
            gameObject.AddComponent<CoopLabels>();
            gameObject.AddComponent<Net.NetSession>();
            gameObject.AddComponent<Net.NetMenu>();
            gameObject.AddComponent<CoopProbe>();
            gameObject.AddComponent<DevServer>();
            if (Net.NetLaunch.Active)
            {
                gameObject.AddComponent<Net.NetLaunch>();
            }
            Log.LogInfo($"{Name} {Version} loaded");
        }
    }
}
