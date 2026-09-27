using UnityEngine;
using UnityEngine.Rendering;

namespace DodgeballUltra.Combat
{
    /// <summary>
    /// Flight trail of a <see cref="DodgeBall"/>. Lives on a child of the ball's physics root (not on the VisualRoot, so
    /// squash &amp; stretch and the Overcharge radius scale never distort it). Only emits while the ball is Live.
    /// <para>
    /// The width follows the ball's speed and radius; plain match balls only show a faint motion streak above ~70 km/h
    /// (it reads like motion blur), while ability balls carry their style colour: Meteor fire orange, Beam cyan-white,
    /// Glue green, Freeze icy blue, Turret tracer (see <see cref="BallAppearance.GetTrailLook"/>).
    /// </para>
    /// Driven by the owning ball (<see cref="Tick"/> from its LateUpdate): no Update of its own.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TrailRenderer))]
    public sealed class BallTrail : MonoBehaviour
    {
        [Tooltip("Width multiplier at the lowest visible speed relative to full speed (the streak thins as the ball slows).")]
        [Range(0f, 1f)] [SerializeField] private float slowWidthFraction = 0.45f;

        [Tooltip("Minimum distance (m) between trail vertices.")]
        [Min(0.005f)] [SerializeField] private float minVertexDistance = 0.04f;

        /// <summary>The underlying renderer.</summary>
        public TrailRenderer Trail { get; private set; }

        /// <summary>Style currently displayed.</summary>
        public BallStyle Style { get; private set; } = BallStyle.Standard;

        /// <summary>True while new trail segments are being emitted.</summary>
        public bool IsEmitting { get; private set; }

        private BallTrailLook _look;
        private float _lastAlpha = -1f;

        /// <summary>One-time setup of the renderer (called by the ball right after AddComponent).</summary>
        public void Initialize()
        {
            Trail = GetComponent<TrailRenderer>();
            if (Trail == null) Trail = gameObject.AddComponent<TrailRenderer>();

            Trail.alignment = LineAlignment.View;
            Trail.textureMode = LineTextureMode.Stretch;
            Trail.shadowCastingMode = ShadowCastingMode.Off;
            Trail.receiveShadows = false;
            Trail.generateLightingData = false;
            Trail.numCapVertices = 2;
            Trail.numCornerVertices = 0;
            Trail.minVertexDistance = minVertexDistance;
            Trail.autodestruct = false;
            Trail.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            // Head at full width, tapering to a thin tail.
            Trail.widthCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.12f));
            Trail.endColor = new Color(1f, 1f, 1f, 0f);

            ApplyStyle(BallStyle.Standard);
            SetEmitting(false, true);
        }

        /// <summary>Switches material, lifetime and colour to <paramref name="style"/>.</summary>
        public void ApplyStyle(BallStyle style)
        {
            if (Trail == null) return;
            Style = style;
            _look = BallAppearance.GetTrailLook(style);
            var material = BallAppearance.GetTrailMaterial(style);
            if (material != null) Trail.sharedMaterial = material;
            Trail.time = Mathf.Max(0.01f, _look.Time);
            _lastAlpha = -1f;
        }

        /// <summary>Starts/stops emitting. <paramref name="clear"/> removes the existing segments (teleports, relaunch).</summary>
        public void SetEmitting(bool emitting, bool clear)
        {
            if (Trail == null) return;
            if (clear) Trail.Clear();
            Trail.emitting = emitting;
            IsEmitting = emitting;
        }

        /// <summary>Per-frame width/opacity update from the ball's speed (m/s) and current radius (m).</summary>
        public void Tick(float speed, float radius)
        {
            if (Trail == null || !IsEmitting) return;

            float kmh = speed * Core.GameConstants.MsToKmh;
            float visibility = _look.FullSpeedKmh > _look.MinSpeedKmh
                ? Mathf.InverseLerp(_look.MinSpeedKmh, _look.FullSpeedKmh, kmh)
                : 1f;

            Trail.widthMultiplier = radius * 2f * _look.WidthFactor * Mathf.Lerp(slowWidthFraction, 1f, visibility);

            float alpha = _look.MaxAlpha * visibility;
            if (Mathf.Abs(alpha - _lastAlpha) > 0.01f)
            {
                _lastAlpha = alpha;
                Trail.startColor = new Color(1f, 1f, 1f, alpha);
            }
        }
    }
}
