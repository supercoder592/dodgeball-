using System;
using UnityEngine;

namespace DodgeballUltra.VFX
{
    /// <summary>Every effect the game can spawn. Built procedurally (ParticleSystem) by the VFX module, overridable by prefabs.</summary>
    public enum VfxId
    {
        HitImpact = 0,
        EliminationBurst,
        CatchPuff,
        PerfectCatchBurst,
        ThrowWhoosh,
        FloorImpactDust,
        SlideDust,
        LandingDust,
        FootstepDust,
        Shockwave,          // Rayne meteor AOE ring
        FireTrail,          // Rayne meteor trail (attached)
        BeamTrail,          // Rayne ultimate (attached)
        BeamImpact,
        CloneSpawn,         // Shadow
        CloneDissolve,
        CloakShimmer,       // Gale
        TeleportPoof,       // Gale / Houdini
        MagneticField,      // Bear (attached, looping)
        ShieldImpact,       // Bear Aegis
        TackleDust,         // Gouki
        EarthquakeRing,     // Gouki
        GlueSplat,          // Screws
        TurretMuzzle,       // Screws
        SwapFlash,          // Houdini
        VanishSmoke,        // Houdini
        IceBurst,           // Elsa
        IceTrail,           // Elsa frost trail (attached)
        FrozenMist,         // Elsa freeze (attached, looping)
        DodgeAfterimage,    // Specter
        RewindTrail,        // Specter / Chrono
        StasisBubble,       // Chrono (attached, looping)
        TemporalZone,       // Chrono ult area
        ReviveBeam,
        UltimateReady,
    }

    /// <summary>Handle to a spawned effect (for stopping looping/attached effects).</summary>
    public struct VfxHandle
    {
        public int Id;
        public GameObject Instance;
        public bool IsValid => Instance != null;
    }

    /// <summary>
    /// CONTRACT (kernel) - pooled particle effects. Realistic-leaning procedural effects (soft dust, sparks, heat distortion,
    /// ice shards) built from ParticleSystems with pipeline-appropriate materials. Prefab overrides can be assigned per id.
    /// Static helpers are null-safe so gameplay code never checks for the manager.
    /// <para>Owner module: VFX.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VfxManager : MonoBehaviour
    {
        public static VfxManager Instance { get; private set; }

        // IMPLEMENT: VFX module
        public VfxHandle Play(VfxId id, Vector3 position, Quaternion rotation, float scale = 1f, Color? tint = null) => throw new NotImplementedException();

        /// <summary>Attaches an effect to <paramref name="parent"/>. <paramref name="duration"/> &lt;= 0 keeps looping until Stop.</summary>
        public VfxHandle PlayAttached(VfxId id, Transform parent, Vector3 localOffset, float scale = 1f, Color? tint = null, float duration = 0f) => throw new NotImplementedException();

        public void Stop(VfxHandle handle, bool immediate = false) => throw new NotImplementedException();

        /// <summary>Null-safe shortcut.</summary>
        public static VfxHandle Spawn(VfxId id, Vector3 position, Quaternion rotation, float scale = 1f, Color? tint = null) =>
            Instance != null ? Instance.Play(id, position, rotation, scale, tint) : default;

        /// <summary>Null-safe shortcut.</summary>
        public static VfxHandle SpawnAttached(VfxId id, Transform parent, Vector3 localOffset, float scale = 1f, Color? tint = null, float duration = 0f) =>
            Instance != null ? Instance.PlayAttached(id, parent, localOffset, scale, tint, duration) : default;

        /// <summary>Null-safe shortcut.</summary>
        public static void StopEffect(VfxHandle handle, bool immediate = false)
        {
            if (Instance != null && handle.IsValid) Instance.Stop(handle, immediate);
        }
    }
}
