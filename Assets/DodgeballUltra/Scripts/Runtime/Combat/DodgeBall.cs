using System;
using System.Collections.Generic;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.Juice;
using DodgeballUltra.Player;
using UnityEngine;
using UnityEngine.Rendering;

namespace DodgeballUltra.Combat
{
    /// <summary>
    /// CONTRACT (kernel) - a physical foam dodgeball.
    /// <para>
    /// Live balls resolve contacts with a manual sphere sweep each FixedUpdate (layers: <see cref="GameLayers.BallSweepMask"/>)
    /// instead of relying on OnCollisionEnter, so 220 km/h balls never tunnel and catch timing is exact:
    /// <code>
    /// FixedUpdate (Live):
    ///   apply BallManager field effects (magnet, stasis) -> payload.OnTick
    ///   custom gravity (GravityScale) ; sweep from position along velocity*dt (radius = Radius)
    ///   first hit:
    ///     IBallHittable            -> OnBallHit response (Block / Absorb / Handled / PassThrough)
    ///     DodgeballPlayer (enemy)  -> victim.Combat.TryResolveCatch(ball, point, impactTime)  => caught?  (BallCaughtEvent)
    ///                                 else HitResolver.ResolveHit(...)                        (BallHitPlayerEvent)
    ///                                 Pierce balls continue; others deflect and become Free
    ///     DodgeballPlayer (mate)   -> passes are received (Combat.ReceivePass); other balls pass through teammates
    ///     court surface            -> reflect with restitution, BallBouncedEvent; floor => RallyCount = 0 and Free
    /// </code>
    /// Free balls are plain physics bodies (Rigidbody, CCD, bouncy material). Held balls are kinematic and follow the hand socket.
    /// </para>
    /// <para>
    /// Implementation notes:
    /// <list type="bullet">
    /// <item>While Live the body is kinematic and moved with <c>MovePosition</c> (interpolated) to the position resolved by
    /// the sweep; <see cref="FlightPosition"/> is the authoritative, un-interpolated position.</item>
    /// <item>Besides physical colliders the sweep also tests two "virtual" volumes of enemies: the catch zone of players whose
    /// catch stance is armed (hands reach for balls that would narrowly miss the body, and the catch is judged the moment
    /// the ball enters the hands' reach) and the ball an enemy is holding (classic block rule, see
    /// <see cref="BallPhysicsTuning.heldBallBlocks"/>).</item>
    /// <item>Every callback into other code (payloads, hittables, catches, hits) may change this ball's state; the sweep
    /// re-checks the state and a motion version counter after each one and stops as soon as someone else took over.</item>
    /// <item><see cref="IBallPayload.OnEnded"/> runs exactly once per launch, right after the ball left the Live state
    /// (so <see cref="State"/> already shows where it went: Free, Held, Stasis or Despawned).</item>
    /// </list>
    /// </para>
    /// <para>Owner module: Combat.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
    [DefaultExecutionOrder(100)] // LateUpdate after the character's IK/lean so a held ball sits in the final hand pose.
    public sealed class DodgeBall : MonoBehaviour
    {
        public int BallId { get; private set; }
        public BallState State { get; private set; } = BallState.Free;
        public BallStyle Style { get; private set; } = BallStyle.Standard;

        /// <summary>Player holding the ball (Held state) or null.</summary>
        public DodgeballPlayer Holder { get; private set; }

        /// <summary>Last player that threw this ball (kept after it lands, used for credit and team checks).</summary>
        public DodgeballPlayer LastThrower { get; private set; }

        public TeamId ThrowerTeam => LastThrower != null ? LastThrower.Team : TeamId.None;

        /// <summary>Consecutive catch+rethrow count without touching the floor (Rally Boost).</summary>
        public int RallyCount { get; private set; }

        public bool IsLive => State == BallState.Live;
        public bool IsFree => State == BallState.Free;

        /// <summary>True for temporary ability projectiles (meteor, beam, glue, freeze, turret shots): recycled after use, never picked up.</summary>
        public bool IsAbilityBall { get; private set; }

        public bool IsPass { get; private set; }
        public bool Unblockable { get; private set; }
        public bool Pierce { get; private set; }
        public float GravityScale { get; private set; } = 1f;
        public IBallPayload Payload { get; private set; }
        public DodgeballPlayer LockedTarget { get; private set; }

        /// <summary>Time.time when last launched.</summary>
        public float LaunchTime { get; private set; }
        public Vector3 LaunchOrigin { get; private set; }

        public Vector3 Velocity { get; private set; }
        public float Speed => Velocity.magnitude;
        public float SpeedKmh => Speed * Core.GameConstants.MsToKmh;

        /// <summary>Current collision radius (m). Base radius * RadiusMultiplier of the last throw.</summary>
        public float Radius { get; private set; } = Core.GameConstants.BallRadius;
        public float BaseRadius => Core.GameConstants.BallRadius;

        public Rigidbody Body { get; private set; }
        public SphereCollider SphereCollider { get; private set; }

        /// <summary>Child transform that holds the mesh; squash &amp; stretch scales this, never the physics root.</summary>
        public Transform VisualRoot { get; private set; }

        public TimeRewindRecorder Rewind { get; private set; }

        /// <summary>(ball, previous, current)</summary>
        public event Action<DodgeBall, BallState, BallState> StateChanged;

        // ------------------------------------------------------------------ additional public state

        /// <summary>
        /// Authoritative physics position (not interpolated): the swept flight position while Live/Stasis, the rigidbody
        /// position while Free and the hand position while Held. Use it for predictions and rules; use transform.position
        /// for visuals.
        /// </summary>
        public Vector3 FlightPosition
        {
            get
            {
                switch (State)
                {
                    case BallState.Live:
                    case BallState.Stasis:
                        return _simPosition;
                    case BallState.Free:
                        return Body != null ? Body.position : transform.position;
                    default:
                        return transform.position;
                }
            }
        }

        /// <summary>True while an ability ball dissolves before returning to the pool.</summary>
        public bool IsFading => _fadeRemaining >= 0f;

        /// <summary>True while an ability ball sits in the BallManager's pool (inactive).</summary>
        public bool IsPooled { get; private set; }

        /// <summary>Visual angular velocity (rad/s) of the current throw.</summary>
        public Vector3 Spin => _spin;

        /// <summary>Seconds left in Stasis / Despawned (0 otherwise).</summary>
        public float StateTimeRemaining => State == BallState.Stasis || State == BallState.Despawned ? Mathf.Max(0f, _stateTimer) : 0f;

        // ------------------------------------------------------------------ internals

        private enum ContactKind
        {
            Physical = 0,   // a real collider found by the sphere sweep
            CatchZone,      // an armed enemy's catch reach (sphere around the chest)
            HeldBallBlock,  // the ball an enemy is holding
        }

        private enum ContactResult
        {
            Continue = 0,   // keep sweeping through (pass-through, teammates, pierce)
            Stop,           // the ball's motion for this step was resolved (bounce, catch, hit, block...)
        }

        private struct SweepContact
        {
            public ContactKind Kind;
            public float Distance;          // along the sweep (m)
            public Vector3 Centre;          // ball centre at the moment of contact
            public Vector3 Point;           // contact point on the other object
            public Vector3 Normal;          // surface normal pointing toward the ball
            public RaycastHit Hit;          // physical contacts only (overlaps patched with a real point/normal)
            public DodgeballPlayer Player;  // virtual contacts only
        }

        private const int SweepBufferSize = 16;
        private const int ContactBufferSize = 24;
        private const float SurfaceSkin = 0.004f;      // distance kept from surfaces after a bounce (m)
        private const float MinSweepDistance = 1e-6f;

        // Shared by every ball: only used inside GatherContacts, before any callback can run.
        private static readonly RaycastHit[] s_sweepHits = new RaycastHit[SweepBufferSize];

        private readonly SweepContact[] _contacts = new SweepContact[ContactBufferSize];
        private readonly List<DodgeballPlayer> _hitPlayers = new List<DodgeballPlayer>(4);
        private readonly List<Collider> _ignoredColliders = new List<Collider>(4);

        private bool _initialized;
        private Renderer[] _renderers = Array.Empty<Renderer>();
        private Material[][] _prefabMaterials;
        private bool _usesPrefabVisual;
        private SquashStretch _squash;
        private BallTrail _trail;
        private Transform _socket;
        private bool _visible = true;

        private Vector3 _simPosition;
        private Quaternion _simRotation = Quaternion.identity;
        private Vector3 _spin;
        private bool _flightActive;          // a launch whose payload has not been ended yet
        private int _motionVersion;          // bumped by every external relocation/state change of the flight
        private DodgeballPlayer _dangerTarget;

        private float _stateTimer;           // Stasis / Despawned countdown (scaled seconds)
        private float _stasisTime;
        private Vector3 _stasisAnchor;
        private Vector3 _respawnPosition;

        private float _fadeRemaining = -1f;  // ability ball dissolve countdown (-1 = not fading)
        private bool _awaitingLaunch;        // ability ball spawned but not launched yet
        private float _awaitingLaunchTimer;
        private float _visualRadiusScale = 1f;
        private float _lastBounceEventTime = float.NegativeInfinity;

        /// <summary>BallManager bookkeeping: seconds this free ball has been out of every player's reach.</summary>
        internal float UnreachableTime;

        /// <summary>BallManager bookkeeping: an unreachable ball was already nudged back toward play.</summary>
        internal bool UnreachableNudged;

        private static BallPhysicsTuning Tuning
        {
            get
            {
                var manager = BallManager.Instance;
                return manager != null ? manager.Tuning : BallPhysicsTuning.Default;
            }
        }

        // ------------------------------------------------------------------ Unity lifecycle

        private void Awake() => CacheComponents();

        private void Start()
        {
            if (_initialized) return;
            // Hand-placed in a scene (not spawned by the BallManager): become a match ball.
            Initialize(BallManager.AllocateBallId(), false, Style);
            ResetTo(transform.position);
            var manager = BallManager.Instance;
            if (manager != null) manager.AdoptBall(this);
        }

        private void OnDestroy()
        {
            var manager = BallManager.Instance;
            if (manager != null) manager.NotifyBallDestroyed(this);
        }

        private void Update()
        {
            if (!_initialized) return;
            float dt = Time.deltaTime;

            if (_awaitingLaunch)
            {
                _awaitingLaunchTimer -= dt;
                if (_awaitingLaunchTimer <= 0f)
                {
                    // Spawned for an ability that never launched it (exception, cancelled cast): give it back.
                    _awaitingLaunch = false;
                    RecycleSelf();
                    return;
                }
            }

            if (_fadeRemaining >= 0f)
            {
                _fadeRemaining -= dt;
                if (_fadeRemaining <= 0f)
                {
                    _fadeRemaining = -1f;
                    RecycleSelf();
                }
            }
        }

        private void FixedUpdate()
        {
            if (!_initialized) return;
            float dt = Time.fixedDeltaTime;
            switch (State)
            {
                case BallState.Live:
                    StepLive(dt);
                    break;
                case BallState.Free:
                    if (Body != null && !Body.isKinematic) Velocity = Body.GetVelocity();
                    break;
                case BallState.Held:
                    // The holder was destroyed (despawned player, scene unload): drop the ball where it is.
                    if (Holder == null) MakeFree(Vector3.zero, false);
                    break;
                case BallState.Stasis:
                    StepStasis(dt);
                    break;
                case BallState.Despawned:
                    StepDespawned(dt);
                    break;
            }
        }

        private void LateUpdate()
        {
            if (!_initialized) return;
            if (State == BallState.Held) FollowHolder();

            UpdateVisualScale(Time.deltaTime);

            if (_squash != null)
                _squash.SetVelocity(State == BallState.Live || State == BallState.Free ? Velocity : Vector3.zero);
            if (_trail != null)
                _trail.Tick(State == BallState.Live ? Velocity.magnitude : 0f, Radius);
        }

        /// <summary>
        /// Free balls are ordinary rigidbodies: their contacts come from PhysX. Touching the floor resets the rally
        /// (spec: the boost lasts "until the ball hits the floor") and audible bounces publish <see cref="BallBouncedEvent"/>.
        /// </summary>
        private void OnCollisionEnter(Collision collision)
        {
            if (!_initialized || State != BallState.Free || collision.contactCount == 0) return;
            var other = collision.collider;
            if (other == null) return;

            var tuning = Tuning;
            var contact = collision.GetContact(0);
            Vector3 normal = contact.normal; // points from the other collider toward this ball
            bool isBall = other.gameObject.layer == GameLayers.Ball;
            bool isFloor = !isBall && normal.y >= tuning.floorNormalThreshold;
            if (isFloor) RallyCount = 0;

            float impactSpeed = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, normal));
            if (impactSpeed < tuning.minBounceEventSpeed) return;
            float now = Time.time;
            if (now - _lastBounceEventTime < tuning.minBounceEventInterval) return;
            _lastBounceEventTime = now;

            SquashOnImpact(normal, impactSpeed);
            GameEvents.Publish(new BallBouncedEvent
            {
                Ball = this,
                Point = contact.point,
                Normal = normal,
                ImpactSpeed = impactSpeed,
                HitFloor = isFloor,
                RallyReset = isFloor,
                EndedLive = false,
            });
        }

        // ------------------------------------------------------------------ contract API

        /// <summary>Called once by BallManager after instantiation: builds visuals (realistic PBR foam ball), physics, recorder.</summary>
        public void Initialize(int ballId, bool isAbilityBall, BallStyle style)
        {
            BallId = ballId;
            IsAbilityBall = isAbilityBall;
            CacheComponents();

            if (!_initialized)
            {
                _initialized = true;
                gameObject.layer = GameLayers.Ball;
                ConfigurePhysicsBody();
                BuildVisuals();
                BuildRewindRecorder();
                ApplyPhysicsMode(State);
                _simPosition = transform.position;
                _simRotation = transform.rotation;
            }

            SetStyle(style);
        }

        /// <summary>Puts the ball in <paramref name="holder"/>'s hand (kinematic, follows <paramref name="socket"/>).</summary>
        public void AttachTo(DodgeballPlayer holder, Transform socket)
        {
            if (holder == null) return;
            EnsureInitialized();

            var previousHolder = Holder;
            _awaitingLaunch = false;
            _fadeRemaining = -1f;
            Holder = holder;
            _socket = socket;
            Radius = BaseRadius;
            if (SphereCollider != null) SphereCollider.radius = BaseRadius;
            Velocity = Vector3.zero;
            _spin = Vector3.zero;
            _motionVersion++;

            // A conjured projectile that ends up in someone's hands is just a ball now (its effect was consumed).
            if (IsAbilityBall && Style != BallStyle.Standard) SetStyle(BallStyle.Standard);

            SetState(BallState.Held, previousHolder != holder);
            EndFlight();
            if (_trail != null) _trail.SetEmitting(false, true);
            FollowHolder();
        }

        /// <summary>Launches the ball as Live with <paramref name="velocity"/>. Sets thrower, rally, payload, flags from <paramref name="p"/>.</summary>
        public void Launch(in ThrowParams p, Vector3 velocity)
        {
            EnsureInitialized();
            if (!gameObject.activeInHierarchy)
            {
                Debug.LogWarning($"[Dodgeball Ultra] Ball {BallId} cannot be launched while inactive (pooled or disabled).", this);
                return;
            }

            // A relaunch while already live (e.g. a hittable that re-throws the ball) ends the previous throw first.
            if (_flightActive) EndFlight();

            _awaitingLaunch = false;
            _fadeRemaining = -1f;
            IsPooled = false;
            Holder = null;
            _socket = null;

            LastThrower = p.Thrower;
            RallyCount = Mathf.Max(0, p.RallyCount);
            IsPass = p.IsPass;
            Unblockable = p.Unblockable;
            Pierce = p.Pierce;
            GravityScale = Mathf.Max(0f, p.GravityScale);
            LockedTarget = p.Target;
            if (p.Style != Style) SetStyle(p.Style);

            float radiusMultiplier = p.RadiusMultiplier > 0f ? p.RadiusMultiplier : 1f;
            Radius = BaseRadius * Mathf.Max(0.1f, radiusMultiplier);
            if (SphereCollider != null) SphereCollider.radius = Radius;

            LaunchTime = Time.time;
            LaunchOrigin = p.Origin;
            _hitPlayers.Clear();
            _ignoredColliders.Clear();
            Velocity = ClampSpeed(velocity);
            _spin = ComputeLaunchSpin(Velocity);
            _simPosition = p.Origin;
            _simRotation = transform.rotation;
            _motionVersion++;

            SetState(BallState.Live);
            HardPlace(p.Origin); // no interpolation smear from wherever the hand was last physics step

            if (_trail != null) _trail.SetEmitting(true, true);
            _flightActive = true;
            Payload = p.Payload;
            if (Payload != null)
            {
                try { Payload.OnLaunched(this); }
                catch (Exception e) { Debug.LogException(e, this); }
            }
        }

        /// <summary>Makes the ball a plain Free physics ball with <paramref name="velocity"/> (drops, deflections). Calls payload.OnEnded if it was live.</summary>
        public void MakeFree(Vector3 velocity, bool resetRally)
        {
            EnsureInitialized();
            var previous = State;
            Vector3 position = previous == BallState.Live || previous == BallState.Stasis
                ? _simPosition
                : previous == BallState.Free && Body != null ? Body.position : transform.position;
            Quaternion rotation = previous == BallState.Live ? _simRotation : transform.rotation;

            _awaitingLaunch = false;
            Holder = null;
            _socket = null;
            Radius = BaseRadius;
            if (SphereCollider != null) SphereCollider.radius = BaseRadius;
            if (resetRally) RallyCount = 0;
            _motionVersion++;

            SetState(BallState.Free);
            if (previous != BallState.Free)
            {
                HardPlace(position);
                if (Body != null) Body.rotation = rotation;
                transform.rotation = rotation;
            }

            Velocity = velocity;
            if (Body != null && !Body.isKinematic)
            {
                Body.SetVelocity(velocity);
                Body.SetAngularVelocity(_spin);
                Body.WakeUp();
            }

            // State is Free before the payload hears about it (an absorbed Meteor detonates where it dropped).
            EndFlight();
            if (_trail != null) _trail.SetEmitting(false, false);

            if (IsAbilityBall && _fadeRemaining < 0f) _fadeRemaining = Tuning.abilityBallFadeTime;
        }

        /// <summary>Changes the flight velocity while Live (magnets, curves, Chrono rewinds).</summary>
        public void SetVelocity(Vector3 velocity)
        {
            switch (State)
            {
                case BallState.Live:
                    Velocity = ClampSpeed(velocity);
                    break;
                case BallState.Free:
                    Velocity = velocity;
                    if (Body != null && !Body.isKinematic) Body.SetVelocity(velocity);
                    break;
            }
        }

        /// <summary>Teleports the ball keeping its state (Houdini, Chrono rewind).</summary>
        public void TeleportTo(Vector3 position, Vector3 velocity)
        {
            EnsureInitialized();
            switch (State)
            {
                case BallState.Held:
                    return; // the hand owns a held ball; take it from the holder first (MakeFree/GiveBall)
                case BallState.Live:
                    _simPosition = position;
                    Velocity = ClampSpeed(velocity);
                    HardPlace(position);
                    if (_trail != null) _trail.SetEmitting(true, true); // no streak across the teleport
                    break;
                case BallState.Stasis:
                    _stasisAnchor = position;
                    _simPosition = position;
                    HardPlace(position);
                    break;
                case BallState.Free:
                    HardPlace(position);
                    Velocity = velocity;
                    if (Body != null && !Body.isKinematic) Body.SetVelocity(velocity);
                    break;
                default:
                    HardPlace(position);
                    break;
            }
            _motionVersion++;
        }

        /// <summary>Freezes the ball mid-air for <paramref name="duration"/> seconds (Chrono). Any player may grab it.</summary>
        public void EnterStasis(float duration)
        {
            EnsureInitialized();
            if (duration <= 0f || State == BallState.Held || State == BallState.Despawned) return;

            Vector3 position = FlightPosition;
            _stasisAnchor = position;
            _simPosition = position;
            _simRotation = transform.rotation;
            _stateTimer = duration;
            _stasisTime = 0f;
            _fadeRemaining = -1f;
            Velocity = Vector3.zero;
            _motionVersion++;

            SetState(BallState.Stasis);
            HardPlace(position);
            // The throw is over: the payload ends now (a frozen Meteor never detonates), allies may snatch the ball.
            EndFlight();
            if (_trail != null) _trail.SetEmitting(false, false);
        }

        /// <summary>Removes the ball from play for <paramref name="duration"/> s then respawns at <paramref name="respawnPosition"/> (Houdini).</summary>
        public void Despawn(float duration, Vector3 respawnPosition)
        {
            EnsureInitialized();
            if (IsAbilityBall)
            {
                // Conjured projectiles have nowhere to come back to: they simply vanish.
                RecycleSelf();
                return;
            }

            _awaitingLaunch = false;
            _fadeRemaining = -1f;
            Holder = null;
            _socket = null;
            Radius = BaseRadius;
            if (SphereCollider != null) SphereCollider.radius = BaseRadius;
            Velocity = Vector3.zero;
            _respawnPosition = respawnPosition;
            _stateTimer = Mathf.Max(0f, duration);
            _motionVersion++;

            SetState(BallState.Despawned);
            EndFlight();
            if (_trail != null) _trail.SetEmitting(false, true);

            if (duration <= 0f) ResetTo(respawnPosition);
        }

        /// <summary>Resets everything and places the ball Free at <paramref name="position"/> (round start).</summary>
        public void ResetTo(Vector3 position)
        {
            EnsureInitialized();
            if (!gameObject.activeSelf) gameObject.SetActive(true);

            // A reset is not an impact: the payload ends while the ball is still in its old state.
            EndFlight();

            _awaitingLaunch = false;
            _fadeRemaining = -1f;
            IsPooled = false;
            Holder = null;
            _socket = null;
            LastThrower = null;
            RallyCount = 0;
            Radius = BaseRadius;
            if (SphereCollider != null) SphereCollider.radius = BaseRadius;
            _visualRadiusScale = 1f;
            Velocity = Vector3.zero;
            _spin = Vector3.zero;
            _hitPlayers.Clear();
            _ignoredColliders.Clear();
            UnreachableTime = 0f;
            UnreachableNudged = false;
            if (!IsAbilityBall && Style != BallStyle.Standard) SetStyle(BallStyle.Standard);
            _motionVersion++;

            SetState(BallState.Free);

            // Random yaw so the seam/valve of the lined-up balls do not all face the same way.
            var rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            HardPlace(position);
            transform.rotation = rotation;
            _simPosition = position;
            _simRotation = rotation;
            if (Body != null)
            {
                Body.rotation = rotation;
                if (!Body.isKinematic)
                {
                    Body.SetVelocity(Vector3.zero);
                    Body.SetAngularVelocity(Vector3.zero);
                }
            }

            if (VisualRoot != null) VisualRoot.localScale = Vector3.one;
            if (_trail != null) _trail.SetEmitting(false, true);
            if (Rewind != null) Rewind.Clear();
        }

        /// <summary>Changes the look/behaviour flavour (trail, emissive tint).</summary>
        public void SetStyle(BallStyle style)
        {
            Style = style;
            if (_renderers != null && _renderers.Length > 0)
            {
                if (_usesPrefabVisual) ApplyStyleToPrefabVisual(style);
                else
                {
                    var material = BallAppearance.GetBallMaterial(style);
                    if (material != null)
                        for (int i = 0; i < _renderers.Length; i++)
                            if (_renderers[i] != null) _renderers[i].sharedMaterial = material;
                }
            }
            if (_trail != null) _trail.ApplyStyle(style);
        }

        /// <summary>True when <paramref name="player"/> is allowed to pick this ball up right now.</summary>
        public bool CanBePickedUpBy(DodgeballPlayer player)
        {
            if (player == null || !_initialized || IsPooled || !isActiveAndEnabled) return false;
            switch (State)
            {
                case BallState.Free:
                    // Conjured projectiles are never loose balls; a ball being spawned for an ability is not either.
                    return !IsAbilityBall && _fadeRemaining < 0f && !_awaitingLaunch;
                case BallState.Stasis:
                    // Chrono's stasis: anyone (ally or enemy) may snatch the frozen ball.
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Overrides the rally count (e.g. counter throws increment it).</summary>
        public void SetRallyCount(int rallyCount) => RallyCount = Mathf.Max(0, rallyCount);

        // ------------------------------------------------------------------ BallManager / combat internals

        /// <summary>BallManager: activates a pooled ability ball at <paramref name="position"/>, awaiting its launch.</summary>
        internal void PrepareAbilitySpawn(Vector3 position, BallStyle style)
        {
            EnsureInitialized();
            IsPooled = false;
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            ResetTo(position);
            SetStyle(style);
            _awaitingLaunch = true;
            _awaitingLaunchTimer = Tuning.abilityBallLaunchTimeout;
        }

        /// <summary>BallManager: hides and deactivates an ability ball for pooling (holders are notified).</summary>
        internal void ReturnToPool()
        {
            if (IsPooled) return;
            _awaitingLaunch = false;
            _fadeRemaining = -1f;
            Holder = null;
            _socket = null;
            Velocity = Vector3.zero;
            _motionVersion++;
            SetState(BallState.Despawned);
            EndFlight();
            if (_trail != null) _trail.SetEmitting(false, true);
            IsPooled = true;
            gameObject.SetActive(false);
        }

        /// <summary>BallManager: a match ball is about to be destroyed; let its holder know first.</summary>
        internal void PrepareForRemoval()
        {
            Holder = null;
            _socket = null;
            _motionVersion++;
            SetState(BallState.Despawned);
            EndFlight();
        }

        /// <summary>Combat: this throw raised a Danger Sense warning for <paramref name="target"/>; clear it when the throw ends.</summary>
        internal void TrackDangerSense(DodgeballPlayer target) => _dangerTarget = target;

        // ------------------------------------------------------------------ live flight

        private void StepLive(float dt)
        {
            int version = _motionVersion;

            // 1. Field effects (Bear's magnet, Chrono's fields...) may bend, catch or freeze the ball.
            var manager = BallManager.Instance;
            if (manager != null) manager.ApplyFieldEffects(this, dt);
            if (State != BallState.Live || version != _motionVersion) return;

            // 2. Payload behaviour (homing, trails, timers).
            var payload = Payload;
            if (payload != null)
            {
                try { payload.OnTick(this, dt); }
                catch (Exception e) { Debug.LogException(e, this); }
                if (State != BallState.Live || version != _motionVersion) return;
            }

            // 3. Integrate: semi-implicit Euler with scaled gravity and (light) quadratic air drag.
            var tuning = Tuning;
            Vector3 v = Velocity + Vector3.down * (GameConstants.Gravity * GravityScale * dt);
            float speed = v.magnitude;
            if (tuning.liveQuadraticDrag > 0f && speed > 0.01f)
            {
                float dv = tuning.liveQuadraticDrag * speed * speed * dt;
                v *= Mathf.Max(0f, 1f - dv / speed);
            }
            v = ClampSpeed(v);
            Velocity = v;

            // 4. Sweep the whole step and resolve contacts in order of distance.
            Vector3 start = _simPosition;
            Vector3 displacement = v * dt;
            float distance = displacement.magnitude;
            if (distance < MinSweepDistance)
            {
                CommitLiveMotion(start, dt);
                return;
            }
            Vector3 direction = displacement / distance;

            int count = GatherContacts(start, direction, distance, tuning);
            for (int i = 0; i < count; i++)
            {
                var contact = _contacts[i];
                var result = ProcessContact(ref contact, distance, dt, version, tuning);
                if (result == ContactResult.Stop) return;
                if (State != BallState.Live || version != _motionVersion) return;
            }

            // 5. Nothing stopped the ball: fly the full step.
            CommitLiveMotion(start + displacement, dt);
        }

        /// <summary>Collects the physical sweep hits plus the virtual catch-zone / held-ball volumes, sorted by distance.</summary>
        private int GatherContacts(Vector3 start, Vector3 direction, float distance, BallPhysicsTuning tuning)
        {
            int count = 0;
            int hits = Physics.SphereCastNonAlloc(start, Radius, direction, s_sweepHits, distance,
                GameLayers.BallSweepMask, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hits && count < ContactBufferSize; i++)
            {
                var hit = s_sweepHits[i];
                var collider = hit.collider;
                if (collider == null || collider == SphereCollider) continue;
                if (Body != null && collider.attachedRigidbody == Body) continue;
                if (IsIgnored(collider)) continue;

                if (hit.distance <= 0f && hit.point == Vector3.zero)
                {
                    // The sphere already overlapped this collider at the start of the sweep: PhysX reports no point.
                    ResolveInitialOverlap(collider, start, direction, out var point, out var normal);
                    hit.point = point;
                    hit.normal = normal;
                    hit.distance = 0f;
                }

                _contacts[count++] = new SweepContact
                {
                    Kind = ContactKind.Physical,
                    Distance = hit.distance,
                    Centre = start + direction * hit.distance,
                    Point = hit.point,
                    Normal = hit.normal.sqrMagnitude > 1e-8f ? hit.normal : -direction,
                    Hit = hit,
                };
            }

            count = AddVirtualContacts(start, direction, distance, tuning, count);
            SortContacts(count);
            return count;
        }

        /// <summary>Armed catch zones and held balls of enemies the ball is about to reach this step.</summary>
        private int AddVirtualContacts(Vector3 start, Vector3 direction, float distance, BallPhysicsTuning tuning, int count)
        {
            if (Unblockable) return count; // the beam can be neither caught nor blocked

            var players = PlayerRegistry.All;
            bool inGrace = Time.time - LaunchTime < tuning.throwerGraceTime;
            for (int i = 0; i < players.Count && count < ContactBufferSize; i++)
            {
                var player = players[i];
                if (player == null || !player.IsTargetable || !IsHostileTo(player)) continue;
                if (inGrace && player == LastThrower) continue;
                if (_hitPlayers.Contains(player)) continue;
                var combat = player.Combat;
                if (combat == null) continue;

                if (combat.IsCatchArmed)
                {
                    Vector3 centre = player.ChestPosition;
                    float reach = combat.Profile.catchRadius + Radius;
                    // Only the entry counts: a ball that was already inside the reach when the stance armed must reach
                    // the body (normal capsule contact) to be judged.
                    if ((start - centre).sqrMagnitude > reach * reach &&
                        SegmentSphereEntry(start, direction, distance, centre, reach, out float d))
                    {
                        Vector3 ballCentre = start + direction * d;
                        Vector3 n = ballCentre - centre;
                        _contacts[count++] = new SweepContact
                        {
                            Kind = ContactKind.CatchZone,
                            Distance = d,
                            Centre = ballCentre,
                            Point = ballCentre,
                            Normal = n.sqrMagnitude > 1e-8f ? n.normalized : -direction,
                            Player = player,
                        };
                        if (count >= ContactBufferSize) break;
                    }
                }

                var held = combat.HeldBall;
                if (tuning.heldBallBlocks && held != null && held != this && held.State == BallState.Held)
                {
                    Vector3 centre = held.transform.position;
                    float r = held.Radius + Radius;
                    if ((start - centre).sqrMagnitude > r * r &&
                        SegmentSphereEntry(start, direction, distance, centre, r, out float d))
                    {
                        Vector3 ballCentre = start + direction * d;
                        Vector3 n = (ballCentre - centre).normalized;
                        _contacts[count++] = new SweepContact
                        {
                            Kind = ContactKind.HeldBallBlock,
                            Distance = d,
                            Centre = ballCentre,
                            Point = centre + n * held.Radius,
                            Normal = n,
                            Player = player,
                        };
                    }
                }
            }
            return count;
        }

        private ContactResult ProcessContact(ref SweepContact contact, float sweepDistance, float dt, int version, BallPhysicsTuning tuning)
        {
            float impactTime = Time.time + Mathf.Clamp01(contact.Distance / sweepDistance) * dt;

            switch (contact.Kind)
            {
                case ContactKind.CatchZone:
                    return ProcessCatchZone(ref contact, impactTime);
                case ContactKind.HeldBallBlock:
                    return ProcessHeldBallBlock(ref contact, tuning);
            }

            var collider = contact.Hit.collider;
            if (collider == null) return ContactResult.Continue;
            int layer = collider.gameObject.layer;

            if (layer == GameLayers.Player)
            {
                var player = collider.GetComponentInParent<DodgeballPlayer>();
                return player != null ? ProcessPlayer(player, ref contact, impactTime, version, tuning) : ContactResult.Continue;
            }

            if (layer != GameLayers.Court)
            {
                var hittable = collider.GetComponentInParent<IBallHittable>();
                if (hittable != null) return ProcessHittable(hittable, ref contact, version, tuning);

                var player = collider.GetComponentInParent<DodgeballPlayer>();
                if (player != null) return ProcessPlayer(player, ref contact, impactTime, version, tuning);
            }

            return ProcessSurface(collider, ref contact, version, tuning);
        }

        private ContactResult ProcessCatchZone(ref SweepContact contact, float impactTime)
        {
            var player = contact.Player;
            if (player == null || !player.IsTargetable || player.Combat == null || _hitPlayers.Contains(player))
                return ContactResult.Continue;

            var quality = player.Combat.TryResolveCatch(this, contact.Centre, impactTime);
            // A miss at the hands (bad timing, outside the cone) is not a hit yet: the body contact decides that.
            return quality != CatchQuality.Miss ? ContactResult.Stop : ContactResult.Continue;
        }

        private ContactResult ProcessHeldBallBlock(ref SweepContact contact, BallPhysicsTuning tuning)
        {
            var player = contact.Player;
            var held = player != null && player.Combat != null ? player.Combat.HeldBall : null;
            if (held == null) return ContactResult.Continue;

            _hitPlayers.Add(player); // blocked once: the ball cannot also hit the blocker on its way down
            Vector3 n = contact.Normal;
            Vector3 v = Velocity;
            float vn = Vector3.Dot(v, n);
            Vector3 outVelocity = vn < 0f ? v - (1f + tuning.heldBallBlockRestitution) * vn * n : v * tuning.heldBallBlockRestitution;

            PlaceAt(contact.Centre + n * SurfaceSkin);
            MakeFree(outVelocity, false);
            SquashOnImpact(n, Mathf.Abs(vn));

            // The blocker feels the impact through the ball.
            if (player.Motor != null && tuning.heldBallBlockShove > 0f)
            {
                Vector3 shove = new Vector3(v.x, 0f, v.z);
                if (shove.sqrMagnitude > 1e-4f) player.Motor.AddImpulse(shove.normalized * tuning.heldBallBlockShove);
            }

            GameEvents.Publish(new BallBlockedEvent { Ball = this, Blocker = held, Point = contact.Point, Normal = n });
            return ContactResult.Stop;
        }

        private ContactResult ProcessHittable(IBallHittable hittable, ref SweepContact contact, int version, BallPhysicsTuning tuning)
        {
            var collider = contact.Hit.collider;
            BallHitResponse response;
            try { response = hittable.OnBallHit(this, in contact.Hit); }
            catch (Exception e)
            {
                Debug.LogException(e, this);
                response = BallHitResponse.PassThrough;
            }

            bool takenOver = State != BallState.Live || version != _motionVersion;
            if (response == BallHitResponse.Handled)
            {
                if (!takenOver)
                {
                    // Handled but left alone: stop at the contact and never ask this collider again this throw.
                    MarkIgnored(collider);
                    CommitLiveMotion(contact.Centre, Time.fixedDeltaTime);
                }
                return ContactResult.Stop;
            }
            if (takenOver) return ContactResult.Stop; // the hittable already caught/froze/moved the ball itself

            bool passes = response == BallHitResponse.PassThrough ||
                          (Unblockable && (response == BallHitResponse.Block || response == BallHitResponse.Absorb));
            if (passes)
            {
                MarkIgnored(collider);
                return ContactResult.Continue;
            }

            Component blocker = hittable as Component;
            if (blocker == null) blocker = collider;
            Vector3 n = contact.Normal;
            Vector3 v = Velocity;

            if (response == BallHitResponse.Block)
            {
                float vn = Vector3.Dot(v, n);
                Vector3 outVelocity = vn < 0f ? v - (1f + tuning.blockRestitution) * vn * n : v * tuning.blockRestitution;
                PlaceAt(contact.Centre + n * SurfaceSkin);
                MakeFree(outVelocity, false);
                SquashOnImpact(n, Mathf.Abs(vn));
            }
            else // Absorb: stopped dead at the contact, drops
            {
                PlaceAt(contact.Centre);
                MakeFree(v * tuning.absorbRetention, false);
            }

            GameEvents.Publish(new BallBlockedEvent { Ball = this, Blocker = blocker, Point = contact.Point, Normal = n });
            return ContactResult.Stop;
        }

        private ContactResult ProcessPlayer(DodgeballPlayer player, ref SweepContact contact, float impactTime, int version,
            BallPhysicsTuning tuning)
        {
            if (_hitPlayers.Contains(player)) return ContactResult.Continue;
            if (player == LastThrower && Time.time - LaunchTime < tuning.throwerGraceTime) return ContactResult.Continue;

            // Your own ball never hits you or your teammates.
            bool friendly = LastThrower != null && (player == LastThrower || PlayerRegistry.AreTeammates(LastThrower, player));

            if (IsPass)
            {
                if (friendly)
                {
                    var receiver = player.Combat;
                    if (player != LastThrower && receiver != null && !receiver.HasBall && player.CanAct)
                    {
                        receiver.ReceivePass(this, LastThrower);
                        return ContactResult.Stop;
                    }
                    return ContactResult.Continue;
                }

                // Enemy in the lob's path: they may intercept it with a catch; otherwise it just bounces off (no damage).
                if (!player.IsTargetable) return ContactResult.Continue;
                if (player.Combat != null)
                {
                    var quality = player.Combat.TryResolveCatch(this, contact.Point, impactTime);
                    if (quality != CatchQuality.Miss) return ContactResult.Stop;
                    if (State != BallState.Live || version != _motionVersion) return ContactResult.Stop;
                }
                _hitPlayers.Add(player);
                DeflectOffBody(ref contact, player, tuning);
                return ContactResult.Stop;
            }

            if (friendly) return ContactResult.Continue;
            if (!player.IsTargetable) return ContactResult.Continue; // outfield / mid-elimination: pass through

            // Catch window check first (the beam cannot be caught).
            if (!Unblockable && player.Combat != null)
            {
                var quality = player.Combat.TryResolveCatch(this, contact.Point, impactTime);
                if (quality != CatchQuality.Miss) return ContactResult.Stop;
                if (State != BallState.Live || version != _motionVersion) return ContactResult.Stop;
            }

            _hitPlayers.Add(player);
            var outcome = HitResolver.ResolveHit(this, player, contact.Point, contact.Normal);
            if (State != BallState.Live || version != _motionVersion) return ContactResult.Stop;

            if (outcome == HitOutcome.Ignored) return ContactResult.Continue;
            // Auto-evasion (Specter's dodge, invulnerability): the ball flies through the afterimage.
            if (outcome == HitOutcome.Negated && player.Status != null && player.Status.Has(StatusEffectType.Invulnerable))
                return ContactResult.Continue;
            if (Pierce) return ContactResult.Continue;

            DeflectOffBody(ref contact, player, tuning);
            return ContactResult.Stop;
        }

        private ContactResult ProcessSurface(Collider collider, ref SweepContact contact, int version, BallPhysicsTuning tuning)
        {
            Vector3 n = contact.Normal;
            bool isFloor = n.y >= tuning.floorNormalThreshold;
            Vector3 v = Velocity;
            float vn = Vector3.Dot(v, n);
            if (vn >= 0f) return ContactResult.Continue; // grazing, or leaving a surface it started against

            var payload = Payload;
            if (payload != null)
            {
                try { payload.OnHitSurface(this, contact.Point, n, collider, isFloor); }
                catch (Exception e) { Debug.LogException(e, this); }
                if (State != BallState.Live || version != _motionVersion) return ContactResult.Stop;
            }

            // Reflect: normal component reversed with restitution, tangential component reduced by friction.
            Vector3 normalPart = vn * n;
            Vector3 tangentPart = v - normalPart;
            float restitution = isFloor ? tuning.floorRestitution : tuning.wallRestitution;
            Vector3 outVelocity = -normalPart * restitution + tangentPart * (1f - tuning.surfaceTangentialLoss);
            float impactSpeed = -vn;

            PlaceAt(contact.Centre + n * SurfaceSkin);
            MakeFree(outVelocity, isFloor); // any surface contact ends the throw; the floor also resets the rally
            SquashOnImpact(n, impactSpeed);

            GameEvents.Publish(new BallBouncedEvent
            {
                Ball = this,
                Point = contact.Point,
                Normal = n,
                ImpactSpeed = impactSpeed,
                HitFloor = isFloor,
                RallyReset = isFloor,
                EndedLive = true,
            });
            return ContactResult.Stop;
        }

        /// <summary>A non-piercing ball glances off a body: damped reflection plus a little pop so it drops near the player.</summary>
        private void DeflectOffBody(ref SweepContact contact, DodgeballPlayer player, BallPhysicsTuning tuning)
        {
            Vector3 n = contact.Normal;
            Vector3 v = Velocity;
            float vn = Vector3.Dot(v, n);
            Vector3 normalPart = vn < 0f ? vn * n : Vector3.zero;
            Vector3 tangentPart = v - normalPart;
            Vector3 outVelocity = -normalPart * tuning.bodyRestitution
                                  + tangentPart * tuning.bodyTangentialRetention
                                  + Vector3.up * tuning.bodyDeflectLift;
            if (player != null) outVelocity += player.Velocity * 0.5f; // the moving body carries the ball a little

            PlaceAt(contact.Centre + n * SurfaceSkin);
            MakeFree(outVelocity, false);
        }

        private void CommitLiveMotion(Vector3 position, float dt)
        {
            _simPosition = position;
            if (Body != null) Body.MovePosition(position);

            if (_spin.sqrMagnitude > 1e-6f)
            {
                float angle = _spin.magnitude * Mathf.Rad2Deg * dt;
                _simRotation = Quaternion.Normalize(Quaternion.AngleAxis(angle, _spin.normalized) * _simRotation);
                if (Body != null) Body.MoveRotation(_simRotation);
            }
        }

        // ------------------------------------------------------------------ other states

        private void StepStasis(float dt)
        {
            var tuning = Tuning;
            _stasisTime += dt;
            _stateTimer -= dt;

            float bob = Mathf.Sin(_stasisTime * 2f * Mathf.PI * tuning.stasisBobFrequency) * tuning.stasisBobAmplitude;
            _simPosition = _stasisAnchor + Vector3.up * bob;
            _simRotation = Quaternion.Normalize(Quaternion.AngleAxis(tuning.stasisSpinDegreesPerSecond * dt, Vector3.up) * _simRotation);
            if (Body != null)
            {
                Body.MovePosition(_simPosition);
                Body.MoveRotation(_simRotation);
            }

            // Time is up and nobody grabbed it: gravity takes over again.
            if (_stateTimer <= 0f) MakeFree(Vector3.zero, false);
        }

        private void StepDespawned(float dt)
        {
            _stateTimer -= dt;
            if (_stateTimer > 0f) return;
            ResetTo(_respawnPosition);
        }

        // ------------------------------------------------------------------ state / physics modes

        private void SetState(BallState next, bool forceNotify = false)
        {
            var previous = State;
            State = next;
            ApplyPhysicsMode(next);
            if (previous == next && !forceNotify) return;

            var handler = StateChanged;
            if (handler == null) return;
            try { handler(this, previous, next); }
            catch (Exception e) { Debug.LogException(e, this); }
        }

        /// <summary>Rigidbody/collider/visibility configuration of each state.</summary>
        private void ApplyPhysicsMode(BallState state)
        {
            if (Body == null || SphereCollider == null) return;
            switch (state)
            {
                case BallState.Free:
                    SetVisible(true);
                    SphereCollider.enabled = true;
                    SetKinematic(false);
                    Body.detectCollisions = true;
                    Body.useGravity = true;
                    Body.interpolation = RigidbodyInterpolation.Interpolate;
                    break;

                case BallState.Held:
                    SetVisible(true);
                    SetKinematic(true);
                    Body.detectCollisions = false;
                    SphereCollider.enabled = false;
                    Body.useGravity = false;
                    Body.interpolation = RigidbodyInterpolation.None; // moved by the transform in LateUpdate
                    break;

                case BallState.Live:
                    // Kinematic + MovePosition: the sweep decides every contact; no physical pushes on other bodies.
                    SetVisible(true);
                    SetKinematic(true);
                    SphereCollider.enabled = true; // still found by ability overlap queries
                    Body.detectCollisions = false;
                    Body.useGravity = false;
                    Body.interpolation = RigidbodyInterpolation.Interpolate;
                    break;

                case BallState.Stasis:
                    // Frozen in time: an immovable obstacle loose balls bounce off.
                    SetVisible(true);
                    SetKinematic(true);
                    SphereCollider.enabled = true;
                    Body.detectCollisions = true;
                    Body.useGravity = false;
                    Body.interpolation = RigidbodyInterpolation.Interpolate;
                    break;

                case BallState.Despawned:
                    SetVisible(false);
                    SetKinematic(true);
                    Body.detectCollisions = false;
                    SphereCollider.enabled = false;
                    Body.useGravity = false;
                    Body.interpolation = RigidbodyInterpolation.None;
                    break;
            }
        }

        private void SetKinematic(bool kinematic)
        {
            if (kinematic)
            {
                if (Body.isKinematic) return;
                // Zero the velocities while still dynamic (setting them on a kinematic body is not supported), and switch
                // to speculative CCD first: kinematic bodies do not support ContinuousDynamic.
                Body.SetVelocity(Vector3.zero);
                Body.SetAngularVelocity(Vector3.zero);
                Body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                Body.isKinematic = true;
            }
            else
            {
                if (!Body.isKinematic) return;
                Body.isKinematic = false;
                Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
        }

        private void SetVisible(bool visible)
        {
            if (_visible == visible || VisualRoot == null) return;
            _visible = visible;
            VisualRoot.gameObject.SetActive(visible);
            if (_trail != null) _trail.gameObject.SetActive(visible);
        }

        /// <summary>Ends the current throw: payload.OnEnded, Danger Sense off, throw flags cleared. Idempotent.</summary>
        private void EndFlight()
        {
            if (!_flightActive) return;
            _flightActive = false;

            var payload = Payload;
            if (payload != null)
            {
                try { payload.OnEnded(this); }
                catch (Exception e) { Debug.LogException(e, this); }
            }
            Payload = null;

            if (_dangerTarget != null)
            {
                GameEvents.Publish(new DangerSenseEvent
                {
                    Player = _dangerTarget,
                    Ball = this,
                    TimeToImpact = 0f,
                    SpeedKmh = SpeedKmh,
                    Active = false,
                });
            }
            _dangerTarget = null;

            IsPass = false;
            Unblockable = false;
            Pierce = false;
            GravityScale = 1f;
            LockedTarget = null;
            if (_trail != null) _trail.SetEmitting(false, false);
        }

        // ------------------------------------------------------------------ helpers

        private void CacheComponents()
        {
            if (Body == null) Body = GetComponent<Rigidbody>();
            if (SphereCollider == null) SphereCollider = GetComponent<SphereCollider>();
        }

        private void EnsureInitialized()
        {
            if (!_initialized) Initialize(BallManager.AllocateBallId(), IsAbilityBall, Style);
        }

        private void ConfigurePhysicsBody()
        {
            var tuning = Tuning;
            if (Body != null)
            {
                Body.mass = tuning.mass;
                Body.SetLinearDamping(tuning.linearDamping);
                Body.SetAngularDamping(tuning.angularDamping);
                Body.maxAngularVelocity = tuning.maxAngularVelocity;
                Body.useGravity = true;
                Body.interpolation = RigidbodyInterpolation.Interpolate;
                if (!Body.isKinematic) Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
            if (SphereCollider != null)
            {
                SphereCollider.radius = BaseRadius;
                SphereCollider.center = Vector3.zero;
                SphereCollider.isTrigger = false;
                SphereCollider.SetPhysicsMaterial(tuning.GetPhysicsMaterial());
            }
        }

        /// <summary>
        /// Hierarchy: Ball (Rigidbody, SphereCollider, DodgeBall) ─┬─ VisualRoot (SquashStretch; radius scale) ─ BallMesh
        ///                                                          └─ Trail (TrailRenderer, BallTrail)
        /// </summary>
        private void BuildVisuals()
        {
            var visual = new GameObject("VisualRoot") { layer = GameLayers.Ball };
            visual.transform.SetParent(transform, false);
            VisualRoot = visual.transform;

            var meshObject = new GameObject("BallMesh") { layer = GameLayers.Ball };
            meshObject.transform.SetParent(VisualRoot, false);

            var prefab = IsAbilityBall ? null : BallManager.ResolveBallVisualPrefab();
            if (prefab != null)
            {
                var instance = Instantiate(prefab, meshObject.transform, false);
                instance.name = prefab.name;
                StripPhysics(instance);
                GameLayers.SetLayerRecursively(instance, GameLayers.Ball);
                _renderers = instance.GetComponentsInChildren<Renderer>(true);
                _prefabMaterials = new Material[_renderers.Length][];
                for (int i = 0; i < _renderers.Length; i++) _prefabMaterials[i] = _renderers[i].sharedMaterials;
                _usesPrefabVisual = true;
            }
            else
            {
                var filter = meshObject.AddComponent<MeshFilter>();
                filter.sharedMesh = ProceduralBallMesh.Get();
                var meshRenderer = meshObject.AddComponent<MeshRenderer>();
                _renderers = new Renderer[] { meshRenderer };
                _usesPrefabVisual = false;
            }

            for (int i = 0; i < _renderers.Length; i++) ConfigureRenderer(_renderers[i]);

            _squash = visual.AddComponent<SquashStretch>();

            var trailObject = new GameObject("Trail") { layer = GameLayers.Ball };
            trailObject.transform.SetParent(transform, false);
            trailObject.AddComponent<TrailRenderer>();
            _trail = trailObject.AddComponent<BallTrail>();
            _trail.Initialize();
        }

        private static void ConfigureRenderer(Renderer renderer)
        {
            if (renderer == null) return;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            // Per-object motion vectors: a 200 km/h ball gets physically plausible motion blur in HDRP.
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
        }

        private static void StripPhysics(GameObject instance)
        {
            var colliders = instance.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++) DestroyComponent(colliders[i]);
            var bodies = instance.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++) DestroyComponent(bodies[i]);
        }

        private static void DestroyComponent(Component component)
        {
            if (component == null) return;
            if (Application.isPlaying) Destroy(component);
            else DestroyImmediate(component);
        }

        private void ApplyStyleToPrefabVisual(BallStyle style)
        {
            var styleMaterial = style == BallStyle.Standard ? null : BallAppearance.GetBallMaterial(style);
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;
                if (styleMaterial == null)
                {
                    if (_prefabMaterials != null && i < _prefabMaterials.Length) r.sharedMaterials = _prefabMaterials[i];
                }
                else
                {
                    int slots = Mathf.Max(1, _prefabMaterials != null && i < _prefabMaterials.Length ? _prefabMaterials[i].Length : 1);
                    var materials = new Material[slots];
                    for (int s = 0; s < slots; s++) materials[s] = styleMaterial;
                    r.sharedMaterials = materials;
                }
            }
        }

        private void BuildRewindRecorder()
        {
            Rewind = GetComponent<TimeRewindRecorder>();
            if (Rewind == null) Rewind = gameObject.AddComponent<TimeRewindRecorder>();
            Rewind.Capture = CaptureSnapshot;
        }

        private RewindSnapshot CaptureSnapshot()
        {
            var holder = Holder;
            return new RewindSnapshot
            {
                Time = Time.time,
                Position = FlightPosition,
                Rotation = transform.rotation,
                Velocity = Velocity,
                Hp = 0f,
                State = (int)State,
                HolderId = holder != null ? holder.PlayerId : -1,
            };
        }

        private void FollowHolder()
        {
            if (_socket != null)
            {
                transform.SetPositionAndRotation(_socket.position, _socket.rotation);
                return;
            }
            var holder = Holder;
            if (holder == null) return;
            // No hand socket (model without a humanoid rig): carry the ball at the right hip / chest.
            Vector3 position = holder.ChestPosition + holder.Forward * 0.32f + holder.transform.right * 0.22f + Vector3.down * 0.28f;
            transform.SetPositionAndRotation(position, holder.Rotation);
        }

        private void UpdateVisualScale(float dt)
        {
            if (VisualRoot == null) return;
            float target = Radius / BaseRadius;
            // Grows/shrinks over ~50 ms (Overcharge +20 % reads as the ball swelling as it leaves the hand).
            _visualRadiusScale = Mathf.MoveTowards(_visualRadiusScale, target, Mathf.Max(0f, dt) * 4f);

            float fade = 1f;
            if (_fadeRemaining >= 0f)
            {
                float total = Mathf.Max(0.01f, Tuning.abilityBallFadeTime);
                float t = Mathf.Clamp01(_fadeRemaining / total);
                fade = t * t * (3f - 2f * t);
            }

            float scale = _visualRadiusScale * fade;
            var current = VisualRoot.localScale;
            if (Mathf.Abs(current.x - scale) > 1e-4f || Mathf.Abs(current.y - scale) > 1e-4f || Mathf.Abs(current.z - scale) > 1e-4f)
                VisualRoot.localScale = new Vector3(scale, scale, scale);
        }

        private void SquashOnImpact(Vector3 normal, float normalSpeed)
        {
            if (_squash == null || normalSpeed <= 0f) return;
            var tuning = Tuning;
            float intensity = Mathf.Clamp01(normalSpeed / tuning.fullSquashImpactSpeed) * tuning.maxBounceSquash;
            if (intensity > 0.02f) _squash.Impact(normal, intensity, 0.16f);
        }

        /// <summary>Places the (kinematic) ball exactly at <paramref name="position"/> as part of resolving a contact.</summary>
        private void PlaceAt(Vector3 position)
        {
            _simPosition = position;
            HardPlace(position);
        }

        /// <summary>Instant relocation without interpolation.</summary>
        private void HardPlace(Vector3 position)
        {
            transform.position = position;
            if (Body != null) Body.position = position;
        }

        private void RecycleSelf()
        {
            var manager = BallManager.Instance;
            if (manager != null && IsAbilityBall) manager.Recycle(this);
            else if (IsAbilityBall) ReturnToPool();
        }

        private bool IsHostileTo(DodgeballPlayer player)
        {
            // Thrower-less live balls (environment, rewinds) threaten everybody.
            if (LastThrower == null) return true;
            return PlayerRegistry.AreEnemies(LastThrower, player);
        }

        private bool IsIgnored(Collider collider)
        {
            for (int i = 0; i < _ignoredColliders.Count; i++)
                if (_ignoredColliders[i] == collider) return true;
            return false;
        }

        private void MarkIgnored(Collider collider)
        {
            if (collider != null && !IsIgnored(collider)) _ignoredColliders.Add(collider);
        }

        private void SortContacts(int count)
        {
            // Insertion sort: tiny arrays, no allocations, stable for equal distances.
            for (int i = 1; i < count; i++)
            {
                var key = _contacts[i];
                int j = i - 1;
                while (j >= 0 && _contacts[j].Distance > key.Distance)
                {
                    _contacts[j + 1] = _contacts[j];
                    j--;
                }
                _contacts[j + 1] = key;
            }
        }

        private static void ResolveInitialOverlap(Collider collider, Vector3 centre, Vector3 direction, out Vector3 point, out Vector3 normal)
        {
            // ClosestPoint supports primitives and convex meshes only.
            bool supported = !(collider is MeshCollider meshCollider) || meshCollider.convex;
            Vector3 closest = supported ? collider.ClosestPoint(centre) : centre;
            Vector3 away = centre - closest;
            if (away.sqrMagnitude > 1e-8f)
            {
                point = closest;
                normal = away.normalized;
            }
            else
            {
                // Centre inside the collider: assume we are entering it head-on.
                point = centre;
                normal = -direction;
            }
        }

        /// <summary>Earliest distance along a ray segment at which it enters a sphere (the start must be outside).</summary>
        private static bool SegmentSphereEntry(Vector3 start, Vector3 direction, float length, Vector3 centre, float radius, out float distance)
        {
            distance = 0f;
            Vector3 m = start - centre;
            float b = Vector3.Dot(m, direction);
            float c = m.sqrMagnitude - radius * radius;
            if (c > 0f && b > 0f) return false; // outside and moving away
            float discriminant = b * b - c;
            if (discriminant < 0f) return false;
            float t = -b - Mathf.Sqrt(discriminant);
            if (t < 0f) t = 0f;
            if (t > length) return false;
            distance = t;
            return true;
        }

        private Vector3 ComputeLaunchSpin(Vector3 velocity)
        {
            var tuning = Tuning;
            if (velocity.sqrMagnitude < 1e-4f || tuning.launchSpinRate <= 0f) return Vector3.zero;
            Vector3 direction = velocity.normalized;
            // Overhand throws leave the fingertips last: backspin around cross(direction, up), plus random side spin.
            Vector3 axis = Vector3.Cross(direction, Vector3.up);
            if (axis.sqrMagnitude < 1e-4f) axis = Vector3.right;
            axis.Normalize();
            axis = Quaternion.AngleAxis(UnityEngine.Random.Range(-tuning.launchSpinTilt, tuning.launchSpinTilt), direction) * axis;
            return axis * (tuning.launchSpinRate * UnityEngine.Random.Range(0.75f, 1.25f));
        }

        private static Vector3 ClampSpeed(Vector3 velocity)
        {
            float max = GameConstants.MaxBallSpeedMs;
            float sqr = velocity.sqrMagnitude;
            return sqr > max * max ? velocity * (max / Mathf.Sqrt(sqr)) : velocity;
        }
    }
}
