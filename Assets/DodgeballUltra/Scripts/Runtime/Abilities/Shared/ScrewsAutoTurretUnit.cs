using System;
using System.Collections.Generic;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.Player;
using DodgeballUltra.Rendering;
using DodgeballUltra.VFX;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>Tuning of one <see cref="ScrewsAutoTurretUnit"/> (copied from <see cref="ScrewsAutoTurret"/>).</summary>
    [Serializable]
    public struct ScrewsAutoTurretSettings
    {
        /// <summary>Free match balls within this planar radius (m) are pulled into the hopper. Spec: 4 m.</summary>
        public float CollectRadius;
        /// <summary>Balls the hopper holds.</summary>
        public int Capacity;
        /// <summary>Seconds between two shots (scaled). Spec: 1.5 s.</summary>
        public float FireInterval;
        /// <summary>Muzzle speed (km/h). Spec: ~95 km/h.</summary>
        public float ShotSpeedKmh;
        /// <summary>Target acquisition range (m).</summary>
        public float Range;
        /// <summary>Seconds (scaled) the tripod takes to unfold before it goes live.</summary>
        public float DeployTime;
        /// <summary>Seconds (unscaled) the unit takes to fold away at the end.</summary>
        public float FoldTime;
        /// <summary>Seconds after going live before the first shot.</summary>
        public float FirstShotDelay;
        /// <summary>Yaw servo speed (deg/s).</summary>
        public float YawRate;
        /// <summary>Pitch servo speed (deg/s).</summary>
        public float PitchRate;
        /// <summary>Total aim error (deg) under which the turret fires.</summary>
        public float AimTolerance;
        /// <summary>Seconds the turret waits for its servos before firing anyway.</summary>
        public float MaxAimWait;
        /// <summary>Maximum speed (m/s) of a ball lifted into the hopper.</summary>
        public float MagnetSpeed;
        /// <summary>Proportional gain (1/s) of the magnetic lift (speed = distance x gain, clamped).</summary>
        public float MagnetGain;
        /// <summary>Minimum lift speed (m/s).</summary>
        public float MagnetMinSpeed;
        /// <summary>Distance (m) from the hopper mouth at which a ball is stored.</summary>
        public float LoadRadius;
        /// <summary>A ball still not stored after this long (s) is snapped in.</summary>
        public float MaxMagnetTime;
        /// <summary>Seconds between two scans for loose balls.</summary>
        public float ScanInterval;
        /// <summary>Seconds between two target re-evaluations.</summary>
        public float RetargetInterval;
        /// <summary>Safety despawn time (s) of a stored ball: it respawns by itself if the turret never releases it.</summary>
        public float StoreSafety;
        /// <summary>Barrel kick (m) per shot.</summary>
        public float Recoil;
        /// <summary>Half-angle (deg) of the idle scanning sweep.</summary>
        public float IdleSweep;
        /// <summary>Balls higher than this above the floor (m) are not collected (live lobs, balls on the stands).</summary>
        public float MaxCollectHeight;

        /// <summary>Spec defaults: 4 m, 3 balls, 1.5 s, 95 km/h.</summary>
        public static ScrewsAutoTurretSettings Default => new ScrewsAutoTurretSettings
        {
            CollectRadius = 4f,
            Capacity = 3,
            FireInterval = 1.5f,
            ShotSpeedKmh = 95f,
            Range = 30f,
            DeployTime = 0.55f,
            FoldTime = 0.45f,
            FirstShotDelay = 0.4f,
            YawRate = 345f,
            PitchRate = 230f,
            AimTolerance = 6f,
            MaxAimWait = 0.45f,
            MagnetSpeed = 6.5f,
            MagnetGain = 3.2f,
            MagnetMinSpeed = 2.2f,
            LoadRadius = 0.3f,
            MaxMagnetTime = 2.4f,
            ScanInterval = 0.12f,
            RetargetInterval = 0.2f,
            StoreSafety = 15f,
            Recoil = 0.085f,
            IdleSweep = 32f,
            MaxCollectHeight = 2.5f,
        };
    }

    /// <summary>
    /// Screws' [Auto-Turret] unit (自動砲台): a tripod-mounted pneumatic ball launcher built from machined parts -
    /// powder-coated gunmetal housing, brushed steel barrel and hopper, rubber feet, team-enamel side plates, a sensor lens
    /// and a status LED (blue boot, green loaded, amber empty, red flash on fire).
    /// <list type="bullet">
    /// <item><b>Deploy</b>: the legs splay out and the head squats onto them (scaled time), then it goes live.</item>
    /// <item><b>Collect</b>: FREE match balls within <see cref="ScrewsAutoTurretSettings.CollectRadius"/> are magnetically
    ///       lifted into the hopper (velocity steering in FixedUpdate with one step of gravity compensated). A stored ball is
    ///       taken out of play with <see cref="DodgeBall.Despawn"/> (a dummy shows in the hopper) and comes back through
    ///       <see cref="DodgeBall.ResetTo"/> when fired or released. Capacity 3.</item>
    /// <item><b>Fire</b>: every <see cref="ScrewsAutoTurretSettings.FireInterval"/> s at the nearest targetable, uncloaked
    ///       enemy. The head (yaw) and barrel (pitch) servo onto a lead point (ballistic pitch from
    ///       <see cref="Ballistics.SolveLaunchAngle"/>), then a stored MATCH ball is launched through
    ///       <see cref="PlayerCombatController.BuildThrowParams"/> / <see cref="PlayerCombatController.LaunchBall"/> with the
    ///       muzzle as origin, ~95 km/h, style <see cref="BallStyle.Turret"/>; the thrower is Screws, so hits credit him.
    ///       While Screws is charging a throw of his own (LaunchBall would cancel it), or if the combat pipeline fails, the
    ///       unit launches the ball itself (same params, own ballistic solve) and publishes <see cref="BallThrownEvent"/>.
    ///       The character animation ignores these shots (<see cref="BallStyle.Turret"/>): Screws does not swing.</item>
    /// <item><b>Body</b>: a box collider on <see cref="GameLayers.Hittable"/> implementing <see cref="IBallHittable"/>:
    ///       live enemy balls are <see cref="BallHitResponse.Block"/>ed (the head wobbles and clangs), Screws' team's balls
    ///       and unblockable beams pass.</item>
    /// <item><b>Shutdown</b>: stored balls drop out as free balls, the unit folds and destroys itself. Round start / end and
    ///       match end dispose it immediately.</item>
    /// </list>
    /// Gameplay runs on scaled time (freezes in hitstop); the fold and the LED run on unscaled time.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class ScrewsAutoTurretUnit : MonoBehaviour, IBallHittable
    {
        /// <summary>Lifecycle of a unit.</summary>
        public enum TurretState { Deploying, Active, Folding, Disposed }

        // ------------------------------------------------------------------ geometry (metres)

        private const float LegLength = 0.74f;
        private const float SplayFolded = 0.08f;   // rad from vertical
        private const float SplayDeployed = 0.56f; // rad from vertical: feet on the floor
        private const float BodyCenterY = 0.93f;
        private static readonly Vector3 s_bodySize = new Vector3(0.6f, 0.84f, 0.6f);
        private const float BarrelZ = 0.25f;

        private static readonly Color s_ledBoot = new Color(0.25f, 0.63f, 1f);
        private static readonly Color s_ledReady = new Color(0.24f, 1f, 0.42f);
        private static readonly Color s_ledEmpty = new Color(1f, 0.66f, 0.12f);
        private static readonly Color s_ledFire = new Color(1f, 0.2f, 0.13f);

        // ------------------------------------------------------------------ registry

        private static readonly List<ScrewsAutoTurretUnit> s_active = new List<ScrewsAutoTurretUnit>(4);
        private static Mesh s_funnelMesh;
        private static Mesh s_bandMesh;
        private static readonly TurretShotPayload s_payload = new TurretShotPayload();

        /// <summary>Every deployed turret (AI can avoid throwing into them).</summary>
        public static IReadOnlyList<ScrewsAutoTurretUnit> ActiveUnits => s_active;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_active.Clear();
            s_funnelMesh = null;
            s_bandMesh = null;
        }

        // ------------------------------------------------------------------ state

        private struct IncomingBall
        {
            public DodgeBall Ball;
            public float Time;
        }

        private readonly List<DodgeBall> _stored = new List<DodgeBall>(4);
        private readonly List<IncomingBall> _incoming = new List<IncomingBall>(4);
        private readonly List<Transform> _legTilts = new List<Transform>(3);
        private readonly List<GameObject> _dummies = new List<GameObject>(3);

        private ScrewsAutoTurretSettings _settings;
        private DodgeballPlayer _owner;
        private TeamId _team = TeamId.None;
        private BoxCollider _collider;
        private float _floorY;
        private float _k;               // deploy amount 0..1
        private float _baseYaw;         // deg
        private float _yaw;             // deg (world)
        private float _pitch;           // deg, positive = up
        private DodgeballPlayer _target;
        private float _aimError = 180f;
        private float _aimWait;
        private float _fireTimer;
        private float _scanTimer;
        private float _retargetTimer;
        private float _ledFlash;
        private float _recoil, _recoilVelocity;
        private float _wobble, _wobbleVelocity;
        private float _time;
        private int _shownDummies = -1;
        private Color _ledShown = Color.clear;
        private bool _loggedPipelineFailure;

        private Transform _hub, _head, _pitchGroup, _barrel, _muzzle, _mouth;
        private Material _ledMaterial;

        private Action<RoundEndedEvent> _onRoundEnded;
        private Action<RoundStartedEvent> _onRoundStarted;
        private Action<MatchEndedEvent> _onMatchEnded;

        // ------------------------------------------------------------------ public state

        /// <inheritdoc />
        public TeamId OwnerTeam => _team;

        /// <summary>Screws (may be null if he left the match).</summary>
        public DodgeballPlayer Owner => _owner;

        public TurretState State { get; private set; } = TurretState.Deploying;

        /// <summary>Collecting, aiming, blocking (from half way through the deploy until the fold starts).</summary>
        public bool IsOperational => State == TurretState.Active || (State == TurretState.Deploying && _k > 0.5f);

        public int StoredCount => _stored.Count;
        public int ShotsFired { get; private set; }
        public int BallsBlocked { get; private set; }
        public int BallsCollected { get; private set; }

        /// <summary>Current target (null when scanning).</summary>
        public DodgeballPlayer Target => _target;

        /// <summary>World position of the muzzle (release point of the shots).</summary>
        public Vector3 MuzzlePosition => _muzzle != null ? _muzzle.position : transform.position + Vector3.up * BodyCenterY;

        /// <summary>World position of the hopper mouth (where collected balls are pulled to).</summary>
        public Vector3 HopperMouth => _mouth != null ? _mouth.position : transform.position + Vector3.up * 1.4f;

        /// <summary>Raised once when the unit is gone (folded away or disposed).</summary>
        public event Action<ScrewsAutoTurretUnit> Disposed;

        // ------------------------------------------------------------------ creation

        /// <summary>
        /// Deploys a turret for <paramref name="owner"/> standing on the floor at <paramref name="floorPosition"/>, initially
        /// facing <paramref name="yawDegrees"/> (world yaw).
        /// </summary>
        public static ScrewsAutoTurretUnit Deploy(DodgeballPlayer owner, Vector3 floorPosition, float yawDegrees,
            in ScrewsAutoTurretSettings settings)
        {
            var go = new GameObject(owner != null ? "ScrewsAutoTurret_" + owner.PlayerId : "ScrewsAutoTurret")
            {
                layer = GameLayers.Hittable,
            };
            go.transform.SetPositionAndRotation(floorPosition, Quaternion.identity);
            var unit = go.AddComponent<ScrewsAutoTurretUnit>();
            unit.Build(owner, floorPosition, yawDegrees, settings);
            return unit;
        }

        private void Awake()
        {
            _onRoundEnded = _ => Dispose(true);
            _onRoundStarted = _ => Dispose(true);
            _onMatchEnded = _ => Dispose(true);
        }

        private void OnEnable()
        {
            GameEvents.Subscribe(_onRoundEnded);
            GameEvents.Subscribe(_onRoundStarted);
            GameEvents.Subscribe(_onMatchEnded);
            if (!s_active.Contains(this)) s_active.Add(this);
        }

        private void OnDisable()
        {
            GameEvents.Unsubscribe(_onRoundEnded);
            GameEvents.Unsubscribe(_onRoundStarted);
            GameEvents.Unsubscribe(_onMatchEnded);
            s_active.Remove(this);
        }

        private void Build(DodgeballPlayer owner, Vector3 floorPosition, float yawDegrees, in ScrewsAutoTurretSettings s)
        {
            _owner = owner;
            _team = owner != null ? owner.Team : TeamId.None;
            _settings = s;
            _settings.Capacity = Mathf.Clamp(s.Capacity, 1, 3); // the hopper shows three dummies
            _settings.DeployTime = Mathf.Max(0.01f, s.DeployTime);
            _settings.FoldTime = Mathf.Max(0.01f, s.FoldTime);
            _settings.FireInterval = Mathf.Max(0.1f, s.FireInterval);
            _settings.ScanInterval = Mathf.Max(0.02f, s.ScanInterval);
            _settings.RetargetInterval = Mathf.Max(0.02f, s.RetargetInterval);
            _floorY = floorPosition.y;
            _baseYaw = yawDegrees;
            _yaw = yawDegrees;
            _pitch = 3f;
            _k = 0f;
            State = TurretState.Deploying;

            // ---- gameplay body (Hittable): enemy balls are blocked by the housing
            _collider = gameObject.AddComponent<BoxCollider>();
            _collider.isTrigger = false;
            _collider.center = new Vector3(0f, BodyCenterY, 0f);
            _collider.size = s_bodySize;
            _collider.enabled = false;

            BuildVisuals(owner);
            Pose(0f);

            VfxManager.Spawn(VfxId.FloorImpactDust, floorPosition, Quaternion.identity, 0.8f);
            AudioManager.PlayAt(SfxId.Turret, floorPosition + Vector3.up * 0.5f, 0.8f, 0.85f);
        }

        // ------------------------------------------------------------------ control

        /// <summary>Ends operation: drops the stored balls, stops blocking, folds up and destroys itself.</summary>
        public void Shutdown()
        {
            if (State == TurretState.Folding || State == TurretState.Disposed) return;
            ReleaseAll();
            State = TurretState.Folding;
            _target = null;
            if (_collider != null) _collider.enabled = false;
            AudioManager.PlayAt(SfxId.Turret, transform.position + Vector3.up * 0.8f, 0.55f, 0.72f);
        }

        /// <summary>Immediate removal (round reset, unequip). Stored balls are released unless <paramref name="releaseBalls"/> is false.</summary>
        public void Dispose(bool releaseBalls = true)
        {
            if (State == TurretState.Disposed) return;
            if (releaseBalls) ReleaseAll();
            else
            {
                _stored.Clear();
                _incoming.Clear();
            }
            State = TurretState.Disposed;
            _target = null;
            if (_collider != null) _collider.enabled = false;
            Destroy(gameObject);
        }

        // ------------------------------------------------------------------ IBallHittable

        /// <inheritdoc />
        public BallHitResponse OnBallHit(DodgeBall ball, in RaycastHit hit)
        {
            if (!IsOperational || ball == null || !ball.IsLive) return BallHitResponse.PassThrough;
            if (!ScrewsGadgetKit.IsEnemyBall(_team, ball)) return BallHitResponse.PassThrough;

            Vector3 point = hit.point;
            if (hit.distance <= 0f && point == Vector3.zero) point = ball.transform.position;
            Vector3 normal = hit.normal.sqrMagnitude > 1e-4f ? hit.normal : -ball.Velocity.normalized;
            if (normal.sqrMagnitude < 1e-4f) normal = Vector3.up;

            if (ball.Unblockable)
            {
                // A beam tears straight through: sparks, but the ball keeps going.
                VfxManager.Spawn(VfxId.BeamImpact, point, Quaternion.LookRotation(normal, Vector3.up), 0.4f);
                _wobbleVelocity += 3f;
                return BallHitResponse.PassThrough;
            }

            BallsBlocked++;
            _wobbleVelocity += Mathf.Clamp(ball.Speed * 0.25f, 3f, 9f);
            VfxManager.Spawn(VfxId.ShieldImpact, point, Quaternion.LookRotation(normal, Vector3.up), 0.6f, new Color(1f, 0.85f, 0.63f, 1f));
            AudioManager.PlayAt(SfxId.BallBounceWall, point, Mathf.Clamp01(0.55f + ball.SpeedKmh / 250f), UnityEngine.Random.Range(1.15f, 1.3f));
            return BallHitResponse.Block;
        }

        // ------------------------------------------------------------------ update

        private void Update()
        {
            if (State == TurretState.Disposed) return;
            float dt = Time.deltaTime;         // gameplay: freezes during hitstop
            float realDt = Time.unscaledDeltaTime;
            _time += dt;

            switch (State)
            {
                case TurretState.Deploying:
                    _k = Mathf.Min(1f, _k + dt / _settings.DeployTime);
                    if (_k >= 1f)
                    {
                        State = TurretState.Active;
                        _fireTimer = _settings.FirstShotDelay;
                        AudioManager.PlayAt(SfxId.Turret, HopperMouth, 0.45f, 1.35f); // servo whirr: ready
                    }
                    else if (IsOperational)
                    {
                        TickCollect(dt);
                    }
                    break;

                case TurretState.Active:
                    TickActive(dt);
                    break;

                case TurretState.Folding:
                    _k = Mathf.Max(0f, _k - realDt / _settings.FoldTime);
                    if (_k <= 0f)
                    {
                        Dispose(false);
                        return;
                    }
                    break;
            }

            if (_collider != null && _collider.enabled != IsOperational) _collider.enabled = IsOperational;
            Pose(realDt);
        }

        private void FixedUpdate()
        {
            if (!IsOperational || _incoming.Count == 0) return;
            SteerIncoming(Time.fixedDeltaTime);
        }

        private void TickCollect(float dt)
        {
            _scanTimer -= dt;
            if (_scanTimer <= 0f)
            {
                _scanTimer = _settings.ScanInterval;
                Scan();
            }
        }

        private void TickActive(float dt)
        {
            TickCollect(dt);

            _retargetTimer -= dt;
            if (_retargetTimer <= 0f || !IsValidTarget(_target))
            {
                _retargetTimer = _settings.RetargetInterval;
                _target = FindTarget();
            }

            AimAt(dt);

            _fireTimer -= dt;
            if (_fireTimer <= 0f && _stored.Count > 0 && _target != null)
            {
                if (_aimError <= _settings.AimTolerance || _aimWait >= _settings.MaxAimWait)
                {
                    _fireTimer = Shoot() ? _settings.FireInterval : 0.25f;
                    _aimWait = 0f;
                }
                else
                {
                    _aimWait += dt;
                }
            }
        }

        // ------------------------------------------------------------------ collection

        private void Scan()
        {
            BallManager manager = BallManager.Instance;
            if (manager == null) return;
            int free = _settings.Capacity - _stored.Count - _incoming.Count;
            if (free <= 0) return;

            Vector3 centre = transform.position;
            float r2 = _settings.CollectRadius * _settings.CollectRadius;
            float maxY = _floorY + _settings.MaxCollectHeight;
            IReadOnlyList<DodgeBall> balls = manager.MatchBalls;
            for (int i = 0; i < balls.Count && free > 0; i++)
            {
                DodgeBall b = balls[i];
                if (b == null || b.State != BallState.Free || b.IsAbilityBall || b.IsPooled || b.IsFading || !b.isActiveAndEnabled) continue;
                if (IsIncoming(b)) continue;
                Vector3 p = b.transform.position;
                float dx = p.x - centre.x, dz = p.z - centre.z;
                if (dx * dx + dz * dz > r2 || p.y > maxY) continue;

                _incoming.Add(new IncomingBall { Ball = b, Time = 0f });
                free--;
                AudioManager.PlayAt(SfxId.Magnet, p, 0.4f, 1.3f);
            }
        }

        private bool IsIncoming(DodgeBall ball)
        {
            for (int i = 0; i < _incoming.Count; i++)
                if (_incoming[i].Ball == ball) return true;
            return false;
        }

        /// <summary>Magnetic lift toward the hopper mouth (velocity steering, one physics step of gravity compensated).</summary>
        private void SteerIncoming(float fixedDt)
        {
            Vector3 mouth = HopperMouth;
            Vector3 gravityCompensation = -Physics.gravity * fixedDt;
            for (int i = _incoming.Count - 1; i >= 0; i--)
            {
                IncomingBall entry = _incoming[i];
                DodgeBall b = entry.Ball;
                if (b == null || b.State != BallState.Free)
                {
                    _incoming.RemoveAt(i); // picked up, thrown or reset meanwhile
                    continue;
                }

                entry.Time += fixedDt;
                Vector3 to = mouth - b.transform.position;
                float d = to.magnitude;
                if (d <= _settings.LoadRadius || entry.Time >= _settings.MaxMagnetTime)
                {
                    _incoming.RemoveAt(i);
                    Store(b);
                    continue;
                }
                _incoming[i] = entry;

                float speed = Mathf.Clamp(d * _settings.MagnetGain, _settings.MagnetMinSpeed, _settings.MagnetSpeed);
                b.SetVelocity(to * (speed / Mathf.Max(d, 1e-4f)) + gravityCompensation);
            }
        }

        private void Store(DodgeBall ball)
        {
            if (ball == null || _stored.Count >= _settings.Capacity || ball.IsAbilityBall) return;

            // Safety respawn beside the unit, used only if the turret never gives the ball back.
            float yawRad = _baseYaw * Mathf.Deg2Rad;
            Vector3 safety = new Vector3(transform.position.x + 0.6f * Mathf.Sin(yawRad), _floorY + 0.12f,
                transform.position.z + 0.6f * Mathf.Cos(yawRad));
            ball.Despawn(_settings.StoreSafety, safety);
            if (ball.State != BallState.Despawned) return; // refused

            _stored.Add(ball);
            BallsCollected++;
            Color tint = _owner != null && _owner.Visual != null ? _owner.Visual.TeamColor : Color.white;
            VfxManager.Spawn(VfxId.CatchPuff, HopperMouth, Quaternion.identity, 0.45f, tint);
            AudioManager.PlayAt(SfxId.Pickup, HopperMouth, 0.8f, 0.72f); // metallic clunk into the hopper
        }

        /// <summary>Drops every stored ball out of the hopper as a free ball and forgets balls being pulled in.</summary>
        private void ReleaseAll()
        {
            Vector3 mouth = HopperMouth;
            for (int i = 0; i < _stored.Count; i++)
            {
                DodgeBall b = _stored[i];
                if (b == null || b.State != BallState.Despawned) continue; // already reset by the round flow
                float a = (_yaw + 180f + (i - 1) * 40f) * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                Vector3 pos = mouth + dir * 0.25f;
                pos.y = Mathf.Max(_floorY + 0.3f, mouth.y);
                b.ResetTo(pos);
                b.MakeFree(dir * 1.3f + Vector3.up * 1.2f, true);
            }
            _stored.Clear();
            _incoming.Clear();
        }

        // ------------------------------------------------------------------ targeting

        private bool IsValidTarget(DodgeballPlayer p)
        {
            if (p == null || _owner == null || !p.isActiveAndEnabled) return false;
            if (!PlayerRegistry.AreEnemies(_owner, p) || !p.IsTargetable) return false;
            if (p.Status != null && p.Status.Has(StatusEffectType.Cloaked)) return false; // the sensor cannot see Gale
            Vector3 d = p.Position - transform.position;
            d.y = 0f;
            return d.sqrMagnitude <= _settings.Range * _settings.Range;
        }

        private DodgeballPlayer FindTarget()
        {
            DodgeballPlayer best = null;
            float bestSqr = float.MaxValue;
            IReadOnlyList<DodgeballPlayer> players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                DodgeballPlayer p = players[i];
                if (!IsValidTarget(p)) continue;
                Vector3 d = p.Position - transform.position;
                d.y = 0f;
                float sqr = d.sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = p;
                }
            }
            return best;
        }

        private float ShotSpeed => Mathf.Max(1f, _settings.ShotSpeedKmh / GameConstants.MsToKmh);

        private float GravityScale
        {
            get
            {
                var combat = _owner != null ? _owner.Combat : null;
                float scale = combat != null && combat.Profile != null ? combat.Profile.thrownGravityScale : 0.65f;
                return scale > 0f ? scale : 0.65f;
            }
        }

        /// <summary>Servo the head (yaw) and the barrel (pitch) onto the target's lead point, or scan slowly when idle.</summary>
        private void AimAt(float dt)
        {
            float yawGoal, pitchGoal;
            DodgeballPlayer t = _target;
            Vector3 pivot = _pitchGroup != null ? _pitchGroup.position : transform.position + Vector3.up * BodyCenterY;
            if (t != null)
            {
                Vector3 aim = LeadPoint(pivot, t);
                Vector3 d = aim - pivot;
                float horizontal = new Vector2(d.x, d.z).magnitude;
                yawGoal = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                float g = Mathf.Abs(Physics.gravity.y) * GravityScale;
                Ballistics.SolveLaunchAngle(ShotSpeed, horizontal, d.y, g, true, out float angle);
                pitchGoal = Mathf.Clamp(angle * Mathf.Rad2Deg, -18f, 34f);
            }
            else
            {
                yawGoal = _baseYaw + Mathf.Sin(_time * 0.6f) * _settings.IdleSweep;
                pitchGoal = 3f;
            }

            _yaw = Mathf.MoveTowardsAngle(_yaw, yawGoal, _settings.YawRate * dt);
            _pitch = Mathf.MoveTowards(_pitch, pitchGoal, _settings.PitchRate * dt);
            _aimError = t != null ? Mathf.Abs(Mathf.DeltaAngle(_yaw, yawGoal)) + Mathf.Abs(pitchGoal - _pitch) : 180f;
        }

        /// <summary>Chest of <paramref name="target"/> where it will be when a shot from <paramref name="from"/> arrives.</summary>
        private Vector3 LeadPoint(Vector3 from, DodgeballPlayer target)
        {
            Vector3 chest = target.ChestPosition;
            Vector3 v = target.Velocity;
            v.y = 0f; // jumps are too short-lived to lead
            float speed = ShotSpeed;
            Vector3 aim = chest;
            for (int i = 0; i < 2; i++)
            {
                float time = Vector3.Distance(from, aim) / speed;
                aim = chest + v * time;
            }
            return aim;
        }

        // ------------------------------------------------------------------ firing

        private bool Shoot()
        {
            DodgeballPlayer target = _target;
            if (_owner == null || target == null) return false;

            DodgeBall ball = null;
            while (_stored.Count > 0 && ball == null)
            {
                DodgeBall candidate = _stored[0];
                _stored.RemoveAt(0);
                if (candidate != null && candidate.State == BallState.Despawned) ball = candidate;
            }
            if (ball == null) return false;

            Vector3 muzzle = MuzzlePosition;
            Vector3 aim = LeadPoint(muzzle, target);
            PlayerCombatController combat = _owner.Combat;
            float fullCharge = combat != null && combat.Profile != null ? combat.Profile.fullChargeTime : 0.75f;

            ThrowParams p = default;
            bool built = false;
            if (combat != null)
            {
                try
                {
                    p = combat.BuildThrowParams(fullCharge, target, true);
                    built = true;
                }
                catch (Exception e)
                {
                    LogPipelineFailure(e);
                }
            }

            // Turret shots are fixed: ~95 km/h from the muzzle, never charged, boosted or special.
            p.Thrower = _owner;
            p.Target = target;
            p.Origin = muzzle;
            p.AimPoint = aim;
            p.AimDirection = (aim - muzzle).sqrMagnitude > 1e-6f ? (aim - muzzle).normalized : _muzzle.forward;
            p.BaseSpeed = ShotSpeed;
            p.ChargeNormalized = 1f;
            p.ChargeSeconds = fullCharge;
            p.SpeedMultiplier = 1f;
            p.RadiusMultiplier = 1f;
            p.RallyCount = 0;
            if (p.GravityScale <= 0f) p.GravityScale = GravityScale;
            p.Unblockable = false;
            p.Pierce = false;
            p.IsAbilityThrow = true;
            p.IsPass = false;
            p.IsCounterThrow = false;
            p.RevealsThrower = false;
            p.Style = BallStyle.Turret;
            p.Payload = s_payload;

            ball.ResetTo(muzzle); // back in play at the muzzle (Free), launched on the same frame
            bool launched = false;
            // LaunchBall releases the owner's throw charge: while Screws is winding up a throw of his own, the turret uses
            // the equivalent direct launch so his charge is never cancelled by a shot he did not make.
            if (built && !combat.IsCharging)
            {
                try
                {
                    combat.LaunchBall(ball, p);
                }
                catch (Exception e)
                {
                    LogPipelineFailure(e);
                }
                launched = ball.State == BallState.Live;
            }
            if (!launched) launched = LaunchDirect(ball, in p, aim);

            if (!launched)
            {
                ball.MakeFree(p.AimDirection * 2f, true);
                return false;
            }

            ShotsFired++;
            _recoil = -_settings.Recoil;
            _recoilVelocity = 0f;
            _ledFlash = 0.14f;
            VfxManager.Spawn(VfxId.TurretMuzzle, muzzle, Quaternion.LookRotation(p.AimDirection, Vector3.up), 1f, new Color(1f, 0.95f, 0.82f, 1f));
            AudioManager.PlayAt(SfxId.Turret, muzzle, 1f, UnityEngine.Random.Range(0.95f, 1.05f));
            return true;
        }

        /// <summary>
        /// Launch-equivalent used when the combat pipeline is unavailable: ballistic low arc onto the lead point at the
        /// shot speed, <see cref="DodgeBall.Launch"/> with the same params, and the usual <see cref="BallThrownEvent"/>.
        /// </summary>
        private bool LaunchDirect(DodgeBall ball, in ThrowParams p, Vector3 aim)
        {
            if (ball == null) return false;
            float speed = p.FinalSpeed;
            Vector3 d = aim - p.Origin;
            var planar = new Vector3(d.x, 0f, d.z);
            float horizontal = planar.magnitude;
            Vector3 heading = horizontal > 1e-4f ? planar / horizontal : HumanoidPlanar(p.AimDirection);
            float g = Mathf.Abs(Physics.gravity.y) * Mathf.Max(0f, p.GravityScale);
            Ballistics.SolveLaunchAngle(speed, horizontal, d.y, g, true, out float angle);
            Vector3 velocity = heading * (Mathf.Cos(angle) * speed) + Vector3.up * (Mathf.Sin(angle) * speed);

            ball.Launch(p, velocity);
            if (!ball.IsLive) return false;
            GameEvents.Publish(new BallThrownEvent
            {
                Ball = ball,
                Thrower = p.Thrower,
                Target = p.Target,
                Origin = p.Origin,
                Velocity = ball.Velocity,
                SpeedKmh = ball.SpeedKmh,
                RallyCount = 0,
                ChargeNormalized = 1f,
                IsAbilityThrow = true,
                IsPass = false,
                IsCounterThrow = false,
            });
            return true;
        }

        private static Vector3 HumanoidPlanar(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        private void LogPipelineFailure(Exception e)
        {
            if (_loggedPipelineFailure) return;
            _loggedPipelineFailure = true;
            Debug.LogWarning("[Dodgeball Ultra] Auto-Turret: the combat throw pipeline failed, firing directly instead. " + e.Message, this);
        }

        // ------------------------------------------------------------------ pose

        private void Pose(float realDt)
        {
            if (_hub == null) return;
            float e = _k * _k * (3f - 2f * _k);
            float splay = Mathf.Lerp(SplayFolded, SplayDeployed, e);
            float splayDeg = splay * Mathf.Rad2Deg;
            for (int i = 0; i < _legTilts.Count; i++) _legTilts[i].localRotation = Quaternion.Euler(-splayDeg, 0f, 0f); // feet outward
            _hub.localPosition = new Vector3(0f, LegLength * Mathf.Cos(splay), 0f);

            if (realDt > 0f)
            {
                SpringStep(ref _recoil, ref _recoilVelocity, 26f, 0.55f, realDt);
                SpringStep(ref _wobble, ref _wobbleVelocity, 18f, 0.35f, realDt);
            }

            _head.localRotation = Quaternion.Euler(0f, _yaw, _wobble * 3f);
            _head.localScale = Vector3.one * (0.7f + 0.3f * e);
            _pitchGroup.localRotation = Quaternion.Euler(-_pitch, 0f, 0f);
            _barrel.localPosition = new Vector3(0f, 0f, BarrelZ * (0.55f + 0.45f * e) + _recoil);

            int shown = Mathf.Min(_stored.Count, _dummies.Count);
            if (shown != _shownDummies)
            {
                _shownDummies = shown;
                for (int i = 0; i < _dummies.Count; i++) _dummies[i].SetActive(i < shown);
            }

            // Status LED: boot (blue) -> loaded (green) / empty (amber blink), red flash on every shot.
            _ledFlash = Mathf.Max(0f, _ledFlash - realDt);
            float blink = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 10f);
            Color led;
            if (_ledFlash > 0f) led = s_ledFire * 6f;
            else if (State == TurretState.Active) led = _stored.Count > 0 ? s_ledReady * 3f : s_ledEmpty * (1.5f + 2f * blink);
            else if (State == TurretState.Folding) led = s_ledEmpty;
            else led = s_ledBoot * (2f + 2f * blink);
            led.a = 1f;
            if (_ledMaterial != null && led != _ledShown)
            {
                _ledShown = led;
                ScrewsGadgetKit.SetTint(_ledMaterial, led);
            }
        }

        /// <summary>Damped spring toward 0 (semi-implicit Euler).</summary>
        private static void SpringStep(ref float x, ref float v, float omega, float zeta, float dt)
        {
            v += (-omega * omega * x - 2f * zeta * omega * v) * dt;
            x += v * dt;
            if (Mathf.Abs(x) < 1e-5f && Mathf.Abs(v) < 1e-4f)
            {
                x = 0f;
                v = 0f;
            }
        }

        // ------------------------------------------------------------------ visuals

        private void BuildVisuals(DodgeballPlayer owner)
        {
            Material gun = MaterialFactory.GetOrCreate("DU_Turret_Gunmetal",
                () => MaterialFactory.CreateLit("DU_Turret_Gunmetal", new Color(0.14f, 0.15f, 0.16f), 0.58f, 0.85f));
            Material steel = MaterialFactory.GetOrCreate("DU_Turret_Steel",
                () => MaterialFactory.CreateLit("DU_Turret_Steel", new Color(0.6f, 0.61f, 0.62f), 0.74f, 1f));
            Material rubber = MaterialFactory.GetOrCreate("DU_Turret_Rubber",
                () => MaterialFactory.CreateLit("DU_Turret_Rubber", new Color(0.035f, 0.035f, 0.035f), 0.28f, 0f));
            Material glass = MaterialFactory.GetOrCreate("DU_Turret_Glass",
                () => MaterialFactory.CreateLit("DU_Turret_Glass", new Color(0.02f, 0.03f, 0.05f), 0.96f, 0f));
            Color team = owner != null && owner.Visual != null ? owner.Visual.TeamColor : new Color(0.96f, 0.74f, 0.16f);
            team.a = 1f;
            string paintKey = "DU_Turret_Paint_" + ColorUtility.ToHtmlStringRGB(team);
            Material paint = MaterialFactory.GetOrCreate(paintKey,
                () => MaterialFactory.CreateLit(paintKey, team, 0.82f, 0f)); // glossy enamel
            _ledMaterial = MaterialFactory.CreateUnlit("DU_Turret_LED", s_ledBoot * 2f, false);
            Material ballMaterial = BallAppearance.GetBallMaterial(BallStyle.Standard);

            Transform root = transform;
            Quaternion alongZ = Quaternion.Euler(90f, 0f, 0f); // primitive cylinders run along Y

            // ---- hub + tripod legs (yaw pivot -> tilt -> tube, hinge block, rubber foot)
            _hub = ScrewsGadgetKit.CreatePivot("Hub", root, Vector3.zero, Quaternion.identity);
            ScrewsGadgetKit.CreatePart("HubCasting", PrimitiveType.Cylinder, _hub, Vector3.zero, Quaternion.identity,
                new Vector3(0.17f, 0.05f, 0.17f), gun);
            for (int i = 0; i < 3; i++)
            {
                Transform yawPivot = ScrewsGadgetKit.CreatePivot("Leg" + i, _hub, Vector3.zero,
                    Quaternion.Euler(0f, _baseYaw + 60f + i * 120f, 0f));
                Transform tilt = ScrewsGadgetKit.CreatePivot("Tilt", yawPivot, Vector3.zero, Quaternion.identity);
                ScrewsGadgetKit.CreatePart("Tube", PrimitiveType.Cylinder, tilt, new Vector3(0f, -LegLength * 0.5f, 0f),
                    Quaternion.identity, new Vector3(0.044f, LegLength * 0.5f, 0.044f), steel);
                ScrewsGadgetKit.CreatePart("Hinge", PrimitiveType.Cube, tilt, Vector3.zero, Quaternion.identity,
                    new Vector3(0.05f, 0.05f, 0.05f), gun);
                ScrewsGadgetKit.CreatePart("Clamp", PrimitiveType.Cylinder, tilt, new Vector3(0f, -LegLength * 0.45f, 0f),
                    Quaternion.identity, new Vector3(0.056f, 0.018f, 0.056f), gun);
                ScrewsGadgetKit.CreatePart("Foot", PrimitiveType.Cylinder, tilt, new Vector3(0f, -LegLength, 0f),
                    Quaternion.identity, new Vector3(0.076f, 0.015f, 0.076f), rubber);
                _legTilts.Add(tilt);
            }
            ScrewsGadgetKit.CreatePart("Bearing", PrimitiveType.Cylinder, _hub, new Vector3(0f, 0.072f, 0f), Quaternion.identity,
                new Vector3(0.22f, 0.0225f, 0.22f), steel);

            // ---- head (yaw): housing, enamel side plates, vents, sensor lens, status LED
            _head = ScrewsGadgetKit.CreatePivot("Head", _hub, new Vector3(0f, 0.1f, 0f), Quaternion.identity);
            ScrewsGadgetKit.CreatePart("Housing", PrimitiveType.Cube, _head, new Vector3(0f, 0.14f, -0.03f), Quaternion.identity,
                new Vector3(0.32f, 0.24f, 0.46f), gun);
            for (int side = -1; side <= 1; side += 2)
            {
                ScrewsGadgetKit.CreatePart(side < 0 ? "PlateLeft" : "PlateRight", PrimitiveType.Cube, _head,
                    new Vector3(side * 0.169f, 0.14f, -0.03f), Quaternion.identity, new Vector3(0.018f, 0.18f, 0.34f), paint);
                // Hex bolts at the plate corners.
                for (int b = 0; b < 4; b++)
                {
                    float by = (b & 1) == 0 ? 0.065f : 0.215f;
                    float bz = (b & 2) == 0 ? -0.18f : 0.12f;
                    ScrewsGadgetKit.CreatePart("Bolt", PrimitiveType.Cylinder, _head, new Vector3(side * 0.18f, by, bz),
                        Quaternion.Euler(0f, 0f, 90f), new Vector3(0.018f, 0.004f, 0.018f), steel, false);
                }
            }
            for (int v = 0; v < 3; v++)
            {
                ScrewsGadgetKit.CreatePart("Vent", PrimitiveType.Cube, _head, new Vector3(0f, 0.262f, -0.2f + v * 0.045f),
                    Quaternion.identity, new Vector3(0.2f, 0.006f, 0.022f), rubber, false);
            }
            ScrewsGadgetKit.CreatePart("SensorLens", PrimitiveType.Cylinder, _head, new Vector3(-0.1f, 0.22f, 0.2f), alongZ,
                new Vector3(0.06f, 0.0175f, 0.06f), glass, false);
            ScrewsGadgetKit.CreatePart("SensorBezel", PrimitiveType.Cylinder, _head, new Vector3(-0.1f, 0.22f, 0.195f), alongZ,
                new Vector3(0.075f, 0.012f, 0.075f), steel, false);
            ScrewsGadgetKit.CreatePart("StatusLed", PrimitiveType.Sphere, _head, new Vector3(0.11f, 0.265f, 0.19f), Quaternion.identity,
                new Vector3(0.032f, 0.032f, 0.032f), _ledMaterial, false);

            // ---- barrel (pitch + recoil): tube, dark bore, reinforcing rings, muzzle collar
            _pitchGroup = ScrewsGadgetKit.CreatePivot("Pitch", _head, new Vector3(0f, 0.16f, 0.06f), Quaternion.identity);
            _barrel = ScrewsGadgetKit.CreatePivot("Barrel", _pitchGroup, Vector3.zero, Quaternion.identity);
            ScrewsGadgetKit.CreatePart("Tube", PrimitiveType.Cylinder, _barrel, Vector3.zero, alongZ,
                new Vector3(0.28f, 0.25f, 0.28f), gun);
            ScrewsGadgetKit.CreatePart("Collar", PrimitiveType.Cylinder, _barrel, new Vector3(0f, 0f, 0.25f), alongZ,
                new Vector3(0.31f, 0.03f, 0.31f), steel);
            ScrewsGadgetKit.CreatePart("Bore", PrimitiveType.Cylinder, _barrel, new Vector3(0f, 0f, 0.281f), alongZ,
                new Vector3(0.236f, 0.002f, 0.236f), rubber, false);
            ScrewsGadgetKit.CreatePart("RingRear", PrimitiveType.Cylinder, _barrel, new Vector3(0f, 0f, -0.12f), alongZ,
                new Vector3(0.3f, 0.01f, 0.3f), steel);
            ScrewsGadgetKit.CreatePart("RingMid", PrimitiveType.Cylinder, _barrel, new Vector3(0f, 0f, 0.06f), alongZ,
                new Vector3(0.3f, 0.01f, 0.3f), steel);
            _muzzle = ScrewsGadgetKit.CreatePivot("Muzzle", _barrel, new Vector3(0f, 0f, 0.31f), Quaternion.identity);

            // ---- hopper: neck, funnel, rolled rim, mouth marker, dummy balls
            Transform hopper = ScrewsGadgetKit.CreatePivot("Hopper", _head, new Vector3(0f, 0.26f, -0.15f), Quaternion.identity);
            ScrewsGadgetKit.CreatePart("Neck", PrimitiveType.Cylinder, hopper, new Vector3(0f, 0.06f, 0f), Quaternion.identity,
                new Vector3(0.25f, 0.06f, 0.25f), steel);
            ScrewsGadgetKit.CreateMeshPart("Funnel", FunnelMesh, hopper, new Vector3(0f, 0.12f, 0f), Quaternion.identity,
                Vector3.one, steel);
            ScrewsGadgetKit.CreateMeshPart("Rim", BandMesh, hopper, new Vector3(0f, 0.355f, 0f), Quaternion.identity,
                Vector3.one, steel);
            _mouth = ScrewsGadgetKit.CreatePivot("Mouth", hopper, new Vector3(0f, 0.42f, 0f), Quaternion.identity);

            Mesh ballMesh = ProceduralBallMesh.Get();
            Vector3[] dummyPositions = { new Vector3(0f, 0.2f, 0f), new Vector3(0.09f, 0.3f, 0.05f), new Vector3(-0.09f, 0.31f, -0.04f) };
            for (int i = 0; i < dummyPositions.Length; i++)
            {
                Transform dummy = ScrewsGadgetKit.CreateMeshPart("StoredBall", ballMesh, hopper, dummyPositions[i],
                    Quaternion.Euler(0f, i * 97f, 0f), Vector3.one, ballMaterial);
                dummy.gameObject.SetActive(false);
                _dummies.Add(dummy.gameObject);
            }
        }

        /// <summary>Open truncated cone (bottom r 0.13, top r 0.23, 0.24 m tall), double-sided, shared by every turret.</summary>
        private static Mesh FunnelMesh => s_funnelMesh != null ? s_funnelMesh : (s_funnelMesh = BuildFrustum("DU_TurretFunnel", 0.13f, 0.23f, 0.24f, 32));

        /// <summary>Short band around the funnel mouth (the rolled rim).</summary>
        private static Mesh BandMesh => s_bandMesh != null ? s_bandMesh : (s_bandMesh = BuildFrustum("DU_TurretRim", 0.232f, 0.242f, 0.025f, 32));

        /// <summary>
        /// Double-sided open frustum along +Y (bottom ring at y = 0). Separate outside / inside vertices with their own
        /// normals so the bright steel shades correctly on both faces.
        /// </summary>
        private static Mesh BuildFrustum(string name, float bottomRadius, float topRadius, float height, int segments)
        {
            segments = Mathf.Max(8, segments);
            int ring = segments + 1;
            var vertices = new Vector3[ring * 4];
            var normals = new Vector3[ring * 4];
            var uvs = new Vector2[ring * 4];
            var triangles = new int[segments * 12];
            float slope = (bottomRadius - topRadius) / Mathf.Max(1e-4f, height);

            for (int i = 0; i < ring; i++)
            {
                float u = (float)i / segments;
                float a = u * Mathf.PI * 2f;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                var bottom = new Vector3(c * bottomRadius, 0f, s * bottomRadius);
                var top = new Vector3(c * topRadius, height, s * topRadius);
                Vector3 n = new Vector3(c, slope, s).normalized;
                // Outside: [0, ring) bottom, [ring, 2 ring) top. Inside: [2 ring, 3 ring) bottom, [3 ring, 4 ring) top.
                vertices[i] = bottom;
                vertices[i + ring] = top;
                vertices[i + ring * 2] = bottom;
                vertices[i + ring * 3] = top;
                normals[i] = n;
                normals[i + ring] = n;
                normals[i + ring * 2] = -n;
                normals[i + ring * 3] = -n;
                uvs[i] = uvs[i + ring * 2] = new Vector2(u, 0f);
                uvs[i + ring] = uvs[i + ring * 3] = new Vector2(u, 1f);
            }

            int t = 0;
            for (int i = 0; i < segments; i++)
            {
                int b0 = i, b1 = i + 1, t0 = i + ring, t1 = i + 1 + ring;
                // Seen from outside (b0 left-bottom, b1 right-bottom): clockwise (b0, t0, t1), (b0, t1, b1).
                triangles[t++] = b0; triangles[t++] = t0; triangles[t++] = t1;
                triangles[t++] = b0; triangles[t++] = t1; triangles[t++] = b1;
                int ib0 = b0 + ring * 2, ib1 = b1 + ring * 2, it0 = t0 + ring * 2, it1 = t1 + ring * 2;
                triangles[t++] = ib0; triangles[t++] = it1; triangles[t++] = it0;
                triangles[t++] = ib0; triangles[t++] = ib1; triangles[t++] = it1;
            }

            var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------------ teardown

        private void OnDestroy()
        {
            s_active.Remove(this);
            // Destroyed without a proper shutdown (scene change, hero swap): never leave balls stuck in the void.
            if (_stored.Count > 0) ReleaseAll();
            _incoming.Clear();
            State = TurretState.Disposed;
            if (_ledMaterial != null) Object.Destroy(_ledMaterial);
            _ledMaterial = null;

            var handler = Disposed;
            Disposed = null;
            handler?.Invoke(this);
        }

        // ------------------------------------------------------------------ payload

        /// <summary>Turret shots fly with the Turret look; the MATCH ball gets its standard look back once the flight ends.</summary>
        private sealed class TurretShotPayload : BallPayloadBase
        {
            public override void OnEnded(DodgeBall ball)
            {
                if (ball != null && !ball.IsAbilityBall && ball.Style == BallStyle.Turret) ball.SetStyle(BallStyle.Standard);
            }
        }
    }
}
