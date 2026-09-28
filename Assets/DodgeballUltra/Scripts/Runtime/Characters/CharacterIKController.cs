using System.Collections.Generic;
using DodgeballUltra.Combat;
using DodgeballUltra.Events;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Characters
{
    /// <summary>
    /// CONTRACT (kernel) - Humanoid IK (Mecanim OnAnimatorIK; requires IK Pass on the base layer):
    /// <list type="bullet">
    /// <item>LookAt: head/eyes (and a little spine) track the most threatening incoming ball, otherwise the aim target.</item>
    /// <item>Catching: both hands reach toward the predicted intercept point of the incoming ball.</item>
    /// <item>Throwing: procedural right-arm wind-up behind the head while charging and a whip-through on release
    ///       (used when no authored throw clip exists), plus chest twist toward the aim.</item>
    /// <item>Holding: right hand keeps the ball near the hip/chest while running.</item>
    /// </list>
    /// All weights blend smoothly. Lives on the model's Animator GameObject.
    /// <para>
    /// Implementation notes:
    /// <list type="bullet">
    /// <item>Goals are placed in a body frame built from <c>Animator.bodyPosition</c> (valid for the current frame inside the
    ///       IK pass) plus shoulder offsets measured from the evaluated skeleton, so targets never lag one frame behind a
    ///       sprinting body (bone transforms read inside OnAnimatorIK still hold last frame's pose).</item>
    /// <item>Hand orientations are authored as "palm normal + finger direction" and converted to Mecanim's normalised IK
    ///       goal space with a per-avatar offset calibrated at runtime (the constant relation between the hand bone and its
    ///       IK goal), so palms really face the ball on any Humanoid rig.</item>
    /// <item>The chest twist is applied to Spine/Chest/UpperChest right after the Animator wrote the pose (LateUpdate).
    ///       <c>Animator.SetBoneLocalRotation</c> replaces the animated local rotation and bones are one frame stale during
    ///       the IK pass, so an additive twist through it would either freeze the mocap spine or compound every frame; the
    ///       post-animation twist is exact and self-correcting (see <see cref="HumanoidUtil.ApplySpineTwist"/>).
    ///       Hand goals are authored in the pre-twist torso frame, so the twist carries the wind-up further back and the
    ///       follow-through across the body exactly like a real throwing motion.</item>
    /// </list>
    /// </para>
    /// <para>Owner module: Characters.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-10)] // LateUpdate twist before ProceduralLean (0), clone pose mirroring (0) and RagdollController (100)
    public sealed class CharacterIKController : MonoBehaviour
    {
        // ------------------------------------------------------------------ tuning: look at

        [Header("Look at")]
        [Tooltip("Spine share of the look-at rotation.")]
        [Range(0f, 1f)] public float lookBodyWeight = 0.25f;
        [Tooltip("Head share of the look-at rotation.")]
        [Range(0f, 1f)] public float lookHeadWeight = 0.85f;
        [Tooltip("Eyes share of the look-at rotation.")]
        [Range(0f, 1f)] public float lookEyesWeight = 1f;
        [Tooltip("0 = unrestricted, 1 = locked straight ahead. 0.5 keeps the head within natural neck rotation.")]
        [Range(0f, 1f)] public float lookClampWeight = 0.5f;
        [Tooltip("Angular response (1/s) of the gaze toward slow targets (aim, opponents).")]
        [Min(0.1f)] public float lookSharpness = 8f;
        [Tooltip("Angular response (1/s) of the gaze toward balls in flight (fast saccade-like tracking).")]
        [Min(0.1f)] public float threatLookSharpness = 18f;
        [Tooltip("Response (1/s) of the look-at weight.")]
        [Min(0.1f)] public float lookWeightSharpness = 6f;
        [Tooltip("Targets further than this (deg) from the body forward fade the look weight out (no owl necks).")]
        [Range(30f, 180f)] public float maxLookAngle = 110f;
        [Tooltip("Look weight multiplier while stunned (dizzy).")]
        [Range(0f, 1f)] public float stunnedLookWeight = 0.3f;
        [Tooltip("Seconds ahead the ball predictor looks for impacts.")]
        [Min(0.1f)] public float threatHorizon = 1.5f;
        [Tooltip("Balls flying toward the player inside this range (m) are watched even when they will miss.")]
        [Min(0f)] public float nearbyBallWatchRange = 14f;

        // ------------------------------------------------------------------ tuning: catching

        [Header("Catching")]
        [Tooltip("Hands start reaching for the intercept point when impact is closer than this (s). Spec: 0.6 s.")]
        [Min(0.05f)] public float catchImminenceWindow = 0.6f;
        [Tooltip("Lateral offset (m) of each hand from the intercept point (hands either side of a 0.21 m ball).")]
        [Range(0.05f, 0.2f)] public float handSpacing = 0.11f;
        [Tooltip("Weight of the ready pose (hands up in front of the chest) while the catch stance is armed.")]
        [Range(0f, 1f)] public float readyPoseWeight = 0.8f;
        [Tooltip("Ready pose: distance (m, 1.78 m adult) of the hands in front of the shoulders.")]
        [Range(0.1f, 0.6f)] public float readyForward = 0.34f;
        [Tooltip("Ready pose: drop (m) of the hands below shoulder height.")]
        [Range(0f, 0.5f)] public float readyDrop = 0.16f;
        [Tooltip("Ready pose: half distance (m) between the hands.")]
        [Range(0.05f, 0.3f)] public float readyHalfWidth = 0.13f;
        [Tooltip("Response (1/s) when raising the hands into the catch stance.")]
        [Min(0.1f)] public float catchRaiseSharpness = 20f;
        [Tooltip("Response (1/s) when lowering the hands after the stance.")]
        [Min(0.1f)] public float catchLowerSharpness = 7f;
        [Tooltip("Fraction of the full arm length a hand may reach (keeps elbows from locking straight).")]
        [Range(0.5f, 1f)] public float maxReachFraction = 0.94f;
        [Tooltip("Seconds the hands take to pull a caught ball into the body.")]
        [Min(0.05f)] public float cradleDuration = 0.3f;

        // ------------------------------------------------------------------ tuning: throwing

        [Header("Throwing (offsets from the right shoulder: x = right, y = up, z = toward the aim; metres for a 1.78 m adult)")]
        [Tooltip("Wind-up point: behind and above the right shoulder.")]
        public Vector3 windupOffset = new Vector3(0.1f, 0.24f, -0.3f);
        [Tooltip("Release point: arm extended in front, slightly above the shoulder.")]
        public Vector3 releaseOffset = new Vector3(0.04f, 0.08f, 0.6f);
        [Tooltip("Follow-through point: across the body toward the opposite hip.")]
        public Vector3 followThroughOffset = new Vector3(-0.5f, -0.6f, 0.3f);
        [Tooltip("Minimum wind-up weight while charging (the arm cocks visibly even at a low charge).")]
        [Range(0f, 1f)] public float minWindupWeight = 0.25f;
        [Tooltip("Response (1/s) of the wind-up weight to the charge amount.")]
        [Min(0.1f)] public float chargeWeightSharpness = 10f;
        [Tooltip("Duration (s) of the whip arc from wind-up through release to follow-through.")]
        [Range(0.08f, 0.6f)] public float whipDuration = 0.2f;
        [Tooltip("Normalised moment of release inside the whip arc.")]
        [Range(0.1f, 0.9f)] public float releaseMoment = 0.4f;
        [Tooltip("Seconds the arm takes to hand control back to the mocap after the follow-through.")]
        [Min(0.01f)] public float whipRecoveryTime = 0.22f;
        [Tooltip("Arc intensity of passes relative to throws (softer, shorter follow-through).")]
        [Range(0.2f, 1f)] public float passWhipIntensity = 0.55f;
        [Tooltip("Chest twist (deg) at full wind-up; positive turns the throwing shoulder back.")]
        [Range(0f, 70f)] public float windupTwist = 35f;
        [Tooltip("Chest twist (deg) at the end of the follow-through; negative turns the throwing shoulder forward.")]
        [Range(-70f, 0f)] public float followThroughTwist = -30f;
        [Tooltip("Share of the yaw between body and aim added as chest twist while charging (strafing throws).")]
        [Range(0f, 1f)] public float aimTwistFactor = 0.4f;
        [Tooltip("Limit (deg) of the aim part of the chest twist.")]
        [Range(0f, 60f)] public float maxAimTwist = 35f;
        [Tooltip("Response (1/s) of the chest twist outside the whip.")]
        [Min(0.1f)] public float twistSharpness = 10f;
        [Tooltip("Weight of the non-throwing arm extending toward the target for balance while charging.")]
        [Range(0f, 1f)] public float balanceArmWeight = 0.5f;

        // ------------------------------------------------------------------ tuning: carrying

        [Header("Carrying")]
        [Tooltip("Weight of the carry pose (ball tucked at the hip) while holding a ball. Lower keeps more mocap arm swing.")]
        [Range(0f, 1f)] public float carryWeight = 0.6f;
        [Tooltip("Carry point from the right shoulder (x = right, y = up, z = forward; metres for a 1.78 m adult).")]
        public Vector3 carryOffset = new Vector3(0.2f, -0.4f, 0.16f);

        [Header("Master")]
        [Tooltip("Response (1/s) of the internal gate when IK becomes allowed again (after freeze / ragdoll).")]
        [Min(0.1f)] public float gateSharpness = 6f;

        /// <summary>
        /// Master IK weight (0 while ragdolled / frozen). External control: abilities can fade IK out. The controller also
        /// gates itself internally to 0 while the body is ragdolled, blending back from ragdoll, frozen or eliminated.
        /// </summary>
        public float MasterWeight { get; set; } = 1f;

        // ------------------------------------------------------------------ public state (additions)

        public DodgeballPlayer Owner { get; private set; }

        /// <summary>Ball currently predicted to hit this player first (null when none).</summary>
        public DodgeBall CurrentThreat => _threat;

        /// <summary>Seconds until <see cref="CurrentThreat"/> impacts (float.MaxValue when none).</summary>
        public float CurrentThreatTime => _threat != null ? _threatTime : float.MaxValue;

        /// <summary>Smoothed world point the head looks at.</summary>
        public Vector3 LookPoint => _lookPoint;

        /// <summary>Effective (gated) IK weight applied this frame.</summary>
        public float EffectiveWeight => _gate * Mathf.Clamp01(MasterWeight);

        /// <summary>Chest twist (deg) applied after animation this frame.</summary>
        public float CurrentTwist => _appliedTwist;

        // ------------------------------------------------------------------ internals

        /// <summary>One IK goal accumulated from prioritised layers (carry &lt; catch &lt; cradle &lt; wind-up &lt; whip).</summary>
        private struct HandGoal
        {
            public Vector3 Position;
            public Quaternion BoneRotation; // desired hand BONE world rotation (converted to goal space on apply)
            public Vector3 Hint;
            public float Weight;
            public float RotationWeight;
            public float HintWeight;

            public void Clear()
            {
                Weight = 0f;
                RotationWeight = 0f;
                HintWeight = 0f;
            }

            /// <summary>A later layer of weight <paramref name="w"/> pulls the goal toward itself by w.</summary>
            public void Blend(Vector3 position, Quaternion rotation, float rotationWeight, Vector3 hint, float hintWeight, float w)
            {
                if (w <= 1e-4f) return;
                if (Weight <= 1e-4f)
                {
                    Position = position;
                    BoneRotation = rotation;
                    Hint = hint;
                    RotationWeight = rotationWeight;
                    HintWeight = hintWeight;
                    Weight = Mathf.Clamp01(w);
                    return;
                }
                Position = Vector3.Lerp(Position, position, w);
                BoneRotation = Quaternion.Slerp(BoneRotation, rotation, w);
                Hint = Vector3.Lerp(Hint, hint, w);
                RotationWeight = Mathf.Lerp(RotationWeight, rotationWeight, w);
                HintWeight = Mathf.Lerp(HintWeight, hintWeight, w);
                Weight = Mathf.Clamp01(Weight + (1f - Weight) * w);
            }
        }

        private Animator _animator;
        private CharacterVisual _visual;
        private RagdollController _ragdoll;
        private bool _humanoid;
        private bool _subscribed;

        private Transform _head;
        private Transform _rightUpperArm, _leftUpperArm, _rightHand, _leftHand;
        private PalmFrame _rightPalm, _leftPalm;
        private float _statureScale = 1f;
        private float _rightArmLength = 0.58f, _leftArmLength = 0.58f;

        // Body-frame shoulder offsets (model-root rotation frame, metres) measured from the evaluated skeleton.
        private Vector3 _rightShoulderFromBody, _leftShoulderFromBody;
        private bool _shouldersMeasured;

        // Goal-space calibration: goalRotation = boneRotation * _goalFromBone (constant per avatar).
        private Quaternion _rightGoalFromBone = Quaternion.identity, _leftGoalFromBone = Quaternion.identity;
        private bool _rightCalibrated, _leftCalibrated;
        private bool _calibrateRightThisFrame, _calibrateLeftThisFrame, _measureShouldersThisFrame;

        // Captured in the IK pass for LateUpdate measurements.
        private int _ikFrame = -1;
        private int _computedFrame = -1;
        private Quaternion _ikRootRotation = Quaternion.identity;
        private Vector3 _ikBodyPosition;
        private Quaternion _ikRightGoalRotation = Quaternion.identity, _ikLeftGoalRotation = Quaternion.identity;
        private bool _ikSeen;
        private int _initFrame;
        private bool _warnedNoIkPass;

        // Gate / layer weights.
        private float _gate;
        private float _catchWeight, _chargeWeight, _carryWeight;
        private HandGoal _right, _left;

        // Look at.
        private Vector3 _lookPoint;
        private Vector3 _lookDirection = Vector3.forward;
        private float _lookDistance = 10f;
        private float _lookWeight;
        private bool _lookInitialized;
        private Vector3 _overridePoint;
        private float _overrideUntil = -1f;

        // Threats.
        private readonly List<DodgeBall> _balls = new List<DodgeBall>(8);
        private DodgeBall _threat;
        private DodgeBall _watchBall;
        private float _threatTime = float.MaxValue;
        private Vector3 _threatImpact;
        private Vector3 _threatIntercept;

        // Cradle after a catch.
        private float _cradleRemaining;
        private Vector3 _cradleStartLocal;

        // Whip.
        private bool _whipActive, _whipRecovering;
        private float _whipElapsed, _whipDuration = 0.2f, _whipIntensity = 1f, _recoverElapsed;
        private Vector3 _whipDir3 = Vector3.forward, _whipFwd = Vector3.forward, _whipRight = Vector3.right;
        private Vector3 _whipStartLocal, _whipEndLocal;
        private Quaternion _whipStartRotation = Quaternion.identity, _whipEndRotation = Quaternion.identity;
        private Vector3 _whipEndHintLocal;
        private float _whipStartTwist, _whipEndTwist;
        private int _lastReleaseFrame = -1;

        // Last applied right-hand position (whip start) and shoulder.
        private Vector3 _lastRightHandWorld, _lastRightShoulder;
        private bool _hasLastHand;

        // Spine twist.
        private readonly Transform[] _spine = new Transform[3];
        private readonly Quaternion[] _spineWritten = new Quaternion[3];
        private int _spineCount;
        private float _twist;
        private float _appliedTwist;

        // ------------------------------------------------------------------ contract

        /// <summary>Binds the controller to <paramref name="owner"/>; no-op IK when the model is not Humanoid.</summary>
        public void Initialize(DodgeballPlayer owner, Animator animator)
        {
            Owner = owner;
            _animator = animator;
            _visual = GetComponentInParent<CharacterVisual>();
            _ragdoll = animator != null ? animator.GetComponent<RagdollController>() : null;
            _humanoid = HumanoidUtil.IsValidHumanoid(animator);
            _initFrame = Time.frameCount;
            _ikSeen = false;
            _rightCalibrated = _leftCalibrated = false;
            _shouldersMeasured = false;
            ResetIK();

            if (!_humanoid)
            {
                _spineCount = 0;
                return;
            }

            _head = animator.GetBoneTransform(HumanBodyBones.Head);
            _rightUpperArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            _leftUpperArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            _rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            _leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            _rightPalm = HumanoidUtil.ComputePalmFrame(animator, true);
            _leftPalm = HumanoidUtil.ComputePalmFrame(animator, false);
            _rightArmLength = HumanoidUtil.MeasureArmLength(animator, true);
            _leftArmLength = HumanoidUtil.MeasureArmLength(animator, false);

            HumanoidUtil.TryGetSkeletonAxes(animator, out _, out Vector3 up, out _);
            _statureScale = HumanoidUtil.EstimateStatureScale(animator, up);

            _spineCount = HumanoidUtil.GetSpineChain(animator, _spine);
            for (int i = 0; i < _spineWritten.Length; i++) _spineWritten[i] = default;

            // First guess of the shoulder offsets (refined from the evaluated skeleton in LateUpdate before IK engages).
            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            Quaternion inv = Quaternion.Inverse(animator.transform.rotation);
            Vector3 body = hips != null ? hips.position + animator.transform.up * (0.05f * _statureScale) : animator.transform.position;
            if (_rightUpperArm != null) _rightShoulderFromBody = inv * (_rightUpperArm.position - body);
            if (_leftUpperArm != null) _leftShoulderFromBody = inv * (_leftUpperArm.position - body);
        }

        /// <summary>Overrides the look target for <paramref name="seconds"/> (abilities can call attention to something).</summary>
        public void OverrideLookTarget(Vector3 worldPoint, float seconds)
        {
            _overridePoint = worldPoint;
            _overrideUntil = Time.time + Mathf.Max(0f, seconds);
        }

        /// <summary>Plays the procedural throw follow-through now (called by combat on release).</summary>
        public void PlayThrowRelease(Vector3 throwDirection) => StartWhip(throwDirection, 1f);

        // ------------------------------------------------------------------ additions

        /// <summary>Clears every procedural pose (round reset, teleports).</summary>
        public void ResetIK()
        {
            _gate = 0f;
            _catchWeight = _chargeWeight = _carryWeight = 0f;
            _right.Clear();
            _left.Clear();
            _lookWeight = 0f;
            _lookInitialized = false;
            _overrideUntil = -1f;
            _cradleRemaining = 0f;
            _whipActive = _whipRecovering = false;
            _twist = 0f;
            _appliedTwist = 0f;
            _hasLastHand = false;
            _threat = null;
            _watchBall = null;
            for (int i = 0; i < _spineWritten.Length; i++) _spineWritten[i] = default;
        }

        /// <summary>Plays a softer pass arc (underhand-like follow-through) toward <paramref name="direction"/>.</summary>
        public void PlayPassRelease(Vector3 direction) => StartWhip(direction, passWhipIntensity);

        // ------------------------------------------------------------------ lifecycle

        private void OnEnable()
        {
            if (_subscribed) return;
            GameEvents.Subscribe<BallThrownEvent>(OnBallThrown);
            GameEvents.Subscribe<BallCaughtEvent>(OnBallCaught);
            _subscribed = true;
        }

        private void OnDisable()
        {
            if (!_subscribed) return;
            GameEvents.Unsubscribe<BallThrownEvent>(OnBallThrown);
            GameEvents.Unsubscribe<BallCaughtEvent>(OnBallCaught);
            _subscribed = false;
        }

        private void OnBallThrown(BallThrownEvent e)
        {
            if (Owner == null || e.Thrower != Owner) return;
            if (!HumanoidUtil.IsBodyThrow(Owner, in e)) return; // turret shots etc.: the arm stays where it is
            StartWhip(e.Velocity, e.IsPass ? passWhipIntensity : 1f);
        }

        private void OnBallCaught(BallCaughtEvent e)
        {
            if (Owner == null || e.Catcher != Owner || !_humanoid || _animator == null) return;
            // Pull the ball from where it was caught into the body (then the carry pose takes over).
            Vector3 chest = Owner.ChestPosition;
            _cradleStartLocal = Quaternion.Inverse(_animator.transform.rotation) * (e.Point - chest);
            if (_cradleStartLocal.sqrMagnitude > 1f) _cradleStartLocal = _cradleStartLocal.normalized; // guard bad points
            _cradleRemaining = cradleDuration;
        }

        // ------------------------------------------------------------------ IK pass

        private void OnAnimatorIK(int layerIndex)
        {
            if (!_humanoid || _animator == null) return;
            _ikSeen = true;

            // With IK Pass on several layers this runs once per layer: compute once, apply the same goals each time.
            if (_computedFrame != Time.frameCount)
            {
                _computedFrame = Time.frameCount;
                ComputeFrame(Time.deltaTime);
            }
            Apply();
        }

        private void ComputeFrame(float dt)
        {
            Transform root = _animator.transform;
            _ikFrame = Time.frameCount;
            _ikRootRotation = root.rotation;
            _ikBodyPosition = _animator.bodyPosition;
            _ikRightGoalRotation = _animator.GetIKRotation(AvatarIKGoal.RightHand);
            _ikLeftGoalRotation = _animator.GetIKRotation(AvatarIKGoal.LeftHand);
            Vector3 animatedRightHand = _animator.GetIKPosition(AvatarIKGoal.RightHand);

            // ---- gate: hold at 0 until the per-avatar calibration is known, snap to 0 when the body is not ours to pose
            bool blocked = IsBlocked();
            bool calibrated = _rightCalibrated && _leftCalibrated && _shouldersMeasured;
            if (blocked) _gate = 0f;
            else _gate = Approach(_gate, calibrated ? 1f : 0f, gateSharpness, dt);

            // ---- body frame (current frame: bodyPosition is already evaluated in the IK pass)
            Vector3 up = Vector3.up;
            Vector3 ownerForward = Owner != null ? Owner.Forward : Vector3.forward;
            Vector3 bodyForward = HumanoidUtil.Planar(root.forward, ownerForward);
            Vector3 bodyRight = Vector3.Cross(up, bodyForward);
            float s = _statureScale;
            Vector3 rightShoulder = _ikBodyPosition + _ikRootRotation * _rightShoulderFromBody;
            Vector3 leftShoulder = _ikBodyPosition + _ikRootRotation * _leftShoulderFromBody;
            Vector3 shoulderMid = (rightShoulder + leftShoulder) * 0.5f;
            Vector3 chest = shoulderMid - up * (0.12f * s);

            // ---- owner state
            PlayerCombatController combat = Owner != null ? Owner.Combat : null;
            PlayerStateMachine fsm = Owner != null ? Owner.StateMachine : null;
            PlayerStateId state = fsm != null ? fsm.Current : PlayerStateId.Grounded;
            bool armed = (combat != null && combat.IsCatchArmed) || state == PlayerStateId.Catching;
            bool charging = combat != null && combat.IsCharging;
            bool holding = combat != null && combat.HasBall;
            float charge = combat != null ? Mathf.Clamp01(combat.ChargeNormalized) : 0f;
            bool stunned = state == PlayerStateId.Stunned;
            bool sliding = state == PlayerStateId.Sliding || (Owner != null && Owner.Motor != null && Owner.Motor.IsSliding);

            UpdateThreat(chest, combat);
            UpdateLook(dt, bodyForward, chest, combat, stunned);

            // ---- layer weights
            _catchWeight = Approach(_catchWeight, armed ? 1f : 0f, armed ? catchRaiseSharpness : catchLowerSharpness, dt);
            float chargeTarget = charging && !_whipActive ? Mathf.Max(minWindupWeight, charge) : 0f;
            _chargeWeight = Approach(_chargeWeight, chargeTarget, chargeWeightSharpness, dt);
            bool carrying = holding && !charging && !armed && !_whipActive && !sliding && !stunned && _cradleRemaining <= 0f;
            _carryWeight = Approach(_carryWeight, carrying ? carryWeight : 0f, 8f, dt);
            if (_cradleRemaining > 0f) _cradleRemaining -= dt;

            Vector3 aimForward = ComputeAimForward(chest, bodyForward, combat);
            Vector3 aimRight = Vector3.Cross(up, aimForward);

            _right.Clear();
            _left.Clear();

            // 1) carry: ball tucked at the right hip, palm cradling it up and in
            if (_carryWeight > 1e-3f)
            {
                Vector3 p = rightShoulder + bodyRight * (carryOffset.x * s) + up * (carryOffset.y * s) + bodyForward * (carryOffset.z * s);
                Quaternion r = _rightPalm.BoneRotationFor(bodyForward, (up * 0.55f - bodyRight * 0.75f + bodyForward * 0.1f).normalized);
                Vector3 hint = rightShoulder + bodyRight * (0.22f * s) - up * (0.28f * s) - bodyForward * (0.12f * s);
                _right.Blend(p, r, 0.8f, hint, 0.6f, _carryWeight);
            }

            // 2) catch stance: ready pose in front of the chest, reaching for the intercept as impact nears
            if (_catchWeight > 1e-3f) BlendCatch(bodyForward, bodyRight, up, s, shoulderMid, rightShoulder, leftShoulder);

            // 3) cradle: pull a caught ball into the chest
            if (_cradleRemaining > 0f) BlendCradle(bodyForward, bodyRight, up, s, chest);

            // 4) wind-up while charging + balance arm toward the target
            if (_chargeWeight > 1e-3f)
            {
                Vector3 p = rightShoulder + aimRight * (windupOffset.x * s) + up * (windupOffset.y * s) + aimForward * (windupOffset.z * s);
                Quaternion r = WindupRotation(aimForward, aimRight, up);
                Vector3 hint = rightShoulder + aimRight * (0.3f * s) + up * (0.02f * s) - aimForward * (0.12f * s);
                _right.Blend(ClampReach(p, rightShoulder, _rightArmLength), r, 0.9f, hint, 0.9f, _chargeWeight);

                Vector3 lp = leftShoulder + aimForward * (0.42f * s) - aimRight * (0.05f * s) - up * (0.02f * s);
                Quaternion lr = _leftPalm.BoneRotationFor(aimForward, (-up * 0.7f + aimRight * 0.3f).normalized);
                Vector3 lh = leftShoulder - aimRight * (0.25f * s) - up * (0.2f * s);
                _left.Blend(ClampReach(lp, leftShoulder, _leftArmLength), lr, 0.5f, lh, 0.6f, _chargeWeight * balanceArmWeight);
            }

            // 5) whip: wind-up -> release -> follow-through across the body, then hand back to the mocap
            float whipTwist = 0f;
            if (_whipActive || _whipRecovering) BlendWhip(dt, rightShoulder, leftShoulder, up, s, out whipTwist);

            // ---- chest twist (applied after the Animator wrote the pose, see LateUpdate)
            if (_whipActive || _whipRecovering)
            {
                _twist = whipTwist;
            }
            else
            {
                float aimYaw = Mathf.Clamp(Vector3.SignedAngle(bodyForward, aimForward, up), -maxAimTwist, maxAimTwist);
                float target = (windupTwist + aimYaw * aimTwistFactor) * _chargeWeight;
                _twist = Approach(_twist, target, twistSharpness, dt);
            }

            // ---- bookkeeping for calibration and the next whip start
            float g = _gate * Mathf.Clamp01(MasterWeight);
            _lastRightShoulder = rightShoulder;
            _lastRightHandWorld = Vector3.Lerp(animatedRightHand, _right.Position, _right.Weight * g);
            _hasLastHand = true;

            // Unperturbed frames (no hand IK, no spine rotation from look-at, twist applied after measurement) reveal
            // the constant hand-bone -> IK-goal relation and the true shoulder positions of the mocap pose.
            bool lookBodyIdle = _lookWeight * g * lookBodyWeight < 1e-4f;
            _calibrateRightThisFrame = !blocked && lookBodyIdle && _right.Weight * g < 1e-4f;
            _calibrateLeftThisFrame = !blocked && lookBodyIdle && _left.Weight * g < 1e-4f;
            _measureShouldersThisFrame = !blocked && (lookBodyIdle || _shouldersMeasured);
        }

        private void Apply()
        {
            float g = _gate * Mathf.Clamp01(MasterWeight);

            _animator.SetLookAtWeight(_lookWeight * g, lookBodyWeight, lookHeadWeight, lookEyesWeight, lookClampWeight);
            if (_lookWeight * g > 0f) _animator.SetLookAtPosition(_lookPoint);

            ApplyHand(AvatarIKGoal.RightHand, AvatarIKHint.RightElbow, in _right, g, _rightCalibrated, _rightGoalFromBone);
            ApplyHand(AvatarIKGoal.LeftHand, AvatarIKHint.LeftElbow, in _left, g, _leftCalibrated, _leftGoalFromBone);
        }

        private void ApplyHand(AvatarIKGoal goal, AvatarIKHint hint, in HandGoal hand, float gate, bool calibrated, Quaternion goalFromBone)
        {
            float w = hand.Weight * gate;
            _animator.SetIKPositionWeight(goal, w);
            if (w > 0f) _animator.SetIKPosition(goal, hand.Position);

            float rw = calibrated ? w * hand.RotationWeight : 0f;
            _animator.SetIKRotationWeight(goal, rw);
            if (rw > 0f) _animator.SetIKRotation(goal, hand.BoneRotation * goalFromBone);

            float hw = w * hand.HintWeight;
            _animator.SetIKHintPositionWeight(hint, hw);
            if (hw > 0f) _animator.SetIKHintPosition(hint, hand.Hint);
        }

        // ------------------------------------------------------------------ layers

        private void BlendCatch(Vector3 bodyForward, Vector3 bodyRight, Vector3 up, float s, Vector3 shoulderMid,
            Vector3 rightShoulder, Vector3 leftShoulder)
        {
            // Ready pose: hands up in front of the chest, palms forward, fingers up and slightly outward.
            Vector3 readyCentre = shoulderMid + bodyForward * (readyForward * s) - up * (readyDrop * s);
            Vector3 readyRight = readyCentre + bodyRight * (readyHalfWidth * s);
            Vector3 readyLeft = readyCentre - bodyRight * (readyHalfWidth * s);
            Quaternion readyRightRot = _rightPalm.BoneRotationFor(up + bodyRight * 0.25f, bodyForward);
            Quaternion readyLeftRot = _leftPalm.BoneRotationFor(up - bodyRight * 0.25f, bodyForward);

            Vector3 rightPos = readyRight, leftPos = readyLeft;
            Quaternion rightRot = readyRightRot, leftRot = readyLeftRot;
            float imminence = 0f;

            if (_threat != null)
            {
                imminence = 1f - Mathf.Clamp01(_threatTime / Mathf.Max(0.05f, catchImminenceWindow));
                if (imminence > 0f)
                {
                    Vector3 ballDir = _threat.Velocity.sqrMagnitude > 1e-4f ? _threat.Velocity.normalized : -bodyForward;
                    Vector3 towardBall = -ballDir;
                    Vector3 lateral = Vector3.Cross(up, towardBall);
                    lateral = lateral.sqrMagnitude > 1e-4f ? lateral.normalized : bodyRight;

                    // Low balls are scooped with fingers down, everything else caught fingers up.
                    float hipHeight = shoulderMid.y - 0.55f * s;
                    Vector3 fingers = _threatIntercept.y < hipHeight ? -up : up;

                    Vector3 r = ClampReach(_threatIntercept + lateral * handSpacing, rightShoulder, _rightArmLength);
                    Vector3 l = ClampReach(_threatIntercept - lateral * handSpacing, leftShoulder, _leftArmLength);
                    Quaternion rr = _rightPalm.BoneRotationFor(fingers + lateral * 0.3f, towardBall);
                    Quaternion lr = _leftPalm.BoneRotationFor(fingers - lateral * 0.3f, towardBall);

                    rightPos = Vector3.Lerp(readyRight, r, imminence);
                    leftPos = Vector3.Lerp(readyLeft, l, imminence);
                    rightRot = Quaternion.Slerp(readyRightRot, rr, imminence);
                    leftRot = Quaternion.Slerp(readyLeftRot, lr, imminence);
                }
            }

            // Elbows out and down: a relaxed goalkeeper-like stance.
            Vector3 rightHint = rightShoulder + bodyRight * (0.3f * s) - up * (0.25f * s);
            Vector3 leftHint = leftShoulder - bodyRight * (0.3f * s) - up * (0.25f * s);
            float w = _catchWeight * Mathf.Lerp(readyPoseWeight, 1f, imminence);
            _right.Blend(ClampReach(rightPos, rightShoulder, _rightArmLength), rightRot, 0.85f, rightHint, 0.5f, w);
            _left.Blend(ClampReach(leftPos, leftShoulder, _leftArmLength), leftRot, 0.85f, leftHint, 0.5f, w);
        }

        private void BlendCradle(Vector3 bodyForward, Vector3 bodyRight, Vector3 up, float s, Vector3 chest)
        {
            float t = 1f - Mathf.Clamp01(_cradleRemaining / Mathf.Max(0.01f, cradleDuration));
            float k = t * t * (3f - 2f * t);
            Vector3 holdLocal = new Vector3(0.08f, -0.06f, 0.26f) * s;
            Vector3 local = Vector3.Lerp(_cradleStartLocal, holdLocal, k);
            Vector3 centre = chest + _ikRootRotation * local;

            // Both hands hug the ball: palms facing each other around it.
            Vector3 right = centre + bodyRight * handSpacing;
            Vector3 left = centre - bodyRight * handSpacing;
            Quaternion rr = _rightPalm.BoneRotationFor(bodyForward + up * 0.4f, -bodyRight);
            Quaternion lr = _leftPalm.BoneRotationFor(bodyForward + up * 0.4f, bodyRight);
            float w = 1f - Mathf.SmoothStep(0.55f, 1f, t);
            Vector3 rh = chest + bodyRight * (0.3f * s) - up * (0.2f * s);
            Vector3 lh = chest - bodyRight * (0.3f * s) - up * (0.2f * s);
            _right.Blend(right, rr, 0.8f, rh, 0.5f, w);
            _left.Blend(left, lr, 0.8f, lh, 0.5f, w);
        }

        private Quaternion WindupRotation(Vector3 aimForward, Vector3 aimRight, Vector3 up) =>
            // Palm faces forward/up behind the head cradling the ball, fingers point up and back.
            _rightPalm.BoneRotationFor(up * 0.8f - aimForward * 0.35f + aimRight * 0.1f, (aimForward * 0.75f + up * 0.45f).normalized);

        private void StartWhip(Vector3 direction, float intensity)
        {
            if (!_humanoid) return;
            if (_lastReleaseFrame == Time.frameCount) return; // combat call + BallThrownEvent report the same release
            _lastReleaseFrame = Time.frameCount;

            Vector3 up = Vector3.up;
            Vector3 fallback = Owner != null ? Owner.Forward : transform.forward;
            _whipDir3 = direction.sqrMagnitude > 1e-6f ? direction.normalized : fallback;
            _whipFwd = HumanoidUtil.Planar(_whipDir3, fallback);
            _whipRight = Vector3.Cross(up, _whipFwd);
            _whipIntensity = Mathf.Clamp(intensity, 0.2f, 1.5f);
            _whipDuration = whipDuration * Mathf.Lerp(1.3f, 1f, Mathf.Clamp01(_whipIntensity));

            // Start wherever the hand is now: the wind-up after a charged throw, the mocap hand after a snap throw.
            float s = _statureScale;
            Vector3 fromShoulder = _hasLastHand
                ? _lastRightHandWorld - _lastRightShoulder
                : _whipRight * (windupOffset.x * s) + up * (windupOffset.y * s) + _whipFwd * (windupOffset.z * s);
            _whipStartLocal = new Vector3(Vector3.Dot(fromShoulder, _whipRight), Vector3.Dot(fromShoulder, up), Vector3.Dot(fromShoulder, _whipFwd));
            _whipStartRotation = _right.Weight > 0.01f ? _right.BoneRotation : WindupRotation(_whipFwd, _whipRight, up);
            _whipStartTwist = _twist;

            _whipElapsed = 0f;
            _whipActive = true;
            _whipRecovering = false;
            _chargeWeight = 0f;
        }

        private void BlendWhip(float dt, Vector3 rightShoulder, Vector3 leftShoulder, Vector3 up, float s, out float twist)
        {
            Vector3 local;
            Quaternion rotation;
            Vector3 hintLocal;
            float weight;

            Vector3 release = releaseOffset * s;
            release.y += _whipDir3.y * 0.3f * s; // lobs release higher, downward throws lower
            Vector3 follow = Vector3.Lerp(releaseOffset + new Vector3(-0.15f, -0.3f, -0.1f), followThroughOffset, _whipIntensity) * s;
            Quaternion releaseRot = _rightPalm.BoneRotationFor(up, _whipDir3);
            Quaternion followRot = _rightPalm.BoneRotationFor(_whipFwd * 0.6f - up * 0.6f - _whipRight * 0.2f,
                (-up * 0.6f - _whipRight * 0.5f + _whipFwd * 0.2f).normalized);
            Vector3 windHint = new Vector3(0.3f, 0.02f, -0.12f) * s;
            Vector3 releaseHint = new Vector3(0.22f, -0.05f, 0.25f) * s;
            Vector3 followHint = new Vector3(0.1f, -0.35f, 0.22f) * s;

            if (_whipActive)
            {
                _whipElapsed += dt;
                float u = Mathf.Clamp01(_whipElapsed / Mathf.Max(0.01f, _whipDuration));
                float rm = Mathf.Clamp(releaseMoment, 0.1f, 0.9f);
                if (u < rm)
                {
                    // Accelerating arc over the shoulder toward the release point.
                    float a = u / rm;
                    a *= a;
                    Vector3 ctrl = Vector3.Lerp(_whipStartLocal, release, 0.5f) + new Vector3(0f, 0.14f * s, -0.02f * s);
                    local = Bezier(_whipStartLocal, ctrl, release, a);
                    rotation = Quaternion.Slerp(_whipStartRotation, releaseRot, a);
                    hintLocal = Vector3.Lerp(windHint, releaseHint, a);
                    twist = Mathf.Lerp(_whipStartTwist, 0f, a);
                }
                else
                {
                    // Decelerating follow-through across the body.
                    float b = (u - rm) / (1f - rm);
                    b = 1f - (1f - b) * (1f - b);
                    Vector3 ctrl = release + new Vector3(-0.05f * s, -0.12f * s, 0.12f * s);
                    local = Bezier(release, ctrl, follow, b);
                    rotation = Quaternion.Slerp(releaseRot, followRot, b);
                    hintLocal = Vector3.Lerp(releaseHint, followHint, b);
                    twist = Mathf.Lerp(0f, followThroughTwist * _whipIntensity, b);
                }
                weight = 1f;

                if (u >= 1f)
                {
                    _whipActive = false;
                    _whipRecovering = true;
                    _recoverElapsed = 0f;
                    _whipEndLocal = local;
                    _whipEndRotation = rotation;
                    _whipEndHintLocal = hintLocal;
                    _whipEndTwist = twist;
                }
            }
            else
            {
                _recoverElapsed += dt;
                float r = Mathf.Clamp01(_recoverElapsed / Mathf.Max(0.01f, whipRecoveryTime));
                float k = r * r * (3f - 2f * r);
                local = _whipEndLocal;
                rotation = _whipEndRotation;
                hintLocal = _whipEndHintLocal;
                weight = 1f - k;
                twist = _whipEndTwist * (1f - k);
                if (r >= 1f) _whipRecovering = false;
            }

            Vector3 position = rightShoulder + _whipRight * local.x + up * local.y + _whipFwd * local.z;
            Vector3 hint = rightShoulder + _whipRight * hintLocal.x + up * hintLocal.y + _whipFwd * hintLocal.z;
            _right.Blend(ClampReach(position, rightShoulder, _rightArmLength), rotation, 0.9f, hint, 0.9f, weight);

            // Counter-rotation: the free arm pulls down and back as the throwing arm comes through.
            Vector3 lp = leftShoulder - _whipRight * (0.18f * s) - up * (0.34f * s) - _whipFwd * (0.1f * s);
            Quaternion lr = _leftPalm.BoneRotationFor(-up, -_whipRight);
            Vector3 lh = leftShoulder - _whipRight * (0.3f * s) - up * (0.1f * s) - _whipFwd * (0.15f * s);
            _left.Blend(ClampReach(lp, leftShoulder, _leftArmLength), lr, 0.4f, lh, 0.6f, weight * balanceArmWeight * _whipIntensity);
        }

        // ------------------------------------------------------------------ look at / threats

        private void UpdateThreat(Vector3 chest, PlayerCombatController combat)
        {
            _threat = null;
            _watchBall = null;
            _threatTime = float.MaxValue;
            if (Owner == null) return;
            BallManager manager = BallManager.Instance;
            if (manager == null) return;

            manager.GetIncomingLiveBalls(Owner, _balls);
            float bestWatchSqr = nearbyBallWatchRange * nearbyBallWatchRange;
            for (int i = 0; i < _balls.Count; i++)
            {
                DodgeBall ball = _balls[i];
                if (ball == null || !ball.IsLive) continue;
                if (TrajectoryPredictor.PredictImpact(ball, Owner, threatHorizon, out float t, out Vector3 impact))
                {
                    if (t < _threatTime)
                    {
                        _threat = ball;
                        _threatTime = t;
                        _threatImpact = impact;
                    }
                    continue;
                }

                Vector3 toChest = chest - ball.transform.position;
                float d2 = toChest.sqrMagnitude;
                if (d2 < bestWatchSqr && Vector3.Dot(ball.Velocity, toChest) > 0f)
                {
                    bestWatchSqr = d2;
                    _watchBall = ball;
                }
            }

            if (_threat == null) return;
            // The hands meet the ball where it enters the catch zone around the chest (not on the body surface).
            float catchRadius = combat != null && combat.Profile != null ? combat.Profile.catchRadius : 0.85f;
            _threatIntercept = TrajectoryPredictor.TimeToReach(_threat, chest, catchRadius, threatHorizon, out _, out Vector3 entry)
                ? entry
                : _threatImpact;
        }

        private void UpdateLook(float dt, Vector3 bodyForward, Vector3 chest, PlayerCombatController combat, bool stunned)
        {
            Vector3 head = _head != null ? _head.position : chest + Vector3.up * (0.45f * _statureScale);
            float sharpness = lookSharpness;
            Vector3 target;

            if (Time.time < _overrideUntil)
            {
                target = _overridePoint;
            }
            else if (_threat != null)
            {
                target = _threat.transform.position;
                sharpness = threatLookSharpness;
            }
            else if (_watchBall != null)
            {
                target = _watchBall.transform.position;
                sharpness = threatLookSharpness;
            }
            else if (combat != null && combat.CurrentTarget != null && combat.CurrentTarget != Owner)
            {
                target = combat.CurrentTarget.ChestPosition;
            }
            else
            {
                Vector3 aim = Owner != null ? Owner.Intent.AimPoint : head + bodyForward * 10f;
                bool valid = !float.IsNaN(aim.x) && !float.IsInfinity(aim.x) && (aim - head).sqrMagnitude > 0.25f;
                target = valid ? aim : head + bodyForward * 10f;
            }

            Vector3 toTarget = target - head;
            float distance = toTarget.magnitude;
            Vector3 direction = distance > 1e-3f ? toTarget / distance : bodyForward;

            // Fade out for targets behind the body instead of snapping the neck.
            float planarAngle = Vector3.Angle(bodyForward, HumanoidUtil.Planar(direction, bodyForward));
            float weight = 1f - Mathf.InverseLerp(maxLookAngle, 180f, planarAngle);
            if (stunned) weight *= stunnedLookWeight;

            if (!_lookInitialized)
            {
                _lookDirection = direction;
                _lookDistance = Mathf.Max(0.5f, distance);
                _lookInitialized = true;
            }
            float k = 1f - Mathf.Exp(-sharpness * dt);
            _lookDirection = Vector3.Slerp(_lookDirection, direction, k);
            if (_lookDirection.sqrMagnitude < 1e-6f) _lookDirection = bodyForward;
            _lookDirection.Normalize();
            _lookDistance = Mathf.Lerp(_lookDistance, Mathf.Max(0.5f, distance), k);
            _lookPoint = head + _lookDirection * _lookDistance;
            _lookWeight = Approach(_lookWeight, weight, lookWeightSharpness, dt);
        }

        private Vector3 ComputeAimForward(Vector3 chest, Vector3 bodyForward, PlayerCombatController combat)
        {
            Vector3 aim;
            if (combat != null && combat.CurrentTarget != null && combat.CurrentTarget != Owner)
                aim = combat.CurrentTarget.ChestPosition - chest;
            else if (Owner != null)
                aim = Owner.Intent.AimDirection;
            else
                aim = bodyForward;
            return HumanoidUtil.Planar(aim, bodyForward);
        }

        // ------------------------------------------------------------------ post-animation (twist, calibration)

        private void LateUpdate()
        {
            if (!_humanoid || _animator == null) return;

            if (_ikFrame == Time.frameCount)
            {
                MeasureAfterAnimation();
            }
            else if (!_ikSeen && !_warnedNoIkPass && _animator.isActiveAndEnabled && _animator.runtimeAnimatorController != null &&
                     Time.frameCount - _initFrame > 60)
            {
                _warnedNoIkPass = true;
                Debug.LogWarning(
                    $"[Dodgeball Ultra] '{_animator.runtimeAnimatorController.name}' has no IK Pass on its base layer, so the " +
                    "procedural catch reach, throw wind-up and ball look-at are inactive. Enable 'IK Pass' on the base layer " +
                    "(the Setup Wizard's generated controllers already do).", this);
            }

            bool blocked = IsBlocked();
            float twist = blocked ? 0f : _twist * _gate * Mathf.Clamp01(MasterWeight);
            _appliedTwist = twist;
            HumanoidUtil.ApplySpineTwist(_spine, _spineCount, _spineWritten, _animator.transform.up, twist);
        }

        /// <summary>
        /// Reads the freshly evaluated skeleton (before this frame's twist): calibrates the hand-bone to IK-goal rotation
        /// offset and tracks the shoulder positions relative to the body centre.
        /// </summary>
        private void MeasureAfterAnimation()
        {
            Quaternion rootNow = _animator.transform.rotation;
            Quaternion invRootNow = Quaternion.Inverse(rootNow);
            Quaternion invRootIk = Quaternion.Inverse(_ikRootRotation);

            if (_calibrateRightThisFrame && _rightHand != null)
            {
                Quaternion boneLocal = invRootNow * _rightHand.rotation;
                Quaternion goalLocal = invRootIk * _ikRightGoalRotation;
                _rightGoalFromBone = Quaternion.Inverse(boneLocal) * goalLocal;
                _rightCalibrated = true;
            }
            if (_calibrateLeftThisFrame && _leftHand != null)
            {
                Quaternion boneLocal = invRootNow * _leftHand.rotation;
                Quaternion goalLocal = invRootIk * _ikLeftGoalRotation;
                _leftGoalFromBone = Quaternion.Inverse(boneLocal) * goalLocal;
                _leftCalibrated = true;
            }

            if (_measureShouldersThisFrame && _rightUpperArm != null && _leftUpperArm != null)
            {
                Vector3 r = invRootNow * (_rightUpperArm.position - _ikBodyPosition);
                Vector3 l = invRootNow * (_leftUpperArm.position - _ikBodyPosition);
                if (!_shouldersMeasured)
                {
                    _rightShoulderFromBody = r;
                    _leftShoulderFromBody = l;
                    _shouldersMeasured = true;
                }
                else
                {
                    // Tracks crouches / stance changes of the mocap with a short time constant.
                    float k = 1f - Mathf.Exp(-6f * Time.deltaTime);
                    _rightShoulderFromBody = Vector3.Lerp(_rightShoulderFromBody, r, k);
                    _leftShoulderFromBody = Vector3.Lerp(_leftShoulderFromBody, l, k);
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        private bool IsBlocked()
        {
            if (Owner == null || _animator == null || !_animator.enabled) return true;
            if (_ragdoll != null && (_ragdoll.IsRagdolled || _ragdoll.IsBlending)) return true;
            if (_visual != null && _visual.IsFrozen) return true;
            PlayerStateMachine fsm = Owner.StateMachine;
            if (fsm != null && fsm.Current == PlayerStateId.Incapacitated)
            {
                IncapacitationReason reason = fsm.IncapacitationReason;
                if (reason == IncapacitationReason.Eliminated || reason == IncapacitationReason.Frozen ||
                    reason == IncapacitationReason.Grabbed)
                    return true;
            }
            return false;
        }

        /// <summary>Keeps <paramref name="point"/> within reach of <paramref name="shoulder"/>.</summary>
        private Vector3 ClampReach(Vector3 point, Vector3 shoulder, float armLength)
        {
            float reach = armLength * maxReachFraction;
            Vector3 d = point - shoulder;
            float m = d.magnitude;
            return m > reach && m > 1e-4f ? shoulder + d * (reach / m) : point;
        }

        private static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, float t)
        {
            float u = 1f - t;
            return u * u * a + 2f * u * t * b + t * t * c;
        }

        /// <summary>Frame-rate independent exponential approach.</summary>
        private static float Approach(float current, float target, float sharpness, float dt) =>
            dt <= 0f ? current : Mathf.Lerp(current, target, 1f - Mathf.Exp(-sharpness * dt));
    }
}
