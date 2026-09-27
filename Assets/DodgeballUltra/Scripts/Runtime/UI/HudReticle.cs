using DodgeballUltra.Combat;
using DodgeballUltra.Player;
using UnityEngine;
using UnityEngine.UI;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// Everything around the screen centre:
    /// <list type="bullet">
    /// <item>minimal crosshair (dot + four ticks) that opens slightly while moving and tints when a target is soft-locked,</item>
    /// <item>hit marker (diagonal ticks; red when the hit eliminated),</item>
    /// <item>throw charge bar (Combat.ChargeNormalized) with an extra overcharge segment for heroes that have one (Rayne),</item>
    /// <item>catch timing feedback ("PERFECT CATCH!" gold / "CATCH" / "WHIFF"),</item>
    /// <item>context prompt "[E] PICK UP" when a free ball is within reach.</item>
    /// </list>
    /// </summary>
    public sealed class HudReticle
    {
        private readonly RectTransform _root;
        private readonly Image _dot;
        private readonly RectTransform[] _ticks = new RectTransform[4];
        private readonly Image[] _tickImages = new Image[4];
        private readonly Image[] _hitTicks = new Image[4];

        private readonly HudFader _chargeFader;
        private readonly Image _chargeFill;
        private readonly RectTransform _overchargeRoot;
        private readonly Image _overchargeFill;
        private readonly Text _overchargeLabel;

        private readonly HudFader _catchFader;
        private readonly Text _catchTitle;
        private readonly Text _catchDetail;

        private readonly HudFader _promptFader;
        private readonly CachedText _promptKey;

        private float _spread;
        private float _hitAmount;
        private Color _hitColor = Color.white;
        private float _chargeLinger;

        /// <summary>Crosshair tick gap at rest / at full movement (reference px).</summary>
        public float restGap = 9f;
        public float movingGap = 15f;

        public HudReticle(RectTransform parent)
        {
            _root = UiFactory.CreateRect("Reticle", parent).Place(UiAnchor.Center, UiAnchor.Center, Vector2.zero, new Vector2(10f, 10f));
            UiFactory.AddSubCanvas(_root.gameObject);

            // ---------------------------------------------------------------- crosshair
            _dot = UiFactory.CreateImage(_root, "Dot", UiFactory.CircleSprite, UiTheme.WithAlpha(Color.white, 0.9f));
            _dot.rectTransform.Place(UiAnchor.Center, UiAnchor.Center, Vector2.zero, new Vector2(5f, 5f));
            UiFactory.AddShadow(_dot, new Color(0f, 0f, 0f, 0.5f), new Vector2(1f, -1f));
            for (int i = 0; i < 4; i++)
            {
                var tick = UiFactory.CreateImage(_root, "Tick" + i, UiFactory.WhiteSprite, UiTheme.WithAlpha(Color.white, 0.85f));
                bool vertical = i < 2;
                tick.rectTransform.Place(UiAnchor.Center, UiAnchor.Center, Vector2.zero, vertical ? new Vector2(2f, 10f) : new Vector2(10f, 2f));
                UiFactory.AddShadow(tick, new Color(0f, 0f, 0f, 0.5f), new Vector2(1f, -1f));
                _ticks[i] = tick.rectTransform;
                _tickImages[i] = tick;

                var hit = UiFactory.CreateImage(_root, "Hit" + i, UiFactory.WhiteSprite, UiTheme.WithAlpha(Color.white, 0f));
                float angle = 45f + 90f * i;
                var dir = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
                hit.rectTransform.Place(UiAnchor.Center, UiAnchor.Center, dir * 17f, new Vector2(2.5f, 12f));
                hit.rectTransform.localRotation = Quaternion.Euler(0f, 0f, angle - 90f);
                _hitTicks[i] = hit;
            }

            // ---------------------------------------------------------------- charge bar
            var charge = UiFactory.CreateRect("Charge", _root).Place(UiAnchor.Center, UiAnchor.Center, new Vector2(0f, -78f), new Vector2(320f, 34f));
            var powerLabel = UiFactory.CreateText(charge, "Label", "POWER", 14, UiTheme.TextSecondary, TextAnchor.LowerLeft);
            powerLabel.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, Vector2.zero, new Vector2(120f, 18f));
            var track = UiFactory.CreatePanel(charge, "Track", UiTheme.WithAlpha(Color.black, 0.55f), true);
            track.rectTransform.Place(UiAnchor.BottomLeft, UiAnchor.BottomLeft, Vector2.zero, new Vector2(252f, 10f));
            _chargeFill = UiFactory.CreateFilled(track.transform, "Fill", UiFactory.WhiteSprite, Color.white, Image.FillMethod.Horizontal,
                (int)Image.OriginHorizontal.Left);
            _chargeFill.rectTransform.Stretch(2f, 2f, 2f, 2f);

            _overchargeRoot = UiFactory.CreateRect("Overcharge", charge).Place(UiAnchor.BottomRight, UiAnchor.BottomRight, Vector2.zero, new Vector2(62f, 34f));
            var overTrack = UiFactory.CreatePanel(_overchargeRoot, "Track", UiTheme.WithAlpha(Color.black, 0.55f), true);
            overTrack.rectTransform.Place(UiAnchor.BottomLeft, UiAnchor.BottomLeft, Vector2.zero, new Vector2(62f, 10f));
            _overchargeFill = UiFactory.CreateFilled(overTrack.transform, "Fill", UiFactory.WhiteSprite, new Color(1f, 0.46f, 0.16f, 1f),
                Image.FillMethod.Horizontal, (int)Image.OriginHorizontal.Left);
            _overchargeFill.rectTransform.Stretch(2f, 2f, 2f, 2f);
            _overchargeLabel = UiFactory.CreateText(_overchargeRoot, "Label", "OVERCHARGE", 14, new Color(1f, 0.55f, 0.2f, 1f), TextAnchor.LowerRight);
            _overchargeLabel.rectTransform.Place(UiAnchor.TopRight, UiAnchor.TopRight, Vector2.zero, new Vector2(140f, 18f));
            _chargeFader = new HudFader(charge, 0.06f, 0.25f);

            // ---------------------------------------------------------------- catch feedback
            var catchRoot = UiFactory.CreateRect("Catch", _root).Place(UiAnchor.Center, UiAnchor.Center, new Vector2(0f, 118f), new Vector2(900f, 100f));
            _catchTitle = UiFactory.CreateText(catchRoot, "Title", string.Empty, 50, UiTheme.Gold, TextAnchor.MiddleCenter);
            _catchTitle.rectTransform.Place(UiAnchor.Top, UiAnchor.Top, Vector2.zero, new Vector2(900f, 62f));
            UiFactory.AddShadow(_catchTitle, new Color(0f, 0f, 0f, 0.45f), new Vector2(2.5f, -2.5f));
            _catchDetail = UiFactory.CreateText(catchRoot, "Detail", string.Empty, 20, UiTheme.TextPrimary, TextAnchor.MiddleCenter);
            _catchDetail.rectTransform.Place(UiAnchor.Bottom, UiAnchor.Bottom, Vector2.zero, new Vector2(900f, 30f));
            _catchFader = new HudFader(catchRoot, 0.05f, 0.35f);

            // ---------------------------------------------------------------- pick-up prompt
            var prompt = UiFactory.CreateRect("Prompt", _root).Place(UiAnchor.Center, UiAnchor.Center, new Vector2(0f, -140f), new Vector2(240f, 34f));
            var keyChip = UiFactory.CreatePanel(prompt, "Key", UiTheme.WithAlpha(Color.white, 0.92f), true);
            keyChip.rectTransform.Place(UiAnchor.Left, UiAnchor.Left, new Vector2(40f, 0f), new Vector2(46f, 30f));
            var key = UiFactory.CreateText(keyChip.transform, "Label", "E", 17, new Color(0.05f, 0.06f, 0.08f, 1f), TextAnchor.MiddleCenter, FontStyle.Bold, false);
            key.rectTransform.Stretch();
            _promptKey = new CachedText(key);
            var action = UiFactory.CreateText(prompt, "Action", "PICK UP", 19, UiTheme.TextPrimary, TextAnchor.MiddleLeft);
            action.rectTransform.Place(UiAnchor.Left, UiAnchor.Left, new Vector2(96f, 0f), new Vector2(140f, 30f));
            _promptFader = new HudFader(prompt, 0.1f, 0.15f);
        }

        public void SetVisible(bool visible)
        {
            if (_root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        }

        /// <summary>Hit confirmation on the crosshair.</summary>
        public void ShowHitMarker(bool eliminated)
        {
            _hitAmount = 1f;
            _hitColor = eliminated ? UiTheme.Danger : Color.white;
        }

        /// <summary>Catch timing feedback. <paramref name="detail"/> e.g. "0.08 s  ·  +15% ULTIMATE".</summary>
        public void ShowCatchFeedback(string title, string detail, Color color, float duration)
        {
            _catchTitle.text = title;
            _catchTitle.color = color;
            _catchDetail.text = detail ?? string.Empty;
            _catchFader.Show(duration, 1.3f);
        }

        /// <param name="local">Local player (not null).</param>
        /// <param name="hasOvercharge">Hero has an overcharge mechanic (extra bar segment).</param>
        /// <param name="overchargeSeconds">Total charge time at which the overcharge segment is full.</param>
        /// <param name="targetColor">Tint while a target is soft-locked (alpha 0 = no lock).</param>
        /// <param name="showPickupPrompt">A free ball can be picked up.</param>
        /// <param name="pickupKey">Prompt glyph for Pick up.</param>
        public void Tick(DodgeballPlayer local, bool hasOvercharge, float overchargeSeconds, Color targetColor, bool showPickupPrompt,
            string pickupKey, float dt)
        {
            var combat = local != null ? local.Combat : null;

            // ---------------------------------------------------------------- crosshair spread / tint
            float speed = local != null ? new Vector2(local.Velocity.x, local.Velocity.z).magnitude : 0f;
            _spread = HudAnim.Damp(_spread, Mathf.Clamp01(speed / 7f), 10f, dt);
            float gap = Mathf.Lerp(restGap, movingGap, _spread);
            _ticks[0].anchoredPosition = new Vector2(0f, gap + 5f);
            _ticks[1].anchoredPosition = new Vector2(0f, -gap - 5f);
            _ticks[2].anchoredPosition = new Vector2(-gap - 5f, 0f);
            _ticks[3].anchoredPosition = new Vector2(gap + 5f, 0f);

            bool holding = combat != null && combat.HasBall;
            Color tickColor = targetColor.a > 0.01f ? Color.Lerp(Color.white, targetColor, 0.65f) : Color.white;
            tickColor.a = holding ? 0.95f : 0.55f;
            for (int i = 0; i < 4; i++) _tickImages[i].color = tickColor;

            // ---------------------------------------------------------------- hit marker
            if (_hitAmount > 0f)
            {
                _hitAmount = Mathf.Max(0f, _hitAmount - dt * 5f);
                var c = UiTheme.WithAlpha(_hitColor, _hitAmount);
                float scale = 1f + 0.35f * _hitAmount;
                for (int i = 0; i < 4; i++)
                {
                    _hitTicks[i].color = c;
                    _hitTicks[i].rectTransform.localScale = new Vector3(1f, scale, 1f);
                }
            }

            // ---------------------------------------------------------------- charge bar
            bool charging = combat != null && combat.IsCharging;
            if (charging)
            {
                _chargeLinger = 0.25f;
                float charge = combat.ChargeNormalized;
                float main = Mathf.Clamp01(charge);
                _chargeFill.fillAmount = main;
                _chargeFill.color = Color.Lerp(Color.white, UiTheme.Gold, main * main);

                float over = 0f;
                if (hasOvercharge)
                {
                    if (charge > 1f)
                    {
                        over = Mathf.Clamp01(charge - 1f);
                    }
                    else
                    {
                        float full = Mathf.Max(0.01f, combat.Profile != null ? combat.Profile.fullChargeTime : 0.75f);
                        over = Mathf.Clamp01((combat.ChargeSeconds - full) / Mathf.Max(0.05f, overchargeSeconds - full));
                    }
                }
                _overchargeFill.fillAmount = over;
                _overchargeLabel.color = UiTheme.WithAlpha(_overchargeLabel.color, over > 0f ? 1f : 0.35f);
                if (_overchargeRoot.gameObject.activeSelf != hasOvercharge) _overchargeRoot.gameObject.SetActive(hasOvercharge);

                if (!_chargeFader.IsVisible) _chargeFader.Show(0f);
                else _chargeFader.ShowPersistent();
            }
            else if (_chargeFader.IsVisible)
            {
                _chargeLinger -= dt;
                if (_chargeLinger <= 0f) _chargeFader.Hide();
            }
            _chargeFader.Tick(dt);

            // ---------------------------------------------------------------- catch feedback / prompt
            _catchFader.Tick(dt);

            if (showPickupPrompt)
            {
                _promptKey.Set(pickupKey);
                _promptFader.ShowPersistent();
            }
            else
            {
                _promptFader.Hide();
            }
            _promptFader.Tick(dt);
        }
    }
}
