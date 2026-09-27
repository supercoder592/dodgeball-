using DodgeballUltra.CameraSystem;
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
    /// <para>
    /// Physical input comes from the shared, once-per-frame <see cref="InputService"/> snapshot. This component:
    /// <list type="bullet">
    /// <item><see cref="Sample"/> (called by DodgeballPlayer, scaled time): movement relative to
    /// <see cref="ThirdPersonCameraRig.PlanarForward"/>/<see cref="ThirdPersonCameraRig.PlanarRight"/>, aim from the camera's
    /// <see cref="ThirdPersonCameraRig.AimRay"/>/<see cref="ThirdPersonCameraRig.AimPoint"/>, and the button edges exactly as
    /// <see cref="PlayerIntent"/> expects (ThrowPressed on the press frame, ThrowHeld while held, ThrowReleased on the release
    /// frame; a tap shorter than one frame is spread over two frames so a charge always starts before it is released).</item>
    /// <item><c>Update</c> (every frame, independent of InputLocked / hitstop): look input to the camera rig — mouse pixels ×
    /// <see cref="mouseSensitivity"/>, right stick × <see cref="gamepadLookSpeed"/> × unscaled Δt — pause edge, cursor lock.</item>
    /// </list>
    /// While a modal menu holds the <see cref="InputGate"/>, intents are neutral (a throw being charged stays "held" so
    /// pausing neither fires nor cancels it), look input stops and the cursor is released.
    /// </para>
    /// <para>Owner module: InputHandling.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HumanInputSource : MonoBehaviour, IIntentSource
    {
        [Header("Look")]
        [Tooltip("Mouse look sensitivity (deg per mouse unit = per screen pixel of mouse movement).")]
        [Range(0.01f, 1f)] public float mouseSensitivity = 0.12f;

        [Tooltip("Gamepad look speed (deg/s at full deflection). X = yaw, Y = pitch.")]
        public Vector2 gamepadLookSpeed = new Vector2(220f, 150f);

        [Tooltip("Invert vertical look (mouse and right stick).")]
        public bool invertY;

        [Tooltip("Right-stick response curve exponent (1 = linear; higher = finer control near the centre).")]
        [Range(1f, 3f)] public float gamepadLookExponent = 1.6f;

        [Tooltip("Mouse look only turns the camera while the cursor is locked, so moving the mouse to a menu never spins the view.")]
        public bool mouseLookRequiresLockedCursor = true;

        [Header("Movement")]
        [Tooltip("Move input below this magnitude is ignored (on top of the backend's stick dead zone).")]
        [Range(0f, 0.5f)] public float moveDeadZone = 0.08f;

        [Tooltip("Gamepad L3 toggles sprint (keyboard Shift is always hold-to-sprint).")]
        public bool gamepadSprintToggle = true;

        [Tooltip("A toggled sprint ends after the stick rested below this deflection for sprintToggleGrace seconds.")]
        [Range(0.05f, 0.6f)] public float sprintToggleReleaseThreshold = 0.2f;

        [Tooltip("Seconds the stick may rest (e.g. while changing direction) before a toggled sprint ends.")]
        [Range(0f, 1f)] public float sprintToggleGrace = 0.3f;

        [Header("Aim")]
        [Tooltip("Aim distance (m) used when no camera rig reports an aim point.")]
        [Min(1f)] public float fallbackAimDistance = 30f;

        [Header("Cursor")]
        [Tooltip("Lock and hide the OS cursor during gameplay. Menus release it automatically (InputGate).")]
        public bool lockCursorInGameplay = true;

        /// <summary>True on the frame Pause was pressed (UI polls this).</summary>
        public bool PausePressedThisFrame { get; private set; }

        /// <summary>The player this source drives (known after the first <see cref="Sample"/>, or when on the same object).</summary>
        public DodgeballPlayer Player => _player;

        /// <summary>True when the local human last used a gamepad (prompts, aim-assist tuning).</summary>
        public bool UsingGamepad => InputService.UsingGamepad;

        /// <summary>The source currently feeding the camera rig (only one local human drives the camera).</summary>
        public static HumanInputSource Active => s_lookOwner;

        private static HumanInputSource s_lookOwner;

        private DodgeballPlayer _player;

        // Throw edge bookkeeping (see ProcessThrow).
        private bool _throwLatched;
        private bool _pendingThrowRelease;
        private bool _swallowThrowUntilRelease;

        // Gamepad click-to-sprint.
        private bool _sprintToggled;
        private float _sprintIdleTime;

        private bool _wantsCursorLocked;

        // ------------------------------------------------------------------ Unity lifecycle

        private void Awake()
        {
            _player = GetComponent<DodgeballPlayer>();
        }

        private void OnEnable()
        {
            InputGate.Changed += OnGateChanged;
            if (s_lookOwner == null) s_lookOwner = this;
            if (lockCursorInGameplay) SetCursorLocked(true);
        }

        private void OnDisable()
        {
            InputGate.Changed -= OnGateChanged;
            if (s_lookOwner == this) s_lookOwner = null;
            if (_wantsCursorLocked) InputGate.ApplyCursor(false);
            _wantsCursorLocked = false;
            ResetTransientState();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            // Alt-tab back into the game: restore the gameplay cursor state (never while a menu is open).
            if (hasFocus && _wantsCursorLocked && isActiveAndEnabled) InputGate.ApplyCursor(true);
        }

        private void Update()
        {
            var frame = InputService.Current;
            PausePressedThisFrame = frame.Pause.Pressed;

            if (s_lookOwner == null) s_lookOwner = this;
            MaintainCursor(frame);

            if (s_lookOwner == this && !InputGate.GameplayBlocked) ForwardLook(frame);
        }

        // ------------------------------------------------------------------ IIntentSource

        /// <inheritdoc />
        public PlayerIntent Sample(DodgeballPlayer player, float deltaTime)
        {
            if (player != null) _player = player;

            var frame = InputService.Current;
            PausePressedThisFrame = frame.Pause.Pressed;

            ResolveBasis(player, out Vector3 planarForward, out Vector3 planarRight, out Vector3 aimDirection, out Vector3 aimPoint);
            Vector3 position = player != null ? player.Position : transform.position;

            var intent = PlayerIntent.Neutral(position, planarForward);
            intent.AimDirection = aimDirection;
            intent.AimPoint = aimPoint;

            if (InputGate.GameplayBlocked)
            {
                // Pause / menus: freeze a throw that is being charged instead of releasing or cancelling it.
                intent.ThrowHeld = _throwLatched && !_pendingThrowRelease;
                _sprintToggled = false;
                return intent;
            }

            // The frame a menu closed belongs to the menu (e.g. gamepad A on "Resume" must not jump).
            bool edges = !InputGate.SuppressEdgesThisFrame;

            // ---------------------------------------------------------------- movement (camera relative)
            Vector2 stick = frame.Move;
            float stickMagnitude = stick.magnitude;
            if (stickMagnitude < moveDeadZone)
            {
                stick = Vector2.zero;
                stickMagnitude = 0f;
            }
            Vector3 move = planarRight * stick.x + planarForward * stick.y;
            move.y = 0f;
            if (move.sqrMagnitude > 1f) move.Normalize();
            intent.Move = move;

            // ---------------------------------------------------------------- sprint
            intent.SprintHeld = ProcessSprint(frame, stickMagnitude, edges);

            // ---------------------------------------------------------------- throw (hold to charge)
            ProcessThrow(frame, edges, ref intent);

            // ---------------------------------------------------------------- one-shot actions
            if (edges)
            {
                intent.JumpPressed = frame.Jump.Pressed;
                intent.SlidePressed = frame.Slide.Pressed;
                intent.CatchPressed = frame.Catch.Pressed;
                intent.PassPressed = frame.Pass.Pressed;
                intent.PickupPressed = frame.Pickup.Pressed;
                intent.SkillPressed = frame.Skill.Pressed;
                intent.UltimatePressed = frame.Ultimate.Pressed;
                intent.CycleTargetPressed = frame.CycleTarget.Pressed;
            }

            return intent;
        }

        /// <summary>Locks/unlocks the cursor for gameplay vs menus.</summary>
        public void SetCursorLocked(bool locked)
        {
            _wantsCursorLocked = locked;
            InputGate.ApplyCursor(locked); // never locks while a menu holds the gate
        }

        // ------------------------------------------------------------------ internals

        private bool ProcessSprint(in InputFrame frame, float stickMagnitude, bool edges)
        {
            if (!gamepadSprintToggle) return frame.Sprint.Held;

            float dt = Time.unscaledDeltaTime;
            if (stickMagnitude < sprintToggleReleaseThreshold) _sprintIdleTime += dt;
            else _sprintIdleTime = 0f;
            if (_sprintToggled && _sprintIdleTime > sprintToggleGrace) _sprintToggled = false;

            if (edges && frame.Sprint.Pressed && frame.SprintFromGamepad)
            {
                _sprintToggled = !_sprintToggled;
                _sprintIdleTime = 0f;
            }

            bool keyboardHeld = frame.Sprint.Held && !frame.SprintFromGamepad;
            return _sprintToggled || keyboardHeld;
        }

        /// <summary>
        /// Press / hold / release with guarantees the state machine relies on:
        /// ThrowPressed (+ThrowHeld) exactly once per physical press, ThrowHeld every frame while held, ThrowReleased exactly
        /// once afterwards and never on the same frame as ThrowPressed.
        /// </summary>
        private void ProcessThrow(in InputFrame frame, bool edges, ref PlayerIntent intent)
        {
            bool held = frame.Throw.Held;

            if (_pendingThrowRelease)
            {
                // A sub-frame tap was reported as pressed last frame; report its release now.
                intent.ThrowReleased = true;
                _pendingThrowRelease = false;
                _throwLatched = false;
                return;
            }

            if (_throwLatched)
            {
                if (held)
                {
                    intent.ThrowHeld = true;
                }
                else
                {
                    intent.ThrowReleased = true;
                    _throwLatched = false;
                }
                return;
            }

            if (_swallowThrowUntilRelease)
            {
                // The click that re-captured the cursor is not a throw.
                if (!held) _swallowThrowUntilRelease = false;
                return;
            }

            if (edges && frame.Throw.Pressed)
            {
                intent.ThrowPressed = true;
                intent.ThrowHeld = true;
                _throwLatched = true;
                if (!held) _pendingThrowRelease = true;
            }
        }

        private void ForwardLook(in InputFrame frame)
        {
            var rig = ThirdPersonCameraRig.Instance;
            if (rig == null) return;

            Vector2 degrees = Vector2.zero;

            bool mouseAllowed = !mouseLookRequiresLockedCursor || Cursor.lockState == CursorLockMode.Locked || Application.isMobilePlatform;
            if (mouseAllowed) degrees += frame.LookPointer * mouseSensitivity;

            Vector2 stick = frame.LookStick;
            float magnitude = stick.magnitude;
            if (magnitude > 1e-3f)
            {
                // Response curve keeps full speed at full deflection while giving precision for small corrections.
                float curved = Mathf.Pow(Mathf.Clamp01(magnitude), gamepadLookExponent);
                stick *= curved / magnitude;
                // Rate input: deg/s × unscaled Δt so the camera stays responsive through hitstop.
                degrees += new Vector2(stick.x * gamepadLookSpeed.x, stick.y * gamepadLookSpeed.y) * Time.unscaledDeltaTime;
            }

            if (invertY) degrees.y = -degrees.y;
            if (degrees.sqrMagnitude > 1e-10f) rig.AddLookInput(degrees);
        }

        private void MaintainCursor(in InputFrame frame)
        {
            if (!_wantsCursorLocked || InputGate.GameplayBlocked) return;
            if (Cursor.lockState == CursorLockMode.Locked) return;

            // The OS / editor released the cursor (focus loss, editor Esc). A click in the game view re-captures it; that
            // click must not also throw.
            if (frame.Throw.Pressed && frame.KeyboardMouseActive)
            {
                InputGate.ApplyCursor(true);
                if (!_throwLatched) _swallowThrowUntilRelease = true;
            }
        }

        private void OnGateChanged(bool blocked)
        {
            if (blocked)
            {
                _sprintToggled = false;
                return;
            }
            if (_wantsCursorLocked && isActiveAndEnabled) InputGate.ApplyCursor(true);
        }

        private void ResetTransientState()
        {
            _throwLatched = false;
            _pendingThrowRelease = false;
            _swallowThrowUntilRelease = false;
            _sprintToggled = false;
            _sprintIdleTime = 0f;
            PausePressedThisFrame = false;
        }

        /// <summary>Camera basis for movement and aim, with fallbacks when the rig is missing or not yet initialised.</summary>
        private void ResolveBasis(DodgeballPlayer player, out Vector3 planarForward, out Vector3 planarRight,
            out Vector3 aimDirection, out Vector3 aimPoint)
        {
            Vector3 origin = player != null ? player.ChestPosition : transform.position + Vector3.up * 1.3f;
            Vector3 playerForward = player != null ? player.Forward : Flatten(transform.forward, Vector3.forward);

            var rig = ThirdPersonCameraRig.Instance;
            if (rig != null && rig.isActiveAndEnabled)
            {
                planarForward = rig.PlanarForward;
                planarRight = rig.PlanarRight;
                aimDirection = rig.AimRay.direction;
                aimPoint = rig.AimPoint;
            }
            else
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    var t = cam.transform;
                    planarForward = t.forward;
                    planarRight = t.right;
                    aimDirection = t.forward;
                    aimPoint = t.position + t.forward * fallbackAimDistance;
                }
                else
                {
                    planarForward = playerForward;
                    planarRight = Vector3.Cross(Vector3.up, playerForward);
                    aimDirection = playerForward;
                    aimPoint = origin + playerForward * fallbackAimDistance;
                }
            }

            planarForward = Flatten(planarForward, playerForward);
            planarRight = Flatten(planarRight, Vector3.Cross(Vector3.up, planarForward));

            if (aimDirection.sqrMagnitude < 1e-6f || !IsFinite(aimDirection)) aimDirection = planarForward;
            aimDirection.Normalize();

            if (!IsFinite(aimPoint) || (aimPoint - origin).sqrMagnitude < 0.25f)
                aimPoint = origin + aimDirection * fallbackAimDistance;
        }

        private static Vector3 Flatten(Vector3 v, Vector3 fallback)
        {
            v.y = 0f;
            if (v.sqrMagnitude < 1e-6f || !IsFinite(v))
            {
                fallback.y = 0f;
                return fallback.sqrMagnitude > 1e-6f ? fallback.normalized : Vector3.forward;
            }
            return v.normalized;
        }

        private static bool IsFinite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
              float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
    }
}
