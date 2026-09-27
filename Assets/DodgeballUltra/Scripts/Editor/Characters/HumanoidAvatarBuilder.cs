using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>
    /// Turns Microsoft Rocketbox (3ds Max Biped) FBX files into Mecanim Humanoids - the editor equivalent of
    /// "Rig ▸ Humanoid ▸ Configure ▸ Mapping: explicit + Pose ▸ Enforce T-Pose":
    /// <list type="number">
    /// <item><b>Avatar models</b> (<c>Avatars/*/Export/*.fbx</c>): after the first (Generic) import the model hierarchy is
    /// instantiated, the A-pose bind pose is rotated into a proper T-pose (upper arm / forearm / hand along ±X of the
    /// character, palms down, elbows hinging forward, legs straight down, feet kept flat), a <see cref="HumanDescription"/>
    /// with the explicit <see cref="RocketboxBipedMap"/> and a <see cref="SkeletonBone"/> for <em>every</em> transform is
    /// validated in memory with <see cref="AvatarBuilder"/> and assigned to the importer (CreateFromThisModel).</item>
    /// <item><b>Motion-capture clips</b> (<c>Animations/*.fbx</c>): their rest pose is frame 0 of the take (not a bind pose),
    /// so they copy the avatar of a configured model with the identical skeleton (male clips from a male avatar, female
    /// from a female one: Rocketbox male/female rigs share exact bone lengths) - "Copy From Other Avatar". Clip settings
    /// come from <c>defaultClipAnimations</c>: loop idles/locomotion, bake root rotation and height into the pose
    /// (based on the original), keep planar root motion as root motion (applyRootMotion is off at runtime, so clips play
    /// in place).</item>
    /// </list>
    /// A userData marker (<c>DU.Humanoid:...</c>) records the configuration so the import postprocessor never re-triggers
    /// it (no re-import loops) - including a "failed" marker for rigs that cannot be mapped.
    /// </summary>
    public static class HumanoidAvatarBuilder
    {
        /// <summary>Bump to force every Rocketbox file to be re-configured on the next build.</summary>
        public const int SetupVersion = 1;

        private const string MarkerKey = "DU.Humanoid";

        /// <summary>Humanoid twist distribution / stretch defaults (Unity defaults, realistic limits).</summary>
        private const float UpperArmTwist = 0.5f, LowerArmTwist = 0.5f, UpperLegTwist = 0.5f, LowerLegTwist = 0.5f;
        private const float ArmStretch = 0.05f, LegStretch = 0.05f, FeetSpacing = 0f;

        private static readonly HashSet<string> s_autoQueue = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool s_autoScheduled;
        private static int s_suspendAuto;

        private static string ModelMarker => $"{MarkerKey}:model:v{SetupVersion}";
        private static string ClipMarkerPrefix => $"{MarkerKey}:clip:v{SetupVersion}:";
        private static string FailedMarkerPrefix => $"{MarkerKey}:failed:v{SetupVersion}";

        // =============================================================================================== import hooks

        /// <summary>
        /// Import settings shared by every Rocketbox FBX (called from <see cref="RocketboxImportPostprocessor"/>):
        /// no blend shapes (facial rigs are not used), no mesh compression, full transform hierarchy (IK sockets, ragdoll),
        /// imported normals + MikkTSpace tangents (matches the baked normal maps), cm -> m file scale. Avatar models import
        /// their FBX materials by description (slot names identify body/head/opacity and are remapped to generated
        /// materials); clip files import no materials. A file without our marker is imported Generic first.
        /// </summary>
        public static void ApplyBaseModelSettings(ModelImporter importer, string assetPath)
        {
            bool isClip = RocketboxAssetSet.IsAnimationPath(assetPath);

            importer.globalScale = 1f;
            importer.useFileScale = true;
            importer.importBlendShapes = false;
            importer.importVisibility = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.isReadable = false;
            importer.optimizeGameObjects = false;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.addCollider = false;

            if (isClip)
            {
                importer.importAnimation = true;
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
                importer.animationCompression = ModelImporterAnimationCompression.Optimal;
                importer.resampleCurves = true;
            }
            else
            {
                // The avatar FBX carries a stray 18-curve take; characters are driven by the curated clips only.
                importer.importAnimation = false;
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
                importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
            }

            if (!HasMarker(importer))
            {
                // First import: a plain skeleton, no auto-mapped Humanoid guess (and no avatar-validation noise).
                importer.animationType = ModelImporterAnimationType.Generic;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            }
        }

        /// <summary>
        /// Called after a Rocketbox FBX was imported. Files without a marker are queued for automatic Humanoid setup on the
        /// next editor tick (so a download made with <c>Tools/fetch_rocketbox.py</c> is configured as soon as Unity imports it).
        /// </summary>
        internal static void NotifyImported(ModelImporter importer, string assetPath)
        {
            if (s_suspendAuto > 0 || HasMarker(importer)) return;
            if (!RocketboxAssetSet.IsAvatarModelPath(assetPath) && !RocketboxAssetSet.IsAnimationPath(assetPath)) return;
            s_autoQueue.Add(assetPath);
            ScheduleAutoQueue();
        }

        /// <summary>Suspends automatic configuration (the pipeline configures explicitly and in order). Dispose to resume.</summary>
        public static IDisposable SuspendAutoConfigure() => new AutoSuspension();

        private sealed class AutoSuspension : IDisposable
        {
            private bool _disposed;

            public AutoSuspension() => s_suspendAuto++;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                s_suspendAuto = Math.Max(0, s_suspendAuto - 1);
            }
        }

        private static void ScheduleAutoQueue()
        {
            if (s_autoScheduled) return;
            s_autoScheduled = true;
            EditorApplication.delayCall += ProcessAutoQueue;
        }

        private static void ProcessAutoQueue()
        {
            s_autoScheduled = false;
            if (s_autoQueue.Count == 0) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode ||
                s_suspendAuto > 0)
            {
                ScheduleAutoQueue(); // try again on a later tick
                return;
            }

            var models = new List<string>();
            var clips = new List<string>();
            foreach (string p in s_autoQueue)
            {
                if (RocketboxAssetSet.IsAvatarModelPath(p)) models.Add(p);
                else if (RocketboxAssetSet.IsAnimationPath(p)) clips.Add(p);
            }
            s_autoQueue.Clear();
            models.Sort(StringComparer.OrdinalIgnoreCase);
            clips.Sort(StringComparer.OrdinalIgnoreCase);

            int configuredModels = 0, configuredClips = 0, pendingClips = 0;
            var problems = new List<string>();
            using (SuspendAutoConfigure())
            {
                using (new AssetEditingScope())
                {
                    foreach (string model in models)
                    {
                        if (HasMarker(AssetImporter.GetAtPath(model))) continue; // configured meanwhile (e.g. by the pipeline)
                        if (ConfigureModel(model, true, out string msg)) configuredModels++;
                        else problems.Add(msg);
                    }
                }

                // Models are re-imported now (end of the editing scope): their Humanoid avatars can be copied by the clips.
                using (new AssetEditingScope())
                {
                    foreach (string clip in clips)
                    {
                        if (HasMarker(AssetImporter.GetAtPath(clip))) continue;
                        BodyType body = RocketboxAssetSet.BodyTypeOfClipFile(clip) ?? BodyType.Male;
                        string reference = FindReferenceModel(body, null, true);
                        if (reference == null)
                        {
                            pendingClips++;
                            continue;
                        }
                        if (ConfigureClip(clip, reference, true, out string msg)) configuredClips++;
                        else problems.Add(msg);
                    }
                }
            }

            if (configuredModels + configuredClips > 0)
                Debug.Log($"[Dodgeball Ultra] Rocketbox Humanoid setup: {configuredModels} avatar(s) and {configuredClips} motion-capture clip(s) configured.");
            if (pendingClips > 0)
                Debug.Log($"[Dodgeball Ultra] {pendingClips} Rocketbox clip(s) wait for a configured avatar; they are set up by 'Build Characters'.");
            foreach (string p in problems) Debug.LogWarning("[Dodgeball Ultra] " + p);
        }

        // ================================================================================================= markers

        /// <summary>True when the importer carries any Dodgeball Ultra humanoid marker (configured or failed, current version).</summary>
        public static bool HasMarker(AssetImporter importer)
        {
            string data = importer != null ? importer.userData : null;
            if (string.IsNullOrEmpty(data)) return false;
            return data == ModelMarker || data.StartsWith(ClipMarkerPrefix, StringComparison.Ordinal) ||
                   data.StartsWith(FailedMarkerPrefix, StringComparison.Ordinal);
        }

        /// <summary>True when the model is configured by this builder and its Humanoid avatar is valid.</summary>
        public static bool IsModelConfigured(string modelPath)
        {
            if (!(AssetImporter.GetAtPath(modelPath) is ModelImporter importer)) return false;
            if (importer.userData != ModelMarker || importer.animationType != ModelImporterAnimationType.Human) return false;
            Avatar avatar = LoadAvatar(modelPath);
            return avatar != null && avatar.isValid && avatar.isHuman;
        }

        /// <summary>True when the clip copies the avatar of <paramref name="referenceModelPath"/> with the current settings.</summary>
        public static bool IsClipConfigured(string clipPath, string referenceModelPath)
        {
            if (!(AssetImporter.GetAtPath(clipPath) is ModelImporter importer)) return false;
            string guid = AssetDatabase.AssetPathToGUID(referenceModelPath);
            return importer.userData == ClipMarkerPrefix + guid &&
                   importer.animationType == ModelImporterAnimationType.Human &&
                   importer.sourceAvatar != null;
        }

        /// <summary>The reference model a configured clip copies its avatar from (null when not configured by us).</summary>
        public static string GetClipReferenceModel(string clipPath)
        {
            string data = AssetImporter.GetAtPath(clipPath)?.userData;
            if (string.IsNullOrEmpty(data) || !data.StartsWith(ClipMarkerPrefix, StringComparison.Ordinal)) return null;
            string path = AssetDatabase.GUIDToAssetPath(data.Substring(ClipMarkerPrefix.Length));
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>Failure reason recorded on a model/clip importer (null when none).</summary>
        public static string GetFailure(string assetPath)
        {
            string data = AssetImporter.GetAtPath(assetPath)?.userData;
            if (string.IsNullOrEmpty(data) || !data.StartsWith(FailedMarkerPrefix, StringComparison.Ordinal)) return null;
            return data.Length > FailedMarkerPrefix.Length + 1 ? data.Substring(FailedMarkerPrefix.Length + 1) : "unknown error";
        }

        // ============================================================================================ configuration

        /// <summary>The Avatar sub-asset of a model (null when absent).</summary>
        public static Avatar LoadAvatar(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath)) return null;
            foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(modelPath))
                if (o is Avatar a) return a;
            return null;
        }

        /// <summary>
        /// Configures an avatar model as Humanoid (explicit Biped map, enforced T-pose). With <paramref name="reimport"/> the
        /// importer is saved and re-imported (deferred until <see cref="AssetDatabase.StopAssetEditing"/> when batching).
        /// Never throws: failures are returned in <paramref name="message"/> and recorded in the importer marker.
        /// </summary>
        public static bool ConfigureModel(string modelPath, bool reimport, out string message)
        {
            string file = Path.GetFileName(modelPath);
            if (!(AssetImporter.GetAtPath(modelPath) is ModelImporter importer))
            {
                message = $"{file}: no ModelImporter (is the file imported?)";
                return false;
            }
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (asset == null)
            {
                message = $"{file}: model could not be loaded";
                return false;
            }

            HumanDescription description;
            string error;
            bool built;
            try
            {
                built = TryBuildHumanDescription(asset, out description, out error);
            }
            catch (Exception ex)
            {
                built = false;
                description = default;
                error = ex.Message;
            }

            if (!built)
            {
                importer.userData = FailedMarkerPrefix + ":" + (error ?? "unknown").Replace('\n', ' ');
                if (reimport) importer.SaveAndReimport();
                message = $"{file}: Humanoid setup failed - {error}";
                return false;
            }

            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.autoGenerateAvatarMappingIfUnspecified = false;
            importer.humanDescription = description;
            importer.userData = ModelMarker;
            if (reimport) importer.SaveAndReimport();
            message = $"{file}: Humanoid ({description.human.Length} bones mapped, T-pose enforced)";
            return true;
        }

        /// <summary>
        /// Configures a motion-capture clip file to copy the Humanoid avatar of <paramref name="referenceModelPath"/> and
        /// applies the clip settings (see class docs).
        /// </summary>
        public static bool ConfigureClip(string clipPath, string referenceModelPath, bool reimport, out string message)
        {
            string file = Path.GetFileName(clipPath);
            if (!(AssetImporter.GetAtPath(clipPath) is ModelImporter importer))
            {
                message = $"{file}: no ModelImporter (is the file imported?)";
                return false;
            }
            Avatar avatar = LoadAvatar(referenceModelPath);
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                message = $"{file}: reference avatar '{Path.GetFileName(referenceModelPath)}' is not a valid Humanoid";
                return false;
            }

            ModelImporterClipAnimation[] takes = importer.defaultClipAnimations;
            if (takes == null || takes.Length == 0) takes = importer.clipAnimations;
            if (takes == null || takes.Length == 0)
            {
                importer.userData = FailedMarkerPrefix + ":no animation take";
                if (reimport) importer.SaveAndReimport();
                message = $"{file}: contains no animation take";
                return false;
            }

            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            importer.sourceAvatar = avatar;
            importer.importAnimation = true;

            bool loop = RocketboxAssetSet.IsLoopingClipFile(clipPath);
            string clipName = RocketboxAssetSet.ClipNameFromFile(clipPath);
            var clips = new ModelImporterClipAnimation[takes.Length];
            for (int i = 0; i < takes.Length; i++)
            {
                ModelImporterClipAnimation c = takes[i];
                c.name = takes.Length == 1 ? clipName : clipName + "_" + (i + 1);
                ApplyClipSettings(c, loop);
                clips[i] = c;
            }
            importer.clipAnimations = clips;
            importer.userData = ClipMarkerPrefix + AssetDatabase.AssetPathToGUID(referenceModelPath);
            if (reimport) importer.SaveAndReimport();
            message = $"{file}: Humanoid clip '{clipName}' ({(loop ? "loop" : "once")}), avatar copied from {Path.GetFileName(referenceModelPath)}";
            return true;
        }

        /// <summary>
        /// Clip settings for in-place humanoid motion capture:
        /// root rotation and height baked into the pose (based on the original authoring), planar root motion left as root
        /// motion (not baked) so the runtime - which never applies root motion - plays locomotion in place.
        /// </summary>
        public static void ApplyClipSettings(ModelImporterClipAnimation clip, bool loop)
        {
            clip.loopTime = loop;
            clip.loopPose = loop;          // blend the last frames into the first for seamless mocap cycles
            clip.cycleOffset = 0f;
            clip.mirror = false;

            clip.lockRootRotation = true;  // Bake Into Pose: rotation
            clip.keepOriginalOrientation = true;
            clip.rotationOffset = 0f;

            clip.lockRootHeightY = true;   // Bake Into Pose: Y
            clip.keepOriginalPositionY = true;
            clip.heightFromFeet = false;
            clip.heightOffset = 0f;

            clip.lockRootPositionXZ = false; // XZ stays root motion (discarded at runtime)
            clip.keepOriginalPositionXZ = false;

            clip.maskType = ClipAnimationMaskType.None;
            clip.wrapMode = loop ? WrapMode.Loop : WrapMode.ClampForever;
        }

        /// <summary>
        /// A configured avatar model to copy the Humanoid definition from: the first one of <paramref name="body"/> among
        /// <paramref name="preferredModelPaths"/>, then among all downloaded avatars; with <paramref name="allowOtherBody"/>
        /// an avatar of the other body type is accepted as a last resort (retargeting still works, bone lengths differ).
        /// </summary>
        public static string FindReferenceModel(BodyType body, IEnumerable<string> preferredModelPaths, bool allowOtherBody)
        {
            var candidates = new List<string>();
            if (preferredModelPaths != null) candidates.AddRange(preferredModelPaths);
            candidates.AddRange(EnumerateDownloadedModels());

            RocketboxManifest manifest = RocketboxAssetSet.LoadManifest();
            foreach (string path in candidates)
            {
                string avatar = RocketboxAssetSet.AvatarNameFromPath(path);
                if (avatar == null || RocketboxAssetSet.GetAvatarBodyType(avatar, manifest) != body) continue;
                if (IsModelConfigured(path)) return path;
            }
            if (!allowOtherBody) return null;
            foreach (string path in candidates)
                if (IsModelConfigured(path)) return path;
            return null;
        }

        /// <summary>Downloaded avatar model paths found on disk (Avatars/*/Export/*.fbx, facial variants excluded).</summary>
        public static List<string> EnumerateDownloadedModels()
        {
            var result = new List<string>();
            string root = RocketboxAssetSet.ToAbsolutePath(RocketboxAssetSet.AvatarsFolder);
            if (!Directory.Exists(root)) return result;
            string[] avatarDirs = Directory.GetDirectories(root);
            Array.Sort(avatarDirs, StringComparer.OrdinalIgnoreCase);
            foreach (string dir in avatarDirs)
            {
                string exportDir = Path.Combine(dir, "Export");
                if (!Directory.Exists(exportDir)) continue;
                foreach (string fbx in Directory.GetFiles(exportDir, "*.fbx"))
                {
                    string assetPath = RocketboxAssetSet.AvatarsFolder + "/" + Path.GetFileName(dir) + "/Export/" + Path.GetFileName(fbx);
                    if (RocketboxAssetSet.IsAvatarModelPath(assetPath)) result.Add(assetPath);
                }
            }
            return result;
        }

        // ======================================================================================= human description

        /// <summary>
        /// Builds the <see cref="HumanDescription"/> of a Biped model with an enforced T-pose and validates it with
        /// <see cref="AvatarBuilder.BuildHumanAvatar"/> (nothing is written to the asset).
        /// </summary>
        public static bool TryBuildHumanDescription(GameObject modelAsset, out HumanDescription description, out string error)
        {
            description = default;
            error = null;
            if (modelAsset == null)
            {
                error = "model is null";
                return false;
            }

            GameObject instance = Object.Instantiate(modelAsset);
            instance.hideFlags = HideFlags.HideAndDontSave;
            instance.name = modelAsset.name; // skeleton[0] must carry the model's root name, not "(Clone)"
            try
            {
                Transform root = instance.transform;
                root.localPosition = Vector3.zero;
                root.localRotation = Quaternion.identity;
                root.localScale = Vector3.one;

                Dictionary<HumanBodyBones, Transform> bones = RocketboxBipedMap.Resolve(root, out string prefix);
                if (prefix == null)
                {
                    error = "no 3ds Max Biped skeleton found ('Bip01 Pelvis' missing)";
                    return false;
                }

                var missing = new List<string>();
                for (int i = 0; i < HumanTrait.BoneCount; i++)
                    if (HumanTrait.RequiredBone(i) && !bones.ContainsKey((HumanBodyBones)i)) missing.Add(HumanTrait.BoneName[i]);
                if (missing.Count > 0)
                {
                    error = "required Humanoid bones not found: " + string.Join(", ", missing);
                    return false;
                }

                EnforceTPose(root, bones);

                // A SkeletonBone for every transform (root first, depth-first), capturing the T-pose.
                Transform[] all = root.GetComponentsInChildren<Transform>(true);
                var skeleton = new SkeletonBone[all.Length];
                for (int i = 0; i < all.Length; i++)
                {
                    Transform t = all[i];
                    skeleton[i] = new SkeletonBone
                    {
                        name = t == root ? modelAsset.name : t.name,
                        position = t.localPosition,
                        rotation = t.localRotation,
                        scale = t.localScale,
                    };
                }

                var human = new List<HumanBone>(bones.Count);
                foreach (KeyValuePair<HumanBodyBones, Transform> kv in bones)
                {
                    var hb = new HumanBone { humanName = RocketboxBipedMap.HumanName(kv.Key), boneName = kv.Value.name };
                    hb.limit.useDefaultValues = true;
                    human.Add(hb);
                }

                description = new HumanDescription
                {
                    human = human.ToArray(),
                    skeleton = skeleton,
                    upperArmTwist = UpperArmTwist,
                    lowerArmTwist = LowerArmTwist,
                    upperLegTwist = UpperLegTwist,
                    lowerLegTwist = LowerLegTwist,
                    armStretch = ArmStretch,
                    legStretch = LegStretch,
                    feetSpacing = FeetSpacing,
                    hasTranslationDoF = false,
                };

                // Validate before touching the importer: Mecanim reports mapping / hierarchy problems here.
                Avatar test = AvatarBuilder.BuildHumanAvatar(instance, description);
                try
                {
                    if (test == null || !test.isValid || !test.isHuman)
                    {
                        error = "Mecanim rejected the Humanoid description (see the Console for details)";
                        return false;
                    }
                }
                finally
                {
                    if (test != null) Object.DestroyImmediate(test);
                }
                return true;
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        // ================================================================================================ T-pose

        /// <summary>
        /// Rotates a Biped A-pose into Mecanim's reference T-pose in place (world space, model at the origin):
        /// arms horizontal along the character's lateral axis with elbows hinging forward and palms down, legs vertical with
        /// knees hinging forward and the authored (flat) foot orientation kept. Spine, neck, head, clavicles and fingers
        /// keep their modelled pose - exactly what Unity's "Enforce T-Pose" changes.
        /// </summary>
        public static void EnforceTPose(Transform root, IReadOnlyDictionary<HumanBodyBones, Transform> bones)
        {
            Vector3 up = root.up;
            Vector3 right = Horizontal(bones[HumanBodyBones.RightUpperArm].position - bones[HumanBodyBones.LeftUpperArm].position, up);
            if (right.sqrMagnitude < 1e-8f)
                right = Horizontal(bones[HumanBodyBones.RightUpperLeg].position - bones[HumanBodyBones.LeftUpperLeg].position, up);
            right = right.sqrMagnitude < 1e-8f ? root.right : right.normalized;
            // Anatomical forward (Unity is left-handed: right x up = forward).
            Vector3 forward = Vector3.Cross(right, up).normalized;

            AlignArm(bones, true, -right, forward);
            AlignArm(bones, false, right, forward);
            AlignLeg(bones, true, -up, forward);
            AlignLeg(bones, false, -up, forward);
        }

        private static void AlignArm(IReadOnlyDictionary<HumanBodyBones, Transform> bones, bool left, Vector3 side, Vector3 forward)
        {
            Transform upper = bones[left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm];
            Transform lower = bones[left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm];
            Transform hand = bones[left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand];
            Transform middle = Get(bones, left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            Transform index = Get(bones, left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            Transform little = Get(bones, left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);

            // Where the hand travels when the elbow flexes, stored in upper-arm space before anything moves.
            bool hasFlex = TryGetFlexDirection(upper, lower, hand, out Vector3 flexLocal);

            AimBone(upper, lower.position, side);
            // Elbow flexion must bring the hand forward (Mecanim's arm muscle frame).
            if (hasFlex) TwistAbout(upper, side, upper.rotation * flexLocal, forward);
            AimBone(lower, hand.position, side);

            if (middle != null) AimBone(hand, middle.position, side);
            // Palms down: with the arm out to the side, the index finger (thumb side) leads forward on both hands.
            if (index != null && little != null) TwistAbout(hand, side, index.position - little.position, forward);
        }

        private static void AlignLeg(IReadOnlyDictionary<HumanBodyBones, Transform> bones, bool left, Vector3 down, Vector3 forward)
        {
            Transform upper = bones[left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg];
            Transform lower = bones[left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg];
            Transform foot = bones[left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot];
            Quaternion footRotation = foot.rotation;

            bool hasFlex = TryGetFlexDirection(upper, lower, foot, out Vector3 flexLocal);
            AimBone(upper, lower.position, down);
            // Knee flexion must send the foot backwards.
            if (hasFlex) TwistAbout(upper, down, upper.rotation * flexLocal, -forward);
            AimBone(lower, foot.position, down);

            foot.rotation = footRotation; // keep the authored, flat-on-the-ground foot
        }

        /// <summary>
        /// Direction (in <paramref name="upper"/>'s local space) in which the end of a two-bone chain moves when the middle
        /// joint flexes; false when the chain is (almost) straight and the hinge is therefore undefined.
        /// </summary>
        private static bool TryGetFlexDirection(Transform upper, Transform lower, Transform end, out Vector3 flexLocal)
        {
            Vector3 a = (lower.position - upper.position).normalized;
            Vector3 b = (end.position - lower.position).normalized;
            Vector3 flex = b - Vector3.Dot(b, a) * a;
            if (flex.magnitude < 0.05f) // < ~3 degrees of bend
            {
                flexLocal = Vector3.zero;
                return false;
            }
            flexLocal = Quaternion.Inverse(upper.rotation) * flex.normalized;
            return true;
        }

        /// <summary>Rotates <paramref name="bone"/> (minimal rotation) so the direction to <paramref name="childPosition"/> equals <paramref name="direction"/>.</summary>
        private static void AimBone(Transform bone, Vector3 childPosition, Vector3 direction)
        {
            Vector3 current = childPosition - bone.position;
            if (current.sqrMagnitude < 1e-10f || direction.sqrMagnitude < 1e-10f) return;
            bone.rotation = Quaternion.FromToRotation(current.normalized, direction.normalized) * bone.rotation;
        }

        /// <summary>Rotates <paramref name="bone"/> about <paramref name="axis"/> so <paramref name="current"/> points at <paramref name="target"/> (projected).</summary>
        private static void TwistAbout(Transform bone, Vector3 axis, Vector3 current, Vector3 target)
        {
            Vector3 a = Vector3.ProjectOnPlane(current, axis);
            Vector3 b = Vector3.ProjectOnPlane(target, axis);
            if (a.sqrMagnitude < 1e-8f || b.sqrMagnitude < 1e-8f) return;
            float angle = Vector3.SignedAngle(a, b, axis);
            bone.rotation = Quaternion.AngleAxis(angle, axis.normalized) * bone.rotation;
        }

        private static Vector3 Horizontal(Vector3 v, Vector3 up) => Vector3.ProjectOnPlane(v, up);

        private static Transform Get(IReadOnlyDictionary<HumanBodyBones, Transform> bones, HumanBodyBones b)
            => bones.TryGetValue(b, out Transform t) ? t : null;
    }
}
