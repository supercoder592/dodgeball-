#if DU_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DodgeballUltra.InputHandling
{
    /// <summary>
    /// <see cref="IInputBackend"/> on top of the Input System package. All actions are created in code (no
    /// .inputactions asset to lose or mis-configure) inside two action maps:
    /// <code>
    /// Gameplay: Move (WASD / arrows composite + left stick), Look (mouse delta + right stick), Sprint, Jump, Slide,
    ///           Throw (hold), Catch, Pass, Pickup, Skill, Ultimate, CycleTarget, Pause
    /// UI:       Navigate (arrows + left stick + D-pad), Submit, Cancel
    /// </code>
    /// Values are read with <c>ReadValue / IsPressed / WasPressedThisFrame / WasReleasedThisFrame</c> once per frame.
    /// The maps are enabled on construction, disabled with <see cref="Disable"/> and released with <see cref="Dispose"/>.
    /// </summary>
    public sealed class InputSystemBackend : IInputBackend
    {
        /// <summary>Stick/trigger magnitude above which a gamepad counts as "being used" for prompt switching.</summary>
        private const float GamepadActivityThreshold = 0.35f;

        private readonly InputActionMap _gameplay;
        private readonly InputActionMap _ui;

        private readonly InputAction _move;
        private readonly InputAction _look;
        private readonly InputAction _sprint;
        private readonly InputAction _jump;
        private readonly InputAction _slide;
        private readonly InputAction _throw;
        private readonly InputAction _catch;
        private readonly InputAction _pass;
        private readonly InputAction _pickup;
        private readonly InputAction _skill;
        private readonly InputAction _ultimate;
        private readonly InputAction _cycleTarget;
        private readonly InputAction _pause;
        private readonly InputAction _navigate;
        private readonly InputAction _submit;
        private readonly InputAction _cancel;

        /// <summary>Digital gameplay/UI actions scanned for device activity (fixed array, no per-frame allocation).</summary>
        private readonly InputAction[] _buttons;

        private bool _disposed;

        public string Name => "Input System";
        public bool IsEnabled { get; private set; }

        public InputSystemBackend()
        {
            _gameplay = new InputActionMap("DodgeballUltra.Gameplay");
            _ui = new InputActionMap("DodgeballUltra.UI");

            // ---------------------------------------------------------------- locomotion
            _move = _gameplay.AddAction("Move", InputActionType.Value, expectedControlLayout: "Vector2");
            _move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
            _move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/upArrow")
                .With("Down", "<Keyboard>/downArrow")
                .With("Left", "<Keyboard>/leftArrow")
                .With("Right", "<Keyboard>/rightArrow");
            _move.AddBinding("<Gamepad>/leftStick");

            // Mouse delta (pixels) and right stick (rate) share one action; the active control's device tells them apart.
            _look = _gameplay.AddAction("Look", InputActionType.Value, expectedControlLayout: "Vector2");
            _look.AddBinding("<Mouse>/delta");
            _look.AddBinding("<Gamepad>/rightStick");

            _sprint = AddButton(_gameplay, "Sprint", "<Keyboard>/leftShift", "<Keyboard>/rightShift", "<Gamepad>/leftStickPress");
            _jump = AddButton(_gameplay, "Jump", "<Keyboard>/space", "<Gamepad>/buttonSouth");
            _slide = AddButton(_gameplay, "Slide", "<Keyboard>/c", "<Keyboard>/leftCtrl", "<Gamepad>/buttonEast");

            // ---------------------------------------------------------------- combat
            _throw = AddButton(_gameplay, "Throw", "<Mouse>/leftButton", "<Gamepad>/rightTrigger");
            _catch = AddButton(_gameplay, "Catch", "<Mouse>/rightButton", "<Gamepad>/leftTrigger");
            _pass = AddButton(_gameplay, "Pass", "<Keyboard>/q", "<Gamepad>/buttonNorth");
            _pickup = AddButton(_gameplay, "Pickup", "<Keyboard>/e", "<Gamepad>/buttonWest");
            _skill = AddButton(_gameplay, "Skill", "<Keyboard>/f", "<Gamepad>/rightShoulder");
            _ultimate = AddButton(_gameplay, "Ultimate", "<Keyboard>/r", "<Gamepad>/leftShoulder");
            _cycleTarget = AddButton(_gameplay, "CycleTarget", "<Keyboard>/tab", "<Mouse>/middleButton",
                "<Gamepad>/dpad/left", "<Gamepad>/dpad/right", "<Gamepad>/rightStickPress");
            _pause = AddButton(_gameplay, "Pause", "<Keyboard>/escape", "<Gamepad>/start");

            // ---------------------------------------------------------------- menus
            _navigate = _ui.AddAction("Navigate", InputActionType.Value, expectedControlLayout: "Vector2");
            _navigate.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/upArrow")
                .With("Down", "<Keyboard>/downArrow")
                .With("Left", "<Keyboard>/leftArrow")
                .With("Right", "<Keyboard>/rightArrow");
            _navigate.AddBinding("<Gamepad>/leftStick");
            _navigate.AddBinding("<Gamepad>/dpad");
            _submit = AddButton(_ui, "Submit", "<Keyboard>/enter", "<Keyboard>/numpadEnter", "<Gamepad>/buttonSouth");
            _cancel = AddButton(_ui, "Cancel", "<Keyboard>/escape", "<Gamepad>/buttonEast");

            _buttons = new[]
            {
                _sprint, _jump, _slide, _throw, _catch, _pass, _pickup, _skill, _ultimate, _cycleTarget, _pause, _submit, _cancel,
            };

            Enable();
        }

        public void Enable()
        {
            if (_disposed || IsEnabled) return;
            _gameplay.Enable();
            _ui.Enable();
            IsEnabled = true;
        }

        public void Disable()
        {
            if (_disposed || !IsEnabled) return;
            _gameplay.Disable();
            _ui.Disable();
            IsEnabled = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            try
            {
                Disable();
                _gameplay.Dispose();
                _ui.Dispose();
            }
            catch (Exception e)
            {
                // The Input System may already be torn down when the application quits; nothing left to release.
                Debug.LogWarning($"[Dodgeball Ultra] Input System backend dispose: {e.Message}");
            }
            _disposed = true;
            IsEnabled = false;
        }

        public void Read(ref InputFrame frame)
        {
            if (!IsEnabled)
            {
                frame = default;
                return;
            }

            // ---------------------------------------------------------------- analogue values
            frame.Move = Vector2.ClampMagnitude(_move.ReadValue<Vector2>(), 1f);

            Vector2 look = _look.ReadValue<Vector2>();
            bool lookFromPad = IsGamepad(_look);
            frame.LookPointer = lookFromPad ? Vector2.zero : look;
            frame.LookStick = lookFromPad ? Vector2.ClampMagnitude(look, 1f) : Vector2.zero;

            frame.UiNavigate = Vector2.ClampMagnitude(_navigate.ReadValue<Vector2>(), 1f);

            // ---------------------------------------------------------------- digital actions
            frame.Sprint = ReadButton(_sprint);
            frame.SprintFromGamepad = IsGamepad(_sprint);
            frame.Jump = ReadButton(_jump);
            frame.Slide = ReadButton(_slide);
            frame.Throw = ReadButton(_throw);
            frame.Catch = ReadButton(_catch);
            frame.Pass = ReadButton(_pass);
            frame.Pickup = ReadButton(_pickup);
            frame.Skill = ReadButton(_skill);
            frame.Ultimate = ReadButton(_ultimate);
            frame.CycleTarget = ReadButton(_cycleTarget);
            frame.Pause = ReadButton(_pause);
            frame.UiSubmit = ReadButton(_submit);
            frame.UiCancel = ReadButton(_cancel);

            // ---------------------------------------------------------------- device activity (prompt switching)
            bool pad = false;
            bool kbm = false;
            ClassifyAnalogue(_move, frame.Move.sqrMagnitude, ref pad, ref kbm);
            if (lookFromPad)
            {
                if (frame.LookStick.sqrMagnitude > GamepadActivityThreshold * GamepadActivityThreshold) pad = true;
            }
            else if (look.sqrMagnitude > 4f)
            {
                kbm = true; // at least 2 px of deliberate mouse movement
            }

            for (int i = 0; i < _buttons.Length; i++)
            {
                var action = _buttons[i];
                if (!action.WasPressedThisFrame()) continue;
                if (IsGamepad(action)) pad = true;
                else kbm = true;
            }

            frame.GamepadActive = pad;
            frame.KeyboardMouseActive = kbm;
        }

        // ------------------------------------------------------------------ helpers

        private static InputAction AddButton(InputActionMap map, string name, params string[] bindings)
        {
            var action = map.AddAction(name, InputActionType.Button);
            for (int i = 0; i < bindings.Length; i++) action.AddBinding(bindings[i]);
            return action;
        }

        private static ButtonState ReadButton(InputAction action) =>
            new ButtonState(action.WasPressedThisFrame(), action.IsPressed(), action.WasReleasedThisFrame());

        /// <summary>True when the control currently driving <paramref name="action"/> belongs to a gamepad.</summary>
        private static bool IsGamepad(InputAction action)
        {
            var control = action.activeControl;
            return control != null && control.device is Gamepad;
        }

        private static void ClassifyAnalogue(InputAction action, float sqrMagnitude, ref bool pad, ref bool kbm)
        {
            if (sqrMagnitude < GamepadActivityThreshold * GamepadActivityThreshold) return;
            if (IsGamepad(action)) pad = true;
            else kbm = true;
        }
    }
}
#endif
