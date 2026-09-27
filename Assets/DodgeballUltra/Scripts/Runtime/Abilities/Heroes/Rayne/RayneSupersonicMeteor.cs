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
    /// SPEC FILE - Rayne's skill <b>[Supersonic Meteor]</b>: "fire-infused fastball; on impact 3 m AOE shockwave knocking back
    /// nearby enemies (CD 10 s)". This class is the reference implementation every other ability follows.
    /// <para><b>Anatomy of an ability</b></para>
    /// <list type="bullet">
    /// <item>A plain <c>[Serializable]</c> class deriving from <see cref="AbilityBase"/>, stored polymorphically in an
    /// <see cref="AbilityData"/> asset and cloned per player. Public fields are its <i>ability-specific</i> tuning values.</item>
    /// <item>Cooldown (10 s), cast time and duration are NOT fields here: they live on <see cref="AbilityData"/> (the roster
    /// factory writes the spec values) so designers balance every ability from one place.</item>
    /// <item>Runtime state is <c>[NonSerialized]</c> and only ever assigned after <see cref="AbilityBase.Initialize"/>, so the
    /// shallow <see cref="AbilityBase.CloneTemplate"/> never shares mutable state between players.</item>
    /// </list>
    /// <para><b>Flow</b></para>
    /// <code>
    /// TryActivate ─► (castTime) OnCastStarted: fire gathers in Rayne's throwing hand
    ///            ─► OnCast: conjure Meteor ball (Style Meteor, x1.6 speed, 0.3 gravity) through AbilityUtil.ThrowAbilityBall
    ///                       at the soft-lock target, carrying a MeteorPayload; HoldActive()
    ///            ─► OnTick: wait until the payload resolves (hit / bounce / catch) or <see cref="resolveTimeout"/> passes;
    ///                       a meteor that burst against a wall is spent and dropped instead of ricocheting on
    ///            ─► EndAbility() ─► cooldown (AbilityData.cooldown = 10 s)
    /// MeteorPayload: player hit or court impact ─► 3 m shockwave: radial knockback on ENEMIES (falls off with distance,
    ///                small upward lift, 0.35 s stun at the core) + Shockwave VFX/SFX + Perlin camera shake.
    ///                The direct hit is still resolved normally by Combat (damage, catch rules, juice pipeline).
    ///                A clean catch smothers the fire (no shockwave) - the enemy's counter-play.
    /// </code>
    /// <para>
    /// Rules of thumb shown here: effects go through the null-safe facades (VfxManager.Spawn / AudioManager.PlayAt /
    /// JuiceManager null-check / ScreenFx), gameplay uses scaled time, queries are allocation-free (PlayerRegistry
    /// helpers with cached lists), and nothing moves a player outside the court rules.
    /// </para>
    /// <para>
    /// Because the meteor is launched through the regular throw pipeline it inherits everything a real throw has:
    /// throw modifiers (e.g. a perfect-catch counter boost), lead targeting, the 220 km/h speed cap and a
    /// <c>BallThrownEvent</c> for the HUD, AI dodging and Danger Sense.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class RayneSupersonicMeteor : AbilityBase
    {
        // ------------------------------------------------------------------ tuning: the fastball

        [Header("Fastball")]
        [Tooltip("Launch speed multiplier on top of Rayne's full-charge throw speed. Spec: fastball (x1.6). " +
                 "The result is still capped at 220 km/h by the throw pipeline.")]
        [Range(1f, 3f)] public float speedMultiplier = 1.6f;

        [Tooltip("Gravity multiplier applied to the meteor (0 = laser-flat, 1 = real gravity). A low value keeps the fastball flat.")]
        [Range(0f, 1f)] public float gravityScale = 0.3f;

        [Tooltip("Collision radius multiplier of the conjured ball (1 = regulation 0.105 m foam ball).")]
        [Range(0.5f, 2f)] public float radiusMultiplier = 1f;

        [Tooltip("Seconds the ability waits for the meteor to resolve (hit / bounce / catch) before it stops tracking it " +
                 "and starts the cooldown. The meteor keeps its payload afterwards.")]
        [Min(0.5f)] public float resolveTimeout = 3f;

        // ------------------------------------------------------------------ tuning: the shockwave

        [Header("Shockwave (AOE)")]
        [Tooltip("AOE radius (m) of the shockwave on the floor plane. Spec: 3 m.")]
        [Min(0.5f)] public float shockwaveRadius = 3f;

        [Tooltip("Horizontal velocity change (m/s) given to an enemy at the epicentre. ~6.5 m/s is a hard shove that " +
                 "throws a 75 kg athlete off balance without launching them across the court.")]
        [Min(0f)] public float knockbackSpeed = 6.5f;

        [Tooltip("Fraction of the knockback still applied at the rim of the AOE (linear falloff in between).")]
        [Range(0f, 1f)] public float edgeKnockbackFraction = 0.25f;

        [Tooltip("Upward velocity change (m/s) at the epicentre (same falloff) - lifts the victim so momentum carries them.")]
        [Min(0f)] public float upwardSpeed = 1.8f;

        [Tooltip("Enemies within this distance (m) of the epicentre are briefly stunned.")]
        [Min(0f)] public float coreStunRadius = 1.25f;

        [Tooltip("Stun length (s) at the core of the blast. Spec: brief (0.35 s).")]
        [Range(0f, 2f)] public float coreStunDuration = 0.35f;

        [Tooltip("Velocity change (m/s) given to loose balls lying inside the blast (0 = balls are not pushed).")]
        [Min(0f)] public float freeBallPushSpeed = 3.5f;

        [Tooltip("Also detonate when the meteor is stopped by an obstacle that is neither a player nor the court " +
                 "(e.g. it pops one of Shadow's clones).")]
        public bool detonateOnObstacles = true;

        [Tooltip("Fraction of its speed a spent meteor keeps when it drops after bursting against a wall.")]
        [Range(0f, 1f)] public float spentBallSpeedFraction = 0.25f;

        // ------------------------------------------------------------------ tuning: feedback

        [Header("Feedback")]
        [Tooltip("Colour of the fire (hand wind-up, trail, shockwave ring). Realistic flame orange.")]
        public Color fireTint = new Color(1f, 0.45f, 0.12f, 1f);

        [Tooltip("Scale of the fire trail attached to the ball.")]
        [Range(0.2f, 3f)] public float trailScale = 1f;

        [Tooltip("Perlin camera shake amplitude at the detonation (falls off with distance).")]
        [Range(0f, 2f)] public float shakeAmplitude = 0.6f;

        [Tooltip("Perlin camera shake frequency (Hz).")]
        [Range(1f, 60f)] public float shakeFrequency = 20f;

        [Tooltip("Perlin camera shake duration (s).")]
        [Range(0.05f, 1.5f)] public float shakeDuration = 0.4f;

        // ------------------------------------------------------------------ tuning: AI

        [Header("AI")]
        [Tooltip("Bots only consider the meteor when the nearest enemy is closer than this (m).")]
        [Min(2f)] public float aiMaxRange = 18f;

        // ------------------------------------------------------------------ runtime state (per player, never serialized)

        [NonSerialized] private DodgeBall _ball;
        [NonSerialized] private MeteorPayload _payload;
        [NonSerialized] private float _trackedFor;
        [NonSerialized] private VfxHandle _windup;

        /// <summary>The meteor currently tracked by the ability (null when none).</summary>
        public DodgeBall ActiveMeteor => _ball;

        /// <summary>Payload of the meteor currently tracked (null when none).</summary>
        public MeteorPayload ActivePayload => _payload;

        /// <summary>Parameterless constructor required by <c>[SerializeReference]</c> and the roster factory.</summary>
        public RayneSupersonicMeteor() { }

        // ------------------------------------------------------------------ activation rules

        protected override bool CanActivateCustom(out AbilityFailReason reason)
        {
            // The meteor is a conjured projectile: without a BallManager there is nothing to throw.
            if (BallManager.Instance == null || Owner.Combat == null)
            {
                reason = AbilityFailReason.Custom;
                return false;
            }

            reason = AbilityFailReason.None;
            return true;
        }

        // ------------------------------------------------------------------ lifecycle hooks

        /// <summary>Wind-up (only when AbilityData.castTime &gt; 0): flames gather in the throwing hand.</summary>
        protected override void OnCastStarted()
        {
            Transform hand = Owner.Visual != null && Owner.Visual.RightHandSocket != null ? Owner.Visual.RightHandSocket : Owner.transform;
            Vector3 offset = hand == Owner.transform ? Vector3.up * 1.3f : Vector3.zero;
            _windup = VfxManager.SpawnAttached(VfxId.FireTrail, hand, offset, 0.5f, fireTint, CastTime + 0.15f);
            AudioManager.PlayAt(SfxId.AbilityCast, Owner.ChestPosition, 0.9f, 1.05f);
        }

        /// <summary>SPEC HOOK: conjure and launch the meteor.</summary>
        protected override void OnCast()
        {
            StopWindup();

            _payload = new MeteorPayload(Owner, BuildShockwaveSettings());

            var options = AbilityThrowOptions.Default(BallStyle.Meteor);
            options.SpeedMultiplier = speedMultiplier;
            options.GravityScale = gravityScale;
            options.RadiusMultiplier = radiusMultiplier;
            options.Payload = _payload;
            // options.Target stays null: the throw pipeline uses the thrower's soft-lock target (or the raw aim).

            _ball = AbilityUtil.ThrowAbilityBall(Owner, options);
            if (_ball == null)
            {
                // No BallManager / pool exhausted: fail gracefully, the cooldown still applies (the input was consumed).
                _payload = null;
                EndAbility();
                return;
            }

            // Stay Active (ignoring AbilityData.duration) until the meteor resolves - OnTick polls the payload.
            _trackedFor = 0f;
            HoldActive();

            // Local feedback for the thrower: heavy release sound + a small recoil shake.
            Vector3 origin = _ball.transform.position;
            AudioManager.PlayAt(SfxId.ThrowHeavy, origin, 1f, 0.95f);
            var juice = JuiceManager.Instance;
            if (juice != null) juice.Shake(shakeAmplitude * 0.3f, shakeFrequency, 0.15f, origin);
        }

        /// <summary>SPEC HOOK: while Active, wait for the meteor to resolve (or time out).</summary>
        protected override void OnTick(float deltaTime)
        {
            _trackedFor += deltaTime;

            // A meteor that burst against a wall is spent: it drops instead of ricocheting on as a live ball.
            // Done here (Update) rather than inside the payload callbacks, which run in the middle of the ball's sweep.
            if (_payload != null && _payload.HasDetonated && !_payload.IsResolved && IsStillOurFlight())
            {
                _ball.MakeFree(_ball.Velocity * spentBallSpeedFraction, true);
            }

            if (_payload == null || _payload.IsResolved || _trackedFor >= resolveTimeout) EndAbility();
        }

        /// <summary>
        /// SPEC HOOK: stunned / frozen / eliminated / round end. A meteor that already left the hand keeps flying (it is a
        /// physical projectile now); only the wind-up is cancelled and the ability stops tracking.
        /// </summary>
        protected override void OnInterrupt(InterruptReason reason)
        {
            StopWindup();
        }

        protected override void OnEnd(bool interrupted)
        {
            StopWindup();
            _ball = null;
            _payload = null;
            _trackedFor = 0f;
        }

        protected override void OnUnequip() => StopWindup();

        // ------------------------------------------------------------------ AI

        /// <summary>
        /// Worth it when an enemy is in range and not while a ball is about to hit Rayne (dodge / catch first). The AOE makes
        /// the meteor much more valuable against clustered enemies, so every extra enemy within the shockwave radius of the
        /// likely target adds utility. Slightly less attractive at long range where the target has time to react.
        /// </summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || ctx.Self == null || ctx.NearestEnemy == null) return 0f;
            if (ctx.NearestEnemyDistance > aiMaxRange) return 0f;
            if (ctx.IncomingBall != null && ctx.IncomingTimeToImpact < 0.45f) return 0f;

            int clustered = CountEnemiesAround(ctx.NearestEnemy, shockwaveRadius);
            float rangeFactor = 1f - 0.5f * Mathf.Clamp01((ctx.NearestEnemyDistance - 6f) / Mathf.Max(1f, aiMaxRange - 6f));
            float utility = Data.aiWeight * (0.55f + 0.25f * clustered) * rangeFactor;

            // Behind on players: be more aggressive with the cooldown.
            if (ctx.AlliesInfield < ctx.EnemiesInfield) utility += 0.1f;
            return Mathf.Clamp01(utility);
        }

        // ------------------------------------------------------------------ helpers

        private MeteorShockwaveSettings BuildShockwaveSettings() => new MeteorShockwaveSettings
        {
            Radius = shockwaveRadius,
            KnockbackSpeed = knockbackSpeed,
            EdgeKnockbackFraction = edgeKnockbackFraction,
            UpwardSpeed = upwardSpeed,
            CoreStunRadius = coreStunRadius,
            CoreStunDuration = coreStunDuration,
            FreeBallPushSpeed = freeBallPushSpeed,
            DetonateOnObstacles = detonateOnObstacles,
            FireTint = fireTint,
            TrailScale = trailScale,
            ShakeAmplitude = shakeAmplitude,
            ShakeFrequency = shakeFrequency,
            ShakeDuration = shakeDuration,
        };

        /// <summary>Other targetable enemies standing within <paramref name="radius"/> of <paramref name="target"/> (allocation-free).</summary>
        private int CountEnemiesAround(DodgeballPlayer target, float radius)
        {
            int count = 0;
            float r2 = radius * radius;
            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || p == target || !IsEnemy(p) || !p.IsTargetable) continue;
                Vector3 d = p.Position - target.Position;
                d.y = 0f;
                if (d.sqrMagnitude <= r2) count++;
            }
            return count;
        }

        /// <summary>True while the tracked ball is still live and still carrying this cast's payload (pooled balls get reused).</summary>
        private bool IsStillOurFlight() => _ball != null && _ball.IsLive && ReferenceEquals(_ball.Payload, _payload);

        private void StopWindup()
        {
            if (!_windup.IsValid) return;
            VfxManager.StopEffect(_windup);
            _windup = default;
        }
    }
}
