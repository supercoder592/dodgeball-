using System;

namespace DodgeballUltra.Core
{
    /// <summary>
    /// Rally Boost System.
    /// <code>
    ///   Velocity_current = Velocity_base * (1.0 + 0.10 * RallyCount)
    /// </code>
    /// capped at 220 km/h. The rally count resets to zero as soon as the ball touches the floor.
    /// </summary>
    public static class RallyMath
    {
        /// <summary>Multiplier contributed by the rally count alone: 1 + 0.1 * rally.</summary>
        public static float RallyMultiplier(int rallyCount, float boostPerCount = GameConstants.RallyBoostPerCount)
        {
            if (rallyCount < 0) rallyCount = 0;
            return 1f + boostPerCount * rallyCount;
        }

        /// <summary>
        /// Applies the rally formula to <paramref name="baseSpeed"/> (m/s) and clamps to <paramref name="capSpeed"/> (m/s).
        /// </summary>
        public static float ComputeSpeed(float baseSpeed, int rallyCount, float capSpeed = GameConstants.MaxBallSpeedMs,
            float boostPerCount = GameConstants.RallyBoostPerCount)
        {
            if (baseSpeed <= 0f) return 0f;
            float v = baseSpeed * RallyMultiplier(rallyCount, boostPerCount);
            return Math.Min(v, capSpeed);
        }

        /// <summary>Clamps any speed (m/s) to the global 220 km/h cap.</summary>
        public static float ClampToCap(float speed, float capSpeed = GameConstants.MaxBallSpeedMs)
        {
            if (speed < 0f) return 0f;
            return Math.Min(speed, capSpeed);
        }

        /// <summary>Next rally count after a successful catch-and-rethrow (ball never touched the floor).</summary>
        public static int NextRallyCount(int current) => current < 0 ? 1 : current + 1;
    }
}
