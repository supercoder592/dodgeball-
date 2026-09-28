using System;
using UnityEngine;
using static DodgeballUltra.Audio.SfxDsp;

namespace DodgeballUltra.Audio
{
    /// <summary>
    /// Synthesises a realistic-leaning placeholder for every <see cref="SfxId"/> (44.1 kHz mono) so the game is never
    /// silent before real recordings are dropped into an <see cref="AudioLibrary"/>.
    /// <para>
    /// Recipes are modelled on the physics of the real sound rather than on chiptune tones: a foam ball hitting a body is
    /// a low, pitch-dropping thump plus a band-limited slap of rubber on cloth; a floor bounce combines the ball's
    /// cavity "pong" with the hollow resonance of a sprung hardwood floor; everything is placed in a small sports-hall
    /// reverb. Each id has several variants (deterministic seeds) so repeated sounds do not machine-gun.
    /// </para>
    /// Pure C# math (no Unity calls) except <see cref="CreateClip"/>, so it can also run in tests.
    /// </summary>
    public static class ProceduralSfx
    {
        public const int DefaultSampleRate = 44100;

        /// <summary>Number of distinct variants synthesised for <paramref name="id"/>.</summary>
        public static int VariantCount(SfxId id)
        {
            switch (id)
            {
                case SfxId.Footstep: return 6;         // 0-3 hardwood taps, 4-5 sneaker squeaks
                case SfxId.BallHitPlayer:
                case SfxId.BallBounceFloor: return 4;
                case SfxId.BallHitHeavy:
                case SfxId.BallBounceWall:
                case SfxId.Catch:
                case SfxId.Throw: return 3;
                case SfxId.Whistle: return 2;          // 0 long blast, 1 double blast
                case SfxId.PerfectCatch:
                case SfxId.CatchWhiff:
                case SfxId.ThrowHeavy:
                case SfxId.Pickup:
                case SfxId.Pass:
                case SfxId.Jump:
                case SfxId.Land:
                case SfxId.Slide:
                case SfxId.Elimination:
                case SfxId.AbilityCast:
                case SfxId.Shockwave:
                case SfxId.Freeze:
                case SfxId.Teleport:
                case SfxId.Glue:
                case SfxId.Turret:
                case SfxId.Clone:
                case SfxId.CrowdCheer:
                case SfxId.CrowdGasp: return 2;
                default: return 1;
            }
        }

        /// <summary>How many of the first variants are picked at random when no explicit variant is requested.</summary>
        public static int RandomVariantCount(SfxId id)
        {
            switch (id)
            {
                case SfxId.Footstep: return 4;  // squeaks are requested explicitly (sharp turns)
                case SfxId.Whistle: return 1;   // double blast is requested explicitly (round end)
                default: return VariantCount(id);
            }
        }

        /// <summary>Ids with a dedicated seamless loop (<see cref="SynthesizeLoop"/>).</summary>
        public static bool SupportsLoop(SfxId id) =>
            id == SfxId.Shield || id == SfxId.Magnet || id == SfxId.Stasis || id == SfxId.CrowdAmbience;

        /// <summary>The regular clip of this id is itself a seamless loop.</summary>
        public static bool IsLoopByDefault(SfxId id) => id == SfxId.CrowdAmbience;

        /// <summary>Creates a Unity clip from samples.</summary>
        public static AudioClip CreateClip(string name, float[] samples, int sampleRate = DefaultSampleRate)
        {
            if (samples == null || samples.Length == 0) return null;
            var clip = AudioClip.Create(name, samples.Length, 1, sampleRate, false);
            clip.SetData(samples, 0);
            clip.hideFlags = HideFlags.DontSave;
            return clip;
        }

        /// <summary>Synthesises variant <paramref name="variant"/> of <paramref name="id"/>.</summary>
        public static float[] Synthesize(SfxId id, int variant, int sampleRate = DefaultSampleRate)
        {
            int sr = Math.Max(8000, sampleRate);
            int v = Math.Max(0, variant) % Math.Max(1, VariantCount(id));
            var r = new SfxRng((uint)((int)id * 7919 + v * 104729 + 1013));
            switch (id)
            {
                case SfxId.Throw: return Throw(sr, ref r);
                case SfxId.ThrowHeavy: return ThrowHeavy(sr, ref r);
                case SfxId.BallHitPlayer: return BallHitPlayer(sr, ref r);
                case SfxId.BallHitHeavy: return BallHitHeavy(sr, ref r);
                case SfxId.BallBounceFloor: return BounceFloor(sr, ref r);
                case SfxId.BallBounceWall: return BounceWall(sr, ref r);
                case SfxId.Catch: return Catch(sr, ref r);
                case SfxId.PerfectCatch: return PerfectCatch(sr, ref r);
                case SfxId.CatchWhiff: return CatchWhiff(sr, ref r);
                case SfxId.Pickup: return Pickup(sr, ref r);
                case SfxId.Pass: return Pass(sr, ref r);
                case SfxId.Footstep: return v < 4 ? FootstepTap(sr, ref r) : FootstepSqueak(sr, ref r);
                case SfxId.Jump: return Jump(sr, ref r);
                case SfxId.Land: return Land(sr, ref r);
                case SfxId.Slide: return Slide(sr, ref r);
                case SfxId.Whistle: return Whistle(sr, ref r, v == 1);
                case SfxId.Countdown: return Beep(sr, 880f, 0.12f, 0.8f);
                case SfxId.RoundStart: return RoundStart(sr);
                case SfxId.Elimination: return Elimination(sr, ref r);
                case SfxId.Revive: return Revive(sr, ref r);
                case SfxId.AbilityCast: return AbilityCast(sr, ref r);
                case SfxId.UltimateCast: return UltimateCast(sr, ref r);
                case SfxId.UltimateReady: return UltimateReady(sr, ref r);
                case SfxId.Shockwave: return Shockwave(sr, ref r);
                case SfxId.Beam: return Beam(sr, ref r);
                case SfxId.Freeze: return Freeze(sr, ref r);
                case SfxId.Teleport: return Teleport(sr, ref r);
                case SfxId.Glue: return Glue(sr, ref r);
                case SfxId.Turret: return Turret(sr, ref r);
                case SfxId.Shield: return ShieldHum(sr, ref r, false);
                case SfxId.Magnet: return MagnetHum(sr, ref r, false);
                case SfxId.Stasis: return StasisTone(sr, ref r, false);
                case SfxId.Rewind: return Rewind(sr, ref r);
                case SfxId.Cloak: return Cloak(sr, ref r);
                case SfxId.Clone: return Clone(sr, ref r);
                case SfxId.Earthquake: return Earthquake(sr, ref r);
                case SfxId.CrowdCheer: return CrowdCheer(sr, ref r, v == 1);
                case SfxId.CrowdGasp: return CrowdGasp(sr, ref r);
                case SfxId.CrowdAmbience: return CrowdAmbience(sr, ref r);
                case SfxId.UiClick: return UiClick(sr, ref r);
                case SfxId.UiConfirm: return UiConfirm(sr);
                default: return BallHitPlayer(sr, ref r);
            }
        }

        /// <summary>Seamless loop for sustained sounds (hums, crowd). Falls back to the regular clip for other ids.</summary>
        public static float[] SynthesizeLoop(SfxId id, int sampleRate = DefaultSampleRate)
        {
            int sr = Math.Max(8000, sampleRate);
            var r = new SfxRng((uint)((int)id * 7919 + 77777));
            switch (id)
            {
                case SfxId.Shield: return ShieldHum(sr, ref r, true);
                case SfxId.Magnet: return MagnetHum(sr, ref r, true);
                case SfxId.Stasis: return StasisTone(sr, ref r, true);
                case SfxId.CrowdAmbience: return CrowdAmbience(sr, ref r);
                default: return Synthesize(id, 0, sr);
            }
        }

        // =====================================================================================================
        // Ball & body
        // =====================================================================================================

        private static float[] BallHitPlayer(int sr, ref SfxRng r)
        {
            var b = Alloc(0.7f, sr);
            float body = r.Jitter(95f, 0.12f);
            Tone(b, sr, 0f, body * 1.5f, body * 0.8f, 0.03f, 0.9f, 0.002f, 0.07f);                         // chest thump, pitch drops
            Tone(b, sr, 0f, r.Jitter(240f, 0.1f), 180f, 0.02f, 0.35f, 0.001f, 0.035f);                     // ball shell resonance
            Noise(b, sr, ref r, 0f, 0.55f, 0.0005f, 0.018f, Filter.BandPass, r.Jitter(1300f, 0.2f), 900f, 0.05f, 0.8f); // rubber on cloth
            Noise(b, sr, ref r, 0f, 0.25f, 0.001f, 0.06f, Filter.LowPass, 600f, 250f, 0.1f);               // air pushed out of the ball
            Saturate(b, 0.6f);
            Room(b, sr, 0.12f, 0.9f);
            return Finish(b, sr);
        }

        private static float[] BallHitHeavy(int sr, ref SfxRng r)
        {
            var b = Alloc(1.1f, sr);
            float f = r.Jitter(70f, 0.1f);
            Tone(b, sr, 0f, f * 1.8f, f * 0.7f, 0.05f, 1f, 0.002f, 0.14f, 0.25f);
            Tone(b, sr, 0f, r.Jitter(200f, 0.1f), 140f, 0.03f, 0.4f, 0.001f, 0.05f);
            Noise(b, sr, ref r, 0f, 0.8f, 0.0005f, 0.025f, Filter.BandPass, r.Jitter(1100f, 0.15f), 700f, 0.06f, 0.7f);
            Noise(b, sr, ref r, 0f, 0.5f, 0.001f, 0.12f, Filter.LowPass, 900f, 150f, 0.2f, 0.7f, true);
            Saturate(b, 1.2f);
            Room(b, sr, 0.18f, 1.2f);
            return Finish(b, sr);
        }

        /// <summary>Ball cavity "pong" + the hollow resonance of a sprung hardwood gym floor + contact click.</summary>
        private static float[] BounceFloor(int sr, ref SfxRng r)
        {
            var b = Alloc(1f, sr);
            float ball = r.Jitter(330f, 0.12f);
            Tone(b, sr, 0f, ball * 1.15f, ball, 0.01f, 0.55f, 0.0008f, 0.035f, 0.2f);
            Tone(b, sr, 0f, r.Jitter(135f, 0.1f), 110f, 0.05f, 0.8f, 0.001f, 0.09f);
            Tone(b, sr, 0f, r.Jitter(410f, 0.1f), 400f, 0f, 0.15f, 0.001f, 0.05f);
            Noise(b, sr, ref r, 0f, 0.35f, 0.0003f, 0.006f, Filter.HighPass, 2500f);
            Noise(b, sr, ref r, 0f, 0.3f, 0.001f, 0.04f, Filter.BandPass, 700f, 500f, 0.05f, 1.2f);
            Room(b, sr, 0.28f, 1.4f, 0.3f);
            return Finish(b, sr);
        }

        private static float[] BounceWall(int sr, ref SfxRng r)
        {
            var b = Alloc(0.9f, sr);
            Tone(b, sr, 0f, r.Jitter(470f, 0.1f), 420f, 0.01f, 0.5f, 0.0008f, 0.03f, 0.15f);
            Tone(b, sr, 0f, r.Jitter(180f, 0.1f), 160f, 0.03f, 0.55f, 0.001f, 0.06f);
            Noise(b, sr, ref r, 0f, 0.35f, 0.0003f, 0.004f, Filter.HighPass, 3000f);
            Noise(b, sr, ref r, 0f, 0.35f, 0.0008f, 0.03f, Filter.BandPass, 1400f, 900f, 0.05f, 1f);
            Room(b, sr, 0.28f, 1.3f, 0.3f);
            return Finish(b, sr);
        }

        /// <summary>Hands slapping onto a foam ball, ball pressed into the chest.</summary>
        private static float[] Catch(int sr, ref SfxRng r)
        {
            var b = Alloc(0.55f, sr);
            Noise(b, sr, ref r, 0f, 0.9f, 0.0003f, 0.014f, Filter.BandPass, r.Jitter(1800f, 0.15f), 1500f, 0.02f, 0.8f);
            Noise(b, sr, ref r, 0f, 0.3f, 0.0002f, 0.006f, Filter.BandPass, 3500f, 3500f, 0.1f, 1f);
            Tone(b, sr, 0.004f, 160f, 120f, 0.02f, 0.6f, 0.001f, 0.05f);
            Tone(b, sr, 0.004f, r.Jitter(280f, 0.1f), 260f, 0f, 0.2f, 0.001f, 0.03f);
            Room(b, sr, 0.1f, 0.8f);
            return Finish(b, sr);
        }

        /// <summary>Bright, crisp "clack" of a perfectly timed catch with a short shimmering tail.</summary>
        private static float[] PerfectCatch(int sr, ref SfxRng r)
        {
            var b = Alloc(1.6f, sr);
            Noise(b, sr, ref r, 0f, 0.8f, 0.0002f, 0.004f, Filter.HighPass, 3000f);
            Noise(b, sr, ref r, 0f, 0.7f, 0.0003f, 0.012f, Filter.BandPass, 2200f, 1800f, 0.03f, 0.9f);
            Tone(b, sr, 0f, 150f, 110f, 0.02f, 0.6f, 0.001f, 0.05f);
            float[] partials = { 1780f, 2690f, 4120f, 5230f };
            float[] amps = { 0.35f, 0.28f, 0.2f, 0.14f };
            float[] decays = { 0.18f, 0.14f, 0.1f, 0.08f };
            for (int i = 0; i < partials.Length; i++)
                Tone(b, sr, 0.001f, r.Jitter(partials[i], 0.03f), 0f, 0f, amps[i], 0.0005f, decays[i]);
            for (int i = 0; i < 6; i++)
            {
                // Pairs of nearly identical frequencies beat against each other -> shimmer.
                float f = r.Range(3000f, 7000f);
                float start = 0.02f + i * 0.015f;
                Tone(b, sr, start, f, 0f, 0f, 0.05f, 0.06f, 0.35f);
                Tone(b, sr, start, f * 1.004f, 0f, 0f, 0.04f, 0.06f, 0.35f);
            }
            Room(b, sr, 0.22f, 1.3f);
            return Finish(b, sr);
        }

        private static float[] CatchWhiff(int sr, ref SfxRng r)
        {
            var b = Alloc(0.4f, sr);
            Noise(b, sr, ref r, 0f, 0.6f, 0.06f, 0.06f, Filter.BandPass, r.Jitter(600f, 0.1f), 1600f, 0.15f, 1.5f, true);
            Room(b, sr, 0.08f, 0.8f);
            return Finish(b, sr, 0.8f, 0.004f);
        }

        /// <summary>Arm whoosh: pink noise through a band-pass sweeping up as the arm accelerates.</summary>
        private static float[] Throw(int sr, ref SfxRng r)
        {
            var b = Alloc(0.55f, sr);
            Noise(b, sr, ref r, 0f, 0.9f, 0.05f, 0.08f, Filter.BandPass, r.Jitter(450f, 0.1f), r.Jitter(1900f, 0.1f), 0.12f, 1.4f, true);
            Noise(b, sr, ref r, 0f, 0.25f, 0.04f, 0.06f, Filter.LowPass, 300f, 300f, 0.1f, 0.7f, true);
            Tone(b, sr, 0.03f, 70f, 55f, 0.05f, 0.15f, 0.02f, 0.05f);
            return Finish(b, sr, 0.85f, 0.004f);
        }

        private static float[] ThrowHeavy(int sr, ref SfxRng r)
        {
            var b = Alloc(0.75f, sr);
            Noise(b, sr, ref r, 0f, 1f, 0.06f, 0.12f, Filter.BandPass, r.Jitter(300f, 0.1f), r.Jitter(1600f, 0.1f), 0.15f, 1.1f, true);
            Noise(b, sr, ref r, 0.05f, 0.3f, 0.04f, 0.05f, Filter.BandPass, 2500f, 4000f, 0.1f, 1f);
            Tone(b, sr, 0.04f, 60f, 45f, 0.05f, 0.35f, 0.04f, 0.1f);
            Saturate(b, 0.3f);
            return Finish(b, sr, 0.9f, 0.004f);
        }

        private static float[] Pickup(int sr, ref SfxRng r)
        {
            var b = Alloc(0.35f, sr);
            Tone(b, sr, 0f, r.Jitter(210f, 0.1f), 170f, 0.02f, 0.6f, 0.001f, 0.04f);
            Noise(b, sr, ref r, 0f, 0.35f, 0.0005f, 0.012f, Filter.BandPass, 900f, 900f, 0.1f, 1f);
            Noise(b, sr, ref r, 0.01f, 0.12f, 0.001f, 0.006f, Filter.BandPass, 2400f, 2400f, 0.1f, 1.5f);
            Room(b, sr, 0.08f, 0.8f);
            return Finish(b, sr, 0.8f);
        }

        private static float[] Pass(int sr, ref SfxRng r)
        {
            var b = Alloc(0.45f, sr);
            Noise(b, sr, ref r, 0f, 0.7f, 0.04f, 0.06f, Filter.BandPass, r.Jitter(700f, 0.1f), 2200f, 0.1f, 1.6f, true);
            Tone(b, sr, 0f, 180f, 150f, 0.02f, 0.15f, 0.001f, 0.03f);
            return Finish(b, sr, 0.8f, 0.004f);
        }

        // =====================================================================================================
        // Locomotion (hardwood court, indoor sneakers)
        // =====================================================================================================

        private static float[] FootstepTap(int sr, ref SfxRng r)
        {
            var b = Alloc(0.3f, sr);
            Noise(b, sr, ref r, 0f, 0.5f, 0.0003f, 0.006f, Filter.BandPass, r.Jitter(3000f, 0.25f), 0f, 0.1f, 0.9f); // sole tap
            Tone(b, sr, 0f, r.Jitter(110f, 0.2f), 85f, 0.02f, 0.7f, 0.001f, 0.025f);                                // hardwood knock
            Tone(b, sr, 0f, r.Jitter(420f, 0.15f), 0f, 0f, 0.18f, 0.001f, 0.018f);                                  // plank ring
            Noise(b, sr, ref r, 0.004f, 0.2f, 0.001f, 0.02f, Filter.LowPass, 500f);                                 // heel thud
            Room(b, sr, 0.12f, 0.9f);
            return Finish(b, sr, 0.85f);
        }

        /// <summary>Rubber sole stick-slip on varnished wood: a gliding tone amplitude-modulated at the slip rate.</summary>
        private static float[] FootstepSqueak(int sr, ref SfxRng r)
        {
            var b = Alloc(0.35f, sr);
            Noise(b, sr, ref r, 0f, 0.25f, 0.0003f, 0.006f, Filter.BandPass, 3000f, 0f, 0.1f, 0.9f);
            Tone(b, sr, 0f, 100f, 85f, 0.02f, 0.35f, 0.001f, 0.025f);
            Squeak(b, sr, ref r, 0.01f, r.Range(0.07f, 0.13f), r.Jitter(2300f, 0.15f), r.Range(0.75f, 0.9f), 0.45f);
            Room(b, sr, 0.15f, 1f);
            return Finish(b, sr, 0.85f);
        }

        private static float[] Jump(int sr, ref SfxRng r)
        {
            var b = Alloc(0.4f, sr);
            Noise(b, sr, ref r, 0f, 0.4f, 0.001f, 0.03f, Filter.BandPass, r.Jitter(1800f, 0.15f), 0f, 0.1f, 0.8f);
            Tone(b, sr, 0f, 95f, 75f, 0.02f, 0.5f, 0.001f, 0.03f);
            Noise(b, sr, ref r, 0.02f, 0.3f, 0.03f, 0.06f, Filter.BandPass, 500f, 1200f, 0.1f, 1.2f, true);
            Room(b, sr, 0.1f, 0.9f);
            return Finish(b, sr, 0.8f);
        }

        private static float[] Land(int sr, ref SfxRng r)
        {
            var b = Alloc(0.7f, sr);
            Tone(b, sr, 0f, r.Jitter(80f, 0.1f), 60f, 0.03f, 1f, 0.001f, 0.07f, 0.2f);
            Tone(b, sr, 0f, 140f, 130f, 0.02f, 0.4f, 0.001f, 0.05f);
            Noise(b, sr, ref r, 0f, 0.45f, 0.0003f, 0.008f, Filter.BandPass, 2600f, 0f, 0.1f, 0.9f);
            Noise(b, sr, ref r, 0f, 0.35f, 0.001f, 0.04f, Filter.LowPass, 700f);
            Room(b, sr, 0.2f, 1.2f);
            return Finish(b, sr);
        }

        /// <summary>Long sneaker skid: two detuned stick-slip partials gliding down plus broadband friction noise.</summary>
        private static float[] Slide(int sr, ref SfxRng r)
        {
            var b = Alloc(1.2f, sr);
            float f0 = r.Jitter(2200f, 0.1f);
            Squeak(b, sr, ref r, 0f, 0.8f, f0, 0.72f, 0.4f);
            Squeak(b, sr, ref r, 0.02f, 0.75f, f0 * 1.013f, 0.7f, 0.25f);
            int n = Math.Min(b.Length, (int)(0.85f * sr));
            var bp = Biquad.BandPass(sr, 2500f, 0.7f);
            var lp = Biquad.LowPass(sr, 300f, 0.7f);
            var pink = new PinkNoise();
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / sr;
                float env = Gate(t, 0.03f, 0.45f, 0.35f);
                float p = pink.Next(ref r) * 3f;
                b[i] += env * (bp.Process(p) * 0.25f + lp.Process(p) * 0.2f);
            }
            Room(b, sr, 0.18f, 1.2f);
            return Finish(b, sr, 0.85f, 0.004f, 0.05f);
        }

        // =====================================================================================================
        // Match flow
        // =====================================================================================================

        /// <summary>Pea whistle (~2.8 kHz) with the pea's characteristic flutter (FM + AM at ~30 Hz) and breath noise.</summary>
        private static float[] Whistle(int sr, ref SfxRng r, bool doubleBlast)
        {
            var b = Alloc(doubleBlast ? 1.3f : 1.6f, sr);
            if (doubleBlast)
            {
                WhistleBlast(b, sr, ref r, 0f, 0.26f);
                WhistleBlast(b, sr, ref r, 0.38f, 0.32f);
            }
            else
            {
                WhistleBlast(b, sr, ref r, 0f, 0.9f);
            }
            Room(b, sr, 0.25f, 1.5f);
            return Finish(b, sr, 0.85f, 0.003f, 0.05f);
        }

        private static void WhistleBlast(float[] b, int sr, ref SfxRng r, float start, float duration)
        {
            float f = r.Jitter(2800f, 0.02f);
            float fm = r.Range(28f, 36f);
            int s0 = (int)(start * sr);
            int n = Math.Min(b.Length - s0, (int)(duration * sr));
            var hp = Biquad.HighPass(sr, 5000f, 0.7f);
            double phase = 0.0;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / sr;
                float env = Gate(t, 0.02f, Math.Max(0f, duration - 0.08f), 0.06f);
                float flutter = (float)Math.Sin(TwoPi * fm * t);
                // The pea alternates the jet between two pitches: a soft square of the flutter gives the "two-tone" trill.
                float freq = f * (1f + 0.035f * (float)Math.Tanh(4f * flutter));
                phase += TwoPi * freq / sr;
                float s = (float)(Math.Sin(phase) + 0.18 * Math.Sin(2.0 * phase) + 0.05 * Math.Sin(3.0 * phase));
                float am = 0.8f + 0.2f * (float)Math.Sin(TwoPi * fm * t + 1.3f);
                float breath = hp.Process(r.Signed) * 0.06f;
                b[s0 + i] += env * (0.8f * am * s + breath);
            }
        }

        private static float[] Beep(int sr, float freq, float hold, float peak)
        {
            var b = Alloc(0.35f, sr);
            GatedTone(b, sr, 0f, freq, 0.8f, 0.005f, hold, 0.04f, 0.1f, 0.05f);
            GatedTone(b, sr, 0f, freq * 0.5f, 0.2f, 0.005f, hold, 0.04f);
            return Finish(b, sr, peak, 0.001f, 0.01f);
        }

        private static float[] RoundStart(int sr)
        {
            var b = Alloc(0.9f, sr);
            GatedTone(b, sr, 0f, 1760f, 0.7f, 0.005f, 0.45f, 0.12f, 0.12f, 0.04f);
            GatedTone(b, sr, 0f, 880f, 0.35f, 0.005f, 0.45f, 0.12f);
            Room(b, sr, 0.2f, 1.4f);
            return Finish(b, sr, 0.8f);
        }

        /// <summary>Knock-out: deep boom, then the body hitting the floor twice in the hall.</summary>
        private static float[] Elimination(int sr, ref SfxRng r)
        {
            var b = Alloc(2f, sr);
            Tone(b, sr, 0f, r.Jitter(58f, 0.08f), 34f, 0.12f, 1f, 0.002f, 0.28f, 0.3f);
            Noise(b, sr, ref r, 0f, 0.7f, 0.001f, 0.2f, Filter.LowPass, 1800f, 180f, 0.25f, 0.7f, true);
            Noise(b, sr, ref r, 0f, 0.4f, 0.0005f, 0.02f, Filter.BandPass, 1200f, 800f, 0.05f, 0.8f);
            float fall = r.Range(0.3f, 0.36f);
            Tone(b, sr, fall, 85f, 65f, 0.02f, 0.6f, 0.001f, 0.06f);
            Noise(b, sr, ref r, fall, 0.3f, 0.001f, 0.04f, Filter.LowPass, 600f);
            Tone(b, sr, fall + r.Range(0.13f, 0.18f), 95f, 70f, 0.02f, 0.35f, 0.001f, 0.05f);
            Saturate(b, 0.8f);
            Room(b, sr, 0.25f, 1.5f);
            return Finish(b, sr);
        }

        private static float[] Revive(int sr, ref SfxRng r)
        {
            var b = Alloc(2.2f, sr);
            float[] notes = { 1046.5f, 1318.5f, 1568f, 2093f };
            for (int i = 0; i < notes.Length; i++)
            {
                float start = i * 0.07f;
                Tone(b, sr, start, notes[i], 0f, 0f, 0.35f, 0.004f, 0.4f, 0.15f);
                Tone(b, sr, start, notes[i] * 1.003f, 0f, 0f, 0.2f, 0.004f, 0.4f);
            }
            Tone(b, sr, 0f, 523.25f, 0f, 0f, 0.15f, 0.15f, 0.6f);
            Noise(b, sr, ref r, 0.05f, 0.05f, 0.1f, 0.4f, Filter.HighPass, 6000f);
            Room(b, sr, 0.3f, 1.8f);
            return Finish(b, sr, 0.8f);
        }

        private static float[] UltimateReady(int sr, ref SfxRng r)
        {
            var b = Alloc(1.4f, sr);
            Tone(b, sr, 0f, 1318.5f, 0f, 0f, 0.4f, 0.003f, 0.3f, 0.1f);
            Tone(b, sr, 0f, 1318.5f * 1.003f, 0f, 0f, 0.2f, 0.003f, 0.3f);
            Tone(b, sr, 0.12f, 1975.5f, 0f, 0f, 0.4f, 0.003f, 0.35f, 0.1f);
            Tone(b, sr, 0.12f, 1975.5f * 1.003f, 0f, 0f, 0.2f, 0.003f, 0.35f);
            Noise(b, sr, ref r, 0.1f, 0.04f, 0.05f, 0.3f, Filter.HighPass, 7000f);
            Room(b, sr, 0.25f, 1.5f);
            return Finish(b, sr, 0.75f);
        }

        // =====================================================================================================
        // Abilities
        // =====================================================================================================

        private static float[] AbilityCast(int sr, ref SfxRng r)
        {
            var b = Alloc(0.9f, sr);
            Noise(b, sr, ref r, 0f, 0.8f, 0.08f, 0.15f, Filter.BandPass, r.Jitter(300f, 0.1f), r.Jitter(3000f, 0.1f), 0.3f, 1.2f, true);
            Tone(b, sr, 0f, 180f, r.Jitter(520f, 0.1f), 0.1f, 0.3f, 0.05f, 0.18f, 0.3f);
            Tone(b, sr, 0f, 60f, 0f, 0f, 0.3f, 0.03f, 0.12f);
            Room(b, sr, 0.15f, 1.2f);
            return Finish(b, sr, 0.85f, 0.003f);
        }

        private static float[] UltimateCast(int sr, ref SfxRng r)
        {
            var b = Alloc(2.6f, sr);
            Noise(b, sr, ref r, 0f, 0.7f, 0.3f, 0.4f, Filter.BandPass, 200f, 4000f, 0.35f, 1.3f, true);
            Tone(b, sr, 0.3f, 55f, 32f, 0.15f, 1f, 0.002f, 0.45f, 0.3f);
            Noise(b, sr, ref r, 0.3f, 0.6f, 0.001f, 0.35f, Filter.LowPass, 2500f, 200f, 0.3f, 0.7f, true);
            Tone(b, sr, 0f, 110f, 0f, 0f, 0.25f, 0.25f, 0.6f);
            Tone(b, sr, 0f, 165f, 0f, 0f, 0.25f, 0.25f, 0.6f);
            Saturate(b, 0.5f);
            Room(b, sr, 0.3f, 1.8f);
            return Finish(b, sr, 0.9f, 0.003f);
        }

        /// <summary>Meteor detonation: pressure blast with a falling low-pass, sub drop, fire crackle.</summary>
        private static float[] Shockwave(int sr, ref SfxRng r)
        {
            var b = Alloc(2.8f, sr);
            Noise(b, sr, ref r, 0f, 1f, 0.001f, 0.45f, Filter.LowPass, 6000f, 180f, 0.6f, 0.7f, true);
            Tone(b, sr, 0f, r.Jitter(50f, 0.08f), 28f, 0.2f, 1f, 0.002f, 0.5f, 0.35f);
            Crackle(b, sr, ref r, 0.02f, 1.2f, 300f, 20f, 0.5f, 0.0015f, 1500f);
            Noise(b, sr, ref r, 0f, 0.4f, 0.0003f, 0.02f, Filter.BandPass, 1200f, 800f, 0.05f, 0.8f);
            Saturate(b, 1f);
            Room(b, sr, 0.3f, 2f);
            return Finish(b, sr);
        }

        /// <summary>Energy beam: detuned sawtooth drone through a resonant sweeping band-pass, whine and electrical crackle.</summary>
        private static float[] Beam(int sr, ref SfxRng r)
        {
            var b = Alloc(1.8f, sr);
            int n = Math.Min(b.Length, (int)(1.3f * sr));
            var bp = new Biquad();
            double p1 = 0.0, p2 = 0.0;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / sr;
                if ((i & 31) == 0)
                {
                    float sweep = t < 0.25f ? 800f + 1700f * (t / 0.25f) : 2500f - 1600f * Math.Min(1f, (t - 0.25f) / 0.8f);
                    sweep *= 1f + 0.1f * (float)Math.Sin(TwoPi * 7f * t);
                    bp.SetBandPass(sr, sweep, 3f);
                }
                p1 += 110f / sr;
                p2 += 110.8f / sr;
                float saw = (float)((p1 - Math.Floor(p1)) * 2.0 - 1.0 + (p2 - Math.Floor(p2)) * 2.0 - 1.0) * 0.5f;
                float env = Gate(t, 0.02f, 0.8f, 0.45f);
                b[i] += bp.Process(saw) * env * 1.2f + saw * env * 0.15f;
            }
            Tone(b, sr, 0f, 3200f, 1600f, 0.4f, 0.15f, 0.01f, 0.5f);
            Crackle(b, sr, ref r, 0f, 1.1f, 150f, 60f, 0.25f, 0.001f, 3000f);
            Saturate(b, 0.4f);
            Room(b, sr, 0.2f, 1.4f);
            return Finish(b, sr, 0.85f, 0.003f);
        }

        /// <summary>Ice forming: dense then sparse cracking ticks, larger cracks, a creak, glassy tinkles and cold air.</summary>
        private static float[] Freeze(int sr, ref SfxRng r)
        {
            var b = Alloc(1.8f, sr);
            Crackle(b, sr, ref r, 0f, 0.9f, 400f, 30f, 0.7f, 0.0008f, 2500f);
            Crackle(b, sr, ref r, 0f, 0.6f, 60f, 5f, 0.9f, 0.003f, 800f, 5000f);
            Tone(b, sr, 0.02f, 300f, 220f, 0.3f, 0.2f, 0.02f, 0.3f, 0.4f);
            for (int i = 0; i < 6; i++)
                Tone(b, sr, r.Range(0.05f, 0.6f), r.Range(4000f, 7500f), 0f, 0f, 0.12f, 0.001f, 0.08f);
            Noise(b, sr, ref r, 0f, 0.15f, 0.05f, 0.4f, Filter.HighPass, 4000f, 0f, 0.1f, 0.7f, true);
            Room(b, sr, 0.25f, 1.6f);
            return Finish(b, sr, 0.85f);
        }

        private static float[] Teleport(int sr, ref SfxRng r)
        {
            var b = Alloc(1.1f, sr);
            Noise(b, sr, ref r, 0f, 0.8f, 0.12f, 0.1f, Filter.BandPass, 400f, r.Jitter(5000f, 0.1f), 0.25f, 1.4f, true);
            Tone(b, sr, 0f, 300f, r.Jitter(1400f, 0.1f), 0.12f, 0.25f, 0.1f, 0.12f);
            Noise(b, sr, ref r, 0.2f, 0.6f, 0.0005f, 0.012f, Filter.BandPass, 1500f, 1500f, 0.1f, 1f);
            Tone(b, sr, 0.2f, 180f, 90f, 0.02f, 0.5f, 0.001f, 0.04f);
            Room(b, sr, 0.18f, 1.3f);
            return Finish(b, sr, 0.85f, 0.003f);
        }

        /// <summary>Viscous splat: low-passed noise burst, a gloopy pitch drop, bubble pops and squelch.</summary>
        private static float[] Glue(int sr, ref SfxRng r)
        {
            var b = Alloc(0.9f, sr);
            Noise(b, sr, ref r, 0f, 0.9f, 0.0008f, 0.05f, Filter.LowPass, 2500f, 600f, 0.08f);
            Tone(b, sr, 0f, r.Jitter(190f, 0.1f), 85f, 0.05f, 0.6f, 0.001f, 0.08f);
            for (int i = 0; i < 5; i++)
            {
                float f = r.Range(500f, 900f);
                Tone(b, sr, r.Range(0.03f, 0.35f), f, f * 1.8f, 0.01f, 0.2f, 0.001f, 0.015f);
            }
            Noise(b, sr, ref r, 0.01f, 0.3f, 0.01f, 0.06f, Filter.BandPass, 900f, 700f, 0.1f, 3f);
            Room(b, sr, 0.1f, 1f);
            return Finish(b, sr);
        }

        /// <summary>Turret: servo whine with gear ripple, mechanical click, then a pneumatic launch pop.</summary>
        private static float[] Turret(int sr, ref SfxRng r)
        {
            var b = Alloc(1f, sr);
            int n = Math.Min(b.Length, (int)(0.24f * sr));
            var lp = Biquad.LowPass(sr, 3000f, 0.7f);
            double phase = 0.0;
            float f0 = r.Jitter(420f, 0.08f);
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / sr;
                float f = f0 + (f0 * 0.62f) * (t / 0.22f);
                phase += TwoPi * f / sr;
                float s = (float)(Math.Sin(phase) + 0.3 * Math.Sin(3.0 * phase));
                float gear = 0.75f + 0.25f * (float)Math.Sin(TwoPi * 45f * t);
                b[i] += lp.Process(s) * gear * Gate(t, 0.01f, 0.18f, 0.04f) * 0.25f;
            }
            Noise(b, sr, ref r, 0.25f, 0.3f, 0.0002f, 0.002f, Filter.HighPass, 3000f);
            Noise(b, sr, ref r, 0.26f, 0.9f, 0.0005f, 0.03f, Filter.HighPass, 400f);
            Tone(b, sr, 0.26f, 110f, 70f, 0.02f, 0.6f, 0.001f, 0.05f);
            Noise(b, sr, ref r, 0.26f, 0.3f, 0.0003f, 0.01f, Filter.BandPass, 2500f, 2500f, 0.1f, 1f);
            Room(b, sr, 0.15f, 1.2f);
            return Finish(b, sr, 0.85f);
        }

        /// <summary>Energy barrier hum: harmonic stack with slow beating and electrical hiss.</summary>
        private static float[] ShieldHum(int sr, ref SfxRng r, bool loop)
        {
            float length = loop ? 4.5f : 2f;
            var b = Alloc(length, sr);
            Hum(b, sr, ref r, 110f, new[] { 1f, 0.5f, 0.3f, 0.15f }, 0.7f, 3.1f, 0.15f, !loop, 0.15f, 1.2f, 0.5f, 0.6f);
            AddHiss(b, sr, ref r, 3500f, 0.05f, !loop, 0.15f, 1.2f, 0.5f);
            return FinishSustain(b, sr, loop);
        }

        /// <summary>Magnetic field: 60 Hz mains-like buzz rich in odd harmonics, pulsing whirr and a faint metallic ring.</summary>
        private static float[] MagnetHum(int sr, ref SfxRng r, bool loop)
        {
            float length = loop ? 4.5f : 2f;
            var b = Alloc(length, sr);
            Hum(b, sr, ref r, 60f, new[] { 1f, 0.2f, 0.55f, 0.15f, 0.35f, 0.1f, 0.22f, 0.06f, 0.14f }, 0.4f, 6f, 0.45f, !loop, 0.1f, 1.2f, 0.4f, 0.6f);
            ApplyFilter(b, sr, Filter.LowPass, 1800f);
            if (!loop) Tone(b, sr, 0.05f, 1850f, 0f, 0f, 0.08f, 0.01f, 0.6f);
            return FinishSustain(b, sr, loop);
        }

        /// <summary>Time-stop tone: pure fifth with tremolo and a high shimmer.</summary>
        private static float[] StasisTone(int sr, ref SfxRng r, bool loop)
        {
            float length = loop ? 4.5f : 1.8f;
            var b = Alloc(length, sr);
            Hum(b, sr, ref r, 660f, new[] { 1f, 0f, 0f }, 0.9f, 5f, 0.3f, !loop, 0.08f, 0.9f, 0.6f, 0.35f);
            Hum(b, sr, ref r, 990f, new[] { 1f }, 1.2f, 5f, 0.3f, !loop, 0.12f, 0.85f, 0.6f, 0.22f);
            Hum(b, sr, ref r, 2640f, new[] { 1f }, 2f, 7f, 0.5f, !loop, 0.2f, 0.7f, 0.6f, 0.06f);
            return FinishSustain(b, sr, loop);
        }

        /// <summary>"Rewind": an impact + chime + whoosh rendered forwards with reverb, then reversed; tape-spool chirps on top.</summary>
        private static float[] Rewind(int sr, ref SfxRng r)
        {
            var b = Alloc(1.4f, sr);
            Tone(b, sr, 0.05f, 1318.5f, 0f, 0f, 0.3f, 0.002f, 0.25f);
            Tone(b, sr, 0.05f, 1975.5f, 0f, 0f, 0.2f, 0.002f, 0.25f);
            Noise(b, sr, ref r, 0.05f, 0.6f, 0.001f, 0.35f, Filter.BandPass, 3000f, 400f, 0.5f, 1.2f, true);
            Tone(b, sr, 0.05f, 80f, 50f, 0.05f, 0.5f, 0.001f, 0.15f);
            Room(b, sr, 0.35f, 1.6f);
            Reverse(b);
            for (int i = 0; i < 8; i++)
                Tone(b, sr, 0.35f + i * 0.06f, 2000f, 400f, 0.03f, 0.15f, 0.001f, 0.04f);
            return Finish(b, sr, 0.85f, 0.01f);
        }

        /// <summary>Optical camouflage: descending high glints and a swish of air.</summary>
        private static float[] Cloak(int sr, ref SfxRng r)
        {
            var b = Alloc(1.2f, sr);
            for (int i = 0; i < 12; i++)
            {
                float f = r.Range(2500f, 8000f);
                Tone(b, sr, r.Range(0f, 0.35f), f, f * 0.85f, 0.2f, 0.07f, 0.01f, r.Range(0.08f, 0.25f));
            }
            Noise(b, sr, ref r, 0f, 0.25f, 0.02f, 0.25f, Filter.BandPass, 6000f, 2500f, 0.5f, 1f, true);
            Room(b, sr, 0.3f, 1.5f);
            return Finish(b, sr, 0.75f, 0.003f);
        }

        /// <summary>Shadow clone: dark smoky whoosh with a low detuned hum and a short slap-back double.</summary>
        private static float[] Clone(int sr, ref SfxRng r)
        {
            var b = Alloc(1f, sr);
            Noise(b, sr, ref r, 0f, 0.7f, 0.05f, 0.12f, Filter.BandPass, r.Jitter(250f, 0.1f), 1400f, 0.2f, 1.3f, true);
            Tone(b, sr, 0f, 90f, 70f, 0.1f, 0.3f, 0.02f, 0.15f, 0.4f);
            Tone(b, sr, 0f, 91.5f, 71f, 0.1f, 0.25f, 0.02f, 0.15f, 0.4f);
            int d = (int)(0.045f * sr);
            for (int i = b.Length - 1; i >= d; i--) b[i] += b[i - d] * 0.45f; // backwards: no feedback
            Room(b, sr, 0.2f, 1.3f);
            return Finish(b, sr, 0.85f, 0.003f);
        }

        /// <summary>Ground slam: sub impact, low-passed blast, long brown-noise rumble with swells, debris rattles.</summary>
        private static float[] Earthquake(int sr, ref SfxRng r)
        {
            var b = Alloc(3.2f, sr);
            Tone(b, sr, 0f, 42f, 26f, 0.3f, 1f, 0.003f, 0.6f, 0.4f);
            Noise(b, sr, ref r, 0f, 0.7f, 0.001f, 0.3f, Filter.LowPass, 2200f, 150f, 0.3f, 0.7f, true);
            int n = Math.Min(b.Length, (int)(2.4f * sr));
            var lp = Biquad.LowPass(sr, 110f, 0.8f);
            var brown = new BrownNoise();
            var swell = new RandomLfo();
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / sr;
                float env = Gate(t, 0.05f, 1.2f, 1f);
                float mod = 0.5f + 0.5f * swell.Next(ref r, 4f, sr, 0.0008f);
                b[i] += lp.Process(brown.Next(ref r)) * env * mod * 0.9f;
            }
            Crackle(b, sr, ref r, 0.1f, 1.7f, 120f, 10f, 0.35f, 0.002f, 700f, 4000f);
            Saturate(b, 0.8f);
            Room(b, sr, 0.3f, 2.2f);
            return Finish(b, sr);
        }

        // =====================================================================================================
        // Crowd
        // =====================================================================================================

        /// <summary>Crowd roar: formant-shaped pink noise ("aah") with per-band shout modulation, whistles and claps.</summary>
        private static float[] CrowdCheer(int sr, ref SfxRng r, bool big)
        {
            float length = big ? 3.6f : 3f;
            var b = Alloc(length, sr);
            float sustainEnd = big ? 1.8f : 1.3f;
            int n = b.Length;
            var pink = new PinkNoise();
            var f1 = Biquad.BandPass(sr, r.Jitter(650f, 0.05f), 2.5f);
            var f2 = Biquad.BandPass(sr, r.Jitter(1150f, 0.05f), 3f);
            var f3 = Biquad.BandPass(sr, r.Jitter(2500f, 0.05f), 3.5f);
            var body = Biquad.LowPass(sr, 400f, 0.7f);
            var m1 = new RandomLfo();
            var m2 = new RandomLfo();
            var m3 = new RandomLfo();
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / sr;
                float rise = Math.Min(1f, t / 0.25f);
                rise = rise * rise * (3f - 2f * rise);
                float env = t < sustainEnd ? rise : (float)Math.Exp(-(t - sustainEnd) / 0.8f);
                float p = pink.Next(ref r) * 3f;
                float v = f1.Process(p) * (0.6f + 0.4f * m1.Next(ref r, 7f, sr))
                          + f2.Process(p) * 0.7f * (0.5f + 0.5f * m2.Next(ref r, 8.5f, sr))
                          + f3.Process(p) * 0.35f * (0.5f + 0.5f * m3.Next(ref r, 6f, sr))
                          + body.Process(p) * 0.3f;
                b[i] += v * env;
            }
            int whistles = big ? 4 : 2;
            for (int w = 0; w < whistles; w++) FingerWhistle(b, sr, ref r, r.Range(0.2f, 1.2f), r.Range(0.4f, 0.8f), r.Range(2000f, 3000f), 0.12f);
            Crackle(b, sr, ref r, 0.1f, length - 0.4f, 40f, big ? 110f : 70f, 0.5f, 0.004f, 900f, 6000f);
            Room(b, sr, 0.35f, 2.2f);
            return Finish(b, sr, 0.85f, 0.02f, 0.2f);
        }

        /// <summary>Crowd "ooh": two-formant ("oo") noise swelling and falling, with a hint of an inhale.</summary>
        private static float[] CrowdGasp(int sr, ref SfxRng r)
        {
            var b = Alloc(1.6f, sr);
            int n = Math.Min(b.Length, (int)(1.3f * sr));
            var pink = new PinkNoise();
            var f1 = new Biquad();
            var f2 = new Biquad();
            var mod = new RandomLfo();
            float baseF1 = r.Jitter(350f, 0.05f), baseF2 = r.Jitter(800f, 0.05f);
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / sr;
                if ((i & 63) == 0)
                {
                    float k = Math.Min(1f, t / 1.1f);
                    f1.SetBandPass(sr, baseF1 + 100f * k, 3f);
                    f2.SetBandPass(sr, baseF2 + 120f * k, 3f);
                }
                float env = t < 0.3f ? (float)Math.Sin(t / 0.3f * Math.PI * 0.5) : (float)Math.Exp(-(t - 0.3f) / 0.4f);
                float p = pink.Next(ref r) * 3f;
                b[i] += (f1.Process(p) + f2.Process(p) * 0.6f) * env * (0.75f + 0.25f * mod.Next(ref r, 5f, sr));
            }
            Noise(b, sr, ref r, 0f, 0.1f, 0.03f, 0.08f, Filter.HighPass, 3000f);
            Room(b, sr, 0.35f, 2f);
            return Finish(b, sr, 0.8f, 0.02f, 0.15f);
        }

        /// <summary>
        /// 10 s seamless crowd murmur loop: babble-like formant bands with independent modulation, bleacher rumble,
        /// slow swells, sparse claps and the odd distant shout.
        /// </summary>
        private static float[] CrowdAmbience(int sr, ref SfxRng r)
        {
            const float loopLength = 10f;
            const float crossfade = 1.5f;
            var b = Alloc(loopLength + crossfade, sr);
            int n = b.Length;
            var pink = new PinkNoise();
            var brown = new BrownNoise();
            var bp1 = Biquad.BandPass(sr, 500f, 0.8f);
            var bp2 = Biquad.BandPass(sr, 1100f, 1.2f);
            var bp3 = Biquad.BandPass(sr, 2200f, 1.5f);
            var rumble = Biquad.LowPass(sr, 250f, 0.7f);
            var m1 = new RandomLfo();
            var m2 = new RandomLfo();
            var m3 = new RandomLfo();
            var swell = new RandomLfo();
            for (int i = 0; i < n; i++)
            {
                float p = pink.Next(ref r) * 3f;
                float s = 0.65f + 0.35f * swell.Next(ref r, 0.15f, sr, 0.00005f);
                float v = bp1.Process(p) * (0.5f + 0.5f * m1.Next(ref r, 4f, sr))
                          + bp2.Process(p) * 0.6f * (0.5f + 0.5f * m2.Next(ref r, 5f, sr))
                          + bp3.Process(p) * 0.25f * (0.5f + 0.5f * m3.Next(ref r, 3.5f, sr))
                          + rumble.Process(brown.Next(ref r)) * 0.3f;
                b[i] += v * s;
            }
            Crackle(b, sr, ref r, 0f, loopLength + crossfade, 3f, 3f, 0.25f, 0.004f, 1000f, 6000f);
            for (int k = 0; k < 3; k++)
            {
                // Distant shouts: short formant bursts.
                float start = r.Range(0.5f, loopLength - 1f);
                Noise(b, sr, ref r, start, 0.25f, 0.08f, 0.15f, Filter.BandPass, r.Range(600f, 900f), r.Range(700f, 1100f), 0.3f, 4f, true, 0.6f);
            }
            Room(b, sr, 0.3f, 2f);
            var loop = MakeLoop(b, sr, crossfade);
            Normalize(loop, 0.8f);
            return loop;
        }

        // =====================================================================================================
        // UI
        // =====================================================================================================

        private static float[] UiClick(int sr, ref SfxRng r)
        {
            var b = Alloc(0.08f, sr);
            Tone(b, sr, 0f, 2400f, 0f, 0f, 0.5f, 0.0005f, 0.004f);
            Noise(b, sr, ref r, 0f, 0.3f, 0.0002f, 0.002f, Filter.HighPass, 4000f);
            return Finish(b, sr, 0.7f, 0.0002f, 0.005f);
        }

        private static float[] UiConfirm(int sr)
        {
            var b = Alloc(0.4f, sr);
            GatedTone(b, sr, 0f, 880f, 0.5f, 0.004f, 0.06f, 0.02f, 0.15f);
            GatedTone(b, sr, 0.07f, 1320f, 0.5f, 0.004f, 0.12f, 0.08f, 0.15f);
            Room(b, sr, 0.1f, 0.8f);
            return Finish(b, sr, 0.7f);
        }

        // =====================================================================================================
        // Recipe helpers
        // =====================================================================================================

        /// <summary>Sneaker squeak: gliding tone with stick-slip amplitude modulation (60-130 Hz) and a bell envelope.</summary>
        private static void Squeak(float[] b, int sr, ref SfxRng r, float start, float duration, float f0, float endRatio, float amp)
        {
            int s0 = (int)(start * sr);
            int n = Math.Min(b.Length - s0, (int)(duration * sr));
            float fm = r.Range(60f, 130f);
            double phase = 0.0;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / sr;
                float k = t / duration;
                float f = f0 * (1f + (endRatio - 1f) * k) * (1f + 0.01f * r.Signed);
                phase += TwoPi * f / sr;
                float am = 0.6f + 0.4f * (float)Math.Sin(TwoPi * fm * t);
                float env = duration > 0.3f ? Gate(t, 0.03f, duration * 0.55f, duration * 0.4f) : Bell(t, duration);
                float s = (float)(Math.Sin(phase) + 0.3 * Math.Sin(2.0 * phase));
                b[s0 + i] += amp * env * am * s;
            }
        }

        /// <summary>Two-finger whistle from the stands: rising glide with vibrato.</summary>
        private static void FingerWhistle(float[] b, int sr, ref SfxRng r, float start, float duration, float freq, float amp)
        {
            int s0 = (int)(start * sr);
            int n = Math.Min(b.Length - s0, (int)(duration * sr));
            double phase = 0.0;
            float vib = r.Range(5f, 7f);
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / sr;
                float f = freq * (0.92f + 0.08f * Math.Min(1f, t / 0.15f)) * (1f + 0.012f * (float)Math.Sin(TwoPi * vib * t));
                phase += TwoPi * f / sr;
                b[s0 + i] += amp * Gate(t, 0.04f, duration * 0.6f, duration * 0.3f) * (float)Math.Sin(phase);
            }
        }

        /// <summary>Tone with a gate envelope (beeps) and optional vibrato.</summary>
        private static void GatedTone(float[] b, int sr, float start, float freq, float amp, float attack, float hold, float release,
            float h2 = 0f, float h3 = 0f, float vibratoHz = 0f, float vibratoDepth = 0f)
        {
            int s0 = (int)(start * sr);
            int n = Math.Min(b.Length - s0, (int)((attack + hold + release) * sr) + 1);
            double phase = 0.0;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / sr;
                float f = vibratoHz > 0f ? freq * (1f + vibratoDepth * (float)Math.Sin(TwoPi * vibratoHz * t)) : freq;
                phase += TwoPi * f / sr;
                float s = (float)Math.Sin(phase);
                if (h2 != 0f) s += h2 * (float)Math.Sin(2.0 * phase);
                if (h3 != 0f) s += h3 * (float)Math.Sin(3.0 * phase);
                b[s0 + i] += amp * Gate(t, attack, hold, release) * s;
            }
        }

        /// <summary>
        /// Harmonic hum with a slightly detuned copy (beating) and amplitude modulation. When <paramref name="gated"/> is
        /// false the hum is steady (for loops).
        /// </summary>
        private static void Hum(float[] b, int sr, ref SfxRng r, float baseF, float[] harmonics, float detuneHz, float amRate, float amDepth,
            bool gated, float attack, float hold, float release, float amp)
        {
            double phaseA = r.Value * TwoPi, phaseB = r.Value * TwoPi;
            float inv = 1f / sr;
            for (int i = 0; i < b.Length; i++)
            {
                float t = i * inv;
                float env = gated ? Gate(t, attack, hold, release) : 1f;
                if (gated && env <= 0f && t > attack) break;
                phaseA += TwoPi * baseF * inv;
                phaseB += TwoPi * (baseF + detuneHz) * inv;
                float s = 0f;
                for (int h = 0; h < harmonics.Length; h++)
                {
                    float a = harmonics[h];
                    if (a == 0f) continue;
                    s += a * (float)(Math.Sin(phaseA * (h + 1)) + 0.6 * Math.Sin(phaseB * (h + 1)));
                }
                float am = 1f - amDepth * 0.5f * (1f + (float)Math.Sin(TwoPi * amRate * t));
                b[i] += amp * env * am * s;
            }
        }

        private static void AddHiss(float[] b, int sr, ref SfxRng r, float centre, float amp, bool gated, float attack, float hold, float release)
        {
            var bp = Biquad.BandPass(sr, centre, 0.8f);
            float inv = 1f / sr;
            for (int i = 0; i < b.Length; i++)
            {
                float env = gated ? Gate(i * inv, attack, hold, release) : 1f;
                b[i] += bp.Process(r.Signed) * amp * env;
            }
        }

        private static float[] FinishSustain(float[] b, int sr, bool loop)
        {
            if (!loop) return Finish(b, sr, 0.8f, 0.01f, 0.1f);
            var looped = MakeLoop(b, sr, 0.5f);
            Normalize(looped, 0.8f);
            return looped;
        }
    }
}
