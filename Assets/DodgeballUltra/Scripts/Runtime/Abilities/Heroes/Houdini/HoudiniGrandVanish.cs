using System;
using System.Collections.Generic;
using DodgeballUltra.Audio;
using DodgeballUltra.Combat;
using DodgeballUltra.Juice;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Houdini ultimate [Grand Vanish]: the great disappearing act.
    /// <list type="number">
    /// <item>Every ball currently held by an enemy (infield or outfield) vanishes from their hands in a puff of smoke:
    /// the charge/hold is cancelled, the ball is released and <see cref="DodgeBall.Despawn"/>ed for 4 s, after which it
    /// reappears exactly on the centre line (spread across the court width like the opening rush, but neutral: it
    /// belongs to whichever team gets there first).</item>
    /// <item>Every Free ball on the court is teleported (<see cref="DodgeBall.TeleportTo"/>) to the feet of Houdini's
    /// infield team (Houdini included), distributed round-robin on a small ring in front of each player - inside their
    /// auto-pickup reach and clamped to their own zone, so the balls land in friendly territory.</item>
    /// </list>
    /// VanishSmoke at both ends of every relocation (a vanished ball puffs back into existence when it returns, tracked
    /// through <see cref="DodgeBall.StateChanged"/>), and heavy juice on cast (hitstop, camera trauma, ultimate screen
    /// pulse, crowd gasp).
    /// <para>The ultimate refuses to fire (<see cref="AbilityFailReason.NoTarget"/>, meter kept) when there is nothing to
    /// vanish and nothing to summon.</para>
    /// </summary>
    [Serializable]
    public sealed class HoudiniGrandVanish : AbilityBase
    {
        [Header("Vanish enemy balls")]
        [Tooltip("Seconds the enemy-held balls stay vanished before reappearing on the centre line. Spec: 4 s.")]
        [Range(0.5f, 10f)] public float despawnSeconds = 4f;

        [Tooltip("Height (m) above the floor at which vanished balls reappear (they drop onto the centre line).")]
        [Range(0.1f, 3f)] public float respawnHeight = 0.6f;

        [Header("Summon free balls")]
        [Tooltip("Houdini's own feet also receive balls.")]
        public bool includeHoudini = true;

        [Tooltip("Radius (m) of the ring of balls around each receiving player (keep below the 0.9 m auto-pickup reach).")]
        [Range(0.3f, 2f)] public float ringRadius = 0.7f;

        [Tooltip("Angle (deg) between balls placed around the same player.")]
        [Range(20f, 120f)] public float ringSpacingDegrees = 55f;

        [Header("Presentation")]
        [Tooltip("Tint of the VanishSmoke effects (stage smoke: neutral grey with a faint violet cast).")]
        public Color smokeTint = new Color(0.6f, 0.56f, 0.66f, 1f);

        [Tooltip("Scale of each VanishSmoke puff.")]
        [Range(0.2f, 3f)] public float smokeScale = 0.8f;

        [Tooltip("Hitstop on cast (s, unscaled).")]
        [Range(0f, 0.1f)] public float castHitstop = 0.07f;

        [Tooltip("Camera trauma on cast (0..1).")]
        [Range(0f, 1f)] public float castTrauma = 0.6f;

        [Header("AI")]
        [Tooltip("Bots cast once enemies hold at least this many balls.")]
        [Range(1, 6)] public int aiMinEnemyHeldBalls = 2;

        [NonSerialized] private List<DodgeBall> _enemyHeld;
        [NonSerialized] private List<DodgeBall> _free;
        [NonSerialized] private List<DodgeballPlayer> _receivers;
        [NonSerialized] private List<Vector3> _respawnPoints;
        /// <summary>Vanished balls whose return (Despawned -> back in play) we are watching for the arrival smoke.</summary>
        [NonSerialized] private List<DodgeBall> _awaitingReturn;
        [NonSerialized] private Action<DodgeBall, BallState, BallState> _onVanishedBallStateChanged;

        /// <summary>Parameterless constructor (roster factory / SerializeReference).</summary>
        public HoudiniGrandVanish()
        {
        }

        /// <summary>Balls vanished by the last cast.</summary>
        public int LastVanishedCount { get; private set; }

        /// <summary>Balls summoned to friendly feet by the last cast.</summary>
        public int LastSummonedCount { get; private set; }

        // ------------------------------------------------------------------ hooks

        protected override void OnInitialize()
        {
            // Per-clone containers (MemberwiseClone would otherwise share the template's lists).
            _enemyHeld = new List<DodgeBall>(6);
            _free = new List<DodgeBall>(6);
            _receivers = new List<DodgeballPlayer>(3);
            _respawnPoints = new List<Vector3>(6);
            _awaitingReturn = new List<DodgeBall>(6);
            _onVanishedBallStateChanged = OnVanishedBallStateChanged;
        }

        protected override bool CanActivateCustom(out AbilityFailReason reason)
        {
            reason = AbilityFailReason.None;
            var manager = BallManager.Instance;
            if (manager == null)
            {
                reason = AbilityFailReason.Custom;
                return false;
            }

            // Never burn a full meter on an empty trick: something must be held by an enemy or lying free.
            CountBalls(manager, Owner, out int enemyHeld, out int free);
            if (enemyHeld + free == 0)
            {
                reason = AbilityFailReason.NoTarget;
                return false;
            }
            return true;
        }

        protected override void OnCast()
        {
            var manager = BallManager.Instance;
            LastVanishedCount = 0;
            LastSummonedCount = 0;
            if (manager == null) return;

            // Snapshot first: dropping an enemy's ball momentarily makes it Free, and it must not be summoned.
            _enemyHeld.Clear();
            _free.Clear();
            var balls = manager.MatchBalls;
            for (int i = 0; i < balls.Count; i++)
            {
                var b = balls[i];
                if (b == null || b.IsAbilityBall) continue;
                if (b.State == BallState.Held && b.Holder != null && IsEnemy(b.Holder)) _enemyHeld.Add(b);
                else if (b.State == BallState.Free) _free.Add(b);
            }

            VanishEnemyBalls();
            SummonFreeBalls();
            PlayCastJuice();

            _enemyHeld.Clear();
            _free.Clear();
            _receivers.Clear();
        }

        protected override void OnRoundReset()
        {
            _enemyHeld?.Clear();
            _free?.Clear();
            _receivers?.Clear();
            StopWatchingReturns(); // the ball manager re-places every match ball for the new round
        }

        protected override void OnUnequip() => StopWatchingReturns();

        /// <summary>Scores enemy ball control: best when the enemies are loaded up, bonus when behind.</summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            var manager = BallManager.Instance;
            if (Data == null || manager == null || ctx.Self == null) return 0f;

            CountBalls(manager, ctx.Self, out int enemyHeld, out int free);

            float u = 0f;
            if (enemyHeld >= aiMinEnemyHeldBalls) u = 0.6f + 0.15f * (enemyHeld - aiMinEnemyHeldBalls);
            else if (enemyHeld > 0 && free > 0 && !ctx.HoldingBall) u = 0.35f;
            if (u > 0f && ctx.AlliesInfield < ctx.EnemiesInfield) u += 0.15f;
            return Mathf.Clamp01(u * (0.5f + Data.aiWeight));
        }

        // ------------------------------------------------------------------ effect

        private void VanishEnemyBalls()
        {
            int count = _enemyHeld.Count;
            if (count == 0) return;

            var court = Court.Instance;
            _respawnPoints.Clear();
            if (court != null) court.GetOpeningBallPositions(count, _respawnPoints);

            for (int i = 0; i < count; i++)
            {
                var ball = _enemyHeld[i];
                if (ball == null) continue;
                var holder = ball.Holder;
                Vector3 from = ball.transform.position;

                // Out of their hands: cancel a charge in progress and release the ball cleanly first.
                if (holder != null && holder.Combat != null)
                {
                    if (holder.Combat.IsCharging) holder.Combat.CancelCharge();
                    if (holder.Combat.HeldBall == ball) holder.Combat.DropBall(Vector3.zero);
                }

                Vector3 respawn = RespawnPoint(court, i, count);
                ball.Despawn(despawnSeconds, respawn);
                WatchForReturn(ball);

                VfxManager.Spawn(VfxId.VanishSmoke, from, Quaternion.identity, smokeScale, smokeTint);
                AudioManager.PlayAt(SfxId.Teleport, from, 0.8f, 0.9f);
                LastVanishedCount++;
            }
        }

        /// <summary>
        /// Respawn point <paramref name="index"/> of <paramref name="count"/>: spread across the court width like the opening
        /// rush (<see cref="Court.GetOpeningBallPositions(int, List{Vector3})"/>) but placed exactly ON the centre line, and
        /// <see cref="respawnHeight"/> above the floor so the ball drops back into play.
        /// </summary>
        private Vector3 RespawnPoint(Court court, int index, int count)
        {
            if (court == null) return new Vector3(0f, respawnHeight, 0f);

            float x;
            if (index < _respawnPoints.Count)
            {
                x = _respawnPoints[index].x;
            }
            else
            {
                // Evenly across the centre line.
                float t = (index + 0.5f) / Mathf.Max(1, count);
                x = court.Center.x + (t - 0.5f) * court.width * 0.8f;
            }
            return new Vector3(x, court.FloorY + respawnHeight, court.Center.z);
        }

        /// <summary>Subscribes to <paramref name="ball"/>'s state changes to puff it back into existence when it returns.</summary>
        private void WatchForReturn(DodgeBall ball)
        {
            if (ball.State != BallState.Despawned || _awaitingReturn.Contains(ball)) return;
            ball.StateChanged += _onVanishedBallStateChanged;
            _awaitingReturn.Add(ball);
        }

        private void OnVanishedBallStateChanged(DodgeBall ball, BallState previous, BallState current)
        {
            if (ball == null || current == BallState.Despawned) return;

            // Back in play (normally Free, dropping onto the centre line): the arrival end of the vanishing act.
            ball.StateChanged -= _onVanishedBallStateChanged;
            _awaitingReturn.Remove(ball);
            if (previous != BallState.Despawned) return; // left the despawn some other way (round reset etc.)

            Vector3 at = ball.transform.position;
            VfxManager.Spawn(VfxId.VanishSmoke, at, Quaternion.identity, smokeScale * 0.8f, smokeTint);
            AudioManager.PlayAt(SfxId.Teleport, at, 0.6f, 1.1f);
        }

        private void StopWatchingReturns()
        {
            if (_awaitingReturn == null) return;
            for (int i = 0; i < _awaitingReturn.Count; i++)
            {
                var ball = _awaitingReturn[i];
                if (ball != null) ball.StateChanged -= _onVanishedBallStateChanged;
            }
            _awaitingReturn.Clear();
        }

        /// <summary>Counts match balls held by enemies of <paramref name="self"/> and balls lying free (allocation-free).</summary>
        private static void CountBalls(BallManager manager, DodgeballPlayer self, out int enemyHeld, out int free)
        {
            enemyHeld = 0;
            free = 0;
            var balls = manager.MatchBalls;
            if (balls == null) return;
            for (int i = 0; i < balls.Count; i++)
            {
                var b = balls[i];
                if (b == null || b.IsAbilityBall) continue;
                if (b.State == BallState.Held && b.Holder != null && PlayerRegistry.AreEnemies(self, b.Holder)) enemyHeld++;
                else if (b.State == BallState.Free) free++;
            }
        }

        private void SummonFreeBalls()
        {
            if (_free.Count == 0) return;

            CollectReceivers();
            if (_receivers.Count == 0) return;

            for (int i = 0; i < _free.Count; i++)
            {
                var ball = _free[i];
                if (ball == null || ball.State != BallState.Free) continue;

                var receiver = _receivers[i % _receivers.Count];
                int slot = i / _receivers.Count;
                Vector3 target = FeetSlot(receiver, slot);
                Vector3 from = ball.transform.position;

                ball.TeleportTo(target, Vector3.zero);

                VfxManager.Spawn(VfxId.VanishSmoke, from, Quaternion.identity, smokeScale * 0.8f, smokeTint);
                VfxManager.Spawn(VfxId.VanishSmoke, target, Quaternion.identity, smokeScale * 0.8f, smokeTint);
                LastSummonedCount++;
            }
        }

        /// <summary>Houdini's infield teammates still in play (and Houdini), falling back to Houdini alone.</summary>
        private void CollectReceivers()
        {
            _receivers.Clear();
            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || p.Team != Owner.Team || !p.IsInitialized || !p.gameObject.activeInHierarchy) continue;
                if (p == Owner && !includeHoudini) continue;
                if (!p.IsTargetable) continue; // infield, alive, not mid-elimination
                _receivers.Add(p);
            }
            if (_receivers.Count == 0) _receivers.Add(Owner);
        }

        /// <summary>
        /// Slot <paramref name="slot"/> on a ring in front of <paramref name="player"/>: straight ahead first, then
        /// alternating left/right, clamped to the player's zone and resting on the floor.
        /// </summary>
        private Vector3 FeetSlot(DodgeballPlayer player, int slot)
        {
            int side = slot == 0 ? 0 : ((slot & 1) == 1 ? 1 : -1);
            int step = (slot + 1) / 2;
            float angle = side * step * ringSpacingDegrees;
            Vector3 dir = Quaternion.Euler(0f, angle, 0f) * player.Forward;

            Vector3 p = player.Position + dir * ringRadius;
            p = AbilityUtil.ClampToPlayerZone(player, p);
            Vector3 ground = AbilityUtil.GroundPoint(p);
            ground.y += Core.GameConstants.BallRadius + 0.02f;
            return ground;
        }

        private void PlayCastJuice()
        {
            Vector3 at = Owner.ChestPosition;
            VfxManager.Spawn(VfxId.VanishSmoke, Owner.Position + Vector3.up * 0.9f, Quaternion.identity, smokeScale * 1.8f, smokeTint);
            AudioManager.PlayAt(SfxId.UltimateCast, at, 1f);
            AudioManager.PlayAt(SfxId.Teleport, at, 1f, 0.8f);
            if (LastVanishedCount > 0) AudioManager.PlayAt(SfxId.CrowdGasp, at, 0.7f);

            var juice = JuiceManager.Instance;
            if (juice != null)
            {
                if (castHitstop > 0f) juice.Hitstop(castHitstop, 0.05f);
                if (castTrauma > 0f) juice.AddTrauma(castTrauma, at);
            }

            var local = PlayerRegistry.LocalPlayer;
            bool localInvolved = local != null && (local == Owner || local.Team != Owner.Team);
            ScreenFx.Pulse(ScreenPulse.UltimateCast, localInvolved ? 1f : 0.6f, 0.5f);
        }
    }
}
