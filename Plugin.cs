using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace CatPatchspace
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    [BepInDependency("CasualtiesMP")]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGUID = "meow.catpatch";
        public const string ModName = "CatPatch";
        public const string ModVersion = "1.0.7";

        internal static new ManualLogSource Logger;
        private readonly Harmony _harmony = new(ModGUID);

        public static Plugin Instance { get; private set; } = null!;

        public void Awake()
        {
            Logger = base.Logger;
            Instance = this;

            _harmony.PatchAll();

            CatPatchKeybinds.RegisterDefaultsOnce();
            CatPatchServerSettings.Bind(Config);
            CatPatchClientSettings.Bind(Config);
            gameObject.AddComponent<SpraySoundPlayer>();

            gameObject.AddComponent<HugController>();
            gameObject.AddComponent<KissController>();
            gameObject.AddComponent<EmoteController>();
            gameObject.AddComponent<DogpileController>();
            gameObject.AddComponent<PiggybackThrowController>();
            gameObject.AddComponent<SelfHarmRescueController>();
            gameObject.AddComponent<CreatorJoinAnnouncer>();
            gameObject.AddComponent<AdminPrivilegeController>();
            gameObject.AddComponent<HostTransferController>();
            gameObject.AddComponent<ChallengeModeController>();
            gameObject.AddComponent<SprayController>();
            gameObject.AddComponent<SprayInspector>();
            gameObject.AddComponent<CatPatchUpdater>();
            gameObject.AddComponent<CatPatchHostSettingsMenu>();
            gameObject.AddComponent<CatPatchHostModerationMenu>();
            Logger.LogInfo("Plugin CatPatch is loaded!");
        }
    }
}
