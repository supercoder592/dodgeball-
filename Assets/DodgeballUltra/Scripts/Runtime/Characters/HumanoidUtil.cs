using System.Collections.Generic;
using DodgeballUltra.Juice;
using UnityEngine;

namespace DodgeballUltra.Characters
{
    /// <summary>
    /// Palm geometry of one hand, expressed in the hand bone's local space (pose independent).
    /// Computed from the finger knuckles so it works for any Humanoid rig (Rocketbox Biped, Mixamo, CC, MetaHuman...).
    /// </summary>
    public struct PalmFrame
    {
        /// <summary>True when the frame could be computed (the hand bone exists).</summary>
        public bool Valid;
        /// <summary>Centre of the palm (between the wrist and the middle-finger knuckle), hand-local.</summary>
        public Vector3 CenterLocal;
        /// <summary>Unit normal pointing out of the palm (the side a ball rests against), hand-local.</summary>
        public Vector3 NormalLocal;
        /// <summary>Unit direction from the wrist toward the fingers (orthogonal to <see cref="NormalLocal"/>), hand-local.</summary>
        public Vector3 FingerLocal;
        /// <summary>Wrist to middle-finger knuckle distance (m, world units at build time).</summary>
        public float HandLength;

        /// <summary>World-space palm normal for the hand's current pose.</summary>
        public Vector3 NormalWorld(Transform hand) => hand != null ? hand.TransformDirection(NormalLocal) : Vector3.up;

        /// <summary>World-space finger direction for the hand's current pose.</summary>
        public Vector3 FingerWorld(Transform hand) => hand != null ? hand.TransformDirection(FingerLocal) : Vector3.forward;

        /// <summary>
        /// World rotation the hand bone must have so that its palm faces exactly <paramref name="palmNormalWorld"/> and its
        /// fingers point as close as possible to <paramref name="fingerWorld"/>. Used by the IK controller to orient hands
        /// (palms toward the ball).
        /// </summary>
        public Quaternion BoneRotationFor(Vector3 fingerWorld, Vector3 palmNormalWorld)
        {
            if (palmNormalWorld.sqrMagnitude < 1e-8f) palmNormalWorld = Vector3.up;
            palmNormalWorld.Normalize();
            Vector3 finger = Vector3.ProjectOnPlane(fingerWorld, palmNormalWorld);
            if (finger.sqrMagnitude < 1e-8f) finger = Vector3.ProjectOnPlane(Vector3.forward, palmNormalWorld);
            if (finger.sqrMagnitude < 1e-8f) finger = Vector3.ProjectOnPlane(Vector3.right, palmNormalWorld);

            // Columns: Z = fingers, Y = palm normal (both orthonormal), in bone-local and in world space.
            Quaternion localBasis = Quaternion.LookRotation(FingerLocal, NormalLocal);
            Quaternion worldBasis = Quaternion.LookRotation(finger.normalized, palmNormalWorld);
            return worldBasis * Quaternion.Inverse(localBasis);
        }
    }

    /// <summary>
    /// Stateless helpers for the realistic Humanoid characters: skeleton queries, palm frames, spine twist, model
    /// sanitising (physics stripping, LOD groups for raw Rocketbox FBX exports) and material colour lookup.
    /// All helpers are allocation-free unless documented otherwise (build-time helpers may allocate once).
    /// </summary>
    public static class HumanoidUtil
    {
        /// <summary>Stature (m) of the reference adult all procedural offsets are authored for.</summary>
        public const float ReferenceStature = 1.78f;

        /// <summary>Height (m) of the Humanoid Head bone (base of the skull) above the soles for <see cref="ReferenceStature"/>.</summary>
        public const float ReferenceHeadBoneHeight = 1.60f;

        /// <summary>Ordered colour properties used by lit/unlit materials of every pipeline (HDRP Lit, HDRP Unlit, URP, Built-in).</summary>
        private static readonly int[] s_colorProperties =
        {
            Shader.PropertyToID("_BaseColor"),
            Shader.PropertyToID("_UnlitColor"),
            Shader.PropertyToID("_Color"),
            Shader.PropertyToID("_TintColor"),
        };

        // ------------------------------------------------------------------ skeleton

        /// <summary>True when <paramref name="animator"/> drives a valid Humanoid avatar (IK, ragdoll and retargeted mocap available).</summary>
        public static bool IsValidHumanoid(Animator animator)
        {
            if (animator == null) return false;
            Avatar avatar = animator.avatar;
            return avatar != null && avatar.isValid && avatar.isHuman && animator.isHuman;
        }

        /// <summary>
        /// Character axes (right, up, forward) derived from the skeleton itself: up = hips to head, right = left to right
        /// hip joint. Independent of how the model root is rotated. Returns false when the bones are missing.
        /// </summary>
        public static bool TryGetSkeletonAxes(Animator animator, out Vector3 right, out Vector3 up, out Vector3 forward)
        {
            right = Vector3.right;
            up = Vector3.up;
            forward = Vector3.forward;
            if (!IsValidHumanoid(animator)) return false;

            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            Transform lLeg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            Transform rLeg = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            if (hips == null || head == null || lLeg == null || rLeg == null) return false;

            Vector3 u = head.position - hips.position;
            Vector3 r = rLeg.position - lLeg.position;
            if (u.sqrMagnitude < 1e-6f || r.sqrMagnitude < 1e-8f) return false;

            u.Normalize();
            r = Vector3.ProjectOnPlane(r, u);
            if (r.sqrMagnitude < 1e-8f) return false;
            r.Normalize();

            // Unity is left-handed: Cross(right, up) = forward.
            forward = Vector3.Cross(r, u).normalized;
            right = r;
            up = u;
            return true;
        }

        /// <summary>
        /// Stature scale of the current skeleton relative to <see cref="ReferenceStature"/> (1 = 1.78 m adult), measured from
        /// the head bone height above the lowest foot. Clamped to a plausible adult range.
        /// </summary>
        public static float EstimateStatureScale(Animator animator, Vector3 up)
        {
            if (!IsValidHumanoid(animator)) return 1f;
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            Transform lFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform rFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (head == null || lFoot == null || rFoot == null) return 1f;

            // Foot bones sit ~8 cm above the sole (ankle).
            float footHeight = Mathf.Min(Vector3.Dot(lFoot.position, up), Vector3.Dot(rFoot.position, up)) - 0.08f;
            float headHeight = Vector3.Dot(head.position, up) - footHeight;
            return Mathf.Clamp(headHeight / ReferenceHeadBoneHeight, 0.7f, 1.35f);
        }

        /// <summary>Upper arm + forearm length (m) of one arm (shoulder joint to wrist).</summary>
        public static float MeasureArmLength(Animator animator, bool right)
        {
            if (!IsValidHumanoid(animator)) return 0.58f;
            Transform upper = animator.GetBoneTransform(right ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm);
            Transform lower = animator.GetBoneTransform(right ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm);
            Transform hand = animator.GetBoneTransform(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            if (upper == null || lower == null || hand == null) return 0.58f;
            return Vector3.Distance(upper.position, lower.position) + Vector3.Distance(lower.position, hand.position);
        }

        /// <summary>
        /// Computes the palm frame of one hand from the finger knuckles:
        /// finger axis = wrist to middle knuckle, lateral axis = little to index knuckle, palm normal = their cross product
        /// (sign chosen per hand so it points out of the palm). Falls back to the forearm direction and the model's forward
        /// (thumb forward in a T-pose) when finger bones are not mapped.
        /// </summary>
        public static PalmFrame ComputePalmFrame(Animator animator, bool right, float palmCentreAlongHand = 0.55f)
        {
            var frame = new PalmFrame();
            if (!IsValidHumanoid(animator)) return frame;

            Transform hand = animator.GetBoneTransform(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            if (hand == null) return frame;
            Transform lower = animator.GetBoneTransform(right ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm);
            Transform middle = animator.GetBoneTransform(right ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal);
            Transform index = animator.GetBoneTransform(right ? HumanBodyBones.RightIndexProximal : HumanBodyBones.LeftIndexProximal);
            Transform little = animator.GetBoneTransform(right ? HumanBodyBones.RightLittleProximal : HumanBodyBones.LeftLittleProximal);
            Transform ring = animator.GetBoneTransform(right ? HumanBodyBones.RightRingProximal : HumanBodyBones.LeftRingProximal);
            if (little == null) little = ring;

            Vector3 wrist = hand.position;
            Vector3 knuckle;
            if (middle != null) knuckle = middle.position;
            else if (index != null && little != null) knuckle = (index.position + little.position) * 0.5f;
            else if (lower != null) knuckle = wrist + (wrist - lower.position).normalized * 0.09f;
            else knuckle = wrist + hand.TransformDirection(Vector3.right) * 0.09f;

            Vector3 finger = knuckle - wrist;
            float handLength = finger.magnitude;
            if (handLength < 1e-4f) return frame;
            finger /= handLength;

            // Lateral axis points from the little finger toward the index finger (thumb side).
            Vector3 lateral = index != null && little != null
                ? index.position - little.position
                : animator.transform.forward; // T-pose: thumbs point forward
            lateral = Vector3.ProjectOnPlane(lateral, finger);
            if (lateral.sqrMagnitude < 1e-8f) lateral = Vector3.ProjectOnPlane(animator.transform.forward, finger);
            if (lateral.sqrMagnitude < 1e-8f) lateral = Vector3.ProjectOnPlane(Vector3.forward, finger);
            lateral.Normalize();

            // T-pose check: right hand fingers +X, index toward +Z -> Cross(X, Z) = -Y (palm down). Left hand mirrors.
            Vector3 normal = right ? Vector3.Cross(finger, lateral) : Vector3.Cross(lateral, finger);
            normal.Normalize();

            frame.Valid = true;
            frame.HandLength = handLength;
            frame.CenterLocal = hand.InverseTransformPoint(wrist + finger * (handLength * Mathf.Clamp01(palmCentreAlongHand)));
            frame.NormalLocal = hand.InverseTransformDirection(normal).normalized;
            Vector3 fingerLocal = hand.InverseTransformDirection(finger);
            fingerLocal = Vector3.ProjectOnPlane(fingerLocal, frame.NormalLocal);
            frame.FingerLocal = fingerLocal.sqrMagnitude > 1e-8f ? fingerLocal.normalized : Vector3.right;
            return frame;
        }

        // ------------------------------------------------------------------ spine twist

        /// <summary>Fills <paramref name="bones"/> (length &gt;= 3) with Spine, Chest, UpperChest (those that exist). Returns the count.</summary>
        public static int GetSpineChain(Animator animator, Transform[] bones)
        {
            int count = 0;
            if (!IsValidHumanoid(animator) || bones == null) return 0;
            Transform spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            Transform chest = animator.GetBoneTransform(HumanBodyBones.Chest);
            Transform upperChest = animator.GetBoneTransform(HumanBodyBones.UpperChest);
            if (spine != null && count < bones.Length) bones[count++] = spine;
            if (chest != null && count < bones.Length) bones[count++] = chest;
            if (upperChest != null && count < bones.Length) bones[count++] = upperChest;
            return count;
        }

        /// <summary>Share of a spine twist carried by bone <paramref name="index"/> of a chain of <paramref name="count"/> bones.</summary>
        public static float TwistFraction(int index, int count)
        {
            switch (count)
            {
                case 1: return 1f;
                case 2: return index == 0 ? 0.45f : 0.55f;
                case 3: return index == 0 ? 0.3f : 0.35f;
                default: return count > 0 ? 1f / count : 0f;
            }
        }

        /// <summary>
        /// Rotates the spine chain by <paramref name="degrees"/> about <paramref name="axisWorld"/>, distributed over the bones,
        /// AFTER the Animator wrote the pose (LateUpdate). The Animator rewrites every bone each evaluated frame, so the twist
        /// never accumulates; if a bone still holds the value written last frame (Animator paused at timeScale 0, disabled
        /// or culled) the bone is skipped to avoid compounding the rotation.
        /// </summary>
        /// <param name="lastWritten">Per-bone local rotation written last time (same length as bones); updated here.</param>
        public static void ApplySpineTwist(Transform[] bones, int count, Quaternion[] lastWritten, Vector3 axisWorld, float degrees)
        {
            if (bones == null || lastWritten == null) return;
            if (Mathf.Abs(degrees) < 0.01f || axisWorld.sqrMagnitude < 1e-8f)
            {
                // Nothing written this frame: invalidate so the next twist never mistakes an animated value for ours.
                for (int i = 0; i < count && i < lastWritten.Length; i++) lastWritten[i] = default;
                return;
            }

            for (int i = 0; i < count && i < bones.Length && i < lastWritten.Length; i++)
            {
                Transform bone = bones[i];
                if (bone == null) continue;
                Quaternion local = bone.localRotation;
                if (ExactlyEqual(local, lastWritten[i])) continue; // not re-animated this frame -> would compound
                bone.rotation = Quaternion.AngleAxis(degrees * TwistFraction(i, count), axisWorld) * bone.rotation;
                lastWritten[i] = bone.localRotation;
            }
        }

        private static bool ExactlyEqual(Quaternion a, Quaternion b) =>
            a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;

        // ------------------------------------------------------------------ model sanitising (build time)

        /// <summary>
        /// Removes every joint, rigidbody and collider from a visual model instance (gameplay collision is the player's
        /// capsule; the ragdoll is rebuilt by <see cref="RagdollController"/>). Joints go first because a Rigidbody cannot be
        /// removed while a Joint depends on it. Uses DestroyImmediate so the components are gone before new ones are added.
        /// Only call on scene instances, never on prefab assets.
        /// </summary>
        public static void StripPhysics(GameObject root)
        {
            if (root == null) return;
            var joints = root.GetComponentsInChildren<Joint>(true);
            for (int i = 0; i < joints.Length; i++) Object.DestroyImmediate(joints[i]);
            var bodies = root.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++) Object.DestroyImmediate(bodies[i]);
            var colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++) Object.DestroyImmediate(colliders[i]);
        }

        /// <summary>
        /// Removes Dodgeball Ultra character components a user-authored model prefab might carry (so a visual copy never
        /// runs gameplay code of its own).
        /// </summary>
        public static void StripCharacterComponents(GameObject root)
        {
            if (root == null) return;
            DestroyAll<CharacterIKController>(root);
            DestroyAll<PlayerAnimatorDriver>(root);
            DestroyAll<RagdollController>(root);
            DestroyAll<ProceduralLean>(root);
            DestroyAll<CharacterVisual>(root);
            DestroyAll<HitFlash>(root);
        }

        private static void DestroyAll<T>(GameObject root) where T : Component
        {
            var items = root.GetComponentsInChildren<T>(true);
            for (int i = 0; i < items.Length; i++) Object.DestroyImmediate(items[i]);
        }

        /// <summary>
        /// A raw Microsoft Rocketbox FBX contains four skinned meshes (hipoly / midpoly / lowpoly / ultralowpoly). The Setup
        /// Wizard's prefabs already carry a LODGroup; when a raw export is used directly, this builds the LODGroup at runtime
        /// so only one level is ever drawn. Returns true if the model has (or now has) a LODGroup.
        /// </summary>
        public static bool EnsureLodGroup(GameObject modelRoot)
        {
            if (modelRoot == null) return false;
            if (modelRoot.GetComponentInChildren<LODGroup>(true) != null) return true;

            var skinned = modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (skinned.Length < 2) return false;

            var buckets = new List<Renderer>[4];
            var untagged = new List<Renderer>();
            int levels = 0;
            for (int i = 0; i < skinned.Length; i++)
            {
                int level = DetailLevelFromName(skinned[i].name);
                if (level < 0)
                {
                    untagged.Add(skinned[i]);
                    continue;
                }
                if (buckets[level] == null)
                {
                    buckets[level] = new List<Renderer>();
                    levels++;
                }
                buckets[level].Add(skinned[i]);
            }
            if (levels < 2) return false;

            // Screen-relative heights for a full-body character (hi-poly while it fills more than ~30 % of the screen).
            float[] heights = { 0.30f, 0.14f, 0.05f, 0.012f };
            var lods = new List<LOD>(levels);
            int lodIndex = 0;
            for (int level = 0; level < 4; level++)
            {
                if (buckets[level] == null) continue;
                var renderers = new List<Renderer>(buckets[level]);
                renderers.AddRange(untagged); // parts without a level tag stay visible at every distance
                lods.Add(new LOD(heights[Mathf.Min(lodIndex, heights.Length - 1)], renderers.ToArray()));
                lodIndex++;
            }

            var group = modelRoot.AddComponent<LODGroup>();
            group.SetLODs(lods.ToArray());
            group.RecalculateBounds();
            return true;
        }

        /// <summary>Rocketbox LOD level encoded in a mesh name (0 = hipoly ... 3 = ultralowpoly), -1 when none.</summary>
        public static int DetailLevelFromName(string meshName)
        {
            if (string.IsNullOrEmpty(meshName)) return -1;
            string n = meshName.ToLowerInvariant();
            if (n.Contains("ultralowpoly") || n.Contains("ultra_low")) return 3;
            if (n.Contains("lowpoly")) return 2;
            if (n.Contains("midpoly") || n.Contains("medpoly")) return 1;
            if (n.Contains("hipoly") || n.Contains("highpoly")) return 0;
            return -1;
        }

        /// <summary>
        /// Collects the renderers of the highest level of detail (LOD0 of the LODGroup, or every renderer when there is none).
        /// Allocates; call at build time and cache the result.
        /// </summary>
        public static void CollectHighestDetailRenderers(GameObject modelRoot, List<Renderer> results)
        {
            results.Clear();
            if (modelRoot == null) return;
            LODGroup group = modelRoot.GetComponentInChildren<LODGroup>(true);
            if (group != null)
            {
                LOD[] lods = group.GetLODs();
                if (lods.Length > 0 && lods[0].renderers != null)
                {
                    var lod0 = lods[0].renderers;
                    for (int i = 0; i < lod0.Length; i++)
                        if (lod0[i] != null && !results.Contains(lod0[i])) results.Add(lod0[i]);
                    if (results.Count > 0) return;
                }
            }

            var all = modelRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] is SkinnedMeshRenderer || all[i] is MeshRenderer) results.Add(all[i]);
        }

        // ------------------------------------------------------------------ materials

        /// <summary>
        /// Finds the main colour property of <paramref name="material"/> (_BaseColor, _UnlitColor, _Color or _TintColor) and
        /// returns its id and current value. Returns -1 when the material has none.
        /// </summary>
        public static int FindColorProperty(Material material, out Color color)
        {
            color = Color.white;
            if (material == null) return -1;
            for (int i = 0; i < s_colorProperties.Length; i++)
            {
                int id = s_colorProperties[i];
                if (!material.HasProperty(id)) continue;
                color = material.GetColor(id);
                return id;
            }
            return -1;
        }

        /// <summary>Destroy that also works in edit mode (EditMode tests, editor previews).</summary>
        public static void DestroySafe(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Object.Destroy(obj);
            else Object.DestroyImmediate(obj);
        }

        /// <summary>Returns the existing component or adds one.</summary>
        public static T GetOrAdd<T>(GameObject go) where T : Component
        {
            T existing = go.GetComponent<T>();
            return existing != null ? existing : go.AddComponent<T>();
        }

        /// <summary>Mean absolute lossy scale of a transform (1 when unscaled); converts world lengths to local units.</summary>
        public static float UniformScale(Transform t)
        {
            if (t == null) return 1f;
            Vector3 s = t.lossyScale;
            float m = (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
            return m > 1e-6f ? m : 1f;
        }

        // ------------------------------------------------------------------ throws

        /// <summary>Distance (m) from the thrower's hand beyond which an ability throw was not released by the body.</summary>
        public const float BodyThrowMaxHandDistance = 1f;

        /// <summary>
        /// True when a throw credited to <paramref name="thrower"/> was really released by the body (throw animation and
        /// IK whip should play). False for shots fired by a device on the thrower's behalf - Screws' Auto-Turret
        /// (<see cref="Combat.BallStyle.Turret"/>) or any ability throw whose origin is far from the hand.
        /// </summary>
        public static bool IsBodyThrow(DodgeballUltra.Player.DodgeballPlayer thrower, in DodgeballUltra.Events.BallThrownEvent e)
        {
            if (thrower == null) return false;
            if (e.Ball != null && e.Ball.Style == Combat.BallStyle.Turret) return false;
            if (!e.IsAbilityThrow) return true;

            CharacterVisual visual = thrower.Visual;
            Vector3 hand = visual != null && visual.RightHandSocket != null ? visual.RightHandSocket.position : thrower.ChestPosition;
            return (e.Origin - hand).sqrMagnitude <= BodyThrowMaxHandDistance * BodyThrowMaxHandDistance;
        }

        /// <summary>Planar (y = 0) normalised direction, or <paramref name="fallback"/> when degenerate.</summary>
        public static Vector3 Planar(Vector3 v, Vector3 fallback)
        {
            v.y = 0f;
            float m = v.magnitude;
            return m > 1e-4f ? v / m : fallback;
        }
    }
}
