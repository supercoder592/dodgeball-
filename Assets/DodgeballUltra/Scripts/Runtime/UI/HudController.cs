using System;
using System.Globalization;
using DodgeballUltra.Abilities;
using DodgeballUltra.CameraSystem;
using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.InputHandling;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;
using UnityEngine.UI;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// CONTRACT (kernel) - the in-game HUD, built entirely in code with uGUI (no prefabs): HP bar, ultimate meter,
    /// skill/ultimate cooldowns, throw charge bar, catch timing feedback (PERFECT!), crosshair and target lock marker,
    /// round timer + score + infield pips, kill feed, centre banners, Danger Sense red screen edges, minimap, pause menu.
    /// Reacts to game events; reads the local player every frame.
    /// <para>
    /// Layout (1920×1080 reference, ScreenSpaceOverlay, CanvasScaler match 0.5):
    /// <code>
    /// ┌ minimap ───────────── [HOME | 2:14 ROUND 2 | AWAY] ───────────── kill feed ┐
    /// │                          ROUND 2 / FIGHT / VICTORY banner                   │
    /// │                    PERFECT CATCH!      ┼ crosshair / lock box             │
    /// │                                        ▬▬▬▬ charge ▬ overcharge            │
    /// └ status chips / player card + HP ──── BALL SPEED ──── skill ◯ ultimate ◯ ──┘
    /// </code>
    /// Widgets are plain classes (HudScoreboard, HudPlayerCard, HudAbilitySlot, HudReticle, HudLockMarker, HudKillFeed,
    /// HudMinimap, HudBanner, HudDangerVignette, HudSpeedReadout) ticked from <c>LateUpdate</c> with unscaled time (runs
    /// after the camera rig, see DefaultExecutionOrder), so the HUD keeps animating through hitstop and pause. Text is
    /// only rewritten when a value changes; number strings come from <see cref="UiStrings"/> caches.
    /// </para>
    /// <para>Owner module: UI.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1000)]
    public sealed class HudController : MonoBehaviour
    {
        public static HudController Instance { get; private set; }

        [Header("Canvas")]
        [Tooltip("Sorting order of the HUD canvas (menus draw above it).")]
        public int sortingOrder = 100;

        [Tooltip("Distance of HUD elements from the screen edges (reference px at 1920x1080).")]
        [Range(0f, 120f)] public float safeMargin = 40f;

        [Header("Minimap")]
        [Tooltip("Minimap panel size (reference px). Portrait: the court's long axis is vertical.")]
        public Vector2 minimapSize = new Vector2(210f, 340f);

        [Header("Kill feed")]
        [Tooltip("Rows in the kill feed.")]
        [Range(1, 10)] public int killFeedEntries = 5;

        [Tooltip("Seconds a kill feed entry stays on screen.")]
        [Min(1f)] public float killFeedLifetime = 6f;

        [Header("Banners (unscaled seconds)")]
        [Min(0.4f)] public float roundBannerTime = 1.8f;
        [Min(0.4f)] public float fightBannerTime = 1f;
        [Min(0.4f)] public float roundEndBannerTime = 2.8f;
        [Min(0.4f)] public float matchEndBannerTime = 5f;
        [Min(0.4f)] public float eliminatedBannerTime = 2.2f;

        [Header("Feedback (unscaled seconds)")]
        [Tooltip("How long 'PERFECT CATCH!' stays on screen.")]
        [Min(0.2f)] public float perfectCatchFeedbackTime = 1.4f;
        [Min(0.2f)] public float catchFeedbackTime = 1f;
        [Min(0.2f)] public float whiffFeedbackTime = 0.8f;
        [Tooltip("How long the last throw's speed readout stays visible.")]
        [Min(0.5f)] public float speedReadoutTime = 3.5f;
        [Min(0.3f)] public float toastTime = 1.1f;

        [Header("Throw charge")]
        [Tooltip("Total charge time (s) at which the overcharge segment is full (Rayne's Overcharge: +50% speed over 2 s).")]
        [Min(0.1f)] public float overchargeSeconds = 2f;

        [Header("Danger Sense")]
        [Tooltip("Peak alpha of the red screen-edge vignette.")]
        [Range(0f, 1f)] public float dangerMaxAlpha = 0.6f;

        [Header("Context prompts")]
        [Tooltip("Show '[E] PICK UP' when a free ball is within reach.")]
        public bool showPickupPrompt = true;

        // ------------------------------------------------------------------ runtime
        private bool _built;
        private bool _visible = true;
        private Canvas _canvas;
        private RectTransform _canvasRect;

        private HudDangerVignette _danger;
        private HudMinimap _minimap;
        private HudScoreboard _scoreboard;
        private HudKillFeed _killFeed;
        private HudLockMarker _lockMarker;
        private HudReticle _reticle;
        private HudSpeedReadout _speed;
        private HudPlayerCard _card;
        private HudAbilitySlot _skill;
        private HudAbilitySlot _ultimate;
        private HudBanner _banner;
        private CachedText _passive;
        private HudFader _toastFader;
        private CachedText _toast;

        private DodgeballPlayer _boundPlayer;
        private AbilityBase _boundPassive;
        private Color _accent = Color.white;
        private bool _hasOvercharge;
        private bool _bindPending = true;
        private int _lastBannerRound = -1;

        private DodgeballPlayer _pickupQueryPlayer;
        private Predicate<DodgeBall> _pickupFilter;

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[Dodgeball Ultra] A second HudController was created; keeping the first one.", this);
                enabled = false;
                return;
            }
            Instance = this;
            _pickupFilter = IsPickupCandidate;
        }

        private void Start()
        {
            if (Instance == this && !_built) Initialize();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void OnEnable()
        {
            GameEvents.Subscribe<MatchStartedEvent>(OnMatchStarted);
            GameEvents.Subscribe<MatchPhaseChangedEvent>(OnPhaseChanged);
            GameEvents.Subscribe<RoundCountdownEvent>(OnRoundCountdown);
            GameEvents.Subscribe<RoundStartedEvent>(OnRoundStarted);
            GameEvents.Subscribe<RoundEndedEvent>(OnRoundEnded);
            GameEvents.Subscribe<MatchEndedEvent>(OnMatchEnded);
            GameEvents.Subscribe<PlayerEliminatedEvent>(OnPlayerEliminated);
            GameEvents.Subscribe<PlayerRevivedEvent>(OnPlayerRevived);
            GameEvents.Subscribe<PlayerDamagedEvent>(OnPlayerDamaged);
            GameEvents.Subscribe<BallThrownEvent>(OnBallThrown);
            GameEvents.Subscribe<BallHitPlayerEvent>(OnBallHitPlayer);
            GameEvents.Subscribe<BallCaughtEvent>(OnBallCaught);
            GameEvents.Subscribe<CatchWhiffEvent>(OnCatchWhiff);
            GameEvents.Subscribe<DangerSenseEvent>(OnDangerSense);
            GameEvents.Subscribe<UltimateChargeChangedEvent>(OnUltimateCharge);
            GameEvents.Subscribe<AbilityCastEvent>(OnAbilityCast);
            GameEvents.Subscribe<AbilityFailedEvent>(OnAbilityFailed);
        }

        private void OnDisable()
        {
            GameEvents.Unsubscribe<MatchStartedEvent>(OnMatchStarted);
            GameEvents.Unsubscribe<MatchPhaseChangedEvent>(OnPhaseChanged);
            GameEvents.Unsubscribe<RoundCountdownEvent>(OnRoundCountdown);
            GameEvents.Unsubscribe<RoundStartedEvent>(OnRoundStarted);
            GameEvents.Unsubscribe<RoundEndedEvent>(OnRoundEnded);
            GameEvents.Unsubscribe<MatchEndedEvent>(OnMatchEnded);
            GameEvents.Unsubscribe<PlayerEliminatedEvent>(OnPlayerEliminated);
            GameEvents.Unsubscribe<PlayerRevivedEvent>(OnPlayerRevived);
            GameEvents.Unsubscribe<PlayerDamagedEvent>(OnPlayerDamaged);
            GameEvents.Unsubscribe<BallThrownEvent>(OnBallThrown);
            GameEvents.Unsubscribe<BallHitPlayerEvent>(OnBallHitPlayer);
            GameEvents.Unsubscribe<BallCaughtEvent>(OnBallCaught);
            GameEvents.Unsubscribe<CatchWhiffEvent>(OnCatchWhiff);
            GameEvents.Unsubscribe<DangerSenseEvent>(OnDangerSense);
            GameEvents.Unsubscribe<UltimateChargeChangedEvent>(OnUltimateCharge);
            GameEvents.Unsubscribe<AbilityCastEvent>(OnAbilityCast);
            GameEvents.Unsubscribe<AbilityFailedEvent>(OnAbilityFailed);
        }

        // ------------------------------------------------------------------ contract

        /// <summary>Builds the HUD hierarchy (idempotent) and makes sure a <see cref="PauseMenu"/> exists.</summary>
        public void Initialize()
        {
            if (Instance != null && Instance != this)
            {
                Instance.Initialize();
                return;
            }
            Instance = this;
            if (_pickupFilter == null) _pickupFilter = IsPickupCandidate;
            if (_built) return;

            _canvas = UiFactory.CreateScreenCanvas("DU_HUD", sortingOrder, transform, false);
            _canvasRect = (RectTransform)_canvas.transform;
            var root = _canvasRect;

            // Creation order = draw order (vignette at the back, banners in front).
            _danger = new HudDangerVignette(root) { maxAlpha = dangerMaxAlpha };
            _minimap = new HudMinimap(root, safeMargin, minimapSize);
            _scoreboard = new HudScoreboard(root, safeMargin);
            _killFeed = new HudKillFeed(root, safeMargin, killFeedEntries) { entryLifetime = killFeedLifetime };
            _lockMarker = new HudLockMarker(root);
            _reticle = new HudReticle(root);
            _speed = new HudSpeedReadout(root, safeMargin) { displayTime = speedReadoutTime };
            _card = new HudPlayerCard(root, safeMargin);

            const float ultimateSize = 120f;
            const float skillSize = 100f;
            _ultimate = new HudAbilitySlot(root, "Ultimate", new Vector2(-safeMargin, safeMargin), ultimateSize, true);
            _skill = new HudAbilitySlot(root, "Skill", new Vector2(-safeMargin - (ultimateSize + 40f) - 10f, safeMargin), skillSize, false);

            var passive = UiFactory.CreateText(root, "Passive", string.Empty, 15, UiTheme.TextSecondary, TextAnchor.LowerRight);
            passive.rectTransform.Place(UiAnchor.BottomRight, UiAnchor.BottomRight, new Vector2(-safeMargin - 8f, safeMargin + ultimateSize + 82f),
                new Vector2(420f, 22f));
            _passive = new CachedText(passive);

            var toastRoot = UiFactory.CreateRect("Toast", root).Place(UiAnchor.BottomRight, UiAnchor.BottomRight,
                new Vector2(-safeMargin - 8f, safeMargin + ultimateSize + 108f), new Vector2(420f, 30f));
            var toast = UiFactory.CreateText(toastRoot, "Text", string.Empty, 20, UiTheme.Danger, TextAnchor.MiddleRight);
            toast.rectTransform.Stretch();
            _toast = new CachedText(toast);
            _toastFader = new HudFader(toastRoot, 0.06f, 0.3f);

            _banner = new HudBanner(root);

            if (GetComponent<PauseMenu>() == null && PauseMenu.Instance == null) gameObject.AddComponent<PauseMenu>();

            _built = true;
            _bindPending = true;
            ApplyVisibility();
        }

        /// <summary>Shows a centre-screen banner (e.g. "ROUND 2", "VICTORY").</summary>
        public void ShowBanner(string text, Color color, float duration = 1.5f)
        {
            if (!_built) Initialize();
            _banner?.Show(text, null, color, duration);
        }

        /// <summary>Shows / hides the whole HUD (hero select, cinematics). The pause menu is separate.</summary>
        public void SetVisible(bool visible)
        {
            if (!_built) Initialize();
            _visible = visible;
            ApplyVisibility();
        }

        /// <summary>True while the HUD canvas is shown.</summary>
        public bool IsVisible => _visible;

        /// <summary>Banner with a subtitle line (round end reasons, final scores).</summary>
        public void ShowBanner(string text, string subtitle, Color color, float duration)
        {
            if (!_built) Initialize();
            _banner?.Show(text, subtitle, color, duration);
        }

        private void ApplyVisibility()
        {
            if (_canvas != null && _canvas.enabled != _visible) _canvas.enabled = _visible;
            if (!_visible) _danger?.Clear();
        }

        // ------------------------------------------------------------------ per frame

        private void LateUpdate()
        {
            if (!_built || !_visible) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);

            var local = ResolveLocalPlayer();
            if (_bindPending || !ReferenceEquals(local, _boundPlayer)) Bind(local);
            bool hasLocal = local != null && local.IsInitialized;

            _card.SetVisible(hasLocal);
            _skill.SetVisible(hasLocal);
            _ultimate.SetVisible(hasLocal);
            _reticle.SetVisible(hasLocal);
            _lockMarker.SetVisible(hasLocal);
            _passive.SetActive(hasLocal);

            _scoreboard.Tick(local, dt);
            _minimap.Tick(local);
            _killFeed.Tick(dt);
            _banner.Tick(dt);
            _speed.Tick(dt);
            _danger.Tick(dt);
            _toastFader.Tick(dt);

            if (hasLocal) TickLocal(local, dt);
        }

        private void TickLocal(DodgeballPlayer local, float dt)
        {
            _card.Tick(local, dt);

            // ---------------------------------------------------------------- abilities
            bool gamepad = InputService.UsingGamepad;
            var abilities = local.Abilities;
            var skill = abilities != null ? abilities.Skill : null;
            var ultimate = abilities != null ? abilities.Ultimate : null;
            bool silenced = local.Status != null && local.Status.Has(StatusEffectType.Silenced);

            _skill.Tick(skill, _accent, 0f, false, InputPrompts.Get(GameAction.Skill, gamepad),
                silenced || BlockedByZone(skill, local), dt);
            _ultimate.Tick(ultimate, _accent, abilities != null ? abilities.UltimateCharge : 0f,
                abilities != null && abilities.IsUltimateFull, InputPrompts.Get(GameAction.Ultimate, gamepad),
                silenced || BlockedByZone(ultimate, local), dt);

            var passive = abilities != null ? abilities.Passive : null;
            if (!ReferenceEquals(passive, _boundPassive))
            {
                _boundPassive = passive;
                _passive.Set(passive != null ? "PASSIVE  ·  " + passive.DisplayName.ToUpperInvariant() : string.Empty);
                _hasOvercharge = DetectOvercharge(local);
            }

            // ---------------------------------------------------------------- reticle / lock
            var combat = local.Combat;
            var target = combat != null ? combat.CurrentTarget : null;
            Color targetColor = target != null ? UiTheme.TeamColor(target.Team) : Color.clear;
            bool prompt = showPickupPrompt && ShouldShowPickupPrompt(local);
            _reticle.Tick(local, _hasOvercharge, overchargeSeconds, targetColor, prompt, InputPrompts.Get(GameAction.Pickup, gamepad), dt);

            var rig = ThirdPersonCameraRig.Instance;
            Camera cam = rig != null && rig.Camera != null ? rig.Camera : Camera.main;
            _lockMarker.Tick(local, target, cam, _canvasRect, _canvas.scaleFactor, dt);
        }

        private void Bind(DodgeballPlayer local)
        {
            _bindPending = false;
            _boundPlayer = local;
            _boundPassive = null;
            _passive.Set(string.Empty);
            _card.Bind(local);
            if (local == null)
            {
                _accent = Color.white;
                _hasOvercharge = false;
                return;
            }
            var data = local.Character;
            _accent = data != null ? data.themeColor : UiTheme.HeroFallbackColor(local.Hero);
            _accent.a = 1f;
            _hasOvercharge = DetectOvercharge(local);
        }

        private static DodgeballPlayer ResolveLocalPlayer()
        {
            var match = MatchManager.Instance;
            var local = match != null ? match.LocalPlayer : null;
            return local != null ? local : PlayerRegistry.LocalPlayer;
        }

        private static bool BlockedByZone(AbilityBase ability, DodgeballPlayer local) =>
            ability != null && !local.IsInfield && ability.Data != null && !ability.Data.usableFromOutfield;

        /// <summary>Rayne (or any hero whose passive is an "overcharge") gets the extra charge-bar segment.</summary>
        private static bool DetectOvercharge(DodgeballPlayer local)
        {
            if (local == null) return false;
            if (local.Hero == HeroId.Rayne) return true;
            var passive = local.Abilities != null ? local.Abilities.Passive : null;
            if (passive == null) return false;
            var data = passive.Data;
            return (data != null && data.abilityId != null && data.abilityId.IndexOf("overcharge", StringComparison.OrdinalIgnoreCase) >= 0) ||
                   passive.DisplayName.IndexOf("overcharge", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool ShouldShowPickupPrompt(DodgeballPlayer local)
        {
            var combat = local.Combat;
            if (combat == null || combat.HasBall || !local.CanAct) return false;
            var manager = BallManager.Instance;
            if (manager == null) return false;
            float radius = combat.Profile != null ? combat.Profile.manualPickupRadius : 1.6f;
            _pickupQueryPlayer = local;
            var ball = manager.FindNearestBall(local.Position, _pickupFilter, radius);
            _pickupQueryPlayer = null;
            return ball != null;
        }

        private bool IsPickupCandidate(DodgeBall ball) =>
            ball != null && ball.IsFree && _pickupQueryPlayer != null && ball.CanBePickedUpBy(_pickupQueryPlayer);

        private void Toast(string text, Color color)
        {
            if (!_built || string.IsNullOrEmpty(text)) return;
            _toast.Set(text);
            _toast.SetColor(color);
            _toastFader.Show(toastTime, 1.1f);
        }

        // ------------------------------------------------------------------ event handlers

        private static bool IsLocal(DodgeballPlayer p) => p != null && p.IsLocalPlayer;

        private TeamId LocalTeam()
        {
            var local = ResolveLocalPlayer();
            return local != null ? local.Team : TeamId.None;
        }

        private void OnMatchStarted(MatchStartedEvent e)
        {
            if (!_built) Initialize();
            _killFeed.Clear();
            _danger.Clear();
            _lastBannerRound = -1;
            _bindPending = true;
            SetVisible(true);
        }

        private void OnPhaseChanged(MatchPhaseChangedEvent e)
        {
            if (!_built) return;
            if (e.Current == MatchPhase.HeroSelect || e.Current == MatchPhase.None)
            {
                SetVisible(false);
            }
            else if (!_visible && (e.Previous == MatchPhase.HeroSelect || e.Previous == MatchPhase.None))
            {
                SetVisible(true);
            }
            if (e.Current != MatchPhase.Playing) _danger.Clear();
        }

        private void OnRoundCountdown(RoundCountdownEvent e)
        {
            if (!_built) return;
            if (e.Round != _lastBannerRound)
            {
                _lastBannerRound = e.Round;
                var match = MatchManager.Instance;
                bool final = false;
                if (match != null && match.Rules != null)
                {
                    int matchPoint = match.Rules.roundsToWin - 1;
                    final = matchPoint > 0 && match.GetScore(TeamId.Home) == matchPoint && match.GetScore(TeamId.Away) == matchPoint;
                }
                _banner.Show(final ? "FINAL ROUND" : UiStrings.Round(e.Round), final ? "WINNER TAKES THE MATCH" : null,
                    final ? UiTheme.Gold : UiTheme.TextPrimary, roundBannerTime);
            }
            _banner.ShowCountdown(e.SecondsLeft);
        }

        private void OnRoundStarted(RoundStartedEvent e)
        {
            if (!_built) return;
            _banner.HideCountdown();
            _banner.Show("FIGHT", null, UiTheme.Gold, fightBannerTime);
        }

        private void OnRoundEnded(RoundEndedEvent e)
        {
            if (!_built) return;
            _danger.Clear();
            var localTeam = LocalTeam();
            string title;
            Color color;
            if (!e.Winner.IsValid() || e.Reason == RoundEndReason.Draw)
            {
                title = "DRAW";
                color = UiTheme.TextSecondary;
            }
            else if (localTeam.IsValid())
            {
                bool won = e.Winner == localTeam;
                title = won ? "ROUND WON" : "ROUND LOST";
                color = won ? UiTheme.TeamColor(localTeam) : UiTheme.Danger;
            }
            else
            {
                title = UiTheme.TeamName(e.Winner) + " TAKES THE ROUND";
                color = UiTheme.TeamColor(e.Winner);
            }
            _banner.Show(title, ReasonText(e.Reason) + "   ·   " + ScoreLine(e.HomeScore, e.AwayScore), color, roundEndBannerTime);
        }

        private void OnMatchEnded(MatchEndedEvent e)
        {
            if (!_built) return;
            _danger.Clear();
            var localTeam = LocalTeam();
            string title;
            Color color;
            if (!e.Winner.IsValid())
            {
                title = "DRAW";
                color = UiTheme.TextSecondary;
            }
            else if (localTeam.IsValid())
            {
                bool won = e.Winner == localTeam;
                title = won ? "VICTORY" : "DEFEAT";
                color = won ? UiTheme.Gold : UiTheme.Danger;
            }
            else
            {
                title = UiTheme.TeamName(e.Winner) + " WINS";
                color = UiTheme.TeamColor(e.Winner);
            }
            _banner.Show(title, "FINAL   ·   " + ScoreLine(e.HomeScore, e.AwayScore), color, matchEndBannerTime);
        }

        private void OnPlayerEliminated(PlayerEliminatedEvent e)
        {
            if (!_built || e.Player == null) return;
            string victim = ColoredName(e.Player);
            string text;
            Color accent;
            if (e.Attacker != null && e.Attacker != e.Player)
            {
                text = ColoredName(e.Attacker) + "   <color=#8E97A3>»</color>   " + victim + CauseSuffix(e.Cause);
                accent = UiTheme.TeamColor(e.Attacker.Team);
            }
            else
            {
                text = victim + "   <color=#8E97A3>ELIMINATED" + CauseSuffix(e.Cause) + "</color>";
                accent = UiTheme.TeamColor(e.Player.Team);
            }
            _killFeed.Push(text, accent);

            if (IsLocal(e.Player))
            {
                _danger.Clear();
                string by = e.Attacker != null && e.Attacker != e.Player ? "BY " + PlainName(e.Attacker) + "   ·   " : string.Empty;
                _banner.Show("ELIMINATED", by + "HEAD TO THE OUTFIELD", UiTheme.Danger, eliminatedBannerTime);
            }
        }

        private void OnPlayerRevived(PlayerRevivedEvent e)
        {
            if (!_built || e.Player == null || e.Cause == RevivalCause.RoundReset) return;
            string cause = RevivalText(e.Cause);
            string text = ColoredName(e.Player) + "   <color=#8E97A3>RETURNS</color>";
            if (!string.IsNullOrEmpty(cause)) text += "   <color=#B9C1CB>" + cause + "</color>";
            _killFeed.Push(text, UiTheme.Positive);

            if (IsLocal(e.Player)) _banner.Show("BACK IN", cause, UiTheme.Positive, 1.4f);
        }

        private void OnPlayerDamaged(PlayerDamagedEvent e)
        {
            if (!_built) return;
            if (IsLocal(e.Player) && e.Damage > 0f) _card.FlashDamage();
        }

        private void OnBallThrown(BallThrownEvent e)
        {
            if (!_built || e.IsPass) return;
            _speed.Show(e.Thrower, e.SpeedKmh, e.RallyCount, e.IsCounterThrow, e.IsAbilityThrow);
        }

        private void OnBallHitPlayer(BallHitPlayerEvent e)
        {
            if (!_built) return;
            bool landed = e.Outcome == HitOutcome.Damaged || e.Outcome == HitOutcome.Eliminated ||
                          e.Outcome == HitOutcome.EliminationDelayed || e.Outcome == HitOutcome.EliminationPrevented;
            if (landed && IsLocal(e.Attacker)) _reticle.ShowHitMarker(e.Outcome == HitOutcome.Eliminated);
            if (landed && IsLocal(e.Victim)) _card.FlashDamage();
        }

        private void OnBallCaught(BallCaughtEvent e)
        {
            if (!_built || e.Catcher == null) return;

            if (e.Quality == CatchQuality.Perfect)
                _killFeed.Push(ColoredName(e.Catcher) + "   <color=#FFCC52>PERFECT CATCH</color>", UiTheme.Gold);

            if (!IsLocal(e.Catcher)) return;
            string timing = e.SecondsBeforeImpact.ToString("0.00", CultureInfo.InvariantCulture) + " s";
            if (e.Quality == CatchQuality.Perfect)
            {
                _reticle.ShowCatchFeedback("PERFECT CATCH!", timing + "   ·   +15% ULTIMATE   ·   COUNTER +20%", UiTheme.Gold,
                    perfectCatchFeedbackTime);
            }
            else if (e.Quality == CatchQuality.Normal)
            {
                _reticle.ShowCatchFeedback("CATCH", timing, UiTheme.TextPrimary, catchFeedbackTime);
            }
        }

        private void OnCatchWhiff(CatchWhiffEvent e)
        {
            if (!_built || !IsLocal(e.Player)) return;
            _reticle.ShowCatchFeedback("WHIFF", "MISTIMED", UiTheme.TextMuted, whiffFeedbackTime);
        }

        private void OnDangerSense(DangerSenseEvent e)
        {
            if (!_built || !IsLocal(e.Player)) return;
            _danger.SetThreat(e.Active, e.TimeToImpact, e.SpeedKmh);
        }

        private void OnUltimateCharge(UltimateChargeChangedEvent e)
        {
            if (!_built || !IsLocal(e.Player) || !e.BecameReady) return;
            _ultimate.Flash(UiTheme.Gold);
            Toast("ULTIMATE READY", UiTheme.Gold);
        }

        private void OnAbilityCast(AbilityCastEvent e)
        {
            if (!_built || !IsLocal(e.Player)) return;
            if (e.Slot == AbilitySlot.Skill) _skill.Flash(Color.white);
            else if (e.Slot == AbilitySlot.Ultimate) _ultimate.Flash(UiTheme.Gold);
        }

        private void OnAbilityFailed(AbilityFailedEvent e)
        {
            if (!_built || !IsLocal(e.Player) || e.Slot == AbilitySlot.Passive) return;
            string reason = FailText(e.Reason);
            if (reason == null) return;
            (e.Slot == AbilitySlot.Ultimate ? _ultimate : _skill).Flash(UiTheme.Danger);
            Toast(reason, UiTheme.Danger);
        }

        // ------------------------------------------------------------------ text helpers (event-time only)

        private static string PlainName(DodgeballPlayer p)
        {
            var data = p.Character;
            if (data != null && !string.IsNullOrEmpty(data.displayName)) return data.displayName.ToUpperInvariant();
            if (!string.IsNullOrEmpty(p.DisplayName)) return p.DisplayName.ToUpperInvariant();
            return p.Hero.ToString().ToUpperInvariant();
        }

        private static string ColoredName(DodgeballPlayer p) =>
            "<color=" + UiTheme.TeamHex(p.Team) + ">" + PlainName(p) + "</color>";

        private static string ScoreLine(int home, int away) =>
            "HOME " + UiStrings.Int(home) + " – " + UiStrings.Int(away) + " AWAY";

        private static string ReasonText(RoundEndReason reason)
        {
            switch (reason)
            {
                case RoundEndReason.AllEliminated: return "ALL ELIMINATED";
                case RoundEndReason.TimeUp: return "TIME UP";
                case RoundEndReason.Draw: return "NO WINNER";
                case RoundEndReason.Forfeit: return "FORFEIT";
                default: return string.Empty;
            }
        }

        private static string CauseSuffix(EliminationCause cause)
        {
            switch (cause)
            {
                case EliminationCause.Frozen: return "  (SHATTERED)";
                case EliminationCause.Tackle: return "  (TACKLED)";
                case EliminationCause.OutOfBounds: return "  (OUT OF BOUNDS)";
                case EliminationCause.DelayedImpact: return "  (DELAYED IMPACT)";
                case EliminationCause.Ability: return "  (ABILITY)";
                case EliminationCause.Forfeit: return "  (FORFEIT)";
                default: return string.Empty;
            }
        }

        private static string RevivalText(RevivalCause cause)
        {
            switch (cause)
            {
                case RevivalCause.PerfectCatch: return "PERFECT CATCH";
                case RevivalCause.OutfieldHit: return "OUTFIELD HIT";
                case RevivalCause.Ability: return "ABILITY";
                case RevivalCause.DelayedImpactCancelled: return "SAVED BY A CATCH";
                case RevivalCause.TimeReversal: return "TIME REVERSAL";
                default: return string.Empty;
            }
        }

        /// <summary>Short reason for a failed activation, or null when the failure should stay silent.</summary>
        private static string FailText(AbilityFailReason reason)
        {
            switch (reason)
            {
                case AbilityFailReason.OnCooldown: return "ON COOLDOWN";
                case AbilityFailReason.UltimateNotCharged: return "ULTIMATE NOT READY";
                case AbilityFailReason.CannotAct: return "CAN'T ACT NOW";
                case AbilityFailReason.Silenced: return "SILENCED";
                case AbilityFailReason.RequiresBall: return "NEEDS A BALL";
                case AbilityFailReason.NoTarget: return "NO TARGET";
                case AbilityFailReason.NotInfield: return "INFIELD ONLY";
                case AbilityFailReason.AlreadyActive: return "ALREADY ACTIVE";
                case AbilityFailReason.Custom: return "UNAVAILABLE";
                default: return null; // None, MatchNotPlaying, IsPassive
            }
        }
    }
}
