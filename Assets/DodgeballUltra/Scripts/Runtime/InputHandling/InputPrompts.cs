namespace DodgeballUltra.InputHandling
{
    /// <summary>
    /// Short, allocation-free button labels for on-screen prompts ("F" / "RB"). Mirrors the default bindings of both
    /// backends (see <see cref="HumanInputSource"/> class docs).
    /// </summary>
    public static class InputPrompts
    {
        // Indexed by (int)GameAction.
        private static readonly string[] s_keyboard =
        {
            "WASD",  // Move
            "MOUSE", // Look
            "SHIFT", // Sprint
            "SPACE", // Jump
            "C",     // Slide
            "LMB",   // Throw
            "RMB",   // Catch
            "Q",     // Pass
            "E",     // Pickup
            "F",     // Skill
            "R",     // Ultimate
            "TAB",   // CycleTarget
            "ESC",   // Pause
            "ARROWS",// UiNavigate
            "ENTER", // UiSubmit
            "ESC",   // UiCancel
        };

        private static readonly string[] s_gamepad =
        {
            "LS",    // Move
            "RS",    // Look
            "L3",    // Sprint
            "A",     // Jump
            "B",     // Slide
            "RT",    // Throw
            "LT",    // Catch
            "Y",     // Pass
            "X",     // Pickup
            "RB",    // Skill
            "LB",    // Ultimate
            "D-PAD", // CycleTarget
            "START", // Pause
            "LS",    // UiNavigate
            "A",     // UiSubmit
            "B",     // UiCancel
        };

        /// <summary>Label for <paramref name="action"/> on the given device family.</summary>
        public static string Get(GameAction action, bool gamepad)
        {
            int i = (int)action;
            var table = gamepad ? s_gamepad : s_keyboard;
            return i >= 0 && i < table.Length ? table[i] : string.Empty;
        }

        /// <summary>Label for <paramref name="action"/> on the most recently used device.</summary>
        public static string Get(GameAction action) => Get(action, InputService.UsingGamepad);
    }
}
