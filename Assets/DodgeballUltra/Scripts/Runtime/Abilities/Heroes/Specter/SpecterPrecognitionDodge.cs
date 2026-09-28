using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Juice;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Specter - SKILL [Precognition Dodge] (預知閃避): a 0.8 s invincible sliding dodge that auto-evades all incoming balls
    /// (cooldown 8 s).
    /// <para>
    /// On cast Specter gets the <see cref="StatusEffectType.Invulnerable"/> status for the dodge window and registers
    /// itself as an <see cref="IIncomingHitFilter"/> that cancels every non-<see cref="HitContext.Unblockable"/> hit during
    /// that window (Rayne's Hyperbeam is, by definition, unblockable and ignores evasion). The body drops into a
    /// momentum-preserving ability slide (<see cref="PlayerMotor.TryStartSlide(Vector3, float, float, bool)"/>, which
    /// ignores the normal slide cooldown so the skill never fizzles) along the move input, or - without input - sideways
    /// out of the flight line of the most threatening ball, choosing the side with room inside the court. The slide starts
    /// at <see cref="dodgeSpeed"/> (never slower than the current momentum), lasts the dodge window and bleeds speed through
    /// the motor's slide friction; the state machine is moved into Sliding so a throw wind-up or catch stance cannot cut it
    /// short. Cast in the air it becomes a short planar air-dodge with the same invulnerability.
    /// </para>
    /// <para>
    /// Evasion feedback (burst where the ball would have struck, <see cref="EvadedThisDodge"/>) is driven by
    /// <see cref="PlayerHealth.HitReceived"/> with <see cref="HitOutcome.Negated"/>, because the health pipeline negates
    /// Invulnerable targets before any filter runs; the filter itself is the fallback when no status controller exists.
    /// </para>
    /// <para>
    /// The dodge respects crowd control: it cannot be used while <see cref="StatusEffectType.DodgeDisabled"/> (Elsa's
    /// Absolute Zero) or <see cref="StatusEffectType.Rooted"/>. Presentation: a DodgeAfterimage particle trail plus
    /// fading translucent body afterimages baked from the realistic skinned mesh, a slide whoosh and small afterimage
    /// bursts where evaded balls would have struck.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class SpecterPrecognitionDodge : AbilityBase, IIncomingHitFilter
    {
        [Header("Dodge")]
        [Tooltip("Invulnerable dodge window (s), used when the AbilityData duration is 0. Spec: 0.8 s.")]
        [Range(0.1f, 2f)] public float dodgeDuration = 0.8f;

        [Tooltip("Planar speed (m/s) the slide is kicked to. The motor's slide friction bleeds it off " +
                 "(7.5 m/s over 0.8 s with 6.5 m/s² friction ≈ 4 m of travel).")]
        [Range(3f, 14f)] public float dodgeSpeed = 7.5f;

        [Tooltip("Planar speed (m/s) of the dodge when cast in the air (no floor to slide on: a short air-dodge).")]
        [Range(0f, 10f)] public float airDodgeSpeed = 5f;

        [Tooltip("Move-input magnitude above which the dodge follows the input instead of auto-evading sideways.")]
        [Range(0.05f, 0.9f)] public float inputThreshold = 0.25f;

        [Tooltip("How far ahead (m) the dodge direction is checked against the court lines.")]
        [Range(0.5f, 6f)] public float roomProbeDistance = 2.5f;

        [Tooltip("Prediction horizon (s) when looking for the most threatening ball to dodge away from.")]
        [Range(0.2f, 3f)] public float threatHorizon = 1.5f;

        [Tooltip("Disarm the catch stance when dodging (you commit to evading, not catching).")]
        public bool cancelCatchStance = true;

        [Header("Evasion")]
        [Tooltip("Unblockable balls (Rayne's Hyperbeam) still connect: the spec defines them as ignoring evasion.")]
        public bool unblockablePierces = true;

        [Tooltip("Hit-filter priority (lower runs first). Evasion runs before shields and damage scaling.")]
        public int filterPriority = -100;

        [Header("AI")]
        [Tooltip("Bots dodge when the incoming ball is at most this many seconds from impact.")]
        [Range(0.1f, 1.5f)] public float aiReactionWindow = 0.55f;

        [Tooltip("Ball speed (km/h) below which bots would rather attempt a catch than burn the dodge.")]
        [Range(0f, 150f)] public float aiPreferCatchBelowKmh = 65f;

        [Header("Presentation")]
        [Tooltip("Seconds between translucent body afterimages (0 = particle trail only).")]
        [Range(0f, 0.5f)] public float afterimageInterval = 0.09f;

        [Tooltip("Lifetime (s) of each body afterimage.")]
        [Range(0.1f, 1.5f)] public float afterimageLifetime = 0.4f;

        [Tooltip("Tint of the afterimages and trail (translucent cool silver).")]
        public Color afterimageTint = new Color(0.76f, 0.86f, 1f, 0.45f);

        [Tooltip("Scale of the burst spawned where an evaded ball would have hit.")]
        [Range(0f, 2f)] public float evadeBurstScale = 0.6f;

        [Tooltip("Volume of the slide whoosh.")]
        [Range(0f, 1f)] public float sfxVolume = 0.9f;

        [Tooltip("Subtle desaturating 'precognition' screen pulse for the local player (0 = off).")]
        [Range(0f, 1f)] public float localScreenPulse = 0.25f;

        // ------------------------------------------------------------------ runtime state
        [NonSerialized] private bool _filterRegistered;
        [NonSerialized] private PlayerHealth _filterHealth;
        [NonSerialized] private PlayerHealth _hitEventHealth;
        [NonSerialized] private Action<PlayerHealth, HitContext, HitOutcome> _onHitReceived; // cached: no per-cast allocation
        [NonSerialized] private VfxHandle _trail;
        [NonSerialized] private float _nextAfterimageTime;
        [NonSerialized] private int _evadedThisDodge;

        /// <summary>Public parameterless constructor (required by [SerializeReference] and the roster factory).</summary>
        public SpecterPrecognitionDodge() { }

        /// <summary>Invulnerable window length: AbilityData.duration when set, else <see cref="dodgeDuration"/>.</summary>
        public override float Duration => Data != null && Data.duration > 0f ? Data.duration : dodgeDuration;

        /// <summary><see cref="IIncomingHitFilter.Priority"/>.</summary>
        public int Priority => filterPriority;

        /// <summary>Balls evaded during the current / last dodge (HUD, stats).</summary>
        public int EvadedThisDodge => _evadedThisDodge;

        // ------------------------------------------------------------------ activation

        protected override void OnInitialize()
        {
            _onHitReceived = OnHitReceived;
        }

        protected override bool CanActivateCustom(out AbilityFailReason reason)
        {
            reason = AbilityFailReason.None;
            var status = Owner.Status;
            // Spec: dodges are disabled by Absolute Zero (DodgeDisabled); a rooted body cannot slide either.
            if (status != null && (status.Has(StatusEffectType.DodgeDisabled) || status.Has(StatusEffectType.Rooted)))
            {
                reason = AbilityFailReason.CannotAct;
                return false;
            }
            if (Owner.Motor == null)
            {
                reason = AbilityFailReason.CannotAct;
                return false;
            }
            return true;
        }

        protected override void OnCast()
        {
            _evadedThisDodge = 0;
            float window = Duration;

            // 1) Invulnerability: status for UI/AI/other systems + our own filter so evasion never depends on who reads it.
            Owner.Status?.Apply(StatusEffectType.Invulnerable, window, 1f, this);
            RegisterFilter();

            if (cancelCatchStance && Owner.Combat != null && Owner.Combat.IsCatchArmed) Owner.Combat.CancelCatch();

            // 2) Sliding dodge along the input, or sideways away from the most dangerous ball.
            Vector3 direction = ResolveDodgeDirection();
            StartDodgeMotion(direction, window);

            // 3) Presentation.
            Color tint = afterimageTint;
            _trail = VfxManager.SpawnAttached(VfxId.DodgeAfterimage, Owner.transform, Vector3.up * 0.9f, 1f, tint, window);
            VfxManager.Spawn(VfxId.SlideDust, Owner.Position, Quaternion.LookRotation(direction, Vector3.up));
            AudioManager.PlayAt(SfxId.Slide, Owner.Position, sfxVolume, 1.08f);
            if (Owner.IsLocalPlayer && localScreenPulse > 0f) ScreenFx.Pulse(ScreenPulse.TimeRewind, localScreenPulse, 0.3f);

            _nextAfterimageTime = Now; // first afterimage right away
        }

        /// <summary>
        /// Grounded: an ability slide for the whole window (ignores the normal slide cooldown, keeps any higher momentum),
        /// then the state machine follows into Sliding. Airborne: a planar air-dodge impulse (there is no floor to slide on).
        /// </summary>
        private void StartDodgeMotion(Vector3 direction, float window)
        {
            var motor = Owner.Motor;
            if (motor == null) return;

            if (motor.IsGrounded && motor.TryStartSlide(direction, dodgeSpeed, window, true))
            {
                // Leaving ChargingThrow / Catching for Sliding: those states would otherwise switch the motor back to their
                // own movement mode next frame and end the slide. (Grounded / Sprinting follow a motor slide on their own.)
                var fsm = Owner.StateMachine;
                if (fsm != null && !fsm.IsIn(PlayerStateId.Sliding) && fsm.CanAct) fsm.ChangeState(PlayerStateId.Sliding);
                return;
            }

            // Airborne (or the motor refused the slide): keep the invulnerability and shove the body sideways instead,
            // never slower than the momentum it already carries along that direction.
            float speed = motor.IsGrounded ? dodgeSpeed : airDodgeSpeed;
            float carried = Mathf.Max(0f, Vector3.Dot(motor.PlanarVelocity, direction));
            if (speed > 0f) motor.SetPlanarVelocity(direction * Mathf.Max(speed, carried));
        }

        protected override void OnTick(float deltaTime)
        {
            if (afterimageInterval <= 0f || Owner.Visual == null) return;
            if (Now < _nextAfterimageTime) return;

            _nextAfterimageTime = Now + afterimageInterval;
            Owner.Visual.SpawnAfterimage(afterimageLifetime, afterimageTint);
        }

        protected override void OnInterrupt(InterruptReason reason)
        {
            // Stop the slide on hard interrupts so an eliminated / round-reset body does not keep gliding.
            if ((reason == InterruptReason.Eliminated || reason == InterruptReason.RoundEnded) && Owner.Motor != null && Owner.Motor.IsSliding)
                Owner.Motor.EndSlide();
        }

        protected override void OnEnd(bool interrupted)
        {
            UnregisterFilter();
            Owner.Status?.Remove(StatusEffectType.Invulnerable, this);
            VfxManager.StopEffect(_trail);
            _trail = default;
        }

        protected override void OnUnequip()
        {
            UnregisterFilter();
            VfxManager.StopEffect(_trail);
            _trail = default;
        }

        // ------------------------------------------------------------------ IIncomingHitFilter

        /// <summary>
        /// Auto-evades every incoming ball while the dodge is active (unblockable balls excepted). Normally the Invulnerable
        /// status already negated the hit before filters run; this keeps the evasion intact when no status controller exists.
        /// </summary>
        public void FilterHit(ref HitContext hit)
        {
            if (!IsActive || hit.Cancelled) return;
            if (hit.Unblockable && unblockablePierces) return;

            hit.Cancelled = true;
            hit.CancelReason = "Precognition Dodge";
        }

        /// <summary>Every negated hit during the dodge is an evaded ball: count it and show where it would have struck.</summary>
        private void OnHitReceived(PlayerHealth health, HitContext hit, HitOutcome outcome)
        {
            if (!IsActive || outcome != HitOutcome.Negated || Owner == null) return;
            _evadedThisDodge++;

            if (evadeBurstScale > 0f)
            {
                Vector3 dir = hit.BallVelocity.sqrMagnitude > 1e-4f ? hit.BallVelocity.normalized : Owner.Forward;
                Vector3 point = hit.Point != Vector3.zero ? hit.Point : Owner.ChestPosition;
                VfxManager.Spawn(VfxId.DodgeAfterimage, point, Quaternion.LookRotation(dir, Vector3.up), evadeBurstScale, afterimageTint);
            }
        }

        // ------------------------------------------------------------------ AI

        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || ctx.IncomingBall == null) return 0f;
            if (ctx.IncomingTimeToImpact > aiReactionWindow || ctx.IncomingTimeToImpact < 0.02f) return 0f;

            var status = Owner != null ? Owner.Status : null;
            if (status != null && (status.Has(StatusEffectType.DodgeDisabled) || status.Has(StatusEffectType.Invulnerable))) return 0f;

            float speed = ctx.IncomingBall.SpeedKmh;
            float speedFactor = Mathf.Clamp01((speed - 60f) / 100f);
            float utility = Data.aiWeight * (0.55f + 0.45f * speedFactor);

            // Unblockable balls cannot be evaded by the filter; the slide itself may still get us out of the way.
            if (ctx.IncomingBall.Unblockable) utility *= 0.5f;

            // Slow, catchable ball and free hands: a catch (possibly perfect) is the better answer.
            if (!ctx.HoldingBall && speed < aiPreferCatchBelowKmh) utility *= 0.5f;
            return Mathf.Clamp01(utility);
        }

        // ------------------------------------------------------------------ helpers

        private Vector3 ResolveDodgeDirection()
        {
            Vector3 move = Owner.Intent.Move;
            move.y = 0f;
            if (move.magnitude >= inputThreshold) return move.normalized;

            var threat = SpecterThreatUtil.FindMostThreateningBall(Owner, threatHorizon, out _, out _);
            Vector3 dir = SpecterThreatUtil.ComputeEvadeDirection(Owner, threat, roomProbeDistance);
            dir.y = 0f;
            return dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.Cross(Vector3.up, Owner.Forward).normalized;
        }

        /// <summary>Registers the evasion filter and the evasion-feedback listener on Specter's health (idempotent).</summary>
        private void RegisterFilter()
        {
            var health = Owner.Health;
            if (health == null) return;

            if (!_filterRegistered)
            {
                _filterHealth = health;
                _filterHealth.AddHitFilter(this);
                _filterRegistered = true;
            }

            if (_hitEventHealth == null && _onHitReceived != null)
            {
                _hitEventHealth = health;
                _hitEventHealth.HitReceived += _onHitReceived;
            }
        }

        private void UnregisterFilter()
        {
            if (_filterRegistered)
            {
                if (_filterHealth != null) _filterHealth.RemoveHitFilter(this);
                _filterHealth = null;
                _filterRegistered = false;
            }

            if (_hitEventHealth != null)
            {
                _hitEventHealth.HitReceived -= _onHitReceived;
                _hitEventHealth = null;
            }
        }
    }
}
