using System.Collections.Generic;
using DodgeballUltra.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace DodgeballUltra.Arena
{
    /// <summary>
    /// One arena build (used by <see cref="RuntimeArenaBuilder.Build(Transform, IArenaMaterialProvider, bool, ArenaBuildSettings)"/>).
    /// Generates every architectural element into per-material <see cref="ArenaMeshBuilder"/>s grouped by area, creates
    /// the simplified collision boxes, the Court component and (optionally) the lighting rig.
    /// </summary>
    /// <remarks>
    /// All coordinates are local to the arena root (court centre at the origin, floor top at y = 0, long axis = Z).
    /// The root stays inactive while building so Court.Awake, reflection probes etc. see the finished arena.
    /// </remarks>
    internal sealed class ArenaConstruction
    {
        // ---- Physical dimensions (m) --------------------------------------------------------------------------------
        private const float MarkingsHeight = 0.005f;     // painted lines above the floor (depth-precision safe offset)
        private const float LogoHeight = 0.0025f;        // stained centre disc
        private const float WallThickness = 0.3f;        // concrete block walls
        private const float FloorColliderDepth = 0.5f;
        private const float TrussSize = 0.46f;           // 20.5" box truss (centre-line spacing of the chords)
        private const float ChordRadius = 0.024f;        // 48 mm aluminium chords
        private const float LacingRadius = 0.0125f;      // 25 mm diagonals
        private const float TrussBay = 0.5f;             // zig-zag lacing pitch
        private const float RoofBeamSpacing = 4f;
        private const float RoofBeamDepth = 0.9f;

        private readonly ArenaBuildSettings s;
        private readonly IArenaMaterialProvider m_provider;
        private readonly Transform m_parent;
        private readonly Dictionary<ArenaSurface, Material> m_materials = new Dictionary<ArenaSurface, Material>();
        private RuntimeArenaMaterials m_fallbackMaterials;
        private GameObject m_root;
        private ArenaRuntimeResources m_resources;
        private float[] m_beamZ;

        /// <summary>Geometry of one area of the arena, one builder per material.</summary>
        private sealed class MeshGroup
        {
            public readonly string Name;
            public readonly ShadowCastingMode Shadows;
            public readonly List<KeyValuePair<ArenaSurface, ArenaMeshBuilder>> Builders = new List<KeyValuePair<ArenaSurface, ArenaMeshBuilder>>();

            public MeshGroup(string name, ShadowCastingMode shadows)
            {
                Name = name;
                Shadows = shadows;
            }

            public ArenaMeshBuilder Get(ArenaSurface surface)
            {
                for (int i = 0; i < Builders.Count; i++)
                    if (Builders[i].Key == surface) return Builders[i].Value;
                var b = new ArenaMeshBuilder(1024);
                Builders.Add(new KeyValuePair<ArenaSurface, ArenaMeshBuilder>(surface, b));
                return b;
            }
        }

        public ArenaConstruction(ArenaBuildSettings settings, IArenaMaterialProvider provider, Transform parent)
        {
            s = settings;
            m_provider = provider;
            m_parent = parent;
        }

        /// <summary>Builds everything and returns the Court component.</summary>
        public Match.Court Build(bool createLights)
        {
            m_root = new GameObject(RuntimeArenaBuilder.RootName);
            m_root.SetActive(false);
            if (m_parent != null) m_root.transform.SetParent(m_parent, false);
            m_resources = m_root.AddComponent<ArenaRuntimeResources>();
            m_resources.Initialize(s.Clone());
            m_beamZ = ComputeBeamPositions();

            Match.Court court = CreateCourt();
            BuildFloor();
            BuildMarkings();
            BuildWalls();
            if (s.includeBleachers)
            {
                BuildBleachers(-1);
                BuildBleachers(1);
            }
            BuildRigging();
            if (s.includeCeiling) BuildRoof();
            BuildColliders();
            if (createLights) BuildLighting();

            GameLayers.SetLayerRecursively(m_root, GameLayers.Court);
            m_root.SetActive(true);
            return court;
        }

        // =============================================================================================================
        // Court component
        // =============================================================================================================

        private Match.Court CreateCourt()
        {
            var go = new GameObject("Court");
            go.transform.SetParent(m_root.transform, false);
            var court = go.AddComponent<Match.Court>();
            court.length = s.courtLength;
            court.width = s.courtWidth;
            court.outfieldDepth = s.outfieldDepth;
            court.runOff = s.runOff;
            return court;
        }

        // =============================================================================================================
        // Floor: maple court, stained outfield strips, maple apron, vinyl run-off
        // =============================================================================================================

        private void BuildFloor()
        {
            var g = new MeshGroup("Floor", ShadowCastingMode.Off);   // nothing below the floor to shadow
            float hw = s.HalfWidth, hl = s.HalfLength, pl = s.PlayAreaHalfLength;
            float wx = hw + s.woodApron, wz = pl + s.woodApron;
            float ix = s.InnerHalfX, iz = s.InnerHalfZ;

            // Top surfaces tile the floor without overlaps, all at y = 0. UVs are world (x, z) metres, so the maple strips
            // run continuously along Z from the infield through the stained outfield strips into the apron.
            ArenaMeshBuilder wood = g.Get(ArenaSurface.CourtWood);
            wood.AddFloorRect(-hw, hw, -hl, hl, 0f);
            wood.AddFloorRect(-wx, -hw, -wz, wz, 0f);
            wood.AddFloorRect(hw, wx, -wz, wz, 0f);
            wood.AddFloorRect(-hw, hw, pl, wz, 0f);
            wood.AddFloorRect(-hw, hw, -wz, -pl, 0f);

            if (s.outfieldDepth > 0.01f)
            {
                ArenaMeshBuilder outfield = g.Get(ArenaSurface.OutfieldZone);
                outfield.AddFloorRect(-hw, hw, hl, pl, 0f);
                outfield.AddFloorRect(-hw, hw, -pl, -hl, 0f);
            }

            ArenaMeshBuilder vinyl = g.Get(ArenaSurface.RunOffFloor);
            vinyl.AddFloorRect(-ix, -wx, -iz, iz, 0f);
            vinyl.AddFloorRect(wx, ix, -iz, iz, 0f);
            vinyl.AddFloorRect(-wx, wx, wz, iz, 0f);
            vinyl.AddFloorRect(-wx, wx, -iz, -wz, 0f);

            Flush(g);
        }

        // =============================================================================================================
        // Painted markings
        // =============================================================================================================

        private void BuildMarkings()
        {
            var g = new MeshGroup("Markings", ShadowCastingMode.Off);
            ArenaMeshBuilder paint = g.Get(ArenaSurface.CourtLinePaint);
            float hw = s.HalfWidth, hl = s.HalfLength, pl = s.PlayAreaHalfLength;
            float lw = s.lineWidth, y = MarkingsHeight;
            float x0 = -hw + lw, x1 = hw - lw;
            bool outfield = s.outfieldDepth > 0.01f;

            // Side lines along the whole playing area (infield + outfield strips); outer edge on the boundary.
            paint.AddFloorRect(-hw, x0, -pl, pl, y);
            paint.AddFloorRect(x1, hw, -pl, pl, y);

            if (outfield)
            {
                // Outer end lines of the outfield strips.
                paint.AddFloorRect(x0, x1, pl - lw, pl, y);
                paint.AddFloorRect(x0, x1, -pl, -pl + lw, y);
                // Baselines: centred on the infield/outfield boundary (both sides are playing areas).
                paint.AddFloorRect(x0, x1, hl - lw * 0.5f, hl + lw * 0.5f, y);
                paint.AddFloorRect(x0, x1, -hl - lw * 0.5f, -hl + lw * 0.5f, y);
            }
            else
            {
                paint.AddFloorRect(x0, x1, hl - lw, hl, y);
                paint.AddFloorRect(x0, x1, -hl, -hl + lw, y);
            }

            // Centre line.
            paint.AddFloorRect(x0, x1, -lw * 0.5f, lw * 0.5f, y);

            // Centre circle and stained logo disc.
            float r = s.centreCircleRadius;
            if (r > lw)
            {
                paint.AddRing(new Vector3(0f, y, 0f), r - lw * 0.5f, r + lw * 0.5f, 128);
                if (s.centreLogoDisc)
                    g.Get(ArenaSurface.OutfieldZone).AddDisc(new Vector3(0f, LogoHeight, 0f), r - lw * 0.25f, 128);
            }

            Flush(g);
        }

        // =============================================================================================================
        // Walls: end walls (full width), side barriers or side walls, back walls behind the bleachers
        // =============================================================================================================

        private void BuildWalls()
        {
            var g = new MeshGroup("Walls", ShadowCastingMode.On);
            ArenaMeshBuilder wall = g.Get(ArenaSurface.Wall);
            ArenaMeshBuilder pad = g.Get(ArenaSurface.WallPadding);
            ArenaMeshBuilder metal = g.Get(ArenaSurface.Metal);
            ArenaMeshBuilder concrete = s.includeBleachers ? g.Get(ArenaSurface.Bleachers) : null;

            float ix = s.InnerHalfX, iz = s.InnerHalfZ, ox = s.OuterHalfX, h = s.ceilingHeight, t = WallThickness;
            float padBottom = s.wallPadBottom, padTop = s.wallPadBottom + s.wallPadHeight, padT = s.wallPadThickness;

            // ---- End walls (z = ±iz) ---------------------------------------------------------------------------------
            for (int sign = -1; sign <= 1; sign += 2)
            {
                float zIn = sign * iz, zOut = sign * (iz + t);
                BoxFaces courtFace = sign > 0 ? BoxFaces.NegZ : BoxFaces.PosZ;
                // Painted block, World UVs so block courses start at the floor.
                wall.AddBoxMinMax(new Vector3(-ox - t, 0f, Mathf.Min(zIn, zOut)), new Vector3(ox + t, h, Mathf.Max(zIn, zOut)),
                    BoxUvMode.World, courtFace);

                // Door openings interrupt the pad band.
                float doorHalf = 0.9f, doorCentre = ix - 1.8f;
                bool doors = s.includeEndWallDetails && doorCentre - doorHalf > s.HalfWidth + s.woodApron;
                var padRuns = new List<Vector2>(3);
                if (doors)
                {
                    float gap = doorHalf + 0.08f;
                    padRuns.Add(new Vector2(-ix, -doorCentre - gap));
                    padRuns.Add(new Vector2(-doorCentre + gap, doorCentre - gap));
                    padRuns.Add(new Vector2(doorCentre + gap, ix));
                }
                else
                {
                    padRuns.Add(new Vector2(-ix, ix));
                }

                for (int r = 0; r < padRuns.Count; r++)
                    AddEndWallPad(pad, metal, sign, padRuns[r].x, padRuns[r].y, padBottom, padTop, padT);

                if (s.includeEndWallDetails)
                {
                    if (doors)
                    {
                        AddDoubleDoor(metal, sign, -doorCentre, doorHalf);
                        AddDoubleDoor(metal, sign, doorCentre, doorHalf);
                    }
                    AddAcousticPanels(pad, sign);
                }
            }

            // ---- Long sides ------------------------------------------------------------------------------------------
            for (int sg = -1; sg <= 1; sg += 2)
            {
                BoxFaces toCourt = sg > 0 ? BoxFaces.NegX : BoxFaces.PosX;
                BoxFaces away = sg > 0 ? BoxFaces.PosX : BoxFaces.NegX;

                if (s.includeBleachers)
                {
                    float bt = s.sideBarrierThickness, bh = s.sideBarrierHeight;
                    // Concrete barrier (spectator side + the strip below the pads), padded on the court side, steel cap.
                    SideBox(concrete, sg, ix, ix + bt, 0f, bh, -iz, iz, BoxUvMode.World, toCourt | away);
                    float padH = bh - padBottom;
                    var uvOffset = new Vector2(0f, Mathf.Max(0f, ArenaTextureGenerator.PadPanelHeight - padH)); // seam on top
                    SideBox(pad, sg, ix - padT, ix, padBottom, bh, -iz, iz, BoxUvMode.FaceLocal,
                        toCourt | BoxFaces.NegY | BoxFaces.PosZ | BoxFaces.NegZ, uvOffset);
                    SideBox(metal, sg, ix - padT - 0.01f, ix + bt + 0.01f, bh, bh + 0.04f, -iz, iz, BoxUvMode.World,
                        BoxFaces.AllButBottom);

                    // Back wall of the building behind the top walkway.
                    SideBox(wall, sg, ox, ox + t, 0f, h, -iz, iz, BoxUvMode.World, toCourt);
                }
                else
                {
                    SideBox(wall, sg, ix, ix + t, 0f, h, -iz, iz, BoxUvMode.World, toCourt);
                    SideBox(pad, sg, ix - padT, ix, padBottom, padTop, -iz, iz, BoxUvMode.FaceLocal,
                        toCourt | BoxFaces.PosY | BoxFaces.NegY);
                    SideBox(metal, sg, ix - padT - 0.005f, ix, padTop, padTop + 0.02f, -iz, iz, BoxUvMode.World,
                        BoxFaces.AllButBottom & ~away);
                }
            }

            Flush(g);
        }

        /// <summary>Padded band on an end wall between x0 and x1 (vinyl pads + aluminium top trim).</summary>
        private void AddEndWallPad(ArenaMeshBuilder pad, ArenaMeshBuilder metal, int sign, float x0, float x1,
            float bottom, float top, float thickness)
        {
            if (x1 - x0 < 0.05f) return;
            float iz = s.InnerHalfZ;
            float zFace = sign * (iz - thickness), zWall = sign * iz;
            BoxFaces courtFace = sign > 0 ? BoxFaces.NegZ : BoxFaces.PosZ;
            pad.AddBoxMinMax(new Vector3(x0, bottom, Mathf.Min(zFace, zWall)), new Vector3(x1, top, Mathf.Max(zFace, zWall)),
                BoxUvMode.FaceLocal, courtFace | BoxFaces.PosY | BoxFaces.NegY | BoxFaces.PosX | BoxFaces.NegX);

            float zTrim = sign * (iz - thickness - 0.005f);
            metal.AddBoxMinMax(new Vector3(x0, top, Mathf.Min(zTrim, zWall)), new Vector3(x1, top + 0.02f, Mathf.Max(zTrim, zWall)),
                BoxUvMode.World, courtFace | BoxFaces.PosY | BoxFaces.PosX | BoxFaces.NegX);
        }

        /// <summary>Brushed-steel double door with frame and panic bars, set into an end wall.</summary>
        private void AddDoubleDoor(ArenaMeshBuilder metal, int sign, float centreX, float halfWidth)
        {
            const float height = 2.15f, frame = 0.06f;
            float iz = s.InnerHalfZ;
            BoxFaces courtFace = sign > 0 ? BoxFaces.NegZ : BoxFaces.PosZ;
            BoxFaces visible = courtFace | BoxFaces.PosX | BoxFaces.NegX | BoxFaces.PosY;

            // Two leaves 3 cm proud of the wall, separated by a 6 mm meeting gap.
            for (int leaf = -1; leaf <= 1; leaf += 2)
            {
                float a = centreX + (leaf < 0 ? -halfWidth : 0.003f);
                float b = centreX + (leaf < 0 ? -0.003f : halfWidth);
                float zf = sign * (iz - 0.03f);
                metal.AddBoxMinMax(new Vector3(a, 0f, Mathf.Min(zf, sign * iz)), new Vector3(b, height, Mathf.Max(zf, sign * iz)),
                    BoxUvMode.FaceLocal, visible);

                // Panic bar at 1 m.
                float zb = sign * (iz - 0.085f);
                float bx0 = leaf < 0 ? centreX - halfWidth + 0.1f : centreX + 0.08f;
                float bx1 = leaf < 0 ? centreX - 0.08f : centreX + halfWidth - 0.1f;
                metal.AddTube(new Vector3(bx0, 1.0f, zb), new Vector3(bx1, 1.0f, zb), 0.018f, 10, true);
                metal.AddTube(new Vector3(bx0, 1.0f, zb), new Vector3(bx0, 1.0f, sign * (iz - 0.03f)), 0.012f, 8, false);
                metal.AddTube(new Vector3(bx1, 1.0f, zb), new Vector3(bx1, 1.0f, sign * (iz - 0.03f)), 0.012f, 8, false);
            }

            // Frame (jambs + head), 6 cm proud.
            float zfr = sign * (iz - frame);
            float zMin = Mathf.Min(zfr, sign * iz), zMax = Mathf.Max(zfr, sign * iz);
            metal.AddBoxMinMax(new Vector3(centreX - halfWidth - frame, 0f, zMin), new Vector3(centreX - halfWidth, height + frame, zMax),
                BoxUvMode.FaceLocal, visible);
            metal.AddBoxMinMax(new Vector3(centreX + halfWidth, 0f, zMin), new Vector3(centreX + halfWidth + frame, height + frame, zMax),
                BoxUvMode.FaceLocal, visible);
            metal.AddBoxMinMax(new Vector3(centreX - halfWidth, height, zMin), new Vector3(centreX + halfWidth, height + frame, zMax),
                BoxUvMode.FaceLocal, visible | BoxFaces.NegY);
        }

        /// <summary>Three fabric-wrapped acoustic panels high on an end wall (break up the large wall, damp reverb).</summary>
        private void AddAcousticPanels(ArenaMeshBuilder pad, int sign)
        {
            float ix = s.InnerHalfX, iz = s.InnerHalfZ, h = s.ceilingHeight;
            const float thickness = 0.05f, gap = 0.6f;
            float bottom = Mathf.Max(s.wallPadBottom + s.wallPadHeight + 2.5f, h * 0.4f);
            float top = Mathf.Min(bottom + ArenaTextureGenerator.PadPanelHeight * 2f, h - RoofBeamDepth - 0.8f);
            if (top - bottom < 1f) return;

            float span = ix * 2f - 2f;
            float width = (span - gap * 2f) / 3f;
            BoxFaces courtFace = sign > 0 ? BoxFaces.NegZ : BoxFaces.PosZ;
            float zf = sign * (iz - thickness);
            for (int i = 0; i < 3; i++)
            {
                float x0 = -ix + 1f + i * (width + gap);
                pad.AddBoxMinMax(new Vector3(x0, bottom, Mathf.Min(zf, sign * iz)), new Vector3(x0 + width, top, Mathf.Max(zf, sign * iz)),
                    BoxUvMode.FaceLocal, courtFace | BoxFaces.PosX | BoxFaces.NegX | BoxFaces.PosY | BoxFaces.NegY);
            }
        }

        // =============================================================================================================
        // Bleachers
        // =============================================================================================================

        private void BuildBleachers(int sg)
        {
            var g = new MeshGroup(sg < 0 ? "Bleachers_West" : "Bleachers_East", ShadowCastingMode.On);
            ArenaMeshBuilder concrete = g.Get(ArenaSurface.Bleachers);
            ArenaMeshBuilder seats = s.includeSeats ? g.Get(ArenaSurface.Seats) : null;
            ArenaMeshBuilder metal = g.Get(ArenaSurface.Metal);

            float x0 = s.InnerHalfX + s.sideBarrierThickness;
            float d = s.rowDepth, rise = s.rowRise, iz = s.InnerHalfZ;
            BoxFaces toCourt = sg > 0 ? BoxFaces.NegX : BoxFaces.PosX;
            float[] aisles = AislePositions();
            float aw = s.aisleWidth;

            // Seat slots along a row, centred, keeping 0.3 m from the end walls.
            int slots = Mathf.Max(0, Mathf.FloorToInt((2f * iz - 0.6f) / s.seatPitch));
            float firstZ = -(slots - 1) * s.seatPitch * 0.5f;

            for (int i = 0; i < s.bleacherRows; i++)
            {
                float xa = x0 + i * d, xb = xa + d;
                float hi = s.firstRowHeight + i * rise;

                // Stepped tier: tread + riser (the riser's lower part is hidden behind the previous tier).
                SideBox(concrete, sg, xa, xb, 0f, hi, -iz, iz, BoxUvMode.World, BoxFaces.PosY | toCourt);

                for (int a = 0; a < aisles.Length; a++)
                {
                    // Aisle half-step on the front half of each tread.
                    SideBox(concrete, sg, xa, xa + d * 0.5f, hi, hi + rise * 0.5f, aisles[a] - aw * 0.5f, aisles[a] + aw * 0.5f,
                        BoxUvMode.World, BoxFaces.PosY | toCourt | BoxFaces.PosZ | BoxFaces.NegZ);
                }

                if (seats == null) continue;
                float xs = sg * (xa + d * 0.58f);
                for (int k = 0; k < slots; k++)
                {
                    float z = firstZ + k * s.seatPitch;
                    if (InAisle(z, aisles, aw * 0.5f + 0.24f)) continue;
                    AddSeat(seats, metal, new Vector3(xs, hi, z), sg);
                }
            }

            // Circulation walkway behind the top row.
            float xw = x0 + s.bleacherRows * d;
            SideBox(concrete, sg, xw, s.OuterHalfX, 0f, s.WalkwayHeight, -iz, iz, BoxUvMode.World, BoxFaces.PosY | toCourt);

            // Aisle handrails: posts every second row, one continuous sloped rail 0.9 m above the half-steps.
            for (int a = 0; a < aisles.Length; a++)
            {
                float z = aisles[a];
                Vector3 first = Vector3.zero, last = Vector3.zero;
                for (int i = 0; i < s.bleacherRows; i++)
                {
                    float xa = x0 + i * d;
                    float stepTop = s.firstRowHeight + i * rise + rise * 0.5f;
                    var basePt = new Vector3(sg * (xa + d * 0.25f), stepTop, z);
                    var top = basePt + Vector3.up * 0.9f;
                    if (i == 0) first = top;
                    last = top;
                    if (i % 2 == 0 || i == s.bleacherRows - 1) metal.AddTube(basePt, top, 0.024f, 8, true);
                }
                if (s.bleacherRows > 1) metal.AddTube(first, last, 0.021f, 8, true);
            }

            Flush(g);
        }

        /// <summary>
        /// One moulded stadium seat on a steel pedestal, facing the court. <paramref name="origin"/> is on the tread.
        /// </summary>
        private static void AddSeat(ArenaMeshBuilder plastic, ArenaMeshBuilder metal, Vector3 origin, int sg)
        {
            // Local frame: +z = the direction the spectator faces (toward the court), +y up.
            Quaternion q = Quaternion.LookRotation(new Vector3(-sg, 0f, 0f), Vector3.up);
            Quaternion panRot = q * Quaternion.Euler(-4f, 0f, 0f);    // front edge slightly raised
            Quaternion backRot = q * Quaternion.Euler(-12f, 0f, 0f);  // backrest reclined

            plastic.AddBox(origin + q * new Vector3(0f, 0.43f, 0.02f), new Vector3(0.44f, 0.045f, 0.40f), panRot, BoxUvMode.FaceLocal);
            plastic.AddBox(origin + q * new Vector3(0f, 0.66f, -0.20f), new Vector3(0.44f, 0.40f, 0.04f), backRot, BoxUvMode.FaceLocal);
            // Rolled front lip of the seat pan.
            Vector3 lipL = origin + q * new Vector3(-0.21f, 0.445f, 0.215f);
            Vector3 lipR = origin + q * new Vector3(0.21f, 0.445f, 0.215f);
            plastic.AddTube(lipL, lipR, 0.022f, 6, false);

            // Pedestal and floor plate.
            metal.AddBox(origin + q * new Vector3(0f, 0.2f, -0.06f), new Vector3(0.06f, 0.4f, 0.06f), q, BoxUvMode.FaceLocal,
                BoxFaces.AllButBottom);
            metal.AddBox(origin + q * new Vector3(0f, 0.005f, -0.06f), new Vector3(0.2f, 0.01f, 0.16f), q, BoxUvMode.FaceLocal,
                BoxFaces.AllButBottom);
        }

        private float[] AislePositions()
        {
            int n = s.aislesPerSide;
            var result = new float[n];
            float span = s.InnerHalfZ * 2f;
            for (int i = 0; i < n; i++) result[i] = -s.InnerHalfZ + span * (i + 1) / (n + 1);
            return result;
        }

        private static bool InAisle(float z, float[] aisles, float clearance)
        {
            for (int i = 0; i < aisles.Length; i++)
                if (Mathf.Abs(z - aisles[i]) < clearance) return true;
            return false;
        }

        // =============================================================================================================
        // Floodlight rig: two box trusses along Z with LED floodlights on yokes, chain hoists to the roof beams
        // =============================================================================================================

        private void BuildRigging()
        {
            // Rigging sits above the floodlights, so it never casts shadows onto the court: no shadow casting (cheaper).
            var g = new MeshGroup("Rigging", ShadowCastingMode.Off);
            ArenaMeshBuilder metal = g.Get(ArenaSurface.Metal);
            ArenaMeshBuilder lens = g.Get(ArenaSurface.LightEmitter);

            float zL = Mathf.Min(s.PlayAreaHalfLength + 2f, s.InnerHalfZ - 0.3f);
            for (int side = -1; side <= 1; side += 2)
            {
                float xc = side * s.trussOffset;
                BuildTruss(metal, xc, s.trussHeight, zL);
                BuildHangers(metal, xc, s.trussHeight + TrussSize, zL);
            }

            int count = RuntimeArenaBuilder.FloodlightCount(s);
            for (int i = 0; i < count; i++) BuildFixture(metal, lens, i);

            Flush(g);
        }

        private static void BuildTruss(ArenaMeshBuilder m, float xc, float yBottom, float zL)
        {
            float hs = TrussSize * 0.5f;
            float yTop = yBottom + TrussSize;
            var chords = new[]
            {
                new Vector2(xc - hs, yBottom), new Vector2(xc + hs, yBottom),
                new Vector2(xc + hs, yTop), new Vector2(xc - hs, yTop),
            };

            for (int c = 0; c < 4; c++)
                m.AddTube(new Vector3(chords[c].x, chords[c].y, -zL), new Vector3(chords[c].x, chords[c].y, zL), ChordRadius, 10, true);

            int bays = Mathf.Max(1, Mathf.RoundToInt(2f * zL / TrussBay));
            float bay = 2f * zL / bays;
            for (int j = 0; j < bays; j++)
            {
                float z0 = -zL + j * bay, z1 = z0 + bay;
                for (int f = 0; f < 4; f++)
                {
                    Vector2 a = chords[f], b = chords[(f + 1) % 4];
                    // Zig-zag lacing on each of the four faces.
                    if (((j + f) & 1) == 0) m.AddTube(new Vector3(a.x, a.y, z0), new Vector3(b.x, b.y, z1), LacingRadius, 6, false);
                    else m.AddTube(new Vector3(b.x, b.y, z0), new Vector3(a.x, a.y, z1), LacingRadius, 6, false);
                }
            }

            // End frames.
            for (int e = -1; e <= 1; e += 2)
            {
                float z = e * zL;
                for (int f = 0; f < 4; f++)
                {
                    Vector2 a = chords[f], b = chords[(f + 1) % 4];
                    m.AddTube(new Vector3(a.x, a.y, z), new Vector3(b.x, b.y, z), LacingRadius, 6, false);
                }
            }
        }

        /// <summary>Chain hoists on the roof beams with steel cables down to the truss top chords.</summary>
        private void BuildHangers(ArenaMeshBuilder m, float xc, float yTrussTop, float zL)
        {
            float h = s.ceilingHeight;
            float hs = TrussSize * 0.5f;
            float[] zs = { -zL + 1f, -zL / 3f, zL / 3f, zL - 1f };
            for (int i = 0; i < zs.Length; i++)
            {
                float z = SnapToBeam(zs[i], zL - 0.25f);
                float hoistY = s.includeCeiling ? h - RoofBeamDepth - 0.25f : h - 0.4f;
                m.AddBox(new Vector3(xc, hoistY, z), new Vector3(0.24f, 0.32f, 0.26f), Quaternion.identity, BoxUvMode.FaceLocal);
                if (s.includeCeiling)
                    m.AddTube(new Vector3(xc, hoistY + 0.16f, z), new Vector3(xc, h - RoofBeamDepth, z), 0.015f, 6, false);
                // Bridle: two cables from the hoist hook to both top chords.
                var hook = new Vector3(xc, hoistY - 0.16f, z);
                m.AddTube(hook, new Vector3(xc - hs, yTrussTop, z), 0.006f, 5, false);
                m.AddTube(hook, new Vector3(xc + hs, yTrussTop, z), 0.006f, 5, false);
            }
        }

        private float SnapToBeam(float z, float limit)
        {
            if (!s.includeCeiling || m_beamZ == null || m_beamZ.Length == 0) return z;
            float best = z, bestD = float.MaxValue;
            for (int i = 0; i < m_beamZ.Length; i++)
            {
                float bz = m_beamZ[i];
                if (Mathf.Abs(bz) > limit) continue;
                float d = Mathf.Abs(bz - z);
                if (d < bestD)
                {
                    bestD = d;
                    best = bz;
                }
            }
            return best;
        }

        /// <summary>LED floodlight: finned housing, emissive lens, bezel, glare visor, yoke and truss clamp.</summary>
        private void BuildFixture(ArenaMeshBuilder metal, ArenaMeshBuilder lens, int index)
        {
            RuntimeArenaBuilder.GetFloodlightFrame(s, index, out Vector3 hc, out Vector3 aim);
            Vector3 f = (aim - hc).normalized;
            Quaternion q = Quaternion.LookRotation(f, Vector3.up);
            const float half = 0.29f, depth = 0.2f;

            metal.AddBox(hc, new Vector3(half * 2f, half * 2f, depth), q, BoxUvMode.FaceLocal);

            // Lens: 0.5 m square = 5 x 5 LED optics; its front sits exactly at FloodlightLensOffset.
            const float lensThickness = 0.015f;
            float lensZ = RuntimeArenaBuilder.FloodlightLensOffset - lensThickness * 0.5f;
            lens.AddBox(hc + q * new Vector3(0f, 0f, lensZ), new Vector3(0.5f, 0.5f, lensThickness), q, BoxUvMode.FaceLocal, BoxFaces.PosZ);

            // Bezel around the lens.
            float bz = depth * 0.5f + 0.01f;
            metal.AddBox(hc + q * new Vector3(0f, 0.27f, bz), new Vector3(0.58f, 0.04f, 0.02f), q, BoxUvMode.FaceLocal);
            metal.AddBox(hc + q * new Vector3(0f, -0.27f, bz), new Vector3(0.58f, 0.04f, 0.02f), q, BoxUvMode.FaceLocal);
            metal.AddBox(hc + q * new Vector3(0.27f, 0f, bz), new Vector3(0.04f, 0.5f, 0.02f), q, BoxUvMode.FaceLocal);
            metal.AddBox(hc + q * new Vector3(-0.27f, 0f, bz), new Vector3(0.04f, 0.5f, 0.02f), q, BoxUvMode.FaceLocal);

            // Glare visor over the lens.
            metal.AddBox(hc + q * new Vector3(0f, half + 0.005f, depth * 0.5f + 0.09f), new Vector3(0.6f, 0.01f, 0.18f), q, BoxUvMode.FaceLocal);

            // Cooling fins on the back.
            for (int k = 0; k < 7; k++)
            {
                float y = -0.24f + k * 0.08f;
                metal.AddBox(hc + q * new Vector3(0f, y, -depth * 0.5f - 0.05f), new Vector3(0.54f, 0.012f, 0.1f), q, BoxUvMode.FaceLocal);
            }

            // Yoke: pivots on both sides, arms up to a cross bar, clamp tube to a hanging pipe between the bottom chords.
            Vector3 pl = hc + q * new Vector3(-half - 0.02f, 0f, 0f);
            Vector3 pr = hc + q * new Vector3(half + 0.02f, 0f, 0f);
            metal.AddTube(hc + q * new Vector3(-half, 0f, 0f), hc + q * new Vector3(-half - 0.05f, 0f, 0f), 0.045f, 12, true);
            metal.AddTube(hc + q * new Vector3(half, 0f, 0f), hc + q * new Vector3(half + 0.05f, 0f, 0f), 0.045f, 12, true);

            float yb = s.trussHeight;
            float barY = yb - 0.08f;
            var tl = new Vector3(pl.x, barY, pl.z);
            var tr = new Vector3(pr.x, barY, pr.z);
            metal.AddTube(pl, tl, 0.02f, 8, true);
            metal.AddTube(pr, tr, 0.02f, 8, true);
            metal.AddTube(tl, tr, 0.022f, 8, true);
            Vector3 mid = (tl + tr) * 0.5f;
            var clampTop = new Vector3(mid.x, yb, mid.z);
            metal.AddTube(mid, clampTop, 0.03f, 8, true);
            float hs = TrussSize * 0.5f;
            float xc = Mathf.Sign(hc.x) * s.trussOffset;
            metal.AddTube(new Vector3(xc - hs, yb, clampTop.z), new Vector3(xc + hs, yb, clampTop.z), 0.024f, 8, true);
        }

        // =============================================================================================================
        // Roof: dark steel deck, I-beams, ventilation ducts
        // =============================================================================================================

        private float[] ComputeBeamPositions()
        {
            float iz = s.InnerHalfZ;
            int n = Mathf.Max(1, Mathf.FloorToInt(2f * (iz - 1f) / RoofBeamSpacing));
            var result = new float[n + 1];
            for (int k = 0; k <= n; k++) result[k] = -n * RoofBeamSpacing * 0.5f + k * RoofBeamSpacing;
            return result;
        }

        private void BuildRoof()
        {
            // Everything here is above every light: never a shadow caster that matters.
            var g = new MeshGroup("Roof", ShadowCastingMode.Off);
            ArenaMeshBuilder deck = g.Get(ArenaSurface.Ceiling);
            ArenaMeshBuilder metal = g.Get(ArenaSurface.Metal);

            float ox = s.OuterHalfX + WallThickness, iz = s.InnerHalfZ + WallThickness, h = s.ceilingHeight;

            // Deck (ribs run along Z).
            deck.AddBoxMinMax(new Vector3(-ox, h, -iz), new Vector3(ox, h + 0.1f, iz), BoxUvMode.World, BoxFaces.NegY);

            // I-beams spanning the hall.
            for (int k = 0; k < m_beamZ.Length; k++)
            {
                float z = m_beamZ[k];
                metal.AddBoxMinMax(new Vector3(-ox, h - RoofBeamDepth, z - 0.15f), new Vector3(ox, h - RoofBeamDepth + 0.02f, z + 0.15f),
                    BoxUvMode.World, BoxFaces.All & ~BoxFaces.PosX & ~BoxFaces.NegX);
                metal.AddBoxMinMax(new Vector3(-ox, h - RoofBeamDepth + 0.02f, z - 0.007f), new Vector3(ox, h, z + 0.007f),
                    BoxUvMode.World, BoxFaces.PosZ | BoxFaces.NegZ);
            }

            // Supply-air ducts over the stands (or over the run-off without bleachers).
            float ductX = s.includeBleachers ? s.InnerHalfX + s.sideBarrierThickness + s.BleacherDepth * 0.5f : s.InnerHalfX - 1f;
            float ductY = h - RoofBeamDepth - 0.6f;
            for (int side = -1; side <= 1; side += 2)
                metal.AddTube(new Vector3(side * ductX, ductY, -s.InnerHalfZ), new Vector3(side * ductX, ductY, s.InnerHalfZ), 0.45f, 24, false);

            Flush(g);
        }

        // =============================================================================================================
        // Colliders (simplified boxes on GameLayers.Court)
        // =============================================================================================================

        private void BuildColliders()
        {
            var root = new GameObject("Colliders");
            root.transform.SetParent(m_root.transform, false);

            float ix = s.InnerHalfX, iz = s.InnerHalfZ, ox = s.OuterHalfX, h = s.ceilingHeight, t = WallThickness;
            float padT = s.wallPadThickness;

            GameObject floor = Child(root.transform, "FloorCollider");
            AddCollider(floor, new Vector3(-ox - t - 1f, -FloorColliderDepth, -iz - t - 1f), new Vector3(ox + t + 1f, 0f, iz + t + 1f));

            GameObject walls = Child(root.transform, "WallColliders");
            for (int sign = -1; sign <= 1; sign += 2)
            {
                // Inner face on the pad surface (6 cm off the upper wall is imperceptible for balls).
                float zIn = sign * (iz - padT), zOut = sign * (iz + t);
                AddCollider(walls, new Vector3(-ox - t, 0f, Mathf.Min(zIn, zOut)), new Vector3(ox + t, h, Mathf.Max(zIn, zOut)));
            }

            for (int sg = -1; sg <= 1; sg += 2)
            {
                if (s.includeBleachers)
                {
                    SideCollider(walls, sg, ix - padT, ix + s.sideBarrierThickness, 0f, s.sideBarrierHeight + 0.04f, -iz, iz);
                    SideCollider(walls, sg, ox, ox + t, 0f, h, -iz, iz);

                    GameObject stands = Child(root.transform, sg < 0 ? "StandColliders_West" : "StandColliders_East");
                    float x0 = ix + s.sideBarrierThickness;
                    for (int i = 0; i < s.bleacherRows; i++)
                    {
                        float xa = x0 + i * s.rowDepth;
                        SideCollider(stands, sg, xa, xa + s.rowDepth, 0f, s.firstRowHeight + i * s.rowRise, -iz, iz);
                    }
                    SideCollider(stands, sg, x0 + s.bleacherRows * s.rowDepth, ox, 0f, s.WalkwayHeight, -iz, iz);
                }
                else
                {
                    SideCollider(walls, sg, ix - padT, ix + t, 0f, h, -iz, iz);
                }
            }

            GameObject roof = Child(root.transform, "RoofCollider");
            AddCollider(roof, new Vector3(-ox - t, h, -iz - t), new Vector3(ox + t, h + 0.5f, iz + t));
        }

        private static GameObject Child(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void AddCollider(GameObject go, Vector3 min, Vector3 max)
        {
            var box = go.AddComponent<BoxCollider>();
            box.center = (min + max) * 0.5f;
            box.size = max - min;
        }

        private static void SideCollider(GameObject go, int sg, float xNear, float xFar, float y0, float y1, float z0, float z1)
        {
            float a = sg * xNear, b = sg * xFar;
            AddCollider(go, new Vector3(Mathf.Min(a, b), y0, z0), new Vector3(Mathf.Max(a, b), y1, z1));
        }

        // =============================================================================================================
        // Lighting
        // =============================================================================================================

        private void BuildLighting()
        {
            var lighting = new GameObject("Lighting").transform;
            lighting.SetParent(m_root.transform, false);
            IRuntimeRenderingHooks hooks = RuntimeRenderingHooks.Active;
            PipelineKind pipeline = RenderPipelineUtil.Current;

            // Floodlights: the most central fixtures get shadows first (they light the play).
            int count = RuntimeArenaBuilder.FloodlightCount(s);
            var order = new List<int>(count);
            for (int i = 0; i < count; i++) order.Add(i);
            var zOf = new float[count];
            for (int i = 0; i < count; i++)
            {
                RuntimeArenaBuilder.GetFloodlightFrame(s, i, out Vector3 hc, out _);
                zOf[i] = Mathf.Abs(hc.z);
            }
            order.Sort((a, b) => zOf[a] != zOf[b] ? zOf[a].CompareTo(zOf[b]) : a.CompareTo(b));
            var shadowed = new bool[count];
            for (int k = 0; k < Mathf.Min(s.shadowedFloodlights, count); k++) shadowed[order[k]] = true;

            Color cct = Mathf.CorrelatedColorTemperatureToRGB(s.floodlightTemperature);
            for (int i = 0; i < count; i++)
            {
                RuntimeArenaBuilder.GetFloodlightFrame(s, i, out Vector3 hc, out Vector3 aim);
                Vector3 f = (aim - hc).normalized;
                var go = new GameObject($"Floodlight_{i:00}");
                go.transform.SetParent(lighting, false);
                // Just in front of the lens so the housing never occludes its own beam.
                go.transform.localPosition = hc + f * (RuntimeArenaBuilder.FloodlightLensOffset + 0.03f);
                go.transform.localRotation = Quaternion.LookRotation(f, Vector3.up);

                var light = go.AddComponent<Light>();
                light.type = LightType.Spot;
                light.spotAngle = s.floodlightSpotAngle;
                light.innerSpotAngle = s.floodlightSpotAngle * s.floodlightInnerAngleFraction;
                light.range = s.floodlightRange;
                light.color = cct;
                light.shadows = shadowed[i] ? LightShadows.Soft : LightShadows.None;
                light.shadowStrength = 0.9f;
                light.shadowNearPlane = 0.2f;
                light.renderMode = LightRenderMode.ForcePixel;
                if (hooks != null) hooks.ConfigureLight(light, s.floodlightLumen, s.floodlightTemperature);
                else light.intensity = s.fallbackFloodlightIntensity;
            }

            // Soft overhead fill: light bounced off the maple floor, walls and roof. No shadows (it would be blocked by the
            // roof and produce a second hard shadow under every player).
            const float fillKelvin = 5000f;
            var fillGo = new GameObject("OverheadFill");
            fillGo.transform.SetParent(lighting, false);
            fillGo.transform.localRotation = Quaternion.Euler(78f, 25f, 0f);
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.shadows = LightShadows.None;
            fill.color = Mathf.CorrelatedColorTemperatureToRGB(fillKelvin);
            if (hooks != null) hooks.ConfigureLight(fill, s.fillIlluminanceLux, fillKelvin);
            else fill.intensity = s.fallbackFillIntensity;

            if (pipeline != PipelineKind.HighDefinition)
            {
                // Built-in / URP fallback: indoor trilight ambient (dark roof, grey walls, warm bounce off the maple).
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = new Color(0.20f, 0.21f, 0.23f);
                RenderSettings.ambientEquatorColor = new Color(0.23f, 0.22f, 0.21f);
                RenderSettings.ambientGroundColor = new Color(0.27f, 0.21f, 0.15f);
                RenderSettings.ambientIntensity = 1f;
                RenderSettings.reflectionIntensity = 1f;

                if (s.createReflectionProbe) CreateReflectionProbe(lighting);
            }

            // HDRP: sky/exposure/fog/AO/SSR volume (and HDRP probes) come from the pipeline adapter.
            hooks?.EnsureEnvironmentVolume(m_root.transform);
        }

        private void CreateReflectionProbe(Transform parent)
        {
            var go = new GameObject("ArenaReflectionProbe");
            go.transform.SetParent(parent, false);
            const float probeHeight = 2.5f;   // roughly eye/ball height: best parallax for the glossy floor
            go.transform.localPosition = new Vector3(0f, probeHeight, 0f);

            Bounds bounds = RuntimeArenaBuilder.GetArenaBounds(s);
            var probe = go.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime;
            probe.refreshMode = ReflectionProbeRefreshMode.OnAwake;
            probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
            probe.boxProjection = true;
            probe.size = bounds.size;
            probe.center = bounds.center - new Vector3(0f, probeHeight, 0f);
            probe.resolution = 256;
            probe.hdr = true;
            probe.intensity = 1f;
            probe.blendDistance = 1f;
            probe.importance = 1;
            probe.nearClipPlane = 0.1f;
            probe.farClipPlane = bounds.size.magnitude;
            probe.clearFlags = ReflectionProbeClearFlags.SolidColor;
            probe.backgroundColor = new Color(0.05f, 0.05f, 0.055f);
            // Static architecture only: players, balls, ragdolls and effects must not be frozen into the reflection.
            probe.cullingMask = GameLayers.CourtMask | 1;
        }

        // =============================================================================================================
        // Helpers
        // =============================================================================================================

        /// <summary>Box on one long side (sg = -1 / +1) given distances from the centre axis.</summary>
        private static void SideBox(ArenaMeshBuilder b, int sg, float xNear, float xFar, float y0, float y1, float z0, float z1,
            BoxUvMode uvMode, BoxFaces faces, Vector2 uvOffset = default)
        {
            float a = sg * xNear, c = sg * xFar;
            b.AddBoxMinMax(new Vector3(Mathf.Min(a, c), y0, z0), new Vector3(Mathf.Max(a, c), y1, z1), uvMode, faces, uvOffset);
        }

        private Material GetMaterial(ArenaSurface surface)
        {
            if (m_materials.TryGetValue(surface, out Material cached)) return cached;
            Material m = m_provider?.GetMaterial(surface);
            if (m == null)
            {
                if (m_fallbackMaterials == null) m_fallbackMaterials = new RuntimeArenaMaterials(s.textureResolution);
                m = m_fallbackMaterials.GetMaterial(surface);
            }
            m_materials[surface] = m;
            return m;
        }

        /// <summary>Creates one renderer per material of the group under a group object.</summary>
        private void Flush(MeshGroup g)
        {
            var groupGo = new GameObject(g.Name);
            groupGo.transform.SetParent(m_root.transform, false);

            for (int i = 0; i < g.Builders.Count; i++)
            {
                ArenaSurface surface = g.Builders[i].Key;
                Mesh mesh = g.Builders[i].Value.ToMesh($"DU_Arena_{g.Name}_{surface}");
                if (mesh == null) continue;
                m_resources.Register(mesh);

                var go = new GameObject($"{g.Name}_{surface}");
                go.transform.SetParent(groupGo.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = GetMaterial(surface);
                bool neverCasts = surface == ArenaSurface.LightEmitter || surface == ArenaSurface.CourtLinePaint;
                mr.shadowCastingMode = neverCasts ? ShadowCastingMode.Off : g.Shadows;
                mr.receiveShadows = true;
                mr.lightProbeUsage = LightProbeUsage.Off;              // large static meshes: lightmaps / ambient, not probes
                mr.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                mr.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;   // static: camera motion only
            }
        }
    }
}
