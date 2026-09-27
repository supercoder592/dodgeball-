using System;
using System.Collections.Generic;
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

        public Vector3 Center => transform.position;
        public float FloorY => transform.position.y;

        // IMPLEMENT: Match module
        /// <summary>-1 for Home (negative Z), +1 for Away.</summary>
        public float SideSign(TeamId team) => throw new NotImplementedException();

        /// <summary>Unit vector from <paramref name="team"/>'s half toward the opponent.</summary>
        public Vector3 AttackDirection(TeamId team) => throw new NotImplementedException();

        public Bounds GetInfieldBounds(TeamId team) => throw new NotImplementedException();

        /// <summary>The strip where <paramref name="team"/>'s eliminated players stand (behind the opponent's baseline).</summary>
        public Bounds GetOutfieldBounds(TeamId team) => throw new NotImplementedException();

        /// <summary>Movement confinement for a player of <paramref name="team"/> in <paramref name="zone"/>.</summary>
        public Bounds GetConfinement(TeamId team, CourtZone zone) => throw new NotImplementedException();

        public bool IsInInfield(TeamId team, Vector3 position) => throw new NotImplementedException();

        /// <summary>Which team's infield half contains <paramref name="position"/> (None if outside both).</summary>
        public TeamId GetHalfOwner(Vector3 position) => throw new NotImplementedException();

        public Vector3 GetSpawnPoint(TeamId team, int slot, int slotCount) => throw new NotImplementedException();
        public Vector3 GetOutfieldSpot(TeamId team, int index) => throw new NotImplementedException();

        /// <summary>Opening-rush ball positions spread along the centre line.</summary>
        public List<Vector3> GetOpeningBallPositions(int count) => throw new NotImplementedException();

        /// <summary>True if a position is beyond the arena walls / below the floor (ball must respawn).</summary>
        public bool IsOutOfArena(Vector3 position) => throw new NotImplementedException();

        public Quaternion GetSpawnRotation(TeamId team) => throw new NotImplementedException();
    }
}
