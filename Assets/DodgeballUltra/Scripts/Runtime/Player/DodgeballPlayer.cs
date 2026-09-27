using System;
using DodgeballUltra.Abilities;
using DodgeballUltra.Audio;
using DodgeballUltra.Characters;
using DodgeballUltra.Combat;
using DodgeballUltra.Events;
using DodgeballUltra.Match;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Player
{
    /// <summary>
    /// CONTRACT (kernel) - hub component of a player. Owns identity (team, hero, zone) and drives the deterministic
    /// per-frame update order of its sub-systems:
    /// <code>
    /// Update:      intent = IntentSource.Sample()  -> Status.Tick -> StateMachine.Tick -> Combat.Tick -> Abilities.Tick -> Health.Tick
    /// FixedUpdate: StateMachine.FixedTick -> Motor.FixedTick
    /// </code>
    /// Sub-components (PlayerMotor, PlayerStateMachine, PlayerCombatController, PlayerHealth, StatusEffectController,
    /// AbilityController) MUST NOT implement Update/FixedUpdate themselves; they expose Tick/FixedTick that this class calls.
    /// Visual components (CharacterVisual and children) run on their own LateUpdate/OnAnimatorIK.
    /// <para>Owner module: Player. Public members below are the contract; keep them.</para>
    /// <para>
    /// Detailed Update order: sample intent (neutralised when input is locked or the player cannot act) -&gt; Status.Tick
    /// -&gt; Motor.Tick (readouts) -&gt; StateMachine.Tick (throw/catch/jump/slide/sprint transitions) -&gt; skill / ultimate /
    /// pass / pick-up intents (only when <see cref="CanAct"/>) -&gt; Combat.Tick -&gt; Abilities.Tick -&gt; Health.Tick.
    /// </para>
    /// <para>
    /// The hub also turns motor events into feedback (jump/landing/slide audio and dust, animator triggers), muted by
    /// Gale's Silent Footsteps, and keeps the motor confined to the team's court zone (<see cref="AutoConfine"/>).
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
    public sealed class DodgeballPlayer : MonoBehaviour
    {
        // ------------------------------------------------------------------ tuning

        [Header("Movement feedback")]
        [Tooltip("Play jump/landing/slide sounds and dust (muted by the SilentFootsteps status).")]
        [SerializeField] private bool movementFeedback = true;

        [Tooltip("Landing impact speed (m/s) below which no landing thud/dust is produced.")]
        [Min(0f)] [SerializeField] private float landingFeedbackMinSpeed = 2.5f;

        [Tooltip("Landing impact speed (m/s) that produces the loudest thud and the biggest dust puff.")]
        [Min(0.1f)] [SerializeField] private float landingFeedbackMaxSpeed = 9f;

        [Tooltip("Volume of the jump effort sound (0..1).")]
        [Range(0f, 1f)] [SerializeField] private float jumpVolume = 0.55f;

        [Tooltip("Volume of the slide scrape sound (0..1).")]
        [Range(0f, 1f)] [SerializeField] private float slideVolume = 0.7f;

        // ------------------------------------------------------------------ identity
        public int PlayerId { get; private set; }
        public string DisplayName { get; private set; }
        public TeamId Team { get; private set; } = TeamId.None;
        public CharacterData Character { get; private set; }
        public HeroId Hero => Character != null ? Character.heroId : HeroId.Rayne;
        public bool IsHumanControlled { get; private set; }

        /// <summary>The player followed by the camera and HUD.</summary>
        public bool IsLocalPlayer { get; private set; }

        public CourtZone Zone { get; private set; } = CourtZone.Infield;
        public bool IsInfield => Zone == CourtZone.Infield;

        /// <summary>True once <see cref="Initialize"/> ran.</summary>
        public bool IsInitialized { get; private set; }

        // ------------------------------------------------------------------ sub-systems (assigned in Initialize / Awake)
        public Rigidbody Body { get; private set; }
        public CapsuleCollider Capsule { get; private set; }
        public PlayerMotor Motor { get; private set; }
        public PlayerStateMachine StateMachine { get; private set; }
        public PlayerCombatController Combat { get; private set; }
        public PlayerHealth Health { get; private set; }
        public StatusEffectController Status { get; private set; }
        public AbilityController Abilities { get; private set; }
        public CharacterVisual Visual { get; private set; }
        public TimeRewindRecorder Rewind { get; private set; }

        /// <summary>Who drives this player. Can be swapped at runtime (e.g. bot takes over).</summary>
        public IIntentSource IntentSource { get; set; }

        /// <summary>Intent sampled this frame, already neutralised when <see cref="InputLocked"/> or the player cannot act.</summary>
        public PlayerIntent Intent { get; private set; }

        /// <summary>When true the intent source is ignored (countdowns, round transitions).</summary>
        public bool InputLocked { get; set; }

        /// <summary>Raised after the zone changes (Infield/Outfield).</summary>
        public event Action<DodgeballPlayer, CourtZone> ZoneChanged;

        /// <summary>
        /// When true (default) the motor is confined to <c>Court.GetConfinement(Team, Zone)</c> whenever the zone changes or
        /// the player spawns/resets. Turn off for free-roam sequences (the Match module may also call Motor.SetConfinement).
        /// </summary>
        public bool AutoConfine { get; set; } = true;

        // ------------------------------------------------------------------ spatial helpers
        public Vector3 Position => transform.position;
        public Quaternion Rotation => transform.rotation;

        /// <summary>Planar facing direction.</summary>
        public Vector3 Forward
        {
            get
            {
                var f = transform.forward;
                f.y = 0f;
                return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
            }
        }

        public Vector3 Velocity => Motor != null ? Motor.Velocity : Vector3.zero;

        /// <summary>World point at chest height: the aim point for throws and the centre of the catch zone.</summary>
        public Vector3 ChestPosition => transform.position + Vector3.up * (Capsule != null ? Capsule.height * 0.72f : 1.3f);

        public Vector3 HeadPosition => transform.position + Vector3.up * (Capsule != null ? Capsule.height * 0.93f : 1.65f);

        /// <summary>Can currently be hit by enemy balls (infield, not mid-elimination).</summary>
        public bool IsTargetable =>
            IsInitialized && Zone == CourtZone.Infield && Health != null && Health.IsAlive &&
            (StateMachine == null || StateMachine.IncapacitationReason != IncapacitationReason.Eliminated);

        /// <summary>Can use abilities / throw / catch this frame.</summary>
        public bool CanAct => StateMachine != null && StateMachine.CanAct && !InputLocked;

        // ------------------------------------------------------------------ internals

        private static UnityEngine.Object s_zeroFrictionMaterial;

        private bool _motorEventsHooked;
        private int _lastTeleportFrame = -1;

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            Body = GetComponent<Rigidbody>();
            Capsule = GetComponent<CapsuleCollider>();
            Intent = PlayerIntent.Neutral(transform.position, transform.forward);
        }

        /// <summary>
        /// Wires every sub-system for <paramref name="info"/>: sets identity, adds/gets components, builds the realistic
        /// character visual (CharacterVisual.Build), sets up abilities from CharacterData, registers in PlayerRegistry,
        /// sets layer <see cref="GameLayers.Player"/> and publishes PlayerSpawnedEvent.
        /// </summary>
        public void Initialize(in PlayerSpawnInfo info)
        {
            bool reinitialising = IsInitialized;
            IsInitialized = false;

            var data = info.Character;
            var movement = data != null && data.movement != null ? data.movement : new MotorProfile();
            var combatProfile = data != null && data.combat != null ? data.combat : new CombatProfile();
            float height = data != null ? data.height : 1.8f;
            float radius = data != null ? data.radius : 0.32f;
            float maxHp = data != null ? data.maxHp : Core.GameConstants.DefaultMaxHp;

            // ---- identity
            PlayerId = info.PlayerId;
            Character = data;
            DisplayName = !string.IsNullOrEmpty(info.DisplayName) ? info.DisplayName
                : data != null && !string.IsNullOrEmpty(data.displayName) ? data.displayName
                : $"Player {info.PlayerId}";
            Team = info.Team;
            IsHumanControlled = info.IsHuman;
            IsLocalPlayer = info.IsLocal;
            IntentSource = info.IntentSource;
            InputLocked = false;
            Zone = CourtZone.Infield;
            gameObject.name = $"Player_{info.PlayerId}_{DisplayName}";

            // ---- components
            Body = GetOrAdd<Rigidbody>();
            Capsule = GetOrAdd<CapsuleCollider>();
            Motor = GetOrAdd<PlayerMotor>();
            StateMachine = GetOrAdd<PlayerStateMachine>();
            Combat = GetOrAdd<PlayerCombatController>();
            Health = GetOrAdd<PlayerHealth>();
            Status = GetOrAdd<StatusEffectController>();
            Abilities = GetOrAdd<AbilityController>();
            Rewind = GetOrAdd<TimeRewindRecorder>();

            // ---- physics body: the motor integrates gravity and velocity itself.
            ConfigureBody(movement.mass);
            ConfigureCapsule(height, radius);
            gameObject.layer = GameLayers.Player; // root only: visuals/ragdoll keep their own layers

            // ---- realistic human body
            BuildVisual(data, reinitialising);

            // ---- gameplay systems (order matters: later systems read earlier ones)
            Motor.Profile = movement;
            Motor.Initialize(this);
            Motor.SetBaseCapsule(height, radius);
            Health.Initialize(this, maxHp);
            Status.Initialize(this);
            Combat.Initialize(this, combatProfile);
            Abilities.Setup(this, data);
            StateMachine.Initialize(this);
            Rewind.Capture = CaptureSnapshot;
            HookMotorEvents();

            // ---- placement
            Quaternion rotation = IsValidRotation(info.Rotation) ? info.Rotation : transform.rotation;
            Vector3 position = info.Position;
            if (position == Vector3.zero && transform.position != Vector3.zero) position = transform.position;
            Motor.Teleport(position, rotation);
            Rewind.Clear();
            ApplyAutoConfinement();

            Intent = PlayerIntent.Neutral(Position, Forward);
            PlayerRegistry.Register(this);
            IsInitialized = true;

            GameEvents.Publish(new PlayerSpawnedEvent { Player = this });
        }

        /// <summary>Moves the player between Infield and Outfield (MatchManager decides when). Publishes PlayerZoneChangedEvent.</summary>
        public void SetZone(CourtZone zone)
        {
            bool changed = Zone != zone;
            Zone = zone;
            ApplyAutoConfinement();

            // An eliminated player placed in the outfield (teleported this same frame) takes control again.
            if (zone == CourtZone.Outfield && _lastTeleportFrame == Time.frameCount) ReleaseEliminationLock();

            if (!changed) return;
            GameEvents.Publish(new PlayerZoneChangedEvent { Player = this, Zone = zone });
            RaiseZoneChanged(zone);
        }

        /// <summary>Instantly relocates the player (resets interpolation and planar velocity).</summary>
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            if (!IsValidRotation(rotation)) rotation = transform.rotation;
            if (Motor != null) Motor.Teleport(position, rotation);
            else transform.SetPositionAndRotation(position, rotation);

            // Rewinds must never cross a relocation.
            if (Rewind != null) Rewind.Clear();
            _lastTeleportFrame = Time.frameCount;

            // Arriving in the outfield after an elimination: the ragdoll phase is over, the player plays on from there.
            if (Zone == CourtZone.Outfield) ReleaseEliminationLock();
        }

        /// <summary>Full reset between rounds: HP, statuses, state machine, abilities' round state, held ball dropped.</summary>
        public void ResetForRound(Vector3 position, Quaternion rotation)
        {
            // Effects first: removing Frozen releases its incapacitation; nothing may linger into the next round.
            if (Status != null) Status.RemoveAll();
            if (Combat != null) Combat.ResetForRound();
            if (Abilities != null) Abilities.ResetForRound(false); // the ultimate meter carries over
            if (Health != null) Health.ResetForRound();
            if (StateMachine != null) StateMachine.ResetToGrounded();

            if (Visual != null)
            {
                try
                {
                    Visual.ResetVisual();
                }
                catch (Exception e)
                {
                    Debug.LogException(e, this);
                }
            }

            if (Motor != null)
            {
                Motor.SetFrozen(false);
                if (Motor.IsSliding) Motor.EndSlide();
                Motor.SetCapsuleHeightScale(1f);
                Motor.SetMode(MovementMode.Walk);
                Motor.SetMoveInput(Vector3.zero, 0f);
            }
            if (Capsule != null) Capsule.enabled = true;

            Teleport(position, rotation);
            SetZone(CourtZone.Infield);
            Intent = PlayerIntent.Neutral(Position, Forward);
        }

        /// <summary>Raises <see cref="ZoneChanged"/>. For use by the implementation.</summary>
        private void RaiseZoneChanged(CourtZone zone) => ZoneChanged?.Invoke(this, zone);

        private void OnDestroy()
        {
            PlayerRegistry.Unregister(this);
            UnhookMotorEvents();
        }

        // ------------------------------------------------------------------ frame loop

        private void Update()
        {
            if (!IsInitialized) return;
            float dt = Time.deltaTime;

            // 1. Intent. The source is always sampled (camera look, AI perception keep running), but its commands are
            //    discarded while input is locked or the player is stunned/incapacitated.
            PlayerIntent sampled = default;
            bool hasSample = false;
            if (IntentSource != null)
            {
                try
                {
                    sampled = IntentSource.Sample(this, dt);
                    hasSample = true;
                }
                catch (Exception e)
                {
                    Debug.LogException(e, this);
                }
            }
            Intent = hasSample && CanAct ? Sanitize(sampled) : PlayerIntent.Neutral(Position, Forward);

            // 2. Status effects expire first so this frame's state logic sees the current effects.
            Status.Tick(dt);
            Motor.Tick(dt);

            // 3. Behaviour states: throw / catch / jump / slide / sprint and motor commands.
            StateMachine.Tick(dt);

            // 4. Discrete actions that do not have a state of their own.
            if (CanAct)
            {
                var intent = Intent;
                if (intent.SkillPressed && Abilities != null) Abilities.TryUseSkill();
                if (intent.UltimatePressed && Abilities != null) Abilities.TryUseUltimate();
                // Pass (Houdini's Hat Trick hooks in through Combat.PassHandler); a pass mid wind-up gives the ball away
                // and ChargingThrow notices the empty hand. Manual pick-up grabs the nearest reachable free ball.
                if (intent.PassPressed) Combat.TryPass();
                if (intent.PickupPressed) Combat.TryPickupNearest();
            }

            // 5. Remaining systems.
            Combat.Tick(dt);
            if (Abilities != null) Abilities.Tick(dt);
            Health.Tick(dt);
        }

        private void FixedUpdate()
        {
            if (!IsInitialized) return;
            float dt = Time.fixedDeltaTime;
            StateMachine.FixedTick(dt);
            Motor.FixedTick(dt);
        }

        // ------------------------------------------------------------------ setup helpers

        private T GetOrAdd<T>() where T : Component
        {
            var c = GetComponent<T>();
            return c != null ? c : gameObject.AddComponent<T>();
        }

        private void ConfigureBody(float mass)
        {
            Body.isKinematic = false;
            Body.mass = Mathf.Max(1f, mass);
            Body.useGravity = false; // the motor applies Physics.gravity * gravityMultiplier
            Body.interpolation = RigidbodyInterpolation.Interpolate;
            Body.collisionDetectionMode = CollisionDetectionMode.Continuous;
            Body.constraints = RigidbodyConstraints.FreezeRotation;
            Body.SetLinearDamping(0f);
            Body.SetAngularDamping(0.05f);
            Body.SetVelocity(Vector3.zero);
            Body.SetAngularVelocity(Vector3.zero);
        }

        private void ConfigureCapsule(float height, float radius)
        {
            Capsule.enabled = true;
            Capsule.isTrigger = false;
            Capsule.direction = 1;
            Capsule.radius = Mathf.Max(0.05f, radius);
            Capsule.height = Mathf.Max(Capsule.radius * 2f + 0.01f, height);
            Capsule.center = new Vector3(0f, Capsule.height * 0.5f, 0f);

            // Zero friction: the motor owns all planar motion; friction would make players stick to walls and each other.
            if (s_zeroFrictionMaterial == null)
                s_zeroFrictionMaterial = PhysicsCompat.CreatePhysicsMaterial("DU_PlayerZeroFriction", 0f, 0f, 0f, false);
            Capsule.SetPhysicsMaterial(s_zeroFrictionMaterial);
        }

        private void BuildVisual(CharacterData data, bool reinitialising)
        {
            if (Visual != null)
            {
                if (!reinitialising) return;
                // Hero swap: the old body goes away completely.
                var old = Visual.gameObject;
                Visual = null;
                old.SetActive(false);
                Destroy(old);
            }

            var go = new GameObject("Visual");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            Visual = go.AddComponent<CharacterVisual>();
            try
            {
                Visual.Build(this, data);
            }
            catch (Exception e)
            {
                // Gameplay must keep working even if an art asset is broken.
                Debug.LogException(e, this);
            }
        }

        private void HookMotorEvents()
        {
            if (_motorEventsHooked || Motor == null) return;
            Motor.Jumped += OnMotorJumped;
            Motor.Landed += OnMotorLanded;
            Motor.SlideStarted += OnMotorSlideStarted;
            _motorEventsHooked = true;
        }

        private void UnhookMotorEvents()
        {
            if (!_motorEventsHooked || Motor == null) return;
            Motor.Jumped -= OnMotorJumped;
            Motor.Landed -= OnMotorLanded;
            Motor.SlideStarted -= OnMotorSlideStarted;
            _motorEventsHooked = false;
        }

        /// <summary>Applies the court confinement for the current team and zone (when a Court exists).</summary>
        private void ApplyAutoConfinement()
        {
            if (!AutoConfine || Motor == null || !Team.IsValid()) return;
            var court = Court.Instance;
            if (court == null) return;
            Motor.SetConfinement(court.GetConfinement(Team, Zone));
        }

        /// <summary>Ends the post-elimination lock (ragdoll snapped back, motor/collider restored).</summary>
        private void ReleaseEliminationLock()
        {
            if (StateMachine == null || !StateMachine.IsIn(PlayerStateId.Incapacitated) ||
                StateMachine.IncapacitationReason != IncapacitationReason.Eliminated)
                return;

            var ragdoll = Visual != null ? Visual.Ragdoll : null;
            if (ragdoll != null && ragdoll.IsRagdolled) ragdoll.ResetImmediate();
            StateMachine.ReleaseIncapacitation(IncapacitationReason.Eliminated);
        }

        // ------------------------------------------------------------------ rewind

        private RewindSnapshot CaptureSnapshot()
        {
            var ball = Combat != null ? Combat.HeldBall : null;
            return new RewindSnapshot
            {
                Time = Time.time,
                Position = Body != null ? Body.position : transform.position,
                Rotation = Body != null ? Body.rotation : transform.rotation,
                Velocity = Motor != null ? Motor.Velocity : Vector3.zero,
                Hp = Health != null ? Health.CurrentHp : 0f,
                State = StateMachine != null ? (int)StateMachine.Current : 0,
                HolderId = ball != null ? ball.BallId : -1,
            };
        }

        // ------------------------------------------------------------------ movement feedback

        private bool FeedbackMuted => !movementFeedback || (Status != null && Status.Has(StatusEffectType.SilentFootsteps));

        private void OnMotorJumped()
        {
            if (Visual != null && Visual.AnimatorDriver != null) Visual.AnimatorDriver.TriggerJump();
            if (FeedbackMuted) return;
            AudioManager.PlayAt(SfxId.Jump, Position, jumpVolume, VoicePitch);
        }

        private void OnMotorLanded(float impactSpeed)
        {
            if (FeedbackMuted || impactSpeed < landingFeedbackMinSpeed) return;
            float k = Mathf.InverseLerp(landingFeedbackMinSpeed, Mathf.Max(landingFeedbackMinSpeed + 0.1f, landingFeedbackMaxSpeed), impactSpeed);
            VfxManager.Spawn(VfxId.LandingDust, Position, Quaternion.identity, Mathf.Lerp(0.6f, 1.3f, k));
            AudioManager.PlayAt(SfxId.Land, Position, Mathf.Lerp(0.35f, 1f, k), Mathf.Lerp(1.05f, 0.9f, k));
        }

        private void OnMotorSlideStarted()
        {
            if (FeedbackMuted) return;
            float duration = Motor != null ? Motor.SlideTimeRemaining : 0.8f;
            VfxManager.SpawnAttached(VfxId.SlideDust, transform, Vector3.zero, 1f, null, duration);
            AudioManager.PlayAt(SfxId.Slide, Position, slideVolume, 1f);
        }

        private float VoicePitch => Character != null ? Character.voicePitch : 1f;

        // ------------------------------------------------------------------ misc helpers

        /// <summary>Guards against malformed intents from custom sources (NaN, non-planar move, zero aim).</summary>
        private PlayerIntent Sanitize(PlayerIntent intent)
        {
            Vector3 move = intent.Move;
            if (float.IsNaN(move.x) || float.IsNaN(move.y) || float.IsNaN(move.z)) move = Vector3.zero;
            move.y = 0f;
            intent.Move = Vector3.ClampMagnitude(move, 1f);

            Vector3 aim = intent.AimDirection;
            if (float.IsNaN(aim.x) || float.IsNaN(aim.y) || float.IsNaN(aim.z) || aim.sqrMagnitude < 1e-6f)
            {
                aim = Forward;
                intent.AimPoint = Position + aim * 10f;
            }
            intent.AimDirection = aim.normalized;
            return intent;
        }

        private static bool IsValidRotation(Quaternion q)
        {
            float n = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            return n > 1e-4f && !float.IsNaN(n);
        }
    }
}
