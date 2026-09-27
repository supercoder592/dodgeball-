using System;
using System.Collections.Generic;
using System.Text;
using DodgeballUltra.Editor.Pipeline;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DodgeballUltra.Editor.Setup
{
    /// <summary>Player setting "Active Input Handling" (ProjectSettings.asset → activeInputHandler).</summary>
    public enum InputHandlingMode
    {
        Unknown = -1,
        /// <summary>Input Manager (Old) only.</summary>
        InputManager = 0,
        /// <summary>Input System Package (New) only.</summary>
        InputSystem = 1,
        /// <summary>Both backends (recommended for Dodgeball Ultra).</summary>
        Both = 2,
    }

    /// <summary>
    /// Step 1 of the Setup Wizard: render pipeline, colour space, physics layers/tags and input-handling checks.
    /// </summary>
    public static class ProjectConfigurator
    {
        /// <summary>Layer names the game expects (indices are fixed in <see cref="GameLayers"/>).</summary>
        public static readonly KeyValuePair<int, string>[] RequiredLayers =
        {
            new KeyValuePair<int, string>(GameLayers.Player, "DU_Player"),
            new KeyValuePair<int, string>(GameLayers.Ball, "DU_Ball"),
            new KeyValuePair<int, string>(GameLayers.Hittable, "DU_Hittable"),
            new KeyValuePair<int, string>(GameLayers.Court, "DU_Court"),
            new KeyValuePair<int, string>(GameLayers.PlayerBlocker, "DU_PlayerBlocker"),
            new KeyValuePair<int, string>(GameLayers.Ragdoll, "DU_Ragdoll"),
            new KeyValuePair<int, string>(GameLayers.AbilityVolume, "DU_AbilityVolume"),
            new KeyValuePair<int, string>(GameLayers.Visual, "DU_Visual"),
        };

        /// <summary>Tags shipped in TagManager.asset.</summary>
        public static readonly string[] RequiredTags = { "DU_Ball", "DU_Player" };

        private const string TagManagerPath = "ProjectSettings/TagManager.asset";
        private const string ProjectSettingsPath = "ProjectSettings/ProjectSettings.asset";

        /// <summary>How to switch Active Input Handling to Both (English).</summary>
        public const string InputHelpEnglish =
            "Edit ▸ Project Settings ▸ Player ▸ Other Settings ▸ Configuration ▸ Active Input Handling → \"Both\", then let Unity restart. " +
            "Both keeps the Input System (gamepads, rebinding) and the legacy Input Manager (used by some UI modules) working.";

        /// <summary>How to switch Active Input Handling to Both (Traditional Chinese).</summary>
        public const string InputHelpChinese =
            "編輯 ▸ Project Settings ▸ Player ▸ Other Settings ▸ Configuration ▸ Active Input Handling 設為「Both」，並依提示重新啟動 Unity。" +
            "Both 可同時使用新輸入系統（手把、按鍵重設）與舊版 Input Manager。";

        // ------------------------------------------------------------------ run

        /// <summary>Runs every automatic fix of step 1 and reports what still needs a manual action.</summary>
        public static SetupStepResult Configure()
        {
            var report = new StringBuilder();
            IEditorRenderingHooks hooks = EditorRenderingHooks.Active;

            try
            {
                hooks.ConfigureProject();
                report.Append("Render pipeline: ").Append(hooks.PipelineName).Append(hooks.IsActive ? " (active)." : " (configured).");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return SetupStepResult.Fail($"{hooks.PipelineName}: project configuration failed - {e.Message}");
            }

            if (PlayerSettings.colorSpace != ColorSpace.Linear)
            {
                PlayerSettings.colorSpace = ColorSpace.Linear;
                report.Append(" Colour space set to Linear.");
            }

            int named = EnsureLayersAndTags(false, out List<string> conflicts);
            if (named > 0) report.Append($" Named {named} layer(s)/tag(s).");
            if (conflicts.Count > 0) report.Append(" Layer conflicts: ").Append(string.Join("; ", conflicts)).Append('.');

            InputHandlingMode input = GetConfiguredInputHandling();
            if (input != InputHandlingMode.Both && input != InputHandlingMode.Unknown)
                report.Append(" Active Input Handling is '").Append(input).Append("' - recommended: Both (see step 1).");

            AssetDatabase.SaveAssets();
            bool pipelineOk = !EditorRenderingHooks.HasPipelineHooks || hooks.IsActive;
            string message = report.ToString();
            return pipelineOk ? SetupStepResult.Ok(message) : SetupStepResult.Fail(message + " The pipeline asset is still not active.");
        }

        // ------------------------------------------------------------------ layers & tags

        /// <summary>Issues with the fixed layer indices (empty slot or a different name).</summary>
        public static List<string> CheckLayers(out bool hasConflicts)
        {
            var issues = new List<string>();
            hasConflicts = false;
            foreach (KeyValuePair<int, string> layer in RequiredLayers)
            {
                string actual = LayerMask.LayerToName(layer.Key);
                if (actual == layer.Value) continue;
                if (string.IsNullOrEmpty(actual))
                {
                    issues.Add($"Layer {layer.Key} is unnamed (expected '{layer.Value}').");
                }
                else
                {
                    hasConflicts = true;
                    issues.Add($"Layer {layer.Key} is named '{actual}' but the game uses it as '{layer.Value}' (GameLayers).");
                }
            }
            return issues;
        }

        /// <summary>
        /// Names the game's layers and adds its tags in TagManager.asset. Empty layer slots are always filled; a slot already
        /// named differently is only renamed when <paramref name="overwriteConflicts"/> is set. Returns the number of changes.
        /// </summary>
        public static int EnsureLayersAndTags(bool overwriteConflicts, out List<string> conflicts)
        {
            conflicts = new List<string>();
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(TagManagerPath);
            if (assets == null || assets.Length == 0 || assets[0] == null) return 0;

            var so = new SerializedObject(assets[0]);
            int changes = 0;

            SerializedProperty layers = so.FindProperty("layers");
            if (layers != null && layers.isArray)
            {
                foreach (KeyValuePair<int, string> layer in RequiredLayers)
                {
                    if (layer.Key >= layers.arraySize) continue;
                    SerializedProperty element = layers.GetArrayElementAtIndex(layer.Key);
                    string current = element.stringValue;
                    if (current == layer.Value) continue;
                    if (string.IsNullOrEmpty(current) || overwriteConflicts)
                    {
                        element.stringValue = layer.Value;
                        changes++;
                    }
                    else
                    {
                        conflicts.Add($"layer {layer.Key} = '{current}' (expected '{layer.Value}')");
                    }
                }
            }

            SerializedProperty tags = so.FindProperty("tags");
            if (tags != null && tags.isArray)
            {
                foreach (string tag in RequiredTags)
                {
                    bool found = false;
                    for (int i = 0; i < tags.arraySize && !found; i++) found = tags.GetArrayElementAtIndex(i).stringValue == tag;
                    if (found) continue;
                    tags.InsertArrayElementAtIndex(tags.arraySize);
                    tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tag;
                    changes++;
                }
            }

            if (changes > 0) so.ApplyModifiedPropertiesWithoutUndo();
            return changes;
        }

        // ------------------------------------------------------------------ input handling

        /// <summary>True when the Input System package is installed (the asmdef version define is set).</summary>
        public static bool IsInputSystemPackageInstalled
        {
            get
            {
#if DU_INPUT_SYSTEM
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>True when the HDRP package is installed (the asmdef version define is set).</summary>
        public static bool IsHdrpPackageInstalled
        {
            get
            {
#if DU_HDRP
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// The mode the currently loaded scripts were compiled with (what is in effect right now). Unity defines
        /// ENABLE_INPUT_SYSTEM / ENABLE_LEGACY_INPUT_MANAGER for every assembly from the Player setting.
        /// </summary>
        public static InputHandlingMode GetCompiledInputHandling()
        {
#if ENABLE_INPUT_SYSTEM && ENABLE_LEGACY_INPUT_MANAGER
            return InputHandlingMode.Both;
#elif ENABLE_INPUT_SYSTEM
            return InputHandlingMode.InputSystem;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return InputHandlingMode.InputManager;
#else
            return InputHandlingMode.Unknown;
#endif
        }

        /// <summary>
        /// The saved Player setting (may differ from <see cref="GetCompiledInputHandling"/> until Unity restarts). PlayerSettings
        /// has no public API for it, so it is read (never written) from ProjectSettings.asset.
        /// </summary>
        public static InputHandlingMode GetConfiguredInputHandling()
        {
            try
            {
                foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(ProjectSettingsPath))
                {
                    if (!(o is PlayerSettings)) continue;
                    SerializedProperty property = new SerializedObject(o).FindProperty("activeInputHandler");
                    if (property == null) break;
                    int value = property.intValue;
                    return value >= 0 && value <= 2 ? (InputHandlingMode)value : InputHandlingMode.Unknown;
                }
            }
            catch (Exception)
            {
                // Fall back to the compile-time view below.
            }
            return GetCompiledInputHandling();
        }

        /// <summary>Human-readable warnings about input handling (empty when everything is fine).</summary>
        public static List<string> CheckInputHandling()
        {
            var warnings = new List<string>();
            InputHandlingMode configured = GetConfiguredInputHandling();
            InputHandlingMode compiled = GetCompiledInputHandling();

            if (compiled != InputHandlingMode.Unknown && configured != InputHandlingMode.Unknown && configured != compiled)
                warnings.Add($"Active Input Handling was changed to '{configured}' but the editor still runs '{compiled}': restart Unity.");

            switch (configured)
            {
                case InputHandlingMode.InputManager:
                    warnings.Add(IsInputSystemPackageInstalled
                        ? "Active Input Handling is 'Input Manager (Old)': the Input System package is installed but disabled, so gamepad support and rebinding are off. Recommended: Both."
                        : "Active Input Handling is 'Input Manager (Old)': keyboard/mouse works; install the Input System package and choose Both for gamepads.");
                    break;
                case InputHandlingMode.InputSystem:
                    warnings.Add("Active Input Handling is 'Input System Package (New)' only: legacy UnityEngine.Input calls (e.g. a StandaloneInputModule) would throw. Recommended: Both.");
                    break;
            }
            return warnings;
        }

        /// <summary>Opens Project Settings ▸ Player (where Active Input Handling lives).</summary>
        public static void OpenPlayerSettings() => SettingsService.OpenProjectSettings("Project/Player");
    }
}
