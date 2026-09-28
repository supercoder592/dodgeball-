using System.Collections.Generic;
using DodgeballUltra.Combat;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Allocation-free threat queries shared by Specter's evasion kit (Danger Sense, Precognition Dodge, Time Reversal AI).
    /// <para>
    /// All queries run on the main thread and reuse one static buffer, so they are safe to call every frame from ability
    /// ticks and AI utility evaluation without generating garbage.
    /// </para>
    /// </summary>
    public static class SpecterThreatUtil
    {
        /// <summary>Reused result buffer for <see cref="BallManager.GetIncomingLiveBalls"/>.</summary>
        private static readonly List<DodgeBall> s_incoming = new List<DodgeBall>(8);

        /// <summary>
        /// Closest-approach distance (m) below which a ball that the predictor says will miss is still treated as a threat
        /// when choosing a dodge direction (a near-miss is still worth side-stepping away from).
        /// </summary>
        public const float NearMissRadius = 2.5f;

        /// <summary>
        /// Finds the enemy live ball that endangers <paramref name="player"/> the most: the one predicted to hit soonest
        /// (gravity-aware <see cref="TrajectoryPredictor.PredictImpact"/>). When no ball is predicted to connect, the
        /// approaching ball with the closest straight-line near-miss (within <see cref="NearMissRadius"/>) is returned
        /// instead so evasive movement still has a direction to flee from.
        /// </summary>
        /// <param name="player">The potential victim.</param>
        /// <param name="horizon">Prediction horizon in seconds.</param>
        /// <param name="timeToImpact">Predicted seconds until impact / closest approach (+inf when none).</param>
        /// <param name="predictedHit">True when the returned ball is predicted to actually hit the body.</param>
        public static DodgeBall FindMostThreateningBall(DodgeballPlayer player, float horizon, out float timeToImpact, out bool predictedHit)
        {
            timeToImpact = float.PositiveInfinity;
            predictedHit = false;

            var balls = BallManager.Instance;
            if (player == null || balls == null) return null;

            // The contract does not promise that GetIncomingLiveBalls clears the list first: never inherit stale entries.
            s_incoming.Clear();
            balls.GetIncomingLiveBalls(player, s_incoming);

            DodgeBall best = null;
            DodgeBall nearMiss = null;
            float nearMissTime = float.PositiveInfinity;
            float nearMissDistance = float.PositiveInfinity;
            Vector3 chest = player.ChestPosition;

            for (int i = 0; i < s_incoming.Count; i++)
            {
                var ball = s_incoming[i];
                if (ball == null || !ball.IsLive) continue;

                if (TrajectoryPredictor.PredictImpact(ball, player, horizon, out float t, out _))
                {
                    if (t < timeToImpact)
                    {
                        timeToImpact = t;
                        best = ball;
                    }
                    continue;
                }

                // Not predicted to connect: remember the closest near-miss that is still approaching.
                if (ClosestApproach(ball.transform.position, ball.Velocity, chest, out float tc, out float dc) &&
                    tc <= horizon && dc <= NearMissRadius && dc < nearMissDistance)
                {
                    nearMissDistance = dc;
                    nearMissTime = tc;
                    nearMiss = ball;
                }
            }

            s_incoming.Clear();

            if (best != null)
            {
                predictedHit = true;
                return best;
            }

            timeToImpact = nearMissTime;
            return nearMiss;
        }

        /// <summary>
        /// Straight-line closest approach of a ball moving with <paramref name="velocity"/> to <paramref name="point"/>.
        /// Returns false when the ball is (nearly) static or already moving away from the point.
        /// </summary>
        public static bool ClosestApproach(Vector3 position, Vector3 velocity, Vector3 point, out float time, out float distance)
        {
            Vector3 rel = point - position;
            float v2 = velocity.sqrMagnitude;
            if (v2 < 1e-4f)
            {
                time = 0f;
                distance = rel.magnitude;
                return false;
            }

            time = Vector3.Dot(rel, velocity) / v2;
            if (time < 0f)
            {
                distance = rel.magnitude;
                return false;
            }

            distance = (position + velocity * time - point).magnitude;
            return true;
        }

        /// <summary>
        /// Planar direction that moves <paramref name="player"/> sideways out of the flight line of <paramref name="threat"/>.
        /// Picks the side the player is already offset to (so the dodge never crosses the ball's path) unless that side
        /// has almost no room left inside the player's court confinement, in which case the roomier side wins.
        /// </summary>
        /// <param name="player">The dodging player.</param>
        /// <param name="threat">The ball to evade (may be null: falls back to a sidestep relative to the facing).</param>
        /// <param name="roomProbe">How far (m) ahead the court confinement is checked.</param>
        public static Vector3 ComputeEvadeDirection(DodgeballPlayer player, DodgeBall threat, float roomProbe)
        {
            if (player == null) return Vector3.right;

            Vector3 travel;
            Vector3 offset;
            if (threat != null)
            {
                travel = threat.Velocity;
                travel.y = 0f;
                offset = player.Position - threat.transform.position;
                offset.y = 0f;
                if (travel.sqrMagnitude < 0.01f) travel = -offset; // (almost) static ball: treat it as coming straight at us
            }
            else
            {
                // No ball: sidestep relative to where the player is facing.
                travel = player.Forward;
                offset = Vector3.zero;
            }

            if (travel.sqrMagnitude < 1e-4f) travel = player.Forward;
            travel.Normalize();

            // Right-hand perpendicular of the ball's travel on the court plane.
            Vector3 perpendicular = Vector3.Cross(Vector3.up, travel).normalized;
            float side = Vector3.Dot(offset, perpendicular);

            Vector3 primary = side >= 0f ? perpendicular : -perpendicular;
            Vector3 secondary = -primary;

            float roomPrimary = RoomAlong(player, primary, roomProbe);
            float roomSecondary = RoomAlong(player, secondary, roomProbe);

            // Crossing the flight line is only worth it when we are almost on it AND the other side is much roomier,
            // or when the preferred side is walled in by the court line.
            bool nearlyOnLine = Mathf.Abs(side) < 0.35f;
            bool walledIn = roomPrimary < roomProbe * 0.3f;
            if ((walledIn || nearlyOnLine) && roomSecondary > roomPrimary + 0.25f) return secondary;
            return primary;
        }

        /// <summary>
        /// Distance (m, capped at <paramref name="maxDistance"/>) the player can travel along planar <paramref name="direction"/>
        /// before leaving their court confinement (team half or outfield strip). Returns the cap when no court exists.
        /// </summary>
        public static float RoomAlong(DodgeballPlayer player, Vector3 direction, float maxDistance)
        {
            var court = Court.Instance;
            if (court == null || player == null) return maxDistance;

            Bounds b = court.GetConfinement(player.Team, player.Zone);
            Vector3 p = player.Position;
            float t = maxDistance;

            if (direction.x > 1e-4f) t = Mathf.Min(t, (b.max.x - p.x) / direction.x);
            else if (direction.x < -1e-4f) t = Mathf.Min(t, (b.min.x - p.x) / direction.x);

            if (direction.z > 1e-4f) t = Mathf.Min(t, (b.max.z - p.z) / direction.z);
            else if (direction.z < -1e-4f) t = Mathf.Min(t, (b.min.z - p.z) / direction.z);

            return Mathf.Max(0f, t);
        }

        /// <summary>Theme colour of <paramref name="player"/>'s hero (fallback: Specter's pale silver).</summary>
        public static Color ThemeColor(DodgeballPlayer player)
        {
            if (player != null && player.Character != null) return player.Character.themeColor;
            return new Color(0.82f, 0.86f, 0.95f);
        }
    }
}
