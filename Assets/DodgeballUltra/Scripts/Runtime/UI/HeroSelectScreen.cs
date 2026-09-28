using System;
using System.Collections.Generic;
using System.Globalization;
using DodgeballUltra.Abilities;
using DodgeballUltra.AI;
using DodgeballUltra.Characters;
using DodgeballUltra.InputHandling;
using DodgeballUltra.Match;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Random = UnityEngine.Random;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// CONTRACT (kernel) - full-screen hero select built in code (uGUI): 10 hero cards with portrait, role, description
    /// and the three abilities; team choice; bot difficulty; "Play". Keyboard/gamepad/mouse navigable.
    /// <para>
    /// Layout (1920×1080 reference): 5×2 card grid (portrait sprite from CharacterData.portrait, or the hero's initials on
    /// its theme colour), a details panel for the focused hero (role, HP, sprint and throw stats, description, passive /
    /// skill / ultimate with names, keys, cooldown and descriptions), Team (Home / Away) and Bots (Easy..Pro) selectors and
    /// PLAY / WATCH AI MATCH buttons.
    /// </para>
    /// <para>
    /// Navigation: arrows / WASD / left stick / D-pad move the focus (explicit, grid-aware navigation), Enter / gamepad A
    /// confirms, Esc / B jumps back to the hero grid; the mouse works everywhere. Moving the focus over a card selects that
    /// hero; confirming a card moves the focus to PLAY. While open, gameplay input is blocked (<see cref="InputGate"/>),
    /// the cursor is free and the HUD is hidden.
    /// </para>
    /// <para>
    /// On PLAY a <see cref="MatchSetup"/> is built: the local hero in slot 0 of the chosen team and distinct random bot
    /// heroes for every other slot, then the screen hides and the callback runs (GameBootstrap.StartMatch if none).
    /// </para>
    /// <para>Owner module: UI.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HeroSelectScreen : MonoBehaviour
    {
        public static HeroSelectScreen Instance { get; private set; }

        /// <summary>True while the screen is open.</summary>
        public static bool IsVisible => Instance != null && Instance._open;

        [Header("Canvas")]
        [Tooltip("Sorting order of the hero select canvas (above HUD and pause menu).")]
        public int sortingOrder = 400;

        [Header("Behaviour")]
        [Tooltip("Remember the last hero, team and difficulty between sessions (PlayerPrefs).")]
        public bool rememberChoices = true;

        [Tooltip("Offer a 'Watch AI match' button (3 bots vs 3 bots, no human player).")]
        public bool allowSpectate = true;

        [Header("Layout (reference px)")]
        [Tooltip("Hero cards per row.")]
        [Range(2, 10)] public int columns = 5;
        public Vector2 cardSize = new Vector2(214f, 300f);
        [Min(0f)] public float cardSpacing = 18f;

        private const string PrefHero = "DU.HeroSelect.Hero";
        private const string PrefTeam = "DU.HeroSelect.Team";
        private const string PrefDifficulty = "DU.HeroSelect.Difficulty";

        private static readonly BotDifficulty[] s_difficulties = { BotDifficulty.Easy, BotDifficulty.Normal, BotDifficulty.Hard, BotDifficulty.Pro };

        /// <summary>Display data of one hero (from CharacterData, or a bare HeroId fallback).</summary>
        private struct HeroEntry
        {
            public HeroId Id;
            public string Name;
            public string Title;
            public string Role;
            public string Description;
            public Sprite Portrait;
            public Color Color;
            public int Hp;
            public float SprintSpeed;
            public float ThrowKmh;
            public AbilityData Passive;
            public AbilityData Skill;
            public AbilityData Ultimate;
        }

        private sealed class CardView
        {
            public Button Button;
            public RectTransform Rect;
            public Image Frame;
            public float Scale = 1f;
        }

        private struct FocusRing
        {
            public Selectable Target;
            public Image Ring;
        }

        private readonly List<HeroEntry> _entries = new List<HeroEntry>(10);
        private readonly List<CardView> _cards = new List<CardView>(10);
        private readonly List<FocusRing> _rings = new List<FocusRing>(24);
        private readonly Button[] _difficultyButtons = new Button[4];
        private readonly Text[] _difficultyLabels = new Text[4];

        private GameConfig _config;
        private Action<MatchSetup> _callback;
        private List<CharacterData> _fallbackRoster;
        private bool _open;
        private bool _built;
        private int _selected;
        private TeamId _team = TeamId.Home;
        private BotDifficulty _difficulty = BotDifficulty.Normal;

        private Canvas _canvas;
        private RectTransform _content;
        private Button _home;
        private Button _away;
        private Text _homeLabel;
        private Text _awayLabel;
        private Button _play;
        private Button _watch;

        // Details panel
        private Image _detailAccent;
        private Image _detailPortraitBack;
        private Image _detailPortrait;
        private Text _detailInitials;
        private Text _detailName;
        private Text _detailTitle;
        private Text _detailStats;
        private Text _detailDescription;
        private readonly Text[] _abilityHeaders = new Text[3];
        private readonly Text[] _abilityNames = new Text[3];
        private readonly Text[] _abilityDescriptions = new Text[3];

        // ------------------------------------------------------------------ contract

        /// <summary>Shows the screen (creating it if needed). <paramref name="onConfirmed"/> receives the complete setup (bots filled in).</summary>
        public static void Show(GameConfig config, Action<MatchSetup> onConfirmed)
        {
            if (Instance == null)
            {
                var go = new GameObject("DU_HeroSelect");
                go.AddComponent<HeroSelectScreen>();
            }
            Instance.Open(config, onConfirmed);
        }

        public static void Hide()
        {
            if (Instance != null) Instance.Close();
        }

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[Dodgeball Ultra] Duplicate HeroSelectScreen destroyed.", this);
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (_open) InputGate.Unblock(this);
            if (Instance == this) Instance = null;
            if (_fallbackRoster != null)
            {
                for (int i = 0; i < _fallbackRoster.Count; i++)
                    if (_fallbackRoster[i] != null) Destroy(_fallbackRoster[i]);
                _fallbackRoster = null;
            }
        }

        private void OnDisable()
        {
            if (_open) InputGate.Unblock(this);
        }

        private void OnEnable()
        {
            if (_open) InputGate.Block(this);
        }

        private void Update()
        {
            if (!_open) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            var frame = InputService.Current;
            var eventSystem = EventSystem.current;
            var focused = eventSystem != null ? eventSystem.currentSelectedGameObject : null;

            // Focus on a card = choose that hero (keyboard / gamepad browsing).
            if (focused != null)
            {
                for (int i = 0; i < _cards.Count; i++)
                {
                    if (_cards[i].Button.gameObject != focused) continue;
                    if (i != _selected) SelectHero(i);
                    break;
                }
            }
            else if (frame.UiNavigate.sqrMagnitude > 0.25f || frame.UiSubmit.Pressed)
            {
                // A mouse click on empty space cleared the focus: keys / pad bring it back to the chosen hero.
                FocusSelectedCard();
            }

            // Cancel (Esc / B) from the bottom controls returns to the grid.
            if (frame.UiCancel.Pressed && focused != null && !IsCard(focused)) FocusSelectedCard();

            // Card emphasis + keyboard focus rings.
            for (int i = 0; i < _cards.Count; i++)
            {
                var card = _cards[i];
                float target = i == _selected ? 1.035f : 1f;
                card.Scale = HudAnim.Damp(card.Scale, target, 14f, dt);
                card.Rect.localScale = new Vector3(card.Scale, card.Scale, 1f);
            }
            for (int i = 0; i < _rings.Count; i++)
            {
                var ring = _rings[i];
                bool on = focused != null && ring.Target != null && ring.Target.gameObject == focused;
                if (ring.Ring.enabled != on) ring.Ring.enabled = on;
                if (on) ring.Ring.color = UiTheme.WithAlpha(ring.Ring.color, 0.55f + 0.45f * HudAnim.Pulse(1.3f));
            }
        }

        // ------------------------------------------------------------------ open / close

        private void Open(GameConfig config, Action<MatchSetup> onConfirmed)
        {
            _callback = onConfirmed;
            bool configChanged = !ReferenceEquals(config, _config);
            _config = config;

            if (!_built || configChanged)
            {
                ResolveRoster(config);
                Build();
            }

            LoadChoices();
            ApplyTeam(_team);
            ApplyDifficulty(_difficulty);
            SelectHero(Mathf.Clamp(_selected, 0, Mathf.Max(0, _entries.Count - 1)));

            if (PauseMenu.IsPaused) PauseMenu.Instance.Close();
            var hud = HudController.Instance;
            if (hud != null) hud.SetVisible(false);

            _open = true;
            _canvas.gameObject.SetActive(true);
            InputGate.Block(this);
            UiFactory.EnsureEventSystem();
            FocusSelectedCard();
        }

        private void Close()
        {
            if (!_open) return;
            _open = false;
            if (_canvas != null) _canvas.gameObject.SetActive(false);
            var eventSystem = EventSystem.current;
            if (eventSystem != null && !eventSystem.alreadySelecting) eventSystem.SetSelectedGameObject(null);
            InputGate.Unblock(this);
        }

        // ------------------------------------------------------------------ roster

        private void ResolveRoster(GameConfig config)
        {
            _entries.Clear();
            IList<CharacterData> source = config != null ? config.roster : null;
            if (source == null || CountValid(source) == 0)
            {
                if (_fallbackRoster == null)
                {
                    try
                    {
                        _fallbackRoster = HeroRosterFactory.CreateDefaultRoster();
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[Dodgeball Ultra] Hero select: no roster in GameConfig and the default roster failed ({e.Message}).");
                    }
                }
                source = _fallbackRoster;
            }

            if (source != null)
            {
                for (int i = 0; i < source.Count; i++)
                {
                    var data = source[i];
                    if (data == null) continue;
                    var color = data.themeColor;
                    color.a = 1f;
                    _entries.Add(new HeroEntry
                    {
                        Id = data.heroId,
                        Name = string.IsNullOrEmpty(data.displayName) ? data.heroId.ToString() : data.displayName,
                        Title = data.title ?? string.Empty,
                        Role = UiTheme.RoleName(data.role),
                        Description = data.description ?? string.Empty,
                        Portrait = data.portrait,
                        Color = color,
                        Hp = data.maxHp,
                        SprintSpeed = data.movement != null ? data.movement.sprintSpeed : 0f,
                        ThrowKmh = data.combat != null ? data.combat.baseThrowSpeedKmh : 0f,
                        Passive = data.passive,
                        Skill = data.skill,
                        Ultimate = data.ultimate,
                    });
                }
            }

            if (_entries.Count == 0)
            {
                // Last resort: identities only, so the game can still start.
                foreach (HeroId id in Enum.GetValues(typeof(HeroId)))
                {
                    _entries.Add(new HeroEntry
                    {
                        Id = id,
                        Name = id.ToString(),
                        Title = string.Empty,
                        Role = string.Empty,
                        Description = string.Empty,
                        Color = UiTheme.HeroFallbackColor(id),
                        Hp = id == HeroId.Gouki ? Core.GameConstants.ThickHideMaxHp : Core.GameConstants.DefaultMaxHp,
                    });
                }
            }
        }

        private static int CountValid(IList<CharacterData> list)
        {
            int n = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) n++;
            return n;
        }

        // ------------------------------------------------------------------ building

        private void Build()
        {
            if (_canvas == null)
            {
                _canvas = UiFactory.CreateScreenCanvas("DU_HeroSelect", sortingOrder, transform, true);
            }
            if (_content != null) Destroy(_content.gameObject);
            _cards.Clear();
            _rings.Clear();

            var canvasRect = (RectTransform)_canvas.transform;
            _content = UiFactory.CreateRect("Content", canvasRect).Stretch();
            var root = _content;

            // ---------------------------------------------------------------- background (arena shows through slightly)
            var background = UiFactory.CreateImage(root, "Background", UiFactory.WhiteSprite, new Color(0.016f, 0.02f, 0.028f, 0.9f));
            background.rectTransform.Stretch();
            background.raycastTarget = true;
            var shade = UiFactory.CreateImage(root, "Shade", UiFactory.VerticalFadeSprite, new Color(0f, 0f, 0f, 0.55f));
            shade.rectTransform.Stretch();

            // ---------------------------------------------------------------- header
            var accent = UiFactory.CreateImage(root, "HeaderRule", UiFactory.WhiteSprite, UiTheme.Gold);
            accent.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(82f, -44f), new Vector2(56f, 4f));
            var title = UiFactory.CreateText(root, "Title", "DODGEBALL ULTRA", 54, UiTheme.TextPrimary, TextAnchor.UpperLeft);
            title.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(78f, -56f), new Vector2(900f, 64f));
            var subtitle = UiFactory.CreateText(root, "Subtitle", "3V3 SUPERPOWERED DODGEBALL   ·   CHOOSE YOUR HERO", 19, UiTheme.TextSecondary,
                TextAnchor.UpperLeft, FontStyle.Normal);
            subtitle.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(82f, -122f), new Vector2(1100f, 26f));

            // ---------------------------------------------------------------- cards
            int cols = Mathf.Max(1, columns);
            for (int i = 0; i < _entries.Count; i++)
            {
                int col = i % cols;
                int row = i / cols;
                var pos = new Vector2(80f + col * (cardSize.x + cardSpacing), -170f - row * (cardSize.y + cardSpacing));
                _cards.Add(BuildCard(root, i, _entries[i], pos));
            }

            BuildDetails(root);
            BuildControls(root);
            WireNavigation();

            _built = true;
            _canvas.gameObject.SetActive(_open);
        }

        private CardView BuildCard(RectTransform root, int index, in HeroEntry entry, Vector2 position)
        {
            var holder = UiFactory.CreateRect("Card_" + entry.Id, root).Place(UiAnchor.TopLeft, UiAnchor.TopLeft, position, cardSize);
            holder.pivot = UiAnchor.Center;
            holder.anchoredPosition = position + new Vector2(cardSize.x * 0.5f, -cardSize.y * 0.5f);

            var frame = UiFactory.CreatePanel(holder, "Selected", entry.Color);
            frame.rectTransform.Stretch(-5f, -5f, -5f, -5f);
            frame.enabled = false;

            var background = UiFactory.CreatePanel(holder, "Card", UiTheme.Surface);
            background.rectTransform.Stretch();
            background.raycastTarget = true;
            var button = background.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.colors = UiFactory.ButtonColors();
            int captured = index;
            button.onClick.AddListener(() => OnCardClicked(captured));

            // Portrait window.
            var portraitBack = UiFactory.CreatePanel(background.transform, "PortraitBack", Color.Lerp(entry.Color, Color.black, 0.55f), true);
            portraitBack.rectTransform.Place(UiAnchor.Top, UiAnchor.Top, new Vector2(0f, -8f), new Vector2(cardSize.x - 16f, cardSize.y * 0.65f));
            portraitBack.gameObject.AddComponent<RectMask2D>();
            var gradient = UiFactory.CreateImage(portraitBack.transform, "Light", UiFactory.VerticalFadeSprite, UiTheme.WithAlpha(entry.Color, 0.45f));
            gradient.rectTransform.Stretch();
            gradient.rectTransform.localScale = new Vector3(1f, -1f, 1f); // light from the top
            if (entry.Portrait != null)
            {
                var portrait = UiFactory.CreateImage(portraitBack.transform, "Portrait", entry.Portrait, Color.white);
                portrait.rectTransform.Stretch();
                portrait.preserveAspect = true;
            }
            else
            {
                var initials = UiFactory.CreateText(portraitBack.transform, "Initials", UiFactory.Initials(entry.Name, 1), 96,
                    UiTheme.WithAlpha(Color.white, 0.92f), TextAnchor.MiddleCenter);
                initials.rectTransform.Stretch();
            }
            var strip = UiFactory.CreateImage(background.transform, "Strip", UiFactory.WhiteSprite, entry.Color);
            strip.rectTransform.Place(UiAnchor.Top, UiAnchor.Top, new Vector2(0f, -8f - cardSize.y * 0.65f), new Vector2(cardSize.x - 16f, 3f));

            float textTop = -cardSize.y * 0.65f - 20f;
            var name = UiFactory.CreateText(background.transform, "Name", entry.Name.ToUpperInvariant(), 25, UiTheme.TextPrimary, TextAnchor.UpperLeft);
            name.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(14f, textTop), new Vector2(cardSize.x - 24f, 30f));
            var archetype = UiFactory.CreateText(background.transform, "Title", entry.Title.ToUpperInvariant(), 15,
                Color.Lerp(entry.Color, Color.white, 0.35f), TextAnchor.UpperLeft);
            archetype.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(15f, textTop - 32f), new Vector2(cardSize.x - 24f, 20f));
            var role = UiFactory.CreateText(background.transform, "Role", entry.Role, 13, UiTheme.TextMuted, TextAnchor.UpperLeft, FontStyle.Normal);
            role.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(15f, textTop - 54f), new Vector2(cardSize.x - 24f, 18f));

            AddFocusRing(button, holder, 9f);
            return new CardView { Button = button, Rect = holder, Frame = frame };
        }

        private void BuildDetails(RectTransform root)
        {
            float x = 80f + Mathf.Max(1, columns) * (cardSize.x + cardSpacing) + 22f;
            float width = Mathf.Max(420f, 1920f - 80f - x);
            var panel = UiFactory.CreatePanel(root, "Details", UiTheme.Panel);
            panel.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(x, -170f), new Vector2(width, 690f));
            var t = panel.transform;
            float inner = width - 48f;

            _detailAccent = UiFactory.CreateImage(t, "Accent", UiFactory.WhiteSprite, Color.white);
            _detailAccent.rectTransform.Place(UiAnchor.Top, UiAnchor.Top, Vector2.zero, new Vector2(width, 4f));

            _detailPortraitBack = UiFactory.CreatePanel(t, "Portrait", UiTheme.Surface, true);
            _detailPortraitBack.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(24f, -28f), new Vector2(150f, 150f));
            _detailPortraitBack.gameObject.AddComponent<RectMask2D>();
            _detailPortrait = UiFactory.CreateImage(_detailPortraitBack.transform, "Image", null, Color.white);
            _detailPortrait.rectTransform.Stretch();
            _detailPortrait.preserveAspect = true;
            _detailInitials = UiFactory.CreateText(_detailPortraitBack.transform, "Initials", string.Empty, 76, Color.white, TextAnchor.MiddleCenter);
            _detailInitials.rectTransform.Stretch();

            _detailName = UiFactory.CreateText(t, "Name", string.Empty, 50, UiTheme.TextPrimary, TextAnchor.UpperLeft);
            _detailName.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(194f, -26f), new Vector2(inner - 170f, 60f));
            _detailTitle = UiFactory.CreateText(t, "Title", string.Empty, 22, UiTheme.TextPrimary, TextAnchor.UpperLeft);
            _detailTitle.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(196f, -90f), new Vector2(inner - 170f, 28f));
            _detailStats = UiFactory.CreateParagraph(t, "Stats", string.Empty, 16, UiTheme.TextSecondary);
            _detailStats.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(196f, -124f), new Vector2(inner - 170f, 50f));

            _detailDescription = UiFactory.CreateParagraph(t, "Description", string.Empty, 19, UiTheme.TextPrimary);
            _detailDescription.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(24f, -196f), new Vector2(inner, 96f));

            var rule = UiFactory.CreateImage(t, "Rule", UiFactory.WhiteSprite, UiTheme.Hairline);
            rule.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(24f, -300f), new Vector2(inner, 1f));

            for (int i = 0; i < 3; i++)
            {
                float top = -318f - i * 124f;
                _abilityHeaders[i] = UiFactory.CreateText(t, "AbilityHeader" + i, string.Empty, 14, UiTheme.TextMuted, TextAnchor.UpperLeft);
                _abilityHeaders[i].rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(24f, top), new Vector2(inner, 20f));
                _abilityNames[i] = UiFactory.CreateText(t, "AbilityName" + i, string.Empty, 24, UiTheme.TextPrimary, TextAnchor.UpperLeft);
                _abilityNames[i].rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(24f, top - 22f), new Vector2(inner, 30f));
                _abilityDescriptions[i] = UiFactory.CreateParagraph(t, "AbilityDescription" + i, string.Empty, 16, UiTheme.TextSecondary);
                _abilityDescriptions[i].rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(24f, top - 56f), new Vector2(inner, 60f));
            }
        }

        private void BuildControls(RectTransform root)
        {
            // ---------------------------------------------------------------- team
            var teamLabel = UiFactory.CreateText(root, "TeamLabel", "TEAM", 17, UiTheme.TextMuted, TextAnchor.MiddleLeft);
            teamLabel.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(82f, -820f), new Vector2(110f, 58f));
            _home = UiFactory.CreateButton(root, "Home", "HOME", 22, () => OnTeamClicked(TeamId.Home), out _homeLabel);
            _home.GetComponent<RectTransform>().Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(196f, -820f), new Vector2(206f, 58f));
            _away = UiFactory.CreateButton(root, "Away", "AWAY", 22, () => OnTeamClicked(TeamId.Away), out _awayLabel);
            _away.GetComponent<RectTransform>().Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(414f, -820f), new Vector2(206f, 58f));
            AddFocusRing(_home, (RectTransform)_home.transform, 5f);
            AddFocusRing(_away, (RectTransform)_away.transform, 5f);

            // ---------------------------------------------------------------- bot difficulty
            var botsLabel = UiFactory.CreateText(root, "BotsLabel", "BOTS", 17, UiTheme.TextMuted, TextAnchor.MiddleLeft);
            botsLabel.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(82f, -900f), new Vector2(110f, 58f));
            for (int i = 0; i < s_difficulties.Length; i++)
            {
                var difficulty = s_difficulties[i];
                var label = difficulty.ToString().ToUpperInvariant();
                var button = UiFactory.CreateButton(root, "Difficulty" + label, label, 20, () => OnDifficultyClicked(difficulty), out var text);
                button.GetComponent<RectTransform>().Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(196f + i * 158f, -900f), new Vector2(148f, 58f));
                _difficultyButtons[i] = button;
                _difficultyLabels[i] = text;
                AddFocusRing(button, (RectTransform)button.transform, 5f);
            }

            var hint = UiFactory.CreateText(root, "Hint",
                "ARROWS / STICK  navigate      ENTER / A  confirm      ESC / B  back to heroes      MOUSE  click", 15, UiTheme.TextMuted,
                TextAnchor.MiddleLeft, FontStyle.Normal);
            hint.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(82f, -1000f), new Vector2(1100f, 30f));

            // ---------------------------------------------------------------- play / watch
            float x = 80f + Mathf.Max(1, columns) * (cardSize.x + cardSpacing) + 22f;
            float width = Mathf.Max(420f, 1920f - 80f - x);
            _play = UiFactory.CreateButton(root, "Play", "PLAY", 38, OnPlayClicked, out var playText);
            var playImage = (Image)_play.targetGraphic;
            playImage.color = new Color(0.96f, 0.965f, 0.975f, 1f);
            playText.color = new Color(0.04f, 0.05f, 0.07f, 1f);
            playText.GetComponent<Shadow>().enabled = false;
            _play.GetComponent<RectTransform>().Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(x, -880f), new Vector2(width, 92f));
            AddFocusRing(_play, (RectTransform)_play.transform, 6f, UiTheme.Gold);

            _watch = UiFactory.CreateButton(root, "Watch", "WATCH AI MATCH", 18, OnWatchClicked, out _);
            _watch.GetComponent<RectTransform>().Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(x, -988f), new Vector2(width, 50f));
            _watch.gameObject.SetActive(allowSpectate);
            AddFocusRing(_watch, (RectTransform)_watch.transform, 5f);
        }

        /// <summary>Keyboard / gamepad focus indicator: a pulsing rounded outline around the selectable.</summary>
        private void AddFocusRing(Selectable target, RectTransform owner, float grow, Color? color = null)
        {
            var ring = UiFactory.CreateImage(owner, "Focus", UiFactory.RoundedOutlineSprite, color ?? Color.white, Image.Type.Sliced);
            ring.rectTransform.Stretch(-grow, -grow, -grow, -grow);
            ring.enabled = false;
            _rings.Add(new FocusRing { Target = target, Ring = ring });
        }

        // ------------------------------------------------------------------ navigation

        private void WireNavigation()
        {
            int cols = Mathf.Max(1, columns);
            int count = _cards.Count;
            Selectable selectedCard = count > 0 ? _cards[Mathf.Clamp(_selected, 0, count - 1)].Button : null;

            for (int i = 0; i < count; i++)
            {
                int col = i % cols;
                Selectable left = col > 0 ? _cards[i - 1].Button : null;
                Selectable right = col < cols - 1 && i + 1 < count ? _cards[i + 1].Button : (Selectable)_play;
                Selectable up = i - cols >= 0 ? _cards[i - cols].Button : null;
                Selectable down = i + cols < count ? _cards[i + cols].Button : _home;
                UiFactory.SetNavigation(_cards[i].Button, up, down, left, right);
            }

            Selectable currentDifficulty = _difficultyButtons[Mathf.Clamp(DifficultyIndex(_difficulty), 0, _difficultyButtons.Length - 1)];
            Selectable currentTeam = _team == TeamId.Away ? _away : _home;
            UiFactory.SetNavigation(_home, selectedCard, currentDifficulty, null, _away);
            UiFactory.SetNavigation(_away, selectedCard, currentDifficulty, _home, _play);
            for (int i = 0; i < _difficultyButtons.Length; i++)
            {
                Selectable left = i > 0 ? _difficultyButtons[i - 1] : null;
                Selectable right = i < _difficultyButtons.Length - 1 ? _difficultyButtons[i + 1] : (Selectable)_play;
                UiFactory.SetNavigation(_difficultyButtons[i], currentTeam, null, left, right);
            }
            Selectable watch = allowSpectate ? _watch : null;
            UiFactory.SetNavigation(_play, selectedCard, watch, currentDifficulty, null);
            if (_watch != null) UiFactory.SetNavigation(_watch, _play, null, currentDifficulty, null);
        }

        private bool IsCard(GameObject go)
        {
            for (int i = 0; i < _cards.Count; i++)
                if (_cards[i].Button.gameObject == go) return true;
            return false;
        }

        private void FocusSelectedCard()
        {
            if (_cards.Count == 0) return;
            UiFactory.Focus(_cards[Mathf.Clamp(_selected, 0, _cards.Count - 1)].Button.gameObject);
        }

        // ------------------------------------------------------------------ selection

        private void SelectHero(int index)
        {
            if (_entries.Count == 0) return;
            _selected = Mathf.Clamp(index, 0, _entries.Count - 1);
            var entry = _entries[_selected];

            for (int i = 0; i < _cards.Count; i++) _cards[i].Frame.enabled = i == _selected;

            _detailAccent.color = entry.Color;
            _detailPortrait.sprite = entry.Portrait;
            _detailPortrait.enabled = entry.Portrait != null;
            _detailInitials.gameObject.SetActive(entry.Portrait == null);
            _detailInitials.text = UiFactory.Initials(entry.Name, 1);
            _detailPortraitBack.color = entry.Portrait != null ? UiTheme.Surface : Color.Lerp(entry.Color, Color.black, 0.4f);
            _detailName.text = entry.Name.ToUpperInvariant();
            _detailTitle.text = entry.Title.ToUpperInvariant();
            _detailTitle.color = Color.Lerp(entry.Color, Color.white, 0.3f);
            _detailStats.text = BuildStats(entry);
            _detailDescription.text = entry.Description;

            SetAbility(0, entry.Passive, "PASSIVE", null, entry.Color);
            SetAbility(1, entry.Skill, "SKILL", GameAction.Skill, entry.Color);
            SetAbility(2, entry.Ultimate, "ULTIMATE", GameAction.Ultimate, entry.Color);

            WireNavigation();
        }

        private static string BuildStats(in HeroEntry entry)
        {
            var ci = CultureInfo.InvariantCulture;
            string stats = string.IsNullOrEmpty(entry.Role) ? string.Empty : entry.Role + "   ·   ";
            stats += entry.Hp.ToString(ci) + " HP";
            if (entry.SprintSpeed > 0f) stats += "   ·   SPRINT " + entry.SprintSpeed.ToString("0.0", ci) + " m/s";
            if (entry.ThrowKmh > 0f) stats += "   ·   THROW " + entry.ThrowKmh.ToString("0", ci) + " km/h";
            return stats;
        }

        private void SetAbility(int row, AbilityData data, string slotLabel, GameAction? action, Color accent)
        {
            var ci = CultureInfo.InvariantCulture;
            string header = slotLabel;
            if (action.HasValue) header += "   ·   " + InputPrompts.Get(action.Value, false) + " / " + InputPrompts.Get(action.Value, true);
            if (data != null)
            {
                if (data.slot == AbilitySlot.Ultimate) header += "   ·   " + Mathf.RoundToInt(data.ultimateCost * 100f).ToString(ci) + "% METER";
                else if (data.slot == AbilitySlot.Skill && data.cooldown > 0f) header += "   ·   " + data.cooldown.ToString("0.#", ci) + " s COOLDOWN";
                if (data.duration > 0f) header += "   ·   " + data.duration.ToString("0.#", ci) + " s";
            }
            _abilityHeaders[row].text = header;
            _abilityNames[row].text = data != null ? data.displayName.ToUpperInvariant() : "—";
            _abilityNames[row].color = Color.Lerp(accent, Color.white, 0.45f);
            _abilityDescriptions[row].text = data != null && !string.IsNullOrEmpty(data.description) ? data.description : string.Empty;
        }

        private void ApplyTeam(TeamId team)
        {
            _team = team == TeamId.Away ? TeamId.Away : TeamId.Home;
            StyleToggle(_home, _homeLabel, _team == TeamId.Home, UiTheme.HomeColor);
            StyleToggle(_away, _awayLabel, _team == TeamId.Away, UiTheme.AwayColor);
            WireNavigation();
        }

        private void ApplyDifficulty(BotDifficulty difficulty)
        {
            _difficulty = difficulty;
            int selected = DifficultyIndex(difficulty);
            for (int i = 0; i < _difficultyButtons.Length; i++)
                StyleToggle(_difficultyButtons[i], _difficultyLabels[i], i == selected, UiTheme.TextPrimary);
            WireNavigation();
        }

        /// <summary>Selected toggles are filled with their colour; unselected ones are dark surfaces.</summary>
        private static void StyleToggle(Button button, Text label, bool selected, Color color)
        {
            if (button == null) return;
            var image = (Image)button.targetGraphic;
            bool light = color.r + color.g + color.b > 2.4f;
            image.color = selected ? color : UiTheme.Surface;
            label.color = selected ? (light ? new Color(0.04f, 0.05f, 0.07f, 1f) : Color.white) : UiTheme.TextSecondary;
            var shadow = label.GetComponent<Shadow>();
            if (shadow != null) shadow.enabled = !(selected && light);
        }

        private static int DifficultyIndex(BotDifficulty difficulty)
        {
            for (int i = 0; i < s_difficulties.Length; i++)
                if (s_difficulties[i] == difficulty) return i;
            return 1;
        }

        // ------------------------------------------------------------------ callbacks

        private void OnCardClicked(int index)
        {
            SelectHero(index);
            if (_play != null) UiFactory.Focus(_play.gameObject); // confirm flow: card -> PLAY
        }

        private void OnTeamClicked(TeamId team) => ApplyTeam(team);

        private void OnDifficultyClicked(BotDifficulty difficulty) => ApplyDifficulty(difficulty);

        private void OnPlayClicked() => Confirm(false);

        private void OnWatchClicked() => Confirm(true);

        private void Confirm(bool spectate)
        {
            if (!_open || _entries.Count == 0) return;
            var setup = BuildSetup(spectate);
            SaveChoices();

            var callback = _callback;
            _callback = null;
            Close();

            try
            {
                if (callback != null)
                {
                    callback(setup);
                }
                else if (GameBootstrap.Instance != null)
                {
                    GameBootstrap.Instance.StartMatch(setup);
                }
                else
                {
                    Debug.LogWarning("[Dodgeball Ultra] Hero select confirmed but nobody is listening (no callback, no GameBootstrap).");
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>
        /// Local hero in slot 0 of the chosen team; every other slot gets a distinct random hero (repeats only if the roster is
        /// smaller than the number of slots).
        /// </summary>
        private MatchSetup BuildSetup(bool spectate)
        {
            int perTeam = 3;
            if (_config != null && _config.rules != null) perTeam = _config.rules.playersPerTeam;
            perTeam = Mathf.Clamp(perTeam, 1, 6);

            var localHero = _entries[_selected].Id;
            var pool = new List<HeroId>(_entries.Count);
            for (int i = 0; i < _entries.Count; i++)
            {
                var id = _entries[i].Id;
                if (!spectate && id == localHero) continue;
                if (!pool.Contains(id)) pool.Add(id);
            }
            if (pool.Count == 0) pool.Add(localHero);
            Shuffle(pool);

            var home = new HeroId[perTeam];
            var away = new HeroId[perTeam];
            int next = 0;
            HeroId Draw()
            {
                if (next >= pool.Count)
                {
                    Shuffle(pool);
                    next = 0;
                }
                return pool[next++];
            }

            if (spectate)
            {
                for (int i = 0; i < perTeam; i++) home[i] = Draw();
                for (int i = 0; i < perTeam; i++) away[i] = Draw();
            }
            else
            {
                var mine = _team == TeamId.Away ? away : home;
                var theirs = _team == TeamId.Away ? home : away;
                mine[0] = localHero;
                for (int i = 1; i < perTeam; i++) mine[i] = Draw();
                for (int i = 0; i < perTeam; i++) theirs[i] = Draw();
            }

            return new MatchSetup
            {
                LocalHero = spectate ? home[0] : localHero,
                LocalTeam = _team,
                HomeHeroes = home,
                AwayHeroes = away,
                Difficulty = _difficulty,
                Spectate = spectate,
            };
        }

        private static void Shuffle(List<HeroId> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                var tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }

        // ------------------------------------------------------------------ persistence

        private void LoadChoices()
        {
            _difficulty = _config != null ? _config.defaultDifficulty : BotDifficulty.Normal;
            if (!rememberChoices) return;

            if (PlayerPrefs.HasKey(PrefHero))
            {
                var hero = (HeroId)PlayerPrefs.GetInt(PrefHero);
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].Id != hero) continue;
                    _selected = i;
                    break;
                }
            }
            if (PlayerPrefs.HasKey(PrefTeam)) _team = PlayerPrefs.GetInt(PrefTeam) == (int)TeamId.Away ? TeamId.Away : TeamId.Home;
            if (PlayerPrefs.HasKey(PrefDifficulty))
            {
                int d = PlayerPrefs.GetInt(PrefDifficulty);
                if (d >= (int)BotDifficulty.Easy && d <= (int)BotDifficulty.Pro) _difficulty = (BotDifficulty)d;
            }
        }

        private void SaveChoices()
        {
            if (!rememberChoices || _entries.Count == 0) return;
            PlayerPrefs.SetInt(PrefHero, (int)_entries[_selected].Id);
            PlayerPrefs.SetInt(PrefTeam, (int)_team);
            PlayerPrefs.SetInt(PrefDifficulty, (int)_difficulty);
            PlayerPrefs.Save();
        }
    }
}
