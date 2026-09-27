using System;
using DodgeballUltra.Player;
using UnityEngine;
using UnityEngine.UI;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// Bottom-left "lower third" for the local player: portrait (CharacterData.portrait or coloured initials), hero name and
    /// title, HP bar with a delayed damage trail and numeric HP, "PENDING" while a delayed elimination (Chrono's Delayed
    /// Impact) is unresolved, OUTFIELD / ball-in-hand indicators, and a row of status-effect chips above the card.
    /// </summary>
    public sealed class HudPlayerCard
    {
        private const int MaxChips = 6;

        private static readonly StatusEffectType[] s_statusTypes = (StatusEffectType[])Enum.GetValues(typeof(StatusEffectType));

        private struct Chip
        {
            public RectTransform Rect;
            public Image Background;
            public CachedText Label;
            public CachedText Time;
            public StatusEffectType Type;
            public bool Bound;
        }

        private readonly RectTransform _root;
        private readonly Image _panel;
        private readonly Image _accentBar;
        private readonly Image _portraitBack;
        private readonly Image _portrait;
        private readonly Text _initials;
        private readonly Text _name;
        private readonly Text _title;
        private readonly Image _hpFill;
        private readonly Image _hpTrail;
        private readonly CachedText _hpValue;
        private readonly Text _pending;
        private readonly Text _zone;
        private readonly Image _ball;
        private readonly RectTransform _chipRow;
        private readonly Chip[] _chips = new Chip[MaxChips];

        private float _trail = 1f;
        private float _trailHold;
        private float _lastHp = -1f;
        private float _damageFlash;

        /// <summary>Seconds the damage trail waits before draining.</summary>
        public float trailDelay = 0.35f;
        /// <summary>Damage trail drain speed (fraction of max HP per second).</summary>
        public float trailSpeed = 0.9f;

        public HudPlayerCard(RectTransform parent, float margin)
        {
            _root = UiFactory.CreateRect("PlayerCard", parent).Place(UiAnchor.BottomLeft, UiAnchor.BottomLeft, new Vector2(margin, margin), new Vector2(580f, 150f));

            _panel = UiFactory.CreatePanel(_root, "Panel", UiTheme.Panel);
            _panel.rectTransform.Stretch();

            _accentBar = UiFactory.CreateImage(_root, "Accent", UiFactory.WhiteSprite, UiTheme.TextPrimary);
            _accentBar.rectTransform.Place(UiAnchor.Left, UiAnchor.Left, Vector2.zero, new Vector2(6f, 150f));

            // ---------------------------------------------------------------- portrait
            _portraitBack = UiFactory.CreatePanel(_root, "PortraitBack", UiTheme.Surface, true);
            _portraitBack.rectTransform.Place(UiAnchor.Left, UiAnchor.Left, new Vector2(20f, 0f), new Vector2(118f, 118f));
            var mask = _portraitBack.gameObject.AddComponent<RectMask2D>();
            mask.enabled = true;
            _portrait = UiFactory.CreateImage(_portraitBack.transform, "Portrait", null, Color.white);
            _portrait.rectTransform.Stretch();
            _portrait.preserveAspect = true;
            _initials = UiFactory.CreateText(_portraitBack.transform, "Initials", "?", 58, Color.white, TextAnchor.MiddleCenter);
            _initials.rectTransform.Stretch();

            // ---------------------------------------------------------------- identity
            _name = UiFactory.CreateText(_root, "Name", string.Empty, UiTheme.FontTitle, UiTheme.TextPrimary, TextAnchor.UpperLeft);
            _name.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(156f, -14f), new Vector2(300f, 40f));
            _title = UiFactory.CreateText(_root, "Title", string.Empty, 17, UiTheme.TextSecondary, TextAnchor.UpperLeft);
            _title.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(157f, -54f), new Vector2(300f, 22f));

            _zone = UiFactory.CreateText(_root, "Zone", "OUTFIELD", 16, UiTheme.Warning, TextAnchor.UpperRight);
            _zone.rectTransform.Place(UiAnchor.TopRight, UiAnchor.TopRight, new Vector2(-18f, -18f), new Vector2(220f, 22f));
            _zone.gameObject.SetActive(false);

            _ball = UiFactory.CreateImage(_root, "Ball", UiFactory.CircleSprite, UiTheme.Track);
            _ball.rectTransform.Place(UiAnchor.TopRight, UiAnchor.TopRight, new Vector2(-20f, -46f), new Vector2(18f, 18f));

            // ---------------------------------------------------------------- HP
            var hpLabel = UiFactory.CreateText(_root, "HpLabel", "HP", 15, UiTheme.TextMuted, TextAnchor.LowerLeft);
            hpLabel.rectTransform.Place(UiAnchor.BottomLeft, UiAnchor.BottomLeft, new Vector2(157f, 42f), new Vector2(60f, 20f));

            var track = UiFactory.CreatePanel(_root, "HpTrack", UiTheme.Track, true);
            track.rectTransform.Place(UiAnchor.BottomLeft, UiAnchor.BottomLeft, new Vector2(156f, 22f), new Vector2(318f, 14f));
            _hpTrail = UiFactory.CreateFilled(track.transform, "Trail", UiFactory.WhiteSprite, new Color(0.95f, 0.32f, 0.26f, 0.9f),
                Image.FillMethod.Horizontal, (int)Image.OriginHorizontal.Left);
            _hpTrail.rectTransform.Stretch(2f, 2f, 2f, 2f);
            _hpFill = UiFactory.CreateFilled(track.transform, "Fill", UiFactory.WhiteSprite, UiTheme.TextPrimary,
                Image.FillMethod.Horizontal, (int)Image.OriginHorizontal.Left);
            _hpFill.rectTransform.Stretch(2f, 2f, 2f, 2f);

            _pending = UiFactory.CreateText(track.transform, "Pending", "PENDING", 18, UiTheme.Warning, TextAnchor.MiddleCenter);
            _pending.rectTransform.Stretch(0f, 0f, -8f, -8f);
            _pending.gameObject.SetActive(false);

            var hpValue = UiFactory.CreateText(_root, "HpValue", "100", 34, UiTheme.TextPrimary, TextAnchor.LowerRight);
            hpValue.rectTransform.Place(UiAnchor.BottomRight, UiAnchor.BottomRight, new Vector2(-18f, 12f), new Vector2(90f, 40f));
            _hpValue = new CachedText(hpValue);

            // ---------------------------------------------------------------- status chips (above the card)
            _chipRow = UiFactory.CreateRect("Status", _root).Place(UiAnchor.TopLeft, UiAnchor.BottomLeft, new Vector2(0f, 10f), new Vector2(580f, 30f));
            for (int i = 0; i < MaxChips; i++)
            {
                var bg = UiFactory.CreatePanel(_chipRow, "Chip" + i, UiTheme.Panel, true);
                bg.rectTransform.Place(UiAnchor.Left, UiAnchor.Left, Vector2.zero, new Vector2(120f, 30f));
                var label = UiFactory.CreateText(bg.transform, "Label", string.Empty, 15, UiTheme.TextPrimary, TextAnchor.MiddleLeft);
                label.rectTransform.Stretch(12f, 44f, 0f, 0f);
                var time = UiFactory.CreateText(bg.transform, "Time", string.Empty, 15, UiTheme.TextSecondary, TextAnchor.MiddleRight, FontStyle.Normal);
                time.rectTransform.Stretch(12f, 10f, 0f, 0f);
                bg.gameObject.SetActive(false);
                _chips[i] = new Chip { Rect = bg.rectTransform, Background = bg, Label = new CachedText(label), Time = new CachedText(time) };
            }
        }

        public void SetVisible(bool visible)
        {
            if (_root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        }

        /// <summary>Applies the static identity of <paramref name="player"/> (portrait, name, colours).</summary>
        public void Bind(DodgeballPlayer player)
        {
            _lastHp = -1f;
            _trail = 1f;
            if (player == null) return;

            var data = player.Character;
            Color theme = data != null ? data.themeColor : UiTheme.HeroFallbackColor(player.Hero);
            theme.a = 1f;
            string displayName = data != null && !string.IsNullOrEmpty(data.displayName) ? data.displayName : player.Hero.ToString();

            _name.text = displayName.ToUpperInvariant();
            _title.text = data != null && !string.IsNullOrEmpty(data.title)
                ? data.title.ToUpperInvariant() + "  ·  " + UiTheme.TeamName(player.Team)
                : UiTheme.TeamName(player.Team);
            _title.color = Color.Lerp(theme, UiTheme.TextPrimary, 0.25f);
            _accentBar.color = UiTheme.TeamColor(player.Team);

            var portrait = data != null ? data.portrait : null;
            _portrait.sprite = portrait;
            _portrait.enabled = portrait != null;
            _initials.gameObject.SetActive(portrait == null);
            _initials.text = UiFactory.Initials(displayName, 1);
            _portraitBack.color = portrait != null ? UiTheme.Surface : Color.Lerp(theme, Color.black, 0.35f);
        }

        /// <summary>Visual damage cue on the card (called from PlayerDamagedEvent for the local player).</summary>
        public void FlashDamage() => _damageFlash = 1f;

        public void Tick(DodgeballPlayer player, float dt)
        {
            if (player == null) return;

            // ---------------------------------------------------------------- HP + trail
            var health = player.Health;
            float hp = health != null ? health.CurrentHp : 0f;
            float normalized = health != null ? health.Normalized : 0f;
            if (!Mathf.Approximately(hp, _lastHp))
            {
                if (_lastHp >= 0f && hp < _lastHp) _trailHold = trailDelay; // damage: hold the trail, then drain
                if (_lastHp < 0f || normalized > _trail) _trail = normalized; // first bind / heal: snap
                _lastHp = hp;
                _hpValue.SetInt(Mathf.CeilToInt(Mathf.Max(0f, hp)));
            }
            if (_trailHold > 0f) _trailHold -= dt;
            else _trail = Mathf.MoveTowards(_trail, normalized, trailSpeed * dt);
            _hpFill.fillAmount = normalized;
            _hpTrail.fillAmount = Mathf.Max(_trail, normalized);

            bool pending = health != null && health.IsEliminationPending;
            bool frozen = player.Status != null && player.Status.Has(StatusEffectType.Frozen);
            Color fill;
            if (pending) fill = Color.Lerp(UiTheme.Warning, UiTheme.Danger, HudAnim.Pulse(2.5f));
            else if (frozen) fill = UiTheme.Ice;
            else fill = Color.Lerp(UiTheme.Danger, UiTheme.TextPrimary, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.2f, 0.55f, normalized)));
            _hpFill.color = fill;

            if (_pending.gameObject.activeSelf != pending) _pending.gameObject.SetActive(pending);
            if (pending) _pending.color = UiTheme.WithAlpha(Color.white, 0.65f + 0.35f * HudAnim.Pulse(2.5f));

            // ---------------------------------------------------------------- zone / ball
            bool outfield = !player.IsInfield;
            if (_zone.gameObject.activeSelf != outfield) _zone.gameObject.SetActive(outfield);

            bool hasBall = player.Combat != null && player.Combat.HasBall;
            _ball.color = hasBall ? UiTheme.TextPrimary : UiTheme.Track;

            // ---------------------------------------------------------------- damage flash on the panel
            if (_damageFlash > 0f)
            {
                _damageFlash = Mathf.Max(0f, _damageFlash - dt * 3f);
                _panel.color = Color.Lerp(UiTheme.Panel, new Color(0.55f, 0.06f, 0.05f, 0.85f), _damageFlash);
            }

            TickChips(player);
        }

        private void TickChips(DodgeballPlayer player)
        {
            var status = player.Status;
            int used = 0;
            float x = 0f;
            if (status != null)
            {
                for (int i = 0; i < s_statusTypes.Length && used < MaxChips; i++)
                {
                    var type = s_statusTypes[i];
                    // Permanent passive markers are not interesting to the player who owns them.
                    if (type == StatusEffectType.SilentFootsteps) continue;
                    if (!status.Has(type)) continue;

                    ref var chip = ref _chips[used];
                    if (!chip.Bound || chip.Type != type)
                    {
                        UiTheme.DescribeStatus(type, out string label, out Color color);
                        chip.Type = type;
                        chip.Bound = true;
                        chip.Label.Set(label);
                        chip.Label.SetColor(color);
                        chip.Background.color = new Color(color.r * 0.22f, color.g * 0.22f, color.b * 0.22f, 0.88f);
                        float width = Mathf.Ceil(chip.Label.Text.preferredWidth) + 70f;
                        chip.Rect.sizeDelta = new Vector2(width, 30f);
                    }

                    float remaining = status.GetRemaining(type);
                    chip.Time.Set(float.IsInfinity(remaining) || remaining > 99f ? string.Empty : UiStrings.Tenths(remaining));
                    chip.Rect.anchoredPosition = new Vector2(x, 0f);
                    x += chip.Rect.sizeDelta.x + 8f;
                    if (!chip.Rect.gameObject.activeSelf) chip.Rect.gameObject.SetActive(true);
                    used++;
                }
            }

            for (int i = used; i < MaxChips; i++)
            {
                if (_chips[i].Rect.gameObject.activeSelf) _chips[i].Rect.gameObject.SetActive(false);
                _chips[i].Bound = false;
            }
        }
    }
}
