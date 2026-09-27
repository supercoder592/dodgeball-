using UnityEngine;

namespace DodgeballUltra.Juice
{
    /// <summary>
    /// CONTRACT (kernel) - volume-preserving squash &amp; stretch on a visual transform along an arbitrary world normal,
    /// with a damped spring back to the rest scale. Put it on the ball's VisualRoot (never on physics objects).
    /// Also supports velocity-based stretch while flying.
    /// <para>
    /// <b>Maths.</b> A deformation along a unit axis <c>n</c> with factor <c>s</c> is the symmetric matrix
    /// <c>M = R · diag(p, s, p) · R⁻¹</c> where <c>R</c> rotates +Y onto <c>n</c> and <c>p = 1/√s</c> keeps the volume
    /// (<c>det M = s·p² = 1</c>). Impact squash uses <c>s = 1 - x</c> where <c>x</c> is the spring displacement
    /// (x &gt; 0 squashes, the overshoot x &lt; 0 stretches); flight stretch uses <c>s = 1 + k·|v|</c> along the velocity.
    /// </para>
    /// <para>
    /// <b>Hierarchy.</b> A Transform's scale is axis-aligned in its local space, so an arbitrary axis needs a rotated
    /// scale pivot plus a counter-rotation. On first use the component inserts (once) two such pairs between the
    /// VisualRoot and its original children:
    /// <code>
    /// VisualRoot (this)                      rotation/position/scale owned by the Combat module (spin, radius) - untouched
    ///   └─ SquashAxis     rot = R_impact, scale = (p, s, p)
    ///        └─ SquashCounter  rot = R_impact⁻¹
    ///             └─ StretchAxis    rot = R_velocity, scale = (p, s, p)
    ///                  └─ StretchCounter rot = R_velocity⁻¹
    ///                       └─ original children (ball mesh ...)  local transforms unchanged
    /// </code>
    /// At rest every pivot is identity, so the children render exactly as before. The world-space axes are converted to
    /// the VisualRoot's local space every frame, which keeps the deformation fixed in world space while the ball spins.
    /// When the mesh renderer lives directly on the VisualRoot (no children to adopt), the component falls back to an
    /// approximation written to <c>VisualRoot.localScale</c>: the local-space diagonal of the deformation matrix,
    /// renormalised to preserve volume, multiplied onto the externally owned rest scale (which is re-captured whenever
    /// another system changes it, e.g. Overcharge's +20 % radius).
    /// </para>
    /// <para>
    /// <b>Time.</b> Everything runs on unscaled time: the squash plays out visibly during the hitstop freeze frame.
    /// The spring is integrated analytically (exact for any frame time, no instability at low frame rates).
    /// </para>
    /// <para>Owner module: Juice.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(300)]
    public sealed class SquashStretch : MonoBehaviour
    {
        // ------------------------------------------------------------------ tuning (contract fields)

        [Header("Flight stretch")]
        [Tooltip("Stretch along velocity per m/s while flying (0 disables).")]
        [Min(0f)] public float velocityStretch = 0.004f;

        [Tooltip("Max velocity stretch factor.")]
        [Range(1f, 2f)] public float maxVelocityStretch = 1.25f;

        // ------------------------------------------------------------------ additional tuning

        [Tooltip("How quickly the flight stretch follows speed changes (1/s, unscaled).")]
        [Min(0.1f)] public float velocityStretchSharpness = 28f;

        [Tooltip("Speeds below this (m/s) produce no flight stretch (rolling/held balls stay round).")]
        [Min(0f)] public float minStretchSpeed = 4f;

        [Tooltip("When no explicit SetVelocity arrived recently, estimate the velocity from the transform's motion.")]
        public bool estimateVelocityFromMotion = true;

        [Tooltip("An explicit SetVelocity stays authoritative for this many scaled seconds.")]
        [Min(0f)] public float explicitVelocityHoldTime = 0.1f;

        [Tooltip("Estimated speeds above this (m/s) are treated as teleports and ignored (220 km/h = 61 m/s).")]
        [Min(1f)] public float maxPlausibleSpeed = 75f;

        [Header("Impact squash spring")]
        [Tooltip("Maximum squash fraction accepted by Impact (0.9 = flattened to 10 %).")]
        [Range(0.05f, 0.9f)] public float maxSquash = 0.9f;

        [Tooltip("Damping ratio of the spring back (lower = more wobble; foam balls ring slightly).")]
        [Range(0.05f, 1.5f)] public float springDamping = 0.42f;

        [Tooltip("Spring cycles per impact duration (1 = one full squash/stretch oscillation per 'duration').")]
        [Range(0.25f, 3f)] public float springCyclesPerDuration = 1f;

        [Tooltip("A weaker impact arriving while a stronger squash is still playing is ignored when it is below this " +
                 "fraction of the current squash.")]
        [Range(0f, 1f)] public float impactReplaceThreshold = 0.5f;

        // ------------------------------------------------------------------ state

        /// <summary>Current impact spring displacement (0 = rest, &gt;0 squash along the impact normal).</summary>
        public float CurrentSquash => _x;

        /// <summary>Current flight stretch factor along the velocity (1 = none).</summary>
        public float CurrentStretch => _stretch;

        // impact spring (analytic damped harmonic oscillator)
        private Vector3 _impactNormal = Vector3.up;   // world space
        private float _x;                              // displacement now
        private float _v;                              // velocity now (1/s)
        private float _omega = 30f;                    // natural angular frequency (rad/s)
        private int _impactFrame = -1;                 // frame of the last impact (rendered at full squash)

        // flight stretch
        private Vector3 _velocityAxis = Vector3.forward; // world space
        private float _stretch = 1f;
        private Vector3 _explicitVelocity;
        private float _explicitVelocityTime = float.NegativeInfinity;
        private bool _hasExplicitVelocity;
        private Vector3 _estimatedVelocity;
        private Vector3 _lastPosition;
        private float _lastMoveTime;
        private bool _hasLastPosition;
        private const float MotionStaleTime = 0.08f; // scaled seconds without movement => stopped

        // hierarchy
        private Transform _squashAxis, _squashCounter, _stretchAxis, _stretchCounter;
        private bool _pivotsBuilt;
        private bool _fallbackMode;
        private Vector3 _fallbackRestScale = Vector3.one;
        private Vector3 _fallbackLastWritten = Vector3.one;
        private bool _fallbackWritten;
        private bool _deformed;

        private const float Epsilon = 1e-4f;

        // ------------------------------------------------------------------ public API

        /// <summary>Impact squash along <paramref name="worldNormal"/>. Intensity 0..1.</summary>
        /// <param name="worldNormal">Collision normal (sign does not matter).</param>
        /// <param name="intensity">Squash fraction: the scale along the normal becomes <c>1 - intensity</c>
        /// (clamped to <see cref="maxSquash"/>); the perpendicular axes grow by <c>1/√(1 - intensity)</c>.</param>
        /// <param name="duration">Unscaled seconds of one spring oscillation back to rest.</param>
        public void Impact(Vector3 worldNormal, float intensity, float duration = 0.18f)
        {
            if (float.IsNaN(intensity) || intensity <= 0f) return;
            intensity = Mathf.Min(intensity, maxSquash);

            // A weak bounce must not cut a strong squash short (that would pop).
            if (Mathf.Abs(_x) > Epsilon && intensity < Mathf.Abs(_x) * impactReplaceThreshold) return;

            if (worldNormal.sqrMagnitude < 1e-8f)
                worldNormal = _velocityAxis.sqrMagnitude > 1e-8f ? _velocityAxis : Vector3.up;

            EnsurePivots(adoptNewChildren: true);

            _impactNormal = worldNormal.normalized;
            _x = intensity;   // impacts deform instantly ...
            _v = 0f;          // ... and spring back from rest velocity
            _impactFrame = Time.frameCount;
            _omega = 2f * Mathf.PI * springCyclesPerDuration / Mathf.Max(0.02f, duration);
            enabled = true;
        }

        /// <summary>Provide the current world velocity for in-flight stretch (zero to disable).</summary>
        public void SetVelocity(Vector3 worldVelocity)
        {
            _explicitVelocity = worldVelocity;
            _explicitVelocityTime = Time.time;
            _hasExplicitVelocity = true;
            if (worldVelocity.sqrMagnitude > minStretchSpeed * minStretchSpeed) enabled = true;
        }

        /// <summary>Snaps back to the undeformed rest shape immediately (pooling, teleports).</summary>
        public void ResetDeformation()
        {
            _x = 0f;
            _v = 0f;
            _stretch = 1f;
            _estimatedVelocity = Vector3.zero;
            _hasLastPosition = false;
            ApplyIdentity();
        }

        // ------------------------------------------------------------------ lifecycle

        private void OnEnable()
        {
            _hasLastPosition = false;
        }

        private void OnDisable()
        {
            // Leave the visual exactly as it was authored when disabled / pooled.
            _x = 0f;
            _v = 0f;
            _stretch = 1f;
            ApplyIdentity();
        }

        private void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;
            dt = Mathf.Min(dt, 0.1f);

            // The impact frame renders the full squash: its delta time elapsed before the contact happened.
            if (Time.frameCount != _impactFrame) StepSpring(dt);
            StepFlightStretch(dt);

            bool squashActive = Mathf.Abs(_x) > Epsilon || Mathf.Abs(_v) > Epsilon * 10f;
            bool stretchActive = _stretch > 1f + Epsilon;

            if (!squashActive && !stretchActive)
            {
                if (_deformed) ApplyIdentity();
                return;
            }

            if (!_pivotsBuilt) EnsurePivots(adoptNewChildren: false);

            float squashScale = Mathf.Max(0.05f, 1f - _x);
            if (_fallbackMode) ApplyFallback(squashScale, _stretch);
            else ApplyPivots(squashScale, _stretch);
            _deformed = true;
        }

        // ------------------------------------------------------------------ simulation

        /// <summary>
        /// Exact step of the damped oscillator x'' + 2ζωx' + ω²x = 0 from state (x, v) over dt
        /// (under-, critically and over-damped cases).
        /// </summary>
        private void StepSpring(float dt)
        {
            if (Mathf.Abs(_x) < Epsilon && Mathf.Abs(_v) < Epsilon * 10f)
            {
                _x = 0f;
                _v = 0f;
                return;
            }

            float w = _omega;
            float z = springDamping;
            float x0 = _x, v0 = _v;

            if (z < 0.999f)
            {
                float wd = w * Mathf.Sqrt(1f - z * z);
                float decay = Mathf.Exp(-z * w * dt);
                float cos = Mathf.Cos(wd * dt);
                float sin = Mathf.Sin(wd * dt);
                float b = (v0 + z * w * x0) / wd;
                _x = decay * (x0 * cos + b * sin);
                // derivative of e^{-zwt}(x0 cos + b sin)
                _v = decay * ((-z * w) * (x0 * cos + b * sin) + (-x0 * wd * sin + b * wd * cos));
            }
            else if (z <= 1.001f)
            {
                // critically damped: x = (x0 + (v0 + w x0) t) e^{-wt}
                float decay = Mathf.Exp(-w * dt);
                float c = v0 + w * x0;
                _x = (x0 + c * dt) * decay;
                _v = (c - w * (x0 + c * dt)) * decay;
            }
            else
            {
                // over-damped: two real roots
                float root = Mathf.Sqrt(z * z - 1f);
                float r1 = -w * (z - root);
                float r2 = -w * (z + root);
                float c2 = (v0 - r1 * x0) / (r2 - r1);
                float c1 = x0 - c2;
                float e1 = Mathf.Exp(r1 * dt), e2 = Mathf.Exp(r2 * dt);
                _x = c1 * e1 + c2 * e2;
                _v = c1 * r1 * e1 + c2 * r2 * e2;
            }

            // Keep the overshoot physically sensible (never invert or explode).
            _x = Mathf.Clamp(_x, -0.6f, maxSquash);
        }

        private void StepFlightStretch(float dt)
        {
            Vector3 velocity = SampleVelocity();
            float speed = velocity.magnitude;

            float target = 1f;
            if (velocityStretch > 0f && speed > minStretchSpeed)
            {
                target = Mathf.Min(maxVelocityStretch, 1f + velocityStretch * speed);
                _velocityAxis = velocity / speed;
            }

            // Exponential smoothing (frame-rate independent).
            float k = 1f - Mathf.Exp(-velocityStretchSharpness * dt);
            _stretch = Mathf.Lerp(_stretch, target, k);
            if (Mathf.Abs(_stretch - 1f) < Epsilon) _stretch = 1f;
        }

        /// <summary>Explicit velocity (recent SetVelocity call) or a motion estimate.</summary>
        private Vector3 SampleVelocity()
        {
            UpdateMotionEstimate();

            if (_hasExplicitVelocity && Time.time - _explicitVelocityTime <= explicitVelocityHoldTime)
                return _explicitVelocity;

            // Stale explicit value: fall back to the estimate (or nothing).
            _hasExplicitVelocity = false;
            return estimateVelocityFromMotion ? _estimatedVelocity : Vector3.zero;
        }

        /// <summary>
        /// Velocity from displacement between two frames in which the transform actually moved, divided by the scaled
        /// time between them. Measuring "between moves" (instead of per rendered frame) keeps the estimate exact when a
        /// non-interpolated body only moves on FixedUpdate steps (several rendered frames per physics step, or the
        /// reverse), and during hitstop (scaled time barely advances, the estimate is simply held).
        /// </summary>
        private void UpdateMotionEstimate()
        {
            Vector3 position = transform.position;
            float now = Time.time;

            if (!_hasLastPosition)
            {
                _lastPosition = position;
                _lastMoveTime = now;
                _hasLastPosition = true;
                _estimatedVelocity = Vector3.zero;
                return;
            }

            Vector3 delta = position - _lastPosition;
            float elapsed = now - _lastMoveTime;

            if (delta.sqrMagnitude > 1e-10f)
            {
                if (elapsed > 1e-5f)
                {
                    Vector3 raw = delta / elapsed;
                    // A jump faster than any throw is a teleport (Houdini, Chrono rewind, respawn): ignore it.
                    _estimatedVelocity = raw.sqrMagnitude <= maxPlausibleSpeed * maxPlausibleSpeed ? raw : Vector3.zero;
                }
                _lastPosition = position;
                _lastMoveTime = now;
            }
            else if (elapsed > MotionStaleTime)
            {
                _estimatedVelocity = Vector3.zero; // stopped (held still, resting, stasis)
            }
        }

        // ------------------------------------------------------------------ hierarchy & application

        /// <summary>
        /// Builds the pivot chain once. Children that are pure VFX (particles, trails, lines) are left on the VisualRoot
        /// so they are not deformed; everything else is re-parented without changing its local transform.
        /// </summary>
        private void EnsurePivots(bool adoptNewChildren)
        {
            if (!_pivotsBuilt)
            {
                _pivotsBuilt = true;

                // Mesh directly on this transform and nothing to adopt: pivots would deform nothing -> fallback.
                bool hasOwnRenderer = GetComponent<Renderer>() != null;
                int adoptable = CountAdoptableChildren();
                if (hasOwnRenderer || adoptable == 0)
                {
                    _fallbackMode = true;
                    _fallbackRestScale = transform.localScale;
                    _fallbackLastWritten = _fallbackRestScale;
                    return;
                }

                int layer = gameObject.layer;
                _squashAxis = CreatePivot("SquashAxis", transform, layer);
                _squashCounter = CreatePivot("SquashCounter", _squashAxis, layer);
                _stretchAxis = CreatePivot("StretchAxis", _squashCounter, layer);
                _stretchCounter = CreatePivot("StretchCounter", _stretchAxis, layer);
                AdoptChildren();
                return;
            }

            if (adoptNewChildren && !_fallbackMode) AdoptChildren();
        }

        private static Transform CreatePivot(string name, Transform parent, int layer)
        {
            var go = new GameObject(name) { layer = layer };
            var t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
            return t;
        }

        private int CountAdoptableChildren()
        {
            int n = 0;
            for (int i = 0; i < transform.childCount; i++)
                if (IsAdoptable(transform.GetChild(i))) n++;
            return n;
        }

        private bool IsAdoptable(Transform child)
        {
            if (child == _squashAxis) return false;
            // VFX children are not deformed (their emission shape / width would be skewed).
            if (child.GetComponent<ParticleSystem>() != null) return false;
            if (child.GetComponent<TrailRenderer>() != null) return false;
            if (child.GetComponent<LineRenderer>() != null) return false;
            return true;
        }

        private void AdoptChildren()
        {
            if (_stretchCounter == null) return;
            // Iterate backwards: SetParent removes the child from this list.
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (!IsAdoptable(child)) continue;
                // worldPositionStays = false keeps local values; the counter chain is identity at rest, so the world
                // transform is unchanged.
                child.SetParent(_stretchCounter, false);
            }
        }

        private void ApplyPivots(float squashScale, float stretchScale)
        {
            if (_squashAxis == null || _stretchCounter == null)
            {
                // Pivots were destroyed externally: rebuild on the next impact.
                _pivotsBuilt = false;
                return;
            }

            Quaternion toLocal = Quaternion.Inverse(transform.rotation);

            // Impact axis
            Quaternion rImpact = Quaternion.FromToRotation(Vector3.up, toLocal * _impactNormal);
            float pImpact = 1f / Mathf.Sqrt(squashScale);
            _squashAxis.localRotation = rImpact;
            _squashAxis.localScale = new Vector3(pImpact, squashScale, pImpact);
            _squashCounter.localRotation = Quaternion.Inverse(rImpact);

            // Velocity axis (expressed in the squash counter space, which equals the VisualRoot space rotation-wise)
            Quaternion rVelocity = Quaternion.FromToRotation(Vector3.up, toLocal * _velocityAxis);
            float pVelocity = 1f / Mathf.Sqrt(stretchScale);
            _stretchAxis.localRotation = rVelocity;
            _stretchAxis.localScale = new Vector3(pVelocity, stretchScale, pVelocity);
            _stretchCounter.localRotation = Quaternion.Inverse(rVelocity);
        }

        private void ApplyFallback(float squashScale, float stretchScale)
        {
            // Someone else (e.g. the Combat module setting the radius) changed the scale since our last write:
            // adopt it as the new rest scale.
            Vector3 current = transform.localScale;
            if (!_fallbackWritten || (current - _fallbackLastWritten).sqrMagnitude > 1e-8f) _fallbackRestScale = current;

            Quaternion toLocal = Quaternion.Inverse(transform.rotation);
            Vector3 nImpact = toLocal * _impactNormal;
            Vector3 nVelocity = toLocal * _velocityAxis;

            // Diagonal of R·diag(p,s,p)·R⁻¹ in local axes: d_i = p + (s - p) * n_i²
            Vector3 d1 = AxisDiagonal(nImpact, squashScale);
            Vector3 d2 = AxisDiagonal(nVelocity, stretchScale);
            Vector3 d = Vector3.Scale(d1, d2);

            // Renormalise so the approximation still preserves volume.
            float volume = d.x * d.y * d.z;
            if (volume > 1e-6f) d /= Mathf.Pow(volume, 1f / 3f);

            Vector3 written = Vector3.Scale(_fallbackRestScale, d);
            transform.localScale = written;
            _fallbackLastWritten = written;
            _fallbackWritten = true;
        }

        private static Vector3 AxisDiagonal(Vector3 localAxis, float s)
        {
            float p = 1f / Mathf.Sqrt(s);
            Vector3 n = localAxis.sqrMagnitude > 1e-8f ? localAxis.normalized : Vector3.up;
            return new Vector3(p + (s - p) * n.x * n.x, p + (s - p) * n.y * n.y, p + (s - p) * n.z * n.z);
        }

        private void ApplyIdentity()
        {
            _deformed = false;
            if (_fallbackMode)
            {
                if (_fallbackWritten)
                {
                    Vector3 current = transform.localScale;
                    // Only restore if nobody else changed the scale meanwhile.
                    if ((current - _fallbackLastWritten).sqrMagnitude <= 1e-8f) transform.localScale = _fallbackRestScale;
                    _fallbackWritten = false;
                }
                return;
            }

            if (_squashAxis != null)
            {
                _squashAxis.localRotation = Quaternion.identity;
                _squashAxis.localScale = Vector3.one;
            }
            if (_squashCounter != null) _squashCounter.localRotation = Quaternion.identity;
            if (_stretchAxis != null)
            {
                _stretchAxis.localRotation = Quaternion.identity;
                _stretchAxis.localScale = Vector3.one;
            }
            if (_stretchCounter != null) _stretchCounter.localRotation = Quaternion.identity;
        }
    }
}
