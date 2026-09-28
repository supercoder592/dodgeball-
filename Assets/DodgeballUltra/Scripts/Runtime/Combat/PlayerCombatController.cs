using System;
using System.Collections.Generic;
using System.Reflection;
using DodgeballUltra.Abilities;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Combat
{
    /// <summary>
    /// CONTRACT (kernel, spec file) - throwing (charge/release), catching (timing window), passing and picking up.
    /// <para>
    /// Throw pipeline: <c>BeginCharge -> (hold) -> ReleaseThrow -> BuildThrowParams -> IThrowModifier chain ->
    /// ThrowSolver (lead + ballistic arc) -> DodgeBall.Launch -> BallThrownEvent</c>.
    /// </para>
    /// <para>
    /// Catch pipeline: <c>TryStartCatch</c> records <c>t_input</c> and arms the catch stance for CombatProfile.catchWindow.
    /// When a live enemy ball reaches the catch zone the ball calls <see cref="TryResolveCatch"/>:
    /// <c>quality = CatchTiming.Classify(impactTime - t_input, PerfectCatchWindow, catchWindow)</c>.
    /// Perfect catch rewards: revive one outfield teammate (MatchManager), +15% ultimate, +20% counter-throw speed;
    /// and the JuiceManager pipeline runs (via BallCaughtEvent).
    /// </para>
    /// <para>Owner module: Combat. Ticked by DodgeballPlayer.</para>
    /// <para>
    /// <b>Throwing in detail</b> (all numbers are Inspector / <see cref="CombatProfile"/> tunables):
    /// <code>
    /// ChargeSeconds     += dt while charging, capped at max(maxChargeTime, fullChargeTime)   (Rayne's Overcharge raises it)
    /// ChargeNormalized   = chargeCurve( clamp01(ChargeSeconds / fullChargeTime) )             (0..1, HUD bar / animation)
    /// SpeedMultiplier    = lerp(minChargeMultiplier, 1, ChargeNormalized)                    (0.72 for an instant throw)
    ///                    * (1 + counterBoost)   once, while the Perfect Catch counter boost is up   (+20 %)
    ///                    * every IThrowModifier (sorted by Order: Overcharge 0, stealth 50, ...)
    /// RallyCount         = the held ball's rally count (catch + re-throw without touching the floor)
    /// FinalSpeed         = RallyMath.ComputeSpeed(BaseSpeed * SpeedMultiplier, RallyCount)    (+10 % per rally, 220 km/h cap)
    /// velocity           = ThrowSolver.Solve(params)   lead-aimed low arc under gravity * thrownGravityScale
    /// </code>
    /// The throw target is <see cref="PlayerIntent.DesiredTarget"/> (bots) when valid - and only while the intent's aim point
    /// stays within <see cref="explicitTargetAimTolerance"/> of the perfect lead point, so a bot's deliberate aim error is
    /// kept - otherwise the aim-assist soft lock <see cref="CurrentTarget"/>, otherwise the intent's aim point
    /// (<see cref="PlayerIntent.AimPoint"/>, solved as a ballistic arc through it). A throw held until the hard limit
    /// (<see cref="CombatProfile.maxChargeTime"/>) is released automatically (<see cref="autoReleaseAtMaxCharge"/>).
    /// </para>
    /// <para>
    /// <b>Catching in detail</b>: pressing Catch arms the hands for <c>max(catchWindow, PerfectCatchWindow)</c> seconds and
    /// records <c>t_input = Time.time</c> (scaled gameplay time, so hitstop freezes the window too). A live enemy ball that
    /// reaches the hands (the ball's catch-zone sweep) or the body while armed, inside the frontal cone
    /// (<see cref="CombatProfile.catchConeAngle"/>) and at a catchable height, is classified by
    /// <see cref="CatchTiming.Classify"/>. Nothing arriving before the window closes is a whiff:
    /// <see cref="CatchWhiffEvent"/> and <see cref="CombatProfile.whiffRecovery"/> seconds before the next attempt.
    /// Frozen players (<see cref="CatchingBlocked"/>) and unblockable balls (Rayne's beam) are never caught.
    /// </para>
    /// <para>
    /// <b>Rewards</b> (see <see cref="CombatRuleValues"/>): a Perfect Catch always pays the catcher's rewards here - the
    /// revive call (<see cref="MatchManager.ReviveOneOutfieldTeammate"/>, de-duplicated by the match), +15 % ultimate
    /// (<see cref="MatchRules.ultGainOnPerfectCatch"/>) and the +20 % counter throw. Normal-catch ultimate, hit ultimate and
    /// the optional <see cref="MatchRules.catchEliminatesThrower"/> rule are granted by the <see cref="MatchManager"/> from
    /// the published events; without a match manager (sandbox) this controller / <see cref="HitResolver"/> grant the
    /// ultimate charge themselves.
    /// </para>
    /// <para>
    /// <b>Hands</b>: at most one ball. The controller follows its ball through <see cref="DodgeBall.StateChanged"/> - when an
    /// ability takes it away (Houdini's Grand Vanish, Gale's steal, a pool recycle) the hand is emptied and any charge is
    /// cancelled on the spot. Pick-ups: automatic within <see cref="CombatProfile.autoPickupRadius"/>, manual within
    /// <see cref="CombatProfile.manualPickupRadius"/>. Passes: <see cref="PassHandler"/> first (Houdini's Hat Trick), else a
    /// catchable lob to the receiver (the bot's chosen teammate in <see cref="PlayerIntent.DesiredTarget"/>, else the nearest
    /// teammate who can take the ball); enemies may intercept it with a catch.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerCombatController : MonoBehaviour
    {
        // ================================================================== tuning (per-player, not per-hero)
        // Per-hero numbers (speeds, windows, radii) live in CombatProfile (CharacterData.combat). The values below are
        // shared game-feel constants; they are serialized so they can be tweaked on a player prefab / in play mode.

        [Header("Profile")]
        [Tooltip("Copy the hero's CombatProfile when the player is initialised, so runtime changes (Rayne's Overcharge raising " +
                 "maxChargeTime, buffs) never leak into the shared CharacterData asset or into another player using the same " +
                 "hero. Turn off to live-tune the CharacterData asset in play mode.")]
        [SerializeField] private bool copyProfileOnInitialize = true;

        [Header("Throwing")]
        [Tooltip("Release the throw automatically when it has been held for CombatProfile.maxChargeTime (a real arm cannot " +
                 "stay cocked forever). Off: the charge simply stops growing at the limit.")]
        [SerializeField] private bool autoReleaseAtMaxCharge = true;

        [Tooltip("Speed bonus of the throw right after a Perfect Catch (0.2 = +20 %, spec). Consumed by the next throw.")]
        [Range(0f, 1f)] [SerializeField] private float counterBoost = GameConstants.PerfectCatchCounterBoost;

        [Tooltip("A hand socket farther than this from the chest (m) is ignored as a release point (ragdoll / IK glitch guard).")]
        [Min(0.2f)] [SerializeField] private float handSocketMaxDistance = 1.1f;

        [Tooltip("Release point without a hand socket: metres in front of the chest (outside the body capsule).")]
        [Range(0f, 1f)] [SerializeField] private float throwOriginForward = 0.4f;

        [Tooltip("Release point without a hand socket: metres to the right of the chest (right-handed throw).")]
        [Range(-0.5f, 0.5f)] [SerializeField] private float throwOriginSide = 0f;

        [Tooltip("Release point without a hand socket: metres above the chest.")]
        [Range(-0.5f, 0.5f)] [SerializeField] private float throwOriginUp = 0f;

        [Tooltip("Pull the release point back when the hand reaches through a wall (a ray from the chest to the hand against " +
                 "court geometry), so a ball thrown while hugging a wall never starts on its far side.")]
        [SerializeField] private bool keepReleaseInsideCourt = true;

        [Header("Passing")]
        [Tooltip("Gravity multiplier of a pass lob (1 = real gravity: passes are soft, catchable arcs).")]
        [Range(0.2f, 1.5f)] [SerializeField] private float passGravityScale = 1f;

        [Tooltip("Pass speed is at least this multiple of the minimum (45-degree) speed needed to cover the distance, so long " +
                 "passes still arrive on a comfortable arc.")]
        [Range(1f, 2f)] [SerializeField] private float passArcFactor = 1.18f;

        [Tooltip("Upper bound of a pass speed (m/s). 24 m/s is a firm two-handed chest pass.")]
        [Range(5f, 40f)] [SerializeField] private float passMaxSpeed = 24f;

        [Tooltip("Farthest teammate (m) a pass is thrown to. 0 = anywhere in the arena.")]
        [Min(0f)] [SerializeField] private float passMaxRange = 0f;

        [Header("Catching")]
        [Tooltip("Balls lower than this above the feet (m) cannot be caught - they are scooped by the shins (a hit).")]
        [Range(0f, 1f)] [SerializeField] private float minCatchHeight = 0.25f;

        [Tooltip("How far above the head (m) the hands still reach for a catch.")]
        [Range(0f, 1.2f)] [SerializeField] private float catchHeightAboveHead = 0.6f;

        [Tooltip("Planar distance (m) from the body axis within which a ball counts as in front whatever its bearing " +
                 "(it is already between the hands).")]
        [Range(0f, 0.5f)] [SerializeField] private float catchConeInnerRadius = 0.15f;

        [Header("Picking up")]
        [Tooltip("Seconds after a throw before this player auto-picks-up a ball again.")]
        [Min(0f)] [SerializeField] private float pickupLockAfterThrow = 0.3f;

        [Tooltip("Seconds after dropping / fumbling a ball before this player auto-picks-up a ball again (no instant re-grab).")]
        [Min(0f)] [SerializeField] private float pickupLockAfterDrop = 0.45f;

        [Tooltip("How far below the feet (m) a ball can still be picked up (slopes, the ball's own radius).")]
        [Range(0f, 1f)] [SerializeField] private float pickupReachBelow = 0.35f;

        [Tooltip("How far above the feet (m) a ball can be grabbed: standing overhead reach, e.g. a ball frozen in Chrono's " +
                 "stasis above the head (a jump lifts the feet and extends it). Matches the AI's stasis reach.")]
        [Range(0.5f, 3f)] [SerializeField] private float pickupReachAbove = 2.3f;

        [Tooltip("Forward speed (m/s) of a ball released to make room for another one (GiveBall while holding).")]
        [Min(0f)] [SerializeField] private float swapDropForwardSpeed = 1.2f;

        [Tooltip("Upward speed (m/s) of a ball released to make room for another one.")]
        [Min(0f)] [SerializeField] private float swapDropUpSpeed = 1.5f;

        [Header("Aim assist (soft lock)")]
        [Tooltip("An explicit intent target (PlayerIntent.DesiredTarget, i.e. bots) is lead-solved only while the intent's aim " +
                 "point lies within this distance (m) of the perfect lead point - about a body width. A wider (deliberately " +
                 "imperfect) aim is thrown where it points, so AI lead accuracy / aim error stay meaningful. 0 = always lock.")]
        [Min(0f)] [SerializeField] private float explicitTargetAimTolerance = 0.45f;

        [Tooltip("The current lock is kept while the target stays inside this multiple of the assist cone (no flicker " +
                 "between two close targets).")]
        [Range(1f, 3f)] [SerializeField] private float targetStickiness = 1.6f;

        [Tooltip("The current lock is kept while the target stays within this multiple of the assist range.")]
        [Range(1f, 2f)] [SerializeField] private float targetKeepRangeFactor = 1.1f;

        [Tooltip("Seconds a target chosen with the Cycle Target input is kept against the automatic assist.")]
        [Min(0f)] [SerializeField] private float manualLockTime = 2.5f;

        [Tooltip("Seconds between two automatic aim-assist re-evaluations (line-of-sight rays are not free).")]
        [Range(0f, 0.5f)] [SerializeField] private float targetRefreshInterval = 0.08f;

        // ================================================================== contract state

        public DodgeballPlayer Owner { get; private set; }
        public CombatProfile Profile { get; private set; } = new CombatProfile();

        public DodgeBall HeldBall { get; private set; }
        public bool HasBall => HeldBall != null;

        public bool IsCharging { get; private set; }
        public float ChargeSeconds { get; private set; }

        /// <summary>0..1 charge after the charge curve (full at CombatProfile.fullChargeTime).</summary>
        public float ChargeNormalized { get; private set; }

        /// <summary>True while the catch stance is armed.</summary>
        public bool IsCatchArmed { get; private set; }

        /// <summary>Time.time of the last Catch press (t_input).</summary>
        public float LastCatchInputTime { get; private set; } = -999f;

        /// <summary>Perfect window after multipliers (0.15 s, 0.225 s with Iron Mitts).</summary>
        public float PerfectCatchWindow => CatchTiming.ScaledPerfectWindow(PerfectWindowMultiplier, Profile.perfectCatchWindow);

        /// <summary>Set by Bear's Iron Mitts (1.5). Default 1.</summary>
        public float PerfectWindowMultiplier { get; set; } = 1f;

        /// <summary>When false the player cannot catch (Frozen, Absolute Zero victims, etc.).</summary>
        public bool CatchingBlocked { get; set; }

        /// <summary>Current soft-locked enemy (aim assist) or null.</summary>
        public DodgeballPlayer CurrentTarget { get; private set; }

        /// <summary>Time.time until which the next throw gets the perfect-catch +20% counter boost.</summary>
        public float CounterBoostUntil { get; private set; } = -1f;
        public bool HasCounterBoost => Time.time <= CounterBoostUntil;

        /// <summary>Optional pass override (Houdini's Hat Trick). Null = default lob pass.</summary>
        public IPassHandler PassHandler { get; set; }

        /// <summary>Raised when the hand takes a ball by pick-up, <see cref="GiveBall"/> or a received pass (catches raise <see cref="CatchResolved"/>).</summary>
        public event Action<DodgeBall> BallPickedUp;

        /// <summary>Raised after every launch through <see cref="LaunchBall"/> (throws, passes, ability balls) with the final params.</summary>
        public event Action<DodgeBall, ThrowParams> BallThrown;

        /// <summary>Raised when a catch attempt ends: (ball, Normal/Perfect) on a catch, (null, Miss) when the window closed empty (whiff).</summary>
        public event Action<DodgeBall, CatchQuality> CatchResolved;

        // ================================================================== additional public state

        /// <summary>Catch window actually used: <c>max(catchWindow, PerfectCatchWindow)</c> (a buffed perfect window is never truncated).</summary>
        public float EffectiveCatchWindow => CatchTiming.EffectiveCatchWindow(PerfectCatchWindow, Profile.catchWindow);

        /// <summary>Seconds left in the armed catch window (0 when not armed).</summary>
        public float CatchWindowRemaining => IsCatchArmed ? Mathf.Max(0f, _catchArmedUntil - Time.time) : 0f;

        /// <summary>True during the recovery after a whiffed catch (a new catch cannot be armed yet).</summary>
        public bool IsWhiffRecovering => Time.time < _whiffUntil;

        /// <summary>Quality of the last resolved catch attempt (Miss after a whiff).</summary>
        public CatchQuality LastCatchQuality { get; private set; }

        /// <summary>Release speed (km/h) of the last throw that was not a pass (HUD speedometer).</summary>
        public float LastThrowSpeedKmh { get; private set; }

        /// <summary>Time.time of the last launch by this controller (-inf before the first).</summary>
        public float LastThrowTime { get; private set; } = float.NegativeInfinity;

        /// <summary>Speed multiplier the counter boost applies (1 + counterBoost).</summary>
        public float CounterBoostMultiplier => 1f + counterBoost;

        /// <summary>Longest a throw can be charged (s): max(maxChargeTime, fullChargeTime).</summary>
        public float MaxChargeSeconds => Mathf.Max(0.01f, Mathf.Max(Profile.maxChargeTime, Profile.fullChargeTime));

        /// <summary>True when the throw cooldown (<see cref="CombatProfile.throwCooldown"/>) allows starting a new throw.</summary>
        public bool IsThrowReady => Time.time >= _nextThrowAt;

        /// <summary>Number of registered throw modifiers.</summary>
        public int ThrowModifierCount => _modifiers.Count;

        // ================================================================== internals

        private readonly List<IThrowModifier> _modifiers = new List<IThrowModifier>(4);
        private readonly List<IThrowModifier> _modifierScratch = new List<IThrowModifier>(4);
        private bool _modifierScratchInUse;

        private float _catchArmedUntil;
        private float _whiffUntil;
        private float _pickupLockUntil;
        private float _nextThrowAt;
        private float _manualLockUntil;
        private float _targetRefreshTimer;

        private Action<DodgeBall, BallState, BallState> _heldBallStateHandler;
        private DodgeBall _subscribedBall;

        private const int OverlapBufferSize = 16;
        private static readonly Collider[] s_overlap = new Collider[OverlapBufferSize];
        private static MethodInfo s_memberwiseClone;

        // ================================================================== setup

        public void Initialize(DodgeballPlayer owner, CombatProfile profile)
        {
            Owner = owner;
            Profile = PrepareProfile(profile);

            // Fresh transient state (a re-initialisation after a hero swap starts clean too).
            IsCharging = false;
            ChargeSeconds = 0f;
            ChargeNormalized = 0f;
            IsCatchArmed = false;
            LastCatchInputTime = -999f;
            LastCatchQuality = CatchQuality.Miss;
            CounterBoostUntil = -1f;
            CurrentTarget = null;
            _catchArmedUntil = 0f;
            _whiffUntil = 0f;
            _pickupLockUntil = 0f;
            _nextThrowAt = 0f;
            _manualLockUntil = 0f;
            _targetRefreshTimer = 0f;

            // Registered modifiers / the pass handler belong to the abilities, which (un)register themselves on (un)equip.

            // A ball kept through a re-initialisation (hero swap) moves to the new body's hand socket.
            var ball = HeldBall;
            if ((object)ball != null)
            {
                if (ball != null && ball.State == BallState.Held && ball.Holder == Owner) ball.AttachTo(Owner, HandSocket());
                else ReleaseHandReference();
            }
        }

        public void AddThrowModifier(IThrowModifier modifier)
        {
            if (modifier == null || _modifiers.Contains(modifier)) return;
            // Stable insertion by Order (lower first; equal orders keep registration order).
            int order = modifier.Order;
            int i = _modifiers.Count;
            while (i > 0 && _modifiers[i - 1].Order > order) i--;
            _modifiers.Insert(i, modifier);
        }

        public void RemoveThrowModifier(IThrowModifier modifier)
        {
            if (modifier != null) _modifiers.Remove(modifier);
        }

        // Only event bookkeeping here: the controller never runs Update/FixedUpdate (DodgeballPlayer ticks it).
        private void OnEnable()
        {
            var ball = HeldBall;
            if (ball != null) SubscribeBall(ball);
        }

        private void OnDisable() => UnsubscribeBall();

        private void OnDestroy() => UnsubscribeBall();

        // ================================================================== picking up

        /// <summary>Picks up <paramref name="ball"/> if allowed (range, state, hands free). Publishes BallPickedUpEvent.</summary>
        /// <remarks>
        /// Allowed when: the player can act, the hands are empty, <see cref="DodgeBall.CanBePickedUpBy"/> (a loose match ball
        /// or a ball in Chrono's stasis) and the ball is within <see cref="CombatProfile.manualPickupRadius"/> (planar) and a
        /// reachable height band around the feet.
        /// </remarks>
        public bool TryPickup(DodgeBall ball)
        {
            if (ball == null || Owner == null || !Owner.IsInitialized) return false;
            if (HasBall || !Owner.CanAct) return false;
            if (!ball.CanBePickedUpBy(Owner)) return false;
            if (!IsWithinPickupReach(ball, Profile.manualPickupRadius)) return false;
            if (!AcceptBall(ball)) return false;

            AnnouncePickup(ball);
            return true;
        }

        /// <summary>Picks up the nearest reachable free ball (manual pick-up input).</summary>
        public bool TryPickupNearest()
        {
            if (Owner == null || HasBall || !Owner.CanAct) return false;
            var ball = FindNearestPickableBall(Profile.manualPickupRadius);
            return ball != null && TryPickup(ball);
        }

        /// <summary>Instantly puts <paramref name="ball"/> in this player's hand regardless of distance (passes, teleports, abilities).</summary>
        /// <remarks>
        /// Works from any ball state (free, live, stasis, held by someone else - that holder's hand is emptied). A ball
        /// already held here is released first (small toss). Refused only for pooled balls and for a player in the middle of
        /// being eliminated (check <see cref="HeldBall"/> afterwards). Publishes <see cref="BallPickedUpEvent"/>.
        /// No catch rewards and no rally change: callers that model a catch publish their own <see cref="BallCaughtEvent"/>.
        /// </remarks>
        public void GiveBall(DodgeBall ball)
        {
            if (ball == null || Owner == null) return;
            if (HeldBall == ball && ball.State == BallState.Held && ball.Holder == Owner) return;
            if (ball.IsPooled) return;
            if (IsBeingEliminated()) return;

            if (HasBall) DropBall(SwapDropVelocity());

            // Empty the previous holder's hand right away (their StateChanged listener would do it too).
            var previousHolder = ball.Holder;
            if (previousHolder != null && previousHolder != Owner && previousHolder.Combat != null)
                previousHolder.Combat.ForgetBallIfHeld(ball);

            if (!AcceptBall(ball)) return;
            AnnouncePickup(ball);
        }

        /// <summary>Nearest ball this player may pick up within <paramref name="radius"/> (planar) and the reach height band, or null. Allocation-free.</summary>
        public DodgeBall FindNearestPickableBall(float radius)
        {
            if (Owner == null || radius <= 0f) return null;
            Vector3 feet = Owner.Position;
            DodgeBall best = null;
            float bestSqr = float.PositiveInfinity;

            var manager = BallManager.Instance;
            if (manager != null)
            {
                var balls = manager.ActiveBalls;
                for (int i = 0; i < balls.Count; i++)
                    Consider(balls[i], radius, feet, ref best, ref bestSqr);
                return best;
            }

            // No BallManager (sandbox): physics query on the ball layer around the body.
            float queryRadius = Mathf.Max(radius, pickupReachAbove) + GameConstants.BallRadius;
            int count = Physics.OverlapSphereNonAlloc(feet + Vector3.up * (pickupReachAbove * 0.5f), queryRadius, s_overlap,
                GameLayers.BallMask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < count; i++)
            {
                var col = s_overlap[i];
                s_overlap[i] = null;
                if (col == null) continue;
                var rb = col.attachedRigidbody;
                if (rb != null && rb.TryGetComponent(out DodgeBall ball)) Consider(ball, radius, feet, ref best, ref bestSqr);
            }
            return best;
        }

        // ================================================================== throwing

        /// <summary>Starts charging a throw (requires a ball). Publishes ThrowChargeStartedEvent.</summary>
        /// <remarks>Refused without a ball, while the player cannot act, or within <see cref="CombatProfile.throwCooldown"/> of the last throw.</remarks>
        public bool BeginCharge()
        {
            if (IsCharging) return true;
            if (Owner == null || !HasBall || !Owner.CanAct) return false;
            if (Time.time < _nextThrowAt) return false;

            if (IsCatchArmed) CancelCatch();
            IsCharging = true;
            ChargeSeconds = 0f;
            ChargeNormalized = EvaluateCharge(0f);

            GameEvents.Publish(new ThrowChargeStartedEvent { Player = Owner, Ball = HeldBall });
            return true;
        }

        /// <summary>Releases the charged throw at the current target / aim. Returns the launched ball or null.</summary>
        public DodgeBall ReleaseThrow()
        {
            if (!IsCharging) return null;
            float seconds = ChargeSeconds;
            CancelCharge();

            var ball = HeldBall;
            if (ball == null || Owner == null || !Owner.CanAct) return null;

            var p = BuildThrowParams(seconds, ResolveThrowTarget(out bool explicitTarget), false);
            if (explicitTarget) HonourIntentAim(ref p);
            LaunchBall(ball, p);
            return HeldBall == ball ? null : ball; // still in hand = the launch was refused
        }

        public void CancelCharge()
        {
            IsCharging = false;
            ChargeSeconds = 0f;
            ChargeNormalized = 0f;
        }

        /// <summary>
        /// Throws immediately with an explicit charge (AI, abilities that force a throw). Uses <paramref name="target"/>
        /// if given, otherwise the current aim / soft lock.
        /// </summary>
        /// <remarks>
        /// <paramref name="chargeNormalized"/> is the 0..1 charge after the curve (inverted to the hold time that produces
        /// it); values above 1 mean holding past a full charge (<c>fullChargeTime * value</c>, Rayne's Overcharge). A given
        /// target that can no longer be hit is ignored (the throw goes along the aim). Not gated by the throw cooldown.
        /// </remarks>
        public DodgeBall ThrowImmediate(float chargeNormalized, DodgeballPlayer target = null)
        {
            var ball = HeldBall;
            if (ball == null || Owner == null || !Owner.CanAct) return null;

            float seconds = ChargeSecondsFor(chargeNormalized);
            CancelCharge();

            bool explicitTarget = false;
            DodgeballPlayer throwTarget = target != null
                ? (IsValidThrowTarget(target) ? target : null)
                : ResolveThrowTarget(out explicitTarget);
            var p = BuildThrowParams(seconds, throwTarget, false);
            if (explicitTarget) HonourIntentAim(ref p);
            LaunchBall(ball, p);
            return HeldBall == ball ? null : ball;
        }

        /// <summary>
        /// Builds the throw description for the held ball (or an ability ball when <paramref name="isAbilityThrow"/>),
        /// applies charge, counter boost and all registered IThrowModifiers.
        /// </summary>
        /// <remarks>
        /// Side-effect free (may be evaluated for previews): the counter boost is only consumed by <see cref="LaunchBall"/>.
        /// Ability throws start with rally 0 and the thrower's hand as origin; callers (AbilityUtil, turrets) override
        /// style, payload, gravity or origin afterwards.
        /// </remarks>
        public ThrowParams BuildThrowParams(float chargeSeconds, DodgeballPlayer target, bool isAbilityThrow)
        {
            var profile = Profile;
            float seconds = Mathf.Clamp(float.IsNaN(chargeSeconds) ? 0f : chargeSeconds, 0f, MaxChargeSeconds);
            float charge = EvaluateCharge(seconds);

            Vector3 origin = GetThrowOrigin();
            ResolveAim(origin, out Vector3 aimDirection, out Vector3 aimPoint);

            var held = HeldBall;
            var p = new ThrowParams
            {
                Thrower = Owner,
                Target = IsValidThrowTarget(target) ? target : null,
                Origin = origin,
                AimDirection = aimDirection,
                AimPoint = aimPoint,
                BaseSpeed = Mathf.Max(0f, profile.baseThrowSpeedKmh) * GameConstants.KmhToMs,
                ChargeNormalized = charge,
                ChargeSeconds = seconds,
                SpeedMultiplier = Mathf.Lerp(Mathf.Clamp01(profile.minChargeMultiplier), 1f, charge),
                RadiusMultiplier = 1f,
                RallyCount = !isAbilityThrow && held != null ? held.RallyCount : 0,
                GravityScale = Mathf.Max(0f, profile.thrownGravityScale),
                Unblockable = false,
                Pierce = false,
                IsAbilityThrow = isAbilityThrow,
                IsPass = false,
                IsCounterThrow = false,
                RevealsThrower = false,
                Style = BallStyle.Standard,
                Payload = null,
            };

            // Perfect Catch reward: the next throw is 20 % faster (consumed on launch).
            if (HasCounterBoost)
            {
                p.IsCounterThrow = true;
                p.SpeedMultiplier *= CounterBoostMultiplier;
            }

            ApplyModifiers(ref p);
            return p;
        }

        /// <summary>
        /// Launches <paramref name="ball"/> (held or ability ball) with <paramref name="throwParams"/>: solves the velocity,
        /// calls DodgeBall.Launch, notifies modifiers and publishes BallThrownEvent. Used by abilities too.
        /// </summary>
        /// <remarks>
        /// When <paramref name="ball"/> is the held ball it leaves from where the hand really is (no visual jump) and the hand
        /// is emptied first; any other ball (ability projectile, turret shot) keeps <see cref="ThrowParams.Origin"/> as given
        /// (a zero origin means "the hand"). A null <see cref="ThrowParams.Thrower"/> becomes this player. Consumes the counter
        /// boost when <see cref="ThrowParams.IsCounterThrow"/>, starts the throw cooldown for hand throws, then runs every
        /// modifier's <see cref="IThrowModifier.OnThrowCommitted"/>, <see cref="BallThrown"/> and publishes
        /// <see cref="BallThrownEvent"/> (after the ball is live, so listeners can predict its flight).
        /// </remarks>
        public void LaunchBall(DodgeBall ball, ThrowParams throwParams)
        {
            if (ball == null || Owner == null) return;
            if (ball.IsPooled || !ball.gameObject.activeInHierarchy)
            {
                Debug.LogWarning($"[Dodgeball Ultra] {name}: cannot launch ball {ball.BallId} (pooled or inactive).", this);
                return;
            }

            var p = throwParams;
            bool fromHand = ball == HeldBall;
            if (p.Thrower == null) p.Thrower = Owner;

            // Release point.
            if (!ThrowSolver.IsFinite(p.Origin) || p.Origin == Vector3.zero) p.Origin = GetThrowOrigin();
            if (fromHand)
            {
                Vector3 inHand = ball.transform.position;
                if (ThrowSolver.IsFinite(inHand) && (inHand - Owner.ChestPosition).sqrMagnitude <= handSocketMaxDistance * handSocketMaxDistance)
                    p.Origin = inHand;
                if (keepReleaseInsideCourt) p.Origin = KeepInsideCourt(p.Origin);
            }

            if (!ThrowSolver.IsFinite(p.AimDirection) || p.AimDirection.sqrMagnitude < 1e-8f) p.AimDirection = Owner.Forward;
            p.AimDirection = p.AimDirection.normalized;

            Vector3 velocity = ThrowSolver.Solve(in p, out _);

            // The hand lets go before the ball changes state (our StateChanged listener must not see our own throw).
            if (fromHand) ReleaseHandReference();
            CancelCharge();

            ball.Launch(in p, velocity);

            float now = Time.time;
            if (p.IsCounterThrow) CounterBoostUntil = -1f; // the +20 % is spent
            if (fromHand)
            {
                _pickupLockUntil = now + pickupLockAfterThrow;
                _nextThrowAt = now + Mathf.Max(0f, Profile.throwCooldown);
            }
            LastThrowTime = now;

            Vector3 launched = ball.IsLive ? ball.Velocity : velocity;
            float speedKmh = launched.magnitude * GameConstants.MsToKmh;
            if (!p.IsPass) LastThrowSpeedKmh = speedKmh;

            NotifyThrowCommitted(in p, ball);

            var handler = BallThrown;
            if (handler != null)
            {
                try { handler(ball, p); }
                catch (Exception e) { Debug.LogException(e, this); }
            }

            GameEvents.Publish(new BallThrownEvent
            {
                Ball = ball,
                Thrower = p.Thrower,
                Target = p.Target,
                Origin = p.Origin,
                Velocity = launched,
                SpeedKmh = speedKmh,
                RallyCount = p.RallyCount,
                ChargeNormalized = p.ChargeNormalized,
                IsAbilityThrow = p.IsAbilityThrow,
                IsPass = p.IsPass,
                IsCounterThrow = p.IsCounterThrow,
            });
        }

        /// <summary>World release point (right-hand socket or chest fallback).</summary>
        public Vector3 GetThrowOrigin()
        {
            if (Owner == null) return transform.position;
            Vector3 chest = Owner.ChestPosition;

            var socket = HandSocket();
            if (socket != null)
            {
                Vector3 hand = socket.position;
                if (ThrowSolver.IsFinite(hand) && (hand - chest).sqrMagnitude <= handSocketMaxDistance * handSocketMaxDistance)
                    return hand;
            }

            Vector3 forward = Owner.Forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            return chest + forward * throwOriginForward + right * throwOriginSide + Vector3.up * throwOriginUp;
        }

        /// <summary>Grants the perfect-catch counter boost for CombatProfile.counterBoostDuration.</summary>
        public void GrantCounterBoost()
        {
            CounterBoostUntil = Time.time + Mathf.Max(0f, Profile.counterBoostDuration);
        }

        /// <summary>0..1 charge produced by holding a throw for <paramref name="seconds"/> (the charge curve over fullChargeTime).</summary>
        public float EvaluateCharge(float seconds)
        {
            float full = Profile.fullChargeTime;
            if (full <= 1e-4f) return 1f;
            float x = Mathf.Clamp01(seconds / full);
            var curve = Profile.chargeCurve;
            if (curve == null || curve.length == 0) return x;
            return Mathf.Clamp01(curve.Evaluate(x));
        }

        /// <summary>
        /// Hold time (s) that produces <paramref name="chargeNormalized"/> (inverse of <see cref="EvaluateCharge"/>, assuming a
        /// non-decreasing curve). Values above 1 map linearly past a full charge (<c>fullChargeTime * value</c>), capped at
        /// <see cref="MaxChargeSeconds"/>.
        /// </summary>
        public float ChargeSecondsFor(float chargeNormalized)
        {
            float full = Mathf.Max(0f, Profile.fullChargeTime);
            if (float.IsNaN(chargeNormalized) || chargeNormalized <= 0f) return 0f;
            if (chargeNormalized >= 1f) return Mathf.Min(full * chargeNormalized, MaxChargeSeconds);

            var curve = Profile.chargeCurve;
            if (curve == null || curve.length == 0) return full * chargeNormalized;

            // Bisection on the curve (monotonic charge curves); 16 steps = 1/65536 of the full charge time.
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 16; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (curve.Evaluate(mid) >= chargeNormalized) hi = mid;
                else lo = mid;
            }
            return hi * full;
        }

        // ================================================================== catching

        /// <summary>Catch button: arms the catch stance (records t_input). Publishes CatchAttemptEvent.</summary>
        /// <remarks>
        /// Refused with a ball in hand, while <see cref="CatchingBlocked"/> / Frozen, when the player cannot act, or during
        /// the whiff recovery. Pressing again while armed keeps the original t_input. Cancels a running charge.
        /// </remarks>
        public bool TryStartCatch()
        {
            if (IsCatchArmed) return true;
            if (Owner == null || HasBall || CatchingBlocked || !Owner.CanAct) return false;
            if (IsFrozen()) return false;

            float now = Time.time;
            if (now < _whiffUntil) return false;

            if (IsCharging) CancelCharge();
            IsCatchArmed = true;
            LastCatchInputTime = now;
            _catchArmedUntil = now + EffectiveCatchWindow;

            GameEvents.Publish(new CatchAttemptEvent { Player = Owner });
            return true;
        }

        /// <summary>
        /// Called by a live ball the moment it reaches this player's catch zone/body.
        /// Returns the catch quality (Miss means the ball should be resolved as a hit). Handles rewards and events on success.
        /// </summary>
        /// <remarks>
        /// <para>Miss (nothing changes, the stance stays armed) when: the stance is not armed, the hands are full, catching is
        /// blocked / Frozen / the player cannot act, the ball is not a live enemy ball or is unblockable, the contact is
        /// outside the frontal cone or height band, or <c>CatchTiming.Classify(impactTime - t_input)</c> is Miss.</para>
        /// <para>Success: <c>payload.OnCaught</c> (before the flight ends), the ball goes to the hand with
        /// <c>RallyMath.NextRallyCount</c>, the rewards are paid (see class docs), <see cref="CatchResolved"/> and
        /// <see cref="BallCaughtEvent"/> (Juice pipeline, HUD, Match) follow.</para>
        /// </remarks>
        public CatchQuality TryResolveCatch(DodgeBall ball, Vector3 contactPoint, float impactTime)
        {
            if (ball == null || Owner == null) return CatchQuality.Miss;
            if (!IsCatchArmed || HasBall || CatchingBlocked || !Owner.CanAct || IsFrozen()) return CatchQuality.Miss;
            if (ball.State != BallState.Live || ball.Unblockable || ball.Holder != null) return CatchQuality.Miss;

            var thrower = ball.LastThrower;
            if (thrower != null && (thrower == Owner || PlayerRegistry.AreTeammates(thrower, Owner))) return CatchQuality.Miss;

            Vector3 point = ThrowSolver.IsFinite(contactPoint) && contactPoint != Vector3.zero ? contactPoint : ball.FlightPosition;
            if (!IsInCatchCone(point)) return CatchQuality.Miss;

            float secondsBeforeImpact = impactTime - LastCatchInputTime;
            var quality = CatchTiming.Classify(secondsBeforeImpact, PerfectCatchWindow, EffectiveCatchWindow);
            if (quality == CatchQuality.Miss) return CatchQuality.Miss;

            // ---- success: snapshot the throw before the ball changes hands
            float speedKmh = ball.SpeedKmh;
            int rally = RallyMath.NextRallyCount(ball.RallyCount);
            var payload = ball.Payload;

            IsCatchArmed = false;
            _catchArmedUntil = 0f;
            CancelCharge();

            // The payload hears about the catch while the flight is still active (a caught Meteor never detonates).
            if (payload != null)
            {
                try { payload.OnCaught(ball, Owner, quality); }
                catch (Exception e) { Debug.LogException(e, this); }
            }

            if (!AcceptBall(ball)) return CatchQuality.Miss; // the payload moved the ball elsewhere: resolve as usual
            ball.SetRallyCount(rally);
            LastCatchQuality = quality;

            // ---- rewards
            bool livePlay = CombatRuleValues.IsLivePlay;
            var abilities = Owner.Abilities;
            if (quality == CatchQuality.Perfect)
            {
                if (livePlay)
                {
                    // Revive the teammate who has been in the outfield the longest (the match de-duplicates its own call).
                    var match = MatchManager.Instance;
                    if (match != null)
                    {
                        try { match.ReviveOneOutfieldTeammate(Owner.Team, RevivalCause.PerfectCatch, Owner); }
                        catch (Exception e) { Debug.LogException(e, this); }
                    }
                    if (abilities != null) abilities.AddUltimateCharge(CombatRuleValues.UltGainOnPerfectCatch, UltGainReason.PerfectCatch);
                }
                GrantCounterBoost();
            }
            else if (livePlay && !CombatRuleValues.MatchOwnsPlayRewards && abilities != null)
            {
                // Sandbox only: in a match the MatchManager grants the normal-catch ultimate from BallCaughtEvent.
                abilities.AddUltimateCharge(CombatRuleValues.UltGainOnCatch, UltGainReason.Catch);
            }
            // MatchRules.catchEliminatesThrower is applied by the MatchManager (from BallCaughtEvent): rules only exist there.

            // ---- presentation & events
            var visual = Owner.Visual;
            if (visual != null && visual.AnimatorDriver != null) visual.AnimatorDriver.TriggerCatch();

            RaiseCatchResolved(ball, quality);
            GameEvents.Publish(new BallCaughtEvent
            {
                Ball = ball,
                Catcher = Owner,
                Thrower = thrower,
                Quality = quality,
                SecondsBeforeImpact = secondsBeforeImpact,
                Point = point,
                SpeedKmh = speedKmh,
                RallyCount = rally,
                IsLocalPlayerInvolved = Owner.IsLocalPlayer || (thrower != null && thrower.IsLocalPlayer),
            });
            return quality;
        }

        /// <summary>Disarms the catch stance without a whiff penalty (stun, freeze, elimination).</summary>
        public void CancelCatch()
        {
            IsCatchArmed = false;
            _catchArmedUntil = 0f;
        }

        /// <summary>True if a ball at <paramref name="ballPosition"/> is inside the frontal catch cone.</summary>
        /// <remarks>
        /// Planar bearing within <see cref="CombatProfile.catchConeAngle"/> of the body's facing (a ball already between the
        /// hands counts whatever its bearing) and a height between <c>minCatchHeight</c> above the feet and
        /// <c>catchHeightAboveHead</c> above the head.
        /// </remarks>
        public bool IsInCatchCone(Vector3 ballPosition)
        {
            if (Owner == null || !ThrowSolver.IsFinite(ballPosition)) return false;
            Vector3 feet = Owner.Position;

            float height = Owner.Capsule != null ? Owner.Capsule.height : TrajectoryPredictor.FallbackBodyHeight;
            float h = ballPosition.y - feet.y;
            if (h < minCatchHeight || h > height + catchHeightAboveHead) return false;

            Vector3 planar = new Vector3(ballPosition.x - feet.x, 0f, ballPosition.z - feet.z);
            if (planar.sqrMagnitude <= catchConeInnerRadius * catchConeInnerRadius) return true;
            return Vector3.Angle(Owner.Forward, planar) <= Mathf.Clamp(Profile.catchConeAngle, 0f, 180f);
        }

        // ================================================================== passing

        /// <summary>Passes the held ball to the nearest teammate (Houdini's Hat Trick teleports it instead).</summary>
        /// <remarks>
        /// Receiver: <see cref="PlayerIntent.DesiredTarget"/> when it is a teammate who can take the ball (bots pick their
        /// receiver), else the nearest teammate who can take the ball now (hands free, able to act, not frozen; within
        /// <see cref="passMaxRange"/> when set). The <see cref="PassHandler"/> gets the first say and owns the hand-off (and its
        /// <see cref="BallPassedEvent"/>) when it returns true. Otherwise a lob is thrown: speed enough for a comfortable arc
        /// (<c>passArcFactor</c> x the 45-degree minimum, clamped to [passSpeedKmh, passMaxSpeed]), full gravity, lead-aimed at
        /// the receiver's chest, the rally count preserved but never boosted. Enemies can intercept it with a catch.
        /// Publishes <see cref="BallThrownEvent"/> (IsPass) and <see cref="BallPassedEvent"/>.
        /// </remarks>
        public bool TryPass()
        {
            var ball = HeldBall;
            if (ball == null || Owner == null || !Owner.CanAct) return false;

            var to = FindPassReceiver();
            if (to == null) return false;

            var handler = PassHandler;
            if (handler != null)
            {
                bool handled = false;
                try { handled = handler.TryHandlePass(ball, Owner, to); }
                catch (Exception e) { Debug.LogException(e, this); }

                if (handled)
                {
                    // The handler moved the ball (our StateChanged listener normally emptied the hand already).
                    if (HeldBall == ball && (ball.State != BallState.Held || ball.Holder != Owner)) ReleaseHandReference();
                    CancelCharge();
                    return true;
                }
                if (HeldBall != ball) return false; // declined, but the ball left the hand anyway: nothing left to lob
            }

            var p = BuildPassParams(ball, to);
            ApplyModifiers(ref p); // e.g. Gale: a pass from stealth may reveal her (modifiers check IsPass themselves)
            LaunchBall(ball, p);
            if (HeldBall == ball) return false; // the launch was refused

            GameEvents.Publish(new BallPassedEvent { Ball = ball, From = Owner, To = to, Teleported = false });
            return true;
        }

        /// <summary>Receives a pass ball from a teammate.</summary>
        /// <remarks>
        /// Called by a live pass the moment it reaches this player (the ball has already checked: teammate, hands free,
        /// able to act). Takes the ball with its rally count unchanged and publishes <see cref="BallPickedUpEvent"/>.
        /// </remarks>
        public void ReceivePass(DodgeBall ball, DodgeballPlayer from)
        {
            if (ball == null || Owner == null || HasBall) return;
            if (ball.IsPooled || ball.State == BallState.Despawned) return;
            if (ball.Holder != null && ball.Holder != Owner) return;

            int rally = ball.RallyCount;
            CancelCatch();
            if (!AcceptBall(ball)) return;
            ball.SetRallyCount(rally);

            var visual = Owner.Visual;
            if (visual != null && visual.AnimatorDriver != null) visual.AnimatorDriver.TriggerCatch();
            AnnouncePickup(ball);
        }

        /// <summary>Drops the held ball with <paramref name="velocity"/>. Returns it (or null).</summary>
        /// <remarks>Cancels a running charge; the rally count is kept (it resets when the ball touches the floor). Starts a short auto-pick-up lock.</remarks>
        public DodgeBall DropBall(Vector3 velocity)
        {
            var ball = HeldBall;
            if ((object)ball == null) return null;

            CancelCharge();
            ReleaseHandReference();
            if (ball == null) return null; // destroyed meanwhile

            if (ball.State == BallState.Held && ball.Holder == Owner)
                ball.MakeFree(ThrowSolver.IsFinite(velocity) ? velocity : Vector3.zero, false);

            _pickupLockUntil = Time.time + pickupLockAfterDrop;
            return ball;
        }

        // ================================================================== round / tick

        /// <summary>Round reset: drop ball, cancel charge/catch, clear boosts.</summary>
        public void ResetForRound()
        {
            CancelCharge();
            CancelCatch();

            var ball = HeldBall;
            ReleaseHandReference();
            // The BallManager re-racks every ball on the centre line; just make sure ours is not glued to the hand.
            if (ball != null && ball.State == BallState.Held && ball.Holder == Owner) ball.MakeFree(Vector3.zero, true);

            CounterBoostUntil = -1f;
            LastCatchInputTime = -999f;
            LastCatchQuality = CatchQuality.Miss;
            CurrentTarget = null;
            _whiffUntil = 0f;
            _pickupLockUntil = 0f;
            _nextThrowAt = 0f;
            _manualLockUntil = 0f;
            _targetRefreshTimer = 0f;
        }

        /// <summary>
        /// Per-frame update (scaled delta), called by DodgeballPlayer after the state machine:
        /// hand bookkeeping -&gt; catch window (whiff) -&gt; aim assist -&gt; charge accumulation (auto-release at the hold
        /// limit) -&gt; automatic pick-up of a ball at the feet. Pass / manual pick-up intents are handled by DodgeballPlayer.
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (Owner == null) return;
            float now = Time.time;

            // 1. A ball that left the hand without telling us (destroyed, stolen) is forgotten.
            ValidateHeldBall();

            // 2. Catch window.
            if (IsCatchArmed)
            {
                if (CatchingBlocked || HasBall || IsFrozen()) CancelCatch();
                else if (now > _catchArmedUntil) Whiff(now);
            }

            // 3. Stunned / incapacitated / input locked: no charging, no aim assist, no pick-ups.
            if (!Owner.CanAct)
            {
                if (IsCharging) CancelCharge();
                return;
            }

            // 4. Aim assist.
            UpdateTargeting(Mathf.Max(0f, deltaTime), now);

            // 5. Charge.
            if (IsCharging)
            {
                if (!HasBall)
                {
                    CancelCharge();
                }
                else
                {
                    float cap = MaxChargeSeconds;
                    ChargeSeconds = Mathf.Min(ChargeSeconds + Mathf.Max(0f, deltaTime), cap);
                    ChargeNormalized = EvaluateCharge(ChargeSeconds);
                    if (autoReleaseAtMaxCharge && ChargeSeconds >= cap - 1e-4f && CombatRuleValues.IsLivePlay) ReleaseThrow();
                }
            }

            // 6. Automatic pick-up of a loose ball at the feet.
            if (!HasBall && now >= _pickupLockUntil)
            {
                var ball = FindNearestPickableBall(Profile.autoPickupRadius);
                if (ball != null) TryPickup(ball);
            }
        }

        // ================================================================== internals: hands

        /// <summary>Attaches <paramref name="ball"/> to the hand and starts following it. False if the ball did not end up held here.</summary>
        private bool AcceptBall(DodgeBall ball)
        {
            if (ball == null || Owner == null) return false;
            if ((object)HeldBall != null && HeldBall != ball) ReleaseHandReference();

            ball.AttachTo(Owner, HandSocket());
            if (ball.State != BallState.Held || ball.Holder != Owner) return false;

            HeldBall = ball;
            SubscribeBall(ball);
            CancelCatch(); // hands are full now
            return true;
        }

        /// <summary>Forgets the held ball without touching it (it was thrown, passed, taken or destroyed).</summary>
        private void ReleaseHandReference()
        {
            UnsubscribeBall();
            HeldBall = null;
            if (IsCharging) CancelCharge();
        }

        /// <summary>Another controller took <paramref name="ball"/> (GiveBall): empty this hand if it held it.</summary>
        internal void ForgetBallIfHeld(DodgeBall ball)
        {
            if (ball != null && ReferenceEquals(HeldBall, ball)) ReleaseHandReference();
        }

        private void ValidateHeldBall()
        {
            var ball = HeldBall;
            if ((object)ball == null) return;
            if (ball == null || ball.State != BallState.Held || ball.Holder != Owner) ReleaseHandReference();
        }

        private void SubscribeBall(DodgeBall ball)
        {
            if (ReferenceEquals(_subscribedBall, ball)) return;
            UnsubscribeBall();
            if (_heldBallStateHandler == null) _heldBallStateHandler = OnHeldBallStateChanged;
            ball.StateChanged += _heldBallStateHandler;
            _subscribedBall = ball;
        }

        private void UnsubscribeBall()
        {
            var ball = _subscribedBall;
            _subscribedBall = null;
            // (object) check: a destroyed ball still has its managed event field, unsubscribing is safe.
            if ((object)ball != null && _heldBallStateHandler != null) ball.StateChanged -= _heldBallStateHandler;
        }

        private void OnHeldBallStateChanged(DodgeBall ball, BallState previous, BallState current)
        {
            if (!ReferenceEquals(ball, HeldBall))
            {
                if (ReferenceEquals(ball, _subscribedBall)) UnsubscribeBall();
                return;
            }
            if (current != BallState.Held || ball.Holder != Owner) ReleaseHandReference();
        }

        private void AnnouncePickup(DodgeBall ball)
        {
            var handler = BallPickedUp;
            if (handler != null)
            {
                try { handler(ball); }
                catch (Exception e) { Debug.LogException(e, this); }
            }
            GameEvents.Publish(new BallPickedUpEvent { Ball = ball, Player = Owner });
        }

        private Transform HandSocket()
        {
            var visual = Owner != null ? Owner.Visual : null;
            return visual != null ? visual.RightHandSocket : null;
        }

        private Vector3 SwapDropVelocity()
        {
            Vector3 v = Owner.Forward * swapDropForwardSpeed + Vector3.up * swapDropUpSpeed;
            return v + Owner.Velocity * 0.5f;
        }

        private void Consider(DodgeBall ball, float radius, Vector3 feet, ref DodgeBall best, ref float bestSqr)
        {
            if (ball == null || !ball.CanBePickedUpBy(Owner)) return;
            if (!IsWithinPickupReach(ball, radius, feet, out float planarSqr)) return;
            if (planarSqr < bestSqr)
            {
                bestSqr = planarSqr;
                best = ball;
            }
        }

        private bool IsWithinPickupReach(DodgeBall ball, float radius) =>
            IsWithinPickupReach(ball, radius, Owner.Position, out _);

        private bool IsWithinPickupReach(DodgeBall ball, float radius, Vector3 feet, out float planarSqr)
        {
            Vector3 position = ball.FlightPosition;
            float dx = position.x - feet.x, dz = position.z - feet.z;
            planarSqr = dx * dx + dz * dz;
            float dy = position.y - feet.y;
            if (dy < -pickupReachBelow || dy > pickupReachAbove) return false;
            float reach = Mathf.Max(0f, radius) + ball.Radius;
            return planarSqr <= reach * reach;
        }

        // ================================================================== internals: throwing

        /// <summary>
        /// Intent target (bots) when valid, else the soft lock, else null (throw at the aim point).
        /// <paramref name="fromIntent"/> tells whether the target came from <see cref="PlayerIntent.DesiredTarget"/>.
        /// </summary>
        private DodgeballPlayer ResolveThrowTarget(out bool fromIntent)
        {
            fromIntent = false;
            if (Owner == null) return null;
            var desired = Owner.Intent.DesiredTarget;
            if (IsValidThrowTarget(desired))
            {
                fromIntent = true;
                return desired;
            }
            return IsValidThrowTarget(CurrentTarget) ? CurrentTarget : null;
        }

        /// <summary>
        /// An explicit intent target is only lead-solved while the intent really aims at it (see
        /// <see cref="explicitTargetAimTolerance"/>); otherwise the throw goes to the intent's aim point, lock dropped.
        /// </summary>
        private void HonourIntentAim(ref ThrowParams p)
        {
            var target = p.Target;
            if (target == null || explicitTargetAimTolerance <= 0f) return;
            Vector3 aim = p.AimPoint;
            if (!ThrowSolver.IsFinite(aim) || aim == Vector3.zero) return;

            float gravity = GameConstants.Gravity * Mathf.Max(0f, p.GravityScale);
            Vector3 lead = ThrowSolver.PredictInterceptPoint(p.Origin, target, p.FinalSpeed, gravity, ThrowSolver.LeadIterations);
            if ((aim - lead).sqrMagnitude > explicitTargetAimTolerance * explicitTargetAimTolerance) p.Target = null;
        }

        /// <summary>Somebody this player may throw at: another player who can be hit right now and is not a teammate.</summary>
        private bool IsValidThrowTarget(DodgeballPlayer target) =>
            target != null && Owner != null && target != Owner && target.IsTargetable && !PlayerRegistry.AreTeammates(Owner, target);

        /// <summary>Normalised aim direction and aim point from the intent (falls back to the body facing).</summary>
        private void ResolveAim(Vector3 origin, out Vector3 direction, out Vector3 point)
        {
            var intent = Owner != null ? Owner.Intent : default;
            direction = intent.AimDirection;
            if (!ThrowSolver.IsFinite(direction) || direction.sqrMagnitude < 1e-8f)
                direction = Owner != null ? Owner.Forward : transform.forward;
            direction.Normalize();

            point = intent.AimPoint;
            if (!ThrowSolver.IsFinite(point) || point == Vector3.zero) point = origin + direction * ThrowSolver.StraightThrowReferenceDistance;
        }

        private void ApplyModifiers(ref ThrowParams p)
        {
            if (_modifiers.Count == 0) return;
            var list = BeginModifierIteration(out bool pooled);
            for (int i = 0; i < list.Count; i++)
            {
                var modifier = list[i];
                if (modifier == null) continue;
                try { modifier.ModifyThrow(ref p); }
                catch (Exception e) { Debug.LogException(e, this); }
            }
            EndModifierIteration(list, pooled);
        }

        private void NotifyThrowCommitted(in ThrowParams p, DodgeBall ball)
        {
            if (_modifiers.Count == 0) return;
            var list = BeginModifierIteration(out bool pooled);
            for (int i = 0; i < list.Count; i++)
            {
                var modifier = list[i];
                if (modifier == null) continue;
                try { modifier.OnThrowCommitted(in p, ball); }
                catch (Exception e) { Debug.LogException(e, this); }
            }
            EndModifierIteration(list, pooled);
        }

        /// <summary>Snapshot of the modifiers (they may unregister themselves while running). Re-entrant calls get their own copy.</summary>
        private List<IThrowModifier> BeginModifierIteration(out bool pooled)
        {
            if (_modifierScratchInUse)
            {
                pooled = false;
                return new List<IThrowModifier>(_modifiers); // re-entrant (a modifier building a throw): rare, allowed to allocate
            }
            pooled = true;
            _modifierScratchInUse = true;
            _modifierScratch.Clear();
            _modifierScratch.AddRange(_modifiers);
            return _modifierScratch;
        }

        private void EndModifierIteration(List<IThrowModifier> list, bool pooled)
        {
            if (!pooled) return;
            list.Clear();
            _modifierScratchInUse = false;
        }

        /// <summary>Moves a release point that lies beyond court geometry (hand through a wall) back to this side of it.</summary>
        private Vector3 KeepInsideCourt(Vector3 origin)
        {
            Vector3 chest = Owner.ChestPosition;
            Vector3 d = origin - chest;
            float distance = d.magnitude;
            if (distance < 1e-3f) return origin;
            if (Physics.Raycast(chest, d / distance, out RaycastHit hit, distance + GameConstants.BallRadius, GameLayers.CourtMask,
                    QueryTriggerInteraction.Ignore))
            {
                return hit.point + hit.normal * (GameConstants.BallRadius + 0.02f);
            }
            return origin;
        }

        // ================================================================== internals: passing

        /// <summary>
        /// Pass receiver: the intent's explicit target when it is a teammate who can receive (bots choose their receiver),
        /// otherwise the nearest teammate who can receive a pass right now (or null).
        /// </summary>
        private DodgeballPlayer FindPassReceiver()
        {
            float maxSqr = passMaxRange > 0f ? passMaxRange * passMaxRange : float.PositiveInfinity;
            Vector3 from = Owner.Position;

            var chosen = Owner.Intent.DesiredTarget;
            if (CanReceivePass(chosen))
            {
                float cx = chosen.Position.x - from.x, cz = chosen.Position.z - from.z;
                if (cx * cx + cz * cz <= maxSqr) return chosen;
            }

            DodgeballPlayer best = null;
            float bestSqr = maxSqr;
            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (!CanReceivePass(p)) continue;
                float dx = p.Position.x - from.x, dz = p.Position.z - from.z;
                float sqr = dx * dx + dz * dz;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = p;
                }
            }
            return best;
        }

        private bool CanReceivePass(DodgeballPlayer p)
        {
            if (p == null || p == Owner || !p.IsInitialized || !p.isActiveAndEnabled) return false;
            if (!PlayerRegistry.AreTeammates(Owner, p)) return false;
            if (p.Combat == null || p.Combat.HasBall) return false;
            if (p.StateMachine != null && !p.StateMachine.CanAct) return false;
            return p.Status == null || !p.Status.Has(StatusEffectType.Frozen);
        }

        /// <summary>A soft, catchable lob at <paramref name="to"/>'s chest (see <see cref="TryPass"/>).</summary>
        private ThrowParams BuildPassParams(DodgeBall ball, DodgeballPlayer to)
        {
            Vector3 origin = GetThrowOrigin();
            Vector3 inHand = ball.transform.position;
            if (ThrowSolver.IsFinite(inHand) && (inHand - Owner.ChestPosition).sqrMagnitude <= handSocketMaxDistance * handSocketMaxDistance)
                origin = inHand;

            Vector3 aim = to.ChestPosition;
            Vector3 delta = aim - origin;
            float gravity = GameConstants.Gravity * passGravityScale;
            float distance = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z) + Mathf.Max(0f, delta.y);

            // The 45-degree lob needs v = sqrt(g * d); a little more gives a comfortable, lower arc.
            float needed = Mathf.Sqrt(gravity * Mathf.Max(1f, distance)) * passArcFactor;
            float speed = Mathf.Min(passMaxSpeed, Mathf.Max(Mathf.Max(0f, Profile.passSpeedKmh) * GameConstants.KmhToMs, needed));

            // The ball keeps its rally count through a pass, but a pass is never rally-boosted: pre-divide the multiplier.
            int rally = ball.RallyCount;
            float baseSpeed = speed / RallyMath.RallyMultiplier(rally);

            return new ThrowParams
            {
                Thrower = Owner,
                Target = to,
                Origin = origin,
                AimDirection = delta.sqrMagnitude > 1e-8f ? delta.normalized : Owner.Forward,
                AimPoint = aim,
                BaseSpeed = baseSpeed,
                ChargeNormalized = 0f,
                ChargeSeconds = 0f,
                SpeedMultiplier = 1f,
                RadiusMultiplier = 1f,
                RallyCount = rally,
                GravityScale = passGravityScale,
                Unblockable = false,
                Pierce = false,
                IsAbilityThrow = false,
                IsPass = true,
                IsCounterThrow = false,
                RevealsThrower = false,
                Style = BallStyle.Standard,
                Payload = null,
            };
        }

        // ================================================================== internals: catching

        private void Whiff(float now)
        {
            IsCatchArmed = false;
            _catchArmedUntil = 0f;
            _whiffUntil = now + Mathf.Max(0f, Profile.whiffRecovery);
            LastCatchQuality = CatchQuality.Miss;
            RaiseCatchResolved(null, CatchQuality.Miss);
            GameEvents.Publish(new CatchWhiffEvent { Player = Owner });
        }

        private void RaiseCatchResolved(DodgeBall ball, CatchQuality quality)
        {
            var handler = CatchResolved;
            if (handler == null) return;
            try { handler(ball, quality); }
            catch (Exception e) { Debug.LogException(e, this); }
        }

        private bool IsFrozen() => Owner != null && Owner.Status != null && Owner.Status.Has(StatusEffectType.Frozen);

        private bool IsBeingEliminated() =>
            Owner != null && Owner.StateMachine != null && Owner.StateMachine.IsIn(PlayerStateId.Incapacitated) &&
            Owner.StateMachine.IncapacitationReason == IncapacitationReason.Eliminated;

        // ================================================================== internals: aim assist

        /// <summary>
        /// Soft lock: the intent's explicit target (bots) &gt; a target chosen with Cycle Target (kept for manualLockTime) &gt;
        /// the sticky best enemy in the assist cone, re-evaluated every targetRefreshInterval seconds.
        /// </summary>
        private void UpdateTargeting(float deltaTime, float now)
        {
            var intent = Owner.Intent;
            var profile = Profile;

            if (IsLockable(intent.DesiredTarget))
            {
                CurrentTarget = intent.DesiredTarget;
                return;
            }

            if (!IsLockable(CurrentTarget)) CurrentTarget = null;

            Vector3 origin = Owner.ChestPosition;
            Vector3 direction = intent.AimDirection;
            if (!ThrowSolver.IsFinite(direction) || direction.sqrMagnitude < 1e-8f) direction = Owner.Forward;

            if (intent.CycleTargetPressed)
            {
                var next = TargetingSystem.CycleTarget(Owner, CurrentTarget, origin, direction);
                if (next != null)
                {
                    CurrentTarget = next;
                    _manualLockUntil = now + manualLockTime;
                }
                return;
            }

            if (CurrentTarget != null && now < _manualLockUntil) return;

            _targetRefreshTimer -= deltaTime;
            if (_targetRefreshTimer > 0f && CurrentTarget != null) return;
            _targetRefreshTimer = targetRefreshInterval;

            // Keep the current lock while it stays inside a wider cone / range (no flicker between two close targets).
            if (CurrentTarget != null)
            {
                Vector3 to = CurrentTarget.ChestPosition - origin;
                if (TargetingSystem.PlanarAngle(direction, to) <= profile.aimAssistAngle * targetStickiness &&
                    to.sqrMagnitude <= Sqr(profile.aimAssistRange * targetKeepRangeFactor) &&
                    (!TargetingSystem.RequireLineOfSight || TargetingSystem.HasLineOfSight(origin, CurrentTarget.ChestPosition)))
                    return;
            }

            CurrentTarget = TargetingSystem.FindBestTarget(Owner, origin, direction, profile.aimAssistAngle, profile.aimAssistRange);
        }

        private bool IsLockable(DodgeballPlayer target) => target != null && TargetingSystem.IsCandidate(Owner, target);

        private static float Sqr(float x) => x * x;

        // ================================================================== internals: profile

        /// <summary>The profile this controller uses: a private copy of the hero's (see <see cref="copyProfileOnInitialize"/>).</summary>
        private CombatProfile PrepareProfile(CombatProfile source)
        {
            if (source == null) return new CombatProfile();
            if (!copyProfileOnInitialize) return source;

            try
            {
                if (s_memberwiseClone == null)
                    s_memberwiseClone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
                if (s_memberwiseClone != null && s_memberwiseClone.Invoke(source, null) is CombatProfile copy)
                {
                    // The curve is a reference type: give the copy its own so nothing edits the asset through it.
                    if (source.chargeCurve != null)
                    {
                        copy.chargeCurve = new AnimationCurve(source.chargeCurve.keys)
                        {
                            preWrapMode = source.chargeCurve.preWrapMode,
                            postWrapMode = source.chargeCurve.postWrapMode,
                        };
                    }
                    return copy;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
            return source;
        }
    }
}
