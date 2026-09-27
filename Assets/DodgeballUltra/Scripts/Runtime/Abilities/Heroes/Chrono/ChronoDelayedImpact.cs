using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.Juice;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Chrono - PASSIVE [Delayed Impact] (延遲淘汰): elimination processing is delayed 2 s upon being hit; if a teammate
    /// catches a ball during this window, the elimination is cancelled.
    /// <para>
    /// Implemented as a permanent <see cref="IEliminationInterceptor"/> on Chrono's <see cref="PlayerHealth"/>. When an
    /// elimination arrives from a ball hit, an ability or the frozen second hit, the interceptor returns
    /// <see cref="HitOutcome.EliminationDelayed"/>, stores the <see cref="EliminationContext"/>, flags the health as
    /// pending (<see cref="PlayerHealth.SetEliminationPending"/>) and starts a clock: RewindTrail VFX on Chrono and an
    /// accelerating clock-tick SFX. Only one elimination can be pending at a time; further attempts during the window are
    /// absorbed into it.
    /// </para>
    /// <list type="bullet">
    /// <item>A TEAMMATE (not Chrono) catches a ball inside the window (<see cref="BallCaughtEvent"/>): the elimination is
    /// cancelled - pending flag cleared, HP restored with <see cref="PlayerHealth.Revive"/>, and
    /// <see cref="PlayerRevivedEvent"/> (<see cref="RevivalCause.DelayedImpactCancelled"/>) published.</item>
    /// <item>The window expires: <see cref="PlayerHealth.CommitPendingElimination"/> resolves it with cause
    /// <see cref="EliminationCause.DelayedImpact"/> (original attacker, impulse and point kept for credit and ragdoll).</item>
    /// <item>The round ends or resets: the pending state is cleared silently.</item>
    /// </list>
    /// </summary>
    [Serializable]
    public sealed class ChronoDelayedImpact : AbilityBase, IEliminationInterceptor
    {
        [Header("Delayed Impact")]
        [Tooltip("Seconds the elimination is postponed. Spec: 2 s.")]
        [Range(0.25f, 6f)] public float delaySeconds = 2f;

        [Tooltip("Fraction of max HP restored when a teammate's catch cancels the elimination.")]
        [Range(0.05f, 1f)] public float cancelRestoreHpFraction = 1f;

        [Tooltip("Minimum quality of the teammate's catch that cancels the elimination (Normal = any successful catch).")]
        public CatchQuality minimumCatchQuality = CatchQuality.Normal;

        [Tooltip("Delay eliminations caused by ball hits.")]
        public bool delayBallHits = true;

        [Tooltip("Delay eliminations caused by abilities (shockwaves, turret shots...).")]
        public bool delayAbilityEliminations = true;

        [Tooltip("Delay the frozen second-hit elimination (Elsa's Glacier Freeze).")]
        public bool delayFrozenEliminations = true;

        [Tooltip("Elimination-interceptor priority (lower runs first). Runs late so explicit preventions win.")]
        public int interceptorPriority = 100;

        [Header("Presentation")]
        [Tooltip("Clock-tick / heartbeat sound repeated during the window.")]
        public SfxId pulseSfx = SfxId.Countdown;

        [Tooltip("Interval (s) between ticks at the start of the window.")]
        [Range(0.1f, 1.5f)] public float pulseIntervalStart = 0.5f;

        [Tooltip("Interval (s) between ticks at the end of the window (the clock speeds up).")]
        [Range(0.05f, 1f)] public float pulseIntervalEnd = 0.18f;

        [Tooltip("Tick volume.")]
        [Range(0f, 1f)] public float pulseVolume = 0.7f;

        [Tooltip("Tick pitch (low = heavy heartbeat, high = watch tick).")]
        [Range(0.3f, 2f)] public float pulsePitch = 0.6f;

        [Tooltip("Tint of the clock / rewind effects (clockwork gold).")]
        public Color clockTint = new Color(0.95f, 0.78f, 0.38f, 1f);

        [Tooltip("TimeRewind screen pulse for the local player when the delay starts / is cancelled.")]
        [Range(0f, 1f)] public float localPulseIntensity = 0.6f;

        // ------------------------------------------------------------------ runtime state
        [NonSerialized] private bool _pending;
        [NonSerialized] private float _pendingUntil;
        [NonSerialized] private float _pendingStarted;
        [NonSerialized] private EliminationContext _pendingContext;
        [NonSerialized] private float _nextPulse;
        [NonSerialized] private VfxHandle _clockVfx;
        [NonSerialized] private PlayerHealth _registeredHealth;

        /// <summary>Public parameterless constructor (required by [SerializeReference] and the roster factory).</summary>
        public ChronoDelayedImpact() { }

        /// <summary><see cref="IEliminationInterceptor.Priority"/>.</summary>
        public int Priority => interceptorPriority;

        /// <summary>True while an elimination is pending (HUD countdown, AI: teammates should try to catch).</summary>
        public bool IsPending => _pending;

        /// <summary>Seconds until the pending elimination resolves (0 when none).</summary>
        public float PendingTimeRemaining => _pending ? Mathf.Max(0f, _pendingUntil - Now) : 0f;

        /// <summary>0..1 progress of the pending window (1 = about to resolve).</summary>
        public float PendingProgress => _pending && delaySeconds > 0f ? Mathf.Clamp01((Now - _pendingStarted) / delaySeconds) : 0f;

        // ------------------------------------------------------------------ lifecycle

        protected override void OnEquip()
        {
            EnsureRegistered();
            Listen<BallCaughtEvent>(OnBallCaught);
            Listen<RoundEndedEvent>(OnRoundEnded);
        }

        protected override void OnUnequip()
        {
            // Hero swap / despawn: never leave the health stuck in a pending state.
            ClearPending(true);
            if (_registeredHealth != null) _registeredHealth.RemoveEliminationInterceptor(this);
            _registeredHealth = null;
        }

        protected override void OnRoundReset() => ClearPending(true);

        /// <summary>Passives never cast.</summary>
        protected override void OnCast() { }

        /// <summary>Every frame: late registration, clock ticks and window expiry.</summary>
        protected override void OnTick(float deltaTime)
        {
            EnsureRegistered();
            if (!_pending) return;

            var health = Owner.Health;
            // Resolved elsewhere (forfeit, round flow, another system cleared the flag) or moved out: drop our state.
            if (health == null || !health.IsEliminationPending || !Owner.IsInfield)
            {
                ClearPending(false);
                return;
            }

            float now = Now;
            if (now >= _pendingUntil)
            {
                Commit();
                return;
            }

            if (now >= _nextPulse)
            {
                float k = PendingProgress;
                _nextPulse = now + Mathf.Lerp(pulseIntervalStart, pulseIntervalEnd, k);
                AudioManager.PlayAt(pulseSfx, Owner.Position, pulseVolume, pulsePitch * Mathf.Lerp(1f, 1.25f, k));
            }
        }

        public override float EvaluateAIUtility(in AbilityAIContext ctx) => 0f;

        // ------------------------------------------------------------------ IEliminationInterceptor

        /// <summary>Delays eliminations from ball hits, abilities and frozen second hits.</summary>
        public HitOutcome Intercept(PlayerHealth health, ref EliminationContext context)
        {
            if (Owner == null || health == null) return HitOutcome.Eliminated;
            if (context.Victim != null && context.Victim != Owner) return HitOutcome.Eliminated;
            if (!ShouldDelay(context.Cause)) return HitOutcome.Eliminated;

            // One pending at a time: further attempts are absorbed into the running window.
            if (_pending) return HitOutcome.EliminationDelayed;

            // No point delaying outside live play (the round is over anyway).
            var match = MatchManager.Instance;
            if (match != null && !match.IsPlaying) return HitOutcome.Eliminated;

            _pending = true;
            _pendingStarted = Now;
            _pendingUntil = Now + delaySeconds;
            _pendingContext = context;
            _nextPulse = Now;

            health.SetEliminationPending(true);

            _clockVfx = VfxManager.SpawnAttached(VfxId.RewindTrail, Owner.transform, Vector3.up * 1.0f, 1f, clockTint, delaySeconds);
            if (Owner.IsLocalPlayer && localPulseIntensity > 0f) ScreenFx.Pulse(ScreenPulse.TimeRewind, localPulseIntensity, 0.35f);
            return HitOutcome.EliminationDelayed;
        }

        // ------------------------------------------------------------------ event handlers

        private void OnBallCaught(BallCaughtEvent e)
        {
            if (!_pending || Owner == null) return;
            if (e.Catcher == null || !IsAlly(e.Catcher)) return; // teammates only (IsAlly excludes Chrono)
            if (e.Quality < minimumCatchQuality || e.Quality == CatchQuality.Miss) return;
            CancelByCatch(e.Catcher);
        }

        private void OnRoundEnded(RoundEndedEvent e) => ClearPending(true);

        // ------------------------------------------------------------------ resolution

        private void CancelByCatch(DodgeballPlayer catcher)
        {
            var health = Owner.Health;
            StopClock();
            _pending = false;
            if (health == null) return;

            health.SetEliminationPending(false);
            health.Revive(cancelRestoreHpFraction);

            GameEvents.Publish(new PlayerRevivedEvent { Player = Owner, Reviver = catcher, Cause = RevivalCause.DelayedImpactCancelled });

            VfxManager.Spawn(VfxId.ReviveBeam, Owner.Position, Quaternion.identity, 1f, clockTint);
            VfxManager.Spawn(VfxId.RewindTrail, Owner.Position + Vector3.up * 1.0f, Owner.Rotation, 1.2f, clockTint);
            AudioManager.PlayAt(SfxId.Rewind, Owner.Position, 0.9f, 1.1f);
            if (Owner.IsLocalPlayer && localPulseIntensity > 0f) ScreenFx.Pulse(ScreenPulse.TimeRewind, localPulseIntensity, 0.5f);
        }

        private void Commit()
        {
            var health = Owner.Health;
            var context = _pendingContext;
            StopClock();
            _pending = false;
            if (health == null) return;

            context.Victim = Owner;
            context.Cause = EliminationCause.DelayedImpact;
            health.CommitPendingElimination(context);
        }

        /// <param name="restoreHealthFlag">Also clear the health's pending flag (round flow / unequip).</param>
        private void ClearPending(bool restoreHealthFlag)
        {
            bool was = _pending;
            _pending = false;
            StopClock();
            if (was && restoreHealthFlag && Owner != null && Owner.Health != null) Owner.Health.SetEliminationPending(false);
        }

        private void StopClock()
        {
            VfxManager.StopEffect(_clockVfx);
            _clockVfx = default;
        }

        private bool ShouldDelay(EliminationCause cause)
        {
            switch (cause)
            {
                case EliminationCause.BallHit: return delayBallHits;
                case EliminationCause.Ability: return delayAbilityEliminations;
                case EliminationCause.Frozen: return delayFrozenEliminations;
                default: return false; // tackles, out of bounds, forfeits and the delayed resolution itself go through
            }
        }

        /// <summary>
        /// Registers the interceptor once the player's health exists (abilities can be equipped before the health component
        /// is initialised during <see cref="DodgeballPlayer.Initialize"/>).
        /// </summary>
        private void EnsureRegistered()
        {
            if (_registeredHealth != null || Owner == null || Owner.Health == null) return;
            _registeredHealth = Owner.Health;
            _registeredHealth.AddEliminationInterceptor(this);
        }
    }
}
