using System;
using DodgeballUltra.Core;
using NUnit.Framework;

namespace DodgeballUltra.Tests.Core
{
    public class BallisticsTests
    {
        private const float G = GameConstants.Gravity;

        /// <summary>Simulates the solved launch and checks the projectile passes through the target.</summary>
        [TestCase(20f, 10f, 0f)]
        [TestCase(25f, 15f, -0.4f)]
        [TestCase(30f, 8f, 0.6f)]
        [TestCase(55f, 17f, 0.2f)]
        public void LowArcSolution_HitsTarget(float speed, float distance, float height)
        {
            Assert.IsTrue(Ballistics.SolveLaunchAngle(speed, distance, height, G, true, out float angle));
            float t = Ballistics.FlightTime(speed, angle, distance);
            float y = speed * (float)Math.Sin(angle) * t - 0.5f * G * t * t;
            Assert.AreEqual(height, y, 1e-2f);
        }

        [Test]
        public void LowArc_IsFlatterThanHighArc()
        {
            Ballistics.SolveLaunchAngle(20f, 12f, 0f, G, true, out float low);
            Ballistics.SolveLaunchAngle(20f, 12f, 0f, G, false, out float high);
            Assert.Less(low, high);
        }

        [Test]
        public void OutOfRange_ReturnsFalseWith45DegreeFallback()
        {
            Assert.IsFalse(Ballistics.SolveLaunchAngle(5f, 50f, 0f, G, true, out float angle));
            Assert.AreEqual(Math.PI / 4, angle, 1e-5);
        }

        [Test]
        public void ZeroGravity_IsStraightLine()
        {
            Assert.IsTrue(Ballistics.SolveLaunchAngle(10f, 10f, 10f, 0f, true, out float angle));
            Assert.AreEqual(Math.PI / 4, angle, 1e-5);
        }

        [Test]
        public void MaxRange_MatchesClosedForm()
        {
            Assert.AreEqual(400f / G, Ballistics.MaxRange(20f), 1e-3f);
        }

        [Test]
        public void SolveQuadraticTime_FindsFallTime()
        {
            // Dropped from 4.905 m: reaches the floor after 1 s under 9.81 m/s^2.
            Assert.IsTrue(Ballistics.SolveQuadraticTime(-G, 0f, 4.905f, out float t));
            Assert.AreEqual(1f, t, 1e-3f);
        }
    }
}
