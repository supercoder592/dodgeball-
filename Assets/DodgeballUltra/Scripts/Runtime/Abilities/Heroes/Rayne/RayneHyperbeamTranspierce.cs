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
    /// Rayne's ultimate <b>[Hyperbeam Transpierce]</b>: "unblockable beam-ball that penetrates all enemies in its path".
    /// <para>
    /// Conjures a Beam-style ability ball through <see cref="AbilityUtil.ThrowAbilityBall"/>: Unblockable (ignores shields,
    /// evasion and catches), Pierce (DodgeBall keeps flying after each enemy it hits), x2.2 speed (still capped at
    /// 220 km/h), zero gravity (laser-straight) and x1.3 radius. A <see cref="BeamPayload"/> adds the BeamTrail look and heavy
    /// juice on every pierce. The ability stays Active until the beam grounds out (wall / floor) or a timeout.
    /// </para>
    /// <para>Ultimates gate on the meter; cooldown / cast time come from <see cref="AbilityData"/>.</para>
    /// </summary>
    [Serializable]
    public sealed class RayneHyperbeamTranspierce : AbilityBase
    {
        [Header("Beam-ball")]
        [Tooltip("Launch speed multiplier on top of the full-charge throw speed. Spec: x2.2 (capped at 220 km/h).")]
        [Range(1f, 4f)] public float speedMultiplier = 2.2f;

        [Tooltip("Collision radius multiplier (1.3 = 30% bigger than a match ball, easier to connect).")]
        [Range(0.5f, 3f)] public float radiusMultiplier = 1.3f;

        [Tooltip("Gravity multiplier. Spec: 0 - the beam flies perfectly straight.")]
        [Range(0f, 1f)] public float gravityScale = 0f;

        [Tooltip("Seconds the ability tracks the beam before it stops waiting and ends.")]
        [Min(0.5f)] public float resolveTimeout = 2.5f;

        [Header("Impact")]
        [Tooltip("Minimum knockback (m/s velocity change) for enemies that survive the beam (e.g. Gouki).")]
        [Min(0f)] public float knockbackSpeed = 5f;

        [Tooltip("Extra hitstop (s) per enemy pierced on top of the standard hit pipeline (JuiceManager clamps to 0.03-0.1 s).")]
        [Range(0f, 0.1f)] public float heavyHitstop = 0.1f;

        [Tooltip("Recoil (m/s) pushing Rayne backwards when the beam is released.")]
        [Range(0f, 5f)] public float recoilSpeed = 1.2f;

        [Header("Feedback")]
        [Tooltip("Plasma colour of the beam: white-hot core with a cool edge.")]
        public Color beamTint = new Color(0.78f, 0.9f, 1f, 1f);

        [Tooltip("Scale of the attached BeamTrail effect.")]
        [Range(0.2f, 3f)] public float trailScale = 1.3f;

        [Tooltip("Perlin shake amplitude on every pierce.")]
        [Range(0f, 2f)] public float shakeAmplitude = 0.9f;

        [Tooltip("Perlin shake frequency (Hz).")]
        [Range(1f, 60f)] public float shakeFrequency = 26f;

        [Tooltip("Perlin shake duration (s).")]
        [Range(0.05f, 1.5f)] public float shakeDuration = 0.5f;

        [Header("AI")]
        [Tooltip("Half-width (m) of the corridor used to count enemies lined up behind the target.")]
        [Min(0.2f)] public float aiCorridorHalfWidth = 0.9f;

        [Tooltip("Bots only fire the beam when the nearest enemy is closer than this (m).")]
        [Min(2f)] public float aiMaxRange = 24f;

        [NonSerialized] private DodgeBall _ball;
        [NonSerialized] private BeamPayload _payload;
        [NonSerialized] private float _trackedFor;
        [NonSerialized] private VfxHandle _charge;

        /// <summary>The beam currently in flight (null when none).</summary>
        public DodgeBall ActiveBeam => _ball;

        public RayneHyperbeamTranspierce() { }

        protected override bool CanActivateCustom(out AbilityFailReason reason)
        {
            if (BallManager.Instance == null || Owner.Combat == null)
            {
                reason = AbilityFailReason.Custom;
                return false;
            }
            reason = AbilityFailReason.None;
            return true;
        }

        /// <summary>Charge-up (castTime &gt; 0): plasma gathers in the hand, the screen tightens for everyone watching Rayne.</summary>
        protected override void OnCastStarted()
        {
            Transform hand = Owner.Visual != null && Owner.Visual.RightHandSocket != null ? Owner.Visual.RightHandSocket : Owner.transform;
            Vector3 offset = hand == Owner.transform ? Vector3.up * 1.3f : Vector3.zero;
            _charge = VfxManager.SpawnAttached(VfxId.BeamTrail, hand, offset, 0.5f, beamTint, CastTime + 0.15f);
            AudioManager.PlayAt(SfxId.UltimateCast, Owner.ChestPosition, 1f, 1f);
            if (Owner.IsLocalPlayer) ScreenFx.Pulse(ScreenPulse.UltimateCast, 0.8f, Mathf.Max(0.25f, CastTime));
        }

        protected override void OnCast()
        {
            StopCharge();

            _payload = new BeamPayload(Owner, new BeamSettings
            {
                KnockbackSpeed = knockbackSpeed,
                HitstopDuration = heavyHitstop,
                ShakeAmplitude = shakeAmplitude,
                ShakeFrequency = shakeFrequency,
                ShakeDuration = shakeDuration,
                Tint = beamTint,
                TrailScale = trailScale,
            });

            var options = AbilityThrowOptions.Default(BallStyle.Beam);
            options.SpeedMultiplier = speedMultiplier;
            options.RadiusMultiplier = radiusMultiplier;
            options.GravityScale = gravityScale;
            options.Unblockable = true;
            options.Pierce = true;
            options.Payload = _payload;

            _ball = AbilityUtil.ThrowAbilityBall(Owner, options);
            if (_ball == null)
            {
                _payload = null;
                EndAbility();
                return;
            }

            _trackedFor = 0f;
            HoldActive();

            // Release: muzzle flash at the hand, recoil through Rayne's body, heavy rumble.
            Vector3 dir = _ball.Velocity.sqrMagnitude > 1e-4f ? _ball.Velocity.normalized : Owner.Forward;
            Vector3 origin = _ball.transform.position;
            VfxManager.Spawn(VfxId.BeamImpact, origin, Quaternion.LookRotation(dir), 0.7f, beamTint);
            AudioManager.PlayAt(SfxId.ThrowHeavy, origin, 1f, 0.8f);
            if (Owner.Motor != null && recoilSpeed > 0f)
            {
                Vector3 back = -new Vector3(dir.x, 0f, dir.z);
                if (back.sqrMagnitude > 1e-4f) Owner.Motor.AddImpulse(back.normalized * recoilSpeed);
            }
            var juice = JuiceManager.Instance;
            if (juice != null) juice.Shake(shakeAmplitude * 0.5f, shakeFrequency, 0.25f, origin);
        }

        protected override void OnTick(float deltaTime)
        {
            _trackedFor += deltaTime;

            // The beam grounds out on the first wall / floor it strikes: it never ricochets back through players.
            if (_payload != null && _payload.HasHitSurface && !_payload.IsResolved &&
                _ball != null && _ball.IsLive && ReferenceEquals(_ball.Payload, _payload))
            {
                _ball.MakeFree(_ball.Velocity * 0.1f, true);
            }

            if (_payload == null || _payload.IsResolved || _trackedFor >= resolveTimeout) EndAbility();
        }

        protected override void OnInterrupt(InterruptReason reason) => StopCharge();

        protected override void OnEnd(bool interrupted)
        {
            StopCharge();
            _ball = null;
            _payload = null;
            _trackedFor = 0f;
        }

        protected override void OnUnequip() => StopCharge();

        /// <summary>
        /// The beam cannot be caught or blocked, so any enemy in range is a near-certain hit; it becomes premium value when
        /// several enemies line up behind the target (counted inside a corridor along the throw line).
        /// </summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || ctx.Self == null || ctx.NearestEnemy == null) return 0f;
            if (ctx.NearestEnemyDistance > aiMaxRange) return 0f;
            if (ctx.IncomingBall != null && ctx.IncomingTimeToImpact < 0.35f) return 0f;

            int aligned = CountEnemiesInCorridor(ctx.Self.ChestPosition, ctx.NearestEnemy.ChestPosition);
            float utility = Data.aiWeight * (0.6f + 0.25f * Mathf.Max(0, aligned - 1));
            if (ctx.AlliesInfield < ctx.EnemiesInfield) utility += 0.15f; // comeback tool
            if (ctx.RoundTimeRemaining > 0f && ctx.RoundTimeRemaining < 20f) utility += 0.1f; // do not die with a full meter
            return Mathf.Clamp01(utility);
        }

        /// <summary>Targetable enemies whose position lies inside a corridor from <paramref name="from"/> through <paramref name="through"/>.</summary>
        private int CountEnemiesInCorridor(Vector3 from, Vector3 through)
        {
            Vector3 axis = through - from;
            axis.y = 0f;
            float length = axis.magnitude;
            if (length < 0.01f) return 1;
            axis /= length;

            int count = 0;
            float halfWidth2 = aiCorridorHalfWidth * aiCorridorHalfWidth;
            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || !IsEnemy(p) || !p.IsTargetable) continue;
                Vector3 rel = p.Position - from;
                rel.y = 0f;
                float along = Vector3.Dot(rel, axis);
                if (along < 0f) continue; // behind Rayne
                Vector3 lateral = rel - axis * along;
                if (lateral.sqrMagnitude <= halfWidth2) count++;
            }
            return count;
        }

        private void StopCharge()
        {
            if (!_charge.IsValid) return;
            VfxManager.StopEffect(_charge);
            _charge = default;
        }
    }
}
