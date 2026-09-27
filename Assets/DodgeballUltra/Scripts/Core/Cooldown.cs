using System;

namespace DodgeballUltra.Core
{
    /// <summary>
    /// Allocation-free cooldown timer. Tick it with a (scaled) delta time from the owning system.
    /// </summary>
    [Serializable]
    public sealed class Cooldown
    {
        private float _duration;
        private float _remaining;

        public Cooldown(float duration = 0f)
        {
            _duration = Math.Max(0f, duration);
            _remaining = 0f;
        }

        /// <summary>Full cooldown length in seconds.</summary>
        public float Duration => _duration;

        /// <summary>Seconds left before the cooldown is ready.</summary>
        public float Remaining => _remaining;

        /// <summary>True when the cooldown has elapsed.</summary>
        public bool IsReady => _remaining <= 0f;

        /// <summary>1 when just started, 0 when ready.</summary>
        public float NormalizedRemaining => _duration <= 0f ? 0f : Clamp01(_remaining / _duration);

        /// <summary>Changes the duration without restarting the timer.</summary>
        public void SetDuration(float duration) => _duration = Math.Max(0f, duration);

        /// <summary>Starts (or restarts) the cooldown at its full duration.</summary>
        public void Start() => _remaining = _duration;

        /// <summary>Starts the cooldown with an explicit duration (also stored as the new duration).</summary>
        public void Start(float duration)
        {
            _duration = Math.Max(0f, duration);
            _remaining = _duration;
        }

        /// <summary>Instantly readies the cooldown.</summary>
        public void Reset() => _remaining = 0f;

        /// <summary>Removes <paramref name="seconds"/> from the remaining time (cooldown reduction effects).</summary>
        public void Reduce(float seconds) => _remaining = Math.Max(0f, _remaining - Math.Max(0f, seconds));

        /// <summary>Advances the timer. Returns true on the frame the cooldown becomes ready.</summary>
        public bool Tick(float deltaTime)
        {
            if (_remaining <= 0f) return false;
            _remaining -= deltaTime;
            if (_remaining > 0f) return false;
            _remaining = 0f;
            return true;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
