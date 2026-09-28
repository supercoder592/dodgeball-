using System;
using DodgeballUltra.Player;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DodgeballUltra.Characters
{
    /// <summary>
    /// CONTRACT (kernel) - builds a ragdoll at runtime from the Humanoid avatar bones (hips, spine/chest, head, upper/lower
    /// arms and legs: colliders + rigidbodies + CharacterJoints with anatomical limits, total mass ~ body mass) and handles the
    /// impulse-driven transition on elimination: Animator off, bodies non-kinematic, current animated bone velocities are
    /// preserved, then the ball impulse is applied at the hit point. <see cref="Recover"/> blends back to animation.
    /// Ragdoll colliders use layer <see cref="GameLayers.Ragdoll"/>.
    /// <para>
    /// Build (once, on the bind pose right after the model was instantiated): 12 bodies - pelvis, spine, chest, head,
    /// upper arms, forearms (+hand), thighs, calves (+ a foot box so the soles rest on the floor). Colliders are sized from
    /// the measured bone lengths and the stature of the skeleton (boxes for the torso, a sphere for the head, capsules for
    /// the limbs), masses follow adult segment fractions (pelvis 15 %, chest 20 %, thigh 11 %...) scaled to the hero's body
    /// mass. Joints follow Unity's RagdollBuilder conventions with anatomical ranges: every hinge axis is
    /// <c>cross(segment, flexion direction)</c> so flexion is always the negative twist (knees fold backwards, elbows
    /// toward the palm, hips/spine/head forward), swings limit abduction and axial rotation.
    /// </para>
    /// <para>
    /// While animated every body is kinematic, its colliders are disabled and it does not detect collisions (no cost for
    /// ball sweeps or camera probes); the controller measures each segment's velocity from the animated pose every
    /// LateUpdate. <see cref="EnableRagdoll"/> detaches the pelvis from the model (so a player root that is still falling or
    /// being knocked back cannot drag the limp body), hands the measured velocities to the bodies (momentum preservation)
    /// and adds the hit impulse: a whole-body share plus a share at the struck segment for spin.
    /// <see cref="Recover"/> re-attaches the pelvis and slerps from the physical pose to the animated pose over the blend
    /// time (<see cref="IsBlending"/>); <see cref="ResetImmediate"/> snaps back.
    /// </para>
    /// <para>Owner module: Characters.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)] // after the Animator, IK twist (-10) and lean (0): measures / blends the final pose
    public sealed class RagdollController : MonoBehaviour
    {
        // ------------------------------------------------------------------ tuning

        [Header("Mass")]
        [Tooltip("Total ragdoll mass (kg) when the hero's body mass is unknown.")]
        [Min(20f)] public float referenceMass = 75f;

        [Tooltip("Use the hero's rigidbody mass (CharacterData.movement.mass) as the total ragdoll mass.")]
        public bool useCharacterMass = true;

        [Header("Physics")]
        [Tooltip("Linear damping of every body (air drag of a limp body).")]
        [Range(0f, 2f)] public float linearDamping = 0.08f;

        [Tooltip("Angular damping (approximates joint friction of relaxed muscles).")]
        [Range(0f, 5f)] public float angularDamping = 0.35f;

        [Tooltip("Maximum angular speed of a body (rad/s).")]
        [Min(1f)] public float maxAngularSpeed = 22f;

        [Tooltip("Maximum linear speed a body may be given by the transition (m/s).")]
        [Min(1f)] public float maxLinearSpeed = 18f;

        [Tooltip("Maximum depenetration speed (m/s): limbs starting inside the floor are pushed out gently, not launched.")]
        [Min(0.1f)] public float maxDepenetrationSpeed = 3f;

        [Tooltip("Solver position iterations of each body (joint stiffness).")]
        [Range(4, 30)] public int solverIterations = 12;

        [Tooltip("Solver velocity iterations of each body.")]
        [Range(1, 20)] public int solverVelocityIterations = 4;

        [Tooltip("Friction of the ragdoll colliders against the court.")]
        [Range(0f, 1.5f)] public float friction = 0.6f;

        [Tooltip("Bounciness of the ragdoll colliders.")]
        [Range(0f, 1f)] public float bounciness = 0.05f;

        [Header("Momentum from the animation")]
        [Tooltip("Clamp (m/s) on segment velocities inherited from the animated pose.")]
        [Min(0f)] public float maxInheritedSpeed = 12f;

        [Tooltip("Clamp (rad/s) on segment angular velocities inherited from the animated pose.")]
        [Min(0f)] public float maxInheritedAngularSpeed = 15f;

        [Tooltip("A segment moving further than this in one frame was teleported: its velocity is discarded (m).")]
        [Min(0.1f)] public float teleportDistance = 1.5f;

        [Header("Hit impulse (N*s -> plausible change of velocity)")]
        [Tooltip("Whole-body delta-v = |impulse| / mass * gain (game impulses are exaggerated for readability).")]
        [Min(0f)] public float impulseGain = 3.5f;

        [Tooltip("Minimum whole-body delta-v (m/s): even a soft elimination knocks the body over.")]
        [Min(0f)] public float minDeltaV = 1.2f;

        [Tooltip("Maximum whole-body delta-v (m/s).")]
        [Min(0f)] public float maxDeltaV = 6.5f;

        [Tooltip("Share of the impulse delivered to the struck segment only (the rest moves the whole body).")]
        [Range(0f, 1f)] public float strikeShare = 0.45f;

        [Tooltip("Upward share of the delta-v: bodies lift off a little instead of skidding along the floor.")]
        [Range(0f, 1f)] public float upwardShare = 0.28f;

        [Tooltip("Maximum delta-v (m/s) of the struck segment itself (keeps light limbs from flailing).")]
        [Min(0f)] public float maxStrikeDeltaV = 9f;

        [Tooltip("Lever arm limit (m) used for the spin of the struck segment.")]
        [Min(0f)] public float maxLeverArm = 0.25f;

        [Header("Recovery")]
        [Tooltip("If the pelvis ended further than this from where the animation puts it (m), its position snaps instead of blending.")]
        [Min(0.1f)] public float positionSnapDistance = 1.5f;

        // ------------------------------------------------------------------ contract state

        public bool IsBuilt { get; private set; }
        public bool IsRagdolled { get; private set; }
        public Transform Hips { get; private set; }

        // ------------------------------------------------------------------ additional state

        /// <summary>True while the pose blends from the ragdoll back to the animation (IK stays off until it ends).</summary>
        public bool IsBlending { get; private set; }

        /// <summary>0..1 progress of the recovery blend (1 when not blending).</summary>
        public float BlendProgress => IsBlending && _blendDuration > 0f ? Mathf.Clamp01(_blendElapsed / _blendDuration) : 1f;

        /// <summary>Total mass (kg) of all bodies.</summary>
        public float TotalMass { get; private set; }

        /// <summary>Number of rigid bodies.</summary>
        public int BodyCount => _count;

        public DodgeballPlayer Owner { get; private set; }

        /// <summary>Raised after the body went limp.</summary>
        public event Action<RagdollController> Ragdolled;

        /// <summary>Raised when the body is back under animation control (start of the blend or immediate reset).</summary>
        public event Action<RagdollController> Recovered;

        // ------------------------------------------------------------------ internals

        private enum Segment { Pelvis, Spine, Chest, Head, UpperArm, Forearm, Thigh, Calf }

        private sealed class Part
        {
            public Segment Segment;
            public Transform Bone;
            public Rigidbody Body;
            public Collider Collider;
            public CharacterJoint Joint;
            public float Mass;
            public Vector3 PrevPosition;
            public Quaternion PrevRotation;
            public Vector3 Velocity;
            public Vector3 AngularVelocity;
            public Quaternion BlendFromRotation;
            public Vector3 BlendFromPosition;
        }

        // Adult segment masses (kg) for a 75 kg reference body (hand in the forearm, foot in the calf).
        private const float MassPelvis = 11f, MassSpine = 10f, MassChest = 15f, MassHead = 5.5f;
        private const float MassUpperArm = 2.2f, MassForearm = 1.6f, MassThigh = 8.5f, MassCalf = 4.5f;
        private const float ReferenceSegmentTotal = MassPelvis + MassSpine + MassChest + MassHead +
                                                    2f * (MassUpperArm + MassForearm + MassThigh + MassCalf);

        private static Object s_physicsMaterial;

        private readonly Part[] _parts = new Part[12];
        private int _count;
        private readonly Collider[] _extraColliders = new Collider[4]; // feet (compound with the calves)
        private int _extraCount;
        private Animator _animator;
        private SkinnedMeshRenderer[] _skinned = Array.Empty<SkinnedMeshRenderer>();
        private bool[] _skinnedOffscreen = Array.Empty<bool>();
        private Transform _hipsParent;
        private int _hipsSiblingIndex;
        private bool _detached;
        private bool _trackValid;
        private float _blendElapsed;
        private float _blendDuration;
        private int _chestIndex = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_physicsMaterial = null;

        // ------------------------------------------------------------------ contract

        /// <summary>
        /// Builds the ragdoll on <paramref name="animator"/>'s Humanoid skeleton (bind pose). No-op (IsBuilt = false) for
        /// models without a valid Humanoid avatar or with missing limb bones.
        /// </summary>
        public void Initialize(DodgeballPlayer owner, Animator animator)
        {
            Owner = owner;
            if (IsBuilt && animator == _animator) return; // already built on this skeleton
            _animator = animator;
            IsBuilt = false;
            IsRagdolled = false;
            IsBlending = false;
            _count = 0;
            _extraCount = 0;
            _chestIndex = -1;
            if (!HumanoidUtil.IsValidHumanoid(animator)) return;

            try
            {
                IsBuilt = Build(animator);
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
                IsBuilt = false;
            }

            if (IsBuilt)
            {
                _skinned = animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                _skinnedOffscreen = new bool[_skinned.Length];
                SetAnimatedPhysics();
                _trackValid = false;
            }
        }

        /// <summary>Goes limp and applies <paramref name="impulse"/> (N*s) at <paramref name="worldPoint"/>.</summary>
        public void EnableRagdoll(Vector3 impulse, Vector3 worldPoint)
        {
            if (!IsBuilt) return;
            if (IsRagdolled)
            {
                ApplyImpulse(impulse, worldPoint);
                return;
            }

            IsBlending = false;
            IsRagdolled = true;
            if (_animator != null) _animator.enabled = false; // the last evaluated pose stays on the bones

            Detach();
            SetSkinnedOffscreenUpdate(true);

            for (int i = 0; i < _count; i++)
            {
                Part p = _parts[i];
                p.Body.isKinematic = false;
                p.Body.detectCollisions = true;
                p.Body.interpolation = RigidbodyInterpolation.Interpolate;
                if (p.Collider != null) p.Collider.enabled = true;
                p.Body.SetVelocity(_trackValid ? p.Velocity : Vector3.zero);
                p.Body.SetAngularVelocity(_trackValid ? p.AngularVelocity : Vector3.zero);
                p.Body.WakeUp();
            }
            for (int i = 0; i < _extraCount; i++)
                if (_extraColliders[i] != null) _extraColliders[i].enabled = true;

            ApplyImpulse(impulse, worldPoint);
            Ragdolled?.Invoke(this);
        }

        /// <summary>Returns to animation (smoothly blending the pose over <paramref name="blendTime"/> seconds).</summary>
        public void Recover(float blendTime = 0.35f)
        {
            if (!IsBuilt || !IsRagdolled) return;

            SetAnimatedPhysics();
            Reattach();

            // The physical pose, expressed in the restored hierarchy, is where the blend starts.
            for (int i = 0; i < _count; i++)
            {
                Part p = _parts[i];
                p.BlendFromRotation = p.Bone.localRotation;
                p.BlendFromPosition = p.Bone.localPosition;
            }

            IsRagdolled = false;
            SetSkinnedOffscreenUpdate(false);
            if (_animator != null) _animator.enabled = true; // evaluates this frame, before our LateUpdate blend

            _blendDuration = Mathf.Max(0f, blendTime);
            _blendElapsed = 0f;
            IsBlending = _blendDuration > 1e-3f && _animator != null;
            _trackValid = false;
            Recovered?.Invoke(this);
        }

        /// <summary>Instantly returns to animation (teleports / round reset).</summary>
        public void ResetImmediate()
        {
            if (!IsBuilt) return;
            bool wasRagdolled = IsRagdolled;
            IsBlending = false;
            _trackValid = false;
            if (!wasRagdolled) return;

            SetAnimatedPhysics();
            Reattach();
            IsRagdolled = false;
            SetSkinnedOffscreenUpdate(false);

            if (_animator != null)
            {
                _animator.enabled = true;
                if (_animator.isActiveAndEnabled && _animator.runtimeAnimatorController != null)
                {
                    // Re-evaluate right now so no ragdoll pose is rendered for a frame.
                    _animator.Rebind();
                    _animator.Update(0f);
                }
            }
            Recovered?.Invoke(this);
        }

        // ------------------------------------------------------------------ additions

        /// <summary>
        /// Adds a hit impulse to the limp body (subsequent hits while down, shockwaves). Maps the impulse to a plausible
        /// whole-body delta-v (<see cref="impulseGain"/>, clamped) plus extra velocity and spin on the struck segment.
        /// </summary>
        public void ApplyImpulse(Vector3 impulse, Vector3 worldPoint)
        {
            if (!IsRagdolled || _count == 0) return;

            float magnitude = impulse.magnitude;
            float dv = magnitude > 1e-4f
                ? Mathf.Clamp(magnitude / Mathf.Max(1f, TotalMass) * impulseGain, minDeltaV, maxDeltaV)
                : minDeltaV;

            // Direction: along the impulse, else backwards from where the body faces (a collapse).
            Vector3 dir = magnitude > 1e-4f ? impulse / magnitude : -FacingForward();
            dir.y = Mathf.Max(dir.y, 0f);
            if (dir.sqrMagnitude < 1e-6f) dir = -FacingForward();
            dir.Normalize();

            // Whole-body share keeps the segments together; a little lift so the body leaves the floor.
            Vector3 bodyDelta = dir * (dv * (1f - strikeShare)) + Vector3.up * (dv * upwardShare);
            for (int i = 0; i < _count; i++)
            {
                Rigidbody rb = _parts[i].Body;
                rb.SetVelocity(Vector3.ClampMagnitude(rb.GetVelocity() + bodyDelta, maxLinearSpeed));
            }

            // Struck segment: extra velocity at the hit point (spin).
            bool hasPoint = worldPoint != Vector3.zero && Hips != null &&
                            (worldPoint - Hips.position).sqrMagnitude < 9f; // (0,0,0) = "no point" from generic eliminations
            Part struck = hasPoint ? NearestPart(worldPoint) : (_chestIndex >= 0 ? _parts[_chestIndex] : _parts[0]);
            if (struck == null || strikeShare <= 0f) return;

            float strikeImpulse = TotalMass * dv * strikeShare;
            float strikeDv = Mathf.Min(strikeImpulse / Mathf.Max(0.1f, struck.Mass), maxStrikeDeltaV);
            Rigidbody body = struck.Body;
            body.SetVelocity(Vector3.ClampMagnitude(body.GetVelocity() + dir * strikeDv, maxLinearSpeed));
            if (hasPoint)
            {
                Vector3 lever = Vector3.ClampMagnitude(worldPoint - body.worldCenterOfMass, maxLeverArm);
                body.AddTorque(Vector3.Cross(lever, dir * (strikeDv * struck.Mass)), ForceMode.Impulse);
            }
        }

        // ------------------------------------------------------------------ per frame

        private void LateUpdate()
        {
            if (!IsBuilt || IsRagdolled) return;
            if (IsBlending) UpdateBlend(Time.deltaTime);
            else TrackVelocities(Time.deltaTime);
        }

        /// <summary>Finite differences of the animated segment poses (momentum handed to the ragdoll).</summary>
        private void TrackVelocities(float dt)
        {
            if (dt <= 1e-5f) return; // hitstop / pause: keep the last measurement

            float teleportSqr = teleportDistance * teleportDistance;
            float inv = 1f / dt;
            for (int i = 0; i < _count; i++)
            {
                Part p = _parts[i];
                Vector3 position = p.Bone.position;
                Quaternion rotation = p.Bone.rotation;
                if (_trackValid)
                {
                    Vector3 delta = position - p.PrevPosition;
                    if (delta.sqrMagnitude > teleportSqr)
                    {
                        p.Velocity = Vector3.zero;
                        p.AngularVelocity = Vector3.zero;
                    }
                    else
                    {
                        Vector3 v = Vector3.ClampMagnitude(delta * inv, maxInheritedSpeed);
                        Quaternion dq = rotation * Quaternion.Inverse(p.PrevRotation);
                        dq.ToAngleAxis(out float angle, out Vector3 axis);
                        if (angle > 180f) angle -= 360f;
                        Vector3 w = Mathf.Abs(angle) > 0.01f && IsFinite(axis)
                            ? Vector3.ClampMagnitude(axis * (angle * Mathf.Deg2Rad * inv), maxInheritedAngularSpeed)
                            : Vector3.zero;
                        // Light smoothing: mocap differences are noisy at high frame rates.
                        p.Velocity = Vector3.Lerp(p.Velocity, v, 0.6f);
                        p.AngularVelocity = Vector3.Lerp(p.AngularVelocity, w, 0.6f);
                    }
                }
                p.PrevPosition = position;
                p.PrevRotation = rotation;
            }
            _trackValid = true;
        }

        /// <summary>Slerps from the captured physical pose to the pose the Animator just wrote.</summary>
        private void UpdateBlend(float dt)
        {
            _blendElapsed += Mathf.Max(0f, dt);
            float t = _blendDuration > 0f ? Mathf.Clamp01(_blendElapsed / _blendDuration) : 1f;
            float w = t * t * (3f - 2f * t);
            float snapSqr = positionSnapDistance * positionSnapDistance;

            for (int i = 0; i < _count; i++)
            {
                Part p = _parts[i];
                p.Bone.localRotation = Quaternion.Slerp(p.BlendFromRotation, p.Bone.localRotation, w);
                if (i == 0)
                {
                    // Pelvis: blend the position too unless the player was moved while down (then it snaps).
                    Vector3 animated = p.Bone.localPosition;
                    if ((animated - p.BlendFromPosition).sqrMagnitude < snapSqr)
                        p.Bone.localPosition = Vector3.Lerp(p.BlendFromPosition, animated, w);
                }
            }

            if (t >= 1f)
            {
                IsBlending = false;
                _trackValid = false;
            }
        }

        private void OnDestroy()
        {
            // The detached pelvis lives at the scene root while ragdolled: take it with us.
            if (_detached && Hips != null) HumanoidUtil.DestroySafe(Hips.gameObject);
            _detached = false;
        }

        // ------------------------------------------------------------------ state helpers

        /// <summary>Animated mode: kinematic, no collisions, colliders off (zero cost for sweeps and probes).</summary>
        private void SetAnimatedPhysics()
        {
            for (int i = 0; i < _count; i++)
            {
                Part p = _parts[i];
                if (p.Body == null) continue;
                if (!p.Body.isKinematic)
                {
                    p.Body.SetVelocity(Vector3.zero);
                    p.Body.SetAngularVelocity(Vector3.zero);
                    p.Body.isKinematic = true;
                }
                p.Body.detectCollisions = false;
                p.Body.interpolation = RigidbodyInterpolation.None;
                if (p.Collider != null) p.Collider.enabled = false;
            }
            for (int i = 0; i < _extraCount; i++)
                if (_extraColliders[i] != null) _extraColliders[i].enabled = false;
        }

        private void Detach()
        {
            if (_detached || Hips == null) return;
            _hipsParent = Hips.parent;
            _hipsSiblingIndex = Hips.GetSiblingIndex();
            Hips.SetParent(null, true);
            _detached = true;
        }

        private void Reattach()
        {
            if (!_detached || Hips == null) return;
            _detached = false;
            if (_hipsParent == null) return; // the model is being destroyed
            Hips.SetParent(_hipsParent, true);
            if (_hipsSiblingIndex >= 0 && _hipsSiblingIndex < _hipsParent.childCount) Hips.SetSiblingIndex(_hipsSiblingIndex);
        }

        /// <summary>
        /// Skinned bounds follow the root bone, which may stay behind while the detached pelvis tumbles away: compute them
        /// from the bones while ragdolled so the body is never culled while on screen.
        /// </summary>
        private void SetSkinnedOffscreenUpdate(bool ragdolled)
        {
            for (int i = 0; i < _skinned.Length; i++)
            {
                SkinnedMeshRenderer smr = _skinned[i];
                if (smr == null) continue;
                if (ragdolled)
                {
                    _skinnedOffscreen[i] = smr.updateWhenOffscreen;
                    smr.updateWhenOffscreen = true;
                }
                else
                {
                    smr.updateWhenOffscreen = _skinnedOffscreen[i];
                }
            }
        }

        private Part NearestPart(Vector3 point)
        {
            Part best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < _count; i++)
            {
                Part p = _parts[i];
                float d = (p.Body.worldCenterOfMass - point).sqrMagnitude;
                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = p;
                }
            }
            return best;
        }

        private Vector3 FacingForward()
        {
            Vector3 f = Owner != null ? Owner.Forward : transform.forward;
            f.y = 0f;
            return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
        }

        private static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
            !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        // ------------------------------------------------------------------ build

        private bool Build(Animator animator)
        {
            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            Transform spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            Transform chest = animator.GetBoneTransform(HumanBodyBones.Chest);
            Transform upperChest = animator.GetBoneTransform(HumanBodyBones.UpperChest);
            Transform neck = animator.GetBoneTransform(HumanBodyBones.Neck);
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            Transform lUpperArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            Transform rUpperArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            Transform lLowerArm = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            Transform rLowerArm = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            Transform lHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            Transform rHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            Transform lUpperLeg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            Transform rUpperLeg = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            Transform lLowerLeg = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            Transform rLowerLeg = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            Transform lFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform rFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            Transform lToes = animator.GetBoneTransform(HumanBodyBones.LeftToes);
            Transform rToes = animator.GetBoneTransform(HumanBodyBones.RightToes);

            if (hips == null || head == null || lUpperArm == null || rUpperArm == null || lLowerArm == null ||
                rLowerArm == null || lHand == null || rHand == null || lUpperLeg == null || rUpperLeg == null ||
                lLowerLeg == null || rLowerLeg == null || lFoot == null || rFoot == null)
            {
                Debug.LogWarning($"[Dodgeball Ultra] '{animator.name}': the Humanoid avatar lacks limb bones; no ragdoll is built " +
                                 "(eliminations fall back to the defeat animation).", this);
                return false;
            }

            // Torso chain: pelvis -> (spine) -> chest. The chest body is the highest torso bone below the neck.
            Transform chestBone = chest != null ? chest : upperChest;
            Transform spineBone = spine;
            if (chestBone == null)
            {
                chestBone = spine;
                spineBone = null;
            }
            if (chestBone == null) chestBone = hips; // degenerate rig: the pelvis carries the arms and head
            if (spineBone == chestBone) spineBone = null;

            HumanoidUtil.TryGetSkeletonAxes(animator, out Vector3 right, out Vector3 up, out Vector3 fwd);
            float s = HumanoidUtil.EstimateStatureScale(animator, up);
            Hips = hips;

            float bodyMass = referenceMass;
            if (useCharacterMass && Owner != null && Owner.Character != null && Owner.Character.movement != null)
                bodyMass = Owner.Character.movement.mass;
            bodyMass = Mathf.Clamp(bodyMass, 40f, 160f);
            float massScale = bodyMass / ReferenceSegmentTotal;

            if (s_physicsMaterial == null)
                s_physicsMaterial = PhysicsCompat.CreatePhysicsMaterial("DU_Ragdoll", bounciness, friction * 0.9f, friction, false);

            Vector3 neckPoint = neck != null ? neck.position : head.position - up * (0.06f * s);
            Vector3 hipJoints = (lUpperLeg.position + rUpperLeg.position) * 0.5f;
            float hipWidth = Vector3.Distance(lUpperLeg.position, rUpperLeg.position);
            float shoulderWidth = Vector3.Distance(lUpperArm.position, rUpperArm.position);

            // ---- pelvis (root)
            {
                Vector3 top = spineBone != null ? spineBone.position : (chestBone != hips ? chestBone.position : hips.position + up * (0.12f * s));
                float topH = Vector3.Dot(top - hipJoints, up);
                float bottomH = -0.09f * s;
                topH = Mathf.Max(topH, 0.08f * s);
                Vector3 centre = hipJoints + up * ((topH + bottomH) * 0.5f);
                var size = new Vector3(hipWidth + 0.16f * s, topH - bottomH, 0.22f * s);
                float pelvisMass = MassPelvis + (spineBone == null && chestBone == hips ? MassSpine + MassChest : 0f);
                AddPart(Segment.Pelvis, hips, AddBox(hips, centre, right, up, fwd, size), pelvisMass * massScale);
            }

            int pelvisIndex = 0;
            int torsoTopIndex = pelvisIndex;

            // ---- spine
            if (spineBone != null)
            {
                Vector3 a = spineBone.position, b = chestBone != hips ? chestBone.position : neckPoint;
                float h = Mathf.Max(0.06f * s, Vector3.Dot(b - a, up));
                Vector3 centre = a + up * (h * 0.5f);
                var size = new Vector3(0.3f * s, h, 0.2f * s);
                float m = MassSpine + (chestBone == hips ? MassChest : 0f);
                int index = AddPart(Segment.Spine, spineBone, AddBox(spineBone, centre, right, up, fwd, size), m * massScale);
                AddJoint(index, pelvisIndex, right, fwd, -30f, 20f, 20f, 20f);
                torsoTopIndex = index;
            }

            // ---- chest
            if (chestBone != hips)
            {
                Vector3 a = chestBone.position;
                float h = Mathf.Max(0.1f * s, Vector3.Dot(neckPoint - a, up));
                Vector3 centre = a + up * (h * 0.5f) + fwd * (0.01f * s);
                var size = new Vector3(Mathf.Max(0.26f * s, shoulderWidth * 0.85f), h, 0.22f * s);
                float m = MassChest + (spineBone == null ? MassSpine : 0f);
                int index = AddPart(Segment.Chest, chestBone, AddBox(chestBone, centre, right, up, fwd, size), m * massScale);
                AddJoint(index, torsoTopIndex, right, fwd, -25f, 15f, 15f, 15f);
                torsoTopIndex = index;
                _chestIndex = index;
            }
            else
            {
                _chestIndex = pelvisIndex;
            }

            // ---- head (sphere around the skull; the Head bone sits at its base)
            {
                float radius = 0.105f * s;
                Vector3 centre = head.position + up * (0.085f * s) + fwd * (0.02f * s);
                var sphere = head.gameObject.AddComponent<SphereCollider>();
                float scale = HumanoidUtil.UniformScale(head);
                sphere.radius = radius / scale;
                sphere.center = head.InverseTransformPoint(centre);
                int index = AddPart(Segment.Head, head, sphere, MassHead * massScale);
                AddJoint(index, torsoTopIndex, right, fwd, -45f, 35f, 30f, 60f);
            }

            // ---- arms
            BuildArm(lUpperArm, lLowerArm, lHand, false, torsoTopIndex, fwd, animator, s, massScale);
            BuildArm(rUpperArm, rLowerArm, rHand, true, torsoTopIndex, fwd, animator, s, massScale);

            // ---- legs
            BuildLeg(lUpperLeg, lLowerLeg, lFoot, lToes, pelvisIndex, right, up, fwd, s, massScale);
            BuildLeg(rUpperLeg, rLowerLeg, rFoot, rToes, pelvisIndex, right, up, fwd, s, massScale);

            TotalMass = 0f;
            for (int i = 0; i < _count; i++) TotalMass += _parts[i].Mass;
            return _count >= 10;
        }

        private void BuildArm(Transform upper, Transform lower, Transform hand, bool rightSide, int parentIndex, Vector3 fwd,
            Animator animator, float s, float massScale)
        {
            Vector3 upperDir = (lower.position - upper.position).normalized;
            Vector3 foreDir = (hand.position - lower.position).normalized;

            // Upper arm: horizontal flexion toward the front is the hinge (axis = segment x forward), abduction is swing.
            int upperIndex = AddPart(Segment.UpperArm, upper,
                AddCapsule(upper, upper.position, lower.position, 0.055f * s), MassUpperArm * massScale);
            AddJoint(upperIndex, parentIndex, Vector3.Cross(upperDir, fwd), fwd, -100f, 30f, 95f, 45f);

            // Forearm (+hand): the elbow folds toward the palm side of the forearm.
            PalmFrame palm = HumanoidUtil.ComputePalmFrame(animator, rightSide);
            Vector3 palmNormal = palm.Valid ? palm.NormalWorld(hand) : Vector3.down;
            Vector3 flex = Vector3.ProjectOnPlane(palmNormal, foreDir);
            if (flex.sqrMagnitude < 1e-6f) flex = Vector3.ProjectOnPlane(fwd, foreDir);
            flex.Normalize();
            Vector3 end = hand.position + foreDir * (0.09f * s); // cover the hand
            int foreIndex = AddPart(Segment.Forearm, lower, AddCapsule(lower, lower.position, end, 0.045f * s),
                MassForearm * massScale);
            AddJoint(foreIndex, upperIndex, Vector3.Cross(foreDir, flex), flex, -140f, 2f, 8f, 40f);
        }

        private void BuildLeg(Transform upper, Transform lower, Transform foot, Transform toes, int pelvisIndex, Vector3 right,
            Vector3 up, Vector3 fwd, float s, float massScale)
        {
            Vector3 thighDir = (lower.position - upper.position).normalized;
            Vector3 calfDir = (foot.position - lower.position).normalized;

            // Thigh: flexes forward (hip), abduction as swing.
            int thighIndex = AddPart(Segment.Thigh, upper, AddCapsule(upper, upper.position, lower.position, 0.075f * s),
                MassThigh * massScale);
            AddJoint(thighIndex, pelvisIndex, Vector3.Cross(thighDir, fwd), fwd, -100f, 25f, 35f, 25f);

            // Calf: the knee folds backwards only.
            int calfIndex = AddPart(Segment.Calf, lower, AddCapsule(lower, lower.position, foot.position, 0.055f * s),
                MassCalf * massScale);
            AddJoint(calfIndex, thighIndex, Vector3.Cross(calfDir, -fwd), fwd, -135f, 2f, 4f, 8f);

            // Foot: a box from heel to toe tip on the foot bone (no body of its own: compound with the calf).
            if (_extraCount < _extraColliders.Length)
            {
                Vector3 ankle = foot.position;
                Vector3 forward = Vector3.ProjectOnPlane(toes != null ? toes.position - ankle : fwd, up);
                if (forward.sqrMagnitude < 1e-6f) forward = fwd;
                forward.Normalize();
                Vector3 side = Vector3.Cross(up, forward).normalized;
                float toeReach = toes != null ? Vector3.Dot(toes.position - ankle, forward) + 0.05f * s : 0.17f * s;
                float heel = 0.05f * s;
                float soleDepth = 0.075f * s;
                float length = Mathf.Max(0.12f * s, toeReach + heel);
                Vector3 centre = ankle + forward * ((toeReach - heel) * 0.5f) - up * (soleDepth * 0.5f - 0.01f * s);
                var size = new Vector3(0.095f * s, soleDepth + 0.02f * s, length);
                BoxCollider box = AddBox(foot, centre, side, up, forward, size);
                foot.gameObject.layer = GameLayers.Ragdoll;
                _extraColliders[_extraCount++] = box;
            }
        }

        private int AddPart(Segment segment, Transform bone, Collider collider, float mass)
        {
            GameObject go = bone.gameObject;
            go.layer = GameLayers.Ragdoll;
            if (collider != null) collider.SetPhysicsMaterial(s_physicsMaterial);

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = true;
            rb.mass = Mathf.Max(0.2f, mass);
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.solverIterations = solverIterations;
            rb.solverVelocityIterations = solverVelocityIterations;
            rb.maxAngularVelocity = maxAngularSpeed;
            rb.maxDepenetrationVelocity = maxDepenetrationSpeed;
            rb.SetLinearDamping(linearDamping);
            rb.SetAngularDamping(angularDamping);
            rb.detectCollisions = false;

            var part = new Part
            {
                Segment = segment,
                Bone = bone,
                Body = rb,
                Collider = collider,
                Mass = rb.mass,
                PrevPosition = bone.position,
                PrevRotation = bone.rotation,
            };
            _parts[_count] = part;
            return _count++;
        }

        /// <summary>
        /// CharacterJoint from part <paramref name="childIndex"/> to <paramref name="parentIndex"/>. The hinge
        /// <paramref name="axisWorld"/> is chosen as cross(segment, flexion direction), so flexion is a negative twist:
        /// <paramref name="flexionLimit"/> (negative) .. <paramref name="extensionLimit"/>. Swing 1 turns about
        /// <paramref name="swingAxisWorld"/>, swing 2 about axis x swingAxis (axial rotation for limbs).
        /// </summary>
        private void AddJoint(int childIndex, int parentIndex, Vector3 axisWorld, Vector3 swingAxisWorld, float flexionLimit,
            float extensionLimit, float swing1, float swing2)
        {
            Part child = _parts[childIndex];
            Part parent = _parts[parentIndex];
            if (child == null || parent == null || child == parent) return;

            if (axisWorld.sqrMagnitude < 1e-8f) axisWorld = Vector3.right;
            axisWorld.Normalize();
            Vector3 swing = Vector3.ProjectOnPlane(swingAxisWorld, axisWorld);
            if (swing.sqrMagnitude < 1e-8f) swing = Vector3.ProjectOnPlane(Vector3.up, axisWorld);
            if (swing.sqrMagnitude < 1e-8f) swing = Vector3.ProjectOnPlane(Vector3.forward, axisWorld);
            swing.Normalize();

            var joint = child.Bone.gameObject.AddComponent<CharacterJoint>();
            joint.connectedBody = parent.Body;
            joint.axis = child.Bone.InverseTransformDirection(axisWorld);
            joint.swingAxis = child.Bone.InverseTransformDirection(swing);
            joint.anchor = Vector3.zero;
            joint.autoConfigureConnectedAnchor = false;
            joint.connectedAnchor = parent.Bone.InverseTransformPoint(child.Bone.position);
            joint.enableCollision = false;
            joint.enablePreprocessing = false; // robust when limbs start slightly outside their limits
            joint.enableProjection = true;     // no stretched limbs after violent impulses
            joint.projectionDistance = 0.05f;
            joint.projectionAngle = 30f;

            joint.lowTwistLimit = Limit(Mathf.Clamp(flexionLimit, -177f, 0f));
            joint.highTwistLimit = Limit(Mathf.Clamp(extensionLimit, 0f, 177f));
            joint.swing1Limit = Limit(Mathf.Clamp(swing1, 0f, 177f));
            joint.swing2Limit = Limit(Mathf.Clamp(swing2, 0f, 177f));
            child.Joint = joint;
        }

        private static SoftJointLimit Limit(float degrees) => new SoftJointLimit { limit = degrees, bounciness = 0f, contactDistance = 0f };

        /// <summary>
        /// Box collider on <paramref name="bone"/> enclosing a world-space box (centre, character axes, size). The bone's
        /// local axes need not match the character axes: the local AABB of the rotated box is used.
        /// </summary>
        private static BoxCollider AddBox(Transform bone, Vector3 centreWorld, Vector3 right, Vector3 up, Vector3 fwd, Vector3 sizeWorld)
        {
            Vector3 hx = right * (sizeWorld.x * 0.5f), hy = up * (sizeWorld.y * 0.5f), hz = fwd * (sizeWorld.z * 0.5f);
            var bounds = new Bounds(bone.InverseTransformPoint(centreWorld), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = centreWorld + ((i & 1) != 0 ? hx : -hx) + ((i & 2) != 0 ? hy : -hy) + ((i & 4) != 0 ? hz : -hz);
                bounds.Encapsulate(bone.InverseTransformPoint(corner));
            }
            var box = bone.gameObject.AddComponent<BoxCollider>();
            box.center = bounds.center;
            box.size = bounds.size;
            return box;
        }

        /// <summary>Capsule on <paramref name="bone"/> from <paramref name="startWorld"/> to <paramref name="endWorld"/>.</summary>
        private static CapsuleCollider AddCapsule(Transform bone, Vector3 startWorld, Vector3 endWorld, float radiusWorld)
        {
            Vector3 a = bone.InverseTransformPoint(startWorld);
            Vector3 b = bone.InverseTransformPoint(endWorld);
            Vector3 d = b - a;
            int axis = 0;
            float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y), az = Mathf.Abs(d.z);
            if (ay > ax && ay >= az) axis = 1;
            else if (az > ax && az > ay) axis = 2;

            float scale = HumanoidUtil.UniformScale(bone);
            float radius = radiusWorld / scale;
            var capsule = bone.gameObject.AddComponent<CapsuleCollider>();
            capsule.direction = axis;
            capsule.radius = radius;
            capsule.height = Mathf.Max(radius * 2f, Mathf.Abs(d[axis]) + radius * 1.6f);
            capsule.center = (a + b) * 0.5f;
            return capsule;
        }
    }
}
