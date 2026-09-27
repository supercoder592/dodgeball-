using System;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.InputHandling
{
    /// <summary>
    /// CONTRACT (kernel) - local human controls (keyboard + mouse and gamepad) turned into <see cref="PlayerIntent"/>,
    /// camera-relative. Uses the Input System package when available (DU_INPUT_SYSTEM &amp;&amp; ENABLE_INPUT_SYSTEM, actions built in
    /// code - no asset needed) and falls back to the legacy Input Manager otherwise. Feeds look input to the camera rig.
    /// <code>
    /// Move WASD / LS · Look Mouse / RS · Sprint Shift / L3 · Jump Space / A · Slide C,Ctrl / B
    /// Throw (hold to charge) LMB / RT · Catch RMB / LT · Pass Q / Y · Pick up E / X
    /// Skill F / RB · Ultimate R / LB · Cycle target Tab / D-pad · Pause Esc / Start
    /// </code>
    /// <para>Owner module: InputHandling.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HumanInputSource : MonoBehaviour, IIntentSource
    {
        [Tooltip("Mouse look sensitivity (deg per mouse unit).")] public float mouseSensitivity = 0.12f;
        [Tooltip("Gamepad look speed (deg/s at full deflection).")] public Vector2 gamepadLookSpeed = new Vector2(220f, 150f);
        public bool invertY;

        /// <summary>True on the frame Pause was pressed (UI polls this).</summary>
        public bool PausePressedThisFrame { get; private set; }

        // IMPLEMENT: InputHandling module
        public PlayerIntent Sample(DodgeballPlayer player, float deltaTime) => throw new NotImplementedException();

        /// <summary>Locks/unlocks the cursor for gameplay vs menus.</summary>
        public void SetCursorLocked(bool locked) => throw new NotImplementedException();
    }
}
