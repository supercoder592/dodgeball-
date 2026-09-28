using System;
using System.Collections.Generic;
using System.Reflection;
using DodgeballUltra.Abilities;
using UnityEditor;
using UnityEngine;

namespace DodgeballUltra.Editor.Inspectors
{
    /// <summary>
    /// Inspector for <see cref="AbilityData"/>: draws the designer metadata, then a type dropdown listing every concrete
    /// <see cref="AbilityBase"/> class (<see cref="TypeCache.GetTypesDerivedFrom{T}"/>, grouped by hero) to assign the
    /// <c>[SerializeReference] logic</c>, followed by that class's own tuning fields. Supports multi-object editing and Undo.
    /// </summary>
    [CustomEditor(typeof(AbilityData))]
    [CanEditMultipleObjects]
    public sealed class AbilityDataEditor : UnityEditor.Editor
    {
        private const string LogicField = "logic";

        private static Type[] s_types;
        private static GUIContent[] s_labels;

        private SerializedProperty _logic;

        private void OnEnable()
        {
            _logic = serializedObject.FindProperty(LogicField);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawMetadata();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Logic", EditorStyles.boldLabel);
            DrawLogicTypePopup();
            DrawLogicFields();

            serializedObject.ApplyModifiedProperties();
        }

        // ================================================================================================ metadata

        private void DrawMetadata()
        {
            SerializedProperty it = serializedObject.GetIterator();
            bool enterChildren = true;
            while (it.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (it.propertyPath == LogicField) continue;
                using (new EditorGUI.DisabledScope(it.propertyPath == "m_Script"))
                    EditorGUILayout.PropertyField(it, true);
            }
        }

        // ============================================================================================ logic type

        private void DrawLogicTypePopup()
        {
            EnsureTypes();
            if (_logic == null)
            {
                EditorGUILayout.HelpBox("AbilityData has no 'logic' field.", MessageType.Error);
                return;
            }

            Type current = ResolveType(_logic.managedReferenceFullTypename);
            bool mixed = HasMixedTypes();

            int index = current == null ? 0 : Array.IndexOf(s_types, current) + 1;
            EditorGUI.showMixedValue = mixed;
            EditorGUI.BeginChangeCheck();
            int selected = EditorGUILayout.Popup(new GUIContent("Behaviour", "The ability class run by AbilityController. Its " +
                                                                              "serialized fields are the ability's tuning values."),
                Mathf.Max(0, index), s_labels);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck() && (selected != index || mixed))
            {
                Type type = selected <= 0 ? null : s_types[selected - 1];
                AssignLogic(type);
            }

            if (!mixed && current == null)
            {
                string typename = _logic.managedReferenceFullTypename;
                EditorGUILayout.HelpBox(string.IsNullOrEmpty(typename)
                    ? "No behaviour assigned: this ability does nothing in game. Pick a class above."
                    : $"The behaviour class '{typename}' no longer exists (renamed or deleted). Pick a class above.", MessageType.Warning);
            }
        }

        private void AssignLogic(Type type)
        {
            serializedObject.ApplyModifiedProperties();
            Undo.RecordObjects(targets, type != null ? "Set Ability Logic " + type.Name : "Clear Ability Logic");
            foreach (UnityEngine.Object t in targets)
            {
                if (!(t is AbilityData data)) continue;
                if (type != null && data.logic != null && data.logic.GetType() == type) continue;
                data.logic = type != null ? CreateLogic(type) : null;
                EditorUtility.SetDirty(data);
            }
            serializedObject.Update();
            GUIUtility.ExitGUI(); // the property layout changed: restart the IMGUI pass cleanly
        }

        private static AbilityBase CreateLogic(Type type)
        {
            try
            {
                return (AbilityBase)Activator.CreateInstance(type, true);
            }
            catch (Exception e) when (e is MissingMethodException || e is TargetInvocationException || e is MemberAccessException)
            {
                Debug.LogError($"[Dodgeball Ultra] Could not create {type.FullName}: {e.GetBaseException().Message}");
                return null;
            }
        }

        // ========================================================================================== logic fields

        private void DrawLogicFields()
        {
            if (_logic == null || HasMixedTypes()) return; // different classes: no common fields to edit
            if (string.IsNullOrEmpty(_logic.managedReferenceFullTypename)) return;

            SerializedProperty child = _logic.Copy();
            SerializedProperty end = _logic.GetEndProperty();
            bool any = false;
            using (new EditorGUI.IndentLevelScope())
            {
                if (child.NextVisible(true))
                {
                    while (!SerializedProperty.EqualContents(child, end))
                    {
                        EditorGUILayout.PropertyField(child, true);
                        any = true;
                        if (!child.NextVisible(false)) break;
                    }
                }
            }
            if (!any) EditorGUILayout.LabelField(" ", "No tuning values.", EditorStyles.miniLabel);
        }

        private bool HasMixedTypes()
        {
            Type first = null;
            bool initialized = false;
            foreach (UnityEngine.Object t in targets)
            {
                Type type = (t as AbilityData)?.logic?.GetType();
                if (!initialized)
                {
                    first = type;
                    initialized = true;
                }
                else if (type != first) return true;
            }
            return false;
        }

        // ================================================================================================= types

        private static void EnsureTypes()
        {
            if (s_types != null) return;
            var types = new List<Type>();
            foreach (Type t in TypeCache.GetTypesDerivedFrom<AbilityBase>())
            {
                if (t.IsAbstract || t.IsGenericTypeDefinition || t.IsInterface) continue;
                if (!t.IsSerializable) continue; // [SerializeReference] requires [Serializable]
                if (t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) == null)
                    continue;
                types.Add(t);
            }
            types.Sort((a, b) => string.Compare(MenuPath(a), MenuPath(b), StringComparison.Ordinal));

            s_types = types.ToArray();
            s_labels = new GUIContent[s_types.Length + 1];
            s_labels[0] = new GUIContent("None");
            for (int i = 0; i < s_types.Length; i++) s_labels[i + 1] = new GUIContent(MenuPath(s_types[i]), s_types[i].FullName);
        }

        /// <summary>"Rayne/Supersonic Meteor" for hero abilities (class name prefixed by a hero), else "Shared/..." .</summary>
        private static string MenuPath(Type type)
        {
            string name = type.Name;
            foreach (string hero in Enum.GetNames(typeof(HeroId)))
            {
                if (name.Length > hero.Length && name.StartsWith(hero, StringComparison.Ordinal) && char.IsUpper(name[hero.Length]))
                    return hero + "/" + ObjectNames.NicifyVariableName(name.Substring(hero.Length));
            }
            return "Shared/" + ObjectNames.NicifyVariableName(name);
        }

        /// <summary>Type from <see cref="SerializedProperty.managedReferenceFullTypename"/> ("Assembly Namespace.Type").</summary>
        private static Type ResolveType(string fullTypename)
        {
            if (string.IsNullOrEmpty(fullTypename) || s_types == null) return null;
            int space = fullTypename.IndexOf(' ');
            string assembly = space > 0 ? fullTypename.Substring(0, space) : null;
            string typeName = space > 0 ? fullTypename.Substring(space + 1) : fullTypename;
            foreach (Type t in s_types)
            {
                if (!string.Equals(t.FullName?.Replace('+', '/'), typeName, StringComparison.Ordinal) &&
                    !string.Equals(t.FullName, typeName, StringComparison.Ordinal)) continue;
                if (assembly == null || t.Assembly.GetName().Name == assembly) return t;
            }
            return null;
        }
    }
}
