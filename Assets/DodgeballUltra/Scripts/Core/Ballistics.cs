using System;

namespace DodgeballUltra.Core
{
    /// <summary>
    /// Scalar ballistic helpers used by the throw solver. The Unity layer projects the 3D problem onto
    /// the vertical plane that contains the launch point and the target (horizontal distance + height delta)
    /// and then rebuilds the 3D velocity from the returned angle.
    /// </summary>
    public static class Ballistics
    {
        /// <summary>
        /// Solves the launch elevation angle that makes a projectile with fixed <paramref name="speed"/> pass through
        /// a point <paramref name="horizontalDistance"/> away and <paramref name="heightDelta"/> above the launch point.
        /// <code>tan(theta) = (v^2 -/+ sqrt(v^4 - g (g x^2 + 2 y v^2))) / (g x)</code>
        /// </summary>
        /// <param name="speed">Launch speed (m/s), &gt; 0.</param>
        /// <param name="horizontalDistance">Horizontal distance to the target (m), &gt;= 0.</param>
        /// <param name="heightDelta">Target height minus launch height (m).</param>
        /// <param name="gravity">Gravity magnitude (m/s^2, positive). Zero means a straight line.</param>
        /// <param name="preferLowArc">True for the flat, fast solution (what a real fastball uses).</param>
        /// <param name="angleRadians">Elevation angle in radians (positive = upwards).</param>
        /// <returns>False when the target is out of range at this speed (angleRadians is then the 45-degree max-range fallback).</returns>
        public static bool SolveLaunchAngle(float speed, float horizontalDistance, float heightDelta, float gravity,
            bool preferLowArc, out float angleRadians)
        {
            if (speed <= 0f)
            {
                angleRadians = 0f;
                return false;
            }

            if (horizontalDistance < 1e-4f)
            {
                // Straight up / straight down.
                angleRadians = heightDelta >= 0f ? (float)(Math.PI * 0.5) : (float)(-Math.PI * 0.5);
                return true;
            }

            if (gravity <= 1e-6f)
            {
                angleRadians = (float)Math.Atan2(heightDelta, horizontalDistance);
                return true;
            }

            double v2 = (double)speed * speed;
            double v4 = v2 * v2;
            double x = horizontalDistance;
            double y = heightDelta;
            double g = gravity;
            double discriminant = v4 - g * (g * x * x + 2.0 * y * v2);
            if (discriminant < 0.0)
            {
                angleRadians = (float)(Math.PI * 0.25);
                return false;
            }

            double root = Math.Sqrt(discriminant);
            double tanTheta = preferLowArc ? (v2 - root) / (g * x) : (v2 + root) / (g * x);
            angleRadians = (float)Math.Atan(tanTheta);
            return true;
        }

        /// <summary>Time of flight (s) to cover <paramref name="horizontalDistance"/> when launched at <paramref name="angleRadians"/>.</summary>
        public static float FlightTime(float speed, float angleRadians, float horizontalDistance)
        {
            double horizontalSpeed = speed * Math.Cos(angleRadians);
            if (horizontalSpeed < 1e-5) return float.PositiveInfinity;
            return (float)(horizontalDistance / horizontalSpeed);
        }

        /// <summary>Maximum horizontal range on flat ground for a given launch speed.</summary>
        public static float MaxRange(float speed, float gravity = GameConstants.Gravity)
        {
            if (gravity <= 1e-6f) return float.PositiveInfinity;
            return speed * speed / gravity;
        }

        /// <summary>
        /// Solves t for 0.5*a*t^2 + v*t + p = 0 (smallest non-negative root). Used for "when does this ball reach height h".
        /// </summary>
        /// <returns>False when no non-negative root exists.</returns>
        public static bool SolveQuadraticTime(float a, float v, float p, out float time)
        {
            time = 0f;
            if (Math.Abs(a) < 1e-7f)
            {
                if (Math.Abs(v) < 1e-7f) return false;
                float t = -p / v;
                if (t < 0f) return false;
                time = t;
                return true;
            }

            double A = 0.5 * a, B = v, C = p;
            double disc = B * B - 4.0 * A * C;
            if (disc < 0.0) return false;
            double sq = Math.Sqrt(disc);
            double t1 = (-B - sq) / (2.0 * A);
            double t2 = (-B + sq) / (2.0 * A);
            double lo = Math.Min(t1, t2), hi = Math.Max(t1, t2);
            if (lo >= 0.0) { time = (float)lo; return true; }
            if (hi >= 0.0) { time = (float)hi; return true; }
            return false;
        }
    }
}
