using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Events;
using DodgeballUltra.Juice;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Specter - ULTIMATE [Time Reversal] (時間倒轉): if Specter is eliminated within 3 s of casting, time rewinds and
    /// Specter's position and HP return to the cast moment.
    /// <para>
    /// On cast an anchor snapshot is taken (position, rotation, velocity, HP - through the player's
    /// <see cref="TimeRewindRecorder.Capture"/> when available, overwritten with the exact live values) and the ability
    /// registers itself as an <see cref="IEliminationInterceptor"/> for the window. When Specter would be eliminated
    /// (any cause except a forfeit) the interceptor claims it (<see cref="HitOutcome.EliminationPrevented"/>) and:
    /// </para>
    /// <list type="number">
    /// <item>restores the anchor HP (reviving first if the pipeline already flagged the elimination),</item>
    /// <item>optionally clears Frozen/Stunned gained after the cast and releases the matching stun/incapacitation,</item>
    /// <item>teleports Specter back to the anchor, clamped to the current zone,</item>
    /// <item>plays the rewind: RewindTrail VFX along the recorded path, a TimeRewind screen pulse, the rewind SFX and a
    /// cool hit-flash, and publishes <see cref="PlayerRevivedEvent"/> (<see cref="RevivalCause.TimeReversal"/>),</item>
    /// <item>grants a short invulnerability so the same volley cannot finish the job, then ends the ability.</item>
    /// </list>
    /// If nothing happens the ultimate expires silently when the window closes.
    /// <para>
    /// Registration changes never happen inside the health pipeline: the interceptor and the grace hit filter stay
    /// registered for the whole active phase and are removed in <see cref="OnEnd"/>, which runs from the ability tick.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class SpecterTimeReversal : AbilityBase, IEliminationInterceptor, IIncomingHitFilter
    {
        [Header("Time Reversal")]
        [Tooltip("Window (s) after casting during which an elimination is reversed, used when AbilityData.duration is 0. Spec: 3 s.")]
        [Range(0.5f, 10f)] public float reversalWindow = 3f;

        [Tooltip("Invulnerability (s) after the rewind so the same volley cannot immediately eliminate Specter again.")]
        [Range(0f, 2f)] public float postRewindInvulnerability = 0.4f;

        [Tooltip("Minimum HP restored (guards against an anchor taken at very low HP).")]
        [Min(1f)] public float minimumRestoredHp = 1f;

        [Tooltip("Also remove Frozen / Stunned gained after the cast (they did not exist at the cast moment).")]
        public bool clearDisablesOnRewind = true;

        [Tooltip("Unblockable balls ignore the post-rewind invulnerability (consistent with evasion rules).")]
        public bool unblockableIgnoresGrace = true;

        [Tooltip("Elimination-interceptor / hit-filter priority (lower runs first): the rewind claims eliminations early.")]
        public int priority = -50;

        [Header("Presentation")]
        [Tooltip("RewindTrail bursts sampled along the recorded path between the elimination point and the anchor.")]
        [Range(0, 16)] public int trailSamples = 6;

        [Tooltip("Seconds between faint 'anchor echo' bursts at the cast position while the window is open (0 = off).")]
        [Range(0f, 3f)] public float anchorEchoInterval = 0.75f;

        [Tooltip("TimeRewind screen pulse when the local player is Specter.")]
        [Range(0f, 1f)] public float localPulseIntensity = 1f;

        [Tooltip("TimeRewind screen pulse for everybody else (the rewind is visible to all).")]
        [Range(0f, 1f)] public float remotePulseIntensity = 0.35f;

        [Tooltip("Screen pulse duration (s, unscaled).")]
        [Range(0.1f, 2f)] public float pulseDuration = 0.6f;

        [Tooltip("Tint of the rewind trail, anchor echoes and flash (cool silver-blue).")]
        public Color rewindTint = new Color(0.7f, 0.82f, 1f, 1f);

        [Header("AI")]
        [Tooltip("Bots cast when an enemy ball will reach them within this many seconds.")]
        [Range(0.2f, 3f)] public float aiThreatWindow = 1.1f;

        [Tooltip("Bots also cast when an enemy within this distance (m) is charging a throw.")]
        [Range(2f, 30f)] public float aiChargingEnemyRange = 16f;

        // ------------------------------------------------------------------ runtime state
        [NonSerialized] private RewindSnapshot _anchor;
        [NonSerialized] private float _windowEnd;
        [NonSerialized] private bool _triggered;
        [NonSerialized] private float _graceEnd;
        [NonSerialized] private float _nextEcho;
        [NonSerialized] private PlayerHealth _registeredHealth;

        /// <summary>Public parameterless constructor (required by [SerializeReference] and the roster factory).</summary>
        public SpecterTimeReversal() { }

        /// <summary>Reversal window: AbilityData.duration when set, else <see cref="reversalWindow"/>.</summary>
        public override float Duration => Data != null && Data.duration > 0f ? Data.duration : reversalWindow;

        /// <summary>Shared by <see cref="IEliminationInterceptor.Priority"/> and <see cref="IIncomingHitFilter.Priority"/>.</summary>
        public int Priority => priority;

        /// <summary>True while the reversal window is open and unused.</summary>
        public bool IsArmed => IsActive && !_triggered && Now <= _windowEnd;

        /// <summary>Seconds left in the reversal window (0 when not armed).</summary>
        public float WindowRemaining => IsArmed ? Mathf.Max(0f, _windowEnd - Now) : 0f;

        /// <summary>World position Specter will return to (valid while <see cref="IsArmed"/>).</summary>
        public Vector3 AnchorPosition => _anchor.Position;

        // ------------------------------------------------------------------ lifecycle

        protected override void OnCast()
        {
            _anchor = TakeSnapshot();
            _windowEnd = Now + Duration;
            _triggered = false;
            _graceEnd = 0f;
            _nextEcho = Now;

            // The base Active timer (Duration) closes the window, so the HUD shows a proper countdown; a triggered rewind
            // switches to HoldActive until the post-rewind grace has elapsed (see PerformRewind / OnTick).
            Register();

            VfxManager.Spawn(VfxId.RewindTrail, Owner.Position + Vector3.up * 0.9f, Owner.Rotation, 0.8f, rewindTint);
            AudioManager.PlayAt(SfxId.Rewind, Owner.Position, 0.45f, 1.3f);
        }

        protected override void OnTick(float deltaTime)
        {
            float now = Now;
            if (_triggered)
            {
                if (now >= _graceEnd) EndAbility();
                return;
            }

            if (now > _windowEnd)
            {
                EndAbility(); // expired silently
                return;
            }

            // Faint echo at the anchor so everyone can read where Specter will snap back to.
            if (anchorEchoInterval > 0f && now >= _nextEcho)
            {
                _nextEcho = now + anchorEchoInterval;
                VfxManager.Spawn(VfxId.RewindTrail, _anchor.Position + Vector3.up * 0.9f, _anchor.Rotation, 0.45f, rewindTint);
            }
        }

        protected override void OnEnd(bool interrupted)
        {
            Unregister();
            Owner.Status?.Remove(StatusEffectType.Invulnerable, this);
        }

        protected override void OnUnequip() => Unregister();

        // ------------------------------------------------------------------ IEliminationInterceptor

        /// <summary>Claims Specter's elimination while armed and rewinds to the anchor.</summary>
        public HitOutcome Intercept(PlayerHealth health, ref EliminationContext context)
        {
            if (!IsArmed || context.Cause == EliminationCause.Forfeit) return HitOutcome.Eliminated;
            if (health == null || (context.Victim != null && context.Victim != Owner)) return HitOutcome.Eliminated;

            PerformRewind(health);
            return HitOutcome.EliminationPrevented;
        }

        // ------------------------------------------------------------------ IIncomingHitFilter

        /// <summary>Post-rewind grace: negates hits for <see cref="postRewindInvulnerability"/> seconds.</summary>
        public void FilterHit(ref HitContext hit)
        {
            if (!_triggered || hit.Cancelled || Now >= _graceEnd) return;
            if (hit.Unblockable && unblockableIgnoresGrace) return;
            hit.Cancelled = true;
            hit.CancelReason = "Time Reversal";
        }

        // ------------------------------------------------------------------ AI

        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || Owner == null || !Owner.IsInfield) return 0f;
            float w = Data.aiWeight;

            // Imminent incoming ball: cast the insurance now.
            if (ctx.IncomingBall != null && ctx.IncomingTimeToImpact <= aiThreatWindow) return w;

            // An enemy is winding up a throw nearby: likely target soon.
            var enemy = ctx.NearestEnemy;
            if (enemy != null && enemy.Combat != null && enemy.Combat.IsCharging && ctx.NearestEnemyDistance <= aiChargingEnemyRange)
                return w * 0.8f;

            // Last one standing: never waste the ultimate, but keep it for real danger.
            return 0f;
        }

        // ------------------------------------------------------------------ rewind

        private RewindSnapshot TakeSnapshot()
        {
            RewindSnapshot snap = default;
            var recorder = Owner.Rewind;
            if (recorder != null && recorder.Capture != null)
            {
                try { snap = recorder.Capture(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }

            // Direct fields are authoritative (the recorder's capture may be a sampled approximation).
            snap.Time = Now;
            snap.Position = Owner.Position;
            snap.Rotation = Owner.Rotation;
            snap.Velocity = Owner.Velocity;
            snap.Hp = Owner.Health != null ? Owner.Health.CurrentHp : snap.Hp;
            if (Owner.StateMachine != null) snap.State = (int)Owner.StateMachine.Current;
            return snap;
        }

        private void PerformRewind(PlayerHealth health)
        {
            _triggered = true;
            _graceEnd = Now + postRewindInvulnerability;
            HoldActive(); // keep the grace filter alive past the window if needed; OnTick ends it

            Vector3 from = Owner.Position;
            Vector3 to = AbilityUtil.ClampToPlayerZone(Owner, _anchor.Position);
            float floor = AbilityUtil.GroundPoint(to).y;
            if (to.y < floor) to.y = floor;

            // 1) HP back to the anchor value.
            float hp = Mathf.Max(minimumRestoredHp, _anchor.Hp);
            if (health.MaxHp > 0f) hp = Mathf.Min(hp, health.MaxHp);
            if (health.IsEliminated && health.MaxHp > 0f) health.Revive(hp / health.MaxHp);
            health.SetHpSilently(hp);

            // 2) Disables that did not exist at the anchor moment.
            if (clearDisablesOnRewind) ClearDisables();

            // 3) Rewind playback VFX along the recorded path (before teleporting, while history is continuous).
            SpawnPathTrail(from, to);

            // 4) Snap back.
            Owner.Teleport(to, ChronoTimeUtil.YawOnly(_anchor.Rotation, Owner.Rotation));
            if (postRewindInvulnerability > 0f)
                Owner.Status?.Apply(StatusEffectType.Invulnerable, postRewindInvulnerability, 1f, this);

            // 5) Presentation.
            VfxManager.Spawn(VfxId.RewindTrail, to + Vector3.up * 0.9f, Owner.Rotation, 1.2f, rewindTint);
            AudioManager.PlayAt(SfxId.Rewind, to, 1f, 1f);
            float pulse = Owner.IsLocalPlayer ? localPulseIntensity : remotePulseIntensity;
            if (pulse > 0f) ScreenFx.Pulse(ScreenPulse.TimeRewind, pulse, pulseDuration);
            if (JuiceManager.Instance != null) JuiceManager.Instance.Flash(Owner, rewindTint, 0.08f);

            GameEvents.Publish(new PlayerRevivedEvent { Player = Owner, Reviver = Owner, Cause = RevivalCause.TimeReversal });
        }

        private void ClearDisables()
        {
            var status = Owner.Status;
            if (status != null)
            {
                status.Remove(StatusEffectType.Frozen);
                status.Remove(StatusEffectType.Stunned);
            }

            var fsm = Owner.StateMachine;
            if (fsm == null) return;
            if (fsm.IsIn(PlayerStateId.Stunned))
            {
                fsm.ResetToGrounded();
            }
            else if (fsm.IsIn(PlayerStateId.Incapacitated) && fsm.IncapacitationReason != IncapacitationReason.Eliminated &&
                     fsm.IncapacitationReason != IncapacitationReason.RoundTransition)
            {
                fsm.ResetToGrounded();
            }
        }

        /// <summary>RewindTrail bursts along the path Specter actually took since the cast (recorded history).</summary>
        private void SpawnPathTrail(Vector3 from, Vector3 to)
        {
            if (trailSamples <= 0) return;
            var recorder = Owner.Rewind;
            float start = _anchor.Time;
            float end = Now;

            for (int i = 0; i < trailSamples; i++)
            {
                float k = (i + 0.5f) / trailSamples;
                Vector3 p;
                if (recorder != null && end > start && recorder.TryGetSnapshotAt(Mathf.Lerp(end, start, k), out RewindSnapshot s))
                    p = s.Position;
                else
                    p = Vector3.Lerp(from, to, k); // no history: straight line
                VfxManager.Spawn(VfxId.RewindTrail, p + Vector3.up * 0.9f, Quaternion.identity, Mathf.Lerp(0.9f, 0.5f, k), rewindTint);
            }
        }

        private void Register()
        {
            if (_registeredHealth != null || Owner.Health == null) return;
            _registeredHealth = Owner.Health;
            _registeredHealth.AddEliminationInterceptor(this);
            _registeredHealth.AddHitFilter(this);
        }

        private void Unregister()
        {
            if (_registeredHealth == null) return;
            _registeredHealth.RemoveEliminationInterceptor(this);
            _registeredHealth.RemoveHitFilter(this);
            _registeredHealth = null;
        }
    }
}
