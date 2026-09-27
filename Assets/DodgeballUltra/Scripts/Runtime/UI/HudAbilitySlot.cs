using DodgeballUltra.Abilities;
using UnityEngine;
using UnityEngine.UI;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// One circular ability slot (Skill or Ultimate) in the bottom-right cluster:
    /// <list type="bullet">
    /// <item>icon (AbilityData.icon, or initials of the ability name),</item>
    /// <item>radial cooldown sweep + remaining seconds,</item>
    /// <item>progress ring: channel progress / active time left (hero accent) or, for the Ultimate, the meter (with %),</item>
    /// <item>soft gold glow + "READY" when the ultimate meter is full,</item>
    /// <item>key prompt chip (keyboard or gamepad glyph, follows the last used device) and the ability name,</item>
    /// <item>flash on cast (white) and on a failed activation (red).</item>
    /// </list>
    /// </summary>
    public sealed class HudAbilitySlot
    {
        private readonly bool _isUltimate;
        private readonly RectTransform _root;
        private readonly Image _glow;
        private readonly Image _disc;
        private readonly Image _icon;
        private readonly Text _iconInitials;
        private readonly Image _cooldown;
        private readonly Image _ringTrack;
        private readonly Image _ring;
        private readonly CachedText _center;
        private readonly CachedText _key;
        private readonly CachedText _name;
        private readonly CachedText _ready;
        private readonly Image _flash;

        private AbilityBase _bound;
        private bool _boundAny;
        private float _flashAmount;
        private Color _flashColor = Color.white;
        private int _shownPercent = -1;

        public HudAbilitySlot(RectTransform parent, string name, Vector2 position, float size, bool isUltimate)
        {
            _isUltimate = isUltimate;
            _root = UiFactory.CreateRect(name, parent).Place(UiAnchor.BottomRight, UiAnchor.BottomRight, position, new Vector2(size + 40f, size + 74f));

            var discRoot = UiFactory.CreateRect("Disc", _root).Place(UiAnchor.Top, UiAnchor.Top, new Vector2(0f, -4f), new Vector2(size, size));

            _glow = UiFactory.CreateImage(discRoot, "Glow", UiFactory.SoftGlowSprite, UiTheme.WithAlpha(UiTheme.Gold, 0f));
            _glow.rectTransform.Place(UiAnchor.Center, UiAnchor.Center, Vector2.zero, new Vector2(size * 1.7f, size * 1.7f));

            _disc = UiFactory.CreateImage(discRoot, "Base", UiFactory.CircleSprite, new Color(0.05f, 0.06f, 0.08f, 0.86f));
            _disc.rectTransform.Stretch();

            _icon = UiFactory.CreateImage(discRoot, "Icon", null, Color.white);
            _icon.rectTransform.Place(UiAnchor.Center, UiAnchor.Center, Vector2.zero, new Vector2(size * 0.6f, size * 0.6f));
            _icon.preserveAspect = true;
            _iconInitials = UiFactory.CreateText(discRoot, "Initials", string.Empty, Mathf.RoundToInt(size * 0.32f), UiTheme.TextPrimary, TextAnchor.MiddleCenter);
            _iconInitials.rectTransform.Stretch();

            _cooldown = UiFactory.CreateFilled(discRoot, "Cooldown", UiFactory.CircleSprite, new Color(0f, 0f, 0f, 0.66f),
                Image.FillMethod.Radial360, (int)Image.Origin360.Top, false);
            _cooldown.rectTransform.Stretch();
            _cooldown.fillAmount = 0f;

            _ringTrack = UiFactory.CreateImage(discRoot, "RingTrack", UiFactory.RingSprite, UiTheme.WithAlpha(Color.white, 0.16f));
            _ringTrack.rectTransform.Stretch(-3f, -3f, -3f, -3f);
            _ring = UiFactory.CreateFilled(discRoot, "Ring", UiFactory.RingSprite, UiTheme.TextPrimary, Image.FillMethod.Radial360,
                (int)Image.Origin360.Top, true);
            _ring.rectTransform.Stretch(-3f, -3f, -3f, -3f);

            var center = UiFactory.CreateText(discRoot, "Center", string.Empty, Mathf.RoundToInt(size * 0.3f), UiTheme.TextPrimary, TextAnchor.MiddleCenter);
            center.rectTransform.Stretch();
            _center = new CachedText(center);

            _flash = UiFactory.CreateImage(discRoot, "Flash", UiFactory.CircleSprite, UiTheme.WithAlpha(Color.white, 0f));
            _flash.rectTransform.Stretch();

            var ready = UiFactory.CreateText(discRoot, "Ready", "READY", 15, UiTheme.Gold, TextAnchor.MiddleCenter);
            ready.rectTransform.Place(UiAnchor.Bottom, UiAnchor.Bottom, new Vector2(0f, 12f), new Vector2(size, 20f));
            _ready = new CachedText(ready);
            _ready.SetActive(false);

            // Key prompt chip + ability name under the disc.
            var chip = UiFactory.CreatePanel(_root, "Key", UiTheme.PanelStrong, true);
            chip.rectTransform.Place(UiAnchor.Bottom, UiAnchor.Bottom, new Vector2(0f, 30f), new Vector2(64f, 28f));
            var key = UiFactory.CreateText(chip.transform, "Label", string.Empty, 17, UiTheme.TextPrimary, TextAnchor.MiddleCenter);
            key.rectTransform.Stretch();
            _key = new CachedText(key);

            var abilityName = UiFactory.CreateText(_root, "Name", string.Empty, 15, UiTheme.TextSecondary, TextAnchor.MiddleCenter);
            abilityName.rectTransform.Place(UiAnchor.Bottom, UiAnchor.Bottom, new Vector2(0f, 2f), new Vector2(size + 80f, 22f));
            _name = new CachedText(abilityName);
        }

        public void SetVisible(bool visible)
        {
            if (_root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        }

        public void Flash(Color color)
        {
            _flashColor = color;
            _flashAmount = 1f;
        }

        /// <summary>Binds icon / name of <paramref name="ability"/> (null = empty slot).</summary>
        public void Bind(AbilityBase ability, Color accent)
        {
            _bound = ability;
            _boundAny = true;
            _shownPercent = -1;
            var data = ability != null ? ability.Data : null;
            var icon = data != null ? data.icon : null;
            _icon.sprite = icon;
            _icon.enabled = icon != null;
            _iconInitials.gameObject.SetActive(icon == null);
            _iconInitials.text = ability != null ? UiFactory.Initials(ability.DisplayName) : "—";
            _iconInitials.color = Color.Lerp(accent, Color.white, 0.55f);
            _name.Set(ability != null ? ability.DisplayName.ToUpperInvariant() : string.Empty);
        }

        /// <param name="ability">Bound ability (null = empty slot).</param>
        /// <param name="accent">Hero theme colour.</param>
        /// <param name="ultimateCharge">0..1 meter (Ultimate slot only).</param>
        /// <param name="ultimateFull">Meter is full.</param>
        /// <param name="keyLabel">Prompt for the current device.</param>
        /// <param name="unusable">Silenced / outfield / cannot act: shown desaturated.</param>
        public void Tick(AbilityBase ability, Color accent, float ultimateCharge, bool ultimateFull, string keyLabel, bool unusable, float dt)
        {
            if (!_boundAny || !ReferenceEquals(ability, _bound)) Bind(ability, accent);
            _key.Set(keyLabel);

            float cooldownFill = 0f;
            float ringFill = 0f;
            Color ringColor = UiTheme.TextPrimary;
            bool showReady = false;
            float glow = 0f;

            if (ability == null)
            {
                _center.Set(string.Empty);
            }
            else
            {
                switch (ability.Phase)
                {
                    case AbilityPhase.Cooldown:
                        cooldownFill = ability.CooldownNormalized;
                        ringFill = 1f - ability.CooldownNormalized;
                        ringColor = UiTheme.WithAlpha(UiTheme.TextSecondary, 0.8f);
                        float remaining = ability.CooldownRemaining;
                        _center.Set(remaining >= 10f ? UiStrings.Int(Mathf.CeilToInt(remaining)) : UiStrings.Tenths(remaining));
                        break;
                    case AbilityPhase.Casting:
                        ringFill = ability.CastProgress;
                        ringColor = accent;
                        _center.Set(string.Empty);
                        break;
                    case AbilityPhase.Active:
                        float duration = ability.Duration;
                        ringFill = duration > 0f ? Mathf.Clamp01(ability.ActiveTimeRemaining / duration) : 1f;
                        ringColor = accent;
                        _center.Set(duration > 0f ? UiStrings.Tenths(ability.ActiveTimeRemaining) : string.Empty);
                        break;
                    default:
                        if (_isUltimate && !ultimateFull)
                        {
                            ringFill = Mathf.Clamp01(ultimateCharge);
                            ringColor = Color.Lerp(accent, UiTheme.TextPrimary, 0.2f);
                            int percent = Mathf.FloorToInt(Mathf.Clamp01(ultimateCharge) * 100f);
                            if (percent != _shownPercent)
                            {
                                _shownPercent = percent;
                                _center.Set(UiStrings.Percent(percent));
                            }
                            cooldownFill = 0f;
                        }
                        else
                        {
                            ringFill = 1f;
                            ringColor = _isUltimate ? UiTheme.Gold : UiTheme.TextPrimary;
                            _center.Set(string.Empty);
                            _shownPercent = -1;
                            if (_isUltimate)
                            {
                                showReady = true;
                                glow = 0.35f + 0.45f * HudAnim.Pulse(1.2f);
                            }
                        }
                        break;
                }
            }

            if (unusable && ability != null)
            {
                ringColor = UiTheme.WithAlpha(UiTheme.Danger, 0.85f);
                glow = 0f;
            }

            _cooldown.fillAmount = cooldownFill;
            _ring.fillAmount = ringFill;
            _ring.color = ringColor;
            _glow.color = UiTheme.WithAlpha(UiTheme.Gold, glow);
            _ready.SetActive(showReady && !unusable);

            float iconAlpha = ability == null ? 0.25f : unusable ? 0.35f : ability.Phase == AbilityPhase.Cooldown ? 0.55f : 1f;
            _icon.color = UiTheme.WithAlpha(Color.white, iconAlpha);
            _iconInitials.color = UiTheme.WithAlpha(_iconInitials.color, iconAlpha);

            if (_flashAmount > 0f)
            {
                _flashAmount = Mathf.Max(0f, _flashAmount - dt * 4f);
                _flash.color = UiTheme.WithAlpha(_flashColor, 0.55f * _flashAmount);
            }
        }
    }
}
