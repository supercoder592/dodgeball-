using System;
using DodgeballUltra.Abilities;
using DodgeballUltra.Combat;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.AI
{
    /// <summary>
    /// Decides when a bot presses Skill / Ultimate. Each decision tick it builds an <see cref="AbilityAIContext"/>, asks
    /// every ready ability for <see cref="AbilityBase.EvaluateAIUtility"/>, scales it by the difficulty's usage factor and
    /// fires when it beats the threshold (with a probability that grows with the margin, so bots are not robotic).
    /// Abilities are never spammed: every press starts a retry interval and every <c>AbilityFailedEvent</c> a longer
    /// back-off. Threat-triggered evaluations (e.g. Specter's Precognition Dodge, Bear's Magnetic Pull) only fire when
    /// the incoming ball is what makes the ability useful.
    /// </summary>
    public sealed class BotAbilityUser
    {
        private float _skillRetryAt;
        private float _ultimateRetryAt;

        /// <summary>Scaled utility of the skill at the last evaluation (0 if not usable).</summary>
        public float LastSkillUtility { get; private set; }

        /// <summary>Scaled utility of the ultimate at the last evaluation (0 if not usable).</summary>
        public float LastUltimateUtility { get; private set; }

        public void Reset()
        {
            _skillRetryAt = 0f;
            _ultimateRetryAt = 0f;
            LastSkillUtility = 0f;
            LastUltimateUtility = 0f;
        }

        /// <summary>The button was pressed: wait at least the retry interval before pressing it again.</summary>
        public void NotifyPressed(AbilitySlot slot, float now, BotDifficultyProfile profile)
        {
            float until = now + profile.abilityRetryInterval;
            if (slot == AbilitySlot.Skill) _skillRetryAt = Mathf.Max(_skillRetryAt, until);
            else if (slot == AbilitySlot.Ultimate) _ultimateRetryAt = Mathf.Max(_ultimateRetryAt, until);
        }

        /// <summary>The ability failed to activate: back off.</summary>
        public void NotifyFailed(AbilitySlot slot, float now, BotDifficultyProfile profile)
        {
            float until = now + profile.abilityFailBackoff;
            if (slot == AbilitySlot.Skill) _skillRetryAt = Mathf.Max(_skillRetryAt, until);
            else if (slot == AbilitySlot.Ultimate) _ultimateRetryAt = Mathf.Max(_ultimateRetryAt, until);
        }

        /// <summary>
        /// Chooses an ability to press now.
        /// </summary>
        /// <param name="self">The bot's player.</param>
        /// <param name="ctx">Situation snapshot (see <see cref="BuildContext"/>).</param>
        /// <param name="calmCtx">
        /// For threat-triggered evaluations: the same snapshot without the incoming ball. An ability only fires if the threat
        /// raises its utility. Pass <paramref name="ctx"/> itself for regular decision ticks.
        /// </param>
        /// <param name="threatTriggered">True when called because a new incoming ball was perceived.</param>
        /// <param name="profile">Difficulty profile.</param>
        /// <param name="rng">Per-bot random source.</param>
        /// <param name="now">Time.time.</param>
        /// <param name="slot">The chosen slot.</param>
        public bool TryChoose(DodgeballPlayer self, in AbilityAIContext ctx, in AbilityAIContext calmCtx, bool threatTriggered,
            BotDifficultyProfile profile, BotRandom rng, float now, out AbilitySlot slot)
        {
            slot = AbilitySlot.Passive;
            var abilities = self != null ? self.Abilities : null;
            if (abilities == null)
            {
                LastSkillUtility = LastUltimateUtility = 0f;
                return false;
            }

            float skill = Evaluate(abilities.Skill, ctx, profile, now, ref _skillRetryAt);
            float ultimate = Evaluate(abilities.Ultimate, ctx, profile, now, ref _ultimateRetryAt);

            if (threatTriggered)
            {
                // Only abilities that the threat makes (more) useful are considered on a threat trigger.
                if (skill > 0f && skill <= Evaluate(abilities.Skill, calmCtx, profile, now, ref _skillRetryAt) + 0.05f) skill = 0f;
                if (ultimate > 0f && ultimate <= Evaluate(abilities.Ultimate, calmCtx, profile, now, ref _ultimateRetryAt) + 0.05f) ultimate = 0f;
            }

            LastSkillUtility = skill;
            LastUltimateUtility = ultimate;

            float threshold = profile.abilityUtilityThreshold;
            bool useUltimate = ultimate >= threshold && ultimate >= skill;
            float best = useUltimate ? ultimate : skill;
            if (best < threshold) return false;

            // The further above the threshold, the more certain the press (marginal cases are sometimes skipped).
            float pressChance = Mathf.Lerp(0.35f, 1f, Mathf.InverseLerp(threshold, Mathf.Max(threshold + 0.01f, 1f), best));
            if (!rng.Chance(pressChance)) return false;

            slot = useUltimate ? AbilitySlot.Ultimate : AbilitySlot.Skill;
            return true;
        }

        /// <summary>Builds the situation snapshot used by <see cref="AbilityBase.EvaluateAIUtility"/>.</summary>
        /// <param name="self">The bot's player.</param>
        /// <param name="threat">Most urgent perceived incoming ball (may be invalid).</param>
        /// <param name="cloakDetectionRadius">Cloaked enemies farther than this are not "seen" as the nearest enemy.</param>
        /// <param name="freeBallRadius">Radius (m) for <see cref="AbilityAIContext.FreeBallsNearby"/>.</param>
        /// <param name="ctx">The snapshot.</param>
        public static void BuildContext(DodgeballPlayer self, in BotThreat threat, float cloakDetectionRadius, float freeBallRadius,
            out AbilityAIContext ctx)
        {
            ctx = new AbilityAIContext
            {
                Self = self,
                NearestEnemyDistance = float.PositiveInfinity,
                IncomingTimeToImpact = float.PositiveInfinity,
                RoundTimeRemaining = BotWorld.RoundTimeRemaining,
            };
            if (self == null) return;

            // Nearest visible, targetable enemy.
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null) continue;
                if (PlayerRegistry.AreEnemies(self, p))
                {
                    if (!p.IsTargetable || !BotWorld.IsPerceivable(self, p, cloakDetectionRadius)) continue;
                    float d = Vector3.Distance(self.Position, p.Position);
                    if (d < ctx.NearestEnemyDistance)
                    {
                        ctx.NearestEnemyDistance = d;
                        ctx.NearestEnemy = p;
                    }
                }
                else if (PlayerRegistry.AreTeammates(self, p) && p.Zone == CourtZone.Outfield)
                {
                    ctx.TeammatesInOutfield++;
                }
            }

            if (threat.Ball != null)
            {
                ctx.IncomingBall = threat.Ball;
                ctx.IncomingTimeToImpact = threat.TimeToImpact;
            }

            ctx.EnemiesInfield = BotWorld.CountTargetable(self.Team.Opponent());
            ctx.AlliesInfield = BotWorld.CountTargetable(self.Team);
            ctx.HoldingBall = BotWorld.HoldsBall(self);
            ctx.UltimateCharge = self.Abilities != null ? self.Abilities.UltimateCharge : 0f;

            var manager = BallManager.Instance;
            if (manager != null && manager.MatchBalls != null)
            {
                var balls = manager.MatchBalls;
                float r2 = freeBallRadius * freeBallRadius;
                for (int i = 0; i < balls.Count; i++)
                {
                    var b = balls[i];
                    if (b == null || b.State != BallState.Free) continue;
                    if ((b.transform.position - self.Position).sqrMagnitude <= r2) ctx.FreeBallsNearby++;
                }
            }
        }

        private const float BrokenAbilityRetryDelay = 15f;

        private static float Evaluate(AbilityBase ability, in AbilityAIContext ctx, BotDifficultyProfile profile, float now, ref float retryAt)
        {
            if (ability == null || ability.IsPassive || !ability.IsReady || now < retryAt) return 0f;
            if (!ability.CanActivate(out _)) return 0f; // cooldown, silence, ult meter, requires ball... (no event published)

            float utility;
            try
            {
                utility = ability.EvaluateAIUtility(ctx);
            }
            catch (Exception e)
            {
                // A broken ability must not take the whole bot down (and must not spam the log every tick).
                Debug.LogException(e);
                retryAt = now + BrokenAbilityRetryDelay;
                return 0f;
            }
            if (float.IsNaN(utility) || utility <= 0f) return 0f;
            return Mathf.Clamp01(utility) * profile.abilityUsageFactor;
        }
    }
}
