using System.Collections.Generic;
using DodgeballUltra.Combat;
using DodgeballUltra.Events;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Characters
{
    /// <summary>
    /// CONTRACT (kernel) - feeds <see cref="AnimatorParams"/> from the player's motor/state/combat every frame and exposes
    /// one-shot triggers. Lives on the model's Animator GameObject.
    /// <para>
    /// Parameters written every frame (only those the controller actually has - user-authored controllers may lack some):
    /// Speed (planar m/s, damped), VerticalSpeed, MotionSpeed (stride matching), Lean, Grounded, Sprinting, Sliding,
    /// Charging, ChargeAmount, Catching, Stunned, Frozen, Outfield, HoldingBall.
    /// </para>
    /// <para>
    /// Triggers come from gameplay facts: <c>BallThrownEvent</c> (thrower = owner) -> Throw, <c>BallCaughtEvent</c>
    /// (catcher = owner) -> Catch, <c>BallHitPlayerEvent</c> (victim = owner) -> Hit, <c>PlayerMotor.Jumped</c> -> Jump,
    /// <c>RoundEndedEvent</c> -> Cheer (winners) / Defeat (losers). Other modules may call the Trigger* methods directly
    /// too; a trigger fired twice in the same frame is only sent once.
    /// </para>
    /// <para>
    /// Stride matching: the locomotion blend tree interpolates idle/walk/run/sprint clips at their natural speeds
    /// (<see cref="AnimatorParams.WalkSpeed"/>...). Inside that range playback speed 1 already matches the ground speed;
    /// beyond the fastest clip (haste, overdrive) and when the clips carry root motion, <c>MotionSpeed</c> scales playback
    /// so the planted foot does not slide.
    /// </para>
    /// <para>Owner module: Characters.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(50)] // after DodgeballPlayer ticked motor/state/combat, before the Animator evaluates
    public sealed class PlayerAnimatorDriver : MonoBehaviour
    {
        // ------------------------------------------------------------------ tuning

        [Header("Smoothing")]
        [Tooltip("Damping time (s) of the Speed parameter. Smooths blend-tree transitions without visible lag.")]
        [Range(0f, 0.5f)] public float speedDampTime = 0.08f;

        [Tooltip("Damping time (s) of VerticalSpeed.")]
        [Range(0f, 0.5f)] public float verticalSpeedDampTime = 0.05f;

        [Tooltip("Damping time (s) of ChargeAmount.")]
        [Range(0f, 0.5f)] public float chargeDampTime = 0.05f;

        [Tooltip("Damping time (s) of the optional Lean parameter.")]
        [Range(0f, 0.5f)] public float leanDampTime = 0.1f;

        [Header("Stride matching (reduces foot sliding)")]
        [Tooltip("Drive MotionSpeed so the stride of the mocap clips matches the actual ground speed.")]
        public bool matchStrideToSpeed = true;

        [Tooltip("Planar speed (m/s) of the fastest locomotion clip. Above it the sprint clip is played faster instead.")]
        [Min(0.5f)] public float topClipSpeed = AnimatorParams.SprintSpeed;

        [Tooltip("When the mocap clips carry root motion, measure their real stride speed from Animator.velocity (more " +
                 "accurate than the blend thresholds when a hero is scaled or a clip was authored at another pace).")]
        public bool measureRootMotionStride = true;

        [Tooltip("Lowest MotionSpeed ever written.")]
        [Range(0.3f, 1f)] public float minMotionSpeed = 0.75f;

        [Tooltip("Highest MotionSpeed ever written (beyond this the stride would look frantic).")]
        [Range(1f, 2.5f)] public float maxMotionSpeed = 1.45f;

        [Tooltip("Stride reduction on ice (Absolute Zero): with 0 traction the legs cycle this much slower while the body glides.")]
        [Range(0f, 1f)] public float iceStrideReduction = 0.35f;

        [Tooltip("Response (1/s) of the MotionSpeed smoothing.")]
        [Min(0.1f)] public float motionSpeedSharpness = 8f;

        // ------------------------------------------------------------------ state

        public Animator Animator { get; private set; }

        /// <summary>The player whose state is animated.</summary>
        public DodgeballPlayer Owner { get; private set; }

        /// <summary>True while playback is frozen (Elsa's freeze).</summary>
        public bool IsPaused { get; private set; }

        /// <summary>Playback speed multiplier used while not paused (1 = normal). Abilities may slow or speed the body up.</summary>
        public float PlaybackSpeed
        {
            get => _playbackSpeed;
            set
            {
                _playbackSpeed = Mathf.Max(0f, value);
                ApplyAnimatorSpeed();
            }
        }

        /// <summary>MotionSpeed value written last frame.</summary>
        public float CurrentMotionSpeed => _motionSpeed;

        private float _playbackSpeed = 1f;
        private float _motionSpeed = 1f;
        private float _naturalStrideSpeed;   // measured m/s of the current blend at MotionSpeed 1 (0 = unknown)
        private bool _subscribed;
        private bool _jumpHooked;
        private PlayerMotor _hookedMotor;
        private CharacterVisual _visual;

        // Parameter availability cache (user controllers may lack some parameters).
        private readonly HashSet<int> _floats = new HashSet<int>();
        private readonly HashSet<int> _bools = new HashSet<int>();
        private readonly HashSet<int> _triggers = new HashSet<int>();
        private RuntimeAnimatorController _cachedController;
        private bool _cacheValid;

        // Same-frame de-duplication of triggers (several systems report the same fact).
        private static readonly int[] s_triggerHashes =
        {
            AnimatorParams.ThrowHash, AnimatorParams.CatchHash, AnimatorParams.HitHash,
            AnimatorParams.JumpHash, AnimatorParams.CheerHash, AnimatorParams.DefeatHash,
        };
        private readonly int[] _triggerFrame = { -1, -1, -1, -1, -1, -1 };

        // ------------------------------------------------------------------ contract

        /// <summary>Binds the driver to <paramref name="owner"/> and its (possibly null) <paramref name="animator"/>.</summary>
        public void Initialize(DodgeballPlayer owner, Animator animator)
        {
            UnhookMotor();
            Owner = owner;
            Animator = animator;
            _visual = GetComponentInParent<CharacterVisual>();
            _cachedController = null;
            _cacheValid = false;
            _motionSpeed = 1f;
            _naturalStrideSpeed = 0f;
            IsPaused = false;
            for (int i = 0; i < _triggerFrame.Length; i++) _triggerFrame[i] = -1;
            if (isActiveAndEnabled)
            {
                Subscribe();
                HookMotor();
            }
            ApplyAnimatorSpeed();
            RefreshParameterCache();
        }

        public void TriggerThrow() => Fire(0);
        public void TriggerCatch() => Fire(1);
        public void TriggerHit() => Fire(2);
        public void TriggerJump() => Fire(3);
        public void TriggerCheer() => Fire(4);
        public void TriggerDefeat() => Fire(5);

        /// <summary>Freezes/unfreezes animation playback (Elsa freeze, hitstop is handled by timeScale).</summary>
        public void SetPaused(bool paused)
        {
            IsPaused = paused;
            ApplyAnimatorSpeed();
        }

        // ------------------------------------------------------------------ additions

        /// <summary>True when the bound controller has a parameter with this hash (any type).</summary>
        public bool HasParameter(int hash)
        {
            EnsureCache();
            return _floats.Contains(hash) || _bools.Contains(hash) || _triggers.Contains(hash);
        }

        /// <summary>
        /// Round reset: unpauses, clears pending triggers and rebinds the Animator to its default state (Locomotion),
        /// evaluating the pose immediately so no stale pose (cheer, dizzy, ragdoll) is shown for a frame.
        /// </summary>
        public void ResetAnimator()
        {
            IsPaused = false;
            _motionSpeed = 1f;
            _naturalStrideSpeed = 0f;
            for (int i = 0; i < _triggerFrame.Length; i++) _triggerFrame[i] = -1;
            if (Animator == null) return;
            ApplyAnimatorSpeed();
            if (!Animator.isActiveAndEnabled || Animator.runtimeAnimatorController == null) return;

            Animator.Rebind();
            Animator.Update(0f);
            _cacheValid = false;
            RefreshParameterCache();
        }

        // ------------------------------------------------------------------ lifecycle

        private void OnEnable()
        {
            if (Owner == null) return;
            Subscribe();
            HookMotor();
            ApplyAnimatorSpeed();
        }

        private void OnDisable()
        {
            Unsubscribe();
            UnhookMotor();
        }

        private void OnDestroy()
        {
            Unsubscribe();
            UnhookMotor();
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            GameEvents.Subscribe<BallThrownEvent>(OnBallThrown);
            GameEvents.Subscribe<BallCaughtEvent>(OnBallCaught);
            GameEvents.Subscribe<BallHitPlayerEvent>(OnBallHitPlayer);
            GameEvents.Subscribe<RoundEndedEvent>(OnRoundEnded);
            GameEvents.Subscribe<RoundStartedEvent>(OnRoundStarted);
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            GameEvents.Unsubscribe<BallThrownEvent>(OnBallThrown);
            GameEvents.Unsubscribe<BallCaughtEvent>(OnBallCaught);
            GameEvents.Unsubscribe<BallHitPlayerEvent>(OnBallHitPlayer);
            GameEvents.Unsubscribe<RoundEndedEvent>(OnRoundEnded);
            GameEvents.Unsubscribe<RoundStartedEvent>(OnRoundStarted);
            _subscribed = false;
        }

        private void HookMotor()
        {
            if (_jumpHooked || Owner == null || Owner.Motor == null) return;
            _hookedMotor = Owner.Motor;
            _hookedMotor.Jumped += OnJumped;
            _jumpHooked = true;
        }

        private void UnhookMotor()
        {
            if (!_jumpHooked) return;
            if (_hookedMotor != null) _hookedMotor.Jumped -= OnJumped;
            _hookedMotor = null;
            _jumpHooked = false;
        }

        // ------------------------------------------------------------------ per-frame parameters

        private void Update()
        {
            if (Owner == null) return;
            // Motor may be created after the visual during initialisation: hook lazily.
            if (!_jumpHooked) HookMotor();

            Animator a = Animator;
            if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) return;
            EnsureCache();
            if (!_cacheValid) return;

            float dt = Time.deltaTime;
            PlayerMotor motor = Owner.Motor;
            PlayerStateMachine fsm = Owner.StateMachine;
            PlayerCombatController combat = Owner.Combat;
            StatusEffectController status = Owner.Status;

            PlayerStateId state = fsm != null ? fsm.Current : PlayerStateId.Grounded;
            float planarSpeed = motor != null ? motor.PlanarSpeed : 0f;
            float verticalSpeed = motor != null ? motor.Velocity.y : 0f;
            bool grounded = motor == null || motor.IsGrounded;
            bool sliding = (motor != null && motor.IsSliding) || state == PlayerStateId.Sliding;
            bool sprinting = state == PlayerStateId.Sprinting;
            bool charging = (combat != null && combat.IsCharging) || state == PlayerStateId.ChargingThrow;
            float charge = combat != null ? Mathf.Clamp01(combat.ChargeNormalized) : 0f;
            bool catching = (combat != null && combat.IsCatchArmed) || state == PlayerStateId.Catching;
            bool stunned = state == PlayerStateId.Stunned;
            bool frozen = (status != null && status.Has(StatusEffectType.Frozen)) ||
                          (fsm != null && state == PlayerStateId.Incapacitated && fsm.IncapacitationReason == IncapacitationReason.Frozen) ||
                          (_visual != null && _visual.IsFrozen);
            bool outfield = !Owner.IsInfield;
            bool holding = combat != null && combat.HasBall;

            SetFloat(AnimatorParams.SpeedHash, planarSpeed, speedDampTime, dt);
            SetFloat(AnimatorParams.VerticalSpeedHash, grounded ? 0f : verticalSpeed, verticalSpeedDampTime, dt);
            SetFloat(AnimatorParams.ChargeAmountHash, charging ? charge : 0f, chargeDampTime, dt);

            if (_floats.Contains(AnimatorParams.LeanHash))
            {
                float lean = 0f;
                ProceduralLean pl = _visual != null ? _visual.Lean : null;
                if (pl != null && pl.maxRoll > 0.01f) lean = Mathf.Clamp(pl.CurrentRoll / pl.maxRoll, -1f, 1f);
                SetFloat(AnimatorParams.LeanHash, lean, leanDampTime, dt);
            }

            SetBool(AnimatorParams.GroundedHash, grounded);
            SetBool(AnimatorParams.SprintingHash, sprinting);
            SetBool(AnimatorParams.SlidingHash, sliding);
            SetBool(AnimatorParams.ChargingHash, charging);
            SetBool(AnimatorParams.CatchingHash, catching);
            SetBool(AnimatorParams.StunnedHash, stunned);
            SetBool(AnimatorParams.FrozenHash, frozen);
            SetBool(AnimatorParams.OutfieldHash, outfield);
            SetBool(AnimatorParams.HoldingBallHash, holding);

            UpdateMotionSpeed(a, motor, planarSpeed, grounded, sliding, dt);
        }

        /// <summary>
        /// Chooses MotionSpeed so the stride matches the ground speed:
        /// <list type="bullet">
        /// <item>root-motion clips: natural speed = |Animator.velocity| / (MotionSpeed * Animator.speed) of the last frame
        ///       (low-pass filtered), MotionSpeed = groundSpeed / natural;</item>
        /// <item>otherwise: the blend tree matches the ground speed up to <see cref="topClipSpeed"/>; beyond it the sprint
        ///       clip plays faster (groundSpeed / topClipSpeed).</item>
        /// </list>
        /// Ice (low traction) slows the stride: the body glides further than the legs step.
        /// </summary>
        private void UpdateMotionSpeed(Animator a, PlayerMotor motor, float planarSpeed, bool grounded, bool sliding, float dt)
        {
            float target = 1f;
            if (matchStrideToSpeed && grounded && !sliding && planarSpeed > 0.3f)
            {
                if (measureRootMotionStride && a.hasRootMotion && a.speed > 0.01f && _motionSpeed > 0.01f && dt > 0f)
                {
                    Vector3 v = a.velocity;
                    v.y = 0f;
                    float natural = v.magnitude / (_motionSpeed * a.speed);
                    // Accept only plausible locomotion speeds (in-place clips report ~0 and are ignored).
                    if (natural > 0.5f && natural < 12f)
                    {
                        _naturalStrideSpeed = _naturalStrideSpeed <= 0f
                            ? natural
                            : Mathf.Lerp(_naturalStrideSpeed, natural, 1f - Mathf.Exp(-4f * dt));
                    }
                }

                float reference = _naturalStrideSpeed > 0.5f ? _naturalStrideSpeed : Mathf.Min(planarSpeed, topClipSpeed);
                target = planarSpeed / Mathf.Max(0.1f, reference);

                float traction = motor != null ? Mathf.Clamp01(motor.Traction) : 1f;
                target *= Mathf.Lerp(1f - iceStrideReduction, 1f, traction);
                target = Mathf.Clamp(target, minMotionSpeed, maxMotionSpeed);
            }
            else if (planarSpeed <= 0.3f)
            {
                _naturalStrideSpeed = 0f; // re-measure next time the character moves
            }

            _motionSpeed = dt > 0f ? Mathf.Lerp(_motionSpeed, target, 1f - Mathf.Exp(-motionSpeedSharpness * dt)) : _motionSpeed;
            if (_floats.Contains(AnimatorParams.MotionSpeedHash)) a.SetFloat(AnimatorParams.MotionSpeedHash, _motionSpeed);
        }

        private void SetFloat(int hash, float value, float damp, float dt)
        {
            if (!_floats.Contains(hash)) return;
            if (damp > 0f && dt > 0f) Animator.SetFloat(hash, value, damp, dt);
            else Animator.SetFloat(hash, value);
        }

        private void SetBool(int hash, bool value)
        {
            if (_bools.Contains(hash)) Animator.SetBool(hash, value);
        }

        // ------------------------------------------------------------------ triggers

        private void Fire(int index)
        {
            int frame = Time.frameCount;
            if (_triggerFrame[index] == frame) return; // already sent this frame by another reporter
            _triggerFrame[index] = frame;

            Animator a = Animator;
            if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) return;
            EnsureCache();
            int hash = s_triggerHashes[index];
            if (_triggers.Contains(hash)) a.SetTrigger(hash);
        }

        private void ResetTrigger(int index)
        {
            Animator a = Animator;
            if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) return;
            EnsureCache();
            int hash = s_triggerHashes[index];
            if (_triggers.Contains(hash)) a.ResetTrigger(hash);
        }

        private void OnBallThrown(BallThrownEvent e)
        {
            // Shots a device fires on the owner's behalf (Screws' turret) must not swing the arm.
            if (Owner != null && e.Thrower == Owner && HumanoidUtil.IsBodyThrow(Owner, in e)) TriggerThrow();
        }

        private void OnBallCaught(BallCaughtEvent e)
        {
            if (Owner != null && e.Catcher == Owner) TriggerCatch();
        }

        private void OnBallHitPlayer(BallHitPlayerEvent e)
        {
            if (Owner == null || e.Victim != Owner) return;
            // A negated hit (shield, evasion, invulnerability) must not make the body flinch.
            if (e.Outcome == HitOutcome.Negated || e.Outcome == HitOutcome.Ignored) return;
            TriggerHit();
        }

        private void OnJumped() => TriggerJump();

        private void OnRoundEnded(RoundEndedEvent e)
        {
            if (Owner == null || !e.Winner.IsValid()) return; // draw: nobody celebrates
            if (e.Winner == Owner.Team) TriggerCheer();
            else TriggerDefeat();
        }

        private void OnRoundStarted(RoundStartedEvent e)
        {
            // Leftover celebration triggers must not fire in the new round.
            ResetTrigger(4);
            ResetTrigger(5);
        }

        // ------------------------------------------------------------------ helpers

        private void ApplyAnimatorSpeed()
        {
            if (Animator != null) Animator.speed = IsPaused ? 0f : _playbackSpeed;
        }

        private void EnsureCache()
        {
            if (Animator == null) return;
            if (!_cacheValid || _cachedController != Animator.runtimeAnimatorController) RefreshParameterCache();
        }

        /// <summary>One allocation (Animator.parameters) per controller: records which shared parameters exist.</summary>
        private void RefreshParameterCache()
        {
            _floats.Clear();
            _bools.Clear();
            _triggers.Clear();
            _cacheValid = false;
            Animator a = Animator;
            if (a == null) return;
            _cachedController = a.runtimeAnimatorController;
            if (_cachedController == null) return;
            if (!a.isActiveAndEnabled || !a.isInitialized) return; // parameters are unavailable until the Animator initialised

            AnimatorControllerParameter[] parameters = a.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                AnimatorControllerParameter p = parameters[i];
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: _floats.Add(p.nameHash); break;
                    case AnimatorControllerParameterType.Bool: _bools.Add(p.nameHash); break;
                    case AnimatorControllerParameterType.Trigger: _triggers.Add(p.nameHash); break;
                }
            }
            _cacheValid = true;
        }
    }
}
