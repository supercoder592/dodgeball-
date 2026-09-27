using UnityEngine;
using static DodgeballUltra.VFX.VfxBuildKit;

namespace DodgeballUltra.VFX
{
    /// <summary>
    /// Builds every <see cref="VfxId"/> as a layered ParticleSystem template at start-up (no authored assets needed).
    /// <para>
    /// Art direction is realistic: a foam dodgeball striking a player does not spark, it blasts a puff of chalk/dust and a
    /// few crumbs out of the ball's surface; floor bounces and slides kick up low, spreading gym dust; ability effects are
    /// restrained and physically motivated (heat, frost vapour, viscous splashes, smoke-bomb teleports, soft light beams)
    /// with HDR additive cores so they bloom under HDRP rather than looking like flat cartoon sprites.
    /// </para>
    /// Sizes at <c>scale = 1</c> are documented on each <see cref="VfxId"/> value. Conventions: see <see cref="VfxBuildKit"/>.
    /// </summary>
    public static class ProceduralVfxLibrary
    {
        // Physically-motivated palette (linear-ish sRGB values chosen for a neutral-lit indoor gym).
        private static readonly Color Chalk = new Color(0.93f, 0.92f, 0.89f);
        private static readonly Color GymDust = new Color(0.80f, 0.76f, 0.70f);
        private static readonly Color FloorDust = new Color(0.62f, 0.57f, 0.51f);
        private static readonly Color Soot = new Color(0.22f, 0.21f, 0.21f);
        private static readonly Color FireHot = new Color(1f, 0.9f, 0.62f);
        private static readonly Color FireMid = new Color(1f, 0.48f, 0.14f);
        private static readonly Color FireDeep = new Color(0.55f, 0.12f, 0.03f);
        private static readonly Color Frost = new Color(0.86f, 0.94f, 1f);
        private static readonly Color IceBlue = new Color(0.62f, 0.82f, 0.98f);
        private static readonly Color Glue = new Color(0.38f, 0.74f, 0.12f);
        private static readonly Color GlueDark = new Color(0.25f, 0.55f, 0.07f);
        private static readonly Color BeamCyan = new Color(0.5f, 0.9f, 1f);
        private static readonly Color ShadowInk = new Color(0.08f, 0.075f, 0.1f);
        private static readonly Color ShadowViolet = new Color(0.2f, 0.16f, 0.28f);
        private static readonly Color MagicViolet = new Color(0.72f, 0.6f, 1f);
        private static readonly Color StageSmoke = new Color(0.82f, 0.81f, 0.84f);
        private static readonly Color TimeBlue = new Color(0.58f, 0.82f, 1f);
        private static readonly Color TimeGold = new Color(1f, 0.84f, 0.55f);
        private static readonly Color WarmLight = new Color(1f, 0.95f, 0.84f);

        /// <summary>
        /// Creates the (inactive) template for <paramref name="id"/> under <paramref name="parent"/> and returns its definition.
        /// </summary>
        public static VfxDefinition Build(VfxId id, Transform parent)
        {
            var root = new GameObject("VFX_" + id);
            root.layer = GameLayers.Visual;
            root.transform.SetParent(parent, false);
            root.SetActive(false); // templates never simulate; clones are activated by the manager

            var def = new VfxDefinition { Id = id, Template = root };
            switch (id)
            {
                case VfxId.HitImpact: HitImpact(root, def); break;
                case VfxId.EliminationBurst: EliminationBurst(root, def); break;
                case VfxId.CatchPuff: CatchPuff(root, def); break;
                case VfxId.PerfectCatchBurst: PerfectCatchBurst(root, def); break;
                case VfxId.ThrowWhoosh: ThrowWhoosh(root, def); break;
                case VfxId.FloorImpactDust: FloorDustRing(root, def, 12, 0.06f, 1.0f, 2.4f, 0.08f, 0.18f, 0.38f, true); def.MaxInstances = 16; def.Prewarm = 6; break;
                case VfxId.SlideDust: SlideDust(root, def); break;
                case VfxId.LandingDust: FloorDustRing(root, def, 14, 0.2f, 1.2f, 2.6f, 0.1f, 0.2f, 0.3f, false); def.MaxInstances = 8; def.Prewarm = 3; break;
                case VfxId.FootstepDust: FloorDustRing(root, def, 4, 0.05f, 0.3f, 0.8f, 0.05f, 0.1f, 0.2f, false); def.MaxInstances = 16; def.Prewarm = 4; break;
                case VfxId.Shockwave: Shockwave(root, def); break;
                case VfxId.FireTrail: FireTrail(root, def); break;
                case VfxId.BeamTrail: BeamTrail(root, def); break;
                case VfxId.BeamImpact: BeamImpact(root, def); break;
                case VfxId.CloneSpawn: CloneSpawn(root, def); break;
                case VfxId.CloneDissolve: CloneDissolve(root, def); break;
                case VfxId.CloakShimmer: CloakShimmer(root, def); break;
                case VfxId.TeleportPoof: TeleportPoof(root, def); break;
                case VfxId.MagneticField: MagneticField(root, def); break;
                case VfxId.ShieldImpact: ShieldImpact(root, def); break;
                case VfxId.TackleDust: TackleDust(root, def); break;
                case VfxId.EarthquakeRing: EarthquakeRing(root, def); break;
                case VfxId.GlueSplat: GlueSplat(root, def); break;
                case VfxId.TurretMuzzle: TurretMuzzle(root, def); break;
                case VfxId.SwapFlash: SwapFlash(root, def); break;
                case VfxId.VanishSmoke: VanishSmoke(root, def); break;
                case VfxId.IceBurst: IceBurst(root, def); break;
                case VfxId.IceTrail: IceTrail(root, def); break;
                case VfxId.FrozenMist: FrozenMist(root, def); break;
                case VfxId.DodgeAfterimage: DodgeAfterimage(root, def); break;
                case VfxId.RewindTrail: RewindTrail(root, def); break;
                case VfxId.StasisBubble: StasisBubble(root, def); break;
                case VfxId.TemporalZone: TemporalZone(root, def); break;
                case VfxId.ReviveBeam: ReviveBeam(root, def); break;
                case VfxId.UltimateReady: UltimateReady(root, def); break;
                default: HitImpact(root, def); break;
            }
            return def;
        }

        // =====================================================================================================
        // Generic impacts
        // =====================================================================================================

        /// <summary>Foam ball on a body: compressed-air chalk puff out of the contact, a faint pressure haze, crumbs.</summary>
        private static void HitImpact(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 12;
            def.Prewarm = 4;

            var puff = NewSystem(root, "ChalkPuff", VfxTexture.Smoke, false);
            Main(puff, 0.6f, R(0.35f, 0.65f), R(1.2f, 3.2f), R(0.14f, 0.28f), Alpha(Chalk, 0.55f), Alpha(GymDust, 0.42f), gravity: -0.02f, maxParticles: 24);
            Burst(puff, 9, 13);
            ConeUp(puff, 62f, 0.04f);
            Drag(puff, 5f);
            SizeOverLife(puff, 2.2f, Expand(0.35f));
            ColorOverLife(puff, Fade(0.06f, 0.35f));
            Noise(puff, 0.35f, 1.4f);
            Spin(puff, -40f, 40f);

            var haze = NewSystem(root, "PressureHaze", VfxTexture.Soft, true, 0.8f);
            Main(haze, 0.2f, R(0.1f, 0.13f), 0f, 0.22f, Alpha(WarmLight, 0.22f), maxParticles: 2, randomRotation: false);
            Burst(haze, 1, 1);
            SizeOverLife(haze, 3f, Curve(0f, 0.35f, 1f, 1f));
            ColorOverLife(haze, FadeOut(0.05f));

            var crumbs = NewSystem(root, "Crumbs", VfxTexture.Debris, false);
            Main(crumbs, 0.9f, R(0.5f, 0.9f), R(2f, 4.5f), R(0.012f, 0.028f), C(0.86f, 0.85f, 0.8f), C(0.56f, 0.54f, 0.5f), gravity: 1f, maxParticles: 16);
            Burst(crumbs, 6, 10);
            ConeUp(crumbs, 70f, 0.03f);
            FloorCollision(crumbs, 0.3f, 0.4f);
            Spin(crumbs, -360f, 360f);
            ColorOverLife(crumbs, FadeOut(0.7f));
        }

        /// <summary>Heavy knockout hit: bigger dust blast, brief bright flash, debris and slowly settling motes.</summary>
        private static void EliminationBurst(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 6;
            def.Prewarm = 2;

            var puff = NewSystem(root, "DustBlast", VfxTexture.Smoke, false);
            Main(puff, 1f, R(0.55f, 1.05f), R(1.5f, 4.5f), R(0.2f, 0.4f), Alpha(Chalk, 0.55f), Alpha(GymDust, 0.45f), gravity: -0.03f, maxParticles: 32);
            Burst(puff, 16, 22);
            ConeUp(puff, 78f, 0.08f);
            Drag(puff, 4f);
            SizeOverLife(puff, 2.6f, Expand(0.35f));
            ColorOverLife(puff, Fade(0.05f, 0.4f));
            Noise(puff, 0.5f, 1.1f);
            Spin(puff, -35f, 35f);

            var flash = NewSystem(root, "Flash", VfxTexture.Soft, true, 1.5f);
            Main(flash, 0.2f, 0.09f, 0f, 0.9f, Alpha(WarmLight, 0.6f), maxParticles: 1, randomRotation: false);
            Burst(flash, 1, 1);
            SizeOverLife(flash, 1.2f, Curve(0f, 0.55f, 1f, 1f));
            ColorOverLife(flash, FadeOut(0.1f));

            var debris = NewSystem(root, "Debris", VfxTexture.Debris, false);
            Main(debris, 1.2f, R(0.6f, 1.1f), R(3f, 6f), R(0.015f, 0.035f), C(0.85f, 0.84f, 0.8f), C(0.5f, 0.48f, 0.45f), gravity: 1f, maxParticles: 24);
            Burst(debris, 14, 20);
            ConeUp(debris, 75f, 0.05f);
            FloorCollision(debris, 0.3f, 0.35f);
            Spin(debris, -400f, 400f);
            ColorOverLife(debris, FadeOut(0.75f));

            var motes = NewSystem(root, "Motes", VfxTexture.Soft, false);
            Main(motes, 1f, R(1.2f, 2.2f), R(0.2f, 0.6f), R(0.01f, 0.02f), Alpha(Chalk, 0.35f), gravity: -0.01f, maxParticles: 12);
            Burst(motes, 8, 12);
            Sphere(motes, 0.3f);
            Drag(motes, 1.5f);
            Noise(motes, 0.3f, 0.8f);
            ColorOverLife(motes, Fade(0.1f, 0.5f));
        }

        /// <summary>Normal catch: small puff of chalk squeezed out between hands and ball.</summary>
        private static void CatchPuff(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 8;
            def.Prewarm = 3;

            var puff = NewSystem(root, "Puff", VfxTexture.Smoke, false);
            Main(puff, 0.5f, R(0.25f, 0.45f), R(0.6f, 1.5f), R(0.08f, 0.15f), Alpha(Chalk, 0.45f), Alpha(GymDust, 0.35f), maxParticles: 12);
            Burst(puff, 6, 9);
            Sphere(puff, 0.08f);
            Drag(puff, 6f);
            SizeOverLife(puff, 2f, Expand(0.4f));
            ColorOverLife(puff, Fade(0.08f, 0.35f));
            Noise(puff, 0.2f, 1.6f);

            var motes = NewSystem(root, "Crumbs", VfxTexture.Debris, false);
            Main(motes, 0.6f, R(0.35f, 0.6f), R(0.8f, 2f), R(0.008f, 0.016f), C(0.9f, 0.89f, 0.85f), gravity: 0.9f, maxParticles: 8);
            Burst(motes, 4, 6);
            Sphere(motes, 0.06f);
            ColorOverLife(motes, FadeOut(0.6f));
        }

        /// <summary>Perfect catch: crisp warm flash, expanding pressure ring, radial light streaks, chalk puff, light flash.</summary>
        private static void PerfectCatchBurst(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 6;
            def.Prewarm = 2;
            def.LightFlashDuration = 0.18f;
            AddLight(root, new Color(1f, 0.88f, 0.66f), 3400f, 25000f, 5f);

            var flash = NewSystem(root, "Flash", VfxTexture.Soft, true, 2f);
            Main(flash, 0.2f, 0.1f, 0f, 0.7f, C(1f, 0.93f, 0.78f, 0.9f), maxParticles: 1, randomRotation: false);
            Burst(flash, 1, 1);
            SizeOverLife(flash, 1.3f, Curve(0f, 0.4f, 1f, 1f));
            ColorOverLife(flash, FadeOut(0.15f));

            var ring = NewSystem(root, "Ring", VfxTexture.Ring, true, 1.6f);
            Main(ring, 0.3f, 0.22f, 0f, 0.35f, C(1f, 0.88f, 0.62f, 0.8f), maxParticles: 1, randomRotation: false);
            Burst(ring, 1, 1);
            SizeOverLife(ring, 4f, Curve(0f, 0.25f, 0.35f, 0.8f, 1f, 1f));
            ColorOverLife(ring, FadeOut(0.2f));

            var streaks = NewSystem(root, "Streaks", VfxTexture.Soft, true, 1.8f);
            Main(streaks, 0.3f, R(0.12f, 0.24f), R(5f, 10f), R(0.02f, 0.035f), C(1f, 0.92f, 0.7f), C(1f, 0.8f, 0.5f), maxParticles: 20, randomRotation: false);
            Burst(streaks, 14, 18);
            Sphere(streaks, 0.05f);
            Drag(streaks, 6f);
            Stretch(streaks, 0.035f, 1f);
            ColorOverLife(streaks, FadeOut(0.2f));

            var puff = NewSystem(root, "Chalk", VfxTexture.Smoke, false);
            Main(puff, 0.6f, R(0.3f, 0.55f), R(1f, 2.4f), R(0.08f, 0.16f), Alpha(Chalk, 0.5f), maxParticles: 12);
            Burst(puff, 7, 10);
            Sphere(puff, 0.08f);
            Drag(puff, 5f);
            SizeOverLife(puff, 2.2f, Expand(0.35f));
            ColorOverLife(puff, Fade(0.05f, 0.35f));
        }

        /// <summary>Air wake behind a fast release (+Z = throw direction): faint streaks and a displaced-air haze.</summary>
        private static void ThrowWhoosh(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 8;
            def.Prewarm = 3;

            var streaks = NewSystem(root, "AirStreaks", VfxTexture.Soft, false);
            Main(streaks, 0.25f, R(0.1f, 0.18f), R(7f, 13f), R(0.02f, 0.05f), Alpha(Color.white, 0.14f), maxParticles: 8, randomRotation: false);
            Burst(streaks, 5, 7);
            ConeForward(streaks, 6f, 0.05f);
            Stretch(streaks, 0.06f, 1f);
            ColorOverLife(streaks, Fade(0.1f, 0.3f));

            var haze = NewSystem(root, "Wake", VfxTexture.Smoke, false);
            Main(haze, 0.3f, R(0.22f, 0.3f), R(1f, 2f), R(0.12f, 0.18f), Alpha(Chalk, 0.1f), maxParticles: 4);
            Burst(haze, 2, 3);
            ConeForward(haze, 12f, 0.04f);
            Drag(haze, 4f);
            SizeOverLife(haze, 2.5f, Expand(0.4f));
            ColorOverLife(haze, Fade(0.1f, 0.3f));
        }

        /// <summary>
        /// Low dust ring hugging the floor (ball bounces, landings, footsteps): particles spread radially along the floor,
        /// slow down through drag and lift only slightly, as disturbed gym dust does.
        /// </summary>
        private static void FloorDustRing(GameObject root, VfxDefinition def, int count, float radius, float speedMin, float speedMax,
            float sizeMin, float sizeMax, float alpha, bool withMotes)
        {
            var ring = NewSystem(root, "DustRing", VfxTexture.Smoke, false);
            Main(ring, 0.8f, R(0.45f, 0.9f), R(speedMin, speedMax), R(sizeMin, sizeMax), Alpha(GymDust, alpha), Alpha(FloorDust, alpha * 0.85f),
                maxParticles: count + 6);
            Burst(ring, Mathf.Max(1, count - 2), count + 2);
            FlatCircle(ring, radius, 0.5f);
            Velocity(ring, new Vector3(0f, 0.05f, 0f), new Vector3(0f, 0.25f, 0f));
            Drag(ring, 4f);
            SizeOverLife(ring, 2.5f, Expand(0.4f));
            ColorOverLife(ring, Fade(0.08f, 0.4f));
            Noise(ring, 0.2f, 1.2f);
            Spin(ring, -30f, 30f);

            if (!withMotes) return;
            var motes = NewSystem(root, "Motes", VfxTexture.Debris, false);
            Main(motes, 0.8f, R(0.35f, 0.7f), R(0.8f, 1.8f), R(0.006f, 0.014f), C(0.8f, 0.77f, 0.72f), gravity: 0.6f, maxParticles: 8);
            Burst(motes, 3, 6);
            ConeUp(motes, 40f, 0.04f);
            ColorOverLife(motes, FadeOut(0.6f));
        }

        /// <summary>Looping dust kicked up under a sliding player (attach at the feet; emits per metre travelled).</summary>
        private static void SlideDust(GameObject root, VfxDefinition def)
        {
            def.Looping = true;
            def.DefaultLoopDuration = 1f;
            def.MaxInstances = 8;
            def.Prewarm = 3;

            var dust = NewSystem(root, "SlideDust", VfxTexture.Smoke, false);
            Main(dust, 1f, R(0.5f, 1f), R(0.2f, 0.7f), R(0.12f, 0.26f), Alpha(GymDust, 0.32f), Alpha(FloorDust, 0.28f), loop: true, maxParticles: 80);
            Rate(dust, 10f, 9f);
            ConeUp(dust, 60f, 0.18f);
            Drag(dust, 2f);
            SizeOverLife(dust, 2.2f, Expand(0.4f));
            ColorOverLife(dust, Fade(0.1f, 0.4f));
            Noise(dust, 0.25f, 1f);
            Spin(dust, -25f, 25f);

            var scuff = NewSystem(root, "Scuff", VfxTexture.Debris, false);
            Main(scuff, 1f, R(0.25f, 0.5f), R(0.4f, 1.2f), R(0.005f, 0.012f), C(0.75f, 0.72f, 0.66f), gravity: 0.8f, loop: true, maxParticles: 24);
            Rate(scuff, 0f, 6f);
            ConeUp(scuff, 45f, 0.12f);
            ColorOverLife(scuff, FadeOut(0.6f));
        }

        // =====================================================================================================
        // Rayne
        // =====================================================================================================

        /// <summary>Meteor impact (3 m AOE): heat-haze fireball, ground shock of dust, embers, smoke residue, light flash.</summary>
        private static void Shockwave(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 4;
            def.LightFlashDuration = 0.35f;
            AddLight(root, new Color(1f, 0.62f, 0.32f), 2600f, 120000f, 8f, new Vector3(0f, 0.6f, 0f));

            var fire = NewSystem(root, "Fireball", VfxTexture.Smoke, true, 2.2f);
            Main(fire, 0.6f, R(0.25f, 0.5f), R(1f, 3f), R(0.5f, 1f), Color.white, maxParticles: 16);
            Burst(fire, 10, 14);
            Sphere(fire, 0.25f, 1f, true, new Vector3(0f, 0.15f, 0f));
            Drag(fire, 3f);
            SizeOverLife(fire, 1.3f, Curve(0f, 0.6f, 0.3f, 1f, 1f, 1f));
            ColorOverLife(fire, Grad(
                new[] { CK(0f, FireHot), CK(0.25f, FireMid), CK(0.6f, FireDeep), CK(1f, new Color(0.25f, 0.05f, 0.02f)) },
                new[] { AK(0f, 1f), AK(0.25f, 0.9f), AK(0.6f, 0.45f), AK(1f, 0f) }));
            Noise(fire, 0.6f, 1.2f);
            Spin(fire, -60f, 60f);

            var shock = NewSystem(root, "GroundShock", VfxTexture.Smoke, false);
            Main(shock, 1f, R(0.5f, 0.9f), R(8f, 11f), R(0.35f, 0.6f), Alpha(new Color(0.55f, 0.5f, 0.46f), 0.45f), Alpha(FloorDust, 0.4f), maxParticles: 40);
            Burst(shock, 28, 34);
            FlatCircle(shock, 0.3f, 0.2f);
            Velocity(shock, new Vector3(0f, 0.1f, 0f), new Vector3(0f, 0.5f, 0f));
            Drag(shock, 3.2f); // v0 / drag ~ 3 m reach
            SizeOverLife(shock, 2.2f, Expand(0.4f));
            ColorOverLife(shock, Fade(0.05f, 0.45f));
            Noise(shock, 0.3f, 0.9f);

            var heat = NewSystem(root, "HeatFront", VfxTexture.Ring, true, 0.8f, ParticleSystemRenderMode.HorizontalBillboard);
            Main(heat, 0.4f, 0.3f, 0f, 0.5f, C(1f, 0.7f, 0.42f, 0.25f), maxParticles: 1);
            Burst(heat, 1, 1);
            Sphere(heat, 0.001f, 1f, false, new Vector3(0f, 0.03f, 0f));
            SizeOverLife(heat, 12f, Curve(0f, 0.05f, 0.4f, 0.75f, 1f, 1f));
            ColorOverLife(heat, FadeOut(0.3f));

            var embers = NewSystem(root, "Embers", VfxTexture.Soft, true, 2.5f);
            Main(embers, 1.4f, R(0.6f, 1.3f), R(3f, 8f), R(0.015f, 0.035f), C(1f, 0.72f, 0.3f), C(1f, 0.45f, 0.12f), gravity: 0.35f, maxParticles: 40);
            Burst(embers, 26, 34);
            ConeUp(embers, 70f, 0.15f);
            Drag(embers, 1.2f);
            Stretch(embers, 0.02f, 1f);
            FloorCollision(embers, 0.2f, 0.5f);
            ColorOverLife(embers, FadeOut(0.5f));

            var smoke = NewSystem(root, "SmokeResidue", VfxTexture.Smoke, false);
            Main(smoke, 1f, R(1.2f, 2f), R(0.4f, 1.2f), R(0.6f, 1f), Alpha(Soot, 0.5f), Alpha(new Color(0.34f, 0.32f, 0.31f), 0.45f), gravity: -0.06f, maxParticles: 12);
            Delay(smoke, 0.1f);
            Burst(smoke, 6, 9);
            Sphere(smoke, 0.4f, 1f, true);
            Drag(smoke, 1.5f);
            SizeOverLife(smoke, 1.8f, Expand(0.5f));
            ColorOverLife(smoke, Fade(0.15f, 0.5f));
            Noise(smoke, 0.4f, 0.7f);
            Spin(smoke, -20f, 20f);
        }

        /// <summary>Looping trail of a fire-infused fastball: flame licks, heat smoke, embers and a flickering light.</summary>
        private static void FireTrail(GameObject root, VfxDefinition def)
        {
            def.Looping = true;
            def.DefaultLoopDuration = 2f;
            def.MaxInstances = 6;
            def.LightLooping = true;
            def.LightFlicker = 0.25f;
            AddLight(root, new Color(1f, 0.58f, 0.28f), 2400f, 20000f, 4f);

            var flames = NewSystem(root, "Flames", VfxTexture.Smoke, true, 2f);
            Main(flames, 1f, R(0.12f, 0.28f), R(0f, 0.4f), R(0.18f, 0.3f), Color.white, loop: true, maxParticles: 200);
            Rate(flames, 20f, 28f);
            Sphere(flames, 0.08f);
            SizeOverLife(flames, 1f, Shrink(0.25f));
            ColorOverLife(flames, Grad(
                new[] { CK(0f, FireHot), CK(0.35f, FireMid), CK(1f, FireDeep) },
                new[] { AK(0f, 0.95f), AK(0.5f, 0.6f), AK(1f, 0f) }));
            Noise(flames, 0.5f, 2f, 0.8f);
            Spin(flames, -90f, 90f);

            var smoke = NewSystem(root, "HeatSmoke", VfxTexture.Smoke, false);
            Main(smoke, 1f, R(0.5f, 0.9f), 0f, R(0.15f, 0.25f), Alpha(Soot, 0.3f), gravity: -0.08f, loop: true, maxParticles: 60);
            Rate(smoke, 0f, 6f);
            Sphere(smoke, 0.06f);
            SizeOverLife(smoke, 2.2f, Expand(0.4f));
            ColorOverLife(smoke, Fade(0.15f, 0.4f));
            Noise(smoke, 0.3f, 1f);

            var embers = NewSystem(root, "Embers", VfxTexture.Soft, true, 2.5f);
            Main(embers, 1f, R(0.3f, 0.7f), R(0.5f, 2f), R(0.012f, 0.025f), C(1f, 0.68f, 0.26f), gravity: 0.2f, loop: true, maxParticles: 40);
            Rate(embers, 30f);
            Sphere(embers, 0.1f);
            Stretch(embers, 0.03f, 1f);
            ColorOverLife(embers, FadeOut(0.4f));
        }

        /// <summary>Looping soft cyan beam: a tapering light ribbon along the flight path, core glow, crackling motes.</summary>
        private static void BeamTrail(GameObject root, VfxDefinition def)
        {
            def.Looping = true;
            def.DefaultLoopDuration = 2f;
            def.MaxInstances = 4;
            def.LightLooping = true;
            def.LightFlicker = 0.1f;
            AddLight(root, new Color(0.55f, 0.86f, 1f), 9000f, 30000f, 5f);

            var ribbon = NewSystem(root, "Ribbon", VfxTexture.Soft, true, 2.5f);
            Main(ribbon, 1f, 0.3f, 0f, 0.16f, Alpha(BeamCyan, 0.9f), loop: true, maxParticles: 120, randomRotation: false);
            Rate(ribbon, 30f, 20f);
            SizeOverLife(ribbon, 1f, Shrink(0f));
            ColorOverLife(ribbon, FadeOut(0.2f));
            Ribbon(ribbon, VfxTexture.Soft, true, 2.5f, Linear(0f, 1f, 1f, 0.2f));

            var core = NewSystem(root, "CoreGlow", VfxTexture.Soft, true, 3f);
            Main(core, 1f, 0.1f, 0f, 0.45f, Alpha(new Color(0.8f, 0.97f, 1f), 0.8f), loop: true, maxParticles: 8, randomRotation: false);
            LocalSpace(core);
            Rate(core, 30f);
            ColorOverLife(core, Fade(0.2f, 0.6f));

            var sparks = NewSystem(root, "Motes", VfxTexture.Soft, true, 2f);
            Main(sparks, 1f, R(0.15f, 0.35f), R(1f, 3f), R(0.012f, 0.025f), C(0.7f, 0.95f, 1f), loop: true, maxParticles: 40, randomRotation: false);
            Rate(sparks, 40f);
            Sphere(sparks, 0.1f);
            Drag(sparks, 3f);
            Stretch(sparks, 0.03f, 1f);
            ColorOverLife(sparks, FadeOut(0.3f));
        }

        /// <summary>Beam-ball impact: cyan flash, ripple ring, radial sparks, cool vapour, light flash.</summary>
        private static void BeamImpact(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 6;
            def.LightFlashDuration = 0.25f;
            AddLight(root, new Color(0.55f, 0.86f, 1f), 9000f, 60000f, 6f);

            var flash = NewSystem(root, "Flash", VfxTexture.Soft, true, 3f);
            Main(flash, 0.2f, 0.12f, 0f, 1f, C(0.8f, 0.96f, 1f, 0.9f), maxParticles: 1, randomRotation: false);
            Burst(flash, 1, 1);
            SizeOverLife(flash, 1.2f, Curve(0f, 0.5f, 1f, 1f));
            ColorOverLife(flash, FadeOut(0.15f));

            var ring = NewSystem(root, "Ripple", VfxTexture.Ring, true, 2f);
            Main(ring, 0.3f, 0.25f, 0f, 0.3f, Alpha(BeamCyan, 0.8f), maxParticles: 1, randomRotation: false);
            Burst(ring, 1, 1);
            SizeOverLife(ring, 5f, Curve(0f, 0.2f, 0.4f, 0.8f, 1f, 1f));
            ColorOverLife(ring, FadeOut(0.25f));

            var sparks = NewSystem(root, "Sparks", VfxTexture.Soft, true, 2.2f);
            Main(sparks, 0.4f, R(0.15f, 0.35f), R(6f, 12f), R(0.015f, 0.03f), C(0.75f, 0.95f, 1f), C(0.5f, 0.85f, 1f), maxParticles: 30, randomRotation: false);
            Burst(sparks, 20, 26);
            ConeUp(sparks, 80f, 0.05f);
            Drag(sparks, 4f);
            Stretch(sparks, 0.03f, 1f);
            ColorOverLife(sparks, FadeOut(0.3f));

            var mist = NewSystem(root, "Vapour", VfxTexture.Smoke, false);
            Main(mist, 0.8f, R(0.5f, 0.8f), R(0.5f, 1.5f), R(0.2f, 0.35f), Alpha(new Color(0.75f, 0.9f, 1f), 0.25f), maxParticles: 8);
            Burst(mist, 5, 7);
            Sphere(mist, 0.12f, 1f, true);
            Drag(mist, 3f);
            SizeOverLife(mist, 2f, Expand(0.4f));
            ColorOverLife(mist, Fade(0.1f, 0.4f));
        }

        // =====================================================================================================
        // Shadow / Gale / Houdini (smoke & light tricks)
        // =====================================================================================================

        /// <summary>Clone appears: inky smoke gathering around a human-sized volume, low smoke ring, violet glints.</summary>
        private static void CloneSpawn(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 8;

            var smoke = NewSystem(root, "InkSmoke", VfxTexture.Smoke, false);
            Main(smoke, 1f, R(0.6f, 1f), R(0.3f, 1f), R(0.3f, 0.55f), Alpha(ShadowInk, 0.55f), Alpha(ShadowViolet, 0.45f), gravity: -0.05f, maxParticles: 24);
            Burst(smoke, 14, 18);
            Box(smoke, new Vector3(0.6f, 1.7f, 0.4f), new Vector3(0f, 0.85f, 0f));
            Scatter(smoke, 1f);
            Drag(smoke, 1.5f);
            SizeOverLife(smoke, 1.8f, Expand(0.5f));
            ColorOverLife(smoke, Fade(0.12f, 0.45f));
            Noise(smoke, 0.5f, 1f);
            Spin(smoke, -30f, 30f);

            var ring = NewSystem(root, "FloorRing", VfxTexture.Smoke, false);
            Main(ring, 0.8f, R(0.5f, 0.8f), R(1.5f, 2.5f), R(0.2f, 0.35f), Alpha(ShadowInk, 0.45f), maxParticles: 14);
            Burst(ring, 10, 12);
            FlatCircle(ring, 0.2f, 0.4f);
            Drag(ring, 3.5f);
            SizeOverLife(ring, 2f, Expand(0.4f));
            ColorOverLife(ring, Fade(0.08f, 0.4f));

            var glints = NewSystem(root, "Glints", VfxTexture.Soft, true, 1.2f);
            Main(glints, 0.8f, R(0.3f, 0.6f), R(0.2f, 0.6f), R(0.02f, 0.04f), Alpha(new Color(0.55f, 0.45f, 0.9f), 0.8f), maxParticles: 16, randomRotation: false);
            Burst(glints, 10, 14);
            Box(glints, new Vector3(0.6f, 1.7f, 0.4f), new Vector3(0f, 0.85f, 0f));
            Scatter(glints, 1f);
            ColorOverLife(glints, Twinkle());
        }

        /// <summary>Clone popped by a ball: rising ash flakes, inky smoke, faint violet pop.</summary>
        private static void CloneDissolve(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 8;

            var ash = NewSystem(root, "Ash", VfxTexture.Debris, false);
            Main(ash, 1.4f, R(0.8f, 1.4f), 0f, R(0.015f, 0.035f), Alpha(ShadowInk, 0.9f), Alpha(ShadowViolet, 0.8f), maxParticles: 56);
            Burst(ash, 40, 50);
            Box(ash, new Vector3(0.5f, 1.7f, 0.3f), new Vector3(0f, 0.85f, 0f));
            Velocity(ash, new Vector3(-0.2f, 0.4f, -0.2f), new Vector3(0.2f, 1.1f, 0.2f));
            Noise(ash, 0.8f, 1.2f, 0.5f);
            Spin(ash, -200f, 200f);
            ColorOverLife(ash, Fade(0.05f, 0.5f));

            var smoke = NewSystem(root, "Smoke", VfxTexture.Smoke, false);
            Main(smoke, 1f, R(0.6f, 1f), R(0.2f, 0.8f), R(0.3f, 0.5f), Alpha(ShadowInk, 0.45f), gravity: -0.04f, maxParticles: 14);
            Burst(smoke, 10, 12);
            Box(smoke, new Vector3(0.5f, 1.7f, 0.3f), new Vector3(0f, 0.85f, 0f));
            Scatter(smoke, 1f);
            Drag(smoke, 2f);
            SizeOverLife(smoke, 1.8f, Expand(0.5f));
            ColorOverLife(smoke, Fade(0.08f, 0.4f));
            Noise(smoke, 0.4f, 1f);

            var pop = NewSystem(root, "Pop", VfxTexture.Soft, true, 1.2f);
            Main(pop, 0.2f, 0.08f, 0f, 1.4f, Alpha(MagicViolet, 0.35f), maxParticles: 1, randomRotation: false);
            Burst(pop, 1, 1);
            Sphere(pop, 0.001f, 1f, false, new Vector3(0f, 1f, 0f));
            ColorOverLife(pop, FadeOut(0.1f));
        }

        /// <summary>Optical camouflage engaging: refractive sparkles over the body and faint vertical light streaks.</summary>
        private static void CloakShimmer(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 6;

            var sparkles = NewSystem(root, "Sparkles", VfxTexture.Soft, true, 1.2f);
            Main(sparkles, 0.7f, R(0.3f, 0.6f), 0f, R(0.012f, 0.03f), Alpha(new Color(0.82f, 0.95f, 1f), 0.6f), maxParticles: 36, randomRotation: false);
            Burst(sparkles, 26, 32);
            Box(sparkles, new Vector3(0.55f, 1.75f, 0.35f), new Vector3(0f, 0.9f, 0f));
            Velocity(sparkles, new Vector3(0f, 0.2f, 0f), new Vector3(0f, 0.5f, 0f));
            ColorOverLife(sparkles, Twinkle());

            var streaks = NewSystem(root, "Streaks", VfxTexture.Soft, true, 1f);
            Main(streaks, 0.5f, R(0.25f, 0.4f), 0f, 0.04f, Alpha(new Color(0.8f, 0.92f, 1f), 0.15f), maxParticles: 10, randomRotation: false);
            Burst(streaks, 7, 9);
            Box(streaks, new Vector3(0.5f, 1.2f, 0.3f), new Vector3(0f, 0.6f, 0f));
            Velocity(streaks, new Vector3(0f, 1.5f, 0f), new Vector3(0f, 2.5f, 0f));
            Stretch(streaks, 0.12f, 3f);
            ColorOverLife(streaks, Fade(0.15f, 0.4f));

            var wisps = NewSystem(root, "Distortion", VfxTexture.Smoke, true, 0.6f);
            Main(wisps, 0.6f, 0.5f, 0f, R(0.4f, 0.6f), Alpha(new Color(0.7f, 0.85f, 1f), 0.08f), maxParticles: 8);
            Burst(wisps, 5, 6);
            Box(wisps, new Vector3(0.4f, 1.5f, 0.25f), new Vector3(0f, 0.9f, 0f));
            ColorOverLife(wisps, Fade(0.2f, 0.5f));
        }

        /// <summary>Smoke-bomb style teleport: grey-violet smoke burst at body height, floor ring, brief inner flash.</summary>
        private static void TeleportPoof(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 8;
            def.Prewarm = 2;

            var smoke = NewSystem(root, "Smoke", VfxTexture.Smoke, false);
            Main(smoke, 1f, R(0.7f, 1.2f), R(0.8f, 2f), R(0.3f, 0.5f), Alpha(new Color(0.55f, 0.53f, 0.56f), 0.6f), Alpha(new Color(0.35f, 0.33f, 0.38f), 0.5f),
                gravity: -0.04f, maxParticles: 24);
            Burst(smoke, 16, 20);
            Sphere(smoke, 0.35f, 1f, false, new Vector3(0f, 0.9f, 0f));
            Drag(smoke, 3.5f);
            SizeOverLife(smoke, 2f, Expand(0.4f));
            ColorOverLife(smoke, Fade(0.06f, 0.4f));
            Noise(smoke, 0.5f, 1f);
            Spin(smoke, -40f, 40f);

            var ring = NewSystem(root, "FloorRing", VfxTexture.Smoke, false);
            Main(ring, 0.8f, R(0.4f, 0.7f), R(2f, 3f), R(0.15f, 0.3f), Alpha(new Color(0.6f, 0.58f, 0.6f), 0.4f), maxParticles: 14);
            Burst(ring, 10, 12);
            FlatCircle(ring, 0.25f, 0.4f);
            Drag(ring, 4f);
            SizeOverLife(ring, 2.2f, Expand(0.4f));
            ColorOverLife(ring, Fade(0.06f, 0.4f));

            var flash = NewSystem(root, "Flash", VfxTexture.Soft, true, 1.2f);
            Main(flash, 0.2f, 0.08f, 0f, 1.2f, Alpha(new Color(0.85f, 0.8f, 1f), 0.5f), maxParticles: 1, randomRotation: false);
            Burst(flash, 1, 1);
            Sphere(flash, 0.001f, 1f, false, new Vector3(0f, 0.9f, 0f));
            ColorOverLife(flash, FadeOut(0.1f));
        }

        /// <summary>Two-way position swap marker: brief violet light column, swirling sparkles, puff of smoke.</summary>
        private static void SwapFlash(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 6;

            var column = NewSystem(root, "Column", VfxTexture.Soft, true, 1.6f, ParticleSystemRenderMode.VerticalBillboard);
            Main(column, 0.3f, 0.18f, 0f, 1f, Alpha(MagicViolet, 0.7f), maxParticles: 1, randomRotation: false);
            Size3D(column, 0.8f, 2.2f);
            Burst(column, 1, 1);
            Sphere(column, 0.001f, 1f, false, new Vector3(0f, 1f, 0f));
            SizeOverLife(column, 1f, Linear(0f, 1f, 1f, 0.3f));
            ColorOverLife(column, FadeOut(0.2f));

            var swirl = NewSystem(root, "Swirl", VfxTexture.Soft, true, 1.4f);
            Main(swirl, 0.8f, R(0.4f, 0.7f), 0f, R(0.02f, 0.04f), C(0.82f, 0.72f, 1f), maxParticles: 32, randomRotation: false);
            LocalSpace(swirl);
            Burst(swirl, 24, 30);
            FlatCircle(swirl, 0.45f, 0.2f);
            Velocity(swirl, new Vector3(0f, 1f, 0f), new Vector3(0f, 2f, 0f), false, 4f);
            ColorOverLife(swirl, FadeOut(0.4f));

            var smoke = NewSystem(root, "Smoke", VfxTexture.Smoke, false);
            Main(smoke, 0.8f, R(0.5f, 0.9f), R(0.5f, 1.2f), R(0.25f, 0.4f), Alpha(new Color(0.45f, 0.4f, 0.55f), 0.4f), gravity: -0.03f, maxParticles: 10);
            Burst(smoke, 7, 9);
            Sphere(smoke, 0.3f, 1f, false, new Vector3(0f, 0.9f, 0f));
            Drag(smoke, 3f);
            SizeOverLife(smoke, 1.8f, Expand(0.45f));
            ColorOverLife(smoke, Fade(0.08f, 0.4f));
        }

        /// <summary>Stage-magic vanish: dense white-grey smoke bomb and a little gold glitter.</summary>
        private static void VanishSmoke(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 8;

            var smoke = NewSystem(root, "SmokeBomb", VfxTexture.Smoke, false);
            Main(smoke, 1.5f, R(1.2f, 2.2f), R(1f, 2.8f), R(0.45f, 0.8f), Alpha(StageSmoke, 0.75f), Alpha(new Color(0.65f, 0.64f, 0.68f), 0.65f),
                gravity: -0.05f, maxParticles: 32);
            Burst(smoke, 22, 28);
            Sphere(smoke, 0.4f, 1f, false, new Vector3(0f, 0.6f, 0f));
            Drag(smoke, 2.5f);
            SizeOverLife(smoke, 2f, Expand(0.4f));
            ColorOverLife(smoke, Fade(0.04f, 0.5f));
            Noise(smoke, 0.6f, 0.8f);
            Spin(smoke, -30f, 30f);

            var glitter = NewSystem(root, "Glitter", VfxTexture.Soft, true, 1.6f);
            Main(glitter, 1f, R(0.6f, 1.2f), R(0.5f, 2f), R(0.01f, 0.02f), C(1f, 0.86f, 0.52f), gravity: 0.1f, maxParticles: 20, randomRotation: false);
            Burst(glitter, 14, 18);
            Sphere(glitter, 0.35f, 1f, false, new Vector3(0f, 0.8f, 0f));
            Drag(glitter, 1.5f);
            ColorOverLife(glitter, Twinkle());
        }

        // =====================================================================================================
        // Bear / Gouki / Screws
        // =====================================================================================================

        /// <summary>
        /// Looping 5 m magnetic field (hemisphere centred on the root, attach at the feet): faint field-line streaks flowing
        /// inwards, boundary shimmer on the floor and inward "pull" pulses.
        /// </summary>
        private static void MagneticField(GameObject root, VfxDefinition def)
        {
            def.Looping = true;
            def.DefaultLoopDuration = 1.5f;
            def.MaxInstances = 4;

            var lines = NewSystem(root, "FieldLines", VfxTexture.Soft, true, 0.9f);
            Main(lines, 1f, R(1.3f, 1.5f), -3.6f, R(0.02f, 0.035f), Alpha(new Color(0.6f, 0.78f, 1f), 0.35f), loop: true, maxParticles: 160, randomRotation: false);
            Rate(lines, 70f);
            Sphere(lines, 5f, 0f, true);
            Stretch(lines, 0.08f, 1.5f);
            ColorOverLife(lines, Fade(0.2f, 0.75f));

            var boundary = NewSystem(root, "Boundary", VfxTexture.Soft, true, 0.8f);
            Main(boundary, 1f, R(0.6f, 1f), 0f, R(0.03f, 0.05f), Alpha(new Color(0.55f, 0.75f, 1f), 0.45f), loop: true, maxParticles: 80, randomRotation: false);
            Rate(boundary, 50f);
            FlatCircle(boundary, 5f, 0f);
            Velocity(boundary, new Vector3(0f, 0.1f, 0f), new Vector3(0f, 0.35f, 0f));
            ColorOverLife(boundary, Fade(0.2f, 0.5f));

            var pulse = NewSystem(root, "PullPulse", VfxTexture.Ring, true, 0.8f, ParticleSystemRenderMode.HorizontalBillboard);
            Main(pulse, 1f, 1.2f, 0f, 10f, Alpha(new Color(0.5f, 0.7f, 1f), 0.15f), loop: true, maxParticles: 4, randomRotation: false);
            LocalSpace(pulse);
            Rate(pulse, 1.2f);
            Sphere(pulse, 0.001f, 1f, false, new Vector3(0f, 0.03f, 0f));
            SizeOverLife(pulse, 1f, Curve(0f, 1f, 1f, 0.15f));
            ColorOverLife(pulse, Fade(0.2f, 0.7f));
        }

        /// <summary>Energy barrier struck (+Y = barrier normal): ripple ring, crackle along the surface, flash, small light.</summary>
        private static void ShieldImpact(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 8;
            def.Prewarm = 2;
            def.LightFlashDuration = 0.2f;
            AddLight(root, new Color(0.5f, 0.8f, 1f), 8000f, 15000f, 4f);

            var ripple = NewSystem(root, "Ripple", VfxTexture.Ring, true, 1.8f);
            Main(ripple, 0.4f, 0.3f, 0f, 0.3f, C(0.45f, 0.8f, 1f, 0.85f), maxParticles: 1, randomRotation: false);
            Burst(ripple, 1, 1);
            SizeOverLife(ripple, 5f, Curve(0f, 0.2f, 0.4f, 0.8f, 1f, 1f));
            ColorOverLife(ripple, FadeOut(0.25f));

            var crackle = NewSystem(root, "Crackle", VfxTexture.Soft, true, 2f);
            Main(crackle, 0.4f, R(0.15f, 0.35f), R(2f, 4.5f), R(0.012f, 0.025f), C(0.65f, 0.92f, 1f), maxParticles: 24, randomRotation: false);
            Burst(crackle, 16, 22);
            FlatCircle(crackle, 0.05f, 1f);
            Drag(crackle, 3f);
            Stretch(crackle, 0.04f, 1f);
            ColorOverLife(crackle, FadeOut(0.3f));

            var flash = NewSystem(root, "Flash", VfxTexture.Soft, true, 2f);
            Main(flash, 0.2f, 0.08f, 0f, 0.6f, C(0.75f, 0.93f, 1f, 0.8f), maxParticles: 1, randomRotation: false);
            Burst(flash, 1, 1);
            ColorOverLife(flash, FadeOut(0.1f));
        }

        /// <summary>Shoulder charge (+Z = charge direction): floor dust kicked backwards and sideways, debris.</summary>
        private static void TackleDust(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 6;

            var kick = NewSystem(root, "KickUp", VfxTexture.Smoke, false);
            Main(kick, 1f, R(0.5f, 1f), R(1.5f, 3.5f), R(0.15f, 0.3f), Alpha(GymDust, 0.45f), Alpha(FloorDust, 0.4f), gravity: 0.05f, maxParticles: 26);
            Burst(kick, 16, 22);
            Cone(kick, 35f, 0.3f, new Vector3(-30f, 180f, 0f)); // backwards and up
            Drag(kick, 3.5f);
            SizeOverLife(kick, 2.4f, Expand(0.4f));
            ColorOverLife(kick, Fade(0.06f, 0.4f));
            Noise(kick, 0.3f, 1f);

            var spread = NewSystem(root, "Spread", VfxTexture.Smoke, false);
            Main(spread, 0.8f, R(0.4f, 0.8f), R(1.5f, 2.5f), R(0.12f, 0.22f), Alpha(GymDust, 0.35f), maxParticles: 12);
            Burst(spread, 8, 10);
            FlatCircle(spread, 0.3f, 0.5f);
            Drag(spread, 4f);
            SizeOverLife(spread, 2.2f, Expand(0.4f));
            ColorOverLife(spread, Fade(0.06f, 0.4f));

            var debris = NewSystem(root, "Debris", VfxTexture.Debris, false);
            Main(debris, 1f, R(0.4f, 0.8f), R(2f, 4f), R(0.01f, 0.022f), C(0.75f, 0.72f, 0.66f), C(0.5f, 0.47f, 0.43f), gravity: 1f, maxParticles: 12);
            Burst(debris, 8, 10);
            Cone(debris, 30f, 0.2f, new Vector3(-35f, 180f, 0f));
            FloorCollision(debris, 0.3f, 0.4f);
            Spin(debris, -300f, 300f);
            ColorOverLife(debris, FadeOut(0.7f));
        }

        /// <summary>
        /// Ground slam (reach ~8 m at scale 1): fast primary dust wave, slower secondary wave, a pressure ring on the floor,
        /// thrown debris chunks and a heavy central cloud.
        /// </summary>
        private static void EarthquakeRing(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 3;

            var wave = NewSystem(root, "PrimaryWave", VfxTexture.Smoke, false);
            Main(wave, 1.3f, R(0.8f, 1.3f), R(14f, 18f), R(0.5f, 0.9f), Alpha(new Color(0.5f, 0.46f, 0.42f), 0.5f), Alpha(FloorDust, 0.45f), maxParticles: 70);
            Burst(wave, 50, 60);
            FlatCircle(wave, 0.6f, 0.2f);
            Velocity(wave, new Vector3(0f, 0.1f, 0f), new Vector3(0f, 0.5f, 0f));
            Drag(wave, 2f); // ~8 m reach
            SizeOverLife(wave, 2.2f, Expand(0.4f));
            ColorOverLife(wave, Fade(0.04f, 0.45f));
            Noise(wave, 0.4f, 0.6f);
            Spin(wave, -25f, 25f);

            var second = NewSystem(root, "SecondaryWave", VfxTexture.Smoke, false);
            Main(second, 1.5f, R(1.2f, 1.8f), R(6f, 9f), R(0.6f, 1.1f), Alpha(FloorDust, 0.4f), maxParticles: 36);
            Delay(second, 0.12f);
            Burst(second, 26, 32);
            FlatCircle(second, 0.5f, 0.3f);
            Drag(second, 2.2f);
            SizeOverLife(second, 2f, Expand(0.45f));
            ColorOverLife(second, Fade(0.08f, 0.5f));
            Noise(second, 0.4f, 0.5f);

            var front = NewSystem(root, "PressureRing", VfxTexture.Ring, false, 1f, ParticleSystemRenderMode.HorizontalBillboard);
            Main(front, 0.6f, 0.5f, 0f, 1f, Alpha(new Color(0.45f, 0.42f, 0.38f), 0.35f), maxParticles: 1);
            Burst(front, 1, 1);
            Sphere(front, 0.001f, 1f, false, new Vector3(0f, 0.03f, 0f));
            SizeOverLife(front, 16f, Curve(0f, 0.05f, 0.5f, 0.8f, 1f, 1f));
            ColorOverLife(front, FadeOut(0.3f));

            var chunks = NewSystem(root, "Chunks", VfxTexture.Debris, false);
            Main(chunks, 1.4f, R(0.7f, 1.3f), R(3f, 7f), R(0.03f, 0.07f), C(0.35f, 0.3f, 0.26f), C(0.55f, 0.5f, 0.45f), gravity: 1.4f, maxParticles: 44);
            Burst(chunks, 30, 40);
            ConeUp(chunks, 55f, 1f);
            FloorCollision(chunks, 0.25f, 0.45f);
            Spin(chunks, -300f, 300f);
            ColorOverLife(chunks, FadeOut(0.75f));

            var cloud = NewSystem(root, "CentralCloud", VfxTexture.Smoke, false);
            Main(cloud, 1f, R(1f, 1.6f), R(0.5f, 1.5f), R(0.8f, 1.3f), Alpha(new Color(0.52f, 0.48f, 0.44f), 0.5f), gravity: -0.03f, maxParticles: 12);
            Burst(cloud, 8, 10);
            Sphere(cloud, 0.5f, 1f, true);
            Drag(cloud, 1.8f);
            SizeOverLife(cloud, 1.8f, Expand(0.5f));
            ColorOverLife(cloud, Fade(0.08f, 0.5f));
            Noise(cloud, 0.35f, 0.6f);
        }

        /// <summary>
        /// Viscous glue ball bursting: glossy droplets that stick to the floor, heavy goo blobs, and a glossy puddle that
        /// spreads quickly and lasts the spec's 4 s (diameter ~2 m at scale 1, i.e. scale = puddle radius in metres).
        /// </summary>
        private static void GlueSplat(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 6;

            var droplets = NewSystem(root, "Droplets", VfxTexture.Droplet, false);
            Main(droplets, 1f, R(0.5f, 0.9f), R(1.8f, 4f), R(0.03f, 0.07f), Alpha(Glue, 0.95f), Alpha(GlueDark, 0.95f), gravity: 1.6f, maxParticles: 30, randomRotation: false);
            Burst(droplets, 18, 24);
            ConeUp(droplets, 65f, 0.08f);
            Stretch(droplets, 0.04f, 1.2f);
            FloorCollision(droplets, 0f, 0.9f, 0.6f);
            ColorOverLife(droplets, FadeOut(0.8f));

            var blobs = NewSystem(root, "Blobs", VfxTexture.Droplet, false);
            Main(blobs, 0.8f, R(0.35f, 0.6f), R(0.6f, 1.5f), R(0.1f, 0.2f), Alpha(Glue, 0.95f), Alpha(GlueDark, 0.9f), gravity: 1.2f, maxParticles: 10);
            Burst(blobs, 6, 8);
            ConeUp(blobs, 40f, 0.06f);
            SizeOverLife(blobs, 1f, Curve(0f, 0.6f, 0.3f, 1f, 1f, 0.7f));
            ColorOverLife(blobs, FadeOut(0.75f));

            var puddle = NewSystem(root, "Puddle", VfxTexture.Droplet, false, 1f, ParticleSystemRenderMode.HorizontalBillboard);
            Main(puddle, 0.1f, 4f, 0f, 2f, Alpha(new Color(0.3f, 0.62f, 0.1f), 0.82f), maxParticles: 1);
            Burst(puddle, 1, 1);
            Sphere(puddle, 0.001f, 1f, false, new Vector3(0f, 0.012f, 0f));
            SizeOverLife(puddle, 1f, Curve(0f, 0.3f, 0.06f, 1f, 1f, 1f));
            ColorOverLife(puddle, Grad(new[] { CK(0f, Color.white), CK(1f, new Color(0.85f, 0.9f, 0.85f)) },
                new[] { AK(0f, 0f), AK(0.02f, 1f), AK(0.85f, 1f), AK(1f, 0f) }));
            SortBias(puddle, 10f);

            var satellites = NewSystem(root, "Satellites", VfxTexture.Droplet, false, 1f, ParticleSystemRenderMode.HorizontalBillboard);
            Main(satellites, 0.1f, R(3.6f, 4f), 0f, R(0.3f, 0.6f), Alpha(new Color(0.32f, 0.64f, 0.1f), 0.8f), maxParticles: 5);
            Burst(satellites, 3, 5);
            FlatCircle(satellites, 0.9f, 0.3f, new Vector3(0f, 0.013f, 0f));
            SizeOverLife(satellites, 1f, Curve(0f, 0.3f, 0.08f, 1f, 1f, 1f));
            ColorOverLife(satellites, Grad(new[] { CK(0f, Color.white), CK(1f, Color.white) },
                new[] { AK(0f, 0f), AK(0.03f, 1f), AK(0.85f, 1f), AK(1f, 0f) }));
            SortBias(satellites, 9f);
        }

        /// <summary>Pneumatic launcher shot (+Z = barrel direction): compressed-air puff, tiny status-LED glint.</summary>
        private static void TurretMuzzle(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 8;

            var puff = NewSystem(root, "AirPuff", VfxTexture.Smoke, false);
            Main(puff, 0.5f, R(0.25f, 0.45f), R(2f, 4.5f), R(0.06f, 0.12f), Alpha(new Color(0.9f, 0.9f, 0.92f), 0.4f), maxParticles: 10);
            Burst(puff, 6, 8);
            ConeForward(puff, 18f, 0.03f);
            Drag(puff, 8f);
            SizeOverLife(puff, 2.5f, Expand(0.35f));
            ColorOverLife(puff, Fade(0.05f, 0.35f));

            var glint = NewSystem(root, "Glint", VfxTexture.Soft, true, 1.5f);
            Main(glint, 0.2f, 0.05f, 0f, 0.12f, Alpha(new Color(0.6f, 0.85f, 1f), 0.35f), maxParticles: 1, randomRotation: false);
            Burst(glint, 1, 1);
            ColorOverLife(glint, FadeOut(0.2f));
        }

        // =====================================================================================================
        // Elsa (ice)
        // =====================================================================================================

        /// <summary>Freezing ball impact: ice shards that bounce and settle, sinking cold mist, glitter, cold light flash.</summary>
        private static void IceBurst(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 6;
            def.LightFlashDuration = 0.2f;
            AddLight(root, new Color(0.72f, 0.88f, 1f), 7500f, 15000f, 4f);

            var shards = NewSystem(root, "Shards", VfxTexture.Shard, false);
            Main(shards, 1f, R(0.5f, 0.9f), R(2.5f, 6f), R(0.03f, 0.08f), Alpha(Frost, 0.95f), Alpha(IceBlue, 0.9f), gravity: 1.2f, maxParticles: 30);
            Burst(shards, 20, 26);
            Sphere(shards, 0.1f);
            FloorCollision(shards, 0.35f, 0.3f);
            Spin(shards, -540f, 540f);
            ColorOverLife(shards, FadeOut(0.75f));

            var mist = NewSystem(root, "ColdMist", VfxTexture.Smoke, false);
            Main(mist, 1.2f, R(0.9f, 1.6f), R(0.4f, 1.4f), R(0.25f, 0.45f), Alpha(Frost, 0.45f), gravity: 0.04f, maxParticles: 18);
            Burst(mist, 12, 16);
            Sphere(mist, 0.15f);
            Drag(mist, 2f);
            SizeOverLife(mist, 2.2f, Expand(0.4f));
            ColorOverLife(mist, Fade(0.06f, 0.45f));
            Noise(mist, 0.3f, 0.8f);
            Spin(mist, -20f, 20f);

            var glitter = NewSystem(root, "Glitter", VfxTexture.Soft, true, 1.5f);
            Main(glitter, 1f, R(0.4f, 0.9f), R(0.5f, 2.5f), R(0.008f, 0.018f), C(0.82f, 0.95f, 1f), gravity: 0.1f, maxParticles: 24, randomRotation: false);
            Burst(glitter, 18, 22);
            Sphere(glitter, 0.2f);
            Drag(glitter, 2f);
            ColorOverLife(glitter, Twinkle());
        }

        /// <summary>Looping frost trail behind a thrown ball: sinking cold vapour, glitter and tiny falling ice crystals.</summary>
        private static void IceTrail(GameObject root, VfxDefinition def)
        {
            def.Looping = true;
            def.DefaultLoopDuration = 2f;
            def.MaxInstances = 8;

            var vapour = NewSystem(root, "Vapour", VfxTexture.Smoke, false);
            Main(vapour, 1f, R(0.5f, 0.9f), 0f, R(0.1f, 0.18f), Alpha(Frost, 0.35f), gravity: 0.03f, loop: true, maxParticles: 90);
            Rate(vapour, 8f, 10f);
            Sphere(vapour, 0.06f);
            SizeOverLife(vapour, 2.4f, Expand(0.4f));
            ColorOverLife(vapour, Fade(0.1f, 0.4f));
            Noise(vapour, 0.2f, 1f);

            var glitter = NewSystem(root, "Glitter", VfxTexture.Soft, true, 1.4f);
            Main(glitter, 1f, R(0.3f, 0.7f), R(0.2f, 0.8f), R(0.01f, 0.02f), C(0.82f, 0.95f, 1f), gravity: 0.25f, loop: true, maxParticles: 60, randomRotation: false);
            Rate(glitter, 0f, 12f);
            Sphere(glitter, 0.08f);
            ColorOverLife(glitter, Twinkle());

            var crystals = NewSystem(root, "Crystals", VfxTexture.Shard, false);
            Main(crystals, 1f, R(0.4f, 0.7f), R(0.5f, 1.5f), R(0.015f, 0.03f), Alpha(Frost, 0.9f), gravity: 1f, loop: true, maxParticles: 24);
            Rate(crystals, 0f, 4f);
            Sphere(crystals, 0.08f);
            Spin(crystals, -360f, 360f);
            ColorOverLife(crystals, FadeOut(0.6f));
        }

        /// <summary>Looping cold vapour rolling off a frozen player (attach at the feet), ground mist and ice glints.</summary>
        private static void FrozenMist(GameObject root, VfxDefinition def)
        {
            def.Looping = true;
            def.DefaultLoopDuration = 2.5f;
            def.MaxInstances = 6;

            var vapour = NewSystem(root, "Vapour", VfxTexture.Smoke, false);
            Main(vapour, 1f, R(0.9f, 1.5f), R(0f, 0.15f), R(0.2f, 0.4f), Alpha(Frost, 0.3f), gravity: 0.03f, loop: true, maxParticles: 40);
            Rate(vapour, 14f);
            Box(vapour, new Vector3(0.55f, 1.7f, 0.35f), new Vector3(0f, 0.85f, 0f));
            Scatter(vapour, 1f);
            SizeOverLife(vapour, 1.8f, Expand(0.5f));
            ColorOverLife(vapour, Fade(0.2f, 0.5f));
            Noise(vapour, 0.2f, 0.8f);
            Spin(vapour, -15f, 15f);

            var ground = NewSystem(root, "GroundMist", VfxTexture.Smoke, false);
            Main(ground, 1f, R(1.5f, 2.2f), R(0.2f, 0.5f), R(0.3f, 0.5f), Alpha(Frost, 0.25f), loop: true, maxParticles: 20);
            Rate(ground, 6f);
            FlatCircle(ground, 0.4f, 0.5f);
            Drag(ground, 0.8f);
            SizeOverLife(ground, 2f, Expand(0.5f));
            ColorOverLife(ground, Fade(0.2f, 0.5f));

            var glints = NewSystem(root, "Glints", VfxTexture.Soft, true, 1.3f);
            Main(glints, 1f, R(0.2f, 0.5f), 0f, R(0.008f, 0.02f), C(0.86f, 0.96f, 1f), loop: true, maxParticles: 16, randomRotation: false);
            Rate(glints, 10f);
            Box(glints, new Vector3(0.5f, 1.7f, 0.3f), new Vector3(0f, 0.85f, 0f));
            ColorOverLife(glints, Twinkle());
        }

        // =====================================================================================================
        // Specter / Chrono (time)
        // =====================================================================================================

        /// <summary>Evasive dodge (+Z = dodge direction): faint air streaks left behind and a scuff of floor dust.</summary>
        private static void DodgeAfterimage(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 6;

            var streaks = NewSystem(root, "AirStreaks", VfxTexture.Soft, true, 1f);
            Main(streaks, 0.4f, R(0.2f, 0.35f), 0f, R(0.02f, 0.04f), Alpha(new Color(0.75f, 0.9f, 1f), 0.25f), maxParticles: 16, randomRotation: false);
            Burst(streaks, 10, 14);
            Box(streaks, new Vector3(0.5f, 1.6f, 0.2f), new Vector3(0f, 0.9f, 0f));
            Velocity(streaks, new Vector3(0f, 0f, -3f), new Vector3(0f, 0f, -1f), false);
            Stretch(streaks, 0.1f, 3f);
            ColorOverLife(streaks, FadeOut(0.2f));

            var dust = NewSystem(root, "Scuff", VfxTexture.Smoke, false);
            Main(dust, 0.6f, R(0.35f, 0.6f), R(0.8f, 1.6f), R(0.08f, 0.14f), Alpha(GymDust, 0.25f), maxParticles: 8);
            Burst(dust, 5, 7);
            FlatCircle(dust, 0.2f, 0.5f);
            Drag(dust, 4f);
            SizeOverLife(dust, 2.2f, Expand(0.4f));
            ColorOverLife(dust, Fade(0.08f, 0.4f));
        }

        /// <summary>Looping time trail on a rewinding body (attach at the feet): blue/gold motes plus a soft light ribbon.</summary>
        private static void RewindTrail(GameObject root, VfxDefinition def)
        {
            def.Looping = true;
            def.DefaultLoopDuration = 1.5f;
            def.MaxInstances = 6;

            var motes = NewSystem(root, "Motes", VfxTexture.Soft, true, 1.5f);
            Main(motes, 1f, R(0.5f, 0.9f), R(0.05f, 0.2f), R(0.015f, 0.035f), Alpha(TimeBlue, 0.6f), Alpha(TimeGold, 0.5f), loop: true, maxParticles: 80, randomRotation: false);
            Rate(motes, 16f, 18f);
            Box(motes, new Vector3(0.4f, 1.6f, 0.3f), new Vector3(0f, 0.9f, 0f));
            Scatter(motes, 1f);
            SizeOverLife(motes, 1f, Shrink(0f));
            ColorOverLife(motes, Fade(0.1f, 0.5f));
            Noise(motes, 0.2f, 1f);

            var ribbon = NewSystem(root, "Ribbon", VfxTexture.Soft, true, 1.6f);
            Main(ribbon, 1f, 0.45f, 0f, 0.08f, Alpha(TimeBlue, 0.45f), loop: true, maxParticles: 60, randomRotation: false);
            Rate(ribbon, 8f, 12f);
            Sphere(ribbon, 0.01f, 1f, false, new Vector3(0f, 1f, 0f));
            SizeOverLife(ribbon, 1f, Shrink(0.1f));
            ColorOverLife(ribbon, FadeOut(0.2f));
            Ribbon(ribbon, VfxTexture.Soft, true, 1.6f, Linear(0f, 1f, 1f, 0.1f));

            var haze = NewSystem(root, "Haze", VfxTexture.Smoke, true, 0.6f);
            Main(haze, 1f, 0.6f, 0f, R(0.5f, 0.8f), Alpha(new Color(0.5f, 0.75f, 1f), 0.06f), loop: true, maxParticles: 20);
            Rate(haze, 2f, 4f);
            Sphere(haze, 0.2f, 1f, false, new Vector3(0f, 0.9f, 0f));
            ColorOverLife(haze, Fade(0.2f, 0.5f));
        }

        /// <summary>Looping stasis bubble around a frozen ball (~0.8 m diameter): orbiting shell sparkles, rim haze, inner glow.</summary>
        private static void StasisBubble(GameObject root, VfxDefinition def)
        {
            def.Looping = true;
            def.DefaultLoopDuration = 2f;
            def.MaxInstances = 6;
            def.LightLooping = true;
            def.LightFlicker = 0.08f;
            AddLight(root, new Color(0.6f, 0.82f, 1f), 8000f, 6000f, 2.5f);

            var shell = NewSystem(root, "Shell", VfxTexture.Soft, true, 1.2f);
            Main(shell, 1f, R(0.5f, 0.9f), 0f, R(0.01f, 0.02f), Alpha(new Color(0.65f, 0.85f, 1f), 0.5f), loop: true, maxParticles: 60, randomRotation: false);
            LocalSpace(shell);
            Rate(shell, 40f);
            Sphere(shell, 0.38f, 0f);
            Velocity(shell, Vector3.zero, Vector3.zero, false, 1.5f);
            ColorOverLife(shell, Twinkle());

            var rim = NewSystem(root, "Rim", VfxTexture.Ring, true, 1f);
            Main(rim, 1f, 0.8f, 0f, 0.8f, Alpha(new Color(0.55f, 0.8f, 1f), 0.25f), loop: true, maxParticles: 4, randomRotation: true);
            LocalSpace(rim);
            Rate(rim, 2.5f);
            SizeOverLife(rim, 1f, Curve(0f, 0.92f, 0.5f, 1f, 1f, 0.96f));
            ColorOverLife(rim, Fade(0.25f, 0.6f));

            var glow = NewSystem(root, "InnerGlow", VfxTexture.Soft, true, 1f);
            Main(glow, 1f, 0.6f, 0f, 0.55f, Alpha(new Color(0.5f, 0.75f, 1f), 0.18f), loop: true, maxParticles: 4, randomRotation: false);
            LocalSpace(glow);
            Rate(glow, 4f);
            ColorOverLife(glow, Fade(0.3f, 0.6f));
        }

        /// <summary>Temporal Reset zone (5 x 5 m at scale 1, centred on the root): rising border motes, floor shimmer, falling motes.</summary>
        private static void TemporalZone(GameObject root, VfxDefinition def)
        {
            def.Looping = true;
            def.DefaultLoopDuration = 2f;
            def.MaxInstances = 3;

            var border = NewSystem(root, "Border", VfxTexture.Soft, true, 1.5f);
            Main(border, 1f, R(0.8f, 1.4f), 0f, R(0.015f, 0.03f), Alpha(TimeBlue, 0.55f), loop: true, maxParticles: 200, randomRotation: false);
            Rate(border, 110f);
            BoxEdges(border, new Vector3(5f, 0.02f, 5f), new Vector3(0f, 0.02f, 0f));
            Velocity(border, new Vector3(0f, 0.4f, 0f), new Vector3(0f, 1.2f, 0f));
            ColorOverLife(border, Fade(0.1f, 0.5f));

            var floor = NewSystem(root, "FloorShimmer", VfxTexture.Smoke, true, 0.6f, ParticleSystemRenderMode.HorizontalBillboard);
            Main(floor, 1f, R(0.8f, 1.2f), 0f, R(0.8f, 1.3f), Alpha(new Color(0.55f, 0.8f, 1f), 0.07f), loop: true, maxParticles: 30);
            Rate(floor, 18f);
            Box(floor, new Vector3(5f, 0.02f, 5f), new Vector3(0f, 0.03f, 0f));
            ColorOverLife(floor, Fade(0.3f, 0.6f));

            var falling = NewSystem(root, "Reversal", VfxTexture.Soft, true, 1.4f);
            Main(falling, 1f, R(0.8f, 1.4f), 0f, R(0.012f, 0.025f), Alpha(TimeGold, 0.5f), loop: true, maxParticles: 120, randomRotation: false);
            Rate(falling, 60f);
            Box(falling, new Vector3(5f, 2.5f, 5f), new Vector3(0f, 1.25f, 0f));
            Velocity(falling, new Vector3(0f, -1.2f, 0f), new Vector3(0f, -0.5f, 0f));
            ColorOverLife(falling, Fade(0.15f, 0.6f));
        }

        // =====================================================================================================
        // Match flow
        // =====================================================================================================

        /// <summary>Return to the infield: soft warm light column (~4.5 m), rising motes, floor ring, warm light.</summary>
        private static void ReviveBeam(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 6;
            def.LightFlashDuration = 1f;
            AddLight(root, WarmLight, 4500f, 30000f, 5f, new Vector3(0f, 1.2f, 0f));

            var column = NewSystem(root, "Column", VfxTexture.Soft, true, 1.6f, ParticleSystemRenderMode.VerticalBillboard);
            Main(column, 0.2f, 1.2f, 0f, 1f, Alpha(WarmLight, 0.55f), maxParticles: 1, randomRotation: false);
            Size3D(column, 0.9f, 4.5f);
            Burst(column, 1, 1);
            Sphere(column, 0.001f, 1f, false, new Vector3(0f, 2.2f, 0f));
            SizeOverLife(column, 1f, Curve(0f, 0.6f, 0.15f, 1f, 1f, 0.7f));
            ColorOverLife(column, Fade(0.1f, 0.5f));

            var motes = NewSystem(root, "RisingMotes", VfxTexture.Soft, true, 1.5f);
            Main(motes, 0.9f, R(0.6f, 1.1f), 0f, R(0.015f, 0.035f), C(1f, 0.93f, 0.75f), maxParticles: 60, randomRotation: false);
            Rate(motes, 45f);
            FlatCircle(motes, 0.45f, 0.3f);
            Velocity(motes, new Vector3(0f, 1.5f, 0f), new Vector3(0f, 3f, 0f));
            ColorOverLife(motes, Fade(0.1f, 0.5f));

            var ring = NewSystem(root, "FloorRing", VfxTexture.Ring, true, 1.4f, ParticleSystemRenderMode.HorizontalBillboard);
            Main(ring, 0.3f, 0.6f, 0f, 0.6f, Alpha(new Color(1f, 0.92f, 0.75f), 0.6f), maxParticles: 1);
            Burst(ring, 1, 1);
            Sphere(ring, 0.001f, 1f, false, new Vector3(0f, 0.03f, 0f));
            SizeOverLife(ring, 3.2f, Curve(0f, 0.3f, 0.5f, 0.85f, 1f, 1f));
            ColorOverLife(ring, FadeOut(0.3f));
        }

        /// <summary>Ultimate charged (tinted with the hero's theme colour): rising spiral motes, two floor pulses, body glow.</summary>
        private static void UltimateReady(GameObject root, VfxDefinition def)
        {
            def.MaxInstances = 6;

            var motes = NewSystem(root, "Spiral", VfxTexture.Soft, true, 1.5f);
            Main(motes, 1f, R(0.6f, 1f), 0f, R(0.02f, 0.04f), Alpha(Color.white, 0.85f), maxParticles: 60, randomRotation: false);
            LocalSpace(motes);
            Rate(motes, 50f);
            FlatCircle(motes, 0.5f, 0.1f);
            Velocity(motes, new Vector3(0f, 1.2f, 0f), new Vector3(0f, 2.4f, 0f), false, 2f);
            SizeOverLife(motes, 1f, Shrink(0.2f));
            ColorOverLife(motes, Fade(0.1f, 0.5f));

            var pulse = NewSystem(root, "FloorPulse", VfxTexture.Ring, true, 1.3f, ParticleSystemRenderMode.HorizontalBillboard);
            Main(pulse, 0.6f, 0.5f, 0f, 0.4f, Alpha(Color.white, 0.5f), maxParticles: 2);
            LocalSpace(pulse);
            Bursts(pulse, new ParticleSystem.Burst(0f, (short)1), new ParticleSystem.Burst(0.35f, (short)1));
            Sphere(pulse, 0.001f, 1f, false, new Vector3(0f, 0.03f, 0f));
            SizeOverLife(pulse, 3f, Curve(0f, 0.3f, 0.5f, 0.85f, 1f, 1f));
            ColorOverLife(pulse, FadeOut(0.25f));

            var glow = NewSystem(root, "BodyGlow", VfxTexture.Soft, true, 1f);
            Main(glow, 0.3f, 0.6f, 0f, 1.6f, Alpha(Color.white, 0.15f), maxParticles: 1, randomRotation: false);
            LocalSpace(glow);
            Burst(glow, 1, 1);
            Sphere(glow, 0.001f, 1f, false, new Vector3(0f, 1f, 0f));
            ColorOverLife(glow, Fade(0.2f, 0.4f));
        }

        // ------------------------------------------------------------------ helpers

        private static Color Alpha(Color c, float a) => new Color(c.r, c.g, c.b, a);
    }
}
