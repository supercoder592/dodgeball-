using DodgeballUltra.Combat;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Abilities
{
    /// <summary>Options for <see cref="AbilityUtil.ThrowAbilityBall"/>.</summary>
    public struct AbilityThrowOptions
    {
        public BallStyle Style;
        /// <summary>Multiplier on top of the thrower's full-charge speed.</summary>
        public float SpeedMultiplier;
        public float RadiusMultiplier;
        public float GravityScale;
        public bool Unblockable;
        public bool Pierce;
        public IBallPayload Payload;
        /// <summary>Explicit target; null = the thrower's soft-lock target (or straight along the aim).</summary>
        public DodgeballPlayer Target;

        public static AbilityThrowOptions Default(BallStyle style) => new AbilityThrowOptions
        {
            Style = style,
            SpeedMultiplier = 1f,
            RadiusMultiplier = 1f,
            GravityScale = 0.5f,
        };
    }

    /// <summary>Shared helpers for hero abilities (kernel).</summary>
    public static class AbilityUtil
    {
        /// <summary>
        /// Conjures a temporary ability projectile at the thrower's hand and launches it through the normal throw
        /// pipeline (throw modifiers, rally cap, BallThrownEvent). Returns the ball or null if no BallManager exists.
        /// Ability balls are recycled automatically when they stop being live.
        /// </summary>
        public static DodgeBall ThrowAbilityBall(DodgeballPlayer thrower, in AbilityThrowOptions options)
        {
            if (thrower == null || thrower.Combat == null || BallManager.Instance == null) return null;

            var combat = thrower.Combat;
            var ball = BallManager.Instance.SpawnAbilityBall(combat.GetThrowOrigin(), options.Style);
            if (ball == null) return null;

            // Ability throws always count as fully charged.
            var p = combat.BuildThrowParams(combat.Profile.fullChargeTime, options.Target ?? combat.CurrentTarget, true);
            p.Style = options.Style;
            p.SpeedMultiplier *= options.SpeedMultiplier <= 0f ? 1f : options.SpeedMultiplier;
            p.RadiusMultiplier *= options.RadiusMultiplier <= 0f ? 1f : options.RadiusMultiplier;
            p.GravityScale = options.GravityScale;
            p.Unblockable |= options.Unblockable;
            p.Pierce |= options.Pierce;
            p.Payload = options.Payload;
            p.RallyCount = 0;
            p.IsAbilityThrow = true;
            combat.LaunchBall(ball, p);
            return ball;
        }

        /// <summary>Point on the floor directly below <paramref name="position"/> (court raycast, falls back to Court.FloorY).</summary>
        public static Vector3 GroundPoint(Vector3 position)
        {
            if (Physics.Raycast(position + Vector3.up * 0.5f, Vector3.down, out var hit, 20f, GameLayers.GroundMask, QueryTriggerInteraction.Ignore))
                return hit.point;
            float y = Match.Court.Instance != null ? Match.Court.Instance.FloorY : 0f;
            return new Vector3(position.x, y, position.z);
        }

        /// <summary>Clamps a world position into the confinement of <paramref name="player"/>'s team and zone.</summary>
        public static Vector3 ClampToPlayerZone(DodgeballPlayer player, Vector3 position)
        {
            var court = Match.Court.Instance;
            if (court == null || player == null) return position;
            var b = court.GetConfinement(player.Team, player.Zone);
            return new Vector3(Mathf.Clamp(position.x, b.min.x, b.max.x), position.y, Mathf.Clamp(position.z, b.min.z, b.max.z));
        }
    }
}
