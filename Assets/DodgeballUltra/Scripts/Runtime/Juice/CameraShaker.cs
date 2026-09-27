using UnityEngine;

namespace DodgeballUltra.Juice
{
    /// <summary>
    /// CONTRACT (kernel) - applies procedural Perlin-noise shake (position + rotation) to its transform using unscaled time
    /// (so it keeps shaking during hitstop). Put it on a pivot between the camera rig and the Camera. Configurable decay,
    /// amplitude, frequency; trauma-based (shake = trauma^2).
    /// <para>
    /// Model (after Squirrel Eiserloh, "Juicing Your Cameras With Math", GDC 2016):
    /// <code>
    /// trauma      : 0..1, raised by impacts (AddTrauma), falls linearly by traumaDecay per unscaled second
    /// timed shakes: explicit Shake(amplitude, frequency, duration) requests whose trauma-equivalent value fades
    ///               linearly to 0 over their duration (layered on top of trauma)
    /// shake       = clamp01( trauma^exponent + sum(timed_i^exponent) ) * GlobalIntensity
    /// offset.pos  = shake * maxOffset * (perlin_x, perlin_y, perlin_z * forwardWeight)      (metres, local space)
    /// offset.rot  = shake * (perlin_pitch * maxAngles.x, perlin_yaw * maxAngles.y, perlin_roll * maxAngles.z)
    /// </code>
    /// Each axis samples <see cref="Mathf.PerlinNoise"/> with its own random seed, advancing a phase accumulator at the
    /// current frequency (so changing frequency never makes the noise jump). The offsets are applied as LOCAL offsets
    /// on top of a cached rest pose; the component never drifts the transform away from that pose.
    /// </para>
    /// <para>Registers itself with <see cref="JuiceManager"/> while enabled (the registry is static, so creation order of
    /// the manager and the shakers does not matter).</para>
    /// <para>Owner module: Juice.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(250)]
    public sealed class CameraShaker : MonoBehaviour
    {
        // ------------------------------------------------------------------ tuning (contract fields)

        [Header("Amplitude at trauma 1")]
        [Tooltip("Max translational offset (m) at trauma 1.")]
        [Min(0f)] public float maxOffset = 0.12f;

        [Tooltip("Max rotational offset (deg) at trauma 1 (pitch, yaw, roll).")]
        public Vector3 maxAngles = new Vector3(2.2f, 2.2f, 3.5f);

        [Header("Noise & decay")]
        [Tooltip("Perlin noise frequency (Hz).")]
        [Min(0.1f)] public float frequency = 22f;

        [Tooltip("Trauma lost per second.")]
        [Min(0f)] public float traumaDecay = 1.6f;

        [Tooltip("Shake = trauma ^ exponent.")]
        [Range(1f, 4f)] public float traumaExponent = 2f;

        // ------------------------------------------------------------------ additional tuning

        [Tooltip("Weight of the translational shake along the view axis (dolly). Forward shake reads less clearly than " +
                 "lateral/vertical shake, so it is damped.")]
        [Range(0f, 1f)] public float forwardOffsetWeight = 0.35f;

        [Tooltip("Upper bound of the combined shake intensity (1 = the maxOffset / maxAngles envelope).")]
        [Range(0.1f, 2f)] public float maxShakeIntensity = 1f;

        [Tooltip("Upper clamp of the unscaled delta time used by the shaker (avoids a huge trauma drop after a hitch).")]
        [Range(0.02f, 0.25f)] public float maxDeltaTime = 0.1f;

        /// <summary>
        /// Global accessibility multiplier applied to every shaker (options menu "camera shake" slider). 0 disables shake.
        /// </summary>
        public static float GlobalIntensity { get; set; } = 1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => GlobalIntensity = 1f;

        // ------------------------------------------------------------------ state

        /// <summary>Current trauma (0..1).</summary>
        public float Trauma { get; private set; }

        /// <summary>Shake intensity applied this frame (after exponent, timed shakes, clamp and global scale).</summary>
        public float CurrentIntensity { get; private set; }

        /// <summary>True while any trauma or timed shake is active.</summary>
        public bool IsShaking => CurrentIntensity > 0f || Trauma > 0f || _timedCount > 0;

        /// <summary>Local position the shake is centred on.</summary>
        public Vector3 RestLocalPosition => _restPosition;

        /// <summary>Local rotation the shake is centred on.</summary>
        public Quaternion RestLocalRotation => _restRotation;

        /// <summary>One explicit, timed shake request (plain data, stored in a fixed array: no allocations).</summary>
        private struct TimedShake
        {
            public float Amplitude;  // trauma-equivalent at the start
            public float Frequency;  // Hz
            public float Duration;   // unscaled seconds
            public float Elapsed;    // unscaled seconds

            /// <summary>Trauma-equivalent value now: fades linearly to zero like trauma does.</summary>
            public float Value => Duration <= 0f ? 0f : Amplitude * (1f - Mathf.Clamp01(Elapsed / Duration));
        }

        private const int MaxTimedShakes = 8;
        private readonly TimedShake[] _timed = new TimedShake[MaxTimedShakes];
        private int _timedCount;

        private Vector3 _restPosition;
        private Quaternion _restRotation = Quaternion.identity;
        private bool _restCached;
        private bool _offsetApplied;

        // Independent noise seeds per channel (x, y, z translation; pitch, yaw, roll rotation).
        private float _seedX, _seedY, _seedZ, _seedPitch, _seedYaw, _seedRoll;

        // Phase accumulator (cycles). Advancing phase by f*dt keeps the noise continuous when f changes.
        private float _phase;

        // ------------------------------------------------------------------ public API

        /// <summary>Adds (or, with a negative value, removes) trauma. Trauma is clamped to 0..1.</summary>
        public void AddTrauma(float amount)
        {
            if (float.IsNaN(amount)) return;
            Trauma = Mathf.Clamp01(Trauma + amount);
        }

        /// <summary>Explicit shake: amplitude (0..1 trauma-equivalent), frequency override, duration.</summary>
        /// <remarks>
        /// The request is layered on top of trauma. Its trauma-equivalent value fades linearly from
        /// <paramref name="amplitude"/> to 0 over <paramref name="duration"/> unscaled seconds and is raised to
        /// <see cref="traumaExponent"/> like trauma, so weak shakes stay subtle. While it dominates, the noise runs at
        /// <paramref name="frequencyOverride"/> (values &lt;= 0 keep <see cref="frequency"/>).
        /// </remarks>
        public void Shake(float amplitude, float frequencyOverride, float duration)
        {
            if (amplitude <= 0f || duration <= 0f || float.IsNaN(amplitude)) return;

            var request = new TimedShake
            {
                Amplitude = Mathf.Min(amplitude, 2f),
                Frequency = frequencyOverride > 0f ? frequencyOverride : frequency,
                Duration = duration,
                Elapsed = 0f,
            };

            if (_timedCount < MaxTimedShakes)
            {
                _timed[_timedCount++] = request;
                return;
            }

            // Full: replace the weakest running shake if the new one is stronger.
            int weakest = 0;
            float weakestValue = float.MaxValue;
            for (int i = 0; i < _timedCount; i++)
            {
                float v = _timed[i].Value;
                if (v < weakestValue)
                {
                    weakestValue = v;
                    weakest = i;
                }
            }
            if (request.Amplitude > weakestValue) _timed[weakest] = request;
        }

        /// <summary>Stops every shake immediately and returns the transform to its rest pose.</summary>
        public void StopAll()
        {
            Trauma = 0f;
            _timedCount = 0;
            CurrentIntensity = 0f;
            RestorePose();
        }

        /// <summary>
        /// Redefines the local pose the shake is centred on (call it if the owner moves this transform on purpose).
        /// </summary>
        public void SetRestPose(Vector3 localPosition, Quaternion localRotation)
        {
            _restPosition = localPosition;
            _restRotation = localRotation;
            _restCached = true;
            if (!_offsetApplied)
            {
                transform.localPosition = _restPosition;
                transform.localRotation = _restRotation;
            }
        }

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            CacheRestPose();

            // Different seeds per axis decorrelate the channels (otherwise the camera would move diagonally only).
            // Seeds are spread far apart on the noise lattice; the phase runs along the other axis.
            float baseSeed = Random.Range(0f, 512f);
            _seedX = baseSeed + 11.3f;
            _seedY = baseSeed + 47.9f;
            _seedZ = baseSeed + 83.1f;
            _seedPitch = baseSeed + 131.7f;
            _seedYaw = baseSeed + 173.3f;
            _seedRoll = baseSeed + 219.9f;
            _phase = Random.Range(0f, 100f);
        }

        private void OnEnable()
        {
            CacheRestPose();
            JuiceManager.AddShaker(this);
        }

        private void OnDisable()
        {
            JuiceManager.RemoveShaker(this);
            RestorePose();
        }

        private void LateUpdate()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, maxDeltaTime);

            // 1) decay trauma and age timed shakes (unscaled: shake continues through hitstop / slow motion).
            if (Trauma > 0f) Trauma = Mathf.Max(0f, Trauma - traumaDecay * dt);

            float exponent = Mathf.Max(1f, traumaExponent);
            float shake = Trauma > 0f ? Mathf.Pow(Trauma, exponent) : 0f;
            float dominantShake = shake;
            float currentFrequency = frequency;

            for (int i = _timedCount - 1; i >= 0; i--)
            {
                _timed[i].Elapsed += dt;
                if (_timed[i].Elapsed >= _timed[i].Duration)
                {
                    // swap-remove expired request
                    _timed[i] = _timed[--_timedCount];
                    continue;
                }

                float contribution = Mathf.Pow(_timed[i].Value, exponent);
                shake += contribution;
                if (contribution > dominantShake)
                {
                    dominantShake = contribution;
                    currentFrequency = _timed[i].Frequency;
                }
            }

            shake = Mathf.Min(shake, maxShakeIntensity) * Mathf.Max(0f, GlobalIntensity);
            CurrentIntensity = shake;

            if (shake <= 1e-4f)
            {
                RestorePose();
                return;
            }

            // 2) advance the noise phase at the dominant frequency.
            _phase += dt * Mathf.Max(0.1f, currentFrequency);
            if (_phase > 10000f) _phase -= 10000f; // keep float precision (Perlin is smooth, the wrap is not noticeable)

            // 3) sample centred Perlin noise (-1..1) per channel.
            float nx = Noise(_seedX);
            float ny = Noise(_seedY);
            float nz = Noise(_seedZ) * forwardOffsetWeight;
            float np = Noise(_seedPitch);
            float nyaw = Noise(_seedYaw);
            float nr = Noise(_seedRoll);

            // 4) apply as local offsets on top of the rest pose.
            Vector3 positionOffset = new Vector3(nx, ny, nz) * (maxOffset * shake);
            Quaternion rotationOffset = Quaternion.Euler(np * maxAngles.x * shake, nyaw * maxAngles.y * shake, nr * maxAngles.z * shake);

            transform.localPosition = _restPosition + _restRotation * positionOffset;
            transform.localRotation = _restRotation * rotationOffset;
            _offsetApplied = true;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Perlin noise remapped to roughly -1..1 for the channel with the given seed.</summary>
        private float Noise(float seed) => (Mathf.PerlinNoise(seed, _phase) - 0.5f) * 2f;

        private void CacheRestPose()
        {
            if (_restCached) return;
            _restPosition = transform.localPosition;
            _restRotation = transform.localRotation;
            _restCached = true;
        }

        private void RestorePose()
        {
            if (!_offsetApplied || !_restCached) return;
            transform.localPosition = _restPosition;
            transform.localRotation = _restRotation;
            _offsetApplied = false;
        }
    }
}
