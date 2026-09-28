using System;
using System.Collections.Generic;
using DodgeballUltra.Editor.Pipeline;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>
    /// Builds the hero model prefab <c>Assets/DodgeballUltra/Generated/Characters/&lt;Hero&gt;.prefab</c> from a configured
    /// Rocketbox Humanoid FBX:
    /// <list type="bullet">
    /// <item>generated PBR materials assigned by FBX slot name (<c>&lt;id&gt;_body</c>, <c>_head</c>, <c>_opacity</c>,
    /// <c>_helmet</c>, <c>_equipment</c>, <c>_tools</c>, <c>_hat</c> ...);</item>
    /// <item>weapon sub-meshes (combat knife, pistols ...) stripped into a generated mesh when enabled - dodgeball players
    /// carry no weapons;</item>
    /// <item>a <see cref="LODGroup"/> when the FBX carries several of the Rocketbox hipoly / midpoly / lowpoly / ultralowpoly
    /// meshes;</item>
    /// <item>an <see cref="Animator"/> with the model's Humanoid avatar and the generated controller, root motion off
    /// (gameplay moves the body; clips play in place), culling per <see cref="CharacterPipelineSettings"/>.</item>
    /// </list>
    /// The prefab is a plain prefab (not a model variant): regenerating it keeps its GUID, so CharacterData references
    /// survive every rebuild.
    /// </summary>
    public static class CharacterPrefabBuilder
    {
        /// <summary>Prefab asset path for a hero name (e.g. "Rayne").</summary>
        public static string PrefabPath(string heroName)
            => CharacterPipeline.GeneratedRoot + "/" + RocketboxAssetSet.SanitizeFileName(heroName) + ".prefab";

        /// <summary>
        /// Builds / overwrites the prefab for <paramref name="heroName"/>. Returns the saved prefab asset, or null (with the
        /// reason in <paramref name="log"/>).
        /// </summary>
        public static GameObject Build(string heroName, string avatar, string modelPath, IDictionary<string, Material> materials,
            RuntimeAnimatorController controller, List<string> log)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (model == null)
            {
                log?.Add($"{heroName}: model '{modelPath}' could not be loaded.");
                return null;
            }

            CharacterPipelineSettings settings = CharacterPipelineSettings.Instance;
            Avatar humanoid = HumanoidAvatarBuilder.LoadAvatar(modelPath);
            if (humanoid == null || !humanoid.isValid || !humanoid.isHuman)
            {
                string failure = HumanoidAvatarBuilder.GetFailure(modelPath);
                log?.Add($"{heroName}: '{avatar}' is not a valid Humanoid{(failure != null ? " (" + failure + ")" : string.Empty)} - " +
                         "the prefab is built, but motion capture, IK and ragdoll need a Humanoid avatar.");
            }

            // Work in a preview scene: the user's open scene is never touched (nor marked dirty).
            Scene stage = EditorSceneManager.NewPreviewScene();
            var instance = PrefabUtility.InstantiatePrefab(model, stage) as GameObject;
            if (instance == null)
            {
                EditorSceneManager.ClosePreviewScene(stage);
                log?.Add($"{heroName}: '{modelPath}' could not be instantiated.");
                return null;
            }
            // A plain prefab (not a model variant): generated overrides such as stripped meshes stay self-contained.
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            instance.name = RocketboxAssetSet.SanitizeFileName(heroName);
            try
            {
                Transform root = instance.transform;
                root.localPosition = Vector3.zero;
                root.localRotation = Quaternion.identity;

                ConfigureAnimator(instance, humanoid, controller, settings);
                List<Renderer> renderers = CollectRenderers(instance);
                AssignMaterials(heroName, renderers, materials, settings, avatar, log);
                ConfigureRenderers(renderers, settings);
                BuildLods(instance, renderers, settings, log);

                string path = PrefabPath(heroName);
                TextureAssetUtility.EnsureAssetFolder(CharacterPipeline.GeneratedRoot);
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(instance, path, out bool saved);
                if (!saved || prefab == null)
                {
                    log?.Add($"{heroName}: could not save {path}.");
                    return null;
                }
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(instance);
                EditorSceneManager.ClosePreviewScene(stage);
            }
        }

        // ================================================================================================= animator

        private static void ConfigureAnimator(GameObject instance, Avatar avatar, RuntimeAnimatorController controller,
            CharacterPipelineSettings settings)
        {
            var animator = instance.GetComponent<Animator>();
            if (animator == null)
            {
                // Imported models carry their Animator on the root; a nested one would drive the wrong hierarchy.
                foreach (Animator nested in instance.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(nested);
                animator = instance.AddComponent<Animator>();
            }
            if (avatar != null) animator.avatar = avatar;
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.updateMode = AnimatorUpdateMode.Normal;
            animator.cullingMode = settings.animatorCulling;
        }

        // ================================================================================================ materials

        private static List<Renderer> CollectRenderers(GameObject instance)
        {
            var list = new List<Renderer>();
            foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true))
                if (r is SkinnedMeshRenderer || r is MeshRenderer) list.Add(r);
            return list;
        }

        private static void AssignMaterials(string heroName, List<Renderer> renderers, IDictionary<string, Material> materials,
            CharacterPipelineSettings settings, string avatar, List<string> log)
        {
            var unmatched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Renderer renderer in renderers)
            {
                Material[] current = renderer.sharedMaterials;
                var slotNames = new string[current.Length];
                var assigned = new Material[current.Length];
                for (int i = 0; i < current.Length; i++)
                {
                    slotNames[i] = current[i] != null ? CleanSlotName(current[i].name) : string.Empty;
                    Material generated = ResolveMaterial(slotNames[i], materials);
                    if (generated == null && current[i] != null) unmatched.Add(slotNames[i]);
                    assigned[i] = generated != null ? generated : current[i];
                }
                renderer.sharedMaterials = assigned;

                if (settings.stripWeaponSubmeshes && renderer is SkinnedMeshRenderer skinned)
                    StripWeapons(heroName, avatar, skinned, slotNames, assigned, settings, log);
            }

            if (unmatched.Count > 0 && materials != null && materials.Count > 0)
                log?.Add($"{heroName}: no generated material for slot(s) {string.Join(", ", unmatched)} (FBX material kept).");
        }

        /// <summary>
        /// Generated material for an FBX slot: exact name, else the material with the same part after the texture-set id
        /// (e.g. slot "m301_body" -> any "*_body"), else null.
        /// </summary>
        public static Material ResolveMaterial(string slotName, IDictionary<string, Material> materials)
        {
            if (string.IsNullOrEmpty(slotName) || materials == null || materials.Count == 0) return null;
            foreach (KeyValuePair<string, Material> kv in materials)
                if (string.Equals(kv.Key, slotName, StringComparison.OrdinalIgnoreCase)) return kv.Value;

            string part = PartOf(slotName);
            if (part.Length == 0) return null;
            Material match = null;
            foreach (KeyValuePair<string, Material> kv in materials)
            {
                if (!string.Equals(PartOf(kv.Key), part, StringComparison.OrdinalIgnoreCase)) continue;
                if (match != null) return null; // ambiguous
                match = kv.Value;
            }
            return match;
        }

        private static string PartOf(string slot)
        {
            int underscore = slot.IndexOf('_');
            return underscore >= 0 && underscore < slot.Length - 1 ? slot.Substring(underscore + 1) : string.Empty;
        }

        /// <summary>FBX material names can carry importer decorations ("m301_body (Instance)", "m301_body.001").</summary>
        private static string CleanSlotName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            string n = name.Replace(" (Instance)", string.Empty).Trim();
            int dot = n.LastIndexOf('.');
            if (dot > 0 && dot < n.Length - 1 && int.TryParse(n.Substring(dot + 1), out _)) n = n.Substring(0, dot);
            return n;
        }

        // =================================================================================================== weapons

        private static void StripWeapons(string heroName, string avatar, SkinnedMeshRenderer renderer, string[] slotNames,
            Material[] materials, CharacterPipelineSettings settings, List<string> log)
        {
            Mesh source = renderer.sharedMesh;
            if (source == null || settings.weaponMaterialTokens == null || settings.weaponMaterialTokens.Length == 0) return;

            var keep = new List<int>();
            var removed = new List<string>();
            int count = Math.Min(slotNames.Length, source.subMeshCount);
            for (int i = 0; i < count; i++)
            {
                if (IsWeaponSlot(slotNames[i], settings.weaponMaterialTokens)) removed.Add(slotNames[i]);
                else keep.Add(i);
            }
            if (removed.Count == 0 || keep.Count == 0) return;

            // Read every kept sub-mesh first (edit-mode access works for non-readable imports; verify anyway).
            var indices = new List<int[]>(keep.Count);
            foreach (int s in keep)
            {
                int[] idx = source.GetIndices(s);
                if (idx == null || idx.Length != (int)source.GetIndexCount(s))
                {
                    log?.Add($"{heroName}: mesh data of '{source.name}' is not readable - weapons ({string.Join(", ", removed)}) kept.");
                    return;
                }
                indices.Add(idx);
            }

            string folder = RocketboxAssetSet.GeneratedAvatarFolder(avatar);
            TextureAssetUtility.EnsureAssetFolder(folder);
            string meshPath = folder + "/" + RocketboxAssetSet.SanitizeFileName(source.name) + "_noweapons.asset";

            Mesh stripped = Object.Instantiate(source);
            stripped.name = source.name + "_noweapons";
            stripped.subMeshCount = keep.Count;
            for (int j = 0; j < keep.Count; j++)
                stripped.SetIndices(indices[j], source.GetTopology(keep[j]), j, false, (int)source.GetBaseVertex(keep[j]));
            stripped.bounds = source.bounds;

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            Mesh saved;
            if (existing != null)
            {
                EditorUtility.CopySerialized(stripped, existing);
                existing.name = stripped.name;
                Object.DestroyImmediate(stripped);
                EditorUtility.SetDirty(existing);
                saved = existing;
            }
            else
            {
                AssetDatabase.CreateAsset(stripped, meshPath);
                saved = stripped;
            }

            var keptMaterials = new Material[keep.Count];
            for (int j = 0; j < keep.Count; j++) keptMaterials[j] = keep[j] < materials.Length ? materials[keep[j]] : null;
            renderer.sharedMesh = saved;
            renderer.sharedMaterials = keptMaterials;
            log?.Add($"{heroName}: removed weapon sub-mesh(es) {string.Join(", ", removed)}.");
        }

        private static bool IsWeaponSlot(string slot, string[] tokens)
        {
            if (string.IsNullOrEmpty(slot)) return false;
            foreach (string token in tokens)
                if (!string.IsNullOrEmpty(token) && slot.IndexOf(token.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // =============================================================================================== renderers

        private static void ConfigureRenderers(List<Renderer> renderers, CharacterPipelineSettings settings)
        {
            foreach (Renderer renderer in renderers)
            {
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
                renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                renderer.motionVectorGenerationMode = settings.skinnedMotionVectors
                    ? MotionVectorGenerationMode.Object
                    : MotionVectorGenerationMode.Camera;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    skinned.skinnedMotionVectors = settings.skinnedMotionVectors;
                    skinned.updateWhenOffscreen = false;
                    skinned.quality = SkinQuality.Auto;
                }
            }
        }

        // ===================================================================================================== LODs

        /// <summary>LOD level from a Rocketbox mesh / object name (hipoly 0, midpoly 1, lowpoly 2, ultralowpoly 3), or -1.</summary>
        public static int LodLevelOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            string n = name.ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
            if (n.Contains("ultralowpoly") || n.Contains("ultralow")) return 3;
            if (n.Contains("lowpoly")) return 2;
            if (n.Contains("midpoly")) return 1;
            if (n.Contains("hipoly") || n.Contains("highpoly")) return 0;
            return -1;
        }

        private static void BuildLods(GameObject instance, List<Renderer> renderers, CharacterPipelineSettings settings, List<string> log)
        {
            var byLevel = new SortedDictionary<int, List<Renderer>>();
            var unleveled = new List<Renderer>();
            foreach (Renderer r in renderers)
            {
                string meshName = r is SkinnedMeshRenderer s && s.sharedMesh != null ? s.sharedMesh.name : null;
                int level = LodLevelOf(r.gameObject.name);
                if (level < 0) level = LodLevelOf(meshName);
                if (level < 0)
                {
                    unleveled.Add(r);
                    continue;
                }
                if (!byLevel.TryGetValue(level, out List<Renderer> list)) byLevel.Add(level, list = new List<Renderer>());
                list.Add(r);
            }

            foreach (LODGroup old in instance.GetComponentsInChildren<LODGroup>(true)) Object.DestroyImmediate(old);
            if (byLevel.Count < 2) return; // only one detail level in the FBX: nothing to switch between

            // Renderers without a level token (props, eyes) stay visible on the most detailed LOD.
            var levels = new List<List<Renderer>>(byLevel.Values);
            levels[0].AddRange(unleveled);

            float[] transitions = { settings.lod0ScreenHeight, settings.lod1ScreenHeight, settings.lod2ScreenHeight };
            var lods = new LOD[levels.Count];
            float previous = 1f;
            for (int i = 0; i < levels.Count; i++)
            {
                float height = i == levels.Count - 1 ? settings.cullScreenHeight : transitions[Math.Min(i, transitions.Length - 1)];
                height = Mathf.Min(height, previous - 0.001f);
                height = Mathf.Max(height, 0.0001f);
                previous = height;
                lods[i] = new LOD(height, levels[i].ToArray());
            }

            // Lower LODs of the same mesh set must not render on top of LOD0 outside the group's control.
            LODGroup group = instance.AddComponent<LODGroup>();
            group.fadeMode = LODFadeMode.None;
            group.SetLODs(lods);
            group.RecalculateBounds();
            log?.Add($"{instance.name}: LODGroup with {lods.Length} levels.");
        }
    }
}
