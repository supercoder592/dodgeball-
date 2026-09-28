using System;
using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Abilities.Heroes
{
    /// <summary>
    /// Payload of Screws' viscous [Glue Trap Ball]: the first time the ball touches anything - a player (hit or catch), the
    /// floor or a wall, or it stops being live some other way (e.g. blocked by a shield) - it spawns exactly one
    /// <see cref="ScrewsGluePuddle"/> on the floor below the contact. The direct hit itself is resolved normally
    /// (<see cref="OnHitPlayer"/> never suppresses it).
    /// <para>One instance per throw (it carries the "already splatted" state).</para>
    /// </summary>
    public sealed class ScrewsGluePayload : BallPayloadBase
    {
        private readonly DodgeballPlayer _owner;
        private readonly TeamId _ownerTeam;
        private readonly ScrewsGluePuddleSettings _settings;
        private readonly object _slowSource;
        private readonly bool _puddleOnCatch;
        private readonly Action<ScrewsGluePuddle> _onSpawned;
        private bool _spawned;

        /// <summary>The puddle this throw created, or null.</summary>
        public ScrewsGluePuddle Puddle { get; private set; }

        /// <param name="owner">Screws (team + credit).</param>
        /// <param name="settings">Puddle tuning.</param>
        /// <param name="slowSource">Status key shared by all of this Screws' puddles (no double slows).</param>
        /// <param name="puddleOnCatch">Also splat when the ball is caught (the catcher stands in the glue).</param>
        /// <param name="onSpawned">Optional callback when the puddle is created.</param>
        public ScrewsGluePayload(DodgeballPlayer owner, in ScrewsGluePuddleSettings settings, object slowSource,
            bool puddleOnCatch, Action<ScrewsGluePuddle> onSpawned = null)
        {
            _owner = owner;
            _ownerTeam = owner != null ? owner.Team : TeamId.None;
            _settings = settings;
            _slowSource = slowSource;
            _puddleOnCatch = puddleOnCatch;
            _onSpawned = onSpawned;
        }

        /// <inheritdoc />
        public override void OnAfterHitPlayer(DodgeBall ball, in HitContext hit, HitOutcome outcome)
        {
            if (outcome == HitOutcome.Ignored || hit.Victim == null) return;
            SpawnAt(hit.Victim.Position); // at the victim's feet
        }

        /// <inheritdoc />
        public override void OnHitSurface(DodgeBall ball, Vector3 point, Vector3 normal, Collider surface, bool isFloor)
        {
            // Walls: step off the surface before projecting down so the ray does not graze the wall itself.
            SpawnAt(isFloor ? point : point + normal * 0.3f);
        }

        /// <inheritdoc />
        public override void OnCaught(DodgeBall ball, DodgeballPlayer catcher, CatchQuality quality)
        {
            if (_puddleOnCatch && catcher != null) SpawnAt(catcher.Position);
        }

        /// <inheritdoc />
        public override void OnEnded(DodgeBall ball)
        {
            // Blocked, despawned, stopped by a hittable...: splat where it ended, if that is still inside the arena.
            if (!_spawned && ball != null) SpawnAt(ball.transform.position);
        }

        private void SpawnAt(Vector3 position)
        {
            if (_spawned) return;
            _spawned = true;

            Vector3 floor = AbilityUtil.GroundPoint(position);
            var court = Court.Instance;
            if (court != null && court.IsOutOfArena(floor)) return;

            Puddle = ScrewsGluePuddle.Spawn(floor, _settings, _ownerTeam, _owner, _slowSource);
            _onSpawned?.Invoke(Puddle);
        }
    }
}
