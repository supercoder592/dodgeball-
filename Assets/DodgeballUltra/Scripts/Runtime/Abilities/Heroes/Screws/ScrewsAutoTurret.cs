using System;
using System.Collections.Generic;
using DodgeballUltra.Combat;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Screws - ULTIMATE [Auto-Turret] (自動砲台): deploys a turret for 8 s (<see cref="AbilityData.duration"/>) 1.5 m in
    /// front of Screws, inside his own zone. It automatically collects free balls within 4 m (magnetic hopper, 3 balls) and
    /// fires a stored match ball at the nearest enemy every 1.5 s (~95 km/h, hits credit Screws). Its body blocks enemy
    /// throws.
    /// <para>
    /// OnCast places a <see cref="ScrewsAutoTurretUnit"/> on the floor (<see cref="AbilityUtil.ClampToPlayerZone"/> +
    /// <see cref="AbilityUtil.GroundPoint"/>) facing Screws' forward and plays an underhand toss on his arm. The ability stays
    /// Active for the duration; OnEnd (timeout, elimination, round end) shuts the unit down - stored balls drop out as free
    /// balls and it folds away. Round reset / unequip dispose it at once. If the unit disappears early the ability ends.
    /// </para>
    /// <para>
    /// AI: worth more the more loose balls it can feed on around Screws; useless without enemies left on the court.
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class ScrewsAutoTurret : AbilityBase
    {
        [Header("Placement")]
        [Tooltip("Distance (m) in front of Screws where the turret is deployed. Spec: 1.5 m.")]
        [Range(0.5f, 4f)] public float placeDistance = 1.5f;

        [Header("Collection")]
        [Tooltip("Free match balls within this radius (m) are pulled into the hopper. Spec: 4 m.")]
        [Range(1f, 10f)] public float collectRadius = 4f;

        [Tooltip("Balls the hopper can hold.")]
        [Range(1, 3)] public int capacity = 3;

        [Tooltip("Maximum speed (m/s) of balls lifted into the hopper.")]
        [Range(1f, 15f)] public float magnetSpeed = 6.5f;

        [Tooltip("Balls higher than this above the floor (m) are ignored.")]
        [Range(0.5f, 6f)] public float maxCollectHeight = 2.5f;

        [Header("Firing")]
        [Tooltip("Seconds between shots. Spec: 1.5 s.")]
        [Range(0.3f, 5f)] public float fireInterval = 1.5f;

        [Tooltip("Muzzle speed (km/h). Spec: ~95 km/h.")]
        [Range(40f, 200f)] public float shotSpeedKmh = 95f;

        [Tooltip("Target acquisition range (m).")]
        [Range(5f, 60f)] public float range = 30f;

        [Tooltip("Seconds after going live before the first shot.")]
        [Range(0f, 3f)] public float firstShotDelay = 0.4f;

        [Tooltip("Yaw servo speed (deg/s).")]
        [Range(30f, 1080f)] public float yawRate = 345f;

        [Tooltip("Pitch servo speed (deg/s).")]
        [Range(30f, 720f)] public float pitchRate = 230f;

        [Tooltip("Aim error (deg) under which the turret fires.")]
        [Range(0.5f, 30f)] public float aimTolerance = 6f;

        [Header("Mechanics")]
        [Tooltip("Seconds (scaled) the tripod takes to unfold.")]
        [Range(0.05f, 2f)] public float deployTime = 0.55f;

        [Tooltip("Seconds (unscaled) the unit takes to fold away.")]
        [Range(0.05f, 2f)] public float foldTime = 0.45f;

        [Header("AI")]
        [Tooltip("Utility bonus per loose ball near Screws (up to three).")]
        [Range(0f, 0.5f)] public float aiPerLooseBall = 0.22f;

        [NonSerialized] private ScrewsAutoTurretUnit _unit;

        /// <summary>Required public parameterless constructor (SerializeReference / roster factory).</summary>
        public ScrewsAutoTurret() { }

        /// <summary>The deployed unit (null when none).</summary>
        public ScrewsAutoTurretUnit Unit => _unit;

        // ------------------------------------------------------------------ lifecycle

        protected override void OnCast()
        {
            DisposeUnit(true);
            var owner = Owner;
            if (owner == null) return;

            Vector3 forward = owner.Forward;
            Vector3 spot = AbilityUtil.ClampToPlayerZone(owner, owner.Position + forward * placeDistance); // inside his zone
            spot = AbilityUtil.GroundPoint(spot);
            float yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;

            _unit = ScrewsAutoTurretUnit.Deploy(owner, spot, yaw, BuildSettings());

            // He tosses the folded unit onto the floor: a short underhand arc of the throwing arm.
            var ik = owner.Visual != null ? owner.Visual.IK : null;
            if (ik != null) ik.PlayPassRelease(forward);
        }

        protected override void OnTick(float deltaTime)
        {
            // The unit vanished early (round flow, destroyed): the ultimate is over.
            if (_unit == null || _unit.State == ScrewsAutoTurretUnit.TurretState.Disposed)
            {
                _unit = null;
                EndAbility();
            }
        }

        protected override void OnEnd(bool interrupted)
        {
            // Timeout, elimination, round end: drop the stored balls, fold, and let the unit destroy itself.
            if (_unit != null) _unit.Shutdown();
            _unit = null;
        }

        protected override void OnRoundReset() => DisposeUnit(true);

        protected override void OnUnequip() => DisposeUnit(true);

        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            var self = ctx.Self != null ? ctx.Self : Owner;
            if (Data == null || self == null || !self.IsInfield || ctx.EnemiesInfield <= 0) return 0f;

            // Loose balls it can feed on: around the spot it will stand (Screws + placement + collection radius).
            int loose = 0;
            BallManager manager = BallManager.Instance;
            if (manager != null)
            {
                float reach = collectRadius + placeDistance + 1f;
                float reachSqr = reach * reach;
                Vector3 origin = self.Position;
                IReadOnlyList<DodgeBall> balls = manager.MatchBalls;
                for (int i = 0; i < balls.Count && loose < 3; i++)
                {
                    DodgeBall b = balls[i];
                    if (b == null || b.State != BallState.Free || b.IsAbilityBall) continue;
                    Vector3 d = b.transform.position - origin;
                    d.y = 0f;
                    if (d.sqrMagnitude <= reachSqr) loose++;
                }
            }
            else
            {
                loose = Mathf.Min(3, ctx.FreeBallsNearby);
            }

            float w = Data.aiWeight;
            float utility = w * (0.35f + aiPerLooseBall * loose);
            if (ctx.HoldingBall) utility *= 0.85f; // his own ball is a throw the turret does not need to make
            return Mathf.Clamp01(utility);
        }

        // ------------------------------------------------------------------ helpers

        private ScrewsAutoTurretSettings BuildSettings()
        {
            var s = ScrewsAutoTurretSettings.Default;
            s.CollectRadius = collectRadius;
            s.Capacity = capacity;
            s.MagnetSpeed = magnetSpeed;
            s.MaxCollectHeight = maxCollectHeight;
            s.FireInterval = fireInterval;
            s.ShotSpeedKmh = shotSpeedKmh;
            s.Range = range;
            s.FirstShotDelay = firstShotDelay;
            s.YawRate = yawRate;
            s.PitchRate = pitchRate;
            s.AimTolerance = aimTolerance;
            s.DeployTime = deployTime;
            s.FoldTime = foldTime;
            // Stored balls must come back by themselves long after the turret is gone, never before.
            s.StoreSafety = Mathf.Max(15f, Duration + foldTime + 5f);
            return s;
        }

        private void DisposeUnit(bool releaseBalls)
        {
            if (_unit != null) _unit.Dispose(releaseBalls);
            _unit = null;
        }
    }
}
