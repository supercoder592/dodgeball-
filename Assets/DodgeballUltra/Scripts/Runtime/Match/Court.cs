using System.Collections.Generic;
using DodgeballUltra.Core;
using UnityEngine;

namespace DodgeballUltra.Match
{
    /// <summary>
    /// CONTRACT (kernel) - court geometry and zone queries. Court is centred on this transform's position, long axis = Z.
    /// Home defends z &lt; 0, Away defends z &gt; 0. Each team's OUTFIELD strip lies behind the OPPONENT's baseline (so
    /// eliminated players attack from behind the enemy, Taiwanese 外場 style).
    /// <code>
    ///   z = -L/2 - D ... -L/2   : Away outfield   (behind Home baseline)
    ///   z = -L/2 ... 0          : Home infield
    ///   z = 0 ... +L/2          : Away infield
    ///   z = +L/2 ... +L/2 + D   : Home outfield   (behind Away baseline)
    /// </code>
    /// <para>
    /// The court is always axis-aligned in world space (only the transform's <b>position</b> is used; rotation and scale
    /// are ignored) so that every query can return a plain axis-aligned <see cref="Bounds"/>, which the motor uses for
    /// allocation-free confinement.
    /// </para>
    /// <para>Owner module: Match.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Court : MonoBehaviour
    {
        public static Court Instance { get; private set; }

        [Tooltip("Court length along Z (m). Two 9x9 halves.")] public float length = 18f;
        [Tooltip("Court width along X (m).")] public float width = 9f;
        [Tooltip("Depth of each outfield strip behind the baselines (m).")] public float outfieldDepth = 3f;
        [Tooltip("Distance from the court edge to the arena walls (m).")] public float runOff = 4f;

        [Header("Confinement")]
        [Tooltip("Inset (m) applied to every zone when confining a player, so the capsule (radius ~0.32 m) never " +
                 "overlaps a painted line or crosses the centre line.")]
        [Range(0f, 1f)] public float confinementInset = 0.35f;
        [Tooltip("Height (m) of the zone volumes returned by the Bounds queries. Only X/Z matter for confinement; the " +
                 "height just has to contain jumps.")]
        [Min(2f)] public float zoneHeight = 8f;

        [Header("Spawn layout")]
        [Tooltip("Distance of the spawn line from the centre line, as a fraction of the half-court length. 0.72 puts the " +
                 "players at the front edge of their back third (6.5 m on a regulation 18 m court).")]
        [Range(0.35f, 0.95f)] public float spawnDepthFraction = 0.72f;
        [Tooltip("Margin (m) kept between the outermost spawn slots / balls and the side lines.")]
        [Min(0f)] public float lateralMargin = 1.25f;
        [Tooltip("How far (m) the centre spawn slot stands in front of the outer slots (a shallow arrowhead, like a real " +
                 "opening line-up). 0 = flat line.")]
        [Range(0f, 2f)] public float spawnStagger = 0.6f;

        [Header("Outfield layout")]
        [Tooltip("Number of outfield spots across the width of a strip before a second, deeper row is used.")]
        [Min(1)] public int outfieldSpotsPerRow = 3;
        [Tooltip("Distance (m) between rows of outfield spots.")]
        [Range(0.3f, 2f)] public float outfieldRowSpacing = 0.8f;

        [Header("Opening rush")]
        [Tooltip("Each opening ball sits this far (m) off the centre line, alternating sides so both teams have balls " +
                 "to rush for.")]
        [Range(0f, 1.5f)] public float openingBallOffset = 0.35f;

        [Header("Out of arena")]
        [Tooltip("Extra distance (m) beyond the run-off walls before a ball is considered to have left the arena.")]
        [Min(0f)] public float outOfArenaMargin = 1.5f;
        [Tooltip("A ball this far (m) below the floor has fallen out of the world.")]
        [Min(0.1f)] public float belowFloorLimit = 2f;

        public Vector3 Center => transform.position;
        public float FloorY => transform.position.y;

        /// <summary>Half of <see cref="length"/> (distance from the centre line to a baseline).</summary>
        public float HalfLength => Mathf.Max(0.5f, length) * 0.5f;

        /// <summary>Half of <see cref="width"/> (distance from the long axis to a side line).</summary>
        public float HalfWidth => Mathf.Max(0.5f, width) * 0.5f;

        /// <summary>Distance from the court centre to the arena walls along Z (outfield strips are always inside).</summary>
        public float WallDistanceZ => HalfLength + Mathf.Max(runOff, outfieldDepth);

        /// <summary>Distance from the court centre to the arena walls along X.</summary>
        public float WallDistanceX => HalfWidth + Mathf.Max(0f, runOff);

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[Dodgeball Ultra] A second Court ('{name}') was found; '{Instance.name}' stays active.", this);
                return;
            }
            Instance = this;
        }

        private void OnEnable()
        {
            if (Instance == null) Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void OnValidate()
        {
            length = Mathf.Max(2f, length);
            width = Mathf.Max(2f, width);
            outfieldDepth = Mathf.Max(0.5f, outfieldDepth);
            runOff = Mathf.Max(0f, runOff);
        }

        // ------------------------------------------------------------------ orientation

        /// <summary>-1 for Home (negative Z), +1 for Away.</summary>
        public float SideSign(TeamId team)
        {
            switch (team)
            {
                case TeamId.Home: return -1f;
                case TeamId.Away: return 1f;
                default: return 0f;
            }
        }

        /// <summary>Unit vector from <paramref name="team"/>'s half toward the opponent.</summary>
        public Vector3 AttackDirection(TeamId team)
        {
            // Home lives at -Z and attacks toward +Z; Away the opposite. None gets +Z so callers never receive zero.
            float sign = SideSign(team);
            return sign > 0f ? Vector3.back : Vector3.forward;
        }

        public Quaternion GetSpawnRotation(TeamId team) => Quaternion.LookRotation(AttackDirection(team), Vector3.up);

        // ------------------------------------------------------------------ zones

        public Bounds GetInfieldBounds(TeamId team)
        {
            float sign = SideSign(team);
            if (sign == 0f)
            {
                // No team: the whole playing surface.
                return MakeZone(0f, HalfWidth * 2f, HalfLength * 2f);
            }
            return MakeZone(sign * HalfLength * 0.5f, HalfWidth * 2f, HalfLength);
        }

        /// <summary>The strip where <paramref name="team"/>'s eliminated players stand (behind the opponent's baseline).</summary>
        public Bounds GetOutfieldBounds(TeamId team)
        {
            // The strip is behind the OPPONENT's baseline: Home's outfield is at +Z, Away's at -Z.
            float opponentSign = -SideSign(team);
            if (opponentSign == 0f) opponentSign = 1f;
            float depth = Mathf.Max(0.5f, outfieldDepth);
            return MakeZone(opponentSign * (HalfLength + depth * 0.5f), HalfWidth * 2f, depth);
        }

        /// <summary>Movement confinement for a player of <paramref name="team"/> in <paramref name="zone"/>.</summary>
        public Bounds GetConfinement(TeamId team, CourtZone zone)
        {
            var b = zone == CourtZone.Outfield ? GetOutfieldBounds(team) : GetInfieldBounds(team);

            // Shrink on X/Z only (the capsule must stay fully inside the painted zone); never collapse below 0.2 m.
            var size = b.size;
            float inset = Mathf.Max(0f, confinementInset) * 2f;
            size.x = Mathf.Max(0.2f, size.x - inset);
            size.z = Mathf.Max(0.2f, size.z - inset);
            b.size = size;
            return b;
        }

        public bool IsInInfield(TeamId team, Vector3 position)
        {
            if (!team.IsValid()) return false;
            return ContainsPlanar(GetInfieldBounds(team), position);
        }

        /// <summary>True if <paramref name="position"/> is inside <paramref name="team"/>'s outfield strip (X/Z test).</summary>
        public bool IsInOutfield(TeamId team, Vector3 position)
        {
            if (!team.IsValid()) return false;
            return ContainsPlanar(GetOutfieldBounds(team), position);
        }

        /// <summary>Which team's infield half contains <paramref name="position"/> (None if outside both).</summary>
        public TeamId GetHalfOwner(Vector3 position)
        {
            var local = position - Center;
            if (Mathf.Abs(local.x) > HalfWidth || Mathf.Abs(local.z) > HalfLength) return TeamId.None;
            return local.z < 0f ? TeamId.Home : TeamId.Away;
        }

        /// <summary>Clamps <paramref name="position"/> (X/Z) into the confinement of <paramref name="team"/>/<paramref name="zone"/>.</summary>
        public Vector3 ClampToConfinement(TeamId team, CourtZone zone, Vector3 position)
        {
            var b = GetConfinement(team, zone);
            position.x = Mathf.Clamp(position.x, b.min.x, b.max.x);
            position.z = Mathf.Clamp(position.z, b.min.z, b.max.z);
            return position;
        }

        // ------------------------------------------------------------------ placements

        /// <summary>
        /// Spawn slot <paramref name="slot"/> of <paramref name="slotCount"/>: spread evenly across the width near the
        /// team's back third, in a shallow arrowhead (the middle player a little further forward). Slot 0 is on the
        /// team's left-hand side as seen facing the opponent.
        /// </summary>
        public Vector3 GetSpawnPoint(TeamId team, int slot, int slotCount)
        {
            float sign = SideSign(team);
            if (sign == 0f) sign = -1f;
            slotCount = Mathf.Max(1, slotCount);
            slot = ((slot % slotCount) + slotCount) % slotCount;

            // Normalised lateral position in -1..1 (0 for a single slot).
            float t = slotCount == 1 ? 0f : Mathf.Lerp(-1f, 1f, slot / (float)(slotCount - 1));
            float halfSpan = Mathf.Max(0f, HalfWidth - Mathf.Max(lateralMargin, confinementInset));

            // Facing +Z, "left" is -X; facing -Z, "left" is +X. Mirror so slot 0 is always on the team's left.
            float x = t * halfSpan * (sign < 0f ? 1f : -1f);

            // Back third, with the central players stepping forward by up to spawnStagger.
            float depth = HalfLength * Mathf.Clamp(spawnDepthFraction, 0.1f, 0.98f);
            depth -= (1f - Mathf.Abs(t)) * spawnStagger;
            depth = Mathf.Clamp(depth, confinementInset + 0.5f, HalfLength - confinementInset - 0.1f);

            return new Vector3(Center.x + x, FloorY, Center.z + sign * depth);
        }

        /// <summary>
        /// Standing spot <paramref name="index"/> inside <paramref name="team"/>'s outfield strip: spots are spread across
        /// the strip (<see cref="outfieldSpotsPerRow"/> per row); extra players use deeper, half-step-shifted rows.
        /// </summary>
        public Vector3 GetOutfieldSpot(TeamId team, int index)
        {
            var strip = GetConfinement(team, CourtZone.Outfield);
            float opponentSign = -SideSign(team);
            if (opponentSign == 0f) opponentSign = 1f;

            int perRow = Mathf.Max(1, outfieldSpotsPerRow);
            index = Mathf.Max(0, index);
            int column = index % perRow;
            int row = index / perRow;

            float halfSpan = Mathf.Max(0f, strip.extents.x - Mathf.Max(0f, lateralMargin - confinementInset));
            float t = perRow == 1 ? 0f : Mathf.Lerp(-1f, 1f, column / (float)(perRow - 1));
            float x = t * halfSpan;
            if ((row & 1) == 1) x += halfSpan / perRow; // stagger odd rows so nobody stands directly behind another

            // First row sits in the middle of the strip; further rows step away from the court.
            float z = strip.center.z + opponentSign * row * outfieldRowSpacing;

            var p = new Vector3(strip.center.x + x, FloorY, z);
            p.x = Mathf.Clamp(p.x, strip.min.x, strip.max.x);
            p.z = Mathf.Clamp(p.z, strip.min.z, strip.max.z);
            return p;
        }

        /// <summary>
        /// Opening-rush ball positions spread along the centre line. Balls alternate slightly toward each half so both
        /// teams have balls on their own side to sprint for (index 0 leans toward Home).
        /// </summary>
        public List<Vector3> GetOpeningBallPositions(int count)
        {
            var result = new List<Vector3>(Mathf.Max(0, count));
            GetOpeningBallPositions(count, result);
            return result;
        }

        /// <summary>Allocation-free overload of <see cref="GetOpeningBallPositions(int)"/>.</summary>
        public void GetOpeningBallPositions(int count, List<Vector3> results)
        {
            results.Clear();
            if (count <= 0) return;

            float halfSpan = Mathf.Max(0f, HalfWidth - lateralMargin);
            float y = FloorY + GameConstants.BallRadius;
            for (int i = 0; i < count; i++)
            {
                float t = count == 1 ? 0f : Mathf.Lerp(-1f, 1f, i / (float)(count - 1));
                float side = (i & 1) == 0 ? -1f : 1f;
                results.Add(new Vector3(Center.x + t * halfSpan, y, Center.z + side * openingBallOffset));
            }
        }

        /// <summary>True if a position is beyond the arena walls / below the floor (ball must respawn).</summary>
        public bool IsOutOfArena(Vector3 position)
        {
            var local = position - Center;
            if (local.y < -Mathf.Max(0.1f, belowFloorLimit)) return true;
            if (Mathf.Abs(local.x) > WallDistanceX + outOfArenaMargin) return true;
            if (Mathf.Abs(local.z) > WallDistanceZ + outOfArenaMargin) return true;
            return false;
        }

        // ------------------------------------------------------------------ helpers

        private Bounds MakeZone(float centerZOffset, float sizeX, float sizeZ)
        {
            float h = Mathf.Max(2f, zoneHeight);
            var c = Center;
            return new Bounds(new Vector3(c.x, c.y + h * 0.5f, c.z + centerZOffset), new Vector3(sizeX, h, sizeZ));
        }

        private static bool ContainsPlanar(in Bounds b, Vector3 p) =>
            p.x >= b.min.x && p.x <= b.max.x && p.z >= b.min.z && p.z <= b.max.z;

        // ------------------------------------------------------------------ gizmos

        private static readonly Color HomeColor = new Color(0.20f, 0.45f, 1.00f);
        private static readonly Color AwayColor = new Color(1.00f, 0.28f, 0.22f);

        private void OnDrawGizmos()
        {
            var c = Center;
            float y = FloorY + 0.02f;

            // Infield halves (translucent fill + outline).
            DrawZone(GetInfieldBounds(TeamId.Home), HomeColor, 0.10f, y);
            DrawZone(GetInfieldBounds(TeamId.Away), AwayColor, 0.10f, y);

            // Outfield strips: tinted with the colour of the team that stands there.
            DrawZone(GetOutfieldBounds(TeamId.Home), HomeColor, 0.05f, y);
            DrawZone(GetOutfieldBounds(TeamId.Away), AwayColor, 0.05f, y);

            // Centre line.
            Gizmos.color = Color.white;
            Gizmos.DrawLine(new Vector3(c.x - HalfWidth, y, c.z), new Vector3(c.x + HalfWidth, y, c.z));

            // Run-off walls.
            Gizmos.color = new Color(0.7f, 0.7f, 0.7f, 0.8f);
            Gizmos.DrawWireCube(new Vector3(c.x, y, c.z), new Vector3(WallDistanceX * 2f, 0f, WallDistanceZ * 2f));
        }

        private void OnDrawGizmosSelected()
        {
            // Spawn slots, outfield spots and opening balls (only when selected to keep the scene view clean).
            const int slots = 3;
            for (int t = 0; t < 2; t++)
            {
                var team = t == 0 ? TeamId.Home : TeamId.Away;
                Gizmos.color = t == 0 ? HomeColor : AwayColor;
                for (int i = 0; i < slots; i++)
                {
                    var p = GetSpawnPoint(team, i, slots);
                    Gizmos.DrawWireSphere(p + Vector3.up * 0.9f, 0.32f);
                    Gizmos.DrawLine(p + Vector3.up * 0.05f, p + Vector3.up * 0.05f + AttackDirection(team) * 0.8f);

                    var o = GetOutfieldSpot(team, i);
                    Gizmos.DrawWireCube(o + Vector3.up * 0.9f, new Vector3(0.5f, 1.8f, 0.5f));
                }
            }

            Gizmos.color = new Color(1f, 0.85f, 0.3f);
            var balls = GetOpeningBallPositions(6);
            for (int i = 0; i < balls.Count; i++) Gizmos.DrawSphere(balls[i], GameConstants.BallRadius);
        }

        private static void DrawZone(Bounds b, Color color, float fillAlpha, float y)
        {
            var center = new Vector3(b.center.x, y, b.center.z);
            var size = new Vector3(b.size.x, 0.01f, b.size.z);
            Gizmos.color = new Color(color.r, color.g, color.b, fillAlpha);
            Gizmos.DrawCube(center, size);
            Gizmos.color = new Color(color.r, color.g, color.b, 0.9f);
            Gizmos.DrawWireCube(center, size);
        }
    }
}
