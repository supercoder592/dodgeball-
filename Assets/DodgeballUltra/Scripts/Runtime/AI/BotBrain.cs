using System;
using System.Collections.Generic;
using DodgeballUltra.Abilities;
using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.AI
{
    /// <summary>
    /// CONTRACT (kernel) - utility-AI controller implementing <see cref="IIntentSource"/>, so bots use exactly the same
    /// code paths as humans. Behaviours: grab loose balls (opening rush), pick targets, charge &amp; throw with human-like
    /// reaction times, dodge (sidestep/jump/slide) predicted impacts, attempt timed catches (skill-dependent timing noise
    /// around the perfect window), pass, play from the outfield, and use abilities via AbilityBase.EvaluateAIUtility.
    /// <para>Owner module: AI.</para>
    /// <para>
    /// Every frame (<see cref="Sample"/>, scaled time):
    /// <code>
    /// perception    : track enemy live balls, notice them after the reaction delay, predict impacts (BotPerception)
    /// threat layer  : a newly noticed hit pre-empts everything -> catch (timed CatchPressed) or dodge (sidestep/jump/slide)
    /// decision tick : every decisionInterval +/- jitter (or when forced): refresh target / ball / pass receiver, score
    ///                 Retrieve / Attack / Pass / Position with hysteresis, evaluate Skill / Ultimate utilities
    /// execution     : steer (destination, strafe, separation, confinement), aim, and drive the button sequences
    ///                 (ThrowPressed -> ThrowHeld for the chosen charge -> ThrowReleased; one-frame Catch/Jump/Slide/
    ///                 Pickup/Pass/Skill/Ultimate presses) - exactly what a human produces with a controller
    /// </code>
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Dodgeball Ultra/AI/Bot Brain")]
    public sealed class BotBrain : MonoBehaviour, IIntentSource
    {
        // ================================================================== inspector

        [Header("Difficulty")]
        [Tooltip("Use 'Custom Profile' instead of the preset matching the difficulty passed to Initialize.")]
        [SerializeField] private bool useCustomProfile;
        [Tooltip("Hand-tuned skill profile (only used when 'Use Custom Profile' is on).")]
        [SerializeField] private BotDifficultyProfile customProfile = new BotDifficultyProfile();
        [Tooltip("Seed of this bot's random sequence. 0 = derived from the player id and this component.")]
        [SerializeField] private int randomSeed;

        [Header("Movement")]
        [Tooltip("Distance (m) at which a destination counts as reached.")]
        [SerializeField, Range(0.05f, 1.5f)] private float arriveRadius = 0.35f;
        [Tooltip("Distance (m) inside which the bot eases off the stick when approaching a destination.")]
        [SerializeField, Range(0.2f, 4f)] private float slowRadius = 1.4f;
        [Tooltip("The bot sprints when its destination is farther than this (m).")]
        [SerializeField, Min(0f)] private float sprintDistance = 3f;
        [Tooltip("Margin (m) kept from the infield confinement edges when positioning (avoid being cornered).")]
        [SerializeField, Range(0f, 3f)] private float edgeMargin = 0.9f;
        [Tooltip("Once settled on its spot, the bot only moves again when the spot drifts farther than this (m).")]
        [SerializeField, Range(0.2f, 3f)] private float settleRadius = 0.9f;
        [Tooltip("Radius (m) of the short-range push away from nearby bodies.")]
        [SerializeField, Range(0.3f, 3f)] private float separationRadius = 1.1f;
        [Tooltip("Enemies closer than this (m) are noticed even when sneaking up with Silent Footsteps.")]
        [SerializeField, Min(0f)] private float awarenessRadius = 2.5f;

        [Header("Attacking")]
        [Tooltip("Preferred minimum throwing distance (m).")]
        [SerializeField, Min(0.5f)] private float idealRangeMin = 6f;
        [Tooltip("Preferred maximum throwing distance (m). Farther targets make the bot close in on the centre line first.")]
        [SerializeField, Min(1f)] private float idealRangeMax = 12f;
        [Tooltip("The bot never throws at targets farther than this (m).")]
        [SerializeField, Min(2f)] private float maxThrowRange = 17f;
        [Tooltip("Closest distance (m) to the centre line the bot attacks from (aggressive bots).")]
        [SerializeField, Range(0.3f, 4f)] private float attackLineDepthMin = 0.8f;
        [Tooltip("Farthest distance (m) from the centre line the bot attacks from (cautious bots).")]
        [SerializeField, Range(0.5f, 6f)] private float attackLineDepthMax = 2.8f;
        [Tooltip("Seconds holding a ball without a clear shot before the bot passes or throws anyway.")]
        [SerializeField, Min(1f)] private float maxHoldTime = 6f;
        [Tooltip("Abort the throw sequence if the state machine has not started charging within this time (s).")]
        [SerializeField, Range(0.1f, 1f)] private float chargeStartTimeout = 0.45f;
        [Tooltip("Minimum charge (s) before an early (opportunistic or panic) release.")]
        [SerializeField, Range(0f, 0.6f)] private float minEarlyReleaseCharge = 0.15f;
        [Tooltip("Pause (s) after a throw before another charge may begin.")]
        [SerializeField, Range(0f, 2f)] private float postThrowDelay = 0.45f;

        [Header("Threat response")]
        [Tooltip("How far ahead (s) incoming balls are predicted.")]
        [SerializeField, Range(0.5f, 4f)] private float threatLookahead = 2f;
        [Tooltip("Distance (m) at which an unnoticed ball thrown from behind is finally sensed.")]
        [SerializeField, Range(0.5f, 6f)] private float peripheralRadius = 2.5f;
        [Tooltip("Balls predicted to hit below this height above the feet (m) are jumped over.")]
        [SerializeField, Range(0.2f, 1.2f)] private float lowBallHeight = 0.7f;
        [Tooltip("Ideal seconds before impact to press Jump (feet are ~0.7-1.0 m up 0.15-0.3 s after take-off).")]
        [SerializeField, Range(0.05f, 0.6f)] private float jumpPressLead = 0.24f;
        [Tooltip("Ideal seconds before impact to press Slide.")]
        [SerializeField, Range(0.05f, 0.8f)] private float slidePressLead = 0.34f;
        [Tooltip("Extra lateral clearance (m) a sidestep aims for beyond body radius + ball radius.")]
        [SerializeField, Range(0f, 1f)] private float sidestepClearance = 0.3f;
        [Tooltip("A second incoming ball replaces the one being answered only if it arrives this much sooner (s).")]
        [SerializeField, Range(0f, 0.5f)] private float threatSwitchMargin = 0.15f;
        [Tooltip("While charging, release at once when a ball will hit within this time (s) (panic throw).")]
        [SerializeField, Range(0f, 1f)] private float panicReleaseTime = 0.3f;

        [Header("Retrieving")]
        [Tooltip("Highest point above the feet (m) at which a ball in Chrono's stasis can be snatched (with a jump).")]
        [SerializeField, Range(1f, 3f)] private float stasisReachHeight = 2.3f;
        [Tooltip("Seconds after RoundStarted during which the bot sprints for the centre-line balls (opening rush).")]
        [SerializeField, Range(0f, 10f)] private float openingRushDuration = 4f;
        [Tooltip("Seconds chasing the same ball after which the bot gives up on it for a while.")]
        [SerializeField, Range(1f, 20f)] private float maxChaseTime = 6f;
        [Tooltip("Seconds between two Pick up presses while next to a ball.")]
        [SerializeField, Range(0.05f, 1f)] private float pickupRepressInterval = 0.2f;
        [Tooltip("Radius (m) used for 'free balls nearby' in the ability context.")]
        [SerializeField, Min(1f)] private float freeBallAwarenessRadius = 8f;

        [Header("Debug")]
        [Tooltip("Draw the bot's intentions (destination, target, aim, ball, threat, dodge) when selected.")]
        [SerializeField] private bool drawGizmos = true;

        // ================================================================== static registry

        private static readonly List<BotBrain> s_active = new List<BotBrain>(8);

        /// <summary>Enabled bot brains (team coordination: ball claims).</summary>
        public static IReadOnlyList<BotBrain> ActiveBrains => s_active;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_active.Clear();

        // ================================================================== public state

        public BotDifficulty Difficulty { get; private set; } = BotDifficulty.Normal;

        /// <summary>The player this brain drives (set by <see cref="Initialize"/> or on the first <see cref="Sample"/>).</summary>
        public DodgeballPlayer Owner { get; private set; }

        /// <summary>Active skill profile (preset for <see cref="Difficulty"/>, or the custom profile).</summary>
        public BotDifficultyProfile Profile => _profile;

        public BotBehaviour CurrentBehaviour { get; private set; } = BotBehaviour.Idle;

        /// <summary>Enemy the bot intends to throw at (null when it has no ball).</summary>
        public DodgeballPlayer CurrentTarget { get; private set; }

        /// <summary>Loose ball this bot is running for (teammate bots leave it alone).</summary>
        public DodgeBall ClaimedBall { get; private set; }

        /// <summary>Where the bot is steering to.</summary>
        public Vector3 Destination { get; private set; }

        /// <summary>Answer to the ball currently threatening the bot (None when no threat).</summary>
        public BotThreatResponse ThreatResponse => _threatActive ? _response : BotThreatResponse.None;

        /// <summary>Most urgent perceived incoming ball (invalid when none).</summary>
        public BotThreat CurrentThreat => _threatActive ? _threat : default;

        public BotThrowPhase ThrowPhase => _throwPhase;

        /// <summary>Utility scores of the last decision tick.</summary>
        public BotDecisionScores LastScores => _scores;

        public bool IsInitialized => Owner != null;

        // ================================================================== runtime state

        private BotDifficultyProfile _profile = BotDifficultyProfile.CreatePreset(BotDifficulty.Normal);
        private BotHeroTraits _traits;
        private HeroId _traitsHero;
        private bool _traitsValid;
        private BotRandom _rng;
        private BotSteering _steering;
        private readonly BotPerception _perception = new BotPerception();
        private readonly BotTargeting _targeting = new BotTargeting();
        private readonly BotBallSelector _ballSelector = new BotBallSelector();
        private readonly BotAbilityUser _abilityUser = new BotAbilityUser();

        // Cached event handlers (no delegate allocation on enable/disable churn).
        private Action<RoundStartedEvent> _onRoundStarted;
        private Action<RoundEndedEvent> _onRoundEnded;
        private Action<DangerSenseEvent> _onDangerSense;
        private Action<AbilityFailedEvent> _onAbilityFailed;
        private Action<BallCaughtEvent> _onBallCaught;
        private Action<PlayerZoneChangedEvent> _onZoneChanged;
        private Action<PlayerEliminatedEvent> _onEliminated;

        // Frame bookkeeping.
        private float _lastSampleTime = float.NegativeInfinity;
        private int _lastSampleFrame = -1;
        private PlayerIntent _lastIntent;
        private bool _wasLive;

        // Decisions.
        private float _nextDecisionTime;
        private bool _forceDecision = true;
        private float _behaviourSince;
        private float _aggression = 0.5f;
        private BotDecisionScores _scores;
        private Bounds _confinement;
        private Bounds _inner;
        private float _openingRushUntil = float.NegativeInfinity;
        private bool _settled;

        // Target / ball knowledge.
        private float _currentTargetScore;
        private DodgeballPlayer _decoyFor;
        private Vector3 _decoyOffset;
        private DodgeballPlayer _passReceiver;
        private float _nextPassAllowedAt;
        private bool _targetClear;
        private float _claimedBallTime;
        private DodgeBall _ignoredBall;
        private float _ignoredBallUntil;

        // Possession.
        private bool _hadBall;
        private float _possessionStart;
        private bool _passInclination;
        private bool _counterAttack;
        private float _throwReadyAt;
        private float _lastCatchTime = float.NegativeInfinity;
        private bool _lastCatchPerfect;

        // Throw sequence.
        private BotThrowPhase _throwPhase;
        private float _throwPressTime;
        private float _plannedCharge;
        private DodgeballPlayer _throwTarget;
        private bool _opportunityRolled;
        private float _lastThrowTime = float.NegativeInfinity;
        private Vector3 _lastReleaseAim;
        private float _lastReleaseTime = float.NegativeInfinity;

        // Threat response.
        private bool _threatActive;
        private BotThreat _threat;
        private BotThreatResponse _response;
        private DodgeBall _respondingTo;
        private float _respondingLaunch;
        private float _threatExpectedAt;
        private float _plannedCatchLead;
        private bool _catchPressed;
        private bool _maneuverPressed;
        private float _maneuverLead;
        private Vector3 _dodgeDirection;
        private float _responseStartAt;

        // One-shot presses.
        private bool _pendingPass;
        private bool _pendingSkill;
        private bool _pendingUltimate;
        private DodgeballPlayer _abilityTarget;
        private float _nextPickupPressAt;

        // ================================================================== contract API

        /// <summary>
        /// Binds the brain to <paramref name="owner"/> with the preset for <paramref name="difficulty"/> (or the custom
        /// profile), seeds its random sequence and clears all state. Safe to call again (e.g. difficulty change, re-spawn).
        /// </summary>
        public void Initialize(DodgeballPlayer owner, BotDifficulty difficulty)
        {
            Owner = owner;
            Difficulty = difficulty;
            ApplyProfile();

            int seed = randomSeed != 0
                ? randomSeed
                : BotRandom.MakeSeed(owner != null ? owner.PlayerId + 1 : 0, GetInstanceID() ^ ((int)difficulty * 7919));
            if (_rng == null) _rng = new BotRandom(seed);
            else _rng.Reseed(seed);
            _steering = new BotSteering(_rng);

            _traitsValid = false;
            ResetBrain();

            if (owner != null && owner.IntentSource == null) owner.IntentSource = this;
        }

        /// <summary>
        /// Produces this frame's intent for <paramref name="player"/>. Called once per frame by DodgeballPlayer (scaled
        /// time) unless the player's input is locked.
        /// </summary>
        public PlayerIntent Sample(DodgeballPlayer player, float deltaTime)
        {
            if (player == null) return PlayerIntent.Neutral(transform.position, transform.forward);
            if (Owner != player) Initialize(player, Difficulty); // lazily bound (or re-bound to another body)
            EnsureRuntime();

            int frame = Time.frameCount;
            if (frame == _lastSampleFrame) return _lastIntent; // idempotent within a frame
            _lastSampleFrame = frame;

            float now = Time.time;
            // Not sampled for a while (input locked, disabled, countdown): whatever we were doing is stale.
            if (now - _lastSampleTime > 0.3f) ResetTransient();
            _lastSampleTime = now;

            var intent = PlayerIntent.Neutral(player.Position, player.Forward);

            // DodgeballPlayer samples its source even while input is locked (countdown, round end); the bot then idles.
            bool live = player.IsInitialized && BotWorld.IsMatchLive && !player.InputLocked;
            if (!live)
            {
                if (_wasLive) ResetTransient();
                _wasLive = false;
                CurrentBehaviour = BotBehaviour.Idle;
                FaceEnemyHalf(ref intent);
                _lastIntent = intent;
                return intent;
            }
            _wasLive = true;

            RefreshTraits();
            _confinement = BotWorld.GetConfinement(player);

            if (!player.CanAct)
            {
                // Stunned / frozen / ragdoll / channeling: buttons do nothing, drop any half-done sequence.
                AbortSequences();
                _lastIntent = intent;
                return intent;
            }

            TrackPossession(now);
            UpdateThreat(now);

            if (_forceDecision || now >= _nextDecisionTime) Decide(now);

            Execute(ref intent, now, Mathf.Max(0f, deltaTime));
            _lastIntent = intent;
            return intent;
        }

        // ================================================================== extra public API

        /// <summary>Switches to the preset of <paramref name="difficulty"/> (unless a custom profile is used).</summary>
        public void SetDifficulty(BotDifficulty difficulty)
        {
            Difficulty = difficulty;
            ApplyProfile();
        }

        /// <summary>Uses <paramref name="profile"/> (copied) instead of the difficulty preset. Null restores the preset.</summary>
        public void SetCustomProfile(BotDifficultyProfile profile)
        {
            useCustomProfile = profile != null;
            if (profile != null) customProfile = profile.Clone();
            ApplyProfile();
        }

        /// <summary>Forgets everything (round reset, re-spawn). Keeps owner, difficulty and seed.</summary>
        public void ResetBrain()
        {
            ResetTransient();
            _abilityUser.Reset();
            ClaimedBall = null;
            CurrentTarget = null;
            _currentTargetScore = 0f;
            _decoyFor = null;
            _decoyOffset = Vector3.zero;
            _passReceiver = null;
            _ignoredBall = null;
            _ignoredBallUntil = 0f;
            _nextPassAllowedAt = 0f;
            _targetClear = false;
            _hadBall = false;
            _counterAttack = false;
            _passInclination = false;
            _throwReadyAt = 0f;
            _lastThrowTime = float.NegativeInfinity;
            _lastCatchTime = float.NegativeInfinity;
            _openingRushUntil = float.NegativeInfinity;
            _scores = default;
            CurrentBehaviour = BotBehaviour.Idle;
        }

        // ================================================================== Unity lifecycle

        private void Awake() => EnsureRuntime();

        private void OnEnable()
        {
            if (!s_active.Contains(this)) s_active.Add(this);

            _onRoundStarted ??= OnRoundStarted;
            _onRoundEnded ??= OnRoundEnded;
            _onDangerSense ??= OnDangerSense;
            _onAbilityFailed ??= OnAbilityFailed;
            _onBallCaught ??= OnBallCaught;
            _onZoneChanged ??= OnZoneChanged;
            _onEliminated ??= OnEliminated;

            GameEvents.Subscribe(_onRoundStarted);
            GameEvents.Subscribe(_onRoundEnded);
            GameEvents.Subscribe(_onDangerSense);
            GameEvents.Subscribe(_onAbilityFailed);
            GameEvents.Subscribe(_onBallCaught);
            GameEvents.Subscribe(_onZoneChanged);
            GameEvents.Subscribe(_onEliminated);
        }

        private void OnDisable()
        {
            s_active.Remove(this);
            ClaimedBall = null;

            GameEvents.Unsubscribe(_onRoundStarted);
            GameEvents.Unsubscribe(_onRoundEnded);
            GameEvents.Unsubscribe(_onDangerSense);
            GameEvents.Unsubscribe(_onAbilityFailed);
            GameEvents.Unsubscribe(_onBallCaught);
            GameEvents.Unsubscribe(_onZoneChanged);
            GameEvents.Unsubscribe(_onEliminated);
        }

        private void OnValidate()
        {
            if (idealRangeMax < idealRangeMin) idealRangeMax = idealRangeMin;
            if (maxThrowRange < idealRangeMax) maxThrowRange = idealRangeMax;
            if (attackLineDepthMax < attackLineDepthMin) attackLineDepthMax = attackLineDepthMin;
            if (Application.isPlaying && Owner != null) ApplyProfile();
        }

        // ================================================================== event handlers

        private void OnRoundStarted(RoundStartedEvent e)
        {
            if (Owner == null) return;
            EnsureRuntime();
            ResetTransient();
            float now = Time.time;
            _openingRushUntil = now + openingRushDuration;
            // Humans react to "GO!" too.
            _nextDecisionTime = now + Mathf.Max(0.02f, _rng.Jitter(_profile.reactionTime, _profile.reactionJitter));
            _forceDecision = false;
            _lastSampleTime = now; // the countdown gap is expected, do not reset again on the first sample
        }

        private void OnRoundEnded(RoundEndedEvent e)
        {
            if (Owner == null) return;
            ResetTransient();
            _openingRushUntil = float.NegativeInfinity;
        }

        private void OnDangerSense(DangerSenseEvent e)
        {
            if (Owner == null || e.Player != Owner || !e.Active || e.Ball == null) return;
            RefreshTraits();
            if (!_traits.HasDangerSense) return; // Specter's passive only
            EnsureRuntime();
            _perception.NotifyDangerSense(e.Ball, Owner, _profile, _rng, Time.time);
        }

        private void OnAbilityFailed(AbilityFailedEvent e)
        {
            if (Owner == null || e.Player != Owner) return;
            _abilityUser.NotifyFailed(e.Slot, Time.time, _profile);
        }

        private void OnBallCaught(BallCaughtEvent e)
        {
            if (Owner == null || e.Catcher != Owner) return;
            _lastCatchTime = Time.time;
            _lastCatchPerfect = e.Quality == CatchQuality.Perfect;
        }

        private void OnZoneChanged(PlayerZoneChangedEvent e)
        {
            if (Owner == null || e.Player != Owner) return;
            ResetTransient();
        }

        private void OnEliminated(PlayerEliminatedEvent e)
        {
            if (Owner == null || e.Player != Owner) return;
            ResetTransient();
        }

        // ================================================================== setup helpers

        private void EnsureRuntime()
        {
            if (_rng == null) _rng = new BotRandom(randomSeed != 0 ? randomSeed : BotRandom.MakeSeed(GetInstanceID(), 1));
            if (_steering == null) _steering = new BotSteering(_rng);
            if (_profile == null) ApplyProfile();
        }

        private void ApplyProfile()
        {
            _profile = useCustomProfile && customProfile != null ? customProfile.Clone() : BotDifficultyProfile.CreatePreset(Difficulty);
        }

        private void RefreshTraits()
        {
            if (Owner == null) return;
            var hero = Owner.Hero;
            if (_traitsValid && hero == _traitsHero) return;
            _traits = BotHeroTraits.For(hero);
            _traitsHero = hero;
            _traitsValid = true;
        }

        /// <summary>Drops everything short-lived: sequences, threat response, perception, pending presses.</summary>
        private void ResetTransient()
        {
            _throwPhase = BotThrowPhase.None;
            _throwTarget = null;
            _pendingPass = _pendingSkill = _pendingUltimate = false;
            _abilityTarget = null;
            ClearThreat();
            _perception.Clear();
            _steering?.ResetStrafe();
            _settled = false;
            _forceDecision = true;
            _behaviourSince = Time.time;
            CurrentBehaviour = BotBehaviour.Idle;
        }

        private void AbortSequences()
        {
            _throwPhase = BotThrowPhase.None;
            _throwTarget = null;
            _pendingPass = _pendingSkill = _pendingUltimate = false;
            ClearThreat();
            _forceDecision = true;
        }

        // ================================================================== possession

        private void TrackPossession(float now)
        {
            bool hasBall = BotWorld.HoldsBall(Owner);
            if (hasBall && !_hadBall)
            {
                _possessionStart = now;
                bool caught = now - _lastCatchTime < 0.35f;
                float hesitation = _rng.Range(_profile.throwHesitation);
                if (caught) hesitation *= _lastCatchPerfect ? 0.35f : 0.6f; // strike back while the counter boost is hot
                _throwReadyAt = Mathf.Max(_throwReadyAt, now + hesitation);
                _passInclination = _rng.Chance(Mathf.Clamp01(_profile.passChance + _traits.PassBias) * (caught ? 0.5f : 1f));
                _counterAttack = caught;
                ClaimedBall = null;
                _forceDecision = true;
            }
            else if (!hasBall && _hadBall)
            {
                _counterAttack = false;
                if (_throwPhase == BotThrowPhase.Pressing) _throwPhase = BotThrowPhase.None;
                _forceDecision = true;
            }
            _hadBall = hasBall;
        }

        // ================================================================== decisions

        private void Decide(float now)
        {
            _forceDecision = false;
            _nextDecisionTime = now + Mathf.Max(0.05f, _rng.Jitter(_profile.decisionInterval, _profile.decisionJitter));

            var self = Owner;
            float bodyRadius = self.Capsule != null ? self.Capsule.radius : 0.32f;
            _inner = BotWorld.ShrinkPlanar(_confinement, self.IsInfield ? edgeMargin : 0.4f);
            var walkable = BotWorld.ShrinkPlanar(_confinement, bodyRadius);
            _aggression = ComputeAggression();

            bool hasBall = BotWorld.HoldsBall(self);

            // ---------------------------------------------------------- knowledge refresh
            float timeToBall = float.PositiveInfinity;
            if (hasBall)
            {
                ClaimedBall = null;
                SelectAttackTarget(now);
            }
            else
            {
                CurrentTarget = null;
                _decoyFor = null;
                var combatProfile = self.Combat != null ? self.Combat.Profile : null;
                float reach = (combatProfile != null ? combatProfile.manualPickupRadius : 1.6f) * 0.85f;
                float runSpeed = self.Motor != null && self.Motor.Profile != null ? self.Motor.Profile.sprintSpeed * 0.85f : 6f;
                var previous = ClaimedBall;
                var ignored = now < _ignoredBallUntil ? _ignoredBall : null;
                ClaimedBall = _ballSelector.SelectBall(self, walkable, reach, stasisReachHeight, runSpeed, ClaimedBall, ignored,
                    _profile.hysteresis * 2f, out _, out timeToBall);
                if (ClaimedBall != previous) _claimedBallTime = now;
            }

            // ---------------------------------------------------------- utility scores
            var scores = default(BotDecisionScores);
            scores.ThreatResponse = _threatActive ? 10f : 0f;
            scores.Retrieve = ScoreRetrieve(now, hasBall, timeToBall);
            scores.Attack = ScoreAttack(now, hasBall);
            scores.Pass = ScorePass(now, hasBall);
            scores.Position = 0.35f;

            // Hysteresis: the running behaviour gets a bonus so near-equal options do not flip-flop.
            switch (CurrentBehaviour)
            {
                case BotBehaviour.Retrieve: if (scores.Retrieve > 0f) scores.Retrieve += _profile.hysteresis; break;
                case BotBehaviour.Attack: if (scores.Attack > 0f) scores.Attack += _profile.hysteresis; break;
                case BotBehaviour.Position: scores.Position += _profile.hysteresis; break;
            }

            var next = BotBehaviour.Position;
            float best = scores.Position;
            if (scores.Retrieve > best) { best = scores.Retrieve; next = BotBehaviour.Retrieve; }
            if (scores.Attack > best) { best = scores.Attack; next = BotBehaviour.Attack; }
            if (scores.Pass > best) { best = scores.Pass; next = BotBehaviour.Pass; }
            if (scores.ThreatResponse > best) next = BotBehaviour.ThreatResponse;

            if (next != CurrentBehaviour)
            {
                bool dwellOver = now - _behaviourSince >= _profile.minBehaviourDwell;
                if (next == BotBehaviour.ThreatResponse || dwellOver || !IsBehaviourValid(CurrentBehaviour, hasBall))
                    SwitchBehaviour(next, now);
            }

            // ---------------------------------------------------------- abilities
            if (_throwPhase == BotThrowPhase.None && !_pendingSkill && !_pendingUltimate) EvaluateAbilities(now, false);
            scores.SkillUtility = _abilityUser.LastSkillUtility;
            scores.UltimateUtility = _abilityUser.LastUltimateUtility;
            _scores = scores;
        }

        private float ScoreRetrieve(float now, bool hasBall, float timeToBall)
        {
            if (hasBall || ClaimedBall == null) return 0f;
            float s = 0.55f + 0.35f * (1f - Mathf.Clamp01(timeToBall / 3f));
            if (now < _openingRushUntil) s += 1f;                         // opening rush to the centre line
            if (ClaimedBall.State == BallState.Stasis) s += 0.25f;       // snatch Chrono's frozen balls
            // A run right under an enemy ball holder's nose is risky for cautious bots.
            var ballPos = ClaimedBall.transform.position;
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                if (!PlayerRegistry.AreEnemies(Owner, e) || !BotWorld.HoldsBall(e)) continue;
                if (BotWorld.PlanarDistance(e.Position, ballPos) < 6f)
                {
                    s -= 0.25f * (1f - _aggression);
                    break;
                }
            }
            return s;
        }

        private float ScoreAttack(float now, bool hasBall)
        {
            if (!hasBall) return 0f;
            if (CurrentTarget == null) return 0.3f; // holding with no shot: repositioning (0.35) wins until a target shows
            float s = 0.75f + 0.3f * Mathf.Clamp01(_currentTargetScore);
            if (_counterAttack) s += 0.3f;
            if (now - _possessionStart > maxHoldTime * 0.5f) s += 0.2f;
            return s;
        }

        private float ScorePass(float now, bool hasBall)
        {
            _passReceiver = null;
            if (!hasBall || _throwPhase != BotThrowPhase.None) return 0f;

            // CombatController.TryPass sends the ball to the nearest teammate: evaluate exactly that player.
            var receiver = PlayerRegistry.FindNearestTeammate(Owner, Owner.Position);
            if (receiver == null || !receiver.IsInitialized || BotWorld.HoldsBall(receiver)) return 0f;
            _passReceiver = receiver;

            if (now < _nextPassAllowedAt) return 0f;

            // Held too long without a clear shot (everyone cloaked / behind Aegis Barrier): give it to someone who has one.
            bool stuck = now - _possessionStart > maxHoldTime && (CurrentTarget == null || !_targetClear);
            if (stuck) return 1.6f;
            if (!_passInclination || _counterAttack) return 0f;

            float mine = CurrentTarget != null ? _currentTargetScore : 0f;
            float theirs = _targeting.EvaluateOpportunity(Owner, receiver, receiver.ChestPosition, _profile, maxThrowRange);
            if (!receiver.IsInfield) theirs += 0.1f; // outfield teammates throw at the enemies' backs
            float margin = theirs - mine;
            if (margin < 0.25f) return 0f;
            return 0.9f + margin + _traits.PassBias * 0.5f;
        }

        private static bool IsBehaviourValid(BotBehaviour behaviour, bool hasBall)
        {
            switch (behaviour)
            {
                case BotBehaviour.Retrieve: return !hasBall;
                case BotBehaviour.Attack:
                case BotBehaviour.Pass: return hasBall;
                case BotBehaviour.Idle:
                case BotBehaviour.ThreatResponse: return false;
                default: return true;
            }
        }

        private void SwitchBehaviour(BotBehaviour next, float now)
        {
            if (next == CurrentBehaviour) return;
            CurrentBehaviour = next;
            _behaviourSince = now;
            _settled = false;
            if (next == BotBehaviour.Pass) _pendingPass = true;
            if (next == BotBehaviour.Attack) _steering.ResetStrafe();
        }

        private float ComputeAggression()
        {
            float a = _profile.aggression + _traits.AggressionBias;
            int allies = BotWorld.CountTargetable(Owner.Team);
            int enemies = BotWorld.CountTargetable(Owner.Team.Opponent());
            if (allies < enemies) a += 0.1f;       // behind: take more risks
            else if (allies > enemies) a -= 0.05f; // ahead: protect the lead
            if (BotWorld.RoundTimeRemaining < 20f && allies <= enemies) a += 0.15f;
            return Mathf.Clamp01(a);
        }

        private void SelectAttackTarget(float now)
        {
            var self = Owner;
            bool requireLineOfSight = now - _possessionStart < maxHoldTime; // after that, throw at the barrier anyway
            var target = _targeting.SelectTarget(self, self.ChestPosition, _profile, _rng, CurrentTarget, maxThrowRange,
                requireLineOfSight, out float score);

            CurrentTarget = target;
            _currentTargetScore = score;
            _targetClear = target != null && (requireLineOfSight || _targeting.HasLineOfSight(self, self.ChestPosition, target));

            // Mirage Formation / Night Parade: the real body is disguised among clones. Decide once per target whether
            // the bot has been fooled, and if so aim at a plausible clone position beside it.
            if (target != null && BotWorld.Has(target, StatusEffectType.Obscured))
            {
                if (_decoyFor != target)
                {
                    _decoyFor = target;
                    _decoyOffset = _rng.Chance(_profile.obscuredMistargetChance)
                        ? BotTargeting.ChooseDecoyOffset(self.ChestPosition, target, _rng)
                        : Vector3.zero;
                }
            }
            else
            {
                _decoyFor = null;
                _decoyOffset = Vector3.zero;
            }
        }

        private void EvaluateAbilities(float now, bool threatTriggered)
        {
            var threat = _threatActive ? _threat : default;
            BotAbilityUser.BuildContext(Owner, threat, _profile.cloakDetectionRadius, freeBallAwarenessRadius, out var ctx);
            var calm = ctx;
            calm.IncomingBall = null;
            calm.IncomingTimeToImpact = float.PositiveInfinity;

            if (!_abilityUser.TryChoose(Owner, ctx, threatTriggered ? calm : ctx, threatTriggered, _profile, _rng, now, out var slot))
                return;

            if (slot == AbilitySlot.Ultimate) _pendingUltimate = true;
            else _pendingSkill = true;
            _abilityTarget = CurrentTarget != null ? CurrentTarget : ctx.NearestEnemy;
            _abilityUser.NotifyPressed(slot, now, _profile);
        }

        // ================================================================== threat layer

        private void UpdateThreat(float now)
        {
            var self = Owner;
            if (!self.IsInfield)
            {
                if (_threatActive) ClearThreat();
                _perception.Clear();
                return;
            }

            float catchRadius = self.Combat != null && self.Combat.Profile != null ? self.Combat.Profile.catchRadius : 0.85f;
            _perception.Update(self, _profile, _rng, now, threatLookahead, peripheralRadius, catchRadius);

            if (!_perception.HasThreat)
            {
                if (!_threatActive) return;
                // Keep a committed dodge going until the ball has actually gone past (prevents stepping back into it).
                bool dodging = _response == BotThreatResponse.Sidestep || _response == BotThreatResponse.Slide ||
                               _response == BotThreatResponse.Jump;
                if (dodging && _respondingTo != null && _respondingTo.IsLive && now < _threatExpectedAt + 0.12f) return;
                ClearThreat();
                _forceDecision = true;
                return;
            }

            var t = _perception.MostUrgent;
            // Threat hysteresis: keep answering the ball we already committed to unless another one is clearly more
            // urgent - otherwise two balls alternating as "most urgent" would make the bot re-decide every frame.
            if (_threatActive && t.Ball != _respondingTo &&
                _perception.TryGetThreat(_respondingTo, _respondingLaunch, out var committed) &&
                committed.TimeToImpact <= t.TimeToImpact + threatSwitchMargin)
                t = committed;
            bool isNew = !_threatActive || t.Ball != _respondingTo || Mathf.Abs(t.LaunchTime - _respondingLaunch) > 1e-4f;
            _threat = t;
            _threatExpectedAt = now + t.TimeToImpact;

            if (isNew)
            {
                _threatActive = true;
                _respondingTo = t.Ball;
                _respondingLaunch = t.LaunchTime;
                DecideThreatResponse(t, now);
                SwitchBehaviour(BotBehaviour.ThreatResponse, now);
                _forceDecision = true;

                // Abilities that answer threats (evasive dodges, magnetic catches, stasis...) get a say right now.
                if (_throwPhase == BotThrowPhase.None && !_pendingSkill && !_pendingUltimate) EvaluateAbilities(now, true);
            }
            else if (_response == BotThreatResponse.Catch && !_catchPressed && !CanAttemptCatch(t))
            {
                // Situation changed (picked up a ball, got blocked from catching...): fall back to a dodge.
                var dodge = ChooseDodge(t, now, out _);
                _response = dodge != BotThreatResponse.None ? dodge : BotThreatResponse.Brace;
            }
        }

        private void ClearThreat()
        {
            _threatActive = false;
            _threat = default;
            _response = BotThreatResponse.None;
            _respondingTo = null;
            _catchPressed = false;
            _maneuverPressed = false;
            if (CurrentBehaviour == BotBehaviour.ThreatResponse) CurrentBehaviour = BotBehaviour.Position;
        }

        private void DecideThreatResponse(in BotThreat t, float now)
        {
            _catchPressed = false;
            _maneuverPressed = false;
            _responseStartAt = now;

            if (BotWorld.Has(Owner, StatusEffectType.Invulnerable))
            {
                _response = BotThreatResponse.Brace; // e.g. Precognition Dodge running: balls auto-evade
                return;
            }

            bool canCatch = CanAttemptCatch(t);
            float willingness = CatchWillingness(t);
            var dodge = ChooseDodge(t, now, out bool dodgeFeasible);

            if (canCatch && (_rng.Chance(willingness) || (!dodgeFeasible && _rng.Chance(0.5f + 0.5f * willingness))))
            {
                _response = BotThreatResponse.Catch;
                _plannedCatchLead = PlanCatchLead();
                _responseStartAt = now;
            }
            else if (dodge != BotThreatResponse.None)
            {
                _response = dodge;
            }
            else if (canCatch)
            {
                _response = BotThreatResponse.Catch; // desperate catch: nothing else can work
                _plannedCatchLead = PlanCatchLead();
                _responseStartAt = now;
            }
            else
            {
                _response = BotThreatResponse.Brace;
            }
        }

        /// <summary>Hands free, catching allowed, ball catchable, and the bot can square up to it in time.</summary>
        private bool CanAttemptCatch(in BotThreat t)
        {
            var self = Owner;
            var combat = self.Combat;
            var ball = t.Ball;
            if (combat == null || ball == null || combat.HasBall || combat.CatchingBlocked) return false;
            if (BotWorld.Has(self, StatusEffectType.Frozen)) return false;
            if (ball.Unblockable || ball.Pierce || ball.Style == BallStyle.Beam) return false; // Hyperbeam cannot be caught

            // Air catches are allowed by the state machine; catching mid-slide is not.
            if (BotWorld.StateOf(self) == PlayerStateId.Sliding) return false;

            // The ball must arrive inside the frontal catch cone: can the bot turn to face it in time?
            var toBall = BotWorld.PlanarDirection(self.Position, ball.transform.position, self.Forward);
            float angle = Vector3.Angle(self.Forward, toBall);
            float cone = combat.Profile != null ? combat.Profile.catchConeAngle : 75f;
            if (angle > cone * 0.8f)
            {
                float turnRate = self.Motor != null && self.Motor.Profile != null ? Mathf.Max(90f, self.Motor.Profile.turnSpeed) : 720f;
                float turnTime = (angle - cone * 0.6f) / turnRate;
                if (turnTime > t.CatchZoneTime - _profile.idealCatchLead) return false;
            }
            return true;
        }

        /// <summary>Probability of choosing to catch rather than dodge.</summary>
        private float CatchWillingness(in BotThreat t)
        {
            var self = Owner;
            float p = _profile.catchAttemptProbability + _traits.CatchBias;
            p *= Mathf.Lerp(1f, 0.6f, Mathf.InverseLerp(70f, 200f, t.SpeedKmh));  // fastballs are scarier to catch
            if (t.FromBehind) p *= 0.35f;                                          // no time to square up
            if (BotWorld.AnyTeammatePendingElimination(self)) p += 0.35f;          // Chrono's Delayed Impact: a catch saves them
            if (PlayerRegistry.CountTeam(self.Team, CourtZone.Outfield) > 0) p += 0.05f; // perfect catch revives a teammate
            if (BotWorld.Has(self, StatusEffectType.Slippery) || BotWorld.Has(self, StatusEffectType.DodgeDisabled) ||
                BotWorld.Has(self, StatusEffectType.Rooted)) p += 0.25f;          // dodging impaired
            if (self.Health != null && self.Health.CurrentHp > GameConstants.StandardHitDamage + 0.5f) p += 0.1f; // can afford a miss
            return Mathf.Clamp01(p);
        }

        /// <summary>
        /// Seconds before the ball reaches the catch zone at which Catch will be pressed: the ideal lead (~0.08 s, scaled
        /// by Iron Mitts' wider window) plus Gaussian timing noise from the profile. Late presses (negative) mean the ball
        /// arrives first - a hit, like a human who reacted too late; very early presses whiff.
        /// </summary>
        private float PlanCatchLead()
        {
            var combat = Owner.Combat;
            float perfect = combat != null ? combat.PerfectCatchWindow : GameConstants.PerfectCatchWindow;
            float window = combat != null && combat.Profile != null
                ? CatchTiming.EffectiveCatchWindow(perfect, combat.Profile.catchWindow)
                : GameConstants.NormalCatchWindow;
            float scale = perfect / GameConstants.PerfectCatchWindow;
            float ideal = Mathf.Min(_profile.idealCatchLead * scale, perfect * 0.6f);
            float lead = _rng.Gaussian(ideal, _profile.catchTimingSigma);
            return Mathf.Clamp(lead, -0.25f, window + 0.25f);
        }

        /// <summary>
        /// Picks the evasive manoeuvre: jump over low balls, slide under chest-high balls while sprinting, otherwise a
        /// sidestep perpendicular to the ball path toward the side that needs the least movement and has room.
        /// Weaker bots sometimes pick a worse option, the wrong side, or hesitate.
        /// </summary>
        private BotThreatResponse ChooseDodge(in BotThreat t, float now, out bool feasible)
        {
            feasible = false;
            var self = Owner;
            if (BotWorld.Has(self, StatusEffectType.Rooted) || BotWorld.Has(self, StatusEffectType.Frozen)) return BotThreatResponse.None;

            bool dodgeDisabled = BotWorld.Has(self, StatusEffectType.DodgeDisabled);
            float skill = Mathf.Clamp01(_profile.dodgeSkill + _traits.DodgeBias);
            float impactHeight = t.ImpactPoint.y - self.Position.y;
            var motor = self.Motor;
            var state = BotWorld.StateOf(self);

            ComputeSidestep(t, out var sideDirection, out float need);
            _dodgeDirection = sideDirection;
            bool sideFeasible = TimeToCover(need, sideDirection) <= t.TimeToImpact;

            // Jump over a low ball.
            if (!dodgeDisabled && impactHeight < lowBallHeight && motor != null && motor.CanJump &&
                state != PlayerStateId.Airborne && t.TimeToImpact > 0.1f && _rng.Chance(0.35f + 0.65f * skill))
            {
                _maneuverLead = Mathf.Max(0.05f, _rng.Gaussian(jumpPressLead, _profile.maneuverTimingSigma));
                _responseStartAt = now;
                feasible = true;
                return BotThreatResponse.Jump;
            }

            // Slide under a chest/head-high ball when already sprinting (momentum carries the slide).
            float height = self.Capsule != null ? self.Capsule.height : 1.8f;
            float slideTop = height * (motor != null && motor.Profile != null ? motor.Profile.slideHeightMultiplier : 0.55f);
            if (!dodgeDisabled && state == PlayerStateId.Sprinting && motor != null && motor.CanSlide &&
                impactHeight > slideTop + 0.15f && t.TimeToImpact > 0.12f && _rng.Chance(skill))
            {
                var v = BotWorld.Planar(self.Velocity);
                if (v.sqrMagnitude > 1f) _dodgeDirection = v.normalized;
                _maneuverLead = Mathf.Max(0.05f, _rng.Gaussian(slidePressLead, _profile.maneuverTimingSigma));
                _responseStartAt = now;
                feasible = true;
                return BotThreatResponse.Slide;
            }

            // Sidestep. Mistakes: wrong side, or a moment of hesitation.
            if (!_rng.Chance(0.5f + 0.5f * skill)) _dodgeDirection = -_dodgeDirection;
            float hesitation = _rng.Chance(skill) ? 0f : (1f - skill) * 0.25f * _rng.Value;
            _responseStartAt = now + hesitation;
            feasible = sideFeasible && TimeToCover(need, sideDirection) + hesitation <= t.TimeToImpact;
            return BotThreatResponse.Sidestep;
        }

        /// <summary>Side (perpendicular to the ball path) that clears the body with the least movement and has room.</summary>
        private void ComputeSidestep(in BotThreat t, out Vector3 direction, out float need)
        {
            var self = Owner;
            var ball = t.Ball;
            var ballPos = ball.transform.position;

            var path = BotWorld.Planar(ball.Velocity);
            if (path.sqrMagnitude < 0.01f) path = BotWorld.Planar(self.Position - ballPos);
            if (path.sqrMagnitude < 1e-6f) path = -self.Forward;
            path.Normalize();

            var perp = Vector3.Cross(Vector3.up, path).normalized;        // right-hand side of the ball path
            float offset = Vector3.Dot(BotWorld.Planar(self.Position - ballPos), perp); // + = already right of the path
            float bodyRadius = self.Capsule != null ? self.Capsule.radius : 0.32f;
            float clearance = bodyRadius + ball.Radius + sidestepClearance;

            float needRight = clearance - offset;
            float needLeft = clearance + offset;
            float roomRight = BotWorld.RoomAlong(self.Position, perp, _confinement);
            float roomLeft = BotWorld.RoomAlong(self.Position, -perp, _confinement);
            bool rightOk = roomRight >= needRight;
            bool leftOk = roomLeft >= needLeft;

            float side;
            if (rightOk && leftOk) side = needRight <= needLeft ? 1f : -1f;
            else if (rightOk) side = 1f;
            else if (leftOk) side = -1f;
            else side = roomRight - needRight >= roomLeft - needLeft ? 1f : -1f;

            direction = perp * side;
            need = Mathf.Max(0f, side > 0f ? needRight : needLeft);
        }

        /// <summary>Seconds to move <paramref name="distance"/> along <paramref name="direction"/> from the current velocity.</summary>
        private float TimeToCover(float distance, Vector3 direction)
        {
            if (distance <= 0f) return 0f;
            var self = Owner;
            var motor = self.Motor;
            var mp = motor != null ? motor.Profile : null;

            float accel = (mp != null ? mp.acceleration : 34f) * (motor != null ? Mathf.Max(0.15f, motor.Traction) : 1f);
            float vmax = (mp != null ? mp.sprintSpeed : 7.4f) * (motor != null ? Mathf.Max(0.1f, motor.SpeedMultiplier) : 1f);
            if (BotWorld.IsCharging(self) && mp != null) vmax *= mp.chargingSpeedMultiplier;
            accel = Mathf.Max(0.5f, accel);
            vmax = Mathf.Max(0.3f, vmax);

            float v0 = Mathf.Clamp(Vector3.Dot(BotWorld.Planar(self.Velocity), direction), 0f, vmax);
            float tAccel = (vmax - v0) / accel;
            float dAccel = v0 * tAccel + 0.5f * accel * tAccel * tAccel;
            if (distance <= dAccel) return (-v0 + Mathf.Sqrt(v0 * v0 + 2f * accel * distance)) / accel;
            return tAccel + (distance - dAccel) / vmax;
        }

        // ================================================================== execution

        private void Execute(ref PlayerIntent intent, float now, float deltaTime)
        {
            var self = Owner;
            var move = Vector3.zero;
            bool sprint = false;
            var look = self.ChestPosition + self.Forward * 10f;

            if (_threatActive)
            {
                ExecuteThreatResponse(ref intent, now, ref move, ref sprint, ref look);
            }
            else
            {
                switch (CurrentBehaviour)
                {
                    case BotBehaviour.Retrieve: ExecuteRetrieve(ref intent, now, ref move, ref sprint, ref look); break;
                    case BotBehaviour.Attack: ExecuteAttack(ref intent, now, deltaTime, ref move, ref sprint, ref look); break;
                    case BotBehaviour.Pass: ExecutePass(ref intent, now, ref move, ref sprint, ref look); break;
                    default: ExecutePosition(now, ref move, ref sprint, ref look); break;
                }
            }

            SetAim(ref intent, look);

            // The throw button sequence continues whatever the behaviour (a bot may keep charging while side-stepping).
            UpdateThrowSequence(ref intent, now);

            ApplyPendingAbility(ref intent);

            // Final move: body separation, confinement guard, 0..1 magnitude.
            move += BotSteering.Separation(self, separationRadius) * 0.6f;
            move.y = 0f;
            if (move.sqrMagnitude > 1f) move.Normalize();
            move = BotSteering.KeepInside(self.Position, move, _confinement, 0.1f);
            intent.Move = move;
            intent.SprintHeld = sprint && move.sqrMagnitude > 0.25f;
        }

        private void ExecuteThreatResponse(ref PlayerIntent intent, float now, ref Vector3 move, ref bool sprint, ref Vector3 look)
        {
            var self = Owner;
            var t = _threat;
            if (t.Ball != null) look = t.Ball.transform.position;

            switch (_response)
            {
                case BotThreatResponse.Catch:
                    // Square up and stand in; press Catch at the planned lead before the ball reaches the catch zone.
                    move = Vector3.zero;
                    if (!_catchPressed && _perception.HasThreat && t.CatchZoneTime <= _plannedCatchLead)
                    {
                        intent.CatchPressed = true;
                        _catchPressed = true;
                    }
                    break;

                case BotThreatResponse.Sidestep:
                    if (now >= _responseStartAt)
                    {
                        move = _dodgeDirection;
                        sprint = !BotWorld.IsCharging(self);
                    }
                    break;

                case BotThreatResponse.Jump:
                    move = _dodgeDirection * 0.5f; // drift off the line while hopping over it
                    if (!_maneuverPressed && _perception.HasThreat && t.TimeToImpact <= _maneuverLead)
                    {
                        intent.JumpPressed = true;
                        _maneuverPressed = true;
                    }
                    break;

                case BotThreatResponse.Slide:
                    move = _dodgeDirection;
                    sprint = true;
                    if (!_maneuverPressed && _perception.HasThreat && t.TimeToImpact <= _maneuverLead)
                    {
                        intent.SlidePressed = true;
                        _maneuverPressed = true;
                    }
                    break;

                default: // Brace
                    move = Vector3.zero;
                    break;
            }

            Destination = self.Position + move * 1.5f;
        }

        private void ExecuteRetrieve(ref PlayerIntent intent, float now, ref Vector3 move, ref bool sprint, ref Vector3 look)
        {
            var self = Owner;
            var ball = ClaimedBall;
            if (ball == null || (ball.State != BallState.Free && ball.State != BallState.Stasis) || BotWorld.HoldsBall(self))
            {
                _forceDecision = true;
                ExecutePosition(now, ref move, ref sprint, ref look);
                return;
            }

            var ballPos = ball.transform.position;
            float bodyRadius = self.Capsule != null ? self.Capsule.radius : 0.32f;
            var walkable = BotWorld.ShrinkPlanar(_confinement, bodyRadius);

            // Run onto the ball (auto pick-up) - leading a rolling ball a little.
            var aimPos = ballPos;
            if (ball.State == BallState.Free && ball.Body != null && !ball.Body.isKinematic)
                aimPos += BotWorld.Planar(ball.Body.GetVelocity()) * 0.25f;
            var destination = BotWorld.ClampPlanar(aimPos, walkable);
            destination.y = self.Position.y;
            Destination = destination;

            move = BotSteering.Seek(self.Position, destination, 0.05f, 0.6f, out _);
            float planar = BotWorld.PlanarDistance(self.Position, ballPos);
            sprint = planar > sprintDistance || now < _openingRushUntil;
            look = ballPos;

            // Give up on a ball chased for too long (stuck against someone, keeps rolling away): ignore it for a while.
            if (now - _claimedBallTime > maxChaseTime)
            {
                _ignoredBall = ball;
                _ignoredBallUntil = now + 3f;
                ClaimedBall = null;
                _forceDecision = true;
            }

            float manualRadius = self.Combat != null && self.Combat.Profile != null ? self.Combat.Profile.manualPickupRadius : 1.6f;
            if (planar <= manualRadius * 0.9f && now >= _nextPickupPressAt)
            {
                float height = ballPos.y - self.Position.y;
                if (height <= stasisReachHeight)
                {
                    // Leap for a ball frozen high in Chrono's stasis field.
                    if (height > 1.9f && self.Motor != null && self.Motor.CanJump && !BotWorld.Has(self, StatusEffectType.DodgeDisabled) &&
                        BotWorld.StateOf(self) != PlayerStateId.Airborne)
                        intent.JumpPressed = true;
                    intent.PickupPressed = true;
                    _nextPickupPressAt = now + pickupRepressInterval;
                }
            }
        }

        private void ExecuteAttack(ref PlayerIntent intent, float now, float deltaTime, ref Vector3 move, ref bool sprint, ref Vector3 look)
        {
            var self = Owner;
            var target = CurrentTarget;
            if (target == null || !target.IsTargetable)
            {
                _forceDecision = true;
                ExecutePosition(now, ref move, ref sprint, ref look);
                return;
            }

            float strafe = _steering.UpdateStrafe(now, deltaTime, _profile.strafeAmplitude, _rng);
            var spot = _steering.AttackSpot(self, target, _inner, _profile, _aggression, attackLineDepthMin, attackLineDepthMax,
                idealRangeMax, strafe, awarenessRadius);
            spot.y = self.Position.y;
            Destination = spot;

            move = BotSteering.Seek(self.Position, spot, arriveRadius * 0.5f, slowRadius, out float distance);
            sprint = distance > sprintDistance && _throwPhase == BotThrowPhase.None;

            bool fooled = _decoyFor == target && _decoyOffset != Vector3.zero;
            look = target.ChestPosition + (fooled ? _decoyOffset : Vector3.zero);
            if (!fooled) intent.DesiredTarget = target; // lets the combat controller lock the right enemy

            if (_throwPhase == BotThrowPhase.None) TryBeginThrow(now, target, distance);
        }

        private void ExecutePass(ref PlayerIntent intent, float now, ref Vector3 move, ref bool sprint, ref Vector3 look)
        {
            ExecutePosition(now, ref move, ref sprint, ref look);
            if (!_pendingPass) return;

            _pendingPass = false;
            _forceDecision = true;
            var receiver = _passReceiver;
            if (_throwPhase != BotThrowPhase.None || !BotWorld.HoldsBall(Owner) || receiver == null) return;

            intent.PassPressed = true;
            intent.DesiredTarget = receiver;
            look = receiver.ChestPosition;
            _passInclination = false;              // one considered pass per possession
            _nextPassAllowedAt = now + 1.5f;       // and never a pass spam if the pass could not happen
        }

        private void ExecutePosition(float now, ref Vector3 move, ref bool sprint, ref Vector3 look)
        {
            var self = Owner;
            var spot = self.IsInfield
                ? _steering.InfieldPositionSpot(self, _inner, _profile, _aggression, now, awarenessRadius)
                : _steering.OutfieldSpot(self, _inner, _profile, now);
            spot.y = self.Position.y;
            Destination = spot;

            // Settle hysteresis: once on the spot, stand and watch (the body then faces the watch point, keeping the
            // catch cone on the threats) until the wandering spot has drifted clearly away.
            float distance = BotWorld.PlanarDistance(self.Position, spot);
            if (_settled && distance < settleRadius)
            {
                move = Vector3.zero;
            }
            else
            {
                move = BotSteering.Seek(self.Position, spot, arriveRadius, slowRadius, out distance);
                _settled = distance <= arriveRadius;
            }
            sprint = distance > sprintDistance * 1.5f;
            look = ChooseWatchPoint();
        }

        /// <summary>What an idle bot keeps its eyes (and catch cone) on: the nearest enemy ball holder, else the nearest enemy.</summary>
        private Vector3 ChooseWatchPoint()
        {
            var self = Owner;
            DodgeballPlayer holder = null, nearest = null;
            float holderDist = float.PositiveInfinity, nearestDist = float.PositiveInfinity;
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                if (!PlayerRegistry.AreEnemies(self, e) || !e.IsInitialized) continue;
                if (!BotWorld.IsPerceivable(self, e, _profile.cloakDetectionRadius)) continue;
                if (e.Health != null && !e.Health.IsAlive) continue;
                float d = BotWorld.PlanarDistance(self.Position, e.Position);
                if (BotWorld.HoldsBall(e) && d < holderDist)
                {
                    holderDist = d;
                    holder = e;
                }
                if (e.IsTargetable && d < nearestDist)
                {
                    nearestDist = d;
                    nearest = e;
                }
            }
            if (holder != null) return holder.ChestPosition;
            if (nearest != null) return nearest.ChestPosition;
            return self.ChestPosition + BotWorld.AttackDirection(self.Team) * 10f;
        }

        // ================================================================== throw sequence

        private void TryBeginThrow(float now, DodgeballPlayer target, float distanceToSpot)
        {
            var self = Owner;
            if (!BotWorld.HoldsBall(self) || now < _throwReadyAt || now - _lastThrowTime < postThrowDelay) return;

            var state = BotWorld.StateOf(self);
            if (state == PlayerStateId.Airborne || state == PlayerStateId.Sliding || state == PlayerStateId.Catching) return;

            float range = BotWorld.PlanarDistance(self.Position, target.Position);
            if (range > maxThrowRange) return;
            // Still closing in on a distant target: keep approaching unless the ball has been held for a while.
            if (range > idealRangeMax && distanceToSpot > 1.5f && now - _possessionStart < maxHoldTime * 0.5f) return;

            _plannedCharge = ChooseChargeTime(now, range, target);
            _throwTarget = target;
            _opportunityRolled = false;
            _throwPhase = BotThrowPhase.Pressing;
        }

        /// <summary>
        /// Charge time: short for close targets (surprise), long for distant ones; Rayne holds up to 2 s for Overcharge;
        /// committed targets are punished quickly; a perfect-catch counter boost is spent before it expires.
        /// </summary>
        private float ChooseChargeTime(float now, float range, DodgeballPlayer target)
        {
            var combat = Owner.Combat;
            var chargeRange = _traits.ChargeTimeOverride != Vector2.zero ? _traits.ChargeTimeOverride : _profile.chargeTimeRange;
            float rangeT = Mathf.InverseLerp(idealRangeMin * 0.6f, idealRangeMax * 1.2f, range);
            float charge = _rng.Jitter(Mathf.Lerp(chargeRange.x, chargeRange.y, rangeT), 0.25f);

            if (IsCommitted(target)) charge *= 0.6f;
            if (combat != null && combat.HasCounterBoost)
                charge = Mathf.Min(charge, Mathf.Max(chargeRange.x, combat.CounterBoostUntil - now - 0.15f));

            float maxCharge = combat != null && combat.Profile != null ? combat.Profile.maxChargeTime : 2.5f;
            return Mathf.Clamp(charge, 0.05f, Mathf.Max(0.05f, maxCharge - 0.05f));
        }

        private static bool IsCommitted(DodgeballPlayer target)
        {
            if (target == null) return false;
            var state = BotWorld.StateOf(target);
            return state == PlayerStateId.Airborne || state == PlayerStateId.ChargingThrow || state == PlayerStateId.Stunned ||
                   state == PlayerStateId.Incapacitated || BotWorld.Has(target, StatusEffectType.Frozen) ||
                   BotWorld.Has(target, StatusEffectType.Rooted);
        }

        /// <summary>
        /// Drives the throw button like a human: ThrowPressed (+Held) on the first frame, ThrowHeld while the state machine
        /// charges, then ThrowReleased once the planned charge is reached (or earlier: the target commits, a ball is about
        /// to hit us, the charge cap is near).
        /// </summary>
        private void UpdateThrowSequence(ref PlayerIntent intent, float now)
        {
            if (_throwPhase == BotThrowPhase.None) return;
            var self = Owner;
            var combat = self.Combat;
            if (combat == null)
            {
                _throwPhase = BotThrowPhase.None;
                return;
            }

            if (_throwPhase == BotThrowPhase.Pressing)
            {
                if (!combat.HasBall)
                {
                    _throwPhase = BotThrowPhase.None;
                    return;
                }
                intent.ThrowPressed = true;
                intent.ThrowHeld = true;
                _throwPhase = BotThrowPhase.Holding;
                _throwPressTime = now;
                AimAtThrowTarget(ref intent, ResolveThrowTarget());
                return;
            }

            // Holding.
            bool charging = combat.IsCharging;
            if (!charging && !combat.HasBall)
            {
                _throwPhase = BotThrowPhase.None; // ball knocked out of our hands (Earthquake Slam, stun...)
                _forceDecision = true;
                return;
            }
            if (!charging && now - _throwPressTime > chargeStartTimeout)
            {
                // The state machine never entered ChargingThrow (e.g. we were airborne): let go and retry later.
                intent.ThrowHeld = false;
                intent.ThrowReleased = true;
                _throwPhase = BotThrowPhase.None;
                _throwReadyAt = now + 0.25f;
                return;
            }

            var target = ResolveThrowTarget();
            float charged = charging ? combat.ChargeSeconds : 0f;
            float cap = combat.Profile != null ? combat.Profile.maxChargeTime : 2.5f;

            bool release = charging && (charged >= _plannedCharge || charged >= cap - 0.05f);

            // Panic release: a ball will hit us before we can finish the charge.
            if (!release && charging && _threatActive && _response != BotThreatResponse.Catch &&
                _threat.TimeToImpact <= panicReleaseTime && charged >= minEarlyReleaseCharge)
                release = true;

            // Opportunism: the target just committed (jumped, started charging, got stunned) - punish it now.
            if (!release && charging && target != null && !_opportunityRolled && charged >= minEarlyReleaseCharge && IsCommitted(target))
            {
                _opportunityRolled = true;
                if (_rng.Chance(_profile.opportunismSkill)) release = true;
            }

            if (!release)
            {
                intent.ThrowHeld = true;
                AimAtThrowTarget(ref intent, target);
                return;
            }

            intent.ThrowHeld = false;
            intent.ThrowReleased = true;
            ApplyReleaseAim(ref intent, target, charged);
            _throwPhase = BotThrowPhase.None;
            _throwTarget = null;
            _lastThrowTime = now;
            _counterAttack = false;
            _forceDecision = true;
        }

        private DodgeballPlayer ResolveThrowTarget()
        {
            if (CurrentTarget != null && CurrentTarget.IsTargetable) return CurrentTarget;
            if (_throwTarget != null && _throwTarget.IsTargetable) return _throwTarget;
            return null;
        }

        /// <summary>While charging: face the target (rough lead, no noise) so the body winds up toward it.</summary>
        private void AimAtThrowTarget(ref PlayerIntent intent, DodgeballPlayer target)
        {
            if (target == null) return;
            bool fooled = _decoyFor == target && _decoyOffset != Vector3.zero;
            var point = target.ChestPosition + BotWorld.Planar(target.Velocity) * 0.25f + (fooled ? _decoyOffset : Vector3.zero);
            SetAim(ref intent, point);
            intent.DesiredTarget = fooled ? null : target;
        }

        /// <summary>
        /// Release aim: lead intercept blended by lead accuracy + Gaussian angular error (+ decoy offset when fooled by
        /// clones). With no target the ball goes straight into the enemy half.
        /// </summary>
        private void ApplyReleaseAim(ref PlayerIntent intent, DodgeballPlayer target, float charged)
        {
            var self = Owner;
            var origin = self.Combat != null ? self.Combat.GetThrowOrigin() : self.ChestPosition;
            Vector3 aim;
            if (target == null)
            {
                aim = self.ChestPosition + BotWorld.AttackDirection(self.Team) * 12f;
                intent.DesiredTarget = null;
            }
            else
            {
                bool fooled = _decoyFor == target && _decoyOffset != Vector3.zero;
                aim = BotTargeting.ComputeAimPoint(self, target, origin, charged, _profile, _rng,
                    fooled ? _decoyOffset : Vector3.zero, true);
                intent.DesiredTarget = fooled ? null : target;
            }

            var dir = aim - origin;
            intent.AimPoint = aim;
            intent.AimDirection = dir.sqrMagnitude > 1e-6f ? dir.normalized : self.Forward;
            _lastReleaseAim = aim;
            _lastReleaseTime = Time.time;
        }

        // ================================================================== one-shot presses / aim

        private void ApplyPendingAbility(ref PlayerIntent intent)
        {
            if (!_pendingSkill && !_pendingUltimate) return;
            // Never mix an ability press into the frame that presses or releases a throw; try again next frame.
            if (intent.ThrowPressed || intent.ThrowReleased) return;
            if (_throwPhase != BotThrowPhase.None)
            {
                _pendingSkill = _pendingUltimate = false;
                return;
            }

            if (_pendingUltimate) intent.UltimatePressed = true;
            else intent.SkillPressed = true;
            _pendingSkill = _pendingUltimate = false;

            // Targeted abilities (Swap Places, ability throws...) use the explicit target / aim of this frame.
            var target = _abilityTarget != null && _abilityTarget.IsTargetable ? _abilityTarget : CurrentTarget;
            _abilityTarget = null;
            if (target != null && BotWorld.IsPerceivable(Owner, target, _profile.cloakDetectionRadius))
            {
                intent.DesiredTarget = target;
                SetAim(ref intent, target.ChestPosition);
            }
        }

        private void SetAim(ref PlayerIntent intent, Vector3 point)
        {
            var origin = Owner.ChestPosition;
            var dir = point - origin;
            if (dir.sqrMagnitude < 1e-4f) return;
            intent.AimDirection = dir.normalized;
            intent.AimPoint = point;
        }

        private void FaceEnemyHalf(ref PlayerIntent intent)
        {
            if (Owner == null || !Owner.Team.IsValid()) return;
            SetAim(ref intent, Owner.ChestPosition + BotWorld.AttackDirection(Owner.Team) * 10f);
        }

        // ================================================================== gizmos

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos || Owner == null || !Owner.IsInitialized) return;

            var feet = Owner.Position + Vector3.up * 0.05f;
            var chest = Owner.ChestPosition;

            // Confinement.
            Gizmos.color = new Color(0.6f, 0.6f, 0.6f, 0.5f);
            Gizmos.DrawWireCube(new Vector3(_confinement.center.x, feet.y, _confinement.center.z),
                new Vector3(_confinement.size.x, 0.02f, _confinement.size.z));

            // Destination, coloured by behaviour.
            Gizmos.color = BehaviourColor(CurrentBehaviour);
            var dest = Destination;
            dest.y = feet.y;
            Gizmos.DrawLine(feet, dest);
            Gizmos.DrawWireSphere(dest, 0.25f);

            // Attack target (and the decoy position when fooled by clones).
            if (CurrentTarget != null)
            {
                Gizmos.color = new Color(1f, 0.2f, 0.15f);
                Gizmos.DrawLine(chest, CurrentTarget.ChestPosition);
                if (_decoyFor == CurrentTarget && _decoyOffset != Vector3.zero)
                {
                    Gizmos.color = new Color(1f, 0.55f, 0f);
                    Gizmos.DrawWireSphere(CurrentTarget.ChestPosition + _decoyOffset, 0.35f);
                }
            }

            // Last release aim point (1 s).
            if (Application.isPlaying && Time.time - _lastReleaseTime < 1f)
            {
                Gizmos.color = new Color(1f, 0.85f, 0.1f);
                const float s = 0.2f;
                Gizmos.DrawLine(_lastReleaseAim - Vector3.right * s, _lastReleaseAim + Vector3.right * s);
                Gizmos.DrawLine(_lastReleaseAim - Vector3.up * s, _lastReleaseAim + Vector3.up * s);
                Gizmos.DrawLine(_lastReleaseAim - Vector3.forward * s, _lastReleaseAim + Vector3.forward * s);
            }

            // Ball being chased.
            if (ClaimedBall != null)
            {
                Gizmos.color = new Color(1f, 0.95f, 0.3f);
                Gizmos.DrawLine(feet, ClaimedBall.transform.position);
                Gizmos.DrawWireSphere(ClaimedBall.transform.position, 0.2f);
            }

            // Threat: ball -> predicted impact, and the chosen evasive direction.
            if (_threatActive && _threat.Ball != null)
            {
                Gizmos.color = new Color(1f, 0f, 0.9f);
                Gizmos.DrawLine(_threat.Ball.transform.position, _threat.ImpactPoint);
                Gizmos.DrawWireSphere(_threat.ImpactPoint, 0.12f);
                if (_response == BotThreatResponse.Sidestep || _response == BotThreatResponse.Jump || _response == BotThreatResponse.Slide)
                {
                    Gizmos.color = Color.cyan;
                    Gizmos.DrawRay(feet, _dodgeDirection * 1.5f);
                }
                else if (_response == BotThreatResponse.Catch)
                {
                    Gizmos.color = Color.green;
                    Gizmos.DrawWireSphere(chest, 0.3f);
                }
            }
        }

        private static Color BehaviourColor(BotBehaviour behaviour)
        {
            switch (behaviour)
            {
                case BotBehaviour.ThreatResponse: return new Color(1f, 0f, 0.9f);
                case BotBehaviour.Retrieve: return new Color(1f, 0.95f, 0.3f);
                case BotBehaviour.Attack: return new Color(1f, 0.25f, 0.2f);
                case BotBehaviour.Pass: return new Color(0.3f, 0.6f, 1f);
                case BotBehaviour.Position: return new Color(0.3f, 1f, 0.45f);
                default: return Color.gray;
            }
        }
    }
}
