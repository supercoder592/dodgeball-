using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using DodgeballUltra.Juice;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Ball payload of Elsa's [Glacier Freeze].
    /// <list type="bullet">
    /// <item><see cref="OnHitPlayer"/>: the freezing hit itself deals no damage (<c>hit.Damage = 0</c>) and no knockback -
    /// the victim is frozen in place instead.</item>
    /// <item><see cref="OnAfterHitPlayer"/>: when the hit really landed (outcome <see cref="HitOutcome.Damaged"/>, i.e. not
    /// negated by a shield/evasion and not an elimination), applies <see cref="StatusEffectType.Frozen"/> for 2.5 s with
    /// this payload as the source. The <see cref="StatusEffectController"/> turns that into: cannot move, cannot catch,
    /// Incapacitated(Frozen), ice-encased visual. Allies have no way to remove it. A second ball hit while frozen
    /// eliminates (<see cref="PlayerHealth"/> rule: Frozen + hit =&gt; ForceEliminate) - which also applies when the second
    /// ball is another Glacier Freeze.</item>
    /// </list>
    /// Presentation: cold vapour (FrozenMist) streams off the ball in flight, an IceBurst shatters at every contact, and the
    /// frozen victim steams with FrozenMist for the whole freeze.
    /// </summary>
    public sealed class ElsaFreezePayload : BallPayloadBase
    {
        private VfxHandle _flightMist;

        /// <summary>Elsa.</summary>
        public DodgeballPlayer Caster { get; }

        /// <summary>Seconds a hit player stays frozen (spec 2.5 s).</summary>
        public float FreezeDuration { get; }

        /// <summary>Tint for the ice effects.</summary>
        public Color IceTint { get; }

        /// <summary>Scale of the IceBurst on a player hit (floor/catch bursts are smaller).</summary>
        public float BurstScale { get; }

        /// <summary>Player frozen by this ball (null until it hits).</summary>
        public DodgeballPlayer FrozenVictim { get; private set; }

        public ElsaFreezePayload(DodgeballPlayer caster, float freezeDuration, Color iceTint, float burstScale = 1f)
        {
            Caster = caster;
            FreezeDuration = Mathf.Max(0.05f, freezeDuration);
            IceTint = iceTint;
            BurstScale = Mathf.Max(0.1f, burstScale);
        }

        public override void OnLaunched(DodgeBall ball)
        {
            if (ball == null) return;
            // Supercooled ball: visible cold vapour trails off it (looping until the ball stops being live).
            _flightMist = VfxManager.SpawnAttached(VfxId.FrozenMist, ball.transform, Vector3.zero, 0.35f, IceTint);
        }

        public override bool OnHitPlayer(DodgeBall ball, ref HitContext hit)
        {
            // The freezing hit itself does not damage and does not shove the victim: it locks them in place.
            hit.Damage = 0f;
            hit.KnockbackImpulse = 0f;
            return true;
        }

        public override void OnAfterHitPlayer(DodgeBall ball, in HitContext hit, HitOutcome outcome)
        {
            var victim = hit.Victim;
            Vector3 point = hit.Point;

            if (outcome == HitOutcome.Ignored) return;

            // Shatter burst wherever the ball struck (also on shields / evasions, the ball still breaks).
            VfxManager.Spawn(VfxId.IceBurst, point, NormalRotation(hit.Normal), BurstScale, IceTint);
            AudioManager.PlayAt(SfxId.Freeze, point, outcome == HitOutcome.Damaged ? 1f : 0.6f);

            // Only a hit that really landed and left the victim in play freezes them. Negated (shield / evasion) never
            // freezes; Eliminated (already frozen => second hit) and delayed/prevented eliminations belong to their own
            // pipelines (ragdoll, Chrono, Specter) and must not be overlaid with an ice block.
            if (outcome != HitOutcome.Damaged) return;
            if (victim == null || victim.Status == null || !victim.IsInfield) return;
            if (victim.Health != null && !victim.Health.IsAlive) return;

            Freeze(victim);
        }

        public override void OnHitSurface(DodgeBall ball, Vector3 point, Vector3 normal, Collider surface, bool isFloor)
        {
            // The supercooled shell cracks on impact: small frost burst, no gameplay effect.
            VfxManager.Spawn(VfxId.IceBurst, point, NormalRotation(normal), BurstScale * 0.45f, IceTint);
            AudioManager.PlayAt(SfxId.Freeze, point, 0.35f, 1.15f);
        }

        public override void OnCaught(DodgeBall ball, DodgeballPlayer catcher, CatchQuality quality)
        {
            // A clean catch smothers the ball: a puff of frost in the hands, no freeze.
            Vector3 p = ball != null ? ball.transform.position : catcher != null ? catcher.ChestPosition : Vector3.zero;
            VfxManager.Spawn(VfxId.IceBurst, p, Quaternion.identity, BurstScale * 0.35f, IceTint);
        }

        public override void OnEnded(DodgeBall ball)
        {
            VfxManager.StopEffect(_flightMist);
            _flightMist = default;
        }

        /// <summary>Applies the freeze to <paramref name="victim"/> with this payload as source (Allies cannot remove it).</summary>
        private void Freeze(DodgeballPlayer victim)
        {
            FrozenVictim = victim;
            victim.Status.Apply(StatusEffectType.Frozen, FreezeDuration, 1f, this);

            // Defensive: the StatusEffectController drives the Incapacitated(Frozen) state which already does this; doing it
            // here too guarantees a raised catch stance or a charging arm is cancelled on the very frame of the freeze.
            var combat = victim.Combat;
            if (combat != null)
            {
                if (combat.IsCharging) combat.CancelCharge();
                if (combat.IsCatchArmed) combat.CancelCatch();
            }
            if (victim.Abilities != null) victim.Abilities.InterruptAll(InterruptReason.Frozen);

            // The ice block steams for the whole freeze.
            VfxManager.SpawnAttached(VfxId.FrozenMist, victim.transform, Vector3.up * 1f, 1f, IceTint, FreezeDuration);
            AudioManager.PlayAt(SfxId.Freeze, victim.ChestPosition, 1f, 0.9f);

            if (victim.IsLocalPlayer) ScreenFx.Pulse(ScreenPulse.Freeze, 0.9f, Mathf.Min(FreezeDuration, 0.8f));
            else if (Caster != null && Caster.IsLocalPlayer) ScreenFx.Pulse(ScreenPulse.Freeze, 0.25f, 0.2f);
        }

        private static Quaternion NormalRotation(Vector3 normal) =>
            normal.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(normal) : Quaternion.identity;
    }
}
