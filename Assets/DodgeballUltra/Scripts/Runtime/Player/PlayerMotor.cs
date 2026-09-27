using System;
using System.Collections.Generic;
using UnityEngine;

namespace DodgeballUltra.Player
{
    /// <summary>
    /// CONTRACT (kernel) - Rigidbody-based character motor with momentum preservation.
    /// Smooth acceleration curves, friction loss during slides, air control, knockback impulses,
    /// stackable speed modifiers and traction (Absolute Zero ice) handling.
    /// Ticked by <see cref="DodgeballPlayer"/> (no Update/FixedUpdate of its own).
    /// <para>Owner module: Player.</para>
    /// <para>
    /// Model: the motor owns the Rigidbody's linear velocity. Every physics step it
    /// <list type="number">
    /// <item>probes the ground (sphere cast on <see cref="GameLayers.GroundMask"/>, triggers ignored, own colliders skipped),</item>
    /// <item>moves the planar velocity toward the wish velocity (<c>input * CurrentMaxSpeed</c>) with
    ///       <c>acceleration * accelerationCurve(speed / max)</c> - a smooth, speed-dependent acceleration curve - or with
    ///       <c>deceleration</c> when braking; both are scaled by <see cref="Traction"/> (ice = heavy inertia) and by the
    ///       knockback control factor,</item>
    /// <item>applies custom gravity (<c>Physics.gravity * gravityMultiplier</c>) in the air or projects the motion onto the
    ///       ground plane (slopes) and snaps down while grounded,</item>
    /// <item>clamps the body inside its court confinement and removes outward velocity,</item>
    /// <item>turns the body toward the explicit facing request or the move direction, and publishes the yaw rate and planar
    ///       acceleration that drive procedural leaning.</item>
    /// </list>
    /// Slides preserve momentum: they start at <c>max(current planar speed, walkSpeed) * slideBoost</c> and lose speed to
    /// <c>slideFriction</c> (m/s^2) until <c>slideDuration</c> elapses or the speed becomes too low.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerMotor : MonoBehaviour
    {
        // ------------------------------------------------------------------ motor-level tuning (per prefab, not per hero)

        [Header("Ground detection")]
        [Tooltip("Steepest walkable slope (deg). Steeper surfaces are treated as walls.")]
        [Range(0f, 89f)] [SerializeField] private float maxSlopeAngle = 50f;

        [Tooltip("Ground probe sphere radius as a fraction of the capsule radius (smaller than 1 so touching a wall never counts as ground).")]
        [Range(0.3f, 1f)] [SerializeField] private float groundProbeRadiusScale = 0.9f;

        [Tooltip("How far above the feet the ground probe starts (m).")]
        [Min(0.01f)] [SerializeField] private float groundProbeLift = 0.12f;

        [Tooltip("Gap under the feet (m) that still counts as landing while airborne.")]
        [Min(0f)] [SerializeField] private float groundTolerance = 0.05f;

        [Tooltip("While grounded the body sticks to ground up to this far below the feet (m): keeps contact on slopes and small steps.")]
        [Min(0f)] [SerializeField] private float groundSnapDistance = 0.25f;

        [Tooltip("Maximum downward snap speed (m/s) used to keep ground contact.")]
        [Min(0f)] [SerializeField] private float maxSnapSpeed = 6f;

        [Tooltip("Seconds after a jump or an upward impulse during which ground contact is ignored.")]
        [Min(0f)] [SerializeField] private float jumpGroundIgnoreTime = 0.12f;

        [Tooltip("An airborne body rising faster than this (m/s) cannot land.")]
        [Min(0f)] [SerializeField] private float maxLandingRiseSpeed = 1f;

        [Header("Air")]
        [Tooltip("Terminal fall speed (m/s).")]
        [Min(1f)] [SerializeField] private float maxFallSpeed = 40f;

        [Tooltip("Turn-rate multiplier while airborne (bodies cannot pivot freely mid-air).")]
        [Range(0f, 1f)] [SerializeField] private float airTurnMultiplier = 0.5f;

        [Tooltip("Minimum seconds between two jumps.")]
        [Min(0f)] [SerializeField] private float jumpCooldown = 0.2f;

        [Header("Handling")]
        [Tooltip("Sideways grip when changing direction on the ground, as a multiple of the deceleration. Higher = tighter turns.")]
        [Min(0f)] [SerializeField] private float lateralGrip = 1.25f;

        [Tooltip("Lowest traction accepted (0 would make a player on ice completely helpless).")]
        [Range(0.01f, 1f)] [SerializeField] private float minTraction = 0.06f;

        [Tooltip("Planar acceleration readout clamp (m/s^2). Protects procedural lean from collision spikes.")]
        [Min(1f)] [SerializeField] private float maxReportedAcceleration = 80f;

        [Header("Slide")]
        [Tooltip("A slide ends early when its speed drops below this (m/s).")]
        [Min(0f)] [SerializeField] private float slideMinSpeed = 1.6f;

        [Tooltip("How fast the slide direction can be steered with the stick (deg/s).")]
        [Min(0f)] [SerializeField] private float slideSteerRate = 35f;

        [Tooltip("Slide friction multiplier at zero traction (slides on ice go much further). Lerped to 1 at full traction.")]
        [Range(0f, 1f)] [SerializeField] private float slideIceFrictionScale = 0.3f;

        [Tooltip("A slide that leaves the ground for longer than this (s) ends.")]
        [Min(0f)] [SerializeField] private float slideMaxAirTime = 0.2f;

        [Header("Knockback")]
        [Tooltip("Velocity change (m/s) at which movement control is fully lost.")]
        [Min(0.01f)] [SerializeField] private float knockbackFullLossSpeed = 5f;

        [Tooltip("Fraction of movement control left right after a full knockback.")]
        [Range(0f, 1f)] [SerializeField] private float knockbackMinControl = 0.1f;

        [Tooltip("Seconds to regain full control after a full knockback.")]
        [Min(0.01f)] [SerializeField] private float knockbackRecoveryTime = 0.45f;

        [Header("Confinement")]
        [Tooltip("Keep the whole capsule (not only its centre) inside the confinement bounds.")]
        [SerializeField] private bool insetConfinementByRadius = true;

        // ------------------------------------------------------------------ contract state

        public MotorProfile Profile { get; set; } = new MotorProfile();

        public Vector3 Velocity { get; private set; }
        public Vector3 PlanarVelocity { get; private set; }
        public float PlanarSpeed { get; private set; }
        public bool IsGrounded { get; private set; }
        public Vector3 GroundNormal { get; private set; } = Vector3.up;

        /// <summary>Signed yaw rate of the body (deg/s). Positive = turning right. Drives procedural leaning.</summary>
        public float YawRate { get; private set; }

        /// <summary>Planar acceleration this step (m/s^2). Drives procedural pitch lean.</summary>
        public Vector3 PlanarAcceleration { get; private set; }

        public MovementMode Mode { get; private set; } = MovementMode.Walk;
        public bool IsSliding { get; private set; }
        public float SlideTimeRemaining { get; private set; }

        /// <summary>Top speed for the current mode after all modifiers (m/s).</summary>
        public float CurrentMaxSpeed { get; private set; }

        /// <summary>Product of all speed modifiers and status effects (Slow/Haste).</summary>
        public float SpeedMultiplier { get; private set; } = 1f;

        /// <summary>1 = full grip, 0 = ice. Lower traction = heavy inertia/sliding (Absolute Zero).</summary>
        public float Traction { get; private set; } = 1f;

        public bool CanJump { get; private set; }
        public bool CanSlide { get; private set; }

        /// <summary>Raised when the character lands: impact vertical speed (m/s, positive).</summary>
        public event Action<float> Landed;
        public event Action Jumped;
        public event Action SlideStarted;
        public event Action SlideEnded;

        // ------------------------------------------------------------------ additional public state (additions to the contract)

        /// <summary>The player this motor belongs to (may be null for a bare motor in tests).</summary>
        public DodgeballPlayer Owner { get; private set; }

        /// <summary>True while <see cref="SetFrozen"/> holds the body still.</summary>
        public bool IsFrozen => _frozen;

        /// <summary>Current planar slide direction (zero when not sliding).</summary>
        public Vector3 SlideDirection => IsSliding ? _slideDir : Vector3.zero;

        /// <summary>0..1 movement control (drops after knockback impulses, recovers over time).</summary>
        public float Control => _control;

        /// <summary>Seconds since the body left the ground (0 while grounded).</summary>
        public float AirTime => _airTime;

        /// <summary>Seconds until the next slide is allowed.</summary>
        public float SlideCooldownRemaining => _slideCooldownTimer;

        /// <summary>The active confinement, if any.</summary>
        public Bounds? Confinement => _hasConfinement ? _confinement : (Bounds?)null;

        /// <summary>Unscaled capsule height (m) that <see cref="SetCapsuleHeightScale"/> scales.</summary>
        public float BaseCapsuleHeight => _baseHeight;

        // ------------------------------------------------------------------ internals

        private static readonly MotorProfile s_fallbackProfile = new MotorProfile();
        private static readonly RaycastHit[] s_groundHits = new RaycastHit[12];
        private static readonly object s_anonymousSource = new object();

        private readonly Dictionary<object, float> _speedModifiers = new Dictionary<object, float>(4);
        private readonly Dictionary<object, float> _tractionModifiers = new Dictionary<object, float>(2);
        private float _speedModifierProduct = 1f;
        private float _tractionModifierMin = 1f;

        private Rigidbody _rb;
        private CapsuleCollider _capsule;
        private float _baseHeight = 1.8f;
        private float _heightScale = 1f;

        private Vector3 _moveDir;
        private float _moveMagnitude;

        private Vector3 _facingDir;
        private int _facingFrame = int.MinValue;

        private bool _jumpRequested;
        private float _jumpCooldownTimer;
        private float _groundIgnoreTimer;
        private float _airTime;
        private float _airMaxSpeed;
        private float _lastAirVerticalSpeed;

        private Vector3 _slideDir;
        private float _slideSpeed;
        private float _slideCooldownTimer;

        private float _control = 1f;

        private bool _frozen;
        private bool _madeKinematic;
        private CollisionDetectionMode _restoreCollisionMode = CollisionDetectionMode.Continuous;

        private bool _hasConfinement;
        private Bounds _confinement;

        private float _prevYaw;
        private Vector3 _prevPlanarVelocity;

        private MotorProfile ActiveProfile => Profile ?? s_fallbackProfile;

        // ------------------------------------------------------------------ setup

        private void Awake() => CacheComponents();

        /// <summary>
        /// Binds the motor to its player and captures the capsule's current height as the unscaled height.
        /// Called by <see cref="DodgeballPlayer.Initialize"/> after it configured the Rigidbody and capsule.
        /// </summary>
        public void Initialize(DodgeballPlayer owner)
        {
            Owner = owner;
            CacheComponents();
            if (_capsule != null) SetBaseCapsule(_capsule.height, _capsule.radius);
            if (_rb != null) _prevYaw = _rb.rotation.eulerAngles.y;
            ResetMotionState();
        }

        /// <summary>
        /// Sets the unscaled capsule dimensions (feet at the transform origin, centre at height / 2) and re-applies the
        /// current height scale.
        /// </summary>
        public void SetBaseCapsule(float height, float radius)
        {
            CacheComponents();
            if (_capsule == null) return;
            _capsule.direction = 1; // Y axis
            _capsule.radius = Mathf.Max(0.05f, radius);
            _baseHeight = Mathf.Max(_capsule.radius * 2f + 0.01f, height);
            ApplyCapsuleHeight();
        }

        private void CacheComponents()
        {
            if (_rb == null) _rb = GetComponent<Rigidbody>();
            if (_capsule == null) _capsule = GetComponent<CapsuleCollider>();
            if (Owner == null) Owner = GetComponent<DodgeballPlayer>();
        }

        private bool EnsureBody()
        {
            if (_rb == null || _capsule == null) CacheComponents();
            return _rb != null;
        }

        // ------------------------------------------------------------------ commands

        /// <summary>Desired planar direction (world, y ignored) and magnitude 0..1 for this frame.</summary>
        public void SetMoveInput(Vector3 worldDirection, float magnitude)
        {
            worldDirection.y = 0f;
            float sqr = worldDirection.sqrMagnitude;
            if (sqr < 1e-8f || magnitude <= 1e-4f || float.IsNaN(sqr))
            {
                _moveDir = Vector3.zero;
                _moveMagnitude = 0f;
                return;
            }
            _moveDir = worldDirection / Mathf.Sqrt(sqr);
            _moveMagnitude = Mathf.Clamp01(magnitude);
        }

        public void SetMode(MovementMode mode)
        {
            // Leaving the slide regime ends the slide but keeps its momentum (normal braking takes over).
            if (IsSliding && mode != MovementMode.Slide) EndSlide();
            Mode = mode;
        }

        /// <summary>Rotates the body toward <paramref name="worldDirection"/> at Profile.turnSpeed (or instantly).</summary>
        /// <remarks>
        /// The request is valid for the current and the next frame: states call it every frame while they want to face
        /// something (aim, incoming ball); when they stop, the body turns back toward its move direction.
        /// </remarks>
        public void SetFacing(Vector3 worldDirection, bool instant = false)
        {
            worldDirection.y = 0f;
            if (worldDirection.sqrMagnitude < 1e-6f || _frozen) return;
            _facingDir = worldDirection.normalized;
            _facingFrame = Time.frameCount;

            if (!instant || !EnsureBody()) return;
            float yaw = Mathf.Atan2(_facingDir.x, _facingDir.z) * Mathf.Rad2Deg;
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            _rb.rotation = rotation;
            transform.rotation = rotation;
            _prevYaw = yaw;
        }

        /// <summary>Jumps if grounded and allowed. Returns true on success.</summary>
        public bool TryJump()
        {
            if (!EnsureBody()) return false;
            RefreshCapabilities();
            if (!CanJump) return false;

            var p = ActiveProfile;
            _jumpRequested = true;           // velocity applied at the next physics step
            IsGrounded = false;
            GroundNormal = Vector3.up;
            _groundIgnoreTimer = jumpGroundIgnoreTime;
            _jumpCooldownTimer = jumpCooldown;
            _airTime = 0f;
            _airMaxSpeed = Mathf.Max(PlanarSpeed, p.walkSpeed * SpeedMultiplier);
            RefreshCapabilities();
            Jumped?.Invoke();
            return true;
        }

        /// <summary>Starts a slide along <paramref name="direction"/>, preserving momentum. Returns true on success.</summary>
        public bool TryStartSlide(Vector3 direction)
        {
            if (!EnsureBody()) return false;
            RefreshCapabilities();
            if (!CanSlide) return false;

            var p = ActiveProfile;
            float current = CurrentBodyPlanarSpeed();
            float start = Mathf.Max(current, p.walkSpeed * SpeedMultiplier) * Mathf.Max(0f, p.slideBoost);
            return StartSlideInternal(direction, start, p.slideDuration);
        }

        /// <summary>
        /// Ability-driven slide (e.g. Specter's Precognition Dodge): starts at <paramref name="startSpeed"/> m/s for up to
        /// <paramref name="duration"/> s. Momentum is still preserved (never slower than the current planar speed).
        /// <paramref name="ignoreCooldown"/> also waives the grounded requirement and restarts a running slide.
        /// Frozen, Locked, Rooted and DodgeDisabled bodies still cannot slide.
        /// </summary>
        public bool TryStartSlide(Vector3 direction, float startSpeed, float duration, bool ignoreCooldown)
        {
            if (!EnsureBody()) return false;
            RefreshCapabilities();
            if (!ignoreCooldown && !CanSlide) return false;
            if (_frozen || _rb.isKinematic || Mode == MovementMode.Locked || HasStatus(StatusEffectType.DodgeDisabled) ||
                HasStatus(StatusEffectType.Rooted)) return false;

            float start = Mathf.Max(CurrentBodyPlanarSpeed(), Mathf.Max(0f, startSpeed));
            return StartSlideInternal(direction, start, duration > 0f ? duration : ActiveProfile.slideDuration);
        }

        public void EndSlide()
        {
            if (!IsSliding) return;
            IsSliding = false;
            SlideTimeRemaining = 0f;
            _slideSpeed = 0f;
            _slideCooldownTimer = Mathf.Max(0f, ActiveProfile.slideCooldown);
            if (Mode == MovementMode.Slide) Mode = MovementMode.Walk;
            SetCapsuleHeightScale(1f);
            RefreshCapabilities();
            SlideEnded?.Invoke();
        }

        /// <summary>Instant velocity change (m/s) - knockback, shockwaves, tackles.</summary>
        public void AddImpulse(Vector3 velocityChange)
        {
            if (!EnsureBody() || _frozen || _rb.isKinematic) return;
            if (float.IsNaN(velocityChange.x) || float.IsNaN(velocityChange.y) || float.IsNaN(velocityChange.z)) return;

            Vector3 v = _rb.GetVelocity() + velocityChange;
            _rb.SetVelocity(v);
            Velocity = v;
            PlanarVelocity = new Vector3(v.x, 0f, v.z);
            PlanarSpeed = PlanarVelocity.magnitude;

            if (velocityChange.y > 0.5f)
            {
                // Launched upward: leave the ground and keep the new momentum in the air.
                IsGrounded = false;
                GroundNormal = Vector3.up;
                _groundIgnoreTimer = Mathf.Max(_groundIgnoreTimer, jumpGroundIgnoreTime);
                _airMaxSpeed = Mathf.Max(PlanarSpeed, ActiveProfile.walkSpeed * SpeedMultiplier);
            }

            // Knockback recovery: the harder the hit, the less control for a moment.
            float loss = Mathf.Clamp01(new Vector2(velocityChange.x, velocityChange.z).magnitude / knockbackFullLossSpeed);
            _control = Mathf.Min(_control, Mathf.Lerp(1f, knockbackMinControl, loss));
        }

        /// <summary>Hard-sets the planar velocity (dashes, teleports).</summary>
        public void SetPlanarVelocity(Vector3 planarVelocity)
        {
            if (!EnsureBody() || _frozen || _rb.isKinematic) return;
            Vector3 v = _rb.GetVelocity();
            v.x = planarVelocity.x;
            v.z = planarVelocity.z;
            _rb.SetVelocity(v);
            Velocity = v;
            PlanarVelocity = new Vector3(v.x, 0f, v.z);
            PlanarSpeed = PlanarVelocity.magnitude;
            if (IsSliding) _slideSpeed = Mathf.Max(0f, Vector3.Dot(PlanarVelocity, _slideDir));
        }

        /// <summary>
        /// Relocates the body (no interpolation smear), zeroes its velocity and resets motion readouts. Keeps the rewind
        /// history - use <see cref="DodgeballPlayer.Teleport"/> for gameplay relocations that must also clear it.
        /// </summary>
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            if (!EnsureBody())
            {
                transform.SetPositionAndRotation(position, rotation);
                return;
            }

            if (IsSliding) EndSlide();

            // Keep the body upright: only the yaw of the requested rotation is used.
            float yaw = YawOf(rotation, _rb.rotation.eulerAngles.y);
            var upright = Quaternion.Euler(0f, yaw, 0f);

            _rb.position = position;
            _rb.rotation = upright;
            transform.SetPositionAndRotation(position, upright);
            if (!_rb.isKinematic)
            {
                _rb.SetVelocity(Vector3.zero);
                _rb.SetAngularVelocity(Vector3.zero);
            }

            _prevYaw = yaw;
            _facingFrame = int.MinValue;
            ResetMotionState();
        }

        /// <summary>Stops all motion and ignores input while true (frozen / ragdoll).</summary>
        /// <remarks>
        /// The body is made kinematic while frozen so other players cannot shove a frozen statue and abilities can carry it
        /// (grabs, rewinds). The previous collision detection mode is restored on release.
        /// </remarks>
        public void SetFrozen(bool frozen)
        {
            if (_frozen == frozen) return;
            _frozen = frozen;
            if (!EnsureBody()) return;

            if (frozen)
            {
                if (IsSliding) EndSlide();
                _jumpRequested = false;
                if (!_rb.isKinematic)
                {
                    _rb.SetVelocity(Vector3.zero);
                    _rb.SetAngularVelocity(Vector3.zero);
                    _restoreCollisionMode = _rb.collisionDetectionMode;
                    // Kinematic bodies only support speculative CCD: switch first to avoid engine warnings.
                    _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                    _rb.isKinematic = true;
                    _madeKinematic = true;
                }
                ClearMotionReadouts();
            }
            else
            {
                if (_madeKinematic)
                {
                    _rb.isKinematic = false;
                    _rb.collisionDetectionMode = _restoreCollisionMode;
                    _madeKinematic = false;
                }
                if (!_rb.isKinematic)
                {
                    _rb.SetVelocity(Vector3.zero);
                    _rb.SetAngularVelocity(Vector3.zero);
                }
                _prevYaw = _rb.rotation.eulerAngles.y;
                ResetMotionState();
            }
            RefreshCapabilities();
        }

        /// <summary>Adds or replaces a multiplicative speed modifier identified by <paramref name="source"/>.</summary>
        public void SetSpeedModifier(object source, float multiplier)
        {
            _speedModifiers[source ?? s_anonymousSource] = Mathf.Max(0f, multiplier);
            RecomputeSpeedModifiers();
        }

        public void RemoveSpeedModifier(object source)
        {
            if (_speedModifiers.Remove(source ?? s_anonymousSource)) RecomputeSpeedModifiers();
        }

        /// <summary>Adds or replaces a traction modifier (0..1) identified by <paramref name="source"/>. Lowest wins.</summary>
        public void SetTractionModifier(object source, float traction)
        {
            _tractionModifiers[source ?? s_anonymousSource] = Mathf.Clamp01(traction);
            RecomputeTractionModifiers();
        }

        public void RemoveTractionModifier(object source)
        {
            if (_tractionModifiers.Remove(source ?? s_anonymousSource)) RecomputeTractionModifiers();
        }

        /// <summary>Removes every speed and traction modifier (debug / full resets).</summary>
        public void ClearModifiers()
        {
            _speedModifiers.Clear();
            _tractionModifiers.Clear();
            RecomputeSpeedModifiers();
            RecomputeTractionModifiers();
        }

        /// <summary>Confines the player's planar position to <paramref name="bounds"/> (court half or outfield strip). Null = free.</summary>
        public void SetConfinement(Bounds? bounds)
        {
            _hasConfinement = bounds.HasValue;
            if (bounds.HasValue) _confinement = bounds.Value;
        }

        /// <summary>Temporarily scales the capsule height (slides). 1 = default.</summary>
        public void SetCapsuleHeightScale(float scale)
        {
            _heightScale = Mathf.Clamp(scale, 0.2f, 1.5f);
            ApplyCapsuleHeight();
        }

        // ------------------------------------------------------------------ ticking (called by DodgeballPlayer)

        /// <summary>
        /// Per-frame refresh (scaled time): mirrors the real Rigidbody velocity (after collisions) into the readouts used
        /// by animation/AI and refreshes <see cref="CanJump"/>/<see cref="CanSlide"/> for the state machine.
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (!EnsureBody()) return;
            SpeedMultiplier = ComputeSpeedMultiplier();
            Traction = ComputeTraction();

            if (_frozen || _rb.isKinematic)
            {
                ClearMotionReadouts();
            }
            else
            {
                Vector3 v = _rb.GetVelocity();
                Velocity = v;
                PlanarVelocity = new Vector3(v.x, 0f, v.z);
                PlanarSpeed = PlanarVelocity.magnitude;
            }

            RefreshCapabilities();
        }

        public void FixedTick(float fixedDeltaTime)
        {
            if (!EnsureBody() || fixedDeltaTime <= 0f) return;
            float dt = fixedDeltaTime;
            var p = ActiveProfile;

            UpdateTimers(dt);
            SpeedMultiplier = ComputeSpeedMultiplier();
            Traction = ComputeTraction();

            if (_frozen || _rb.isKinematic)
            {
                // Somebody else (freeze, grab, rewind playback, ragdoll) owns the body: no input, no gravity.
                ClearMotionReadouts();
                _prevYaw = _rb.rotation.eulerAngles.y;
                RefreshCapabilities();
                return;
            }

            Vector3 velocity = _rb.GetVelocity();
            bool wasGrounded = IsGrounded;

            // ---------------------------------------------------------- ground
            bool grounded = ProbeGround(wasGrounded, velocity.y, out Vector3 groundNormal, out float groundGap);
            if (_groundIgnoreTimer > 0f || _jumpRequested) grounded = false;

            IsGrounded = grounded;
            GroundNormal = grounded ? groundNormal : Vector3.up;

            if (grounded)
            {
                _airTime = 0f;
                // Impact speed = the vertical speed commanded on the last airborne step (the collision already zeroed
                // the body's own vertical velocity by now).
                if (!wasGrounded) Landed?.Invoke(Mathf.Max(0f, -_lastAirVerticalSpeed));
            }
            else
            {
                if (wasGrounded) _airMaxSpeed = Mathf.Max(PlanarSpeed, p.walkSpeed * SpeedMultiplier);
                _airTime += dt;
            }

            // ---------------------------------------------------------- planar motion
            Vector3 planar = new Vector3(velocity.x, 0f, velocity.z);
            CurrentMaxSpeed = ComputeModeMaxSpeed(p);

            if (IsSliding) planar = SlideStep(planar, grounded, dt, p);
            else planar = LocomotionStep(planar, grounded, dt, p);

            // ---------------------------------------------------------- vertical motion
            Vector3 finalVelocity;
            float snapSpeed = 0f;
            if (_jumpRequested)
            {
                _jumpRequested = false;
                float g = Mathf.Max(0.1f, Mathf.Abs(Physics.gravity.y) * Mathf.Max(0f, p.gravityMultiplier));
                float jumpSpeed = Mathf.Sqrt(2f * g * Mathf.Max(0f, p.jumpHeight)); // v = sqrt(2 g h)
                finalVelocity = planar + Vector3.up * jumpSpeed;
            }
            else if (grounded)
            {
                // Follow the slope: project the planar motion onto the ground plane, preserving its speed.
                Vector3 move = planar;
                if (GroundNormal.y < 0.999f && planar.sqrMagnitude > 1e-6f)
                {
                    Vector3 onPlane = Vector3.ProjectOnPlane(planar, GroundNormal);
                    if (onPlane.sqrMagnitude > 1e-8f) move = onPlane.normalized * planar.magnitude;
                }

                // Stick to the ground when hovering above it (stepping down, slope crests). The resting contact offset is
                // not a gap: pushing into the floor every step would only create friction and jitter.
                float snapGap = groundGap - Physics.defaultContactOffset;
                if (snapGap > 0.01f) snapSpeed = Mathf.Min(snapGap / dt, maxSnapSpeed);
                finalVelocity = move + Vector3.down * snapSpeed;
            }
            else
            {
                float vertical = velocity.y + Physics.gravity.y * Mathf.Max(0f, p.gravityMultiplier) * dt;
                vertical = Mathf.Max(vertical, -maxFallSpeed);
                finalVelocity = planar + Vector3.up * vertical;
            }

            _lastAirVerticalSpeed = grounded ? 0f : finalVelocity.y;

            // ---------------------------------------------------------- confinement
            finalVelocity = ApplyConfinement(finalVelocity, dt);

            _rb.SetVelocity(finalVelocity);

            // ---------------------------------------------------------- facing + lean readouts
            UpdateFacing(grounded, dt, p);

            Vector3 finalPlanar = new Vector3(finalVelocity.x, 0f, finalVelocity.z);
            Vector3 accel = (finalPlanar - _prevPlanarVelocity) / dt;
            PlanarAcceleration = Vector3.ClampMagnitude(accel, maxReportedAcceleration);
            _prevPlanarVelocity = finalPlanar;

            Velocity = finalVelocity + Vector3.up * snapSpeed;
            PlanarVelocity = finalPlanar;
            PlanarSpeed = finalPlanar.magnitude;
            if (IsSliding) CurrentMaxSpeed = PlanarSpeed;

            RefreshCapabilities();
        }

        // ------------------------------------------------------------------ locomotion

        private Vector3 LocomotionStep(Vector3 planar, bool grounded, float dt, MotorProfile p)
        {
            bool voluntary = Mode != MovementMode.Locked && !HasStatus(StatusEffectType.Rooted);
            float targetSpeed = voluntary ? CurrentMaxSpeed * _moveMagnitude : 0f;
            Vector3 targetDir = _moveDir;

            if (grounded)
            {
                float grip = Mathf.Max(minTraction, Traction) * _control;
                float decelRate = Mathf.Max(0f, p.deceleration) * grip;

                if (targetSpeed > 0.01f)
                {
                    float along = Vector3.Dot(planar, targetDir);
                    Vector3 lateral = planar - targetDir * along;

                    float maxSpeed = Mathf.Max(0.01f, CurrentMaxSpeed);
                    float accelRate = Mathf.Max(0f, p.acceleration) * EvaluateAccelerationCurve(p, Mathf.Max(0f, along) / maxSpeed) * grip;

                    // Below target: accelerate along the smooth curve. Above target (e.g. sprint released, slowed): brake.
                    // Reversing: braking through zero uses the stronger of the two rates.
                    float rate = along < targetSpeed ? accelRate : decelRate;
                    if (along < 0f) rate = Mathf.Max(accelRate, decelRate);
                    along = Mathf.MoveTowards(along, targetSpeed, rate * dt);

                    // Kill sideways drift: tight turns with good traction, wide drifting arcs on ice.
                    lateral = Vector3.MoveTowards(lateral, Vector3.zero, decelRate * lateralGrip * dt);
                    return targetDir * along + lateral;
                }

                return Vector3.MoveTowards(planar, Vector3.zero, decelRate * dt);
            }

            // Airborne: limited steering, momentum preserved (no braking above the target, no air drag at these speeds).
            if (targetSpeed > 0.01f)
            {
                float airRate = Mathf.Max(0f, p.acceleration) * Mathf.Clamp01(p.airControl) * _control;
                float along = Vector3.Dot(planar, targetDir);
                Vector3 lateral = planar - targetDir * along;
                if (along < targetSpeed) along = Mathf.MoveTowards(along, targetSpeed, airRate * dt);
                lateral = Vector3.MoveTowards(lateral, Vector3.zero, airRate * 0.5f * dt);
                return targetDir * along + lateral;
            }

            return planar;
        }

        private Vector3 SlideStep(Vector3 planar, bool grounded, float dt, MotorProfile p)
        {
            // Momentum preserved: the slide keeps whatever speed survived collisions along its direction.
            // Hitting a wall head-on kills the slide; glancing contacts only bleed off the blocked component.
            float speed = Mathf.Max(0f, Vector3.Dot(planar, _slideDir));

            // Slight steering with the stick (less on ice).
            if (_moveMagnitude > 0.1f && Mode != MovementMode.Locked)
            {
                float maxRadians = slideSteerRate * Mathf.Deg2Rad * Mathf.Max(minTraction, Traction) * dt;
                _slideDir = Vector3.RotateTowards(_slideDir, _moveDir, maxRadians, 0f);
                _slideDir.y = 0f;
                _slideDir.Normalize();
            }

            // Friction loss (ice slides go further). No friction while airborne.
            if (grounded)
            {
                float friction = Mathf.Max(0f, p.slideFriction) * Mathf.Lerp(slideIceFrictionScale, 1f, Traction);
                speed = Mathf.Max(0f, speed - friction * dt);
            }

            _slideSpeed = speed;
            SlideTimeRemaining -= dt;

            Vector3 result = _slideDir * speed;
            if (SlideTimeRemaining <= 0f || speed < slideMinSpeed || (!grounded && _airTime > slideMaxAirTime))
            {
                EndSlide();
            }
            return result;
        }

        private bool StartSlideInternal(Vector3 direction, float startSpeed, float duration)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-6f)
            {
                Vector3 v = _rb.GetVelocity();
                direction = new Vector3(v.x, 0f, v.z);
                if (direction.sqrMagnitude < 1e-4f) direction = transform.forward;
                direction.y = 0f;
                if (direction.sqrMagnitude < 1e-6f) direction = Vector3.forward;
            }

            _slideDir = direction.normalized;
            _slideSpeed = Mathf.Max(0f, startSpeed);
            IsSliding = true;
            SlideTimeRemaining = Mathf.Max(0.05f, duration);
            Mode = MovementMode.Slide;

            Vector3 vel = _rb.GetVelocity();
            Vector3 planar = _slideDir * _slideSpeed;
            vel.x = planar.x;
            vel.z = planar.z;
            _rb.SetVelocity(vel);
            Velocity = vel;
            PlanarVelocity = planar;
            PlanarSpeed = _slideSpeed;
            CurrentMaxSpeed = _slideSpeed;

            SetCapsuleHeightScale(ActiveProfile.slideHeightMultiplier);
            RefreshCapabilities();
            SlideStarted?.Invoke();
            return true;
        }

        private float ComputeModeMaxSpeed(MotorProfile p)
        {
            float mult = SpeedMultiplier;
            switch (Mode)
            {
                case MovementMode.Walk: return p.walkSpeed * mult;
                case MovementMode.Sprint: return p.sprintSpeed * mult;
                case MovementMode.Charging: return p.walkSpeed * Mathf.Clamp01(p.chargingSpeedMultiplier) * mult;
                case MovementMode.Catching: return p.walkSpeed * Mathf.Clamp01(p.catchingSpeedMultiplier) * mult;
                case MovementMode.Airborne: return Mathf.Max(_airMaxSpeed, p.walkSpeed * mult);
                case MovementMode.Slide: return IsSliding ? _slideSpeed : p.walkSpeed * mult;
                case MovementMode.Locked: return 0f;
                default: return p.walkSpeed * mult;
            }
        }

        private static float EvaluateAccelerationCurve(MotorProfile p, float normalizedSpeed)
        {
            var curve = p.accelerationCurve;
            if (curve == null || curve.length == 0) return 1f;
            return Mathf.Max(0.05f, curve.Evaluate(Mathf.Clamp01(normalizedSpeed)));
        }

        // ------------------------------------------------------------------ facing

        private void UpdateFacing(bool grounded, float dt, MotorProfile p)
        {
            float currentYaw = _rb.rotation.eulerAngles.y;
            float rate = Mathf.Max(0f, p.turnSpeed) * (grounded ? 1f : airTurnMultiplier);

            // Priority: explicit facing request (aim) > slide direction > move direction.
            Vector3 desired = Vector3.zero;
            bool explicitFacing = _facingFrame >= Time.frameCount - 1 && _facingDir.sqrMagnitude > 1e-6f;
            if (explicitFacing) desired = _facingDir;
            else if (IsSliding) desired = _slideDir;
            else if (Mode != MovementMode.Locked && _moveMagnitude > 0.1f) desired = _moveDir;

            float newYaw = currentYaw;
            if (desired.sqrMagnitude > 1e-6f)
            {
                float targetYaw = Mathf.Atan2(desired.x, desired.z) * Mathf.Rad2Deg;
                newYaw = Mathf.MoveTowardsAngle(currentYaw, targetYaw, rate * dt);
                if (Mathf.Abs(Mathf.DeltaAngle(currentYaw, newYaw)) > 1e-4f)
                    _rb.MoveRotation(Quaternion.Euler(0f, newYaw, 0f));
            }

            YawRate = Mathf.DeltaAngle(_prevYaw, newYaw) / dt;
            _prevYaw = newYaw;
        }

        // ------------------------------------------------------------------ ground probe

        private bool ProbeGround(bool wasGrounded, float verticalSpeed, out Vector3 normal, out float gap)
        {
            normal = Vector3.up;
            gap = float.PositiveInfinity;
            if (_capsule == null) return false;

            float radius = Mathf.Max(0.05f, _capsule.radius * groundProbeRadiusScale);
            Vector3 feet = _rb.position + _capsule.center - Vector3.up * (_capsule.height * 0.5f);
            Vector3 origin = feet + Vector3.up * (radius + groundProbeLift);
            float allowedGap = wasGrounded ? groundSnapDistance : groundTolerance;
            float reach = groundProbeLift + allowedGap;

            int count = Physics.SphereCastNonAlloc(origin, radius, Vector3.down, s_groundHits, reach, GameLayers.GroundMask,
                QueryTriggerInteraction.Ignore);

            float minNormalY = Mathf.Cos(maxSlopeAngle * Mathf.Deg2Rad);
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = s_groundHits[i];
                Collider col = hit.collider;
                if (col == null || IsOwnCollider(col)) continue;

                if (hit.distance <= 0f && hit.point == Vector3.zero)
                {
                    // The probe started inside geometry: the lower body is penetrating ground (physics will depenetrate).
                    gap = 0f;
                    normal = Vector3.up;
                    found = true;
                    continue;
                }

                if (hit.normal.y < minNormalY) continue; // too steep: a wall, not ground
                float g = hit.distance - groundProbeLift;
                if (g < gap)
                {
                    gap = g;
                    normal = hit.normal;
                    found = true;
                }
            }

            if (!found || gap > allowedGap) return false;
            // A body flying upward (knockback, jump) cannot land on its way up.
            if (!wasGrounded && verticalSpeed > maxLandingRiseSpeed) return false;
            return true;
        }

        private bool IsOwnCollider(Collider col)
        {
            if (col.attachedRigidbody == _rb) return true;
            return col.transform.IsChildOf(transform);
        }

        // ------------------------------------------------------------------ confinement

        private Vector3 ApplyConfinement(Vector3 velocity, float dt)
        {
            if (!_hasConfinement) return velocity;

            float inset = insetConfinementByRadius && _capsule != null ? _capsule.radius : 0f;
            Vector3 pos = _rb.position;
            bool corrected = false;
            ClampAxis(ref pos.x, ref velocity.x, _confinement.min.x + inset, _confinement.max.x - inset, dt, ref corrected);
            ClampAxis(ref pos.z, ref velocity.z, _confinement.min.z + inset, _confinement.max.z - inset, dt, ref corrected);
            if (corrected) _rb.position = pos;
            return velocity;
        }

        /// <summary>Keeps <paramref name="p"/> in [min, max]: snaps it back if outside and removes outward velocity.</summary>
        private static void ClampAxis(ref float p, ref float v, float min, float max, float dt, ref bool corrected)
        {
            if (min > max)
            {
                float mid = (min + max) * 0.5f;
                min = max = mid;
            }

            if (p < min)
            {
                p = min;
                corrected = true;
                if (v < 0f) v = 0f;
            }
            else if (p > max)
            {
                p = max;
                corrected = true;
                if (v > 0f) v = 0f;
            }
            else
            {
                // Predictive: never step past the boundary this frame.
                float next = p + v * dt;
                if (next < min) v = (min - p) / dt;
                else if (next > max) v = (max - p) / dt;
            }
        }

        // ------------------------------------------------------------------ helpers

        private void UpdateTimers(float dt)
        {
            if (_jumpCooldownTimer > 0f) _jumpCooldownTimer = Mathf.Max(0f, _jumpCooldownTimer - dt);
            if (_groundIgnoreTimer > 0f) _groundIgnoreTimer = Mathf.Max(0f, _groundIgnoreTimer - dt);
            if (_slideCooldownTimer > 0f && !IsSliding) _slideCooldownTimer = Mathf.Max(0f, _slideCooldownTimer - dt);
            if (_control < 1f) _control = Mathf.MoveTowards(_control, 1f, dt / knockbackRecoveryTime);
        }

        private void RefreshCapabilities()
        {
            bool locked = _frozen || (_rb != null && _rb.isKinematic) || Mode == MovementMode.Locked;
            bool dodgeBlocked = HasStatus(StatusEffectType.DodgeDisabled) || HasStatus(StatusEffectType.Rooted);
            bool free = !locked && !dodgeBlocked && IsGrounded && !IsSliding && !_jumpRequested;
            CanJump = free && _jumpCooldownTimer <= 0f;
            CanSlide = free && _slideCooldownTimer <= 0f;
        }

        private bool HasStatus(StatusEffectType type)
        {
            var status = Owner != null ? Owner.Status : null;
            return status != null && status.Has(type);
        }

        private float ComputeSpeedMultiplier()
        {
            float statusMult = 1f;
            var status = Owner != null ? Owner.Status : null;
            if (status != null) statusMult = status.MoveSpeedMultiplier;
            return Mathf.Max(0f, _speedModifierProduct * statusMult);
        }

        private float ComputeTraction() => Mathf.Clamp(_tractionModifierMin, 0f, 1f);

        private void RecomputeSpeedModifiers()
        {
            float product = 1f;
            foreach (var kv in _speedModifiers) product *= kv.Value;
            _speedModifierProduct = product;
            SpeedMultiplier = ComputeSpeedMultiplier();
        }

        private void RecomputeTractionModifiers()
        {
            float min = 1f;
            foreach (var kv in _tractionModifiers) min = Mathf.Min(min, kv.Value);
            _tractionModifierMin = min;
            Traction = ComputeTraction();
        }

        /// <summary>Yaw (deg) of <paramref name="rotation"/>; <paramref name="fallback"/> for an invalid (zero) quaternion.</summary>
        private static float YawOf(Quaternion rotation, float fallback)
        {
            float norm = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            if (norm < 1e-6f || float.IsNaN(norm)) return fallback;
            Vector3 f = rotation * Vector3.forward;
            f.y = 0f;
            return f.sqrMagnitude < 1e-8f ? fallback : Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        private float CurrentBodyPlanarSpeed()
        {
            if (_rb == null || _rb.isKinematic) return 0f;
            Vector3 v = _rb.GetVelocity();
            return new Vector2(v.x, v.z).magnitude;
        }

        private void ApplyCapsuleHeight()
        {
            if (_capsule == null) return;
            float height = Mathf.Max(_capsule.radius * 2f + 0.01f, _baseHeight * _heightScale);
            _capsule.height = height;
            _capsule.center = new Vector3(0f, height * 0.5f, 0f); // feet stay at the transform origin
        }

        private void ClearMotionReadouts()
        {
            Velocity = Vector3.zero;
            PlanarVelocity = Vector3.zero;
            PlanarSpeed = 0f;
            YawRate = 0f;
            PlanarAcceleration = Vector3.zero;
            CurrentMaxSpeed = 0f;
            _prevPlanarVelocity = Vector3.zero;
        }

        private void ResetMotionState()
        {
            ClearMotionReadouts();
            _jumpRequested = false;
            _groundIgnoreTimer = 0f;
            _lastAirVerticalSpeed = 0f;
            _airTime = 0f;
            _airMaxSpeed = 0f;
            _control = 1f;
        }
    }
}
