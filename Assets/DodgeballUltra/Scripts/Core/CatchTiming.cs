namespace DodgeballUltra.Core
{
    /// <summary>Outcome of a catch attempt.</summary>
    public enum CatchQuality
    {
        /// <summary>The ball arrived outside any catch window (or behind the catcher): it is a hit.</summary>
        Miss = 0,
        /// <summary>Caught, but the input was more than the perfect window before impact.</summary>
        Normal = 1,
        /// <summary>0 &lt;= t_input &lt;= perfectWindow seconds before impact.</summary>
        Perfect = 2,
    }

    /// <summary>
    /// Perfect Catch timing rule:
    /// <code>Perfect Catch  &lt;=&gt;  0 &lt;= t_input &lt;= 0.15 s before impact</code>
    /// <para>
    /// <c>secondsBeforeImpact</c> is measured as <c>impactTime - inputTime</c>: the delay between the
    /// player pressing Catch and the ball actually reaching the catch zone.
    /// </para>
    /// </summary>
    public static class CatchTiming
    {
        /// <summary>
        /// Classifies a catch attempt.
        /// </summary>
        /// <param name="secondsBeforeImpact">impactTime - inputTime (seconds). Negative means the button was pressed after impact.</param>
        /// <param name="perfectWindow">Perfect window (0.15 s by default, 0.225 s with Iron Mitts).</param>
        /// <param name="catchWindow">Maximum lead time still accepted as a normal catch.</param>
        public static CatchQuality Classify(float secondsBeforeImpact, float perfectWindow = GameConstants.PerfectCatchWindow,
            float catchWindow = GameConstants.NormalCatchWindow)
        {
            if (secondsBeforeImpact < 0f) return CatchQuality.Miss;
            if (secondsBeforeImpact <= perfectWindow) return CatchQuality.Perfect;
            if (secondsBeforeImpact <= catchWindow) return CatchQuality.Normal;
            return CatchQuality.Miss;
        }

        /// <summary>Perfect window after applying a multiplier (e.g. 1.5 for Bear's Iron Mitts).</summary>
        public static float ScaledPerfectWindow(float multiplier, float baseWindow = GameConstants.PerfectCatchWindow)
        {
            if (multiplier <= 0f) multiplier = 1f;
            return baseWindow * multiplier;
        }

        /// <summary>
        /// The catch window must always be at least as long as the perfect window, otherwise a
        /// heavily-buffed perfect window would be truncated by the normal window.
        /// </summary>
        public static float EffectiveCatchWindow(float perfectWindow, float catchWindow)
            => catchWindow < perfectWindow ? perfectWindow : catchWindow;
    }
}
