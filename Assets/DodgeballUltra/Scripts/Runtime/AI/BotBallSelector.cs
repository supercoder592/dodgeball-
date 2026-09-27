using DodgeballUltra.Combat;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.AI
{
    /// <summary>
    /// Chooses which loose ball a bot should run for. Only Free and Stasis match balls that can be reached from inside the
    /// bot's confinement (own half, or its outfield strip) are considered. Candidates are ranked by estimated time to reach,
    /// with rolling-ball prediction, a priority bonus for Chrono's Stasis balls, a penalty for balls an enemy will reach
    /// first (centre-line contests) and team coordination: a ball already claimed by a closer bot teammate - or about to be
    /// grabbed by a much closer human teammate - is left to them.
    /// </summary>
    public sealed class BotBallSelector
    {
        /// <summary>
        /// Best ball to retrieve, or null.
        /// </summary>
        /// <param name="self">The deciding player.</param>
        /// <param name="confinement">The bot's movement confinement (already shrunk by the body radius).</param>
        /// <param name="reach">How far outside the confinement a ball may be and still be picked up (m).</param>
        /// <param name="stasisReachHeight">Maximum height above the feet at which a Stasis ball can be snatched (m).</param>
        /// <param name="runSpeed">Expected running speed (m/s) used for time estimates.</param>
        /// <param name="current">Ball currently being chased (gets the hysteresis bonus).</param>
        /// <param name="ignored">Ball to skip (given up on after a failed chase), or null.</param>
        /// <param name="hysteresisSeconds">Time bonus (s) of the current ball.</param>
        /// <param name="pickupPoint">Where to run to (inside the confinement).</param>
        /// <param name="timeToReach">Estimated seconds to get there.</param>
        public DodgeBall SelectBall(DodgeballPlayer self, Bounds confinement, float reach, float stasisReachHeight, float runSpeed,
            DodgeBall current, DodgeBall ignored, float hysteresisSeconds, out Vector3 pickupPoint, out float timeToReach)
        {
            pickupPoint = self.Position;
            timeToReach = float.PositiveInfinity;

            var manager = BallManager.Instance;
            if (manager == null) return null;
            var balls = manager.MatchBalls;
            if (balls == null) return null;

            runSpeed = Mathf.Max(0.5f, runSpeed);
            DodgeBall best = null;
            float bestScore = float.PositiveInfinity;

            for (int i = 0; i < balls.Count; i++)
            {
                var ball = balls[i];
                if (ball == null || ball.IsAbilityBall || ball == ignored) continue;
                var state = ball.State;
                if (state != BallState.Free && state != BallState.Stasis) continue;
                if (!ball.CanBePickedUpBy(self)) continue; // fading, pooled, awaiting an ability launch...

                var pos = ball.transform.position;
                if (state == BallState.Stasis && pos.y - self.Position.y > stasisReachHeight) continue;

                // Predict where a rolling ball will be when we get there (capped look-ahead; floor friction ignored).
                var predicted = pos;
                if (state == BallState.Free && ball.Body != null && !ball.Body.isKinematic)
                {
                    var v = BotWorld.Planar(ball.Body.GetVelocity());
                    float guess = BotWorld.PlanarDistance(self.Position, pos) / runSpeed;
                    predicted = pos + v * Mathf.Min(guess, 1f);
                }

                if (BotWorld.PlanarDistanceToBounds(predicted, confinement) > reach)
                {
                    if (BotWorld.PlanarDistanceToBounds(pos, confinement) > reach) continue; // out of our reach entirely
                    predicted = pos;
                }

                var stand = BotWorld.ClampPlanar(predicted, confinement);
                stand.y = self.Position.y;
                float dist = BotWorld.PlanarDistance(self.Position, stand);
                float t = dist / runSpeed;

                if (IsLeftToTeammate(self, ball, pos, dist)) continue;

                // Centre-line contest: an enemy who gets there first makes the run pointless (and dangerous).
                float enemyTime = NearestEnemyTime(self, pos, reach, runSpeed);
                if (enemyTime < t) t += 0.5f + (t - enemyTime) * 0.75f;

                if (state == BallState.Stasis) t -= 0.6f; // frozen enemy ball: snatch it before it resumes
                if (ball == current) t -= hysteresisSeconds;

                if (t < bestScore)
                {
                    bestScore = t;
                    best = ball;
                    pickupPoint = stand;
                    timeToReach = dist / runSpeed;
                }
            }
            return best;
        }

        /// <summary>True if a teammate is clearly better placed to take <paramref name="ball"/>.</summary>
        private static bool IsLeftToTeammate(DodgeballPlayer self, DodgeBall ball, Vector3 ballPos, float myDistance)
        {
            // Bot teammates that already claimed the ball and are at least as close keep it.
            var brains = BotBrain.ActiveBrains;
            for (int i = 0; i < brains.Count; i++)
            {
                var other = brains[i];
                if (other == null || other.ClaimedBall != ball) continue;
                var mate = other.Owner;
                if (!PlayerRegistry.AreTeammates(self, mate) || BotWorld.HoldsBall(mate)) continue;
                if (BotWorld.PlanarDistance(mate.Position, ballPos) <= myDistance + 0.5f) return true;
            }

            // Human teammates cannot announce claims: assume they take a ball they are much closer to.
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var mate = all[i];
                if (!PlayerRegistry.AreTeammates(self, mate) || !mate.IsHumanControlled || BotWorld.HoldsBall(mate)) continue;
                float d = BotWorld.PlanarDistance(mate.Position, ballPos);
                if (d < 3f && d < myDistance * 0.6f) return true;
            }
            return false;
        }

        /// <summary>Seconds the fastest enemy able to reach <paramref name="ballPos"/> from its own confinement needs to get there.</summary>
        private static float NearestEnemyTime(DodgeballPlayer self, Vector3 ballPos, float reach, float runSpeed)
        {
            float best = float.PositiveInfinity;
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var enemy = all[i];
                if (!PlayerRegistry.AreEnemies(self, enemy) || !enemy.IsInitialized || BotWorld.HoldsBall(enemy)) continue;
                if (enemy.Health != null && !enemy.Health.IsAlive) continue;
                var bounds = BotWorld.GetConfinement(enemy);
                if (BotWorld.PlanarDistanceToBounds(ballPos, bounds) > reach) continue;
                float t = BotWorld.PlanarDistance(enemy.Position, BotWorld.ClampPlanar(ballPos, bounds)) / runSpeed;
                if (t < best) best = t;
            }
            return best;
        }
    }
}
