using System;
using System.Collections.Generic;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Juice;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Gouki - ULTIMATE [Earthquake Slam] (地震重擊): slams the ground, forcing every grounded enemy to jump and drop the
    /// ball they hold.
    /// <para>
    /// The slam sends a seismic wave across the court (<see cref="waveSpeed"/>, 0 = instantaneous). When the wave front
    /// reaches a grounded infield enemy, the floor kicks them into the air (vertical velocity change ~5 m/s plus a small
    /// random horizontal stumble, via <see cref="PlayerMotor.AddImpulse"/>), their throw charge / catch stance is broken
    /// and a held ball is shaken loose (<see cref="PlayerCombatController.DropBall"/> with a small outward velocity).
    /// Enemies already airborne when the wave passes - i.e. who read it and jumped - are unaffected, as are invulnerable
    /// (evading) enemies. Loose balls on the floor hop as the wave passes.
    /// </para>
    /// <para>Presentation: EarthquakeRing VFX at the impact, dust under every victim, heavy Perlin camera shake from the
    /// epicentre, a hit screen pulse and the Earthquake SFX.</para>
    /// </summary>
    [Serializable]
    public sealed class GoukiEarthquakeSlam : AbilityBase
    {
        [Header("Seismic wave")]
        [Tooltip("Speed (m/s) of the wave front across the court. 0 = everyone is hit on the same frame.")]
        [Range(0f, 120f)] public float waveSpeed = 28f;

        [Tooltip("Maximum radius (m) of the wave. The whole court is ~20 m across diagonally.")]
        [Range(5f, 60f)] public float maxRadius = 32f;

        [Header("Forced jump")]
        [Tooltip("Vertical velocity (m/s) the floor kicks grounded enemies up with. Spec: ~5 m/s.")]
        [Range(1f, 12f)] public float jumpVelocity = 5f;

        [Tooltip("Random horizontal stumble (m/s) added to the forced jump.")]
        [Range(0f, 4f)] public float horizontalJitter = 0.8f;

        [Tooltip("Also affect enemies who are invulnerable (evading) when the wave arrives.")]
        public bool affectsInvulnerable;

        [Header("Fumble")]
        [Tooltip("Outward speed (m/s, away from Gouki) of a shaken-loose ball.")]
        [Range(0f, 8f)] public float dropOutwardSpeed = 1.8f;

        [Tooltip("Upward speed (m/s) of a shaken-loose ball.")]
        [Range(0f, 8f)] public float dropUpSpeed = 2.4f;

        [Header("Loose balls")]
        [Tooltip("Upward speed (m/s) given to loose balls on the floor as the wave passes (0 = off).")]
        [Range(0f, 6f)] public float freeBallHop = 2.2f;

        [Header("Presentation")]
        [Tooltip("Scale of the EarthquakeRing VFX at the epicentre.")]
        [Range(0.5f, 6f)] public float ringScale = 3f;

        [Tooltip("Camera shake amplitude (0..1, trauma-equivalent).")]
        [Range(0f, 1f)] public float shakeAmplitude = 0.9f;

        [Tooltip("Camera shake frequency (Hz): low = heavy rumble.")]
        [Range(1f, 40f)] public float shakeFrequency = 11f;

        [Tooltip("Camera shake duration (s).")]
        [Range(0.1f, 3f)] public float shakeDuration = 0.9f;

        [Tooltip("Screen pulse intensity on impact (0..1).")]
        [Range(0f, 1f)] public float screenPulse = 0.6f;

        [Header("AI")]
        [Tooltip("Minimum affected enemies (grounded + holding a ball counts double) before a bot slams.")]
        [Range(1, 6)] public int aiMinimumScore = 2;

        [NonSerialized] private Vector3 _epicentre;
        [NonSerialized] private float _elapsed;
        [NonSerialized] private float _endRadius;
        [NonSerialized] private bool _waveRunning;
        [NonSerialized] private List<DodgeballPlayer> _processedPlayers;
        [NonSerialized] private List<DodgeBall> _processedBalls;

        /// <summary>Required public parameterless constructor (SerializeReference / roster factory).</summary>
        public GoukiEarthquakeSlam() { }

        /// <summary>Enemies thrown into the air by the last slam.</summary>
        public int LastAffectedCount { get; private set; }

        protected override void OnInitialize()
        {
            _processedPlayers = new List<DodgeballPlayer>(6);
            _processedBalls = new List<DodgeBall>(8);
        }

        protected override void OnCastStarted()
        {
            // Wind-up: a low rumble while he raises his fists (only when the data has a cast time).
            AudioManager.PlayAt(SfxId.Earthquake, Owner.Position, 0.35f, 0.7f);
            if (JuiceManager.Instance != null) JuiceManager.Instance.AddTrauma(0.12f, Owner.Position);
        }

        protected override void OnCast()
        {
            _epicentre = AbilityUtil.GroundPoint(Owner.Position);
            _elapsed = 0f;
            _processedPlayers.Clear();
            _processedBalls.Clear();
            LastAffectedCount = 0;
            _endRadius = ComputeEndRadius();
            _waveRunning = true;
            HoldActive();

            VfxManager.Spawn(VfxId.EarthquakeRing, _epicentre, Quaternion.identity, ringScale);
            VfxManager.Spawn(VfxId.FloorImpactDust, _epicentre, Quaternion.identity, 2f);
            AudioManager.PlayAt(SfxId.Earthquake, _epicentre, 1f, 0.95f);
            var juice = JuiceManager.Instance;
            if (juice != null) juice.Shake(shakeAmplitude, shakeFrequency, shakeDuration, _epicentre);
            if (screenPulse > 0f) ScreenFx.Pulse(ScreenPulse.HeavyHit, screenPulse, 0.35f);

            if (waveSpeed <= 0f)
            {
                // Instantaneous quake: the whole court at once.
                PropagateWave(maxRadius);
                FinishWave();
            }
            else
            {
                // Radius 0 frame: anyone standing right on top of the impact.
                PropagateWave(0f);
            }
        }

        protected override void OnTick(float deltaTime)
        {
            if (!_waveRunning)
            {
                EndAbility();
                return;
            }
            _elapsed += deltaTime;
            float radius = waveSpeed > 0f ? waveSpeed * _elapsed : maxRadius;
            PropagateWave(radius);
            if (radius >= _endRadius) FinishWave();
        }

        protected override void OnEnd(bool interrupted)
        {
            // The slam already happened; an interruption just stops the remaining wave.
            _waveRunning = false;
            _processedPlayers?.Clear();
            _processedBalls?.Clear();
        }

        protected override void OnRoundReset()
        {
            _waveRunning = false;
            _processedPlayers?.Clear();
            _processedBalls?.Clear();
        }

        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || ctx.Self == null) return 0f;
            int score = 0;
            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (!IsAffectable(p)) continue;
                if (p.Motor != null && !p.Motor.IsGrounded) continue;
                score += 1;
                if (p.Combat != null && p.Combat.HasBall) score += p.Combat.IsCharging ? 2 : 1;
            }
            if (score < aiMinimumScore) return 0f;
            return Mathf.Clamp01(Data.aiWeight * (0.6f + 0.1f * score));
        }

        // ------------------------------------------------------------------ wave

        private void PropagateWave(float radius)
        {
            float r2 = radius * radius;
            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || _processedPlayers.Contains(p) || !IsEnemy(p)) continue;
                if (PlanarSqrDistance(p.Position, _epicentre) > r2) continue;
                _processedPlayers.Add(p); // the wave passes each player exactly once
                if (IsAffectable(p)) Quake(p);
            }

            if (freeBallHop <= 0f || BallManager.Instance == null) return;
            var balls = BallManager.Instance.MatchBalls;
            for (int i = 0; i < balls.Count; i++)
            {
                var ball = balls[i];
                if (ball == null || !ball.IsFree || _processedBalls.Contains(ball)) continue;
                if (PlanarSqrDistance(ball.transform.position, _epicentre) > r2) continue;
                _processedBalls.Add(ball);
                var body = ball.Body;
                if (body == null || body.isKinematic) continue;
                // Only balls resting/rolling on the floor get kicked (a ball in the air does not feel the floor).
                if (ball.transform.position.y - _epicentre.y > ball.Radius + 0.12f) continue;
                float falloff = 1f - 0.5f * Mathf.Clamp01(radius / Mathf.Max(1f, maxRadius));
                body.SetVelocity(body.GetVelocity() + Vector3.up * (freeBallHop * falloff));
            }
        }

        private bool IsAffectable(DodgeballPlayer p)
        {
            if (p == null || !IsEnemy(p) || !p.IsInfield || p.Health == null || !p.Health.IsAlive) return false;
            if (!affectsInvulnerable && p.Status != null && p.Status.Has(StatusEffectType.Invulnerable)) return false;
            return true;
        }

        private void Quake(DodgeballPlayer victim)
        {
            // Airborne players (they jumped in time) do not feel the floor.
            if (victim.Motor != null && !victim.Motor.IsGrounded) return;

            Vector3 outward = ScrewsGadgetKit.Planar(victim.Position - _epicentre, victim.Forward);

            var combat = victim.Combat;
            if (combat != null)
            {
                if (combat.IsCharging) combat.CancelCharge();
                if (combat.IsCatchArmed) combat.CancelCatch();
                if (combat.HasBall) combat.DropBall(outward * dropOutwardSpeed + Vector3.up * dropUpSpeed);
            }

            if (victim.Motor != null)
            {
                Vector2 jitter = UnityEngine.Random.insideUnitCircle * horizontalJitter;
                victim.Motor.AddImpulse(Vector3.up * jumpVelocity + new Vector3(jitter.x, 0f, jitter.y));
            }

            LastAffectedCount++;
            VfxManager.Spawn(VfxId.FloorImpactDust, victim.Position, Quaternion.identity, 1f);
            AudioManager.PlayAt(SfxId.Land, victim.Position, 0.7f, 0.85f);
        }

        private void FinishWave()
        {
            _waveRunning = false;
        }

        private float ComputeEndRadius()
        {
            // Stop as soon as the wave has passed every enemy and every loose ball (capped at maxRadius).
            float farthest = 0f;
            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || !IsEnemy(p)) continue;
                farthest = Mathf.Max(farthest, PlanarSqrDistance(p.Position, _epicentre));
            }
            if (freeBallHop > 0f && BallManager.Instance != null)
            {
                var balls = BallManager.Instance.MatchBalls;
                for (int i = 0; i < balls.Count; i++)
                {
                    if (balls[i] == null) continue;
                    farthest = Mathf.Max(farthest, PlanarSqrDistance(balls[i].transform.position, _epicentre));
                }
            }
            return Mathf.Min(maxRadius, Mathf.Sqrt(farthest) + 0.5f);
        }

        private static float PlanarSqrDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
