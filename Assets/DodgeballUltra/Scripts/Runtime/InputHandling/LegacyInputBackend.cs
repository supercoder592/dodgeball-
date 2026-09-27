#if ENABLE_LEGACY_INPUT_MANAGER
using System;
using UnityEngine;

namespace DodgeballUltra.InputHandling
{
    /// <summary>
    /// <see cref="IInputBackend"/> on top of the legacy Input Manager (<see cref="UnityEngine.Input"/>). Used when the
    /// Input System package is not compiled in.
    /// <para>
    /// Keyboard and mouse use hard-wired <see cref="KeyCode"/>s and the default "Mouse X"/"Mouse Y" axes. Gamepads work
    /// through the Input Manager's standard axes where they exist: the default "Horizontal"/"Vertical" axes carry the
    /// left stick, face/shoulder/menu buttons are read as <c>KeyCode.JoystickButtonN</c> (XInput order on Windows:
    /// 0 A, 1 B, 2 X, 3 Y, 4 LB, 5 RB, 6 Back, 7 Start, 8 L3, 9 R3). The right stick, triggers and D-pad need the optional
    /// axes named by the <c>Axis*</c> constants below (add them in Project Settings ▸ Input Manager). Any axis that is not
    /// defined is detected once at start-up and simply ignored (graceful absence), so the game never throws.
    /// </para>
    /// </summary>
    public sealed class LegacyInputBackend : IInputBackend
    {
        // Optional Input Manager axes for full gamepad support (Windows XInput axis numbers in brackets).
        /// <summary>Right stick X (4th axis).</summary>
        public const string AxisRightStickX = "DU_RightStickX";
        /// <summary>Right stick Y (5th axis, inverted so up = +1).</summary>
        public const string AxisRightStickY = "DU_RightStickY";
        /// <summary>Left trigger 0..1 (9th axis).</summary>
        public const string AxisLeftTrigger = "DU_LeftTrigger";
        /// <summary>Right trigger 0..1 (10th axis).</summary>
        public const string AxisRightTrigger = "DU_RightTrigger";
        /// <summary>D-pad X (6th axis).</summary>
        public const string AxisDPadX = "DU_DPadX";
        /// <summary>D-pad Y (7th axis).</summary>
        public const string AxisDPadY = "DU_DPadY";

        // Default Input Manager axes.
        private const string AxisHorizontal = "Horizontal";
        private const string AxisVertical = "Vertical";
        private const string AxisMouseX = "Mouse X";
        private const string AxisMouseY = "Mouse Y";

        private const KeyCode PadA = KeyCode.JoystickButton0;
        private const KeyCode PadB = KeyCode.JoystickButton1;
        private const KeyCode PadX = KeyCode.JoystickButton2;
        private const KeyCode PadY = KeyCode.JoystickButton3;
        private const KeyCode PadLB = KeyCode.JoystickButton4;
        private const KeyCode PadRB = KeyCode.JoystickButton5;
        private const KeyCode PadStart = KeyCode.JoystickButton7;
        private const KeyCode PadL3 = KeyCode.JoystickButton8;
        private const KeyCode PadR3 = KeyCode.JoystickButton9;

        /// <summary>
        /// "Mouse X/Y" of the default Input Manager are raw pixels × 0.1 (axis sensitivity). Multiplying by this converts
        /// them back to pixels so mouse sensitivity feels identical with both backends.
        /// </summary>
        public float mousePixelsPerAxisUnit = 10f;

        /// <summary>Radial dead zone for sticks read through Input Manager axes.</summary>
        public float stickDeadZone = 0.2f;

        /// <summary>Trigger value above which Throw / Catch count as pressed.</summary>
        public float triggerPressPoint = 0.5f;

        private readonly bool _hasHorizontal;
        private readonly bool _hasVertical;
        private readonly bool _hasMouseX;
        private readonly bool _hasMouseY;
        private readonly bool _hasRightStickX;
        private readonly bool _hasRightStickY;
        private readonly bool _hasLeftTrigger;
        private readonly bool _hasRightTrigger;
        private readonly bool _hasDPadX;
        private readonly bool _hasDPadY;

        // Previous levels for polled analogue "buttons" (triggers, D-pad) so we can synthesise press/release edges.
        private bool _prevLeftTrigger;
        private bool _prevRightTrigger;
        private bool _prevDPadHorizontal;

        public string Name => "Legacy Input Manager";
        public bool IsEnabled { get; private set; }

        public LegacyInputBackend()
        {
            _hasHorizontal = AxisExists(AxisHorizontal);
            _hasVertical = AxisExists(AxisVertical);
            _hasMouseX = AxisExists(AxisMouseX);
            _hasMouseY = AxisExists(AxisMouseY);
            _hasRightStickX = AxisExists(AxisRightStickX);
            _hasRightStickY = AxisExists(AxisRightStickY);
            _hasLeftTrigger = AxisExists(AxisLeftTrigger);
            _hasRightTrigger = AxisExists(AxisRightTrigger);
            _hasDPadX = AxisExists(AxisDPadX);
            _hasDPadY = AxisExists(AxisDPadY);
            IsEnabled = true;
        }

        /// <summary>True when the right stick / triggers are available (optional axes configured).</summary>
        public bool HasFullGamepadSupport => _hasRightStickX && _hasRightStickY && _hasLeftTrigger && _hasRightTrigger;

        public void Enable() => IsEnabled = true;

        public void Disable()
        {
            IsEnabled = false;
            _prevLeftTrigger = _prevRightTrigger = _prevDPadHorizontal = false;
        }

        public void Dispose() => Disable();

        public void Read(ref InputFrame frame)
        {
            if (!IsEnabled)
            {
                frame = default;
                return;
            }

            bool kbm = false;
            bool pad = false;

            // ---------------------------------------------------------------- movement
            Vector2 keys = new Vector2(
                Axis(KeyCode.D, KeyCode.RightArrow) - Axis(KeyCode.A, KeyCode.LeftArrow),
                Axis(KeyCode.W, KeyCode.UpArrow) - Axis(KeyCode.S, KeyCode.DownArrow));
            if (keys.sqrMagnitude > 0f)
            {
                keys = keys.normalized;
                kbm = true;
            }

            // "Horizontal"/"Vertical" also contain the keyboard keys; keyboard input wins when present so the stick
            // reading below only ever reflects the joystick.
            Vector2 stick = Vector2.zero;
            if (keys.sqrMagnitude <= 0f)
            {
                stick = new Vector2(_hasHorizontal ? Input.GetAxisRaw(AxisHorizontal) : 0f,
                    _hasVertical ? Input.GetAxisRaw(AxisVertical) : 0f);
                stick = ApplyRadialDeadZone(stick, stickDeadZone);
                if (stick.sqrMagnitude > 0.1f) pad = true;
            }

            frame.Move = keys.sqrMagnitude > 0f ? keys : stick;

            // ---------------------------------------------------------------- look
            Vector2 mouse = new Vector2(_hasMouseX ? Input.GetAxisRaw(AxisMouseX) : 0f, _hasMouseY ? Input.GetAxisRaw(AxisMouseY) : 0f);
            frame.LookPointer = mouse * mousePixelsPerAxisUnit;
            if (frame.LookPointer.sqrMagnitude > 4f) kbm = true;

            Vector2 rightStick = new Vector2(_hasRightStickX ? Input.GetAxisRaw(AxisRightStickX) : 0f,
                _hasRightStickY ? Input.GetAxisRaw(AxisRightStickY) : 0f);
            frame.LookStick = Vector2.ClampMagnitude(ApplyRadialDeadZone(rightStick, stickDeadZone), 1f);
            if (frame.LookStick.sqrMagnitude > 0.1f) pad = true;

            // ---------------------------------------------------------------- digital actions
            var sprintKeys = Key2(KeyCode.LeftShift, KeyCode.RightShift);
            var sprintPad = Key(PadL3);
            frame.Sprint = ButtonState.Combine(sprintKeys, sprintPad);
            frame.SprintFromGamepad = sprintPad.Held && !sprintKeys.Held;

            frame.Jump = Mixed(Key(KeyCode.Space), Key(PadA), ref kbm, ref pad);
            frame.Slide = Mixed(Key2(KeyCode.C, KeyCode.LeftControl), Key(PadB), ref kbm, ref pad);
            frame.Pass = Mixed(Key(KeyCode.Q), Key(PadY), ref kbm, ref pad);
            frame.Pickup = Mixed(Key(KeyCode.E), Key(PadX), ref kbm, ref pad);
            frame.Skill = Mixed(Key(KeyCode.F), Key(PadRB), ref kbm, ref pad);
            frame.Ultimate = Mixed(Key(KeyCode.R), Key(PadLB), ref kbm, ref pad);
            frame.Pause = Mixed(Key(KeyCode.Escape), Key(PadStart), ref kbm, ref pad);
            if (sprintKeys.Pressed) kbm = true;
            if (sprintPad.Pressed) pad = true;

            // Triggers (optional axes) -> synthesised edges.
            bool rt = _hasRightTrigger && Input.GetAxisRaw(AxisRightTrigger) >= triggerPressPoint;
            bool lt = _hasLeftTrigger && Input.GetAxisRaw(AxisLeftTrigger) >= triggerPressPoint;
            var rtState = ButtonState.FromLevels(_prevRightTrigger, rt);
            var ltState = ButtonState.FromLevels(_prevLeftTrigger, lt);
            _prevRightTrigger = rt;
            _prevLeftTrigger = lt;
            frame.Throw = Mixed(Mouse(0), rtState, ref kbm, ref pad);
            frame.Catch = Mixed(Mouse(1), ltState, ref kbm, ref pad);

            // Cycle target: Tab / middle mouse / R3 / D-pad left-right.
            float dpadX = _hasDPadX ? Input.GetAxisRaw(AxisDPadX) : 0f;
            float dpadY = _hasDPadY ? Input.GetAxisRaw(AxisDPadY) : 0f;
            bool dpadHorizontal = Mathf.Abs(dpadX) > 0.5f;
            var dpadState = ButtonState.FromLevels(_prevDPadHorizontal, dpadHorizontal);
            _prevDPadHorizontal = dpadHorizontal;
            frame.CycleTarget = Mixed(ButtonState.Combine(Key(KeyCode.Tab), Mouse(2)),
                ButtonState.Combine(Key(PadR3), dpadState), ref kbm, ref pad);

            // ---------------------------------------------------------------- menus
            Vector2 nav = new Vector2(Axis(KeyCode.RightArrow, KeyCode.None) - Axis(KeyCode.LeftArrow, KeyCode.None),
                Axis(KeyCode.UpArrow, KeyCode.None) - Axis(KeyCode.DownArrow, KeyCode.None));
            if (nav.sqrMagnitude <= 0f)
            {
                nav = stick.sqrMagnitude > 0f ? stick : new Vector2(dpadX, dpadY);
            }
            frame.UiNavigate = Vector2.ClampMagnitude(nav, 1f);
            frame.UiSubmit = Mixed(Key2(KeyCode.Return, KeyCode.KeypadEnter), Key(PadA), ref kbm, ref pad);
            frame.UiCancel = Mixed(Key(KeyCode.Escape), Key(PadB), ref kbm, ref pad);

            if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)) kbm = true;

            frame.KeyboardMouseActive = kbm;
            frame.GamepadActive = pad;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Probes an Input Manager axis once; UnityEngine.Input throws ArgumentException for undefined axes.</summary>
        private static bool AxisExists(string axis)
        {
            try
            {
                Input.GetAxisRaw(axis);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static float Axis(KeyCode a, KeyCode b) => (a != KeyCode.None && Input.GetKey(a)) || (b != KeyCode.None && Input.GetKey(b)) ? 1f : 0f;

        private static ButtonState Key(KeyCode key) => new ButtonState(Input.GetKeyDown(key), Input.GetKey(key), Input.GetKeyUp(key));

        private static ButtonState Key2(KeyCode a, KeyCode b) => ButtonState.Combine(Key(a), Key(b));

        private static ButtonState Mouse(int button) =>
            new ButtonState(Input.GetMouseButtonDown(button), Input.GetMouseButton(button), Input.GetMouseButtonUp(button));

        /// <summary>Combines a keyboard/mouse source and a gamepad source and records which one produced a press.</summary>
        private static ButtonState Mixed(in ButtonState keyboardMouse, in ButtonState gamepad, ref bool kbm, ref bool pad)
        {
            if (keyboardMouse.Pressed) kbm = true;
            if (gamepad.Pressed) pad = true;
            return ButtonState.Combine(keyboardMouse, gamepad);
        }

        private static Vector2 ApplyRadialDeadZone(Vector2 v, float deadZone)
        {
            float magnitude = v.magnitude;
            if (magnitude <= deadZone) return Vector2.zero;
            // Rescale so the output starts at 0 right outside the dead zone (no jump) and reaches 1 at full deflection.
            float scaled = Mathf.Clamp01((magnitude - deadZone) / Mathf.Max(1e-4f, 1f - deadZone));
            return v / magnitude * scaled;
        }
    }
}
#endif
