using System;
using UnityEngine;

namespace DodgeballUltra.InputHandling
{
    /// <summary>Which family of physical devices the local human used most recently (drives on-screen key prompts).</summary>
    public enum InputDeviceKind
    {
        KeyboardMouse = 0,
        Gamepad = 1,
    }

    /// <summary>
    /// Logical game actions. Used for button prompts (<see cref="InputPrompts"/>) and documentation; every backend maps the
    /// same set so gameplay code never sees physical keys.
    /// </summary>
    public enum GameAction
    {
        Move = 0,
        Look,
        Sprint,
        Jump,
        Slide,
        Throw,
        Catch,
        Pass,
        Pickup,
        Skill,
        Ultimate,
        CycleTarget,
        Pause,
        UiNavigate,
        UiSubmit,
        UiCancel,
    }

    /// <summary>
    /// Edge + level state of one digital action for the current frame.
    /// <list type="bullet">
    /// <item><see cref="Pressed"/>: went down this frame.</item>
    /// <item><see cref="Held"/>: is down now.</item>
    /// <item><see cref="Released"/>: went up this frame.</item>
    /// </list>
    /// A very quick tap can report Pressed and Released in the same frame with Held = false; consumers that need a
    /// press to be followed by a release on a later frame (throw charging) must handle that (see HumanInputSource).
    /// </summary>
    public struct ButtonState
    {
        public bool Pressed;
        public bool Held;
        public bool Released;

        /// <summary>True when the action produced any signal this frame.</summary>
        public bool Any => Pressed || Held || Released;

        public ButtonState(bool pressed, bool held, bool released)
        {
            Pressed = pressed;
            Held = held;
            Released = released;
        }

        /// <summary>Builds edges from the previous and current level (for polled inputs such as analogue triggers).</summary>
        public static ButtonState FromLevels(bool wasHeld, bool isHeld) => new ButtonState(isHeld && !wasHeld, isHeld, !isHeld && wasHeld);

        /// <summary>Logical OR of two sources bound to the same action (e.g. keyboard key + gamepad button).</summary>
        public static ButtonState Combine(in ButtonState a, in ButtonState b) =>
            new ButtonState(a.Pressed || b.Pressed, a.Held || b.Held, (a.Released || b.Released) && !(a.Held || b.Held));
    }

    /// <summary>
    /// Snapshot of every logical input for one rendered frame, produced by an <see cref="IInputBackend"/> and cached by
    /// <see cref="InputService"/> so every reader (player intent, pause menu, hero select) sees identical values.
    /// Plain data, no allocations.
    /// </summary>
    public struct InputFrame
    {
        /// <summary>Time.frameCount the snapshot belongs to.</summary>
        public int FrameCount;

        /// <summary>Planar movement, x = right, y = forward, magnitude 0..1 (dead zone already applied).</summary>
        public Vector2 Move;

        /// <summary>Pointer (mouse) look delta in screen pixels for this frame (frame-rate independent by nature).</summary>
        public Vector2 LookPointer;

        /// <summary>Gamepad right stick deflection -1..1 (a rate: multiply by speed and unscaled delta time).</summary>
        public Vector2 LookStick;

        public ButtonState Sprint;
        /// <summary>The Sprint action is currently driven by a gamepad (L3 supports click-to-toggle sprint).</summary>
        public bool SprintFromGamepad;
        public ButtonState Jump;
        public ButtonState Slide;
        public ButtonState Throw;
        public ButtonState Catch;
        public ButtonState Pass;
        public ButtonState Pickup;
        public ButtonState Skill;
        public ButtonState Ultimate;
        public ButtonState CycleTarget;
        public ButtonState Pause;

        /// <summary>Menu navigation (arrows / D-pad / left stick), -1..1.</summary>
        public Vector2 UiNavigate;
        public ButtonState UiSubmit;
        public ButtonState UiCancel;

        /// <summary>Keyboard or mouse produced input this frame.</summary>
        public bool KeyboardMouseActive;

        /// <summary>A gamepad produced input this frame.</summary>
        public bool GamepadActive;

        /// <summary>Most recently used device family (resolved by <see cref="InputService"/> across frames).</summary>
        public InputDeviceKind LastDevice;
    }

    /// <summary>
    /// Abstraction over the physical input API. Two production implementations exist:
    /// <see cref="InputSystemBackend"/> (Input System package, actions built in code) and <see cref="LegacyInputBackend"/>
    /// (UnityEngine.Input). <see cref="NullInputBackend"/> is used when neither is compiled in (headless / tests).
    /// </summary>
    public interface IInputBackend : IDisposable
    {
        /// <summary>Human readable backend name (diagnostics).</summary>
        string Name { get; }

        /// <summary>True between <see cref="Enable"/> and <see cref="Disable"/>.</summary>
        bool IsEnabled { get; }

        /// <summary>Starts listening to devices.</summary>
        void Enable();

        /// <summary>Stops listening to devices (actions disabled, state cleared).</summary>
        void Disable();

        /// <summary>
        /// Fills <paramref name="frame"/> with this frame's values. Called at most once per rendered frame by
        /// <see cref="InputService"/>; must not allocate.
        /// </summary>
        void Read(ref InputFrame frame);
    }
}
