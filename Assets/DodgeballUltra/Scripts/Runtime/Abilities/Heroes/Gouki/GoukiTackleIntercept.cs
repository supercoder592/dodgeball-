using System;
using System.Collections.Generic;
using DodgeballUltra.Audio;
using DodgeballUltra.Juice;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Gouki - SKILL [Tackle Intercept] (擒抱攔截): charges forward; deflects flying balls in his path and grabs any enemy he
    /// runs into, hurling them into the outfield (CD 11 s).
    /// <para>Sequence:</para>
    /// <list type="number">
    /// <item><b>Charge</b> (0.55 s at ~11 m/s): direction = the aim, softly snapped onto an enemy inside a small cone.
    /// Gouki's zone confinement is temporarily extended 2 m past the centre line (<see cref="PlayerMotor.SetConfinement"/>)
    /// and centre-line player blockers are ignored for his capsule, so he can burst into the enemy half. A
    /// <see cref="GoukiTackleDeflector"/> rides 1.3 m ahead of him and knocks enemy live balls back toward the enemy side
    /// as Free balls. He is unstoppable: stuns are shaken off and knockback is ignored while charging.</item>
    /// <item><b>Grab</b>: an enemy within 1 m ahead is grabbed (<see cref="IncapacitationReason.Grabbed"/>), fumbles any
    /// held ball and is carried for a split second (hitstop + shake sell the impact).</item>
    /// <item><b>Throw</b>: the victim is hurled over Gouki's shoulder toward the outfield:
    /// <see cref="PlayerHealth.Eliminate"/>(<see cref="EliminationCause.Tackle"/>, Gouki, impulse, point) - elimination
    /// interceptors still apply (Specter's Time Reversal, Chrono's Delayed Impact); invulnerable enemies are not grabbed.
    /// A victim who survives is knocked back and briefly stunned.</item>
    /// <item><b>Return</b>: Gouki is pushed back into his half, then his normal confinement is restored
    /// (<see cref="Court.GetConfinement"/>). If the ability is interrupted while he is across the line, the push-back
    /// continues during the cooldown.</item>
    /// </list>
    /// For full stun immunity the AbilityData should be non-interruptible (<c>interruptible = false</c>).
    /// </summary>
    [Serializable]
    public sealed class GoukiTackleIntercept : AbilityBase, IIncomingHitFilter
    {
        private enum Stage { None, Charging, Carrying, Returning }

        [Header("Charge")]
        [Tooltip("Charge speed (m/s). Spec: ~11 m/s (a heavy sprinter's top speed).")]
        [Range(4f, 20f)] public float chargeSpeed = 11f;

        [Tooltip("Charge time used only when the AbilityData has no duration (spec: 0.55 s).")]
        [Range(0.1f, 2f)] public float fallbackChargeDuration = 0.55f;

        [Tooltip("How far (m) past the centre line Gouki may charge into the enemy half.")]
        [Range(0f, 5f)] public float centreLineOvershoot = 2f;

        [Tooltip("Half-angle (deg) of the cone in which the charge snaps onto an enemy (humans).")]
        [Range(0f, 60f)] public float autoAimAngle = 25f;

        [Tooltip("Half-angle (deg) of the snap cone for bots (they aim less precisely).")]
        [Range(0f, 90f)] public float botAutoAimAngle = 55f;

        [Tooltip("The charge ends early when Gouki's measured speed drops below this fraction (he ran into something).")]
        [Range(0f, 1f)] public float blockedSpeedFraction = 0.3f;

        [Header("Deflect balls in path")]
        [Tooltip("How far (m) ahead of Gouki's chest live balls are deflected. Spec: 1.3 m.")]
        [Range(0.5f, 3f)] public float deflectReach = 1.3f;

        [Tooltip("Radius (m) of the deflection volume.")]
        [Range(0.3f, 1.5f)] public float deflectRadius = 0.75f;

        [Tooltip("Rebound speed (m/s) of a deflected ball (it becomes a Free ball).")]
        [Range(1f, 25f)] public float deflectSpeed = 9f;

        [Tooltip("Upward share (0..1) of the rebound direction.")]
        [Range(0f, 0.9f)] public float deflectLift = 0.3f;

        [Header("Grab & throw")]
        [Tooltip("Enemies closer than this (m, planar) are grabbed. Spec: < 1 m.")]
        [Range(0.3f, 2f)] public float grabRadius = 1f;

        [Tooltip("Maximum height difference (m) for a grab (enemies jumping clear escape).")]
        [Range(0.3f, 2.5f)] public float maxGrabHeight = 1.1f;

        [Tooltip("Maximum enemies grabbed in one charge.")]
        [Range(1, 3)] public int maxGrabs = 2;

        [Tooltip("Seconds the victim is carried before being thrown.")]
        [Range(0f, 1f)] public float grabCarryTime = 0.2f;

        [Tooltip("Distance (m) in front of Gouki the victim is held.")]
        [Range(0.4f, 1.5f)] public float carryDistance = 0.8f;

        [Tooltip("Height (m) the victim is lifted off the floor while carried.")]
        [Range(0f, 0.6f)] public float carryLift = 0.25f;

        [Tooltip("Throw the victim over Gouki's shoulder (toward the outfield behind his baseline) instead of shoving them forward.")]
        public bool throwOverShoulder = true;

        [Tooltip("Horizontal impulse (N*s) handed to the victim's ragdoll (~75 kg body).")]
        [Range(0f, 1500f)] public float throwImpulseHorizontal = 320f;

        [Tooltip("Vertical impulse (N*s) handed to the victim's ragdoll.")]
        [Range(0f, 1500f)] public float throwImpulseVertical = 260f;

        [Tooltip("Knockback (m/s) for a victim whose elimination was prevented or delayed.")]
        [Range(0f, 12f)] public float survivorKnockback = 4f;

        [Tooltip("Stun (s) for a victim whose elimination was prevented or delayed.")]
        [Range(0f, 3f)] public float survivorStun = 0.8f;

        [Tooltip("Speed (m/s) of the ball a grabbed enemy fumbles.")]
        [Range(0f, 8f)] public float fumbleSpeed = 2f;

        [Header("Recovery")]
        [Tooltip("Gouki's speed (m/s) right after the charge.")]
        [Range(0f, 8f)] public float postChargeSpeed = 2f;

        [Tooltip("Speed (m/s) at which Gouki is pushed back into his half.")]
        [Range(1f, 12f)] public float returnSpeed = 5.5f;

        [Tooltip("After this many seconds of push-back he is placed inside his half directly.")]
        [Range(0.1f, 3f)] public float maxReturnTime = 0.8f;

        [Tooltip("How far (m) inside his half Gouki must be before his confinement is restored.")]
        [Range(0f, 1.5f)] public float insideMargin = 0.35f;

        [Header("Presentation")]
        [Tooltip("Seconds between dust kicks while charging.")]
        [Range(0.02f, 0.5f)] public float dustInterval = 0.08f;

        [Tooltip("Camera trauma (0..1) on a grab.")]
        [Range(0f, 1f)] public float impactTrauma = 0.45f;

        [Tooltip("Hitstop (s) on a grab (clamped to 0.03-0.1 s by the juice pipeline).")]
        [Range(0f, 0.1f)] public float impactHitstop = 0.06f;

        [Tooltip("Hit-filter priority while charging (knockback immunity).")]
        public int filterPriority = 250;

        [NonSerialized] private Stage _stage;
        [NonSerialized] private Vector3 _dir;
        [NonSerialized] private float _stageTime;
        [NonSerialized] private float _chargeDuration;
        [NonSerialized] private float _dustTimer;
        [NonSerialized] private Vector3 _lastPosition;
        [NonSerialized] private float _returnTimer;
        [NonSerialized] private bool _pendingReturn;
        [NonSerialized] private bool _confinementExtended;
        [NonSerialized] private bool _speedModifierActive;
        [NonSerialized] private PlayerHealth _filterOn;
        [NonSerialized] private GoukiTackleDeflector _deflector;
        [NonSerialized] private InterruptReason _interruptReason;
        [NonSerialized] private bool _hasInterruptReason;
        [NonSerialized] private List<DodgeballPlayer> _victims;
        [NonSerialized] private List<Collider> _ignoredBlockers;

        private static readonly Collider[] s_overlap = new Collider[16];

        /// <summary>Required public parameterless constructor (SerializeReference / roster factory).</summary>
        public GoukiTackleIntercept() { }

        /// <summary>True while charging or carrying (unstoppable window).</summary>
        public bool IsCharging => _stage == Stage.Charging || _stage == Stage.Carrying;

        /// <inheritdoc />
        public int Priority => filterPriority;

        protected override void OnInitialize()
        {
            _victims = new List<DodgeballPlayer>(3);
            _ignoredBlockers = new List<Collider>(4);
        }

        protected override void OnUnequip()
        {
            ReleaseVictims();
            StopCharge();
            FinishReturn();
            _pendingReturn = false;
            _stage = Stage.None;
        }

        protected override void OnCast()
        {
            _hasInterruptReason = false;
            _pendingReturn = false;
            _victims.Clear();
            _chargeDuration = Duration > 0f ? Duration : fallbackChargeDuration;
            _dir = ChooseChargeDirection();

            var combat = Owner.Combat;
            if (combat != null)
            {
                if (combat.IsCharging) combat.CancelCharge();
                if (combat.IsCatchArmed) combat.CancelCatch();
            }

            ExtendConfinement();
            SetCentreBlockersIgnored(true);

            var motor = Owner.Motor;
            if (motor != null)
            {
                if (motor.IsSliding) motor.EndSlide();
                motor.SetMode(MovementMode.Sprint);
                motor.SetSpeedModifier(this, chargeSpeed / Mathf.Max(1f, motor.Profile.sprintSpeed));
                _speedModifierActive = true;
                motor.SetFacing(_dir, true);
                motor.SetPlanarVelocity(_dir * chargeSpeed);
            }

            if (Owner.Health != null)
            {
                Owner.Health.AddHitFilter(this);
                _filterOn = Owner.Health;
            }

            _deflector = GoukiTackleDeflector.Create(Owner, _dir, deflectReach, deflectRadius, deflectSpeed, deflectLift);

            _stage = Stage.Charging;
            _stageTime = 0f;
            _dustTimer = 0f;
            _lastPosition = Owner.Position;
            HoldActive();

            Vector3 feet = Owner.Position;
            VfxManager.Spawn(VfxId.TackleDust, feet, Quaternion.LookRotation(-_dir, Vector3.up), 1.1f);
            AudioManager.PlayAt(SfxId.ThrowHeavy, Owner.ChestPosition, 1f, 0.72f);
            AudioManager.PlayAt(SfxId.Land, feet, 0.8f, 0.8f);
        }

        protected override void OnTick(float deltaTime)
        {
            switch (_stage)
            {
                case Stage.Charging:
                    _stageTime += deltaTime;
                    ShrugOffStun();
                    DriveCharge(deltaTime);
                    CheckGrabs();
                    if (_victims.Count > 0)
                    {
                        _stage = Stage.Carrying;
                        _stageTime = 0f;
                        break;
                    }
                    if (_stageTime >= _chargeDuration || IsBlocked(deltaTime))
                    {
                        StopCharge();
                        BeginReturn();
                    }
                    break;

                case Stage.Carrying:
                    _stageTime += deltaTime;
                    ShrugOffStun();
                    CarryVictims();
                    if (_stageTime >= grabCarryTime)
                    {
                        ThrowVictims();
                        StopCharge();
                        BeginReturn();
                    }
                    break;

                case Stage.Returning:
                    if (TickReturn(deltaTime))
                    {
                        FinishReturn();
                        _stage = Stage.None;
                        EndAbility();
                    }
                    break;

                default:
                    EndAbility();
                    break;
            }
        }

        protected override void OnInterrupt(InterruptReason reason)
        {
            _interruptReason = reason;
            _hasInterruptReason = true;
        }

        protected override void OnEnd(bool interrupted)
        {
            ReleaseVictims();
            StopCharge();
            _stage = Stage.None;

            bool hardStop = !interrupted || !_hasInterruptReason ||
                            _interruptReason == InterruptReason.Eliminated ||
                            _interruptReason == InterruptReason.RoundEnded ||
                            _interruptReason == InterruptReason.Replaced;
            _hasInterruptReason = false;

            if (!hardStop && Owner != null && Owner.IsInfield && DepthIntoOwnHalf(Owner.Position) < insideMargin)
            {
                // Interrupted across the line (e.g. frozen): keep pushing him home while the cooldown runs.
                _pendingReturn = true;
                _returnTimer = 0f;
                return;
            }
            FinishReturn();
        }

        protected override void OnCooldownTick(float deltaTime)
        {
            if (!_pendingReturn) return;
            if (TickReturn(deltaTime))
            {
                _pendingReturn = false;
                FinishReturn();
            }
        }

        protected override void OnRoundReset()
        {
            _pendingReturn = false;
            ReleaseVictims();
            StopCharge();
            FinishReturn();
            _stage = Stage.None;
        }

        /// <summary>Unstoppable charge: no knockback while charging or carrying.</summary>
        public void FilterHit(ref HitContext hit)
        {
            if (hit.Cancelled || hit.Victim != Owner || !IsCharging) return;
            hit.KnockbackImpulse = 0f;
        }

        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || ctx.Self == null || !ctx.Self.IsInfield) return 0f;
            float w = Data.aiWeight;
            float duration = Duration > 0f ? Duration : fallbackChargeDuration;
            float reach = chargeSpeed * duration + grabRadius;

            // Best case: an enemy close to the centre line, within reach -> grab & throw.
            var court = Court.Instance;
            float best = 0f;
            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || !IsEnemy(p) || !p.IsTargetable) continue;
                if (p.Status != null && p.Status.Has(StatusEffectType.Invulnerable)) continue;
                Vector3 d = p.Position - ctx.Self.Position;
                d.y = 0f;
                float dist = d.magnitude;
                if (dist > reach) continue;
                if (court != null && ctx.Self.Team.IsValid())
                {
                    // How deep the enemy stands inside their own half (they must be within Gouki's overshoot).
                    float enemyDepth = Vector3.Dot(p.Position - court.Center, court.AttackDirection(ctx.Self.Team));
                    if (enemyDepth > centreLineOvershoot + grabRadius * 0.5f) continue;
                }
                best = Mathf.Max(best, w * (1f - 0.35f * dist / reach));
            }
            if (best > 0f) return Mathf.Clamp01(best);

            // Fallback: bulldoze an incoming ball about to hit (deflects it).
            if (ctx.IncomingBall != null && ctx.IncomingTimeToImpact < 0.35f) return w * 0.4f;
            return 0f;
        }

        // ------------------------------------------------------------------ charge

        private Vector3 ChooseChargeDirection()
        {
            Vector3 aim = ScrewsGadgetKit.Planar(Owner.Intent.AimDirection, Owner.Forward);
            float cone = Owner.IsHumanControlled ? autoAimAngle : botAutoAimAngle;
            if (cone <= 0f) return aim;

            float duration = Duration > 0f ? Duration : fallbackChargeDuration;
            float reach = chargeSpeed * duration + grabRadius;
            DodgeballPlayer best = null;
            float bestScore = float.MaxValue;
            Vector3 bestDir = aim;
            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || !IsEnemy(p) || !p.IsTargetable) continue;
                Vector3 d = p.Position - Owner.Position;
                d.y = 0f;
                float dist = d.magnitude;
                if (dist < 0.05f || dist > reach) continue;
                float angle = Vector3.Angle(aim, d);
                if (angle > cone) continue;
                float score = angle / cone + dist / reach;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = p;
                    bestDir = d / dist;
                }
            }
            return best != null ? bestDir : aim;
        }

        private void DriveCharge(float deltaTime)
        {
            var motor = Owner.Motor;
            if (motor != null)
            {
                // Abilities tick after the state machine, so these commands win this frame.
                motor.SetMode(MovementMode.Sprint);
                motor.SetMoveInput(_dir, 1f);
                motor.SetFacing(_dir, true);
                motor.SetPlanarVelocity(_dir * chargeSpeed);
            }
            if (_deflector != null) _deflector.SetDirection(_dir);

            _dustTimer -= deltaTime;
            if (_dustTimer <= 0f)
            {
                _dustTimer = dustInterval;
                VfxManager.Spawn(VfxId.TackleDust, Owner.Position, Quaternion.LookRotation(-_dir, Vector3.up), 0.55f);
            }
        }

        /// <summary>True when Gouki ran into something (measured speed collapsed) after the first few frames.</summary>
        private bool IsBlocked(float deltaTime)
        {
            Vector3 pos = Owner.Position;
            Vector3 moved = pos - _lastPosition;
            _lastPosition = pos;
            moved.y = 0f;
            if (_stageTime < 0.15f || deltaTime <= 1e-5f) return false;
            return moved.magnitude / deltaTime < chargeSpeed * blockedSpeedFraction;
        }

        private void CheckGrabs()
        {
            if (_victims.Count >= maxGrabs) return;
            var players = PlayerRegistry.All;
            float r2 = grabRadius * grabRadius;
            for (int i = 0; i < players.Count && _victims.Count < maxGrabs; i++)
            {
                var p = players[i];
                if (p == null || !IsEnemy(p) || !p.IsTargetable || _victims.Contains(p)) continue;
                if (p.Status != null && p.Status.Has(StatusEffectType.Invulnerable)) continue; // evading: slips the grab
                Vector3 d = p.Position - Owner.Position;
                if (Mathf.Abs(d.y) > maxGrabHeight) continue;
                d.y = 0f;
                if (d.sqrMagnitude > r2) continue;
                if (Vector3.Dot(d, _dir) < -0.2f) continue; // must be in front of him
                Grab(p);
            }
        }

        private void Grab(DodgeballPlayer victim)
        {
            _victims.Add(victim);
            if (victim.StateMachine != null) victim.StateMachine.Incapacitate(IncapacitationReason.Grabbed, grabCarryTime + 0.6f);

            var combat = victim.Combat;
            if (combat != null)
            {
                if (combat.IsCharging) combat.CancelCharge();
                if (combat.IsCatchArmed) combat.CancelCatch();
                if (combat.HasBall) combat.DropBall(_dir * fumbleSpeed + Vector3.up * 2f);
            }

            Vector3 contact = Vector3.Lerp(Owner.ChestPosition, victim.ChestPosition, 0.5f);
            VfxManager.Spawn(VfxId.HitImpact, contact, Quaternion.LookRotation(-_dir, Vector3.up), 1.2f);
            VfxManager.Spawn(VfxId.TackleDust, victim.Position, Quaternion.LookRotation(_dir, Vector3.up), 1f);
            AudioManager.PlayAt(SfxId.BallHitHeavy, contact, 1f, 0.62f);

            var juice = JuiceManager.Instance;
            if (juice != null)
            {
                if (impactHitstop > 0f) juice.Hitstop(impactHitstop);
                if (impactTrauma > 0f) juice.AddTrauma(impactTrauma, contact);
            }

            var ik = Owner.Visual != null ? Owner.Visual.IK : null;
            if (ik != null) ik.OverrideLookTarget(victim.HeadPosition, grabCarryTime + 0.3f);
        }

        private void CarryVictims()
        {
            var motor = Owner.Motor;
            if (motor != null)
            {
                motor.SetMoveInput(_dir, 0f);
                motor.SetPlanarVelocity(_dir * postChargeSpeed);
            }

            Vector3 right = Vector3.Cross(Vector3.up, _dir);
            Quaternion facing = Quaternion.LookRotation(-_dir, Vector3.up);
            for (int i = 0; i < _victims.Count; i++)
            {
                var v = _victims[i];
                if (v == null || v.Health == null || !v.Health.IsAlive) continue;
                float lateral = _victims.Count > 1 ? (i - (_victims.Count - 1) * 0.5f) * 0.55f : 0f;
                Vector3 hold = Owner.Position + _dir * carryDistance + right * lateral + Vector3.up * carryLift;
                v.Teleport(hold, facing);
            }
        }

        private void ThrowVictims()
        {
            Vector3 throwDir = throwOverShoulder ? -_dir : _dir;
            for (int i = 0; i < _victims.Count; i++)
            {
                var v = _victims[i];
                if (v == null) continue;
                if (v.StateMachine != null) v.StateMachine.ReleaseIncapacitation(IncapacitationReason.Grabbed);
                if (v.Health == null || !v.Health.IsAlive || !v.IsInfield) continue;
                if (v.Status != null && v.Status.Has(StatusEffectType.Invulnerable)) continue;

                Vector3 impulse = throwDir * throwImpulseHorizontal + Vector3.up * throwImpulseVertical;
                Vector3 point = v.ChestPosition;
                var outcome = v.Health.Eliminate(EliminationCause.Tackle, Owner, impulse, point);
                if (outcome != HitOutcome.Eliminated)
                {
                    // Elimination prevented/delayed (Time Reversal, Delayed Impact): still a brutal shove.
                    if (v.Motor != null) v.Motor.AddImpulse(throwDir * survivorKnockback + Vector3.up * (survivorKnockback * 0.5f));
                    if (v.StateMachine != null && survivorStun > 0f && v.Health.IsAlive) v.StateMachine.Stun(survivorStun);
                }
                VfxManager.Spawn(VfxId.TackleDust, v.Position, Quaternion.LookRotation(throwDir, Vector3.up), 1.3f);
            }
            _victims.Clear();

            var juice = JuiceManager.Instance;
            if (juice != null && impactTrauma > 0f) juice.AddTrauma(impactTrauma * 0.6f, Owner.Position);
        }

        /// <summary>Lets go of anyone still held (interruptions) without throwing them.</summary>
        private void ReleaseVictims()
        {
            if (_victims == null) return;
            for (int i = 0; i < _victims.Count; i++)
            {
                var v = _victims[i];
                if (v != null && v.StateMachine != null) v.StateMachine.ReleaseIncapacitation(IncapacitationReason.Grabbed);
            }
            _victims.Clear();
        }

        /// <summary>Ends the unstoppable charge (speed modifier, knockback filter, ball guard).</summary>
        private void StopCharge()
        {
            if (_speedModifierActive && Owner != null && Owner.Motor != null)
            {
                Owner.Motor.RemoveSpeedModifier(this);
                if (IsCharging) Owner.Motor.SetPlanarVelocity(_dir * postChargeSpeed);
            }
            _speedModifierActive = false;

            if (_filterOn != null) _filterOn.RemoveHitFilter(this);
            _filterOn = null;

            if (_deflector != null) _deflector.Shutdown();
            _deflector = null;

            if (_stage == Stage.Charging || _stage == Stage.Carrying) _stage = Stage.None;
        }

        private void ShrugOffStun()
        {
            var sm = Owner.StateMachine;
            if (sm != null && sm.IsIn(PlayerStateId.Stunned)) sm.ResetToGrounded();
            if (Owner.Status != null && Owner.Status.Has(StatusEffectType.Stunned)) Owner.Status.Remove(StatusEffectType.Stunned);
        }

        // ------------------------------------------------------------------ zone confinement

        private void BeginReturn()
        {
            _stage = Stage.Returning;
            _returnTimer = 0f;
        }

        /// <summary>Pushes Gouki back into his half. Returns true once he is inside (or cannot be moved).</summary>
        private bool TickReturn(float deltaTime)
        {
            var court = Court.Instance;
            if (court == null || Owner == null || !Owner.IsInfield || !Owner.Team.IsValid()) return true;
            if (Owner.Health != null && !Owner.Health.IsAlive) return true;

            float depth = DepthIntoOwnHalf(Owner.Position);
            if (depth >= insideMargin) return true;

            Vector3 home = -ScrewsGadgetKit.Planar(court.AttackDirection(Owner.Team), -Owner.Forward);
            _returnTimer += deltaTime;
            if (_returnTimer >= maxReturnTime)
            {
                // Could not walk back (frozen, blocked): place him just inside his half.
                Owner.Teleport(Owner.Position + home * (insideMargin - depth + 0.05f), Owner.Rotation);
                return true;
            }

            var motor = Owner.Motor;
            if (motor != null)
            {
                motor.SetMoveInput(home, 1f);
                motor.SetPlanarVelocity(home * returnSpeed);
            }
            return false;
        }

        private void FinishReturn()
        {
            RestoreConfinement();
            SetCentreBlockersIgnored(false);
        }

        /// <summary>Signed distance (m) from the centre line into Gouki's own half (negative = in the enemy half).</summary>
        private float DepthIntoOwnHalf(Vector3 position)
        {
            var court = Court.Instance;
            if (court == null || Owner == null || !Owner.Team.IsValid()) return float.PositiveInfinity;
            return Vector3.Dot(position - court.Center, -court.AttackDirection(Owner.Team));
        }

        private void ExtendConfinement()
        {
            var court = Court.Instance;
            if (court == null || Owner.Motor == null || !Owner.IsInfield || !Owner.Team.IsValid()) return;

            Bounds bounds = court.GetConfinement(Owner.Team, CourtZone.Infield);
            Vector3 attack = court.AttackDirection(Owner.Team);
            // Half-size of the axis-aligned box projected on the attack axis -> the face on the centre line.
            float extent = Mathf.Abs(attack.x) * bounds.extents.x + Mathf.Abs(attack.z) * bounds.extents.z;
            Vector3 face = bounds.center + attack * extent;
            bounds.Encapsulate(face + attack * centreLineOvershoot);
            Owner.Motor.SetConfinement(bounds);
            _confinementExtended = true;
        }

        private void RestoreConfinement()
        {
            if (!_confinementExtended) return;
            _confinementExtended = false;
            var court = Court.Instance;
            if (court == null || Owner == null || Owner.Motor == null || !Owner.Team.IsValid()) return;
            Owner.Motor.SetConfinement(court.GetConfinement(Owner.Team, Owner.Zone));
        }

        /// <summary>(Un)ignores collisions between Gouki's capsule and the invisible centre-line player blockers.</summary>
        private void SetCentreBlockersIgnored(bool ignore)
        {
            var capsule = Owner != null ? Owner.Capsule : null;
            if (!ignore)
            {
                for (int i = 0; i < _ignoredBlockers.Count; i++)
                {
                    var c = _ignoredBlockers[i];
                    if (c != null && capsule != null) Physics.IgnoreCollision(capsule, c, false);
                }
                _ignoredBlockers.Clear();
                return;
            }

            var court = Court.Instance;
            if (court == null || capsule == null) return;
            var halfExtents = new Vector3(court.width * 0.5f + court.runOff, 3f, Mathf.Max(0.5f, centreLineOvershoot));
            int count = Physics.OverlapBoxNonAlloc(court.Center + Vector3.up * 1.5f, halfExtents, s_overlap, Quaternion.identity,
                GameLayers.PlayerBlockerMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var c = s_overlap[i];
                s_overlap[i] = null;
                if (c == null || _ignoredBlockers.Contains(c)) continue;
                Physics.IgnoreCollision(capsule, c, true);
                _ignoredBlockers.Add(c);
            }
        }
    }
}
