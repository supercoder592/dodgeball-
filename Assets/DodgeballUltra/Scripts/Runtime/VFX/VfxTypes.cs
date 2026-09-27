using System;
using UnityEngine;

namespace DodgeballUltra.VFX
{
    /// <summary>
    /// Replaces the procedural effect for <see cref="id"/> with an authored prefab (ParticleSystems, VFX Graph, meshes...).
    /// The prefab is instantiated once as a hidden template and pooled exactly like the procedural effects.
    /// </summary>
    [Serializable]
    public struct VfxPrefabOverride
    {
        [Tooltip("Effect to replace.")]
        public VfxId id;

        [Tooltip("Prefab spawned instead of the procedural effect. Its local +Y is treated as the surface normal.")]
        public GameObject prefab;

        [Tooltip("Seconds before a pooled instance is recycled when the prefab has no ParticleSystem to poll (0 = 2 s).")]
        [Min(0f)] public float lifetime;

        [Tooltip("Treat the prefab as a looping effect (plays until Stop / duration).")]
        public bool looping;
    }

    /// <summary>
    /// Metadata of one effect: its hidden template GameObject plus pooling and lighting behaviour. Produced by
    /// <see cref="ProceduralVfxLibrary"/> (or from a <see cref="VfxPrefabOverride"/>), consumed by <see cref="VfxManager"/>.
    /// </summary>
    public sealed class VfxDefinition
    {
        /// <summary>Effect identifier.</summary>
        public VfxId Id;

        /// <summary>Inactive template instantiated into the pool.</summary>
        public GameObject Template;

        /// <summary>Looping effects emit until stopped (or <see cref="DefaultLoopDuration"/> for non-attached plays).</summary>
        public bool Looping;

        /// <summary>Lifetime applied to looping effects spawned with <see cref="VfxManager.Play"/> (they have no owner to stop them).</summary>
        public float DefaultLoopDuration = 2f;

        /// <summary>Maximum simultaneous instances; the oldest one is recycled beyond this.</summary>
        public int MaxInstances = 8;

        /// <summary>Instances created at start-up so the first use does not instantiate.</summary>
        public int Prewarm = 1;

        /// <summary>Recycle time for override prefabs without particle systems (0 = poll particles only).</summary>
        public float FixedLifetime;

        /// <summary>
        /// True for procedural templates: scale is applied to particle size/speed/gravity/noise modules explicitly.
        /// False for prefab overrides: scale is applied through the transform only.
        /// </summary>
        public bool ScaleModules = true;

        /// <summary>Seconds of the light flash (0 = no flash light or a looping light).</summary>
        public float LightFlashDuration;

        /// <summary>The light stays on while the effect plays (fire, beams) and fades out after Stop.</summary>
        public bool LightLooping;

        /// <summary>0..1 flicker amount of the light (fire).</summary>
        public float LightFlicker;
    }

    /// <summary>Cached base values of one ParticleSystem so scale and tint can be re-applied without allocation.</summary>
    internal struct VfxSystemBase
    {
        public ParticleSystem System;
        public bool Size3D;
        public ParticleSystem.MinMaxCurve Size;
        public ParticleSystem.MinMaxCurve SizeX;
        public ParticleSystem.MinMaxCurve SizeY;
        public ParticleSystem.MinMaxCurve SizeZ;
        public ParticleSystem.MinMaxCurve Speed;
        public float Gravity;
        public bool HasVelocity;
        public float VelocityModifier;
        public bool HasLimit;
        public float Limit;
        public bool HasNoise;
        public float Noise;
        public ParticleSystem.MinMaxGradient Color;
    }

    /// <summary>One pooled, reusable instance of an effect.</summary>
    internal sealed class VfxInstance
    {
        public VfxDefinition Definition;
        public GameObject Go;
        public Transform Transform;
        public int GoId;
        public VfxSystemBase[] Systems;
        public Light Light;
        public float LightBaseIntensity;
        public float LightBaseRange;
        public Color LightBaseColor;
        public float LightSeed;

        /// <summary>Unique id of the current play (handles from earlier plays become stale).</summary>
        public int Serial;
        public bool Active;
        public bool Stopping;
        public bool Attached;
        public float StartTime;
        public float StopAt;     // scaled time at which a timed effect stops emitting (0 = never)
        public float StopTime;   // when Stop began
        public float Scale = 1f;
        public bool Tinted;
        public Color Tint = Color.white;
        public float LastAppliedScale = -1f;
        public bool LastAppliedTinted;
        public Color LastAppliedTint = Color.white;
    }
}
