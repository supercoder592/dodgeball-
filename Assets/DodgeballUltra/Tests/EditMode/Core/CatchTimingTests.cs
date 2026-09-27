using DodgeballUltra.Core;
using NUnit.Framework;

namespace DodgeballUltra.Tests.Core
{
    /// <summary>Perfect Catch &lt;=&gt; 0 &lt;= t_input &lt;= 0.15 s before impact.</summary>
    public class CatchTimingTests
    {
        [TestCase(0f)]
        [TestCase(0.05f)]
        [TestCase(0.15f)]
        public void WithinPerfectWindow_IsPerfect(float secondsBeforeImpact)
        {
            Assert.AreEqual(CatchQuality.Perfect, CatchTiming.Classify(secondsBeforeImpact));
        }

        [TestCase(0.151f)]
        [TestCase(0.3f)]
        [TestCase(0.4f)]
        public void BetweenPerfectAndCatchWindow_IsNormal(float secondsBeforeImpact)
        {
            Assert.AreEqual(CatchQuality.Normal, CatchTiming.Classify(secondsBeforeImpact));
        }

        [Test]
        public void PressedAfterImpact_IsMiss()
        {
            Assert.AreEqual(CatchQuality.Miss, CatchTiming.Classify(-0.01f));
        }

        [Test]
        public void TooEarly_IsMiss()
        {
            Assert.AreEqual(CatchQuality.Miss, CatchTiming.Classify(0.41f));
        }

        [Test]
        public void IronMitts_ExtendsPerfectWindowTo225ms()
        {
            float window = CatchTiming.ScaledPerfectWindow(GameConstants.IronMittsWindowMultiplier);
            Assert.AreEqual(0.225f, window, 1e-5f);
            Assert.AreEqual(CatchQuality.Perfect, CatchTiming.Classify(0.2f, window));
            Assert.AreEqual(CatchQuality.Normal, CatchTiming.Classify(0.2f));
        }

        [Test]
        public void EffectiveCatchWindow_NeverShorterThanPerfectWindow()
        {
            Assert.AreEqual(0.5f, CatchTiming.EffectiveCatchWindow(0.5f, 0.4f), 1e-6f);
            Assert.AreEqual(0.4f, CatchTiming.EffectiveCatchWindow(0.15f, 0.4f), 1e-6f);
        }
    }
}
