using DodgeballUltra.Core;
using NUnit.Framework;

namespace DodgeballUltra.Tests.Core
{
    /// <summary>Rally Boost: V = V_base * (1 + 0.10 * RallyCount), capped at 220 km/h.</summary>
    public class RallyMathTests
    {
        private const float Eps = 1e-4f;

        [Test]
        public void NoRally_ReturnsBaseSpeed()
        {
            Assert.AreEqual(20f, RallyMath.ComputeSpeed(20f, 0), Eps);
        }

        [TestCase(1, 1.1f)]
        [TestCase(3, 1.3f)]
        [TestCase(10, 2.0f)]
        public void RallyMultiplier_IsTenPercentPerRally(int rally, float expected)
        {
            Assert.AreEqual(expected, RallyMath.RallyMultiplier(rally), Eps);
        }

        [Test]
        public void Speed_IsCappedAt220Kmh()
        {
            float v = RallyMath.ComputeSpeed(50f, 20); // 50 * 3 = 150 m/s, far above the cap
            Assert.AreEqual(GameConstants.MaxBallSpeedMs, v, Eps);
            Assert.AreEqual(220f, v * GameConstants.MsToKmh, 1e-2f);
        }

        [Test]
        public void NegativeRally_IsTreatedAsZero()
        {
            Assert.AreEqual(1f, RallyMath.RallyMultiplier(-5), Eps);
        }

        [Test]
        public void NextRallyCount_Increments()
        {
            Assert.AreEqual(1, RallyMath.NextRallyCount(0));
            Assert.AreEqual(4, RallyMath.NextRallyCount(3));
            Assert.AreEqual(1, RallyMath.NextRallyCount(-2));
        }

        [Test]
        public void ZeroOrNegativeBase_IsZero()
        {
            Assert.AreEqual(0f, RallyMath.ComputeSpeed(0f, 5), Eps);
            Assert.AreEqual(0f, RallyMath.ComputeSpeed(-3f, 5), Eps);
        }
    }
}
