using DodgeballUltra.Editor.Pipeline;

namespace DodgeballUltra.Editor.Setup
{
    /// <summary>
    /// Every project-relative location the Setup Wizard, data generator and arena builder read or write.
    /// <c>Generated/</c> is git-ignored: it is rebuilt on each machine by the wizard.
    /// </summary>
    public static class DodgeballEditorPaths
    {
        public const string Root = "Assets/DodgeballUltra";
        public const string GeneratedRoot = Root + "/Generated";

        // ---------------------------------------------------------------- game data (ScriptableObjects)
        public const string DataRoot = GeneratedRoot + "/Data";
        public const string HeroesFolder = DataRoot + "/Heroes";
        public const string AbilitiesFolder = DataRoot + "/Abilities";
        public const string GameConfigPath = DataRoot + "/GameConfig.asset";
        public const string MatchRulesPath = DataRoot + "/MatchRules.asset";
        public const string JuiceProfilePath = DataRoot + "/JuiceProfile.asset";

        // ---------------------------------------------------------------- arena
        public const string ArenaRoot = GeneratedRoot + "/Arena";
        public const string ArenaTexturesFolder = ArenaRoot + "/Textures";
        public const string ArenaMaterialsFolder = ArenaRoot + "/Materials";
        public const string ArenaMeshesPath = ArenaRoot + "/ArenaMeshes.asset";
        public const string ArenaRuntimeContentFolder = ArenaRoot + "/SceneContent";
        public const string ArenaLightingFolder = ArenaRoot + "/Lighting";

        // ---------------------------------------------------------------- scenes
        public const string ScenesFolder = Root + "/Scenes";
        public const string ArenaScenePath = ScenesFolder + "/Arena.unity";
        public const string QuickPlayScenePath = ScenesFolder + "/QuickPlay.unity";

        // ---------------------------------------------------------------- licences / docs
        public const string ThirdPartyNoticesPath = "THIRD_PARTY_NOTICES.md";
        public const string RocketboxRepositoryUrl = "https://github.com/microsoft/Microsoft-Rocketbox";

        /// <summary>Creates every missing folder of <paramref name="assetFolder"/> ("Assets/A/B").</summary>
        public static void EnsureFolder(string assetFolder) => TextureAssetUtility.EnsureAssetFolder(assetFolder);

        /// <summary>Absolute file-system path for a project-relative asset path.</summary>
        public static string ToAbsolute(string assetPath) => TextureAssetUtility.ToAbsolutePath(assetPath);
    }
}
