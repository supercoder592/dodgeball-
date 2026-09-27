using System;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Characters
{
    /// <summary>
    /// CONTRACT (kernel) - feeds <see cref="AnimatorParams"/> from the player's motor/state/combat every frame and exposes
    /// one-shot triggers. Lives on the model's Animator GameObject.
    /// <para>Owner module: Characters.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerAnimatorDriver : MonoBehaviour
    {
        public Animator Animator { get; private set; }

        // IMPLEMENT: Characters module
        public void Initialize(DodgeballPlayer owner, Animator animator) => throw new NotImplementedException();
        public void TriggerThrow() => throw new NotImplementedException();
        public void TriggerCatch() => throw new NotImplementedException();
        public void TriggerHit() => throw new NotImplementedException();
        public void TriggerJump() => throw new NotImplementedException();
        public void TriggerCheer() => throw new NotImplementedException();
        public void TriggerDefeat() => throw new NotImplementedException();

        /// <summary>Freezes/unfreezes animation playback (Elsa freeze, hitstop is handled by timeScale).</summary>
        public void SetPaused(bool paused) => throw new NotImplementedException();
    }
}
