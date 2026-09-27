using DodgeballUltra.Player;
using UnityEngine;
using UnityEngine.UI;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// Broadcast-style ball speed graphic (bottom centre) for the last throw: "BALL SPEED 132 km/h", the rally multiplier
    /// ("RALLY ×2", Rally Boost +10 % per rally, capped at 220 km/h), a tag for counter throws / abilities and the thrower's
    /// name in team colour. Fades after a few seconds.
    /// </summary>
    public sealed class HudSpeedReadout
    {
        private readonly HudFader _fader;
        private readonly CachedText _value;
        private readonly CachedText _rally;
        private readonly CachedText _tag;
        private readonly CachedText _thrower;
        private readonly Image _accent;

        /// <summary>Seconds the readout stays visible after a throw.</summary>
        public float displayTime = 3.5f;

        public HudSpeedReadout(RectTransform parent, float margin)
        {
            var root = UiFactory.CreateRect("BallSpeed", parent).Place(UiAnchor.Bottom, UiAnchor.Bottom, new Vector2(0f, margin + 6f), new Vector2(360f, 96f));
            var panel = UiFactory.CreatePanel(root, "Panel", UiTheme.Panel);
            panel.rectTransform.Stretch();
            _accent = UiFactory.CreateImage(root, "Accent", UiFactory.WhiteSprite, Color.white);
            _accent.rectTransform.Place(UiAnchor.Top, UiAnchor.Top, Vector2.zero, new Vector2(360f, 3f));

            var label = UiFactory.CreateText(root, "Label", "BALL SPEED", 14, UiTheme.TextMuted, TextAnchor.UpperLeft);
            label.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(16f, -10f), new Vector2(160f, 18f));

            var value = UiFactory.CreateText(root, "Value", "0", 50, UiTheme.TextPrimary, TextAnchor.LowerRight);
            value.rectTransform.Place(UiAnchor.BottomLeft, UiAnchor.BottomLeft, new Vector2(16f, 8f), new Vector2(110f, 58f));
            _value = new CachedText(value);
            var unit = UiFactory.CreateText(root, "Unit", "km/h", 20, UiTheme.TextSecondary, TextAnchor.LowerLeft, FontStyle.Normal);
            unit.rectTransform.Place(UiAnchor.BottomLeft, UiAnchor.BottomLeft, new Vector2(132f, 16f), new Vector2(60f, 26f));

            var rally = UiFactory.CreateText(root, "Rally", string.Empty, 18, UiTheme.Gold, TextAnchor.UpperRight);
            rally.rectTransform.Place(UiAnchor.TopRight, UiAnchor.TopRight, new Vector2(-16f, -10f), new Vector2(160f, 22f));
            _rally = new CachedText(rally);
            var tag = UiFactory.CreateText(root, "Tag", string.Empty, 15, UiTheme.Gold, TextAnchor.MiddleRight);
            tag.rectTransform.Place(UiAnchor.Right, UiAnchor.Right, new Vector2(-16f, 0f), new Vector2(160f, 20f));
            _tag = new CachedText(tag);
            var thrower = UiFactory.CreateText(root, "Thrower", string.Empty, 17, UiTheme.TextPrimary, TextAnchor.LowerRight);
            thrower.rectTransform.Place(UiAnchor.BottomRight, UiAnchor.BottomRight, new Vector2(-16f, 12f), new Vector2(160f, 22f));
            _thrower = new CachedText(thrower);

            _fader = new HudFader(root, 0.12f, 0.5f);
        }

        public void Show(DodgeballPlayer thrower, float speedKmh, int rallyCount, bool counter, bool ability)
        {
            _value.SetInt(Mathf.RoundToInt(speedKmh));
            _rally.Set(UiStrings.Rally(rallyCount));
            _tag.Set(counter ? "COUNTER +20%" : ability ? "ABILITY" : string.Empty);

            if (thrower != null)
            {
                var data = thrower.Character;
                _thrower.Set(data != null && !string.IsNullOrEmpty(data.displayName) ? data.displayName.ToUpperInvariant() : thrower.Hero.ToString().ToUpperInvariant());
                var color = UiTheme.TeamColor(thrower.Team);
                _thrower.SetColor(Color.Lerp(color, Color.white, 0.35f));
                _accent.color = color;
            }
            else
            {
                _thrower.Set(string.Empty);
                _accent.color = UiTheme.TextSecondary;
            }

            // Near the 220 km/h cap the number turns gold.
            _value.SetColor(speedKmh >= Core.GameConstants.MaxBallSpeedKmh - 0.5f ? UiTheme.Gold : UiTheme.TextPrimary);
            _fader.Show(displayTime, 1.08f);
        }

        public void Tick(float dt) => _fader.Tick(dt);
    }
}
