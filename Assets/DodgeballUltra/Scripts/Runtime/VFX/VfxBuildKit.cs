using System;
using System.Collections.Generic;
using DodgeballUltra.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace DodgeballUltra.VFX
{
    /// <summary>
    /// Small, explicit helper layer over the ParticleSystem module API used to author the procedural effect library.
    /// <para>Authoring conventions (every template follows them so gameplay code can spawn effects predictably):</para>
    /// <list type="bullet">
    /// <item>The effect root's local <b>+Y</b> is the surface normal / "up". Floor effects spawned with
    /// <c>Quaternion.identity</c> lie flat on the court; impacts are spawned with <c>FromToRotation(up, normal)</c>.</item>
    /// <item>Directional effects (throw wake, tackle dust, muzzle puff, dodge streaks) emit along local <b>+Z</b>.</item>
    /// <item>Systems use <see cref="ParticleSystemScalingMode.Shape"/>: the transform scale only scales emitter shapes;
    /// sizes, speeds, gravity, noise and velocity modules are scaled explicitly by the manager so a scaled effect is
    /// geometrically similar (same timing, proportionally larger).</item>
    /// <item>Colour lives in the particle colour (start colour x colour over lifetime) so the manager can tint any
    /// effect; materials stay neutral (white, or HDR white for additive energy so it blooms).</item>
    /// </list>
    /// </summary>
    public static class VfxBuildKit
    {
        /// <summary>Degrees to radians (the scripting API takes radians for particle rotation).</summary>
        public const float Deg = Mathf.Deg2Rad;

        /// <summary>Screen-space clamp for billboards. Large on purpose: realistic dust clouds can fill the view.</summary>
        private const float MaxParticleScreenSize = 8f;

        private static readonly Dictionary<string, Material> s_materials = new Dictionary<string, Material>(32);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_materials.Clear();

        // ------------------------------------------------------------------ materials

        /// <summary>
        /// Shared particle material for a texture/blend/intensity combination (cached). Additive materials take an HDR
        /// intensity so energy effects exceed 1.0 and feed the HDRP bloom; alpha-blended dust stays at 1.0.
        /// </summary>
        public static Material GetMaterial(VfxTexture texture, bool additive, float intensity = 1f)
        {
            if (!additive) intensity = 1f;
            string key = texture + (additive ? "_Add_" : "_Alpha_") + intensity.ToString("0.00");
            if (s_materials.TryGetValue(key, out var cached) && cached != null) return cached;

            var tint = new Color(intensity, intensity, intensity, 1f);
            var mat = MaterialFactory.CreateParticle("DU_VFX_" + key, tint, additive, VfxTextures.Get(texture));
            if (mat != null) mat.hideFlags = HideFlags.DontSave;
            s_materials[key] = mat;
            return mat;
        }

        // ------------------------------------------------------------------ hierarchy

        /// <summary>Creates an empty child GameObject.</summary>
        public static GameObject Child(GameObject parent, string name, Vector3 localPosition = default)
        {
            var go = new GameObject(name);
            go.layer = GameLayers.Visual;
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = localPosition;
            return go;
        }

        /// <summary>
        /// Creates a stopped, non-looping, world-space particle system child with neutral defaults (no emission, no shape)
        /// and a renderer set up for unlit, shadowless, soft particles.
        /// </summary>
        public static ParticleSystem NewSystem(GameObject parent, string name, VfxTexture texture, bool additive, float intensity = 1f,
            ParticleSystemRenderMode renderMode = ParticleSystemRenderMode.Billboard)
        {
            var go = Child(parent, name);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            main.emitterVelocityMode = ParticleSystemEmitterVelocityMode.Transform;
            main.maxParticles = 64;
            main.startColor = Color.white;
            main.startRotation = 0f;
            main.gravityModifier = 0f;
            main.stopAction = ParticleSystemStopAction.None;
            main.cullingMode = ParticleSystemCullingMode.Automatic;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.rateOverDistance = 0f;

            var shape = ps.shape;
            shape.enabled = false;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = renderMode;
            r.sharedMaterial = GetMaterial(texture, additive, intensity);
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            r.sortMode = additive ? ParticleSystemSortMode.None : ParticleSystemSortMode.Distance;
            r.minParticleSize = 0f;
            r.maxParticleSize = MaxParticleScreenSize;
            r.alignment = ParticleSystemRenderSpace.View;
            return ps;
        }

        // ------------------------------------------------------------------ main module

        /// <summary>Configures the main module. Colours: each particle picks a random colour between A and B.</summary>
        public static void Main(ParticleSystem ps, float duration, ParticleSystem.MinMaxCurve lifetime, ParticleSystem.MinMaxCurve speed,
            ParticleSystem.MinMaxCurve size, Color colorA, Color colorB, float gravity = 0f, bool loop = false, int maxParticles = 64,
            bool randomRotation = true)
        {
            var main = ps.main;
            main.duration = Mathf.Max(0.05f, duration);
            main.loop = loop;
            main.startLifetime = lifetime;
            main.startSpeed = speed;
            main.startSize = size;
            main.startColor = colorA == colorB ? new ParticleSystem.MinMaxGradient(colorA) : new ParticleSystem.MinMaxGradient(colorA, colorB);
            main.gravityModifier = gravity;
            main.maxParticles = Mathf.Max(1, maxParticles);
            main.startRotation = randomRotation ? new ParticleSystem.MinMaxCurve(0f, 360f * Deg) : new ParticleSystem.MinMaxCurve(0f);
        }

        /// <summary>Single-colour overload of <see cref="Main(ParticleSystem,float,ParticleSystem.MinMaxCurve,ParticleSystem.MinMaxCurve,ParticleSystem.MinMaxCurve,Color,Color,float,bool,int,bool)"/>.</summary>
        public static void Main(ParticleSystem ps, float duration, ParticleSystem.MinMaxCurve lifetime, ParticleSystem.MinMaxCurve speed,
            ParticleSystem.MinMaxCurve size, Color color, float gravity = 0f, bool loop = false, int maxParticles = 64, bool randomRotation = true) =>
            Main(ps, duration, lifetime, speed, size, color, color, gravity, loop, maxParticles, randomRotation);

        /// <summary>Non-uniform start size (e.g. tall light columns). Uses the 3D start size channel.</summary>
        public static void Size3D(ParticleSystem ps, float x, float y)
        {
            var main = ps.main;
            main.startSize3D = true;
            main.startSizeX = x;
            main.startSizeY = y;
            main.startSizeZ = 1f;
        }

        /// <summary>Simulate in the effect's local space (particles follow the emitter: bubbles, glows, orbiting motes).</summary>
        public static void LocalSpace(ParticleSystem ps)
        {
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
        }

        /// <summary>Delays the start of this sub-system (layered explosions).</summary>
        public static void Delay(ParticleSystem ps, float seconds)
        {
            var main = ps.main;
            main.startDelay = seconds;
        }

        // ------------------------------------------------------------------ emission

        /// <summary>One burst at <paramref name="time"/> with a random count in [min, max].</summary>
        public static void Burst(ParticleSystem ps, int min, int max, float time = 0f)
        {
            var emission = ps.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(time, (short)min, (short)max) });
        }

        /// <summary>Several bursts (e.g. repeated pulses).</summary>
        public static void Bursts(ParticleSystem ps, params ParticleSystem.Burst[] bursts)
        {
            var emission = ps.emission;
            emission.SetBursts(bursts);
        }

        /// <summary>Continuous emission per second and per metre travelled (trails).</summary>
        public static void Rate(ParticleSystem ps, float perSecond, float perMetre = 0f)
        {
            var emission = ps.emission;
            emission.rateOverTime = perSecond;
            emission.rateOverDistance = perMetre;
        }

        // ------------------------------------------------------------------ shapes (all oriented to the +Y convention)

        /// <summary>Cone opening around local +Y.</summary>
        public static void ConeUp(ParticleSystem ps, float angle, float radius, float radiusThickness = 1f, Vector3 position = default)
        {
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = angle;
            shape.radius = Mathf.Max(0.0001f, radius);
            shape.radiusThickness = radiusThickness;
            shape.arc = 360f;
            shape.position = position;
            shape.rotation = new Vector3(-90f, 0f, 0f); // cone axis +Z -> +Y
        }

        /// <summary>Cone opening around an arbitrary local direction expressed as Euler angles applied to +Z.</summary>
        public static void Cone(ParticleSystem ps, float angle, float radius, Vector3 eulerFromForward, Vector3 position = default)
        {
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = angle;
            shape.radius = Mathf.Max(0.0001f, radius);
            shape.radiusThickness = 1f;
            shape.arc = 360f;
            shape.position = position;
            shape.rotation = eulerFromForward;
        }

        /// <summary>Cone opening around local +Z (directional effects).</summary>
        public static void ConeForward(ParticleSystem ps, float angle, float radius, Vector3 position = default) =>
            Cone(ps, angle, radius, Vector3.zero, position);

        /// <summary>Sphere (or upper hemisphere) volume/shell. <paramref name="thickness"/> 0 = surface only.</summary>
        public static void Sphere(ParticleSystem ps, float radius, float thickness = 1f, bool hemisphereUp = false, Vector3 position = default)
        {
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = hemisphereUp ? ParticleSystemShapeType.Hemisphere : ParticleSystemShapeType.Sphere;
            shape.radius = Mathf.Max(0.0001f, radius);
            shape.radiusThickness = thickness;
            shape.position = position;
            shape.rotation = hemisphereUp ? new Vector3(-90f, 0f, 0f) : Vector3.zero; // hemisphere +Z -> +Y
        }

        /// <summary>Circle lying in the local XZ plane (floor). Particles move radially outwards along the floor.</summary>
        public static void FlatCircle(ParticleSystem ps, float radius, float thickness = 1f, Vector3 position = default)
        {
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = Mathf.Max(0.0001f, radius);
            shape.radiusThickness = thickness;
            shape.arc = 360f;
            shape.position = position;
            shape.rotation = new Vector3(-90f, 0f, 0f); // circle plane XY -> XZ
        }

        /// <summary>Box volume (e.g. a human-sized body volume centred at chest height).</summary>
        public static void Box(ParticleSystem ps, Vector3 size, Vector3 center)
        {
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = size;
            shape.position = center;
            shape.rotation = Vector3.zero;
        }

        /// <summary>Edges of a box (a 5x5 m zone outline when the height is ~0).</summary>
        public static void BoxEdges(ParticleSystem ps, Vector3 size, Vector3 center)
        {
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.BoxEdge;
            shape.scale = size;
            shape.position = center;
            shape.rotation = Vector3.zero;
        }

        /// <summary>Adds random direction jitter to the current shape (0 = shape direction, 1 = fully random).</summary>
        public static void Scatter(ParticleSystem ps, float randomDirection)
        {
            var shape = ps.shape;
            shape.randomDirectionAmount = Mathf.Clamp01(randomDirection);
        }

        // ------------------------------------------------------------------ over-lifetime modules

        public static void ColorOverLife(ParticleSystem ps, Gradient gradient)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(gradient);
        }

        /// <summary>Size multiplier over normalised lifetime: <paramref name="multiplier"/> x curve.</summary>
        public static void SizeOverLife(ParticleSystem ps, float multiplier, AnimationCurve curve)
        {
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(multiplier, curve);
        }

        /// <summary>Aerodynamic drag (1/s): decelerates particles exponentially (air resistance on dust and sparks).</summary>
        public static void Drag(ParticleSystem ps, float drag)
        {
            var lim = ps.limitVelocityOverLifetime;
            lim.enabled = true;
            lim.limit = 1000f; // never clamps: drag only
            lim.dampen = 0f;
            lim.drag = drag;
            lim.multiplyDragByParticleSize = false;
            lim.multiplyDragByParticleVelocity = false;
        }

        /// <summary>Curl-like turbulence (breaks up the regular look of dust and smoke).</summary>
        public static void Noise(ParticleSystem ps, float strength, float frequency, float scrollSpeed = 0.35f, int octaves = 2)
        {
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = strength;
            noise.frequency = frequency;
            noise.scrollSpeed = scrollSpeed;
            noise.damping = true;
            noise.octaveCount = Mathf.Clamp(octaves, 1, 4);
            noise.quality = ParticleSystemNoiseQuality.Medium;
        }

        /// <summary>Random angular velocity (deg/s) around the view axis.</summary>
        public static void Spin(ParticleSystem ps, float minDegPerSec, float maxDegPerSec)
        {
            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(minDegPerSec * Deg, maxDegPerSec * Deg);
        }

        /// <summary>
        /// Constant velocity over lifetime (random between min and max per axis) plus optional orbital/radial velocity.
        /// All seven curves are written in the same (two-constants) mode, as Unity requires.
        /// </summary>
        public static void Velocity(ParticleSystem ps, Vector3 min, Vector3 max, bool worldSpace = true, float orbitalY = 0f, float radial = 0f)
        {
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = worldSpace ? ParticleSystemSimulationSpace.World : ParticleSystemSimulationSpace.Local;
            vel.x = new ParticleSystem.MinMaxCurve(min.x, max.x);
            vel.y = new ParticleSystem.MinMaxCurve(min.y, max.y);
            vel.z = new ParticleSystem.MinMaxCurve(min.z, max.z);
            vel.orbitalX = new ParticleSystem.MinMaxCurve(0f, 0f);
            vel.orbitalY = new ParticleSystem.MinMaxCurve(orbitalY, orbitalY);
            vel.orbitalZ = new ParticleSystem.MinMaxCurve(0f, 0f);
            vel.radial = new ParticleSystem.MinMaxCurve(radial, radial);
            vel.speedModifier = 1f;
        }

        /// <summary>Collides particles with the court (floor/walls) so debris, droplets and shards bounce and settle.</summary>
        public static void FloorCollision(ParticleSystem ps, float bounce, float dampen, float lifetimeLoss = 0f, float radiusScale = 0.5f)
        {
            var col = ps.collision;
            col.enabled = true;
            col.type = ParticleSystemCollisionType.World;
            col.mode = ParticleSystemCollisionMode.Collision3D;
            col.collidesWith = GameLayers.CourtMask | 1; // court + Default-layer props
            col.bounce = bounce;
            col.dampen = dampen;
            col.lifetimeLoss = lifetimeLoss;
            col.radiusScale = radiusScale;
            col.quality = ParticleSystemCollisionQuality.Medium;
            col.enableDynamicColliders = false;
            col.sendCollisionMessages = false;
        }

        /// <summary>Velocity-stretched billboards (sparks, streaks, droplets).</summary>
        public static void Stretch(ParticleSystem ps, float velocityScale, float lengthScale)
        {
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.velocityScale = velocityScale;
            r.lengthScale = lengthScale;
            r.cameraVelocityScale = 0f;
        }

        /// <summary>Changes the render mode (HorizontalBillboard = lies on the floor, VerticalBillboard = upright column).</summary>
        public static void RenderAs(ParticleSystem ps, ParticleSystemRenderMode mode)
        {
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = mode;
        }

        /// <summary>Draw order bias for alpha-blended layers (positive = drawn earlier / behind).</summary>
        public static void SortBias(ParticleSystem ps, float fudge)
        {
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.sortingFudge = fudge;
        }

        /// <summary>
        /// Connects the particles into a single ribbon (beams, time trails). The ribbon width follows the particle size,
        /// its colour the particle colour (so it tapers and fades with the particles).
        /// </summary>
        public static void Ribbon(ParticleSystem ps, VfxTexture texture, bool additive, float intensity, AnimationCurve widthOverTrail)
        {
            var trails = ps.trails;
            trails.enabled = true;
            trails.mode = ParticleSystemTrailMode.Ribbon;
            trails.ribbonCount = 1;
            trails.worldSpace = true;
            trails.dieWithParticles = true;
            trails.inheritParticleColor = true;
            trails.sizeAffectsWidth = true;
            trails.textureMode = ParticleSystemTrailTextureMode.Stretch;
            trails.widthOverTrail = new ParticleSystem.MinMaxCurve(1f, widthOverTrail);
            trails.minVertexDistance = 0.04f;
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.trailMaterial = GetMaterial(texture, additive, intensity);
        }

        // ------------------------------------------------------------------ lights

        /// <summary>
        /// Adds a disabled point light (the manager animates it). Physical intensity is configured by the active render
        /// pipeline hook (HDRP: lumen + colour temperature); the built-in fallback maps lumen to a plausible intensity.
        /// </summary>
        public static Light AddLight(GameObject root, Color filter, float kelvin, float lumen, float range, Vector3 localPosition = default)
        {
            var go = Child(root, "Light", localPosition);
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = filter;
            light.range = range;
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.Auto;
            light.intensity = Mathf.Clamp(lumen / 4000f, 0.3f, 8f); // built-in fallback (no physical units)
            var hooks = RuntimeRenderingHooks.Active;
            if (hooks != null)
            {
                try
                {
                    hooks.ConfigureLight(light, lumen, kelvin);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
            light.enabled = false;
            return light;
        }

        // ------------------------------------------------------------------ data helpers

        public static Color C(float r, float g, float b, float a = 1f) => new Color(r, g, b, a);

        public static ParticleSystem.MinMaxCurve R(float min, float max) => new ParticleSystem.MinMaxCurve(min, max);

        public static GradientColorKey CK(float time, Color color) => new GradientColorKey(color, time);

        public static GradientAlphaKey AK(float time, float alpha) => new GradientAlphaKey(alpha, time);

        public static Gradient Grad(GradientColorKey[] colors, GradientAlphaKey[] alphas)
        {
            var g = new Gradient();
            g.SetKeys(colors, alphas);
            return g;
        }

        /// <summary>White with an alpha envelope: fade in until <paramref name="fadeIn"/>, hold, fade out from <paramref name="fadeOutStart"/>.</summary>
        public static Gradient Fade(float fadeIn = 0.08f, float fadeOutStart = 0.45f)
        {
            return Grad(
                new[] { CK(0f, Color.white), CK(1f, Color.white) },
                new[] { AK(0f, 0f), AK(Mathf.Clamp(fadeIn, 0.001f, 0.98f), 1f), AK(Mathf.Clamp(fadeOutStart, fadeIn + 0.01f, 0.99f), 1f), AK(1f, 0f) });
        }

        /// <summary>White with a sharp start and an eased fade-out (flashes, sparks).</summary>
        public static Gradient FadeOut(float holdUntil = 0.1f)
        {
            return Grad(
                new[] { CK(0f, Color.white), CK(1f, Color.white) },
                new[] { AK(0f, 1f), AK(Mathf.Clamp(holdUntil, 0.001f, 0.9f), 1f), AK(1f, 0f) });
        }

        /// <summary>Twinkling alpha envelope (ice glitter, sparkles).</summary>
        public static Gradient Twinkle()
        {
            return Grad(
                new[] { CK(0f, Color.white), CK(1f, Color.white) },
                new[] { AK(0f, 0f), AK(0.1f, 1f), AK(0.25f, 0.3f), AK(0.4f, 1f), AK(0.6f, 0.25f), AK(0.75f, 0.8f), AK(1f, 0f) });
        }

        /// <summary>Animation curve from (time, value) pairs with smooth (flat) tangents.</summary>
        public static AnimationCurve Curve(params float[] timeValuePairs)
        {
            int n = timeValuePairs.Length / 2;
            var keys = new Keyframe[n];
            for (int i = 0; i < n; i++) keys[i] = new Keyframe(timeValuePairs[i * 2], timeValuePairs[i * 2 + 1]);
            return new AnimationCurve(keys);
        }

        /// <summary>Animation curve from (time, value) pairs with linear segments.</summary>
        public static AnimationCurve Linear(params float[] timeValuePairs)
        {
            int n = timeValuePairs.Length / 2;
            var keys = new Keyframe[n];
            for (int i = 0; i < n; i++) keys[i] = new Keyframe(timeValuePairs[i * 2], timeValuePairs[i * 2 + 1]);
            for (int i = 0; i < n; i++)
            {
                float inT = 0f, outT = 0f;
                if (i > 0) inT = (keys[i].value - keys[i - 1].value) / Mathf.Max(1e-5f, keys[i].time - keys[i - 1].time);
                if (i < n - 1) outT = (keys[i + 1].value - keys[i].value) / Mathf.Max(1e-5f, keys[i + 1].time - keys[i].time);
                if (i == 0) inT = outT;
                if (i == n - 1) outT = inT;
                keys[i].inTangent = inT;
                keys[i].outTangent = outT;
            }
            return new AnimationCurve(keys);
        }

        /// <summary>Typical growth of an expanding puff: quick initial expansion, slow spread.</summary>
        public static AnimationCurve Expand(float start = 0.4f) => Curve(0f, start, 0.25f, 0.8f, 1f, 1f);

        /// <summary>Shrinks to nothing (flames, motes, ribbon particles).</summary>
        public static AnimationCurve Shrink(float end = 0f) => Linear(0f, 1f, 1f, end);
    }
}
