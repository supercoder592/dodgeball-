using System;
using UnityEngine;

namespace DodgeballUltra.Arena
{
    /// <summary>Every surface type of the arena; used to request materials and procedural textures.</summary>
    public enum ArenaSurface
    {
        CourtWood = 0,      // lacquered maple parquet (the playing surface)
        CourtLinePaint,     // painted boundary / centre lines
        OutfieldZone,       // tinted outfield strips
        RunOffFloor,        // sports vinyl around the court
        Wall,               // painted concrete block
        WallPadding,        // foam safety padding on the walls
        Bleachers,          // concrete / wood tiers
        Seats,              // plastic stadium seats
        Metal,              // truss, railings, light housings
        LightEmitter,       // floodlight lenses (emissive)
        Ceiling,
    }

    /// <summary>A generated PBR texture set (all textures are linear except BaseColor which is sRGB).</summary>
    public struct ArenaTextureSet
    {
        public Texture2D BaseColor;
        public Texture2D Normal;
        /// <summary>HDRP mask layout: R metallic, G AO, B detail mask, A smoothness.</summary>
        public Texture2D Mask;
        public Vector2 Tiling;      // UV repeats per metre of surface
        public float Smoothness;
        public float Metallic;
        public Color Tint;
    }

    /// <summary>Lets the editor scene builder supply saved material assets instead of runtime materials.</summary>
    public interface IArenaMaterialProvider
    {
        Material GetMaterial(ArenaSurface surface);
    }

    /// <summary>
    /// CONTRACT (kernel) - procedural, photo-plausible PBR textures (wood planks with grain and lacquer, sports vinyl,
    /// concrete block, padding fabric, brushed metal...). Deterministic (fixed seeds) so editor-saved PNGs match runtime.
    /// <para>Owner module: Arena.</para>
    /// </summary>
    public static class ArenaTextureGenerator
    {
        // IMPLEMENT: Arena module
        public static ArenaTextureSet Generate(ArenaSurface surface, int resolution = 1024) => throw new NotImplementedException();
    }

    /// <summary>
    /// CONTRACT (kernel) - builds a complete, realistic-looking indoor sports arena at runtime when the scene does not
    /// contain one (the editor ArenaSceneBuilder calls the same builder with an asset material provider and then bakes
    /// lighting): hardwood court with painted lines, run-off, walls, bleachers, floodlight rig, colliders on
    /// GameLayers.Court, and the Court component (18 x 9 m, outfield strips of Court.outfieldDepth behind each baseline).
    /// <para>Owner module: Arena.</para>
    /// </summary>
    public static class RuntimeArenaBuilder
    {
        // IMPLEMENT: Arena module
        /// <summary>
        /// Builds the arena under a new root object and returns its Court. <paramref name="materials"/> null = runtime
        /// materials from MaterialFactory + ArenaTextureGenerator. <paramref name="createLights"/> false lets the editor
        /// builder create physically-based lighting itself.
        /// </summary>
        public static Match.Court Build(Transform parent = null, IArenaMaterialProvider materials = null, bool createLights = true)
            => throw new NotImplementedException();

        /// <summary>Floodlight positions used by both builders (world space, relative to the court centre at origin).</summary>
        public static Vector3[] GetFloodlightPositions() => throw new NotImplementedException();
    }
}
