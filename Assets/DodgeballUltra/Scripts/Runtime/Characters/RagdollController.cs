using System;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Characters
{
    /// <summary>
    /// CONTRACT (kernel) - builds a ragdoll at runtime from the Humanoid avatar bones (hips, spine/chest, head, upper/lower
    /// arms and legs: colliders + rigidbodies + CharacterJoints with anatomical limits, total mass ~ body mass) and handles the
    /// impulse-driven transition on elimination: Animator off, bodies non-kinematic, current animated bone velocities are
    /// preserved, then the ball impulse is applied at the hit point. <see cref="Recover"/> blends back to animation.
    /// Ragdoll colliders use layer <see cref="GameLayers.Ragdoll"/>.
    /// <para>Owner module: Characters.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RagdollController : MonoBehaviour
    {
        public bool IsBuilt { get; private set; }
        public bool IsRagdolled { get; private set; }
        public Transform Hips { get; private set; }

        // IMPLEMENT: Characters module
        public void Initialize(DodgeballPlayer owner, Animator animator) => throw new NotImplementedException();

        /// <summary>Goes limp and applies <paramref name="impulse"/> (N*s) at <paramref name="worldPoint"/>.</summary>
        public void EnableRagdoll(Vector3 impulse, Vector3 worldPoint) => throw new NotImplementedException();

        /// <summary>Returns to animation (smoothly blending the pose over <paramref name="blendTime"/> seconds).</summary>
        public void Recover(float blendTime = 0.35f) => throw new NotImplementedException();

        /// <summary>Instantly returns to animation (teleports / round reset).</summary>
        public void ResetImmediate() => throw new NotImplementedException();
    }
}
