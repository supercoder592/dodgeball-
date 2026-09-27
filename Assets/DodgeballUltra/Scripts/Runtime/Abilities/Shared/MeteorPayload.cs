using System;
using System.Collections.Generic;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using DodgeballUltra.Juice;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Tuning snapshot handed from <see cref="RayneSupersonicMeteor"/> to its <see cref="MeteorPayload"/> at cast time.
    /// Copied by value so later Inspector edits never change a meteor that is already in flight.
    /// </summary>
    public struct MeteorShockwaveSettings
    {
        /// <summary>AOE radius (m) of the shockwave measured on the floor plane. Spec: 3 m.</summary>
        public float Radius;

        /// <summary>Horizontal velocity change (m/s) given to an enemy standing at the epicentre.</summary>
        public float KnockbackSpeed;

        /// <summary>Fraction (0..1) of <see cref="KnockbackSpeed"/> still applied at the rim of the AOE.</summary>
        public float EdgeKnockbackFraction;

        /// <summary>Upward velocity change (m/s) at the epicentre (scaled by the same falloff).</summary>
        public float UpwardSpeed;

        /// <summary>Enemies within this planar distance (m) of the epicentre are briefly stunned.</summary>
        public float CoreStunRadius;

        /// <summary>Stun length (s) inside <see cref="CoreStunRadius"/>.</summary>
        public float CoreStunDuration;

        /// <summary>Velocity change (m/s) given to loose (Free) balls at the epicentre.</summary>
        public float FreeBallPushSpeed;

        /// <summary>Detonate when the meteor is stopped by an obstacle that is neither a player nor the court (clones...).</summary>
        public bool DetonateOnObstacles;

        /// <summary>Colour of the fire trail and the shockwave ring.</summary>
        public Color FireTint;

        public float ShakeAmplitude;
        public float ShakeFrequency;
        public float ShakeDuration;

        /// <summary>Scale of the attached fire trail effect.</summary>
        public float TrailScale;
    }

    /// <summary>
    /// Ball payload of Rayne's <see cref="RayneSupersonicMeteor"/>: a fire trail while the ball flies and a single
    /// radial shockwave the first time the meteor strikes something.
    /// <code>
    /// OnLaunched       -> attach FireTrail VFX
    /// OnAfterHitPlayer -> Detonate(hit point)        (the direct hit itself was already resolved normally by Combat)
    /// OnHitSurface     -> Detonate(contact point)    (floor, walls, bleachers)
    /// OnCaught         -> the fire is smothered: no shockwave (a catch is the counter-play)
    /// OnEnded          -> optional obstacle detonation, stop trail, IsResolved = true
    /// </code>
    /// The shockwave only affects ENEMIES of the thrower: radial knockback that falls off linearly with distance, a small
    /// upward component (so the body visibly lifts and the motor's momentum carries it) and a short stun at the core.
    /// </summary>
    public sealed class MeteorPayload : BallPayloadBase
    {
        /// <summary>The Shockwave VFX is authored for a ring of this radius (m); the spawn scale is Radius / this.</summary>
        public const float ShockwaveVfxReferenceRadius = 3f;

        private readonly DodgeballPlayer _thrower;
        private readonly TeamId _throwerTeam;
        private readonly MeteorShockwaveSettings _settings;

        // Per-payload scratch list (one meteor = one payload), so re-entrant detonations can never clobber each other.
        private readonly List<DodgeballPlayer> _overlap = new List<DodgeballPlayer>(6);

        private VfxHandle _trail;
        private bool _caught;

        /// <summary>The conjured ball (null until launched).</summary>
        public DodgeBall Ball { get; private set; }

        /// <summary>True once the shockwave went off.</summary>
        public bool HasDetonated { get; private set; }

        /// <summary>True once the ball stopped being live for any reason (hit, bounce, catch, despawn).</summary>
        public bool IsResolved { get; private set; }

        /// <summary>Floor point of the detonation (valid when <see cref="HasDetonated"/>).</summary>
        public Vector3 DetonationPoint { get; private set; }

        /// <summary>How many enemies the shockwave pushed.</summary>
        public int EnemiesKnockedBack { get; private set; }

        /// <summary>Raised once when the shockwave detonates.</summary>
        public event Action<MeteorPayload> Detonated;

        /// <summary>Raised once when the ball stops being live.</summary>
        public event Action<MeteorPayload> Resolved;

        public MeteorPayload(DodgeballPlayer thrower, in MeteorShockwaveSettings settings)
        {
            _thrower = thrower;
            _throwerTeam = thrower != null ? thrower.Team : TeamId.None;
            _settings = settings;
        }

        // ------------------------------------------------------------------ IBallPayload

        public override void OnLaunched(DodgeBall ball)
        {
            Ball = ball;
            if (ball == null) return;

            // Looping fire trail that rides on the ball until the payload ends.
            _trail = VfxManager.SpawnAttached(VfxId.FireTrail, ball.transform, Vector3.zero,
                Mathf.Max(0.1f, _settings.TrailScale), _settings.FireTint);
        }

        public override void OnAfterHitPlayer(DodgeBall ball, in HitContext hit, HitOutcome outcome)
        {
            // Ignored = the ball could not interact with that player at all (teammate / outfield): keep flying.
            if (outcome == HitOutcome.Ignored) return;

            // The direct victim already received the full hit (damage + hit knockback) from Combat; the shockwave
            // spreads the impact to everybody standing around them.
            Detonate(hit.Point, hit.Victim);
        }

        public override void OnHitSurface(DodgeBall ball, Vector3 point, Vector3 normal, Collider surface, bool isFloor)
        {
            Detonate(point, null);
        }

        public override void OnCaught(DodgeBall ball, DodgeballPlayer catcher, CatchQuality quality)
        {
            // A clean catch smothers the fire: that is the enemy's counter-play, so no shockwave.
            _caught = true;
            StopTrail();
        }

        public override void OnEnded(DodgeBall ball)
        {
            // Stopped by something that is neither a player nor the court (e.g. a Shadow clone absorbing it): the meteor
            // still bursts where it stopped. Held/Stasis/Despawned balls (catches, Chrono, Houdini, round resets) never do.
            if (_settings.DetonateOnObstacles && !HasDetonated && !_caught && ball != null && ball.State == BallState.Free)
            {
                Detonate(ball.transform.position, null);
            }

            StopTrail();
            if (IsResolved) return;
            IsResolved = true;
            Resolved?.Invoke(this);
        }

        // ------------------------------------------------------------------ shockwave

        /// <summary>
        /// Fires the shockwave at <paramref name="impactPoint"/> (at most once per meteor). <paramref name="directVictim"/>
        /// is excluded from the knockback because the ball hit already pushed them.
        /// </summary>
        public void Detonate(Vector3 impactPoint, DodgeballPlayer directVictim)
        {
            if (HasDetonated) return;
            HasDetonated = true;

            // Never push players around outside live play (round resets recycle ability balls).
            var match = MatchManager.Instance;
            bool live = match == null || match.IsPlaying;

            // The blast spreads along the floor: every distance below is planar, measured from the floor point.
            Vector3 epicentre = AbilityUtil.GroundPoint(impactPoint);
            DetonationPoint = epicentre;
            float radius = Mathf.Max(0.1f, _settings.Radius);

            PlayEffects(epicentre, radius);

            if (live)
            {
                EnemiesKnockedBack = KnockBackEnemies(epicentre, radius, directVictim);
                PushLooseBalls(epicentre, radius);
            }

            Detonated?.Invoke(this);
        }

        private int KnockBackEnemies(Vector3 epicentre, float radius, DodgeballPlayer directVictim)
        {
            if (!_throwerTeam.IsValid()) return 0;

            // Candidates: opponents whose feet are inside the (slightly generous, 3D) sphere; filtered on the plane below.
            PlayerRegistry.OverlapSphere(epicentre, radius + 0.5f, _overlap, _throwerTeam.Opponent());

            // A stray direction for players standing exactly on the epicentre: along the meteor's flight.
            Vector3 fallbackDir = Ball != null ? Flatten(Ball.Velocity) : Vector3.zero;
            if (fallbackDir.sqrMagnitude < 1e-6f && _thrower != null) fallbackDir = _thrower.Forward;
            if (fallbackDir.sqrMagnitude < 1e-6f) fallbackDir = Vector3.forward;
            fallbackDir.Normalize();

            int pushed = 0;
            for (int i = 0; i < _overlap.Count; i++)
            {
                var enemy = _overlap[i];
                if (enemy == null || enemy == directVictim || !enemy.IsTargetable) continue;
                if (enemy.Status != null && enemy.Status.Has(StatusEffectType.Invulnerable)) continue; // Precognition Dodge etc.

                Vector3 offset = Flatten(enemy.Position - epicentre);
                float distance = offset.magnitude;
                if (distance > radius) continue;

                // Linear falloff from full strength at the epicentre to EdgeKnockbackFraction at the rim.
                float proximity = 1f - Mathf.Clamp01(distance / radius);
                float strength = Mathf.Lerp(Mathf.Clamp01(_settings.EdgeKnockbackFraction), 1f, proximity);
                Vector3 dir = distance > 0.05f ? offset / distance : fallbackDir;
                Vector3 impulse = dir * (_settings.KnockbackSpeed * strength) + Vector3.up * (_settings.UpwardSpeed * strength);

                if (enemy.Motor != null) enemy.Motor.AddImpulse(impulse);
                if (distance <= _settings.CoreStunRadius && _settings.CoreStunDuration > 0f && enemy.StateMachine != null)
                {
                    enemy.StateMachine.Stun(_settings.CoreStunDuration);
                }

                pushed++;
            }

            _overlap.Clear();
            return pushed;
        }

        private void PushLooseBalls(Vector3 epicentre, float radius)
        {
            var balls = BallManager.Instance != null ? BallManager.Instance.ActiveBalls : null;
            if (balls == null || _settings.FreeBallPushSpeed <= 0f) return;

            for (int i = 0; i < balls.Count; i++)
            {
                var b = balls[i];
                if (b == null || b == Ball || b.State != BallState.Free || b.Body == null || b.Body.isKinematic) continue;

                Vector3 offset = Flatten(b.transform.position - epicentre);
                float distance = offset.magnitude;
                if (distance > radius || distance < 1e-3f) continue;

                float proximity = 1f - Mathf.Clamp01(distance / radius);
                Vector3 push = (offset / distance) * (_settings.FreeBallPushSpeed * proximity) +
                               Vector3.up * (_settings.FreeBallPushSpeed * 0.35f * proximity);
                b.Body.SetVelocity(b.Body.GetVelocity() + push);
            }
        }

        private void PlayEffects(Vector3 epicentre, float radius)
        {
            // Ground ring + dust kicked up by the blast; the ring is scaled to the gameplay radius so what you see is what hits.
            Vector3 fxPos = epicentre + Vector3.up * 0.02f;
            VfxManager.Spawn(VfxId.Shockwave, fxPos, Quaternion.identity, radius / ShockwaveVfxReferenceRadius, _settings.FireTint);
            VfxManager.Spawn(VfxId.FloorImpactDust, fxPos, Quaternion.identity, 1.5f);
            AudioManager.PlayAt(SfxId.Shockwave, epicentre, 1f, UnityEngine.Random.Range(0.94f, 1.04f));

            var juice = JuiceManager.Instance;
            if (juice != null) juice.Shake(_settings.ShakeAmplitude, _settings.ShakeFrequency, _settings.ShakeDuration, epicentre);

            // Local player caught in (or right next to) the blast gets a screen punch.
            var local = PlayerRegistry.LocalPlayer;
            if (local != null && Flatten(local.Position - epicentre).sqrMagnitude <= (radius * 1.5f) * (radius * 1.5f))
            {
                ScreenFx.Pulse(ScreenPulse.Hit, 0.6f, 0.3f);
            }
        }

        private void StopTrail()
        {
            if (!_trail.IsValid) return;
            VfxManager.StopEffect(_trail);
            _trail = default;
        }

        private static Vector3 Flatten(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
