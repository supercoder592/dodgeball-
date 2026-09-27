using System;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.CameraSystem
{
    /// <summary>
    /// CONTRACT (kernel) - over-the-shoulder third-person camera: orbit (yaw/pitch) from look input, smoothed follow,
    /// sphere-cast collision against court geometry, FOV kicks (sprint, charge), a CameraShaker pivot, and aim helpers.
    /// Hierarchy: Rig (this) -> Pivot (yaw/pitch) -> ShakePivot (CameraShaker) -> Camera.
    /// Runs in LateUpdate with unscaled smoothing so it stays responsive through hitstop.
    /// <para>Owner module: CameraSystem.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ThirdPersonCameraRig : MonoBehaviour
    {
        public static ThirdPersonCameraRig Instance { get; private set; }

        public Camera Camera { get; private set; }
        public Transform ShakePivot { get; private set; }
        public DodgeballPlayer Target { get; private set; }

        /// <summary>Camera forward flattened on the ground plane (movement basis).</summary>
        public Vector3 PlanarForward { get; private set; } = Vector3.forward;
        public Vector3 PlanarRight { get; private set; } = Vector3.right;

        /// <summary>Ray from the screen centre (crosshair).</summary>
        public Ray AimRay { get; private set; }

        /// <summary>World point under the crosshair (court hit or far point).</summary>
        public Vector3 AimPoint { get; private set; }

        // IMPLEMENT: CameraSystem module
        /// <summary>Creates the camera hierarchy if needed (HDRP-friendly camera settings) and registers the shaker.</summary>
        public void Initialize(Camera existingCamera = null) => throw new NotImplementedException();

        public void SetTarget(DodgeballPlayer target, bool snap) => throw new NotImplementedException();

        /// <summary>Look delta in degrees (already sensitivity-scaled by the input source).</summary>
        public void AddLookInput(Vector2 deltaDegrees) => throw new NotImplementedException();

        /// <summary>Temporary FOV offset (deg) that eases back over <paramref name="duration"/>.</summary>
        public void AddFovKick(float degrees, float duration) => throw new NotImplementedException();

        /// <summary>Cinematic framing for round intros / victory (orbit around a point). Null returns to gameplay.</summary>
        public void SetCinematicFocus(Vector3? worldPoint, float distance = 8f) => throw new NotImplementedException();
    }
}
