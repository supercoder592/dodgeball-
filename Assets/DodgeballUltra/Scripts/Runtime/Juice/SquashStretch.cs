using System;
using UnityEngine;

namespace DodgeballUltra.Juice
{
    /// <summary>
    /// CONTRACT (kernel) - volume-preserving squash &amp; stretch on a visual transform along an arbitrary world normal,
    /// with a damped spring back to the rest scale. Put it on the ball's VisualRoot (never on physics objects).
    /// Also supports velocity-based stretch while flying.
    /// <para>Owner module: Juice.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SquashStretch : MonoBehaviour
    {
        [Tooltip("Stretch along velocity per m/s while flying (0 disables).")] public float velocityStretch = 0.004f;
        [Tooltip("Max velocity stretch factor.")] public float maxVelocityStretch = 1.25f;

        // IMPLEMENT: Juice module
        /// <summary>Impact squash along <paramref name="worldNormal"/>. Intensity 0..1.</summary>
        public void Impact(Vector3 worldNormal, float intensity, float duration = 0.18f) => throw new NotImplementedException();

        /// <summary>Provide the current world velocity for in-flight stretch (zero to disable).</summary>
        public void SetVelocity(Vector3 worldVelocity) => throw new NotImplementedException();
    }
}
