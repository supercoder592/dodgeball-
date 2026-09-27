using DodgeballUltra.Characters;
using DodgeballUltra.Events;
using DodgeballUltra.Juice;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using DodgeballUltra.Rendering;
using UnityEngine;

namespace DodgeballUltra.CameraSystem
{
    /// <summary>What the rig is currently framing.</summary>
    public enum CameraRigMode
    {
        /// <summary>Over-the-shoulder follow of <see cref="ThirdPersonCameraRig.Target"/>.</summary>
        Follow = 0,
        /// <summary>No target: slow orbit around the court centre.</summary>
        Spectate,
        /// <summary>Scripted orbit around a point (round intros, victory).</summary>
        Cinematic,
    }

    /// <summary>
    /// CONTRACT (kernel) - over-the-shoulder third-person camera: orbit (yaw/pitch) from look input, smoothed follow,
    /// sphere-cast collision against court geometry, FOV kicks (sprint, charge), a CameraShaker pivot, and aim helpers.
    /// Hierarchy: Rig (this) -> Pivot (yaw/pitch) -> ShakePivot (CameraShaker) -> Camera.
    /// Runs in LateUpdate with unscaled smoothing so it stays responsive through hitstop.
    /// <para>
    /// Concrete hierarchy built by <see cref="Initialize"/>:
    /// <code>
    /// Rig (this)             world position = smoothed follow anchor (target feet + pivotHeight), world rotation identity
    ///   └─ YawPivot          rotation = yaw
    ///        └─ PitchPivot   rotation = pitch (clamped minPitch..maxPitch, positive looks down)
    ///             └─ Boom    local position = (shoulder, 0, -distance) after collision; the unshaken camera pose
    ///                  └─ ShakePivot (CameraShaker: Perlin offsets around the camera's own position)
    ///                       └─ Camera (+ AudioListener)
    /// </code>
    /// The extra Boom node keeps the shake centred on the camera (a head shake) instead of swinging the whole boom
    /// around the player, and gives the aim helpers an unshaken pose, so the crosshair ray never jitters.
    /// </para>
    /// <para>
    /// Modes: <see cref="CameraRigMode.Follow"/> (target set), <see cref="CameraRigMode.Spectate"/> (no target: orbit of
    /// the court centre), <see cref="CameraRigMode.Cinematic"/> (<see cref="SetCinematicFocus"/>). Transitions blend with
    /// the same smoothing. While the target is ragdolled the rig follows the ragdoll's hips.
    /// </para>
    /// <para>Input convention for <see cref="AddLookInput"/>: x = yaw to the right, y = look up (degrees).</para>
    /// <para>Owner module: CameraSystem.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(200)]
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

        // ------------------------------------------------------------------ additional read-only state

        /// <summary>The shaker on <see cref="ShakePivot"/>.</summary>
        public CameraShaker Shaker { get; private set; }

        /// <summary>What the rig is currently framing.</summary>
        public CameraRigMode Mode { get; private set; } = CameraRigMode.Spectate;

        /// <summary>True once the hierarchy and camera exist.</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>Current orbit yaw (deg, world).</summary>
        public float Yaw => _yaw;

        /// <summary>Current orbit pitch (deg, positive looks down).</summary>
        public float Pitch => _pitch;

        // ------------------------------------------------------------------ tuning

        [Header("Framing (over the shoulder)")]
        [Tooltip("Lateral offset of the view to the right of the target (m). Negative = left shoulder.")]
        [Range(-1.5f, 1.5f)] public float shoulderOffset = 0.45f;
        [Tooltip("Height of the orbit pivot above the target's feet (m).")]
        [Min(0f)] public float pivotHeight = 1.55f;
        [Tooltip("Boom length behind the pivot (m).")]
        [Min(0.5f)] public float distance = 4.2f;
        [Tooltip("Lowest pitch (deg): negative looks up.")]
        [Range(-89f, 0f)] public float minPitch = -35f;
        [Tooltip("Highest pitch (deg): positive looks down.")]
        [Range(0f, 89f)] public float maxPitch = 60f;
        [Tooltip("Pitch used when snapping behind the target (deg).")]
        [Range(-30f, 45f)] public float defaultPitch = 10f;
        [Tooltip("Pivot drop while sliding (m) so the view follows the low body.")]
        [Min(0f)] public float slidePivotDrop = 0.35f;

        [Header("Follow smoothing (unscaled time)")]
        [Tooltip("Planar follow smooth time (s). Small = tight, responsive follow.")]
        [Range(0f, 0.5f)] public float followSmoothTime = 0.06f;
        [Tooltip("Vertical follow smooth time (s). Slower so jumps and landings do not jolt the view.")]
        [Range(0f, 0.5f)] public float verticalSmoothTime = 0.14f;
        [Tooltip("Sharpness (1/s) of framing changes: boom length, shoulder, aim zoom.")]
        [Min(0.1f)] public float framingSharpness = 9f;
        [Tooltip("If the follow point jumps farther than this in one frame (teleport, respawn), the rig snaps (m).")]
        [Min(0.5f)] public float teleportSnapDistance = 3.5f;
        [Tooltip("Time (s) to swing behind the target after it changes zone (0 = snap).")]
        [Range(0f, 2f)] public float recenterTime = 0.35f;

        [Header("Ragdoll follow")]
        [Tooltip("Height of the orbit pivot above the ragdoll hips while the target is ragdolled (m).")]
        [Min(0f)] public float ragdollPivotLift = 0.6f;
        [Tooltip("Boom length multiplier while the target is ragdolled (frames the whole fall).")]
        [Range(0.5f, 2f)] public float ragdollDistanceMultiplier = 1.15f;

        [Header("Collision")]
        [Tooltip("Radius of the camera collision probe (m).")]
        [Min(0.01f)] public float collisionRadius = 0.2f;
        [Tooltip("Layers the camera boom collides with (court floor, walls, bleachers).")]
        public LayerMask collisionMask = 1 << GameLayers.Court;
        [Tooltip("Speed (m/s) at which the boom extends again after an obstruction clears. Obstructions pull in instantly.")]
        [Min(0.1f)] public float collisionRecoverSpeed = 6f;
        [Tooltip("Shortest boom (m) the collision may produce.")]
        [Min(0.02f)] public float minBoomLength = 0.1f;

        [Header("Field of view")]
        [Tooltip("Vertical field of view at rest (deg).")]
        [Range(30f, 100f)] public float baseFov = 60f;
        [Tooltip("Added while sprinting at full speed (deg).")]
        [Range(0f, 20f)] public float sprintFovBonus = 7f;
        [Tooltip("Added while sliding (deg).")]
        [Range(0f, 20f)] public float slideFovBonus = 4f;
        [Tooltip("Added while charging a throw (deg, negative = aim zoom).")]
        [Range(-20f, 0f)] public float chargeFovOffset = -6f;
        [Tooltip("Boom length multiplier while charging (aim zoom brings the camera closer).")]
        [Range(0.4f, 1f)] public float chargeDistanceMultiplier = 0.78f;
        [Tooltip("Shoulder offset multiplier while charging (clears the throwing arm from the crosshair).")]
        [Range(1f, 2f)] public float chargeShoulderMultiplier = 1.15f;
        [Tooltip("FOV smoothing sharpness (1/s).")]
        [Min(0.1f)] public float fovSharpness = 7f;
        [Tooltip("Largest total FOV kick (deg).")]
        [Range(0f, 30f)] public float maxTotalFovKick = 14f;

        [Header("Aim")]
        [Tooltip("Aim ray length (m). The aim point is this far away when the ray hits nothing.")]
        [Min(1f)] public float aimMaxDistance = 60f;
        [Tooltip("Layers the aim ray can hit.")]
        public LayerMask aimMask = (1 << GameLayers.Court) | (1 << GameLayers.Player) | (1 << GameLayers.Hittable);

        [Header("Cinematic (intros / victory)")]
        [Tooltip("Orbit speed around the focus point (deg/s).")]
        public float cinematicOrbitSpeed = 9f;
        [Tooltip("Pitch of the cinematic orbit (deg).")]
        [Range(-20f, 80f)] public float cinematicPitch = 16f;
        [Tooltip("Smooth time (s) of the move to / from the focus point.")]
        [Range(0.05f, 3f)] public float cinematicMoveSmoothTime = 0.8f;
        [Tooltip("Sharpness (1/s) of the boom length blend into / out of cinematic framing.")]
        [Min(0.1f)] public float cinematicBlendSharpness = 2.5f;
        [Tooltip("Seconds the follow uses the slower cinematic smoothing after leaving a cinematic / spectator view " +
                 "without a snap (a glide back instead of a cut).")]
        [Range(0f, 3f)] public float returnToFollowBlendTime = 1f;

        [Header("Spectate (no target)")]
        [Tooltip("Boom length of the spectator orbit (m).")]
        [Min(1f)] public float spectateDistance = 14f;
        [Tooltip("Pitch of the spectator orbit (deg).")]
        [Range(0f, 80f)] public float spectatePitch = 30f;
        [Tooltip("Automatic orbit speed (deg/s).")]
        public float spectateOrbitSpeed = 6f;
        [Tooltip("Height of the orbit centre above the court (m).")]
        public float spectatePivotHeight = 1f;

        [Header("Camera")]
        [Min(0.01f)] public float nearClip = 0.05f;
        [Min(1f)] public float farClip = 400f;
        [Tooltip("Automatically follow the local player when it spawns and nothing is followed yet.")]
        public bool autoTargetLocalPlayer = true;

        // ------------------------------------------------------------------ state

        private Transform _yawPivot, _pitchPivot, _boom, _shakePivot;

        private float _yaw;
        private float _pitch = 10f;
        private float _pitchVelocity;

        private Vector3 _anchor;
        private Vector3 _planarVelocity;
        private float _verticalVelocity;
        private Vector3 _lastDesiredAnchor;
        private bool _hasAnchor;

        private Vector3 _boomLocal = new Vector3(0.45f, 0f, -4.2f); // smoothed framing (before collision)
        private float _boomLength = 4.2f;                            // after collision

        private float _fov = 60f;

        private Vector3? _cinematicPoint;
        private float _cinematicDistance = 8f;
        private float _returnBlendRemaining; // > 0 while gliding from a cinematic / spectator view back to the follow

        private bool _recentering;
        private bool _recenterPending;
        private float _recenterYaw;
        private float _recenterVelocity;

        private struct FovKick
        {
            public float Degrees;
            public float Duration;
            public float Elapsed;
        }

        private const int MaxFovKicks = 6;
        private readonly FovKick[] _fovKicks = new FovKick[MaxFovKicks];
        private int _fovKickCount;

        private readonly RaycastHit[] _aimHits = new RaycastHit[16];

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[Camera] A ThirdPersonCameraRig already exists on '{Instance.name}'. Removing the duplicate on '{name}'.", this);
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void OnEnable()
        {
            if (Instance == null) Instance = this;
            if (Instance != this) return; // duplicate waiting for destruction
            GameEvents.Subscribe<PlayerSpawnedEvent>(OnPlayerSpawned);
            GameEvents.Subscribe<PlayerZoneChangedEvent>(OnPlayerZoneChanged);
        }

        private void OnDisable()
        {
            GameEvents.Unsubscribe<PlayerSpawnedEvent>(OnPlayerSpawned);
            GameEvents.Unsubscribe<PlayerZoneChangedEvent>(OnPlayerZoneChanged);
        }

        private void Start()
        {
            // Robustness: a rig dropped into a scene works without an explicit Initialize call.
            if (!IsInitialized && Instance == this) Initialize();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void LateUpdate()
        {
            if (!IsInitialized || Camera == null) return;
            Step(Mathf.Min(Time.unscaledDeltaTime, 0.1f));
        }

        // ================================================================== contract API

        /// <summary>Creates the camera hierarchy if needed (HDRP-friendly camera settings) and registers the shaker.</summary>
        /// <remarks>
        /// Camera choice: <paramref name="existingCamera"/>, else <c>Camera.main</c>, else a new camera (tag MainCamera,
        /// FOV 60, near 0.05 m, far 400 m, AudioListener). The camera is re-parented under the shake pivot and handed to
        /// <see cref="RuntimeRenderingHooks.Active"/> for pipeline configuration. Safe to call more than once.
        /// </remarks>
        public void Initialize(Camera existingCamera = null)
        {
            BuildHierarchy();

            var cam = existingCamera;
            if (cam == null) cam = Camera;
            if (cam == null) cam = UnityEngine.Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera");
                cam = go.AddComponent<Camera>();
            }

            AttachCamera(cam);

            if (!IsInitialized)
            {
                IsInitialized = true;
                _fov = baseFov;
                _pitch = defaultPitch;
                _boomLocal = new Vector3(shoulderOffset, 0f, -distance);
                _boomLength = _boomLocal.magnitude;
            }

            // Pick up a local player spawned before the rig existed.
            if (Target == null && autoTargetLocalPlayer)
            {
                var local = PlayerRegistry.LocalPlayer;
                if (local != null) SetTarget(local, true);
            }

            Step(0f); // place the camera immediately (no one-frame pop)
        }

        public void SetTarget(DodgeballPlayer target, bool snap)
        {
            Target = target;
            _recentering = false;
            _recenterPending = false;
            if (target == null || !snap) return;

            // Snap: behind the target, default pitch, framing and follow point settled instantly.
            _yaw = YawOf(DesiredFacing(target));
            _pitch = Mathf.Clamp(defaultPitch, minPitch, maxPitch);
            _pitchVelocity = 0f;
            _anchor = DesiredFollowPoint(target, out _);
            _lastDesiredAnchor = _anchor;
            _planarVelocity = Vector3.zero;
            _verticalVelocity = 0f;
            _hasAnchor = true;
            _boomLocal = DesiredFollowBoom(target);
            _boomLength = _boomLocal.magnitude;
            _returnBlendRemaining = 0f;
            Mode = CameraRigMode.Follow;
            if (IsInitialized && Camera != null) Step(0f);
        }

        /// <summary>Look delta in degrees (already sensitivity-scaled by the input source).</summary>
        /// <remarks>x = yaw to the right, y = look up. Ignored during cinematics.</remarks>
        public void AddLookInput(Vector2 deltaDegrees)
        {
            if (_cinematicPoint.HasValue) return;
            if (float.IsNaN(deltaDegrees.x) || float.IsNaN(deltaDegrees.y)) return;

            _yaw = Mathf.Repeat(_yaw + deltaDegrees.x, 360f);
            if (Target != null) _pitch = Mathf.Clamp(_pitch - deltaDegrees.y, minPitch, maxPitch);

            // Manual look always wins over an automatic re-centre.
            if (deltaDegrees.sqrMagnitude > 0.25f)
            {
                _recentering = false;
                _recenterPending = false;
            }
        }

        /// <summary>Temporary FOV offset (deg) that eases back over <paramref name="duration"/>.</summary>
        /// <remarks>Applied instantly at full strength, eased back with a quadratic ease-out in unscaled time.
        /// Kicks are additive (clamped to <see cref="maxTotalFovKick"/>).</remarks>
        public void AddFovKick(float degrees, float duration)
        {
            if (Mathf.Abs(degrees) < 1e-3f || duration <= 0f || float.IsNaN(degrees)) return;
            var kick = new FovKick { Degrees = degrees, Duration = duration, Elapsed = 0f };
            if (_fovKickCount < MaxFovKicks)
            {
                _fovKicks[_fovKickCount++] = kick;
                return;
            }

            // Full: replace the kick closest to finishing.
            int oldest = 0;
            float oldestProgress = -1f;
            for (int i = 0; i < _fovKickCount; i++)
            {
                float progress = _fovKicks[i].Elapsed / _fovKicks[i].Duration;
                if (progress > oldestProgress)
                {
                    oldestProgress = progress;
                    oldest = i;
                }
            }
            _fovKicks[oldest] = kick;
        }

        /// <summary>Cinematic framing for round intros / victory (orbit around a point). Null returns to gameplay.</summary>
        public void SetCinematicFocus(Vector3? worldPoint, float distance = 8f)
        {
            _cinematicPoint = worldPoint;
            if (worldPoint.HasValue) _cinematicDistance = Mathf.Max(1f, distance);
            _recentering = false;
        }

        // ================================================================== extra API

        /// <summary>Swings (or snaps) the view behind the target's attack direction.</summary>
        public void RecenterBehindTarget(bool instant)
        {
            if (Target == null) return;
            float yaw = YawOf(DesiredFacing(Target));
            if (instant || recenterTime <= 0f)
            {
                _yaw = yaw;
                _recentering = false;
                return;
            }
            _recenterYaw = yaw;
            _recenterVelocity = 0f;
            _recentering = true;
        }

        // ================================================================== frame

        private void Step(float dt)
        {
            transform.rotation = Quaternion.identity; // the orbit is expressed in world space

            var target = Target; // Unity-null aware below
            if (_cinematicPoint.HasValue) StepCinematic(dt);
            else if (target != null) StepFollow(target, dt);
            else StepSpectate(dt);

            transform.position = _anchor;
            _yawPivot.localRotation = Quaternion.Euler(0f, _yaw, 0f);
            _pitchPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);

            ApplyCollision(dt);
            StepFov(target, dt);
            UpdateBasisAndAim(target);
        }

        private void StepFollow(DodgeballPlayer target, float dt)
        {
            // Coming back from a cinematic / spectator view without a snap: glide instead of cutting.
            if (Mode != CameraRigMode.Follow && _hasAnchor) _returnBlendRemaining = returnToFollowBlendTime;
            Mode = CameraRigMode.Follow;

            // ---- follow point (feet + pivot height, or the ragdoll hips)
            Vector3 desired = DesiredFollowPoint(target, out bool ragdolled);
            if (_returnBlendRemaining > 0f)
            {
                _returnBlendRemaining -= dt;
                float blend = Mathf.Clamp01(_returnBlendRemaining / Mathf.Max(0.01f, returnToFollowBlendTime));
                // Smooth time eases from the cinematic value to the gameplay value as the glide completes.
                SmoothAnchor(desired, Mathf.Lerp(followSmoothTime, cinematicMoveSmoothTime * 0.5f, blend),
                    Mathf.Lerp(verticalSmoothTime, cinematicMoveSmoothTime * 0.5f, blend), dt);
            }
            else if (!_hasAnchor || (desired - _lastDesiredAnchor).sqrMagnitude > teleportSnapDistance * teleportSnapDistance)
            {
                // Teleport / respawn / first frame: no camera fly-through across the court.
                _anchor = desired;
                _planarVelocity = Vector3.zero;
                _verticalVelocity = 0f;
                _hasAnchor = true;
            }
            else
            {
                SmoothAnchor(desired, followSmoothTime, verticalSmoothTime, dt);
            }
            _lastDesiredAnchor = desired;

            // ---- automatic re-centre (zone change)
            if (_recenterPending)
            {
                _recenterPending = false;
                RecenterBehindTarget(instant: false);
            }
            if (_recentering)
            {
                _yaw = Mathf.SmoothDampAngle(_yaw, _recenterYaw, ref _recenterVelocity, recenterTime, Mathf.Infinity, dt);
                if (Mathf.Abs(Mathf.DeltaAngle(_yaw, _recenterYaw)) < 0.25f) _recentering = false;
            }

            // ---- pitch back inside the gameplay range after a cinematic / spectate
            if (_pitch < minPitch || _pitch > maxPitch)
                _pitch = Mathf.SmoothDampAngle(_pitch, Mathf.Clamp(_pitch, minPitch, maxPitch), ref _pitchVelocity, 0.25f, Mathf.Infinity, dt);

            // ---- framing (aim zoom / ragdoll)
            Vector3 boom = DesiredFollowBoom(target);
            if (ragdolled) boom = new Vector3(0f, 0f, boom.z * ragdollDistanceMultiplier);
            BlendBoom(boom, _returnBlendRemaining > 0f ? cinematicBlendSharpness * 1.5f : framingSharpness, dt);
        }

        private void StepCinematic(float dt)
        {
            Mode = CameraRigMode.Cinematic;
            Vector3 point = _cinematicPoint.GetValueOrDefault();
            if (!_hasAnchor)
            {
                _anchor = point;
                _hasAnchor = true;
            }
            SmoothAnchor(point, cinematicMoveSmoothTime, cinematicMoveSmoothTime, dt);
            _lastDesiredAnchor = point;

            _yaw = Mathf.Repeat(_yaw + cinematicOrbitSpeed * dt, 360f);
            _pitch = Mathf.SmoothDampAngle(_pitch, cinematicPitch, ref _pitchVelocity, cinematicMoveSmoothTime, Mathf.Infinity, dt);
            BlendBoom(new Vector3(0f, 0f, -_cinematicDistance), cinematicBlendSharpness, dt);
        }

        private void StepSpectate(float dt)
        {
            Mode = CameraRigMode.Spectate;
            var court = Court.Instance;
            Vector3 centre = (court != null ? court.Center : Vector3.zero) + Vector3.up * spectatePivotHeight;
            if (!_hasAnchor)
            {
                _anchor = centre;
                _hasAnchor = true;
            }
            SmoothAnchor(centre, cinematicMoveSmoothTime, cinematicMoveSmoothTime, dt);
            _lastDesiredAnchor = centre;

            _yaw = Mathf.Repeat(_yaw + spectateOrbitSpeed * dt, 360f);
            _pitch = Mathf.SmoothDampAngle(_pitch, spectatePitch, ref _pitchVelocity, cinematicMoveSmoothTime, Mathf.Infinity, dt);
            BlendBoom(new Vector3(0f, 0f, -spectateDistance), cinematicBlendSharpness, dt);
        }

        /// <summary>Critically damped follow: planar and vertical smoothing are independent.</summary>
        private void SmoothAnchor(Vector3 desired, float planarTime, float verticalTime, float dt)
        {
            if (dt <= 0f) return;
            Vector3 planar = new Vector3(_anchor.x, 0f, _anchor.z);
            Vector3 desiredPlanar = new Vector3(desired.x, 0f, desired.z);
            planar = Vector3.SmoothDamp(planar, desiredPlanar, ref _planarVelocity, Mathf.Max(0.0001f, planarTime), Mathf.Infinity, dt);
            float y = Mathf.SmoothDamp(_anchor.y, desired.y, ref _verticalVelocity, Mathf.Max(0.0001f, verticalTime), Mathf.Infinity, dt);
            _anchor = new Vector3(planar.x, y, planar.z);
        }

        /// <summary>Exponential (frame-rate independent) blend of the framing vector.</summary>
        private void BlendBoom(Vector3 desired, float sharpness, float dt)
        {
            if (dt <= 0f) return;
            float k = 1f - Mathf.Exp(-sharpness * dt);
            _boomLocal = Vector3.Lerp(_boomLocal, desired, k);
        }

        /// <summary>
        /// Sphere-casts from the pivot toward the framed camera position against <see cref="collisionMask"/>. An
        /// obstruction shortens the boom instantly (the camera never shows the inside of a wall); when it clears, the
        /// boom extends back at <see cref="collisionRecoverSpeed"/>.
        /// </summary>
        private void ApplyCollision(float dt)
        {
            Vector3 pivot = _anchor;
            Quaternion orbit = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 wanted = orbit * _boomLocal;
            float length = wanted.magnitude;
            if (length < 1e-3f)
            {
                _boom.localPosition = _boomLocal;
                _boomLength = length;
                return;
            }

            float allowed = length;
            if (Physics.SphereCast(pivot, collisionRadius, wanted / length, out RaycastHit hit, length, collisionMask,
                    QueryTriggerInteraction.Ignore))
            {
                allowed = Mathf.Clamp(hit.distance, Mathf.Min(minBoomLength, length), length);
            }

            if (allowed < _boomLength || dt <= 0f) _boomLength = allowed;
            else _boomLength = Mathf.MoveTowards(_boomLength, allowed, collisionRecoverSpeed * dt);

            _boom.localPosition = _boomLocal * (_boomLength / length);
            _boom.localRotation = Quaternion.identity;
        }

        private void StepFov(DodgeballPlayer target, float dt)
        {
            float desired = baseFov;
            if (Mode == CameraRigMode.Follow && target != null)
            {
                var motor = target.Motor;
                var states = target.StateMachine;
                var combat = target.Combat;

                if (states != null && states.IsIn(PlayerStateId.Sprinting))
                {
                    float speed01 = motor != null && motor.CurrentMaxSpeed > 0.1f ? Mathf.Clamp01(motor.PlanarSpeed / motor.CurrentMaxSpeed) : 1f;
                    desired += sprintFovBonus * speed01;
                }
                if (motor != null && motor.IsSliding) desired += slideFovBonus;
                if (combat != null && combat.IsCharging) desired += chargeFovOffset;
            }

            if (dt > 0f) _fov = Mathf.Lerp(_fov, desired, 1f - Mathf.Exp(-fovSharpness * dt));

            // FOV kicks: instant, eased back (quadratic ease-out), unscaled.
            float kick = 0f;
            for (int i = _fovKickCount - 1; i >= 0; i--)
            {
                _fovKicks[i].Elapsed += dt;
                float s = _fovKicks[i].Elapsed / _fovKicks[i].Duration;
                if (s >= 1f)
                {
                    _fovKicks[i] = _fovKicks[--_fovKickCount];
                    continue;
                }
                float remaining = 1f - s;
                kick += _fovKicks[i].Degrees * remaining * remaining;
            }
            kick = Mathf.Clamp(kick, -maxTotalFovKick, maxTotalFovKick);

            Camera.fieldOfView = Mathf.Clamp(_fov + kick, 15f, 120f);
        }

        /// <summary>
        /// Planar movement basis from the yaw; aim ray from the unshaken camera pose (the Boom) through the screen centre;
        /// aim point from a raycast that starts at the target's depth (nothing between the camera and the player can be
        /// aimed at) and ignores the target's own colliders.
        /// </summary>
        private void UpdateBasisAndAim(DodgeballPlayer target)
        {
            Quaternion yawRotation = Quaternion.Euler(0f, _yaw, 0f);
            PlanarForward = yawRotation * Vector3.forward;
            PlanarRight = yawRotation * Vector3.right;

            Vector3 origin = _boom.position;
            Vector3 direction = _boom.forward;
            AimRay = new Ray(origin, direction);

            float skip = 0f;
            if (Mode == CameraRigMode.Follow && target != null)
                skip = Mathf.Max(0f, Vector3.Dot(_anchor - origin, direction));
            float maxDistance = Mathf.Max(1f, aimMaxDistance - skip);
            Vector3 start = origin + direction * skip;

            int count = Physics.RaycastNonAlloc(start, direction, _aimHits, maxDistance, aimMask, QueryTriggerInteraction.Ignore);
            float best = float.PositiveInfinity;
            Vector3 point = start + direction * maxDistance;
            for (int i = 0; i < count; i++)
            {
                var h = _aimHits[i];
                if (h.distance >= best || h.collider == null) continue;
                if (target != null && BelongsTo(h.collider, target)) continue;
                best = h.distance;
                point = h.point;
            }
            AimPoint = point;
        }

        // ================================================================== helpers

        private Vector3 DesiredFollowPoint(DodgeballPlayer target, out bool ragdolled)
        {
            ragdolled = false;
            CharacterVisual visual = target.Visual;
            RagdollController ragdoll = visual != null ? visual.Ragdoll : null;
            if (ragdoll != null && ragdoll.IsRagdolled && ragdoll.Hips != null)
            {
                ragdolled = true;
                return ragdoll.Hips.position + Vector3.up * ragdollPivotLift;
            }

            float height = pivotHeight;
            var motor = target.Motor;
            if (motor != null && motor.IsSliding) height -= slidePivotDrop;
            return target.Position + Vector3.up * height;
        }

        private Vector3 DesiredFollowBoom(DodgeballPlayer target)
        {
            bool charging = target.Combat != null && target.Combat.IsCharging;
            float boomDistance = distance * (charging ? chargeDistanceMultiplier : 1f);
            float shoulder = shoulderOffset * (charging ? chargeShoulderMultiplier : 1f);
            return new Vector3(shoulder, 0f, -boomDistance);
        }

        /// <summary>Facing the camera should adopt behind the target: toward the enemy from its current zone.</summary>
        private static Vector3 DesiredFacing(DodgeballPlayer target)
        {
            var court = Court.Instance;
            if (court != null && target.Team.IsValid())
            {
                Vector3 attack = court.AttackDirection(target.Team);
                // Outfield strips lie behind the opponent's baseline: outfield players face back toward the court.
                if (target.Zone == CourtZone.Outfield) attack = -attack;
                attack.y = 0f;
                if (attack.sqrMagnitude > 1e-4f) return attack.normalized;
            }
            return target.Forward;
        }

        private static float YawOf(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

        private static bool BelongsTo(Collider collider, DodgeballPlayer player)
        {
            if (player.Body != null && collider.attachedRigidbody == player.Body) return true;
            return collider.transform.IsChildOf(player.transform);
        }

        private void BuildHierarchy()
        {
            if (_yawPivot != null && _pitchPivot != null && _boom != null && _shakePivot != null) return;

            // Reuse an existing hierarchy (e.g. a rig saved in a scene after an edit-time Initialize).
            _yawPivot = FindOrCreateChild(transform, "YawPivot");
            _pitchPivot = FindOrCreateChild(_yawPivot, "PitchPivot");
            _boom = FindOrCreateChild(_pitchPivot, "Boom");
            _shakePivot = FindOrCreateChild(_boom, "ShakePivot");
            _shakePivot.localPosition = Vector3.zero;
            _shakePivot.localRotation = Quaternion.identity;

            var shaker = _shakePivot.GetComponent<CameraShaker>();
            if (shaker == null) shaker = _shakePivot.gameObject.AddComponent<CameraShaker>();
            shaker.SetRestPose(Vector3.zero, Quaternion.identity);
            Shaker = shaker;
            ShakePivot = _shakePivot;

            // CameraShaker registers itself while enabled; this makes it explicit (idempotent).
            var juice = JuiceManager.Instance;
            if (juice != null) juice.RegisterShaker(shaker);
            else JuiceManager.AddShaker(shaker);
        }

        private static Transform FindOrCreateChild(Transform parent, string childName)
        {
            var existing = parent.Find(childName);
            if (existing != null) return existing;
            var go = new GameObject(childName) { layer = parent.gameObject.layer };
            var t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
            return t;
        }

        private void AttachCamera(Camera cam)
        {
            var t = cam.transform;
            if (t.parent != _shakePivot) t.SetParent(_shakePivot, false);
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;

            if (!cam.CompareTag("MainCamera")) cam.gameObject.tag = "MainCamera";
            cam.fieldOfView = baseFov;
            cam.nearClipPlane = nearClip;
            cam.farClipPlane = farClip;
            cam.enabled = true;

            EnsureSingleAudioListener(cam);

            // Pipeline specifics (HDRP: anti-aliasing, post-processing frame settings) live in the adapter assembly.
            RuntimeRenderingHooks.Active?.ConfigureCamera(cam);

            Camera = cam;
        }

        /// <summary>3D audio is heard from the gameplay camera: exactly one enabled listener, on it.</summary>
        private static void EnsureSingleAudioListener(Camera cam)
        {
            var own = cam.GetComponent<AudioListener>();
            if (own == null) own = cam.gameObject.AddComponent<AudioListener>();
            own.enabled = true;

            var listeners = FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
            for (int i = 0; i < listeners.Length; i++)
                if (listeners[i] != own && listeners[i].enabled) listeners[i].enabled = false;
        }

        // ================================================================== events

        private void OnPlayerSpawned(PlayerSpawnedEvent e)
        {
            if (!autoTargetLocalPlayer || e.Player == null || !e.Player.IsLocalPlayer) return;
            if (Target != null) return;
            SetTarget(e.Player, snap: !_cinematicPoint.HasValue);
        }

        private void OnPlayerZoneChanged(PlayerZoneChangedEvent e)
        {
            // Eliminated -> outfield (behind the enemy) or revived -> infield: swing around to face the court again.
            // Deferred to the next follow step so the teleport that accompanies the zone change has happened.
            if (e.Player != null && e.Player == Target) _recenterPending = true;
        }
    }
}
