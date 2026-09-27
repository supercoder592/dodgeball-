using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>Registers <see cref="CharacterPipelineSettings"/> under Project Settings ▸ Dodgeball Ultra ▸ Character Pipeline.</summary>
    internal static class CharacterPipelineSettingsProvider
    {
        private const string SettingsPath = "Project/Dodgeball Ultra/Character Pipeline";

        [SettingsProvider]
        private static SettingsProvider Create()
        {
            SerializedObject serialized = null;
            return new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label = "Character Pipeline",
                keywords = new HashSet<string>(new[]
                {
                    "Rocketbox", "avatar", "humanoid", "LOD", "smoothness", "specular", "animator", "download", "realistic",
                }),
                guiHandler = _ =>
                {
                    CharacterPipelineSettings settings = CharacterPipelineSettings.instance;
                    if (serialized == null || serialized.targetObject != settings) serialized = new SerializedObject(settings);
                    serialized.Update();

                    EditorGUILayout.HelpBox(
                        "Realistic humans: Microsoft Rocketbox avatars (MIT) -> Humanoid (explicit Biped map, enforced T-pose) -> " +
                        "PBR materials -> LOD prefab -> motion-capture AnimatorControllers. Changes apply on the next " +
                        "'Build Characters'.", MessageType.Info);

                    EditorGUI.BeginChangeCheck();
                    SerializedProperty it = serialized.GetIterator();
                    bool enterChildren = true;
                    while (it.NextVisible(enterChildren))
                    {
                        enterChildren = false;
                        if (it.propertyPath == "m_Script") continue;
                        EditorGUILayout.PropertyField(it, true);
                    }
                    if (EditorGUI.EndChangeCheck())
                    {
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                        settings.SaveSettings();
                    }

                    EditorGUILayout.Space();
                    if (GUILayout.Button("Reset to defaults", GUILayout.Width(160f)))
                    {
                        settings.ResetToDefaults();
                        serialized = null;
                    }
                },
            };
        }
    }
}
