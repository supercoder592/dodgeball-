using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// Visual language of every Dodgeball Ultra screen: a restrained, realistic sports-broadcast look — dark translucent
    /// panels, white typography, team colours and a single gold accent for "perfect" moments. No cartoon colours.
    /// All values are in the 1920×1080 reference space of the CanvasScaler.
    /// </summary>
    public static class UiTheme
    {
        // ------------------------------------------------------------------ canvas
        public static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);
        public const float MatchWidthOrHeight = 0.5f;

        // ------------------------------------------------------------------ surfaces
        /// <summary>Standard HUD panel: near-black, slightly blue, translucent (broadcast lower-third).</summary>
        public static readonly Color Panel = new Color(0.035f, 0.042f, 0.055f, 0.78f);
        /// <summary>Denser panel for menus and text-heavy areas.</summary>
        public static readonly Color PanelStrong = new Color(0.025f, 0.03f, 0.04f, 0.92f);
        /// <summary>Raised element on a panel (buttons, cards at rest).</summary>
        public static readonly Color Surface = new Color(0.11f, 0.125f, 0.15f, 0.95f);
        public static readonly Color SurfaceHover = new Color(0.17f, 0.19f, 0.225f, 1f);
        /// <summary>Hairline separators / strokes.</summary>
        public static readonly Color Hairline = new Color(1f, 1f, 1f, 0.12f);
        /// <summary>Empty bar tracks.</summary>
        public static readonly Color Track = new Color(1f, 1f, 1f, 0.12f);
        /// <summary>Full-screen dim behind menus.</summary>
        public static readonly Color ScreenDim = new Color(0.01f, 0.012f, 0.018f, 0.72f);

        // ------------------------------------------------------------------ text
        public static readonly Color TextPrimary = new Color(0.965f, 0.97f, 0.98f, 1f);
        public static readonly Color TextSecondary = new Color(0.72f, 0.75f, 0.8f, 1f);
        public static readonly Color TextMuted = new Color(0.5f, 0.54f, 0.6f, 1f);
        /// <summary>Soft drop shadow behind all HUD text (legibility over bright HDRP scenes).</summary>
        public static readonly Color TextShadow = new Color(0f, 0f, 0f, 0.6f);

        // ------------------------------------------------------------------ accents
        /// <summary>Perfect catch, victory, ultimate ready.</summary>
        public static readonly Color Gold = new Color(1f, 0.8f, 0.32f, 1f);
        public static readonly Color Danger = new Color(0.9f, 0.16f, 0.14f, 1f);
        public static readonly Color Warning = new Color(1f, 0.62f, 0.2f, 1f);
        public static readonly Color Positive = new Color(0.36f, 0.84f, 0.46f, 1f);
        public static readonly Color Ice = new Color(0.58f, 0.86f, 1f, 1f);

        /// <summary>Home team: broadcast blue.</summary>
        public static readonly Color HomeColor = new Color(0.2f, 0.52f, 0.98f, 1f);
        /// <summary>Away team: broadcast red-orange.</summary>
        public static readonly Color AwayColor = new Color(0.94f, 0.3f, 0.2f, 1f);

        // ------------------------------------------------------------------ type scale (reference px)
        public const int FontCaption = 16;
        public const int FontSmall = 20;
        public const int FontBody = 24;
        public const int FontTitle = 34;
        public const int FontHeadline = 52;
        public const int FontBanner = 88;
        public const int FontCountdown = 220;

        public static Color TeamColor(TeamId team) =>
            team == TeamId.Home ? HomeColor : team == TeamId.Away ? AwayColor : TextSecondary;

        public static string TeamName(TeamId team) =>
            team == TeamId.Home ? "HOME" : team == TeamId.Away ? "AWAY" : "—";

        /// <summary>Rich-text hex for a team (cached, no allocation).</summary>
        public static string TeamHex(TeamId team) => team == TeamId.Home ? HomeHex : team == TeamId.Away ? AwayHex : NeutralHex;

        private static readonly string HomeHex = "#" + ColorUtility.ToHtmlStringRGB(HomeColor);
        private static readonly string AwayHex = "#" + ColorUtility.ToHtmlStringRGB(AwayColor);
        private static readonly string NeutralHex = "#" + ColorUtility.ToHtmlStringRGB(TextSecondary);

        /// <summary>Same colour with a different alpha.</summary>
        public static Color WithAlpha(Color c, float a)
        {
            c.a = a;
            return c;
        }

        /// <summary>Label and colour of a status effect chip on the HUD.</summary>
        public static void DescribeStatus(StatusEffectType type, out string label, out Color color)
        {
            switch (type)
            {
                case StatusEffectType.Slow: label = "SLOWED"; color = new Color(0.55f, 0.66f, 0.8f, 1f); break;
                case StatusEffectType.Haste: label = "HASTE"; color = Positive; break;
                case StatusEffectType.Frozen: label = "FROZEN"; color = Ice; break;
                case StatusEffectType.Stunned: label = "STUNNED"; color = new Color(1f, 0.85f, 0.3f, 1f); break;
                case StatusEffectType.Invulnerable: label = "INVULNERABLE"; color = TextPrimary; break;
                case StatusEffectType.Cloaked: label = "CLOAKED"; color = new Color(0.66f, 0.56f, 0.95f, 1f); break;
                case StatusEffectType.Revealed: label = "REVEALED"; color = Warning; break;
                case StatusEffectType.Silenced: label = "SILENCED"; color = Danger; break;
                case StatusEffectType.Rooted: label = "ROOTED"; color = new Color(0.78f, 0.6f, 0.42f, 1f); break;
                case StatusEffectType.Slippery: label = "ICE"; color = Ice; break;
                case StatusEffectType.DodgeDisabled: label = "NO DODGE"; color = Danger; break;
                case StatusEffectType.SilentFootsteps: label = "SILENT"; color = TextSecondary; break;
                case StatusEffectType.Obscured: label = "MIRAGE"; color = new Color(0.62f, 0.5f, 0.9f, 1f); break;
                case StatusEffectType.Magnetized: label = "MAGNETIZED"; color = new Color(0.7f, 0.78f, 0.86f, 1f); break;
                default: label = type.ToString().ToUpperInvariant(); color = TextSecondary; break;
            }
        }

        /// <summary>Human readable role name for hero cards.</summary>
        public static string RoleName(HeroRole role)
        {
            switch (role)
            {
                case HeroRole.Attacker: return "ATTACKER";
                case HeroRole.Defender: return "DEFENDER";
                case HeroRole.Support: return "SUPPORT";
                case HeroRole.Agility: return "AGILITY";
                default: return role.ToString().ToUpperInvariant();
            }
        }

        /// <summary>Fallback accent per hero when no CharacterData is available (muted, realistic tones).</summary>
        public static Color HeroFallbackColor(HeroId hero)
        {
            switch (hero)
            {
                case HeroId.Rayne: return new Color(0.95f, 0.5f, 0.15f, 1f);
                case HeroId.Shadow: return new Color(0.45f, 0.42f, 0.62f, 1f);
                case HeroId.Gale: return new Color(0.35f, 0.72f, 0.6f, 1f);
                case HeroId.Bear: return new Color(0.72f, 0.5f, 0.3f, 1f);
                case HeroId.Gouki: return new Color(0.75f, 0.25f, 0.2f, 1f);
                case HeroId.Screws: return new Color(0.85f, 0.7f, 0.25f, 1f);
                case HeroId.Houdini: return new Color(0.6f, 0.3f, 0.55f, 1f);
                case HeroId.Elsa: return new Color(0.55f, 0.8f, 0.95f, 1f);
                case HeroId.Specter: return new Color(0.75f, 0.78f, 0.82f, 1f);
                case HeroId.Chrono: return new Color(0.3f, 0.6f, 0.9f, 1f);
                default: return TextSecondary;
            }
        }
    }
}
