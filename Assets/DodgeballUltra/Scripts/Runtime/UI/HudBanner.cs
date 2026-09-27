using UnityEngine;
using UnityEngine.UI;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// Centre-screen broadcast banner (ROUND 2 / FIGHT / ELIMINATED / VICTORY / DEFEAT): a translucent full-width strip that
    /// fades at both ends, thin accent rules above and below, a large title and an optional subtitle. Also owns the big
    /// pre-round countdown numerals (3, 2, 1).
    /// </summary>
    public sealed class HudBanner
    {
        private readonly HudFader _fader;
        private readonly Image _ruleTop;
        private readonly Image _ruleBottom;
        private readonly Text _title;
        private readonly Text _subtitle;

        private readonly HudFader _countFader;
        private readonly Text _count;

        public HudBanner(RectTransform parent)
        {
            // ---------------------------------------------------------------- banner strip
            var root = UiFactory.CreateRect("Banner", parent).Place(UiAnchor.Center, UiAnchor.Center, new Vector2(0f, 190f), new Vector2(1920f, 170f));

            var strip = UiFactory.CreateImage(root, "Strip", UiFactory.HorizontalFadeSprite, new Color(0.015f, 0.02f, 0.03f, 0.72f));
            strip.rectTransform.Stretch();

            _ruleTop = UiFactory.CreateImage(root, "RuleTop", UiFactory.HorizontalFadeSprite, UiTheme.TextPrimary);
            _ruleTop.rectTransform.Place(UiAnchor.Top, UiAnchor.Top, Vector2.zero, new Vector2(1200f, 3f));
            _ruleBottom = UiFactory.CreateImage(root, "RuleBottom", UiFactory.HorizontalFadeSprite, UiTheme.TextPrimary);
            _ruleBottom.rectTransform.Place(UiAnchor.Bottom, UiAnchor.Bottom, Vector2.zero, new Vector2(1200f, 3f));

            _title = UiFactory.CreateText(root, "Title", string.Empty, UiTheme.FontBanner, UiTheme.TextPrimary, TextAnchor.MiddleCenter);
            _title.rectTransform.Stretch(0f, 0f, 8f, 44f);
            UiFactory.AddShadow(_title, new Color(0f, 0f, 0f, 0.45f), new Vector2(3f, -3f));

            _subtitle = UiFactory.CreateText(root, "Subtitle", string.Empty, 26, UiTheme.TextSecondary, TextAnchor.MiddleCenter, FontStyle.Bold);
            _subtitle.rectTransform.Place(UiAnchor.Bottom, UiAnchor.Bottom, new Vector2(0f, 14f), new Vector2(1400f, 34f));

            _fader = new HudFader(root, 0.14f, 0.35f);

            // ---------------------------------------------------------------- countdown numerals
            var countRoot = UiFactory.CreateRect("Countdown", parent).Place(UiAnchor.Center, UiAnchor.Center, new Vector2(0f, 20f), new Vector2(520f, 300f));
            _count = UiFactory.CreateText(countRoot, "Number", string.Empty, UiTheme.FontCountdown, UiTheme.TextPrimary, TextAnchor.MiddleCenter);
            _count.rectTransform.Stretch();
            UiFactory.AddShadow(_count, new Color(0f, 0f, 0f, 0.5f), new Vector2(4f, -4f));
            _countFader = new HudFader(countRoot, 0.04f, 0.3f);
        }

        public bool IsShowing => _fader.IsVisible;

        /// <summary>Shows a banner. <paramref name="duration"/> is the total on-screen time (unscaled seconds).</summary>
        public void Show(string title, string subtitle, Color accent, float duration)
        {
            _title.text = title ?? string.Empty;
            _title.color = Color.Lerp(UiTheme.TextPrimary, accent, 0.35f);
            _subtitle.text = subtitle ?? string.Empty;
            _subtitle.gameObject.SetActive(!string.IsNullOrEmpty(subtitle));
            _ruleTop.color = accent;
            _ruleBottom.color = accent;
            _fader.Show(Mathf.Max(0.4f, duration), 1.12f);
        }

        public void Hide() => _fader.Hide();

        /// <summary>Big countdown numeral with a pop-in; fades out just before the next second.</summary>
        public void ShowCountdown(int secondsLeft)
        {
            if (secondsLeft <= 0)
            {
                _countFader.Hide();
                return;
            }
            _count.text = UiStrings.Int(secondsLeft);
            _count.color = secondsLeft == 1 ? UiTheme.Gold : UiTheme.TextPrimary;
            _countFader.Show(0.95f, 1.7f);
        }

        public void HideCountdown() => _countFader.Hide(true);

        public void Tick(float dt)
        {
            _fader.Tick(dt);
            _countFader.Tick(dt);
        }
    }
}
