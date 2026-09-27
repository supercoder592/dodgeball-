using DodgeballUltra.InputHandling;
using DodgeballUltra.Juice;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;
using UnityEngine.UI;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// In-match pause menu (Esc / Start): RESUME, HERO SELECT (GameBootstrap.ReturnToHeroSelect) and QUIT, plus a controls
    /// reference card for keyboard/mouse and gamepad. While open it
    /// <list type="bullet">
    /// <item>holds <see cref="Time.timeScale"/> at 0 (re-applied every frame so an ending hitstop cannot resume the game),</item>
    /// <item>blocks gameplay input through <see cref="InputGate"/> and unlocks the cursor,</item>
    /// <item>restores the previous time scale on close (1 if a hitstop that was running when pausing has since ended).</item>
    /// </list>
    /// Created automatically by <see cref="HudController.Initialize"/>; runs on unscaled time.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PauseMenu : MonoBehaviour
    {
        public static PauseMenu Instance { get; private set; }

        /// <summary>True while the pause menu is open.</summary>
        public static bool IsPaused => Instance != null && Instance._open;

        [Tooltip("Sorting order of the pause canvas (above the HUD, below hero select).")]
        public int sortingOrder = 300;

        [Tooltip("Only allow pausing while a match exists (not on hero select / empty scenes).")]
        public bool requireMatch = true;

        private bool _built;
        private bool _open;
        private float _storedTimeScale = 1f;
        private bool _storedDuringHitstop;
        private Canvas _canvas;
        private Button _resume;
        private Button _heroSelect;
        private Button _quit;
        private Text _controls;
        private InputDeviceKind _controlsDevice = (InputDeviceKind)(-1);
        private float _openTime;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                enabled = false;
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (_open) Close();
            if (Instance == this) Instance = null;
        }

        private void OnDisable()
        {
            if (_open) Close();
        }

        private void Update()
        {
            var frame = InputService.Current;
            if (_open)
            {
                // Ignore the very frame we opened on (the same Esc press would close it again).
                if (Time.unscaledTime - _openTime > 0.05f && (frame.Pause.Pressed || frame.UiCancel.Pressed))
                {
                    Close();
                    return;
                }
                KeepFocus(frame);
                if (_controlsDevice != InputService.LastDevice) RefreshControls();
            }
            else if (frame.Pause.Pressed && CanPause())
            {
                Open();
            }
        }

        private void LateUpdate()
        {
            // Other systems (hitstop restore, round flow) may write timeScale during the frame: pause wins.
            if (_open && Time.timeScale != 0f) Time.timeScale = 0f;
        }

        // ------------------------------------------------------------------ public API

        /// <summary>Opens the pause menu (no-op if already open).</summary>
        public void Open()
        {
            if (_open) return;
            Build();
            _open = true;
            _openTime = Time.unscaledTime;

            var juice = JuiceManager.Instance;
            _storedDuringHitstop = juice != null && juice.IsHitstopActive;
            _storedTimeScale = Time.timeScale;
            Time.timeScale = 0f;

            InputGate.Block(this);
            UiFactory.EnsureEventSystem();
            _canvas.gameObject.SetActive(true);
            RefreshControls();
            UiFactory.Focus(_resume.gameObject);
        }

        /// <summary>Closes the menu and resumes the game.</summary>
        public void Close()
        {
            if (!_open) return;
            _open = false;

            // A hitstop that was running when we paused has almost certainly ended meanwhile; never resume in slow motion.
            var juice = JuiceManager.Instance;
            bool hitstopStillRunning = juice != null && juice.IsHitstopActive;
            float restore = _storedTimeScale;
            if (_storedDuringHitstop && !hitstopStillRunning) restore = 1f;
            if (restore <= 0f) restore = 1f;
            Time.timeScale = restore;

            if (_canvas != null) _canvas.gameObject.SetActive(false);
            InputGate.Unblock(this);
        }

        public void Toggle()
        {
            if (_open) Close();
            else Open();
        }

        // ------------------------------------------------------------------ internals

        private bool CanPause()
        {
            if (HeroSelectScreen.IsVisible) return false;
            var match = MatchManager.Instance;
            if (match == null) return !requireMatch && PlayerRegistry.LocalPlayer != null;
            switch (match.Phase)
            {
                case MatchPhase.PreRound:
                case MatchPhase.Countdown:
                case MatchPhase.Playing:
                case MatchPhase.RoundEnd:
                case MatchPhase.MatchEnd:
                    return true;
                default:
                    return false;
            }
        }

        private void KeepFocus(in InputFrame frame)
        {
            // Mouse clicks on empty space clear the selection; restore it as soon as keys / pad are used.
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (eventSystem == null) return;
            if (eventSystem.currentSelectedGameObject != null && eventSystem.currentSelectedGameObject.activeInHierarchy) return;
            if (frame.UiNavigate.sqrMagnitude > 0.25f || frame.UiSubmit.Pressed) UiFactory.Focus(_resume.gameObject);
        }

        private void OnResume() => Close();

        private void OnHeroSelect()
        {
            Close();
            var bootstrap = GameBootstrap.Instance;
            if (bootstrap != null)
            {
                bootstrap.ReturnToHeroSelect();
            }
            else
            {
                Debug.LogWarning("[Dodgeball Ultra] No GameBootstrap in the scene; cannot return to hero select.");
            }
        }

        private void OnQuit()
        {
            Close();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void Build()
        {
            if (_built) return;
            _built = true;

            _canvas = UiFactory.CreateScreenCanvas("DU_PauseMenu", sortingOrder, transform, true);
            var root = (RectTransform)_canvas.transform;

            var dim = UiFactory.CreateImage(root, "Dim", UiFactory.WhiteSprite, UiTheme.ScreenDim);
            dim.rectTransform.Stretch();
            dim.raycastTarget = true; // swallow clicks meant for the (paused) game

            // ---------------------------------------------------------------- menu column
            var panel = UiFactory.CreatePanel(root, "Panel", UiTheme.PanelStrong);
            panel.rectTransform.Place(UiAnchor.Center, UiAnchor.Center, new Vector2(-250f, 0f), new Vector2(460f, 470f));

            var accent = UiFactory.CreateImage(panel.transform, "Accent", UiFactory.WhiteSprite, UiTheme.Gold);
            accent.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(40f, -40f), new Vector2(48f, 4f));
            var title = UiFactory.CreateText(panel.transform, "Title", "PAUSED", UiTheme.FontHeadline, UiTheme.TextPrimary, TextAnchor.UpperLeft);
            title.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(38f, -54f), new Vector2(380f, 64f));
            var subtitle = UiFactory.CreateText(panel.transform, "Subtitle", "MATCH ON HOLD", 16, UiTheme.TextMuted, TextAnchor.UpperLeft);
            subtitle.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(40f, -120f), new Vector2(380f, 22f));

            _resume = MenuButton(panel.transform, "RESUME", 0, OnResume);
            _heroSelect = MenuButton(panel.transform, "HERO SELECT", 1, OnHeroSelect);
            _quit = MenuButton(panel.transform, "QUIT GAME", 2, OnQuit);
            UiFactory.SetNavigation(_resume, _quit, _heroSelect, null, null);
            UiFactory.SetNavigation(_heroSelect, _resume, _quit, null, null);
            UiFactory.SetNavigation(_quit, _heroSelect, _resume, null, null);

            // ---------------------------------------------------------------- controls reference
            var controlsPanel = UiFactory.CreatePanel(root, "Controls", UiTheme.Panel);
            controlsPanel.rectTransform.Place(UiAnchor.Center, UiAnchor.Center, new Vector2(250f, 0f), new Vector2(500f, 470f));
            var controlsTitle = UiFactory.CreateText(controlsPanel.transform, "Title", "CONTROLS", 22, UiTheme.TextSecondary, TextAnchor.UpperLeft);
            controlsTitle.rectTransform.Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(36f, -36f), new Vector2(420f, 30f));
            _controls = UiFactory.CreateParagraph(controlsPanel.transform, "List", string.Empty, 19, UiTheme.TextPrimary);
            _controls.rectTransform.Stretch(36f, 36f, 84f, 24f);
            _controls.supportRichText = true;
            _controls.lineSpacing = 1.25f;

            _canvas.gameObject.SetActive(false);
        }

        private static Button MenuButton(Transform parent, string label, int index, UnityEngine.Events.UnityAction onClick)
        {
            var button = UiFactory.CreateButton(parent, label, label, 24, onClick, out var text);
            button.GetComponent<RectTransform>().Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(40f, -172f - index * 84f), new Vector2(380f, 68f));
            text.alignment = TextAnchor.MiddleLeft;
            text.rectTransform.Stretch(26f, 16f, 0f, 0f);
            return button;
        }

        private void RefreshControls()
        {
            if (_controls == null) return;
            _controlsDevice = InputService.LastDevice;
            bool pad = _controlsDevice == InputDeviceKind.Gamepad;
            _controls.text =
                Row("Move", GameAction.Move, pad) +
                Row("Look / aim", GameAction.Look, pad) +
                Row("Sprint", GameAction.Sprint, pad) +
                Row("Jump", GameAction.Jump, pad) +
                Row("Slide", GameAction.Slide, pad) +
                Row("Throw (hold to charge)", GameAction.Throw, pad) +
                Row("Catch", GameAction.Catch, pad) +
                Row("Pass", GameAction.Pass, pad) +
                Row("Pick up", GameAction.Pickup, pad) +
                Row("Skill", GameAction.Skill, pad) +
                Row("Ultimate", GameAction.Ultimate, pad) +
                Row("Cycle target", GameAction.CycleTarget, pad);
        }

        private static string Row(string label, GameAction action, bool pad) =>
            "<color=#F6F7F9><b>" + InputPrompts.Get(action, pad) + "</b></color>   <color=#B8BFC8>" + label + "</color>\n";
    }
}
