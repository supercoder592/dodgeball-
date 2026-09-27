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
        /// <summary>Normal map strength to use on the material (the generator bakes its strength in, so 1).</summary>
        public float NormalStrength;
        /// <summary>Emission colour (sRGB, LDR); black for non-emissive surfaces. BaseColor doubles as emission mask.</summary>
        public Color EmissiveColor;
        /// <summary>Emission luminance in nits (HDRP) - 0 for non-emissive surfaces.</summary>
        public float EmissiveIntensity;
    }

    /// <summary>Lets the editor scene builder supply saved material assets instead of runtime materials.</summary>
    public interface IArenaMaterialProvider
    {
        Material GetMaterial(ArenaSurface surface);
    }

    /// <summary>
    /// CONTRACT (kernel) - builds a complete, realistic-looking indoor sports arena at runtime when the scene does not
    /// contain one (the editor ArenaSceneBuilder calls the same builder with an asset material provider and then bakes
    /// lighting): hardwood court with painted lines, run-off, walls, bleachers, floodlight rig, colliders on
    /// GameLayers.Court, and the Court component (18 x 9 m, outfield strips of Court.outfieldDepth behind each baseline).
    /// <para>Owner module: Arena.</para>
    /// </summary>
    /// <remarks>
    /// <para>Hierarchy: <c>DodgeballArena</c> (root, at the parent's pose = court centre) with <c>Court</c> (the
    /// <see cref="Match.Court"/> component), <c>Floor</c>, <c>Markings</c>, <c>Walls</c>, <c>Bleachers</c>, <c>Rigging</c>,
    /// <c>Roof</c>, <c>Colliders</c> and (optionally) <c>Lighting</c>. Geometry is merged into one mesh per material and
    /// group (few draw calls without runtime batching); UVs are in metres so the procedural textures tile at real scale.
    /// Everything is on <see cref="GameLayers.Court"/>. The editor builder may mark the hierarchy static and bake lighting.</para>
    /// <para>Wall positions: side barriers at x = ±(width/2 + runOff), end walls at z = ±(length/2 + outfieldDepth + runOff).</para>
    /// </remarks>
    public static class RuntimeArenaBuilder
    {
        /// <summary>Name of the root object created by <see cref="Build(Transform, IArenaMaterialProvider, bool)"/>.</summary>
        public const string RootName = "DodgeballArena";

        /// <summary>Luminous flux of one floodlight (lm). ~8 fixtures give ~1000 lux on the court with HDRP's spot reflector.</summary>
        public const float DefaultFloodlightLumen = 55000f;

        /// <summary>Colour temperature of the floodlights (K), typical of sports LED lighting.</summary>
        public const float DefaultFloodlightTemperatureK = 5600f;

        /// <summary>Fixed exposure (EV100) that suits the ~1000 lux court illuminance, for volume/exposure setups.</summary>
        public const float RecommendedExposureEV100 = 9.5f;

        /// <summary>Drop from the truss bottom chord to a floodlight housing's centre (yoke length), metres.</summary>
        public const float FloodlightHousingDrop = 0.45f;

        /// <summary>Distance from a housing's centre to its lens surface along the aim direction, metres.</summary>
        public const float FloodlightLensOffset = 0.115f;

        /// <summary>
        /// Builds the arena under a new root object and returns its Court. <paramref name="materials"/> null = runtime
        /// materials from MaterialFactory + ArenaTextureGenerator. <paramref name="createLights"/> false lets the editor
        /// builder create physically-based lighting itself.
        /// </summary>
        public static Match.Court Build(Transform parent = null, IArenaMaterialProvider materials = null, bool createLights = true)
            => Build(parent, materials, createLights, null);

        /// <summary>
        /// Builds the arena with explicit <paramref name="settings"/> (null = <see cref="ArenaBuildSettings.Default"/>).
        /// The settings are copied, so the caller's instance is never modified.
        /// </summary>
        public static Match.Court Build(Transform parent, IArenaMaterialProvider materials, bool createLights, ArenaBuildSettings settings)
        {
            ArenaBuildSettings s = (settings ?? ArenaBuildSettings.Default).Clone();
            s.Sanitize();
            var construction = new ArenaConstruction(s, materials, parent);
            return construction.Build(createLights);
        }

        /// <summary>Floodlight positions used by both builders (world space, relative to the court centre at origin).</summary>
        public static Vector3[] GetFloodlightPositions() => GetFloodlightPositions(null);

        /// <summary>Lens centres of all floodlights for <paramref name="settings"/> (null = defaults), relative to the court centre.</summary>
        public static Vector3[] GetFloodlightPositions(ArenaBuildSettings settings)
        {
            ArenaBuildSettings s = Sanitized(settings);
            int count = FloodlightCount(s);
            var result = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                GetFloodlightFrame(s, i, out Vector3 housing, out Vector3 aim);
                result[i] = housing + (aim - housing).normalized * FloodlightLensOffset;
            }
            return result;
        }

        /// <summary>Point on the court each floodlight is aimed at (same order as <see cref="GetFloodlightPositions()"/>).</summary>
        public static Vector3[] GetFloodlightAimPoints(ArenaBuildSettings settings = null)
        {
            ArenaBuildSettings s = Sanitized(settings);
            int count = FloodlightCount(s);
            var result = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                GetFloodlightFrame(s, i, out _, out Vector3 aim);
                result[i] = aim;
            }
            return result;
        }

        /// <summary>Rotation of floodlight <paramref name="index"/> (forward = beam direction).</summary>
        public static Quaternion GetFloodlightRotation(int index, ArenaBuildSettings settings = null)
        {
            ArenaBuildSettings s = Sanitized(settings);
            GetFloodlightFrame(s, Mathf.Clamp(index, 0, FloodlightCount(s) - 1), out Vector3 housing, out Vector3 aim);
            return Quaternion.LookRotation((aim - housing).normalized, Vector3.up);
        }

        /// <summary>Interior volume of the hall (relative to the court centre), for reflection probes / fog volumes.</summary>
        public static Bounds GetArenaBounds(ArenaBuildSettings settings = null)
        {
            ArenaBuildSettings s = Sanitized(settings);
            float h = s.ceilingHeight;
            return new Bounds(new Vector3(0f, h * 0.5f, 0f), new Vector3(s.OuterHalfX * 2f, h, s.InnerHalfZ * 2f));
        }

        // ------------------------------------------------------------------------------------------------------------
        // Shared layout maths (used by ArenaConstruction so fixtures and lights always agree)
        // ------------------------------------------------------------------------------------------------------------

        internal static int FloodlightCount(ArenaBuildSettings s) => s.floodlightsPerTruss * 2;

        /// <summary>
        /// Housing centre and aim point of floodlight <paramref name="index"/>. Order: all fixtures of the -X truss by
        /// ascending z, then the +X truss. Each fixture is cross-aimed past the centre line toward the far side.
        /// </summary>
        internal static void GetFloodlightFrame(ArenaBuildSettings s, int index, out Vector3 housingCentre, out Vector3 aimPoint)
        {
            int perTruss = s.floodlightsPerTruss;
            int side = index < perTruss ? -1 : 1;
            int i = index % perTruss;
            float span = s.PlayAreaHalfLength * 2f;
            float spacing = span / perTruss;
            float z = -s.PlayAreaHalfLength + spacing * (i + 0.5f);
            float x = side * s.trussOffset;
            housingCentre = new Vector3(x, s.trussHeight - FloodlightHousingDrop, z);
            float cross = Mathf.Min(s.floodlightCrossAim, s.HalfWidth * 0.5f);
            aimPoint = new Vector3(-side * cross, 0f, z * 0.85f);
        }

        private static ArenaBuildSettings Sanitized(ArenaBuildSettings settings)
        {
            ArenaBuildSettings s = (settings ?? ArenaBuildSettings.Default).Clone();
            s.Sanitize();
            return s;
        }
    }
}
