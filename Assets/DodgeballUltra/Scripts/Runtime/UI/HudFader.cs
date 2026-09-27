using UnityEngine;

namespace DodgeballUltra.UI
{
    /// <summary>Small easing helpers for UI motion (all driven by unscaled time).</summary>
    public static class HudAnim
    {
        public static float EaseOutCubic(float t)
        {
            t = Mathf.Clamp01(t);
            float u = 1f - t;
            return 1f - u * u * u;
        }

        public static float EaseOutBack(float t, float overshoot = 1.4f)
        {
            t = Mathf.Clamp01(t) - 1f;
            return t * t * ((overshoot + 1f) * t + overshoot) + 1f;
        }

        /// <summary>0..1 sine pulse at <paramref name="frequency"/> Hz on the unscaled clock.</summary>
        public static float Pulse(float frequency) => 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * frequency * Mathf.PI * 2f);

        /// <summary>Frame-rate independent exponential approach (sharpness ≈ 1/time-constant).</summary>
        public static float Damp(float current, float target, float sharpness, float dt) =>
            Mathf.Lerp(current, target, 1f - Mathf.Exp(-sharpness * dt));
    }

    /// <summary>
    /// Timed show / hold / fade-out of one HUD element through a CanvasGroup, with an optional "pop" scale on appear.
    /// A duration &lt;= 0 keeps the element up until <see cref="Hide"/>. The GameObject is deactivated while invisible so it
    /// costs nothing to render.
    /// </summary>
    public sealed class HudFader
    {
        public readonly RectTransform Rect;
        public readonly CanvasGroup Group;

        private readonly float _fadeIn;
        private readonly float _fadeOut;
        private float _time;
        private float _duration;
        private float _popScale = 1f;
        private bool _active;
        private bool _persistent;

        public bool IsVisible => _active;

        /// <summary>Seconds since the element was last shown.</summary>
        public float Age => _time;

        public HudFader(RectTransform rect, float fadeIn = 0.12f, float fadeOut = 0.3f)
        {
            Rect = rect;
            _fadeIn = Mathf.Max(0.001f, fadeIn);
            _fadeOut = Mathf.Max(0.001f, fadeOut);
            Group = UiFactory.AddCanvasGroup(rect.gameObject, 0f);
            rect.gameObject.SetActive(false);
        }

        /// <summary>Shows for <paramref name="duration"/> seconds (&lt;= 0 = until hidden), popping from <paramref name="popScale"/>.</summary>
        public void Show(float duration, float popScale = 1f)
        {
            _time = 0f;
            _duration = duration;
            _persistent = duration <= 0f;
            _popScale = popScale;
            _active = true;
            if (!Rect.gameObject.activeSelf) Rect.gameObject.SetActive(true);
            Apply();
        }

        /// <summary>Keeps a persistent element visible without restarting its appear animation.</summary>
        public void ShowPersistent()
        {
            if (_active && _persistent) return;
            if (_active)
            {
                _persistent = true;
                return;
            }
            Show(0f);
        }

        /// <summary>Fades out (or hides at once).</summary>
        public void Hide(bool immediate = false)
        {
            if (!_active) return;
            if (immediate)
            {
                _active = false;
                Group.alpha = 0f;
                Rect.gameObject.SetActive(false);
                return;
            }
            if (_persistent)
            {
                _persistent = false;
                _duration = _time + _fadeOut;
            }
            else
            {
                _duration = Mathf.Min(_duration, _time + _fadeOut);
            }
        }

        public void Tick(float dt)
        {
            if (!_active) return;
            _time += dt;
            if (!_persistent && _time >= _duration)
            {
                _active = false;
                Group.alpha = 0f;
                Rect.gameObject.SetActive(false);
                return;
            }
            Apply();
        }

        private void Apply()
        {
            float alpha;
            if (_time < _fadeIn) alpha = _time / _fadeIn;
            else if (_persistent) alpha = 1f;
            else alpha = Mathf.Clamp01((_duration - _time) / _fadeOut);
            Group.alpha = alpha;

            if (!Mathf.Approximately(_popScale, 1f))
            {
                float s = Mathf.LerpUnclamped(_popScale, 1f, HudAnim.EaseOutCubic(_time / 0.2f));
                Rect.localScale = new Vector3(s, s, 1f);
            }
            else if (Rect.localScale != Vector3.one)
            {
                Rect.localScale = Vector3.one;
            }
        }
    }
}
