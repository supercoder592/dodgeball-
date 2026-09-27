using System;
using DodgeballUltra.Combat;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Juice
{
    /// <summary>Everything the juice pipeline needs to know about a hit.</summary>
    public struct HitJuiceRequest
    {
        public Vector3 Point;
        public Vector3 Normal;          // collision normal (for squash &amp; stretch orientation)
        public float SpeedKmh;          // drives intensity
        public DodgeballPlayer Victim;  // flashed
        public DodgeBall Ball;          // squashed
        public bool Eliminated;
        public bool LocalPlayerInvolved;
    }

    /// <summary>Everything the juice pipeline needs to know about a catch.</summary>
    public struct CatchJuiceRequest
    {
        public Vector3 Point;
        public Vector3 Normal;
        public float SpeedKmh;
        public DodgeballPlayer Catcher;
        public DodgeBall Ball;
        public bool Perfect;
        public bool LocalPlayerInvolved;
    }

    /// <summary>
    /// CONTRACT (kernel, spec file) - Hit-Feel &amp; Game Juice framework.
    /// Subscribes to BallHitPlayerEvent and BallCaughtEvent: every hit and every perfect catch runs the pipeline
    /// <c>Hitstop (0.03-0.1 s, unscaled) -> Perlin camera shake -> ball squash &amp; stretch along the normal -> 0.05 s white hit-flash
    /// -> screen pulse</c>, all tuned by a JuiceProfile ScriptableObject.
    /// <para>Owner module: Juice.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class JuiceManager : MonoBehaviour
    {
        public static JuiceManager Instance { get; private set; }

        /// <summary>True while a hitstop is freezing time.</summary>
        public bool IsHitstopActive { get; private set; }

        // ------------------------------------------------------------------ IMPLEMENT: Juice module

        /// <summary>Runs the full hit pipeline.</summary>
        public void PlayHit(in HitJuiceRequest request) => throw new NotImplementedException();

        /// <summary>Runs the catch pipeline (full pipeline for perfect catches, a light version for normal ones).</summary>
        public void PlayCatch(in CatchJuiceRequest request) => throw new NotImplementedException();

        /// <summary>
        /// Frame freeze: sets Time.timeScale to <paramref name="timeScale"/> for <paramref name="duration"/> unscaled seconds
        /// (clamped to 0.03-0.1 s unless <paramref name="allowLong"/>), then restores the previous time scale. Overlapping
        /// requests extend rather than stack.
        /// </summary>
        public void Hitstop(float duration, float timeScale = 0f, bool allowLong = false) => throw new NotImplementedException();

        /// <summary>
        /// Procedural Perlin-noise camera shake on every registered <see cref="CameraShaker"/>. Amplitude falls off with
        /// distance from <paramref name="worldSource"/> when given.
        /// </summary>
        public void Shake(float amplitude, float frequency, float duration, Vector3? worldSource = null) => throw new NotImplementedException();

        /// <summary>Adds trauma (0..1) to the shakers (trauma-squared shake model).</summary>
        public void AddTrauma(float trauma, Vector3? worldSource = null) => throw new NotImplementedException();

        /// <summary>Squash along <paramref name="normal"/> and stretch perpendicular (volume-preserving), springing back.</summary>
        public void SquashAndStretch(Transform target, Vector3 normal, float intensity, float duration = 0.18f) => throw new NotImplementedException();

        /// <summary>0.05 s (default) white flash on the player's materials.</summary>
        public void Flash(DodgeballPlayer player, Color? color = null, float duration = Core.GameConstants.HitFlashDuration) => throw new NotImplementedException();

        public void RegisterShaker(CameraShaker shaker) => throw new NotImplementedException();
        public void UnregisterShaker(CameraShaker shaker) => throw new NotImplementedException();
    }
}
