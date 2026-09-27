using System;
using DodgeballUltra.Match;
using UnityEngine;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// CONTRACT (kernel) - full-screen hero select built in code (uGUI): 10 hero cards with portrait, role, description
    /// and the three abilities; team choice; bot difficulty; "Play". Keyboard/gamepad/mouse navigable.
    /// <para>Owner module: UI.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HeroSelectScreen : MonoBehaviour
    {
        public static HeroSelectScreen Instance { get; private set; }

        // IMPLEMENT: UI module
        /// <summary>Shows the screen (creating it if needed). <paramref name="onConfirmed"/> receives the complete setup (bots filled in).</summary>
        public static void Show(GameConfig config, Action<MatchSetup> onConfirmed) => throw new NotImplementedException();

        public static void Hide() => throw new NotImplementedException();
    }
}
