using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using DodgeballUltra.Juice;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>Tuning snapshot handed from <see cref="RayneHyperbeamTranspierce"/> to its <see cref="BeamPayload"/>.</summary>
    public struct BeamSettings
    {
        /// <summary>Minimum knockback (m/s velocity change) for enemies that survive the beam (e.g. Gouki's 200 HP).</summary>
        public float KnockbackSpeed;

        /// <summary>Extra hitstop (s, clamped by JuiceManager to 0.03-0.1) per enemy pierced.</summary>
        public float HitstopDuration;

        public float ShakeAmplitude;
        public float ShakeFrequency;
        public float ShakeDuration;

        /// <summary>Plasma colour of the trail and the impact flashes.</summary>
        public Color Tint;

        /// <summary>Scale of the attached BeamTrail effect (the ball is 30% bigger than a match ball).</summary>
        public float TrailScale;
    }

    /// <summary>
    /// Ball payload of Rayne's ultimate <see cref="RayneHyperbeamTranspierce"/>. The ball itself is flagged Unblockable +
    /// Pierce (so DodgeBall keeps flying through every enemy on its line and shields / evasion cannot stop it); this
    /// payload adds the beam look and "heavy" juice on every pierce on top of the standard hit pipeline:
    /// <code>
    /// OnLaunched       -> BeamTrail VFX (attached) + beam SFX
    /// OnHitPlayer      -> hit.Unblockable = true, knockback raised to the beam minimum
    /// OnAfterHitPlayer -> BeamImpact VFX, extra hitstop, heavy Perlin shake, heavy screen pulse, heavy impact SFX
    /// OnHitSurface     -> BeamImpact scorch on the wall / floor that finally stops it
    /// OnEnded          -> stop trail, IsResolved
    /// </code>
    /// </summary>
    public sealed class BeamPayload : BallPayloadBase
    {
        private readonly DodgeballPlayer _thrower;
        private readonly BeamSettings _settings;
        private VfxHandle _trail;

        /// <summary>The beam ball (null until launched).</summary>
        public DodgeBall Ball { get; private set; }

        /// <summary>Number of enemies the beam went through.</summary>
        public int EnemiesPierced { get; private set; }

        /// <summary>Number of those that were eliminated.</summary>
        public int Eliminations { get; private set; }

        /// <summary>True once the ball stopped being live.</summary>
        public bool IsResolved { get; private set; }

        /// <summary>True once the beam struck a wall / the floor (it is spent and should stop being live).</summary>
        public bool HasHitSurface { get; private set; }

        /// <summary>Raised once when the ball stops being live.</summary>
        public event Action<BeamPayload> Resolved;

        public BeamPayload(DodgeballPlayer thrower, in BeamSettings settings)
        {
            _thrower = thrower;
            _settings = settings;
        }

        public override void OnLaunched(DodgeBall ball)
        {
            Ball = ball;
            if (ball == null) return;
            _trail = VfxManager.SpawnAttached(VfxId.BeamTrail, ball.transform, Vector3.zero, Mathf.Max(0.1f, _settings.TrailScale), _settings.Tint);
            AudioManager.PlayAt(SfxId.Beam, ball.transform.position, 1f, 1f);
        }

        public override bool OnHitPlayer(DodgeBall ball, ref HitContext hit)
        {
            // The beam ignores shields and evasion filters, and shoves survivors hard.
            hit.Unblockable = true;
            hit.KnockbackImpulse = Mathf.Max(hit.KnockbackImpulse, _settings.KnockbackSpeed);
            return true;
        }

        public override void OnAfterHitPlayer(DodgeBall ball, in HitContext hit, HitOutcome outcome)
        {
            if (outcome == HitOutcome.Ignored) return;

            EnemiesPierced++;
            if (outcome == HitOutcome.Eliminated) Eliminations++;

            Vector3 travel = hit.BallVelocity.sqrMagnitude > 1e-4f ? hit.BallVelocity.normalized : (ball != null ? ball.transform.forward : Vector3.forward);
            VfxManager.Spawn(VfxId.BeamImpact, hit.Point, Quaternion.LookRotation(travel), 1.2f, _settings.Tint);
            AudioManager.PlayAt(SfxId.BallHitHeavy, hit.Point, 1f, UnityEngine.Random.Range(0.85f, 0.95f));

            // Heavy juice on top of the standard hit pipeline (JuiceManager merges overlapping hitstops instead of stacking).
            var juice = JuiceManager.Instance;
            if (juice != null)
            {
                juice.Hitstop(_settings.HitstopDuration);
                juice.Shake(_settings.ShakeAmplitude, _settings.ShakeFrequency, _settings.ShakeDuration, hit.Point);
            }

            bool localInvolved = (hit.Victim != null && hit.Victim.IsLocalPlayer) || (_thrower != null && _thrower.IsLocalPlayer);
            if (localInvolved) ScreenFx.Pulse(ScreenPulse.HeavyHit, 0.85f, 0.35f);
        }

        public override void OnHitSurface(DodgeBall ball, Vector3 point, Vector3 normal, Collider surface, bool isFloor)
        {
            // The beam finally grounds out on the court: scorch flash + a lighter rumble.
            if (HasHitSurface) return;
            HasHitSurface = true;
            Quaternion rot = normal.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(normal) : Quaternion.identity;
            VfxManager.Spawn(VfxId.BeamImpact, point, rot, 0.9f, _settings.Tint);
            AudioManager.PlayAt(isFloor ? SfxId.BallBounceFloor : SfxId.BallBounceWall, point, 1f, 0.8f);
            var juice = JuiceManager.Instance;
            if (juice != null) juice.Shake(_settings.ShakeAmplitude * 0.4f, _settings.ShakeFrequency, _settings.ShakeDuration * 0.6f, point);
        }

        public override void OnCaught(DodgeBall ball, DodgeballPlayer catcher, CatchQuality quality) => StopTrail();

        public override void OnEnded(DodgeBall ball)
        {
            StopTrail();
            if (IsResolved) return;
            IsResolved = true;
            Resolved?.Invoke(this);
        }

        private void StopTrail()
        {
            if (!_trail.IsValid) return;
            VfxManager.StopEffect(_trail);
            _trail = default;
        }
    }
}
