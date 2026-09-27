using System;
using UnityEngine;

namespace DodgeballUltra.Arena
{
    /// <summary>
    /// CONTRACT (kernel) - builds a complete, realistic-looking indoor sports arena at runtime when the scene does not
    /// contain one (the editor ArenaSceneBuilder produces a baked, higher-quality version of the same layout):
    /// hardwood court with painted lines, run-off, walls, bleachers, floodlight rig, colliders on GameLayers.Court,
    /// invisible PlayerBlocker walls on the centre line and zone edges, and the Court component.
    /// <para>Owner module: Arena.</para>
    /// </summary>
    public static class RuntimeArenaBuilder
    {
        // IMPLEMENT: Arena module
        /// <summary>Builds the arena under a new root object and returns its Court.</summary>
        public static Match.Court Build(Transform parent = null) => throw new NotImplementedException();
    }
}
