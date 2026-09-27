using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Chrono - SKILL [Stasis Field] (靜止力場): freezes a flying enemy ball mid-air for 2 s so allies can snatch it
    /// (cooldown 12 s).
    /// <para>
    /// Target selection (<see cref="FindTarget"/>): among Live balls thrown by the enemy team within
    /// <see cref="maxRange"/> of Chrono, the ball closest to the aim ray wins, with a strong preference for balls that
    /// are heading toward Chrono's team (<see cref="ChronoTimeUtil.IsThreateningTeam"/>) and a mild preference for closer
    /// balls. Threatening balls are always eligible (auto-save); harmless balls must be inside <see cref="maxAimAngle"/>.
    /// Without a valid target activation fails with <see cref="AbilityFailReason.NoTarget"/> (no cooldown is spent).
    /// </para>
    /// <para>
    /// The chosen ball enters <see cref="BallState.Stasis"/> through <see cref="DodgeBall.EnterStasis"/> (any player may
    /// then grab it); a StasisBubble effect is attached to the ball and stopped as soon as it leaves stasis, and the
    /// stasis SFX plays at the ball.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class ChronoStasisField : AbilityBase
    {
        [Header("Targeting")]
        [Tooltip("Maximum distance (m) from Chrono to the ball. Spec: 18 m.")]
        [Range(2f, 40f)] public float maxRange = 18f;

        [Tooltip("Largest angle (deg) from the aim ray for balls that do NOT threaten Chrono's team.")]
        [Range(5f, 180f)] public float maxAimAngle = 60f;

        [Tooltip("How many degrees of aim error a ball heading toward Chrono's team is 'worth' (higher = prefer threats).")]
        [Range(0f, 120f)] public float threatPreferenceDegrees = 30f;

        [Tooltip("Score penalty (deg) per metre of distance: prefers closer balls when their aim angles are similar.")]
        [Range(0f, 5f)] public float distancePenaltyPerMetre = 0.6f;

        [Tooltip("Seconds of gravity-aware look-ahead used to decide whether a ball is heading toward a teammate.")]
        [Range(0.2f, 3f)] public float threatLookAhead = 1.5f;

        [Tooltip("Distance (m) from a teammate's body within which a predicted flight counts as 'heading toward' them.")]
        [Range(0.3f, 4f)] public float threatRadius = 1.4f;

        [Header("Stasis")]
        [Tooltip("Seconds the ball stays frozen mid-air. Spec: 2 s.")]
        [Range(0.25f, 6f)] public float stasisDuration = 2f;

        [Header("Presentation")]
        [Tooltip("Tint of the stasis bubble (clockwork gold).")]
        public Color bubbleTint = new Color(0.95f, 0.8f, 0.45f, 1f);

        [Tooltip("Scale of the stasis bubble effect.")]
        [Range(0.2f, 3f)] public float bubbleScale = 1f;

        [Tooltip("Stasis SFX volume.")]
        [Range(0f, 1f)] public float sfxVolume = 1f;

        [Header("AI")]
        [Tooltip("Bots only freeze balls that will reach a teammate in at least this many seconds (earlier is too late).")]
        [Range(0f, 1f)] public float aiMinTimeToThreat = 0.12f;

        // ------------------------------------------------------------------ runtime state
        [NonSerialized] private DodgeBall _candidate;     // chosen in CanActivateCustom, consumed in OnCast (same frame)
        [NonSerialized] private DodgeBall _frozenBall;    // ball currently held in stasis by this ability
        [NonSerialized] private VfxHandle _bubble;
        [NonSerialized] private float _frozenAt;
        [NonSerialized] private bool _refundCooldown;       // the target vanished between validation and cast

        /// <summary>Public parameterless constructor (required by [SerializeReference] and the roster factory).</summary>
        public ChronoStasisField() { }

        /// <summary>The ball currently frozen by this ability (null when none).</summary>
        public DodgeBall FrozenBall => _frozenBall;

        // ------------------------------------------------------------------ activation

        protected override bool CanActivateCustom(out AbilityFailReason reason)
        {
            _candidate = FindTarget(out _);
            reason = _candidate != null ? AbilityFailReason.None : AbilityFailReason.NoTarget;
            return _candidate != null;
        }

        protected override void OnCast()
        {
            var ball = _candidate;
            _candidate = null;
            if (ball == null || !ball.IsLive) ball = FindTarget(out _); // re-validate (defensive)
            if (ball == null)
            {
                _refundCooldown = true; // nothing was frozen: do not charge the cooldown
                return;
            }

            ReleaseBubble();
            ball.EnterStasis(stasisDuration);
            _frozenBall = ball;
            _frozenAt = Now;

            _bubble = VfxManager.SpawnAttached(VfxId.StasisBubble, ball.transform, Vector3.zero, bubbleScale, bubbleTint, stasisDuration);
            AudioManager.PlayAt(SfxId.Stasis, ball.transform.position, sfxVolume, 1f);

            // A short time-warp burst from Chrono's hand toward the ball reads as the cast.
            Vector3 hand = Owner.Combat != null ? Owner.Combat.GetThrowOrigin() : Owner.ChestPosition;
            Vector3 toBall = ball.transform.position - hand;
            Quaternion look = toBall.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(toBall.normalized, Vector3.up) : Owner.Rotation;
            VfxManager.Spawn(VfxId.RewindTrail, hand, look, 0.5f, bubbleTint);
        }

        /// <summary>The ability is instant; the bubble is watched during the cooldown and stopped when the ball is grabbed.</summary>
        protected override void OnCooldownTick(float deltaTime) => WatchFrozenBall();

        protected override void OnReady() => WatchFrozenBall();

        protected override void OnCooldown()
        {
            if (!_refundCooldown) return;
            _refundCooldown = false;
            ResetCooldown();
        }

        protected override void OnRoundReset() => ReleaseBubble();

        protected override void OnUnequip() => ReleaseBubble();

        // ------------------------------------------------------------------ AI

        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || Owner == null || !IsReady) return 0f;

            var ball = ChronoTimeUtil.FindMostThreateningEnemyBall(Owner, Owner.ChestPosition, maxRange, threatLookAhead, threatRadius,
                aiMinTimeToThreat, out float timeToThreat);
            if (ball == null) return 0f;

            // Sooner threats and faster balls are more urgent; a freeze close to our side also hands us the ball.
            float urgency = Mathf.Clamp01(1f - timeToThreat / Mathf.Max(0.1f, threatLookAhead));
            float speed = Mathf.Clamp01(ball.SpeedKmh / 140f);
            return Mathf.Clamp01(Data.aiWeight * (0.6f + 0.25f * urgency + 0.15f * speed));
        }

        // ------------------------------------------------------------------ targeting

        /// <summary>
        /// Best enemy live ball for the stasis: lowest score = aim angle (deg) + distance penalty - threat preference.
        /// </summary>
        /// <param name="score">Winning score (lower is better; +inf when none).</param>
        public DodgeBall FindTarget(out float score)
        {
            score = float.PositiveInfinity;
            var manager = BallManager.Instance;
            if (Owner == null || manager == null) return null;

            var balls = manager.ActiveBalls;
            if (balls == null) return null;

            // Aim ray from the head (close to the camera/eye line) along the intent's aim direction.
            Vector3 origin = Owner.HeadPosition;
            Vector3 aim = Owner.Intent.AimDirection;
            if (aim.sqrMagnitude < 1e-4f) aim = Owner.Forward;
            aim.Normalize();

            Vector3 chest = Owner.ChestPosition;
            float range2 = maxRange * maxRange;
            DodgeBall best = null;

            for (int i = 0; i < balls.Count; i++)
            {
                var ball = balls[i];
                if (ball == null || !ball.IsLive || !ChronoTimeUtil.IsEnemyBall(ball, Owner)) continue;

                Vector3 position = ball.transform.position;
                if ((position - chest).sqrMagnitude > range2) continue;

                Vector3 toBall = position - origin;
                float distance = toBall.magnitude;
                float angle = distance > 1e-3f ? Vector3.Angle(aim, toBall) : 0f;

                bool threat = ChronoTimeUtil.IsThreateningTeam(ball, Owner.Team, threatLookAhead, threatRadius, out _);
                if (!threat && angle > maxAimAngle) continue;

                float s = angle + distance * distancePenaltyPerMetre - (threat ? threatPreferenceDegrees : 0f);
                if (s < score)
                {
                    score = s;
                    best = ball;
                }
            }

            return best;
        }

        // ------------------------------------------------------------------ helpers

        private void WatchFrozenBall()
        {
            if (_frozenBall == null) return;
            // Small grace so a stasis that is applied on the ball's next physics step is not mistaken for a release.
            if (Now - _frozenAt < 0.1f) return;
            if (_frozenBall.State != BallState.Stasis) ReleaseBubble();
        }

        private void ReleaseBubble()
        {
            VfxManager.StopEffect(_bubble);
            _bubble = default;
            _frozenBall = null;
        }
    }
}
