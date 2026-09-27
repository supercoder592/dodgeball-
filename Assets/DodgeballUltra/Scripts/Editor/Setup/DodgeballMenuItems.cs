using DodgeballUltra.Editor.ArenaScene;
using DodgeballUltra.Editor.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DodgeballUltra.Editor.Setup
{
    /// <summary>
    /// The "Dodgeball Ultra" menu: Setup Wizard, open / play the arena, and direct access to the generators.
    /// </summary>
    public static class DodgeballMenuItems
    {
        private const string Root = "Dodgeball Ultra/";

        // ------------------------------------------------------------------ wizard

        [MenuItem(Root + "Setup Wizard", false, 0)]
        private static void OpenSetupWizard() => SetupWizardWindow.Open();

        // ------------------------------------------------------------------ scenes

        [MenuItem(Root + "Open Arena Scene", false, 20)]
        private static void OpenArenaSceneMenu() => OpenArenaScene();

        [MenuItem(Root + "Open Arena Scene", true)]
        private static bool ValidateOpenArenaScene() => !EditorApplication.isPlayingOrWillChangePlaymode;

        [MenuItem(Root + "Play Arena", false, 21)]
        private static void PlayArenaMenu() => PlayArena();

        [MenuItem(Root + "Play Arena", true)]
        private static bool ValidatePlayArena() => !EditorApplication.isPlayingOrWillChangePlaymode;

        // ------------------------------------------------------------------ generators

        [MenuItem(Root + "Data/Generate Game Data (keep tuning)", false, 40)]
        private static void GenerateData() => ReportData(GameDataGenerator.Generate(DataUpdateMode.KeepTuning));

        [MenuItem(Root + "Data/Update Game Data to Spec Values (keep models)", false, 41)]
        private static void SyncData() => ReportData(GameDataGenerator.Generate(DataUpdateMode.SyncSpecValues));

        [MenuItem(Root + "Data/Reset Game Data to Defaults…", false, 42)]
        private static void ResetData()
        {
            if (ConfirmResetData()) ReportData(GameDataGenerator.Generate(DataUpdateMode.ResetToDefaults));
        }

        [MenuItem(Root + "Arena/Build Arena Scene", false, 60)]
        private static void BuildArena()
        {
            ArenaBuildResult result = ArenaSceneBuilder.Build(SetupRunner.ArenaSettings);
            if (!result.Success && !result.Cancelled)
                EditorUtility.DisplayDialog("Dodgeball Ultra", result.Report, "OK");
        }

        [MenuItem(Root + "Arena/Build Arena Scene", true)]
        private static bool ValidateBuildArena() => !EditorApplication.isPlayingOrWillChangePlaymode;

        // ------------------------------------------------------------------ public helpers (used by the wizard)

        /// <summary>
        /// Opens Arena.unity (asking to save modified scenes first). If it has not been built yet, offers to build it or to
        /// open QuickPlay.unity instead. Returns true when a playable scene is open.
        /// </summary>
        public static bool OpenArenaScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return false;

            string path = DodgeballEditorPaths.ArenaScenePath;
            if (!ArenaSceneBuilder.SceneExists)
            {
                int choice = EditorUtility.DisplayDialogComplex("Dodgeball Ultra",
                    "The arena scene has not been built yet.\n球場場景尚未建立。\n\n" +
                    "Build it now (≈1 min), or open QuickPlay (builds everything at runtime)?",
                    "Build Arena", "Cancel", "Open QuickPlay");
                switch (choice)
                {
                    case 0:
                        ArenaBuildResult result = ArenaSceneBuilder.Build(SetupRunner.ArenaSettings);
                        if (!result.Success)
                        {
                            if (!result.Cancelled) EditorUtility.DisplayDialog("Dodgeball Ultra", result.Report, "OK");
                            return false;
                        }
                        return true; // the builder leaves the new scene open
                    case 2:
                        path = DodgeballEditorPaths.QuickPlayScenePath;
                        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null) return false;
                        break;
                    default:
                        return false;
                }
            }

            Scene active = SceneManager.GetActiveScene();
            if (active.path == path && !active.isDirty) return true;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;
            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            return true;
        }

        /// <summary>Opens the arena (see <see cref="OpenArenaScene"/>) and enters Play mode. Returns false if cancelled.</summary>
        public static bool PlayArena()
        {
            if (EditorApplication.isPlaying) return true;
            if (!OpenArenaScene()) return false;
            EditorApplication.isPlaying = true;
            return true;
        }

        /// <summary>Asks before a destructive data reset.</summary>
        public static bool ConfirmResetData()
        {
            return EditorUtility.DisplayDialog("Reset game data? · 重設遊戲資料？",
                "Every hero, ability, match rule and juice value returns to the design-spec defaults. Model, portrait and " +
                "animator links are cleared too (run 'Build Characters' again afterwards).\n\n" +
                "所有英雄、技能、規則與打擊感數值將回到預設值，模型與動畫連結也會清除（之後請重新執行「建立角色」）。",
                "Reset · 重設", "Cancel · 取消");
        }

        private static void ReportData(DataGenerationResult result)
        {
            if (result.Success)
            {
                if (result.Config != null) EditorGUIUtility.PingObject(result.Config);
            }
            else
            {
                EditorUtility.DisplayDialog("Dodgeball Ultra", result.Report, "OK");
            }
        }
    }
}
