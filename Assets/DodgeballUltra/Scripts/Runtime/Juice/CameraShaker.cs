using System;
using UnityEngine;

namespace DodgeballUltra.Juice
{
    /// <summary>
    /// CONTRACT (kernel) - applies procedural Perlin-noise shake (position + rotation) to its transform using unscaled time
    /// (so it keeps shaking during hitstop). Put it on a pivot between the camera rig and the Camera. Configurable decay,
    /// amplitude, frequency; trauma-based (shake = trauma^2).
    /// <para>Owner module: Juice.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CameraShaker : MonoBehaviour
    {
        [Tooltip("Max translational offset (m) at trauma 1.")] public float maxOffset = 0.12f;
        [Tooltip("Max rotational offset (deg) at trauma 1 (pitch, yaw, roll).")] public Vector3 maxAngles = new Vector3(2.2f, 2.2f, 3.5f);
        [Tooltip("Perlin noise frequency (Hz).")] public float frequency = 22f;
        [Tooltip("Trauma lost per second.")] public float traumaDecay = 1.6f;
        [Tooltip("Shake = trauma ^ exponent.")] public float traumaExponent = 2f;

        public float Trauma { get; private set; }

        // IMPLEMENT: Juice module
        public void AddTrauma(float amount) => throw new NotImplementedException();

        /// <summary>Explicit shake: amplitude (0..1 trauma-equivalent), frequency override, duration.</summary>
        public void Shake(float amplitude, float frequencyOverride, float duration) => throw new NotImplementedException();
    }
}
