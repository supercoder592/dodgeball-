using System;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Characters
{
    /// <summary>
    /// CONTRACT (kernel) - leans the character mesh into turns: roll = f(yaw rate x speed), pitch = f(planar acceleration),
    /// critically-damped smoothing, clamped angles. Rotates only the LeanPivot (physics capsule stays upright).
    /// <para>Owner module: Characters.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ProceduralLean : MonoBehaviour
    {
        [Tooltip("Degrees of roll per (deg/s of yaw rate * m/s of speed).")] public float rollPerYawSpeed = 0.0045f;
        [Tooltip("Maximum roll into a turn (deg).")] public float maxRoll = 16f;
        [Tooltip("Degrees of pitch per m/s^2 of forward acceleration.")] public float pitchPerAcceleration = 0.55f;
        [Tooltip("Maximum pitch (deg).")] public float maxPitch = 10f;
        [Tooltip("Smoothing time (s).")] public float smoothTime = 0.12f;

        public float CurrentRoll { get; private set; }
        public float CurrentPitch { get; private set; }

        // IMPLEMENT: Characters module
        public void Initialize(DodgeballPlayer owner, Transform leanPivot) => throw new NotImplementedException();
    }
}
