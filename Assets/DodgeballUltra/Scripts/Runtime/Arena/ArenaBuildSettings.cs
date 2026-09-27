using System;
using UnityEngine;

namespace DodgeballUltra.Arena
{
    /// <summary>
    /// Tunable dimensions and options of the arena produced by <see cref="RuntimeArenaBuilder"/>. Defaults describe a
    /// realistic indoor competition hall: 18 x 9 m maple court with 3 m outfield strips, 4 m vinyl run-off, padded walls,
    /// ten-row concrete bleachers with moulded seats on both long sides, and two floodlight trusses at 9.5 m.
    /// </summary>
    /// <remarks>
    /// Layout (court centre = builder origin, long axis = Z):
    /// <code>
    ///   side barriers / walls : x = ±(courtWidth/2 + runOff)
    ///   end walls             : z = ±(courtLength/2 + outfieldDepth + runOff)
    ///   bleachers             : behind the side barriers, rising away from the court
    /// </code>
    /// </remarks>
    [Serializable]
    public sealed class ArenaBuildSettings
    {
        [Header("Court (m)")]
        [Tooltip("Court length along Z: two 9 x 9 m halves.")]
        [Min(6f)] public float courtLength = 18f;
        [Tooltip("Court width along X.")]
        [Min(4f)] public float courtWidth = 9f;
        [Tooltip("Depth of each outfield strip behind the baselines.")]
        [Min(0f)] public float outfieldDepth = 3f;
        [Tooltip("Clear distance from the playing area (side lines / outfield end lines) to the padded walls.")]
        [Min(1f)] public float runOff = 4f;
        [Tooltip("Plain maple border around the playing area before the vinyl run-off starts.")]
        [Min(0f)] public float woodApron = 0.6f;
        [Tooltip("Painted line width (dodgeball / volleyball standard: 5 cm).")]
        [Range(0.03f, 0.1f)] public float lineWidth = 0.05f;
        [Tooltip("Radius of the centre circle (0 = none).")]
        [Min(0f)] public float centreCircleRadius = 1.5f;
        [Tooltip("Fill the centre circle with the stained outfield finish (logo area).")]
        public bool centreLogoDisc = true;

        [Header("Hall")]
        [Tooltip("Clear height to the roof deck.")]
        [Range(6f, 25f)] public float ceilingHeight = 12f;
        [Tooltip("Build the roof deck, roof beams and ducts.")]
        public bool includeCeiling = true;
        [Tooltip("Height of the padded band on the walls (standard 6 ft pads).")]
        [Range(1f, 3f)] public float wallPadHeight = 1.83f;
        [Tooltip("Gap between the floor and the bottom of the pads.")]
        [Range(0f, 0.3f)] public float wallPadBottom = 0.05f;
        [Tooltip("Foam + vinyl thickness of the pads.")]
        [Range(0.03f, 0.15f)] public float wallPadThickness = 0.06f;
        [Tooltip("Height of the padded concrete barrier in front of the side bleachers.")]
        [Range(0.8f, 2f)] public float sideBarrierHeight = 1.2f;
        [Tooltip("Thickness of the side barrier (concrete, without padding).")]
        [Range(0.1f, 0.5f)] public float sideBarrierThickness = 0.25f;
        [Tooltip("Steel double doors and fabric acoustic panels on the end walls.")]
        public bool includeEndWallDetails = true;

        [Header("Bleachers (long sides)")]
        public bool includeBleachers = true;
        [Range(1, 30)] public int bleacherRows = 10;
        [Tooltip("Tread depth of one row.")]
        [Range(0.7f, 1.2f)] public float rowDepth = 0.85f;
        [Tooltip("Rise between rows (sightlines over the row in front).")]
        [Range(0.2f, 0.6f)] public float rowRise = 0.40f;
        [Tooltip("Height of the first tread above the court floor.")]
        [Range(0.2f, 1.2f)] public float firstRowHeight = 0.40f;
        [Tooltip("Circulation walkway behind the top row.")]
        [Range(0.8f, 4f)] public float topWalkwayDepth = 2f;
        public bool includeSeats = true;
        [Tooltip("Seat spacing along a row.")]
        [Range(0.45f, 0.65f)] public float seatPitch = 0.5f;
        [Tooltip("Stair aisles per side (evenly spaced).")]
        [Range(0, 8)] public int aislesPerSide = 3;
        [Range(0.9f, 2f)] public float aisleWidth = 1.2f;

        [Header("Floodlight rig")]
        [Tooltip("Height of the truss bottom chord.")]
        [Range(5f, 20f)] public float trussHeight = 9.5f;
        [Tooltip("Lateral distance of each truss from the court's long centre axis.")]
        [Range(2f, 12f)] public float trussOffset = 7f;
        [Range(1, 8)] public int floodlightsPerTruss = 4;
        [Tooltip("Luminous flux of one LED floodlight (lm), handed to the pipeline hooks (HDRP physical light units).")]
        [Min(1000f)] public float floodlightLumen = RuntimeArenaBuilder.DefaultFloodlightLumen;
        [Tooltip("Correlated colour temperature of the floodlights (K). Sports LED: 5000-5700 K.")]
        [Range(2700f, 6500f)] public float floodlightTemperature = RuntimeArenaBuilder.DefaultFloodlightTemperatureK;
        [Tooltip("Outer cone angle of each floodlight (deg).")]
        [Range(20f, 120f)] public float floodlightSpotAngle = 70f;
        [Tooltip("Inner (full intensity) cone as a fraction of the outer angle.")]
        [Range(0f, 1f)] public float floodlightInnerAngleFraction = 0.6f;
        [Range(15f, 60f)] public float floodlightRange = 35f;
        [Tooltip("How far past the court centre line each floodlight aims (cross-aiming evens out vertical illuminance on players' faces).")]
        [Range(0f, 4f)] public float floodlightCrossAim = 1.5f;
        [Tooltip("How many floodlights (the most central ones) cast soft shadows; the rest light without shadows.")]
        [Range(0, 16)] public int shadowedFloodlights = 8;
        [Tooltip("Built-in / URP fallback intensity of one floodlight (non-physical units).")]
        [Range(0.1f, 8f)] public float fallbackFloodlightIntensity = 2.2f;
        [Tooltip("Soft overhead fill simulating light bounced off the roof and walls (lux, physical pipelines).")]
        [Min(0f)] public float fillIlluminanceLux = 150f;
        [Tooltip("Built-in / URP fallback intensity of the overhead fill.")]
        [Range(0f, 2f)] public float fallbackFillIntensity = 0.3f;
        [Tooltip("Realtime box-projected reflection probe for the Built-in / URP fallback (HDRP probes come from the environment volume hook).")]
        public bool createReflectionProbe = true;

        [Header("Textures")]
        [Tooltip("Resolution of the hardwood maps; other surfaces scale down proportionally.")]
        [Range(128, 4096)] public int textureResolution = 1024;

        /// <summary>A fresh instance with the spec defaults.</summary>
        public static ArenaBuildSettings Default => new ArenaBuildSettings();

        /// <summary>Shallow copy (all fields are values).</summary>
        public ArenaBuildSettings Clone() => (ArenaBuildSettings)MemberwiseClone();

        // ---- Derived layout -------------------------------------------------------------------------------------------

        public float HalfWidth => courtWidth * 0.5f;
        public float HalfLength => courtLength * 0.5f;
        /// <summary>Half length of court + both outfield strips.</summary>
        public float PlayAreaHalfLength => HalfLength + outfieldDepth;
        /// <summary>Inner face of the side barriers / side walls.</summary>
        public float InnerHalfX => HalfWidth + runOff;
        /// <summary>Inner face of the end walls.</summary>
        public float InnerHalfZ => PlayAreaHalfLength + runOff;
        /// <summary>Total depth of the bleacher rows (0 without bleachers).</summary>
        public float BleacherDepth => includeBleachers ? bleacherRows * rowDepth : 0f;
        /// <summary>Inner face of the building's side walls (behind the bleacher walkway, or the side walls themselves).</summary>
        public float OuterHalfX => includeBleachers ? InnerHalfX + sideBarrierThickness + BleacherDepth + topWalkwayDepth : InnerHalfX;
        /// <summary>Height of the top bleacher walkway.</summary>
        public float WalkwayHeight => firstRowHeight + bleacherRows * rowRise;

        /// <summary>Clamps inconsistent combinations (called by the builder on its private copy).</summary>
        public void Sanitize()
        {
            courtLength = Mathf.Max(6f, courtLength);
            courtWidth = Mathf.Max(4f, courtWidth);
            outfieldDepth = Mathf.Max(0f, outfieldDepth);
            runOff = Mathf.Max(1f, runOff);
            woodApron = Mathf.Clamp(woodApron, 0f, runOff - 0.5f);
            lineWidth = Mathf.Clamp(lineWidth, 0.03f, 0.1f);
            centreCircleRadius = Mathf.Clamp(centreCircleRadius, 0f, Mathf.Min(HalfWidth, HalfLength) - 0.5f);
            bleacherRows = Mathf.Clamp(bleacherRows, 1, 30);
            aislesPerSide = Mathf.Clamp(aislesPerSide, 0, 8);
            floodlightsPerTruss = Mathf.Clamp(floodlightsPerTruss, 1, 8);
            textureResolution = Mathf.Clamp(textureResolution, 128, 4096);
            wallPadHeight = Mathf.Clamp(wallPadHeight, 1f, 3f);
            sideBarrierHeight = Mathf.Clamp(sideBarrierHeight, 0.8f, 2f);
            float minCeiling = Mathf.Max(trussHeight + 1.5f, includeBleachers ? WalkwayHeight + 3f : 0f);
            ceilingHeight = Mathf.Max(ceilingHeight, minCeiling);
            trussOffset = Mathf.Clamp(trussOffset, 1f, InnerHalfX - 0.5f);
        }
    }
}
