using DodgeballUltra.Core;
using NUnit.Framework;

namespace DodgeballUltra.Tests.Core
{
    public class CooldownAndMeterTests
    {
        [Test]
        public void Cooldown_TicksToReadyExactlyOnce()
        {
            var cd = new Cooldown(1f);
            Assert.IsTrue(cd.IsReady);
            cd.Start();
            Assert.IsFalse(cd.IsReady);
            Assert.IsFalse(cd.Tick(0.6f));
            Assert.IsTrue(cd.Tick(0.6f));
            Assert.IsTrue(cd.IsReady);
            Assert.IsFalse(cd.Tick(0.6f));
        }

        [Test]
        public void Cooldown_NormalizedAndReduce()
        {
            var cd = new Cooldown(10f);
            cd.Start();
            cd.Tick(2.5f);
            Assert.AreEqual(0.75f, cd.NormalizedRemaining, 1e-5f);
            cd.Reduce(100f);
            Assert.IsTrue(cd.IsReady);
        }

        [Test]
        public void Meter_ClampsAndRaisesFilledOnce()
        {
            var meter = new ResourceMeter(1f);
            int filled = 0;
            meter.Filled += () => filled++;
            meter.Add(0.5f);
            meter.Add(0.7f);
            meter.Add(0.3f);
            Assert.AreEqual(1f, meter.Value, 1e-6f);
            Assert.AreEqual(1, filled);
            Assert.IsTrue(meter.TryConsume(1f));
            Assert.AreEqual(0f, meter.Value, 1e-6f);
            meter.Add(1f);
            Assert.AreEqual(2, filled);
        }

        [Test]
        public void Meter_PerfectCatchGain_Is15Percent()
        {
            var meter = new ResourceMeter(1f);
            meter.Add(GameConstants.PerfectCatchUltGain);
            Assert.AreEqual(0.15f, meter.Normalized, 1e-6f);
        }

        [Test]
        public void Meter_TryConsume_FailsWhenInsufficient()
        {
            var meter = new ResourceMeter(1f, 0.4f);
            Assert.IsFalse(meter.TryConsume(0.5f));
            Assert.AreEqual(0.4f, meter.Value, 1e-6f);
        }
    }
}
