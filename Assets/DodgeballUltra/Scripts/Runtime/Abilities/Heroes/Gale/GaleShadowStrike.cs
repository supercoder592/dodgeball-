using System;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Events;
using DodgeballUltra.Juice;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Gale's ultimate <b>[Shadow Strike]</b>: "instantly teleports directly behind the nearest unheld ball and picks it up".
    /// <para>
    /// Target: the nearest match ball that nobody holds (Free, or frozen mid-air in Chrono's Stasis; never a conjured
    /// ability projectile) lying inside Gale's own zone confinement expanded by <see cref="zoneExpansion"/> m, so a ball
    /// resting just across a line is still reachable but Gale can never be placed illegally. Gale appears
    /// <see cref="behindDistance"/> m behind the ball relative to her attack direction (the ball ends up between Gale and the
    /// enemy), clamped into her zone, facing the enemy, with a TeleportPoof at both ends - then the ball is put straight
    /// into her hand (<see cref="PlayerCombatController.GiveBall"/>).
    /// </para>
    /// <para>Fails with <see cref="AbilityFailReason.NoTarget"/> when no ball qualifies, and refuses while already holding a ball.</para>
    /// </summary>
    [Serializable]
    public sealed class GaleShadowStrike : AbilityBase
    {
        [Header("Targeting")]
        [Tooltip("Margin (m) added around Gale's zone confinement when searching for balls. Spec: 0.6 m.")]
        [Range(0f, 3f)] public float zoneExpansion = 0.6f;

        [Tooltip("Balls frozen mid-air by Chrono's Stasis Field can be snatched too.")]
        public bool includeStasisBalls = true;

        [Header("Teleport")]
        [Tooltip("How far (m) behind the ball Gale materialises, relative to her attack direction.")]
        [Range(0.3f, 2f)] public float behindDistance = 0.7f;

        [Tooltip("Tint of the teleport smoke.")]
        public Color poofTint = new Color(0.22f, 0.24f, 0.3f, 1f);

        [Tooltip("Camera shake amplitude at the arrival point.")]
        [Range(0f, 1f)] public float arrivalShake = 0.25f;

        public GaleShadowStrike() { }

        protected override bool CanActivateCustom(out AbilityFailReason reason)
        {
            var combat = Owner.Combat;
            if (combat == null || combat.HasBall)
            {
                // Hands must be free to snatch the ball.
                reason = AbilityFailReason.Custom;
                return false;
            }

            if (FindTargetBall() == null)
            {
                reason = AbilityFailReason.NoTarget;
                return false;
            }

            reason = AbilityFailReason.None;
            return true;
        }

        protected override void OnCastStarted()
        {
            VfxManager.Spawn(VfxId.CloakShimmer, Owner.ChestPosition, Owner.Rotation, 0.6f, poofTint);
            AudioManager.PlayAt(SfxId.UltimateCast, Owner.ChestPosition, 0.8f, 1.1f);
        }

        protected override void OnCast()
        {
            var combat = Owner.Combat;
            var ball = combat != null && !combat.HasBall ? FindTargetBall() : null;
            if (ball == null)
            {
                // The ball was taken during the channel: refund the meter and report the failure to HUD / AI.
                if (Slot == AbilitySlot.Ultimate && Owner.Abilities != null && Data != null)
                    Owner.Abilities.AddUltimateCharge(Data.ultimateCost, UltGainReason.Ability);
                GameEvents.Publish(new AbilityFailedEvent { Player = Owner, Ability = this, Slot = Slot, Reason = AbilityFailReason.NoTarget });
                EndAbility();
                return;
            }

            Vector3 attack = AttackDirection();
            Vector3 ballGround = AbilityUtil.GroundPoint(ball.transform.position);

            // Directly behind the ball: ball between Gale and the enemy. Court rules win over the exact spot.
            Vector3 destination = AbilityUtil.ClampToPlayerZone(Owner, ballGround - attack * behindDistance);
            destination.y = AbilityUtil.GroundPoint(destination).y;

            Vector3 look = ballGround - destination;
            look.y = 0f;
            if (look.sqrMagnitude < 0.01f) look = attack;
            Quaternion facing = Quaternion.LookRotation(look.normalized, Vector3.up);

            Vector3 departure = Owner.ChestPosition;
            VfxManager.Spawn(VfxId.TeleportPoof, departure, Owner.Rotation, 1f, poofTint);
            AudioManager.PlayAt(SfxId.Teleport, departure, 0.9f, 1.05f);

            if (combat.IsCatchArmed) combat.CancelCatch();
            Owner.Teleport(destination, facing);

            Vector3 arrival = Owner.ChestPosition;
            VfxManager.Spawn(VfxId.TeleportPoof, arrival, facing, 1f, poofTint);
            AudioManager.PlayAt(SfxId.Teleport, arrival, 1f, 0.95f);
            var juice = JuiceManager.Instance;
            if (juice != null && arrivalShake > 0f) juice.Shake(arrivalShake, 20f, 0.2f, arrival);

            combat.GiveBall(ball);
        }

        /// <summary>
        /// Worth the meter when Gale is empty-handed and the best ball is far away (a long walk she skips), and as an
        /// escape blink when a ball is about to hit her. Never when a loose ball is already at her feet.
        /// </summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || ctx.Self == null || ctx.HoldingBall) return 0f;
            var ball = FindTargetBall();
            if (ball == null) return 0f;

            Vector3 d = ball.transform.position - ctx.Self.Position;
            d.y = 0f;
            float distance = d.magnitude;

            float utility = Data.aiWeight * Mathf.Clamp01((distance - 2.5f) / 7f);
            if (ctx.IncomingBall != null && ctx.IncomingTimeToImpact < 0.5f && distance > 1.5f) utility = Mathf.Max(utility, Data.aiWeight * 0.7f);
            if (ctx.EnemiesInfield > 0 && ctx.FreeBallsNearby == 0) utility += 0.15f;
            return Mathf.Clamp01(utility);
        }

        // ------------------------------------------------------------------ targeting

        /// <summary>Nearest eligible ball (see class docs) or null. Allocation-free.</summary>
        public DodgeBall FindTargetBall()
        {
            var manager = BallManager.Instance;
            if (manager == null || Owner == null) return null;
            var balls = manager.ActiveBalls;
            if (balls == null) return null;

            var court = Court.Instance;
            Bounds zone = default;
            bool hasZone = court != null && Owner.Team.IsValid();
            if (hasZone)
            {
                zone = court.GetConfinement(Owner.Team, Owner.Zone);
                zone.Expand(new Vector3(zoneExpansion * 2f, 0f, zoneExpansion * 2f));
            }

            DodgeBall best = null;
            float bestSqr = float.PositiveInfinity;
            Vector3 from = Owner.Position;
            for (int i = 0; i < balls.Count; i++)
            {
                var ball = balls[i];
                if (ball == null || ball.IsAbilityBall || ball.Holder != null) continue;
                bool eligibleState = ball.State == BallState.Free || (includeStasisBalls && ball.State == BallState.Stasis);
                if (!eligibleState) continue;

                Vector3 p = ball.transform.position;
                if (hasZone && (p.x < zone.min.x || p.x > zone.max.x || p.z < zone.min.z || p.z > zone.max.z)) continue;

                float dx = p.x - from.x;
                float dz = p.z - from.z;
                float sqr = dx * dx + dz * dz;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = ball;
                }
            }
            return best;
        }

        private Vector3 AttackDirection()
        {
            var court = Court.Instance;
            Vector3 dir = court != null && Owner.Team.IsValid() ? court.AttackDirection(Owner.Team) : Owner.Forward;
            // The outfield strip lies behind the OPPONENT's baseline: from there the enemy is the other way.
            if (court != null && Owner.Team.IsValid() && Owner.Zone == CourtZone.Outfield) dir = -dir;
            dir.y = 0f;
            return dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
        }
    }
}
