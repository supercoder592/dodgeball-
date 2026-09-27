using System;
using System.Collections.Generic;
using DodgeballUltra.Audio;
using DodgeballUltra.Events;
using DodgeballUltra.Juice;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;
using Random = UnityEngine.Random;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Shadow's ultimate <b>[Mirage Formation]</b>: "clones all living teammates simultaneously for 5 s, obscuring real targets".
    /// <para>
    /// Every living infield teammate (and Shadow) gets <see cref="clonesPerPlayer"/> animated <see cref="ShadowClone"/>s and
    /// <see cref="StatusEffectType.Obscured"/> for the ability's duration (AbilityData.duration, spec 5 s). Each player and
    /// their clones stand in a line of formation slots across the court (perpendicular to the attack direction):
    /// <code>
    ///   slot:      0        1        2
    ///            [clone]  [REAL]  [clone]        (slots 1.4 m apart, the real player may occupy any slot)
    /// </code>
    /// <b>Shell game.</b> Every ~1.2 s (jittered, staggered per player) the real player swaps slots with one of their
    /// clones: the real body side-steps into the clone's slot while the clone runs the other way on an arc, so their paths
    /// cross. All bodies are the same realistic model with the same mirrored animation, so after the swap the real target
    /// has changed places in the line. When a player cannot be moved (stunned, airborne, sliding, frozen...) two clones
    /// swap instead, keeping the motion going without touching the player. The side-step only overrides the lateral
    /// velocity for <see cref="transitDuration"/> s; forward/back control is never taken away, and every position is
    /// clamped to the player's own zone.
    /// </para>
    /// <para>Clones mirror their player's throws (illusion balls) and catches, and pop when an enemy ball touches them.</para>
    /// </summary>
    [Serializable]
    public sealed class ShadowMirageFormation : AbilityBase
    {
        /// <summary>Used only if AbilityData.duration is left at 0 (misconfigured asset): the spec duration.</summary>
        private const float FallbackDuration = 5f;

        [Header("Formation")]
        [Tooltip("Clones per player. Spec: 2.")]
        [Range(1, 3)] public int clonesPerPlayer = 2;

        [Tooltip("Distance (m) between neighbouring formation slots.")]
        [Range(0.8f, 3f)] public float slotSpacing = 1.4f;

        [Tooltip("How tightly clones hold their slot while idle (1/s). Lower = more lag.")]
        [Range(1f, 30f)] public float followSharpness = 10f;

        [Tooltip("How tightly clones track their crossing path during a shuffle (1/s).")]
        [Range(5f, 60f)] public float transitSharpness = 28f;

        [Header("Shell game")]
        [Tooltip("Average seconds between two shuffles of the same player.")]
        [Range(0.4f, 4f)] public float shuffleInterval = 1.2f;

        [Tooltip("Random +- fraction applied to each interval so shuffles are not predictable.")]
        [Range(0f, 0.9f)] public float shuffleJitter = 0.3f;

        [Tooltip("Duration (s) of one swap (paths crossing).")]
        [Range(0.15f, 1.5f)] public float transitDuration = 0.45f;

        [Tooltip("Forward/back bulge (m) of the swapping clone's path so the bodies cross instead of passing through each other.")]
        [Range(0f, 1.5f)] public float crossingArc = 0.7f;

        [Tooltip("Real players side-step into a clone's slot during a swap. Off = only clones swap among themselves.")]
        public bool moveRealPlayers = true;

        [Header("Mimicry")]
        [Tooltip("Life (s) of the illusion balls clones throw when their player throws.")]
        [Range(0.1f, 2f)] public float illusionBallLifetime = 0.6f;

        [Tooltip("Clones mirror their player's catch attempts.")]
        public bool mirrorCatches = true;

        /// <summary>One obscured player and their clones.</summary>
        private sealed class MirageGroup
        {
            public DodgeballPlayer Player;
            public ShadowClone[] Clones;
            public int[] CloneSlot;      // current (target) slot of each clone
            public int[] FromSlot;       // slot at the start of the running swap
            public int[] ArcSign;        // -1 / 0 / +1: side of the crossing bulge during a swap
            public int PlayerSlot;
            public int SlotCount;
            public float NextShuffleIn;
            public bool InTransit;
            public float TransitT;
            public bool MovePlayer;
            public int FromPlayerSlot;
            public int ToPlayerSlot;
            public bool Active;

            public MirageGroup(DodgeballPlayer player, int clones)
            {
                Player = player;
                Clones = new ShadowClone[clones];
                CloneSlot = new int[clones];
                FromSlot = new int[clones];
                ArcSign = new int[clones];
                SlotCount = clones + 1;
                Active = true;
            }
        }

        [NonSerialized] private List<MirageGroup> _groups;
        [NonSerialized] private List<DodgeballPlayer> _teamBuffer;

        public ShadowMirageFormation() { }

        /// <summary>Number of players currently obscured by this ultimate.</summary>
        public int ObscuredPlayerCount
        {
            get
            {
                if (_groups == null) return 0;
                int n = 0;
                for (int i = 0; i < _groups.Count; i++) if (_groups[i].Active) n++;
                return n;
            }
        }

        protected override void OnInitialize()
        {
            _groups = new List<MirageGroup>(3);
            _teamBuffer = new List<DodgeballPlayer>(4);
        }

        protected override void OnEquip()
        {
            Listen<BallThrownEvent>(OnBallThrown);
            Listen<CatchAttemptEvent>(OnCatchAttempt);
        }

        protected override void OnUnequip() => EndAllGroups(false);

        protected override void OnCastStarted()
        {
            AudioManager.PlayAt(SfxId.UltimateCast, Owner.ChestPosition, 1f, 0.9f);
        }

        protected override void OnCast()
        {
            EndAllGroups(false);
            float duration = Duration;
            if (duration <= 0f)
            {
                ExtendActive(FallbackDuration);
                duration = FallbackDuration;
            }

            int clones = Mathf.Clamp(clonesPerPlayer, 1, 3);
            PlayerRegistry.GetTeam(Owner.Team, _teamBuffer, CourtZone.Infield);
            for (int i = 0; i < _teamBuffer.Count; i++)
            {
                var player = _teamBuffer[i];
                if (!IsEligible(player)) continue;
                var group = CreateGroup(player, clones, duration);
                if (group != null) _groups.Add(group);
            }
            _teamBuffer.Clear();

            if (_groups.Count == 0)
            {
                EndAbility();
                return;
            }

            // Feedback: a wave of smoke from every obscured player (spawn VFX per clone) and a rumble for the caster.
            AudioManager.PlayAt(SfxId.Clone, Owner.ChestPosition, 1f, 0.8f);
            var juice = JuiceManager.Instance;
            if (juice != null) juice.Shake(0.25f, 14f, 0.3f, Owner.Position);
            var local = PlayerRegistry.LocalPlayer;
            if (local != null && local.Team == Owner.Team) ScreenFx.Pulse(ScreenPulse.UltimateCast, 0.6f, 0.4f);
        }

        protected override void OnTick(float deltaTime)
        {
            int active = 0;
            for (int i = 0; i < _groups.Count; i++)
            {
                var group = _groups[i];
                if (!group.Active) continue;
                if (!IsEligible(group.Player) || CountAlive(group) == 0)
                {
                    EndGroup(group, true); // eliminated / sent to the outfield / every clone popped
                    continue;
                }
                TickGroup(group, deltaTime);
                active++;
            }

            if (active == 0) EndAbility();
        }

        protected override void OnInterrupt(InterruptReason reason) => EndAllGroups(reason != InterruptReason.RoundEnded);

        protected override void OnEnd(bool interrupted) => EndAllGroups(true);

        protected override void OnRoundReset() => EndAllGroups(false);

        /// <summary>
        /// Scales with how many teammates it protects and how many enemy balls are aimed at the team; a comeback tool when
        /// outnumbered.
        /// </summary>
        public override float EvaluateAIUtility(in AbilityAIContext ctx)
        {
            if (Data == null || ctx.Self == null) return 0f;

            int protectedPlayers = 0;
            int armedEnemies = 0;
            var players = PlayerRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null) continue;
                if (p.Team == Owner.Team && IsEligible(p)) protectedPlayers++;
                else if (IsEnemy(p) && p.Combat != null && p.Combat.HasBall) armedEnemies++;
            }
            if (protectedPlayers == 0) return 0f;

            float utility = Data.aiWeight * (0.25f + 0.15f * protectedPlayers + 0.1f * armedEnemies);
            if (ctx.IncomingBall != null && ctx.IncomingTimeToImpact > 0.3f) utility += 0.1f;
            if (ctx.AlliesInfield < ctx.EnemiesInfield) utility += 0.1f;
            return Mathf.Clamp01(utility);
        }

        // ------------------------------------------------------------------ groups

        private MirageGroup CreateGroup(DodgeballPlayer player, int clones, float duration)
        {
            var group = new MirageGroup(player, clones);
            GetAxes(player, out Vector3 lateral, out _);
            float centre = (group.SlotCount - 1) * 0.5f;

            // Start with the real player in the slot that keeps the most of the line inside their zone.
            group.PlayerSlot = ChooseInitialPlayerSlot(player, lateral, group.SlotCount, centre);
            Vector3 anchor = player.Position - lateral * ((group.PlayerSlot - centre) * slotSpacing);

            int spawned = 0;
            for (int j = 0, slot = 0; j < clones; j++, slot++)
            {
                if (slot == group.PlayerSlot) slot++;
                group.CloneSlot[j] = slot;
                group.FromSlot[j] = slot;

                // Clones burst out of the real body and fan out to their slots.
                var clone = ShadowClone.SpawnAnimated(player, Owner, player.Position, player.Rotation, 0f);
                if (clone == null) continue;
                clone.FollowSharpness = followSharpness;
                clone.SetDesiredPose(SlotWorld(player, anchor, lateral, slot, centre), player.Rotation);
                group.Clones[j] = clone;
                spawned++;
            }

            if (spawned == 0) return null;

            if (player.Status != null) player.Status.Apply(StatusEffectType.Obscured, duration, 1f, this);
            // Staggered first shuffle so the three formations never swap in sync.
            group.NextShuffleIn = Random.Range(0.35f, Mathf.Max(0.4f, shuffleInterval));
            return group;
        }

        private void TickGroup(MirageGroup group, float dt)
        {
            var player = group.Player;
            GetAxes(player, out Vector3 lateral, out Vector3 forward);
            float centre = (group.SlotCount - 1) * 0.5f;

            float t = 1f;
            float eased = 1f;
            if (group.InTransit)
            {
                group.TransitT += dt / Mathf.Max(0.05f, transitDuration);
                t = Mathf.Clamp01(group.TransitT);
                eased = t * t * (3f - 2f * t); // smoothstep: zero velocity at both ends
            }

            // The anchor is derived from the REAL position every frame, so the formation always follows the player.
            float playerSlotPos = group.InTransit && group.MovePlayer
                ? Mathf.Lerp(group.FromPlayerSlot, group.ToPlayerSlot, eased)
                : group.PlayerSlot;
            Vector3 anchor = player.Position - lateral * ((playerSlotPos - centre) * slotSpacing);

            float sharpness = group.InTransit ? transitSharpness : followSharpness;
            for (int j = 0; j < group.Clones.Length; j++)
            {
                var clone = group.Clones[j];
                if (clone == null || !clone.IsAlive) continue;

                float slotPos = group.InTransit ? Mathf.Lerp(group.FromSlot[j], group.CloneSlot[j], eased) : group.CloneSlot[j];
                Vector3 pos = anchor + lateral * ((slotPos - centre) * slotSpacing);
                if (group.InTransit && group.ArcSign[j] != 0) pos += forward * (crossingArc * Mathf.Sin(Mathf.PI * t) * group.ArcSign[j]);
                pos = AbilityUtil.ClampToPlayerZone(player, pos);
                pos.y = player.Position.y;
                clone.SetDesiredPose(pos, player.Rotation, sharpness);
            }

            if (group.InTransit)
            {
                if (group.MovePlayer) DriveSideStep(group, lateral, t);
                if (t >= 1f)
                {
                    group.InTransit = false;
                    if (group.MovePlayer) group.PlayerSlot = group.ToPlayerSlot;
                    for (int j = 0; j < group.ArcSign.Length; j++) group.ArcSign[j] = 0;
                    group.NextShuffleIn = NextInterval();
                }
                return;
            }

            group.NextShuffleIn -= dt;
            if (group.NextShuffleIn <= 0f) StartShuffle(group, anchor, lateral, centre);
        }

        /// <summary>Overrides only the lateral component of the real player's velocity to follow the swap curve.</summary>
        private void DriveSideStep(MirageGroup group, Vector3 lateral, float t)
        {
            var player = group.Player;
            if (!CanBeMoved(player)) return;

            // d/dt of smoothstep(t) = 6t(1-t) / T
            float lateralSpeed = (group.ToPlayerSlot - group.FromPlayerSlot) * slotSpacing * 6f * t * (1f - t) / Mathf.Max(0.05f, transitDuration);
            Vector3 planar = player.Motor.PlanarVelocity;
            planar -= lateral * Vector3.Dot(planar, lateral);
            planar += lateral * lateralSpeed;
            player.Motor.SetPlanarVelocity(planar);
        }

        private void StartShuffle(MirageGroup group, Vector3 anchor, Vector3 lateral, float centre)
        {
            var player = group.Player;
            for (int j = 0; j < group.FromSlot.Length; j++)
            {
                group.FromSlot[j] = group.CloneSlot[j];
                group.ArcSign[j] = 0;
            }

            // Preferred: the real player swaps with a random clone whose slot is inside the player's zone.
            int chosen = -1;
            if (moveRealPlayers && CanBeMoved(player))
            {
                int candidates = 0;
                for (int j = 0; j < group.Clones.Length; j++)
                {
                    var c = group.Clones[j];
                    if (c == null || !c.IsAlive) continue;
                    if (!IsSlotInsideZone(player, anchor, lateral, group.CloneSlot[j], centre)) continue;
                    candidates++;
                    if (Random.Range(0, candidates) == 0) chosen = j; // reservoir sampling, no allocation
                }
            }

            if (chosen >= 0)
            {
                group.MovePlayer = true;
                group.FromPlayerSlot = group.PlayerSlot;
                group.ToPlayerSlot = group.CloneSlot[chosen];
                group.CloneSlot[chosen] = group.PlayerSlot;
                group.ArcSign[chosen] = Random.value < 0.5f ? 1 : -1;
            }
            else
            {
                // Fallback: two clones cross paths (the player is left alone).
                int a = -1, b = -1;
                for (int j = 0; j < group.Clones.Length; j++)
                {
                    var c = group.Clones[j];
                    if (c == null || !c.IsAlive) continue;
                    if (a < 0) a = j;
                    else if (b < 0 || Random.value < 0.5f) b = j;
                }
                if (a < 0 || b < 0)
                {
                    group.NextShuffleIn = NextInterval();
                    return;
                }
                group.MovePlayer = false;
                int tmp = group.CloneSlot[a];
                group.CloneSlot[a] = group.CloneSlot[b];
                group.CloneSlot[b] = tmp;
                group.ArcSign[a] = 1;
                group.ArcSign[b] = -1;
            }

            group.InTransit = true;
            group.TransitT = 0f;
        }

        private void EndGroup(MirageGroup group, bool effects)
        {
            if (!group.Active) return;
            group.Active = false;
            for (int j = 0; j < group.Clones.Length; j++)
            {
                var c = group.Clones[j];
                if (c != null) c.Dissolve(effects);
                group.Clones[j] = null;
            }
            if (group.Player != null && group.Player.Status != null) group.Player.Status.Remove(StatusEffectType.Obscured, this);
        }

        private void EndAllGroups(bool effects)
        {
            if (_groups == null) return;
            for (int i = 0; i < _groups.Count; i++) EndGroup(_groups[i], effects);
            _groups.Clear();
        }

        // ------------------------------------------------------------------ events

        private void OnBallThrown(BallThrownEvent e)
        {
            if (!IsActive || e.IsAbilityThrow || e.Ball == null) return;
            var group = FindGroup(e.Thrower);
            if (group == null) return;
            for (int j = 0; j < group.Clones.Length; j++)
            {
                var c = group.Clones[j];
                if (c != null && c.IsAlive) c.MirrorThrow(e.Origin, e.Velocity, e.Ball.GravityScale, illusionBallLifetime);
            }
        }

        private void OnCatchAttempt(CatchAttemptEvent e)
        {
            if (!mirrorCatches || !IsActive) return;
            var group = FindGroup(e.Player);
            if (group == null) return;
            for (int j = 0; j < group.Clones.Length; j++)
            {
                var c = group.Clones[j];
                if (c != null && c.IsAlive) c.MirrorCatch();
            }
        }

        // ------------------------------------------------------------------ helpers

        private MirageGroup FindGroup(DodgeballPlayer player)
        {
            if (player == null || _groups == null) return null;
            for (int i = 0; i < _groups.Count; i++)
            {
                if (_groups[i].Active && _groups[i].Player == player) return _groups[i];
            }
            return null;
        }

        private static int CountAlive(MirageGroup group)
        {
            int n = 0;
            for (int j = 0; j < group.Clones.Length; j++)
            {
                var c = group.Clones[j];
                if (c != null && c.IsAlive) n++;
            }
            return n;
        }

        private static bool IsEligible(DodgeballPlayer p) =>
            p != null && p.IsInfield && p.IsTargetable && p.Health != null && p.Health.IsAlive;

        /// <summary>Only players in a controllable ground state are side-stepped (never mid-air, sliding, stunned or frozen).</summary>
        private static bool CanBeMoved(DodgeballPlayer p)
        {
            if (p == null || p.Motor == null || p.StateMachine == null) return false;
            var state = p.StateMachine.Current;
            if (state != PlayerStateId.Grounded && state != PlayerStateId.Sprinting &&
                state != PlayerStateId.ChargingThrow && state != PlayerStateId.Catching) return false;
            return p.Status == null || (!p.Status.Has(StatusEffectType.Frozen) && !p.Status.Has(StatusEffectType.Rooted));
        }

        /// <summary>Formation line runs across the court (perpendicular to the attack direction) so it always faces the enemy.</summary>
        private static void GetAxes(DodgeballPlayer player, out Vector3 lateral, out Vector3 forward)
        {
            var court = Court.Instance;
            forward = court != null ? court.AttackDirection(player.Team) : player.Forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            lateral = Vector3.Cross(Vector3.up, forward).normalized;
        }

        private Vector3 SlotWorld(DodgeballPlayer player, Vector3 anchor, Vector3 lateral, int slot, float centre)
        {
            Vector3 p = AbilityUtil.ClampToPlayerZone(player, anchor + lateral * ((slot - centre) * slotSpacing));
            p.y = player.Position.y;
            return p;
        }

        private bool IsSlotInsideZone(DodgeballPlayer player, Vector3 anchor, Vector3 lateral, int slot, float centre)
        {
            Vector3 world = anchor + lateral * ((slot - centre) * slotSpacing);
            Vector3 clamped = AbilityUtil.ClampToPlayerZone(player, world);
            float dx = clamped.x - world.x;
            float dz = clamped.z - world.z;
            return dx * dx + dz * dz <= 0.04f; // within 0.2 m of the ideal slot
        }

        private int ChooseInitialPlayerSlot(DodgeballPlayer player, Vector3 lateral, int slotCount, float centre)
        {
            int best = Mathf.RoundToInt(centre);
            int bestInside = -1;
            float bestCentreDistance = float.PositiveInfinity;
            for (int k = 0; k < slotCount; k++)
            {
                Vector3 anchor = player.Position - lateral * ((k - centre) * slotSpacing);
                int inside = 0;
                for (int s = 0; s < slotCount; s++)
                    if (IsSlotInsideZone(player, anchor, lateral, s, centre)) inside++;

                float centreDistance = Mathf.Abs(k - centre);
                if (inside > bestInside || (inside == bestInside && centreDistance < bestCentreDistance))
                {
                    best = k;
                    bestInside = inside;
                    bestCentreDistance = centreDistance;
                }
            }
            return best;
        }

        private float NextInterval() => Mathf.Max(0.2f, shuffleInterval * (1f + Random.Range(-shuffleJitter, shuffleJitter)));
    }
}
