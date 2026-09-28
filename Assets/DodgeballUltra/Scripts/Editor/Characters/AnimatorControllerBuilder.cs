using System;
using System.Collections.Generic;
using System.IO;
using DodgeballUltra.Characters;
using DodgeballUltra.Editor.Pipeline;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>
    /// Generates the motion-capture AnimatorControllers <c>Generated/Animation/DU_Male.controller</c> and
    /// <c>DU_Female.controller</c> from the curated Rocketbox clips (<see cref="RocketboxAssetSet.CuratedClips"/>).
    /// <para>Parameters: every <see cref="AnimatorParams"/> parameter (MotionSpeed defaults to 1, Grounded to true).</para>
    /// <para>Base layer (IK pass on, for the procedural look-at / catch reach / throw arm of <c>CharacterIKController</c>):</para>
    /// <code>
    /// Locomotion  1D blend tree on Speed: idle 0 | walk 1.6 | run 4.6 | sprint 7.4 m/s (speed x MotionSpeed, foot IK)
    /// Airborne    crouch pose played slowly (tucked jump)        &lt;- !Grounded or Jump      -> Locomotion when Grounded
    /// Crouch      crouch idle (slides, catch stance)             &lt;- Sliding or Catching    -> Locomotion when neither
    /// Stunned     drunk idle (dizzy sway)                        &lt;- Any State, Stunned    -> Locomotion when !Stunned
    /// Cheer       cheer                                          &lt;- Any State, Cheer      -> Locomotion at the end / on moving
    /// Defeat      shrug                                          &lt;- Any State, Defeat     -> Locomotion at the end / on moving
    /// </code>
    /// <para>
    /// Optional <c>UpperBody</c> layer (upper-body avatar mask, override): only when authored clips whose name contains
    /// "throw" / "catch" exist under <see cref="RocketboxAssetSet.CustomAnimationFolder"/>; Throw / Catch triggers play
    /// them over locomotion. Without them throws and catches are fully procedural (IK).
    /// </para>
    /// Existing controllers are rebuilt in place (asset GUID kept, so every reference survives).
    /// </summary>
    public static class AnimatorControllerBuilder
    {
        private const string BaseLayerName = "Base Layer";
        private const string EmptyStateName = "Empty";
        private const float GestureExitTime = 0.94f;

        /// <summary>Avatar mask shared by both controllers' UpperBody layer.</summary>
        public static string UpperBodyMaskPath => RocketboxAssetSet.GeneratedAnimationFolder + "/DU_UpperBody.mask";

        /// <summary>Controller asset path for a body type.</summary>
        public static string ControllerPath(BodyType body)
            => RocketboxAssetSet.GeneratedAnimationFolder + "/DU_" + (body == BodyType.Female ? "Female" : "Male") + ".controller";

        /// <summary>The generated controller for <paramref name="body"/> (null when not built yet).</summary>
        public static AnimatorController Load(BodyType body) => AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath(body));

        private static readonly KeyValuePair<string, AnimatorControllerParameterType>[] s_parameters =
        {
            P(AnimatorParams.Speed, AnimatorControllerParameterType.Float),
            P(AnimatorParams.VerticalSpeed, AnimatorControllerParameterType.Float),
            P(AnimatorParams.ChargeAmount, AnimatorControllerParameterType.Float),
            P(AnimatorParams.MotionSpeed, AnimatorControllerParameterType.Float),
            P(AnimatorParams.Lean, AnimatorControllerParameterType.Float),
            P(AnimatorParams.Grounded, AnimatorControllerParameterType.Bool),
            P(AnimatorParams.Sprinting, AnimatorControllerParameterType.Bool),
            P(AnimatorParams.Sliding, AnimatorControllerParameterType.Bool),
            P(AnimatorParams.Charging, AnimatorControllerParameterType.Bool),
            P(AnimatorParams.Catching, AnimatorControllerParameterType.Bool),
            P(AnimatorParams.Stunned, AnimatorControllerParameterType.Bool),
            P(AnimatorParams.Frozen, AnimatorControllerParameterType.Bool),
            P(AnimatorParams.Outfield, AnimatorControllerParameterType.Bool),
            P(AnimatorParams.HoldingBall, AnimatorControllerParameterType.Bool),
            P(AnimatorParams.Throw, AnimatorControllerParameterType.Trigger),
            P(AnimatorParams.Catch, AnimatorControllerParameterType.Trigger),
            P(AnimatorParams.Hit, AnimatorControllerParameterType.Trigger),
            P(AnimatorParams.Jump, AnimatorControllerParameterType.Trigger),
            P(AnimatorParams.Cheer, AnimatorControllerParameterType.Trigger),
            P(AnimatorParams.Defeat, AnimatorControllerParameterType.Trigger),
        };

        // ====================================================================================================== API

        /// <summary>Builds both controllers. Returns body type -> controller (entries missing on failure).</summary>
        public static Dictionary<BodyType, AnimatorController> BuildAll(List<string> log)
        {
            var result = new Dictionary<BodyType, AnimatorController>();
            foreach (BodyType body in new[] { BodyType.Male, BodyType.Female })
            {
                AnimatorController c = Build(body, log);
                if (c != null) result[body] = c;
            }
            return result;
        }

        /// <summary>Builds (or rebuilds in place) the controller of <paramref name="body"/>.</summary>
        public static AnimatorController Build(BodyType body, List<string> log)
        {
            CharacterPipelineSettings settings = CharacterPipelineSettings.Instance;
            string label = body == BodyType.Female ? "DU_Female" : "DU_Male";
            TextureAssetUtility.EnsureAssetFolder(RocketboxAssetSet.GeneratedAnimationFolder);

            // ---- clips ------------------------------------------------------------------------------------------------
            var clips = new Dictionary<RocketboxClipRole, AnimationClip>();
            var missing = new List<string>();
            foreach (RocketboxClip c in RocketboxAssetSet.CuratedClips)
            {
                string path = RocketboxAssetSet.ClipAssetPath(body, c.BaseName);
                AnimationClip clip = LoadClip(path);
                if (clip == null)
                {
                    missing.Add(RocketboxAssetSet.ClipFileName(body, c.BaseName));
                    continue;
                }
                if (!clip.humanMotion)
                    log?.Add($"{label}: '{clip.name}' is not a Humanoid clip yet (run Build Characters with an avatar downloaded).");
                clips[c.Role] = clip;
            }
            if (missing.Count > 0)
                log?.Add($"{label}: {missing.Count} motion-capture clip(s) missing ({string.Join(", ", missing)}) - download the animation set.");

            AnimationClip throwClip = FindCustomClip("throw", body);
            AnimationClip catchClip = FindCustomClip("catch", body);

            // ---- controller asset (GUID kept) --------------------------------------------------------------------------
            string controllerPath = ControllerPath(body);
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(controllerPath) != null) AssetDatabase.DeleteAsset(controllerPath);
                controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
                if (controller == null)
                {
                    log?.Add($"{label}: could not create {controllerPath}.");
                    return null;
                }
            }
            Clear(controller);
            AddParameters(controller);

            BuildBaseLayer(controller, clips, settings);
            if (throwClip != null || catchClip != null)
            {
                BuildUpperBodyLayer(controller, throwClip, catchClip, settings);
                log?.Add($"{label}: UpperBody layer with {Describe(throwClip, "Throw")}{(throwClip != null && catchClip != null ? ", " : string.Empty)}{Describe(catchClip, "Catch")}.");
            }

            EditorUtility.SetDirty(controller);
            return controller;
        }

        // ================================================================================================ structure

        private static void Clear(AnimatorController controller)
        {
            controller.layers = new AnimatorControllerLayer[0];
            controller.parameters = new AnimatorControllerParameter[0];
            // Old state machines, states, transitions and blend trees are sub-assets: remove them so rebuilds never leak.
            string path = AssetDatabase.GetAssetPath(controller);
            foreach (Object sub in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (sub == null || sub == controller) continue;
                Object.DestroyImmediate(sub, true);
            }
        }

        private static void AddParameters(AnimatorController controller)
        {
            foreach (KeyValuePair<string, AnimatorControllerParameterType> p in s_parameters) controller.AddParameter(p.Key, p.Value);

            // Defaults: normal playback speed, standing on the ground.
            AnimatorControllerParameter[] parameters = controller.parameters;
            foreach (AnimatorControllerParameter p in parameters)
            {
                if (p.name == AnimatorParams.MotionSpeed) p.defaultFloat = 1f;
                if (p.name == AnimatorParams.Grounded) p.defaultBool = true;
            }
            controller.parameters = parameters;
        }

        private static AnimatorStateMachine CreateStateMachine(AnimatorController controller, string name)
        {
            var sm = new AnimatorStateMachine
            {
                name = name,
                hideFlags = HideFlags.HideInHierarchy,
                anyStatePosition = new Vector3(20f, 20f, 0f),
                entryPosition = new Vector3(20f, 120f, 0f),
                exitPosition = new Vector3(20f, 220f, 0f),
            };
            AssetDatabase.AddObjectToAsset(sm, controller);
            return sm;
        }

        private static void BuildBaseLayer(AnimatorController controller, Dictionary<RocketboxClipRole, AnimationClip> clips,
            CharacterPipelineSettings settings)
        {
            AnimatorStateMachine sm = CreateStateMachine(controller, BaseLayerName);
            controller.AddLayer(new AnimatorControllerLayer
            {
                name = BaseLayerName,
                stateMachine = sm,
                defaultWeight = 1f,
                iKPass = true,
                blendingMode = AnimatorLayerBlendingMode.Override,
            });

            // ---- states ---------------------------------------------------------------------------------------------
            AnimatorState locomotion = sm.AddState(AnimatorParams.StateLocomotion, new Vector3(300f, 120f, 0f));
            locomotion.motion = BuildLocomotionTree(controller, clips, settings);
            locomotion.speedParameter = AnimatorParams.MotionSpeed;
            locomotion.speedParameterActive = true;
            locomotion.iKOnFeet = true;
            sm.defaultState = locomotion;

            AnimationClip crouchClip = Get(clips, RocketboxClipRole.Crouch);
            AnimationClip idleClip = Get(clips, RocketboxClipRole.Idle);

            AnimatorState airborne = sm.AddState(AnimatorParams.StateAirborne, new Vector3(560f, 0f, 0f));
            airborne.motion = crouchClip != null ? crouchClip : idleClip;
            airborne.speed = crouchClip != null ? settings.airborneClipSpeed : 1f;

            AnimatorState crouch = sm.AddState(AnimatorParams.StateCrouch, new Vector3(560f, 120f, 0f));
            crouch.motion = crouchClip;
            crouch.iKOnFeet = true;

            AnimatorState stunned = sm.AddState(AnimatorParams.StateStunned, new Vector3(560f, 240f, 0f));
            stunned.motion = Get(clips, RocketboxClipRole.Stunned) ?? idleClip;
            stunned.iKOnFeet = true;

            AnimatorState cheer = sm.AddState(AnimatorParams.StateCheer, new Vector3(300f, 300f, 0f));
            cheer.motion = Get(clips, RocketboxClipRole.Cheer) ?? idleClip;

            AnimatorState defeat = sm.AddState(AnimatorParams.StateDefeat, new Vector3(300f, 400f, 0f));
            defeat.motion = Get(clips, RocketboxClipRole.Defeat) ?? idleClip;
            defeat.iKOnFeet = true;

            // ---- transitions (Any State order = priority) -------------------------------------------------------------
            AnimatorStateTransition t = sm.AddAnyStateTransition(stunned);
            Setup(t, settings.stunnedTransition);
            t.canTransitionToSelf = false;
            t.AddCondition(AnimatorConditionMode.If, 0f, AnimatorParams.Stunned);

            t = sm.AddAnyStateTransition(defeat);
            Setup(t, settings.defeatTransition);
            t.canTransitionToSelf = false;
            t.AddCondition(AnimatorConditionMode.If, 0f, AnimatorParams.Defeat);
            t.AddCondition(AnimatorConditionMode.IfNot, 0f, AnimatorParams.Stunned); // played once the stun ends

            t = sm.AddAnyStateTransition(cheer);
            Setup(t, settings.cheerTransition);
            t.canTransitionToSelf = false;
            t.AddCondition(AnimatorConditionMode.If, 0f, AnimatorParams.Cheer);
            t.AddCondition(AnimatorConditionMode.IfNot, 0f, AnimatorParams.Stunned);

            t = stunned.AddTransition(locomotion);
            Setup(t, settings.stunnedTransition);
            t.AddCondition(AnimatorConditionMode.IfNot, 0f, AnimatorParams.Stunned);

            // Leaving the ground (jumps, knock-ups, falling) from locomotion or a slide.
            foreach (AnimatorState from in new[] { locomotion, crouch })
            {
                t = from.AddTransition(airborne);
                Setup(t, settings.airborneTransition);
                t.AddCondition(AnimatorConditionMode.IfNot, 0f, AnimatorParams.Grounded);

                t = from.AddTransition(airborne);
                Setup(t, settings.airborneTransition);
                t.AddCondition(AnimatorConditionMode.If, 0f, AnimatorParams.Jump);
            }

            // Landing: straight into a slide, else back to locomotion.
            t = airborne.AddTransition(crouch);
            Setup(t, settings.landingTransition);
            t.AddCondition(AnimatorConditionMode.If, 0f, AnimatorParams.Grounded);
            t.AddCondition(AnimatorConditionMode.If, 0f, AnimatorParams.Sliding);

            t = airborne.AddTransition(locomotion);
            Setup(t, settings.landingTransition);
            t.AddCondition(AnimatorConditionMode.If, 0f, AnimatorParams.Grounded);

            // Crouch: slides and the catch stance.
            t = locomotion.AddTransition(crouch);
            Setup(t, settings.crouchTransition);
            t.AddCondition(AnimatorConditionMode.If, 0f, AnimatorParams.Sliding);

            t = locomotion.AddTransition(crouch);
            Setup(t, settings.crouchTransition);
            t.AddCondition(AnimatorConditionMode.If, 0f, AnimatorParams.Catching);

            t = crouch.AddTransition(locomotion);
            Setup(t, settings.crouchTransition);
            t.AddCondition(AnimatorConditionMode.IfNot, 0f, AnimatorParams.Sliding);
            t.AddCondition(AnimatorConditionMode.IfNot, 0f, AnimatorParams.Catching);

            // Gestures: back to locomotion when the clip ends, or at once when the player starts moving.
            foreach (KeyValuePair<AnimatorState, float> gesture in new[]
                     {
                         new KeyValuePair<AnimatorState, float>(cheer, settings.cheerTransition),
                         new KeyValuePair<AnimatorState, float>(defeat, settings.defeatTransition),
                     })
            {
                t = gesture.Key.AddTransition(locomotion);
                Setup(t, gesture.Value);
                t.hasExitTime = true;
                t.exitTime = GestureExitTime;

                t = gesture.Key.AddTransition(locomotion);
                Setup(t, settings.landingTransition);
                t.AddCondition(AnimatorConditionMode.Greater, settings.leaveGestureSpeed, AnimatorParams.Speed);

                t = gesture.Key.AddTransition(airborne);
                Setup(t, settings.airborneTransition);
                t.AddCondition(AnimatorConditionMode.IfNot, 0f, AnimatorParams.Grounded);
            }
        }

        private static BlendTree BuildLocomotionTree(AnimatorController controller, Dictionary<RocketboxClipRole, AnimationClip> clips,
            CharacterPipelineSettings settings)
        {
            var tree = new BlendTree
            {
                name = AnimatorParams.StateLocomotion,
                hideFlags = HideFlags.HideInHierarchy,
                blendType = BlendTreeType.Simple1D,
                blendParameter = AnimatorParams.Speed,
                useAutomaticThresholds = false,
            };
            AssetDatabase.AddObjectToAsset(tree, controller);

            var entries = new[]
            {
                new KeyValuePair<RocketboxClipRole, float>(RocketboxClipRole.Idle, AnimatorParams.IdleSpeed),
                new KeyValuePair<RocketboxClipRole, float>(RocketboxClipRole.Walk, AnimatorParams.WalkSpeed),
                new KeyValuePair<RocketboxClipRole, float>(RocketboxClipRole.Run, AnimatorParams.RunSpeed),
                new KeyValuePair<RocketboxClipRole, float>(RocketboxClipRole.Sprint, AnimatorParams.SprintSpeed),
            };

            var children = new List<ChildMotion>(entries.Length);
            foreach (KeyValuePair<RocketboxClipRole, float> e in entries)
            {
                AnimationClip clip = Get(clips, e.Key);
                if (clip == null) continue;
                float timeScale = 1f;
                if (settings.matchLocomotionSpeed && e.Value > 0.1f)
                {
                    float natural = MeasurePlanarSpeed(clip);
                    if (natural > 0.2f)
                        timeScale = Mathf.Clamp(e.Value / natural, settings.locomotionTimeScaleRange.x, settings.locomotionTimeScaleRange.y);
                }
                children.Add(new ChildMotion { motion = clip, threshold = e.Value, timeScale = timeScale, directBlendParameter = AnimatorParams.Speed });
            }
            tree.children = children.ToArray();
            return tree;
        }

        private static void BuildUpperBodyLayer(AnimatorController controller, AnimationClip throwClip, AnimationClip catchClip,
            CharacterPipelineSettings settings)
        {
            AnimatorStateMachine sm = CreateStateMachine(controller, AnimatorParams.LayerUpperBody);
            controller.AddLayer(new AnimatorControllerLayer
            {
                name = AnimatorParams.LayerUpperBody,
                stateMachine = sm,
                defaultWeight = 1f,
                avatarMask = LoadOrCreateUpperBodyMask(),
                blendingMode = AnimatorLayerBlendingMode.Override,
                iKPass = false,
            });

            // An empty default state contributes nothing: locomotion shows through until a trigger fires.
            AnimatorState empty = sm.AddState(EmptyStateName, new Vector3(300f, 120f, 0f));
            sm.defaultState = empty;

            if (throwClip != null) AddUpperBodyAction(sm, empty, AnimatorParams.StateThrow, AnimatorParams.Throw, throwClip, new Vector3(560f, 60f, 0f), settings);
            if (catchClip != null) AddUpperBodyAction(sm, empty, AnimatorParams.StateCatch, AnimatorParams.Catch, catchClip, new Vector3(560f, 180f, 0f), settings);
        }

        private static void AddUpperBodyAction(AnimatorStateMachine sm, AnimatorState empty, string stateName, string trigger,
            AnimationClip clip, Vector3 position, CharacterPipelineSettings settings)
        {
            AnimatorState state = sm.AddState(stateName, position);
            state.motion = clip;

            AnimatorStateTransition t = sm.AddAnyStateTransition(state);
            Setup(t, settings.upperBodyTransition);
            t.canTransitionToSelf = true;
            t.AddCondition(AnimatorConditionMode.If, 0f, trigger);

            t = state.AddTransition(empty);
            Setup(t, settings.upperBodyTransition * 2f);
            t.hasExitTime = true;
            t.exitTime = 0.9f;
        }

        private static AvatarMask LoadOrCreateUpperBodyMask()
        {
            var mask = new AvatarMask { name = Path.GetFileNameWithoutExtension(UpperBodyMaskPath) };
            for (AvatarMaskBodyPart part = 0; part < AvatarMaskBodyPart.LastBodyPart; part++)
            {
                bool upper = part == AvatarMaskBodyPart.Body || part == AvatarMaskBodyPart.Head ||
                             part == AvatarMaskBodyPart.LeftArm || part == AvatarMaskBodyPart.RightArm ||
                             part == AvatarMaskBodyPart.LeftFingers || part == AvatarMaskBodyPart.RightFingers ||
                             part == AvatarMaskBodyPart.LeftHandIK || part == AvatarMaskBodyPart.RightHandIK;
                mask.SetHumanoidBodyPartActive(part, upper);
            }

            var existing = AssetDatabase.LoadAssetAtPath<AvatarMask>(UpperBodyMaskPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(mask, existing);
                Object.DestroyImmediate(mask);
                EditorUtility.SetDirty(existing);
                return existing;
            }
            AssetDatabase.CreateAsset(mask, UpperBodyMaskPath);
            return mask;
        }

        private static void Setup(AnimatorStateTransition t, float duration)
        {
            t.hasExitTime = false;
            t.exitTime = 0f;
            t.hasFixedDuration = true;
            t.duration = Mathf.Max(0f, duration);
            t.offset = 0f;
            t.interruptionSource = TransitionInterruptionSource.Destination;
            t.orderedInterruption = true;
        }

        // ====================================================================================================== clips

        /// <summary>The animation clip of a Rocketbox clip file (named after the file), or null when absent.</summary>
        public static AnimationClip LoadClip(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !RocketboxAssetSet.FileExistsNonEmpty(assetPath)) return null;
            string expected = RocketboxAssetSet.ClipNameFromFile(assetPath);
            AnimationClip first = null;
            foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(assetPath))
            {
                if (!(o is AnimationClip clip) || clip.name.StartsWith("__preview__", StringComparison.Ordinal)) continue;
                if (string.Equals(clip.name, expected, StringComparison.OrdinalIgnoreCase)) return clip;
                if (first == null) first = clip;
            }
            return first;
        }

        /// <summary>
        /// First authored clip under <see cref="RocketboxAssetSet.CustomAnimationFolder"/> whose clip or file name contains
        /// <paramref name="token"/> (case-insensitive). Clips whose file carries the body's m_/f_ prefix (or "male"/"female")
        /// are preferred.
        /// </summary>
        public static AnimationClip FindCustomClip(string token, BodyType body)
        {
            string folder = RocketboxAssetSet.CustomAnimationFolder;
            if (!AssetDatabase.IsValidFolder(folder)) return null;

            var candidates = new List<KeyValuePair<string, AnimationClip>>();
            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { folder }))
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));

            foreach (string path in paths)
            {
                string file = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (!(o is AnimationClip clip) || clip.name.StartsWith("__preview__", StringComparison.Ordinal)) continue;
                    if (clip.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0 &&
                        file.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    candidates.Add(new KeyValuePair<string, AnimationClip>(file, clip));
                }
            }
            if (candidates.Count == 0) return null;

            foreach (KeyValuePair<string, AnimationClip> c in candidates)
                if (MatchesBody(c.Key, body)) return c.Value;
            foreach (KeyValuePair<string, AnimationClip> c in candidates)
                if (!MatchesBody(c.Key, body == BodyType.Male ? BodyType.Female : BodyType.Male)) return c.Value;
            return candidates[0].Value;
        }

        private static bool MatchesBody(string file, BodyType body)
        {
            string f = file.ToLowerInvariant();
            if (body == BodyType.Female) return f.StartsWith("f_", StringComparison.Ordinal) || f.Contains("female");
            return f.StartsWith("m_", StringComparison.Ordinal) || (f.Contains("male") && !f.Contains("female"));
        }

        /// <summary>
        /// Average planar root speed of a clip in m/s (Mecanim's <c>Motion.averageSpeed</c>, an editor-only member read by
        /// reflection; falls back to the RootT curves). 0 for in-place clips.
        /// </summary>
        public static float MeasurePlanarSpeed(AnimationClip clip)
        {
            if (clip == null || clip.length <= 0f) return 0f;
            if (EditorOnlyApi.TryGet(clip, "averageSpeed", out Vector3 average))
            {
                average.y = 0f;
                if (average.sqrMagnitude > 1e-6f) return average.magnitude;
            }

            AnimationCurve x = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), "RootT.x"));
            AnimationCurve z = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), "RootT.z"));
            if (x == null || z == null || x.length < 2 || z.length < 2) return 0f;
            float dx = x.Evaluate(clip.length) - x.Evaluate(0f);
            float dz = z.Evaluate(clip.length) - z.Evaluate(0f);
            return Mathf.Sqrt(dx * dx + dz * dz) / clip.length;
        }

        // ==================================================================================================== helpers

        private static AnimationClip Get(Dictionary<RocketboxClipRole, AnimationClip> clips, RocketboxClipRole role)
            => clips.TryGetValue(role, out AnimationClip c) ? c : null;

        private static string Describe(AnimationClip clip, string state) => clip != null ? $"{state} = '{clip.name}'" : string.Empty;

        private static KeyValuePair<string, AnimatorControllerParameterType> P(string name, AnimatorControllerParameterType type)
            => new KeyValuePair<string, AnimatorControllerParameterType>(name, type);
    }
}
