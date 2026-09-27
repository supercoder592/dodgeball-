using System;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Combat
{
    /// <summary>
    /// CONTRACT (kernel) - converts <see cref="ThrowParams"/> into a launch velocity:
    /// iterative lead targeting on the target's current velocity + low-arc ballistic solution (Core.Ballistics) at
    /// <see cref="ThrowParams.FinalSpeed"/> with gravity * GravityScale. Falls back to a straight aim when unsolvable.
    /// <para>Owner module: Combat.</para>
    /// </summary>
    public static class ThrowSolver
    {
        /// <summary>Launch velocity for <paramref name="p"/>. <paramref name="flightTime"/> is the predicted time to target (or +inf).</summary>
        public static Vector3 Solve(in ThrowParams p, out float flightTime) => throw new NotImplementedException();

        /// <summary>Velocity that makes a projectile from <paramref name="origin"/> hit <paramref name="targetPoint"/> at <paramref name="speed"/>.</summary>
        public static bool SolveStatic(Vector3 origin, Vector3 targetPoint, float speed, float gravity, out Vector3 velocity, out float flightTime)
            => throw new NotImplementedException();

        /// <summary>Lead targeting: aim point where <paramref name="target"/> will be when the ball arrives.</summary>
        public static Vector3 PredictInterceptPoint(Vector3 origin, DodgeballPlayer target, float speed, float gravity, int iterations = 3)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// CONTRACT (kernel) - predicts ball flight (gravity-aware) for catch IK, AI dodging and Danger Sense.
    /// <para>Owner module: Combat.</para>
    /// </summary>
    public static class TrajectoryPredictor
    {
        /// <summary>Position of a projectile after <paramref name="t"/> seconds.</summary>
        public static Vector3 PositionAt(Vector3 position, Vector3 velocity, float gravity, float t)
            => position + velocity * t + 0.5f * t * t * gravity * Vector3.down;

        /// <summary>
        /// Earliest time within <paramref name="maxTime"/> at which the ball comes within <paramref name="radius"/> of
        /// <paramref name="point"/> (sampled + refined). Returns false if it never does.
        /// </summary>
        public static bool TimeToReach(DodgeBall ball, Vector3 point, float radius, float maxTime, out float time, out Vector3 ballPositionAtTime)
            => throw new NotImplementedException();

        /// <summary>Predicted impact on <paramref name="player"/>'s body (capsule). False if the ball will miss.</summary>
        public static bool PredictImpact(DodgeBall ball, DodgeballPlayer player, float maxTime, out float timeToImpact, out Vector3 impactPoint)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// CONTRACT (kernel) - aim assist / soft lock-on.
    /// <para>Owner module: Combat.</para>
    /// </summary>
    public static class TargetingSystem
    {
        /// <summary>
        /// Best enemy for <paramref name="thrower"/> inside a cone of <paramref name="maxAngle"/> degrees around
        /// <paramref name="aimDirection"/> within <paramref name="maxDistance"/>; scored by angle and distance and line of sight.
        /// </summary>
        public static DodgeballPlayer FindBestTarget(DodgeballPlayer thrower, Vector3 aimOrigin, Vector3 aimDirection, float maxAngle, float maxDistance)
            => throw new NotImplementedException();

        /// <summary>Next target after <paramref name="current"/> in screen/angle order (cycle target input).</summary>
        public static DodgeballPlayer CycleTarget(DodgeballPlayer thrower, DodgeballPlayer current, Vector3 aimOrigin, Vector3 aimDirection)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// CONTRACT (kernel) - applies a live-ball hit to a player: builds the HitContext, runs payload hooks,
    /// PlayerHealth.ReceiveHit, knockback, and publishes BallHitPlayerEvent (which feeds the Juice pipeline).
    /// <para>Owner module: Combat.</para>
    /// </summary>
    public static class HitResolver
    {
        public static HitOutcome ResolveHit(DodgeBall ball, DodgeballPlayer victim, Vector3 point, Vector3 normal)
            => throw new NotImplementedException();

        /// <summary>Ability damage without a ball (turret, shockwave). Publishes PlayerDamagedEvent via PlayerHealth.</summary>
        public static HitOutcome ResolveAbilityHit(DodgeballPlayer attacker, DodgeballPlayer victim, float damage, Vector3 point, Vector3 knockback)
            => throw new NotImplementedException();
    }
}
