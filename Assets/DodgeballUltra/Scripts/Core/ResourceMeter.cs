using System;

namespace DodgeballUltra.Core
{
    /// <summary>
    /// A clamped 0..Max meter (used for the Ultimate meter, 0..1). Raises <see cref="Filled"/> once when it reaches max.
    /// </summary>
    [Serializable]
    public sealed class ResourceMeter
    {
        private float _value;
        private readonly float _max;
        private bool _wasFull;

        public ResourceMeter(float max = 1f, float initial = 0f)
        {
            _max = max <= 0f ? 1f : max;
            _value = Math.Max(0f, Math.Min(initial, _max));
            _wasFull = IsFull;
        }

        /// <summary>Raised once each time the meter transitions to full.</summary>
        public event Action Filled;

        /// <summary>Raised whenever the value changes: (oldValue, newValue).</summary>
        public event Action<float, float> Changed;

        public float Max => _max;
        public float Value => _value;
        public float Normalized => _value / _max;
        public bool IsFull => _value >= _max - 1e-5f;

        /// <summary>Adds (or removes, if negative) an amount and clamps. Returns the applied delta.</summary>
        public float Add(float amount)
        {
            float old = _value;
            _value = Math.Max(0f, Math.Min(_max, _value + amount));
            float delta = _value - old;
            if (Math.Abs(delta) > 0f)
            {
                Changed?.Invoke(old, _value);
                bool full = IsFull;
                if (full && !_wasFull) Filled?.Invoke();
                _wasFull = full;
            }
            return delta;
        }

        /// <summary>Consumes <paramref name="amount"/> if available. Returns true on success.</summary>
        public bool TryConsume(float amount)
        {
            if (_value + 1e-5f < amount) return false;
            Add(-amount);
            return true;
        }

        /// <summary>Sets an absolute value (clamped).</summary>
        public void Set(float value) => Add(value - _value);

        /// <summary>Empties the meter.</summary>
        public void Clear() => Set(0f);
    }
}
