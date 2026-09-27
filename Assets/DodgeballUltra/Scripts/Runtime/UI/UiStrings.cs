using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// Lazily-filled string tables for numbers the HUD shows every frame (timer, HP, cooldowns, percentages, speeds) so
    /// updating text never allocates after the first time a value is seen.
    /// </summary>
    public static class UiStrings
    {
        private const int IntCacheSize = 1001;       // 0..1000
        private const int TimerCacheSize = 3600;     // 0:00 .. 59:59
        private const int TenthsCacheSize = 1000;    // 0.0 .. 99.9

        private static readonly string[] s_ints = new string[IntCacheSize];
        private static readonly string[] s_timer = new string[TimerCacheSize];
        private static readonly string[] s_tenths = new string[TenthsCacheSize];
        private static readonly string[] s_percent = new string[101];
        private static readonly string[] s_round = new string[100];
        private static readonly string[] s_meters = new string[200];
        private static readonly string[] s_rally = new string[64];

        /// <summary>"123"</summary>
        public static string Int(int value)
        {
            if (value < 0 || value >= IntCacheSize) return value.ToString(CultureInfo.InvariantCulture);
            return s_ints[value] ?? (s_ints[value] = value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>"2:05" from whole seconds.</summary>
        public static string Timer(int seconds)
        {
            if (seconds < 0) seconds = 0;
            if (seconds >= TimerCacheSize) return (seconds / 60).ToString(CultureInfo.InvariantCulture) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
            return s_timer[seconds] ??= (seconds / 60).ToString(CultureInfo.InvariantCulture) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>"4.2" (one decimal, rounded up so a cooldown never shows 0.0 while still running).</summary>
        public static string Tenths(float seconds)
        {
            int tenths = Mathf.CeilToInt(Mathf.Max(0f, seconds) * 10f - 1e-3f);
            if (tenths >= TenthsCacheSize) return Int(Mathf.CeilToInt(seconds));
            return s_tenths[tenths] ??= (tenths / 10).ToString(CultureInfo.InvariantCulture) + "." + (tenths % 10).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>"74%"</summary>
        public static string Percent(int percent)
        {
            percent = Mathf.Clamp(percent, 0, 100);
            return s_percent[percent] ??= percent.ToString(CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>"ROUND 2"</summary>
        public static string Round(int round)
        {
            if (round < 0 || round >= s_round.Length) return "ROUND " + round.ToString(CultureInfo.InvariantCulture);
            return s_round[round] ??= "ROUND " + round.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>"12 m"</summary>
        public static string Meters(int meters)
        {
            if (meters < 0) meters = 0;
            if (meters >= s_meters.Length) return meters.ToString(CultureInfo.InvariantCulture) + " m";
            return s_meters[meters] ??= meters.ToString(CultureInfo.InvariantCulture) + " m";
        }

        /// <summary>"RALLY ×3" (empty for rally 0).</summary>
        public static string Rally(int rally)
        {
            if (rally <= 0) return string.Empty;
            if (rally >= s_rally.Length) return "RALLY ×" + rally.ToString(CultureInfo.InvariantCulture);
            return s_rally[rally] ??= "RALLY ×" + rally.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// A uGUI <see cref="Text"/> that is only touched when its content really changes (no string building, no mesh
    /// rebuilds for identical values).
    /// </summary>
    public sealed class CachedText
    {
        public readonly Text Text;
        private string _value;
        private int _intValue = int.MinValue;
        private Color _color;
        private bool _hasColor;

        public CachedText(Text text)
        {
            Text = text;
            _value = text != null ? text.text : null;
        }

        public void Set(string value)
        {
            if (Text == null || string.Equals(_value, value)) return;
            _value = value;
            _intValue = int.MinValue;
            Text.text = value;
        }

        public void SetInt(int value)
        {
            if (Text == null || (_intValue == value && _value != null)) return;
            _value = UiStrings.Int(value);
            _intValue = value;
            Text.text = _value;
        }

        public void SetColor(Color color)
        {
            if (Text == null || (_hasColor && _color == color)) return;
            _color = color;
            _hasColor = true;
            Text.color = color;
        }

        public void SetActive(bool active)
        {
            if (Text != null && Text.gameObject.activeSelf != active) Text.gameObject.SetActive(active);
        }
    }
}
