using UnityEngine;
using UnityEngine.UI;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// Danger Sense: red screen-edge vignette that flashes while a high-speed ball is locked on to the local player
    /// (<c>DangerSenseEvent</c>). Pulse rate rises as the impact gets closer, intensity scales with ball speed. A safety
    /// timeout (time-to-impact + grace) turns it off even if the "threat passed" event never arrives.
    /// </summary>
    public sealed class HudDangerVignette
    {
        private readonly Image _image;
        private float _alpha;
        private float _activeUntil;
        private float _impactAt;
        private float _intensity;

        /// <summary>Peak vignette alpha at the maximum ball speed.</summary>
        public float maxAlpha = 0.6f;
        /// <summary>Pulse frequency (Hz) far from impact / right before impact.</summary>
        public float slowPulse = 2.5f;
        public float fastPulse = 7f;
        /// <summary>Extra seconds the warning stays after the predicted impact.</summary>
        public float grace = 0.25f;

        public HudDangerVignette(RectTransform parent)
        {
            _image = UiFactory.CreateImage(parent, "DangerSense", UiFactory.EdgeVignetteSprite, UiTheme.WithAlpha(UiTheme.Danger, 0f));
            _image.rectTransform.Stretch();
            _image.gameObject.SetActive(false);
        }

        public bool IsActive => Time.unscaledTime < _activeUntil;

        /// <summary>Starts / refreshes (active) or clears (inactive) the warning.</summary>
        public void SetThreat(bool active, float timeToImpact, float speedKmh)
        {
            float now = Time.unscaledTime;
            if (!active)
            {
                _activeUntil = 0f;
                return;
            }
            float tti = float.IsInfinity(timeToImpact) || float.IsNaN(timeToImpact) ? 1f : Mathf.Clamp(timeToImpact, 0.05f, 3f);
            _impactAt = now + tti;
            _activeUntil = Mathf.Max(_activeUntil, _impactAt + grace);
            _intensity = Mathf.Lerp(0.55f, 1f, Mathf.InverseLerp(80f, Core.GameConstants.MaxBallSpeedKmh, speedKmh));
        }

        public void Clear()
        {
            _activeUntil = 0f;
            _alpha = 0f;
            _image.gameObject.SetActive(false);
        }

        public void Tick(float dt)
        {
            float now = Time.unscaledTime;
            float target = 0f;
            if (now < _activeUntil)
            {
                // Closer impact -> faster, stronger pulse.
                float urgency = 1f - Mathf.Clamp01((_impactAt - now) / 1.2f);
                float freq = Mathf.Lerp(slowPulse, fastPulse, urgency);
                float pulse = 0.55f + 0.45f * Mathf.Sin(now * freq * Mathf.PI * 2f);
                target = maxAlpha * _intensity * Mathf.Lerp(0.7f, 1f, urgency) * pulse;
            }

            _alpha = HudAnim.Damp(_alpha, target, target > _alpha ? 30f : 8f, dt);
            bool visible = _alpha > 0.005f;
            if (_image.gameObject.activeSelf != visible) _image.gameObject.SetActive(visible);
            if (visible) _image.color = UiTheme.WithAlpha(UiTheme.Danger, _alpha);
        }
    }
}
