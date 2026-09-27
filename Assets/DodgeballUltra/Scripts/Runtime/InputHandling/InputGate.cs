using System;
using System.Collections.Generic;
using UnityEngine;

namespace DodgeballUltra.InputHandling
{
    /// <summary>
    /// Global switch that lets modal UI (pause menu, hero select) take the local player's controls away from gameplay.
    /// Several owners can block at once (reference-counted by owner object). While blocked:
    /// <list type="bullet">
    /// <item><see cref="HumanInputSource"/> returns neutral intents and stops feeding look input to the camera.</item>
    /// <item>The cursor is unlocked and visible.</item>
    /// </list>
    /// The frame on which the gate opens again is remembered (<see cref="LastUnblockFrame"/>) so the button press that
    /// closed a menu (e.g. gamepad A on "Resume") is not also read as a gameplay action (a jump).
    /// </summary>
    public static class InputGate
    {
        private static readonly List<object> s_blockers = new List<object>(4);

        /// <summary>Raised when <see cref="GameplayBlocked"/> changes. Argument = new blocked state.</summary>
        public static event Action<bool> Changed;

        /// <summary>True while any modal UI owns the controls.</summary>
        public static bool GameplayBlocked => s_blockers.Count > 0;

        /// <summary>Time.frameCount of the last blocked -> unblocked transition (-1 = never).</summary>
        public static int LastUnblockFrame { get; private set; } = -1;

        /// <summary>
        /// True on the frame the gate re-opened (and the next one): button edges from that frame belong to the menu that just
        /// closed and must not trigger gameplay actions.
        /// </summary>
        public static bool SuppressEdgesThisFrame => LastUnblockFrame >= 0 && Time.frameCount - LastUnblockFrame <= 1;

        /// <summary>Blocks gameplay input on behalf of <paramref name="owner"/> (idempotent per owner).</summary>
        public static void Block(object owner)
        {
            if (owner == null || s_blockers.Contains(owner)) return;
            bool wasBlocked = GameplayBlocked;
            s_blockers.Add(owner);
            if (!wasBlocked) Notify(true);
        }

        /// <summary>Releases the block held by <paramref name="owner"/>. Safe to call when it holds none.</summary>
        public static void Unblock(object owner)
        {
            if (owner == null || !s_blockers.Remove(owner)) return;
            if (!GameplayBlocked)
            {
                LastUnblockFrame = Time.frameCount;
                Notify(false);
            }
        }

        /// <summary>True when <paramref name="owner"/> currently blocks gameplay input.</summary>
        public static bool IsBlockedBy(object owner) => owner != null && s_blockers.Contains(owner);

        /// <summary>
        /// Applies the OS cursor state. Menus call this with <c>false</c>; gameplay (HumanInputSource) with <c>true</c>.
        /// Never locks while the gate is blocked.
        /// </summary>
        public static void ApplyCursor(bool locked)
        {
            if (locked && GameplayBlocked) locked = false;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private static void Notify(bool blocked)
        {
            if (blocked) ApplyCursor(false);
            var handler = Changed;
            if (handler == null) return;
            try
            {
                handler(blocked);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_blockers.Clear();
            LastUnblockFrame = -1;
            Changed = null;
        }
    }
}
