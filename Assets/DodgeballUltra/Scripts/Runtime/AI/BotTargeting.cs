using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.AI
{
    /// <summary>
    /// Target selection and aiming for bots.
    /// <para>
    /// Targets are scored by how unlikely they are to dodge or catch: close, slow, busy (charging, airborne, stunned,
    /// frozen, holding a ball), facing away (a ball outside the catch cone cannot be caught), not in a catch stance.
    /// Cloaked enemies are ignored unless very close; enemies behind an enemy shield / clone / turret are rejected with a
    /// line-of-sight ray. The aim point is the ballistic lead intercept, blended by the profile's lead accuracy, plus a
    /// Gaussian angular error.
    /// </para>
    /// </summary>
    public sealed class BotTargeting
    {
        private const int MaxCandidates = 8;

        private readonly DodgeballPlayer[] _candidates = new DodgeballPlayer[MaxCandidates];
        private readonly float[] _scores = new float[MaxCandidates];
        private readonly RaycastHit[] _hits = new RaycastHit[8];

        /// <summary>
        /// Picks the enemy to attack from <paramref name="origin"/>. With probability
        /// <see cref="BotDifficultyProfile.targetSelectionSkill"/> the best-scored target is taken, otherwise a
        /// score-weighted random one (weaker bots make poorer choices). <paramref name="current"/> gets the hysteresis bonus.
        /// </summary>
        /// <returns>The chosen enemy or null.</returns>
        public DodgeballPlayer SelectTarget(DodgeballPlayer self, Vector3 origin, BotDifficultyProfile profile, BotRandom rng,
            DodgeballPlayer current, float maxRange, bool requireLineOfSight, out float score)
        {
            int n = 0;
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count && n < MaxCandidates; i++)
            {
                var enemy = all[i];
                if (!PlayerRegistry.AreEnemies(self, enemy) || !enemy.IsTargetable) continue;
                if (!BotWorld.IsPerceivable(self, enemy, profile.cloakDetectionRadius)) continue;

                float s = ScoreTarget(origin, enemy, maxRange);
                if (s <= 0f) continue;
                if (requireLineOfSight && !HasLineOfSight(self, origin, enemy)) continue;
                if (enemy == current) s += profile.hysteresis;

                _candidates[n] = enemy;
                _scores[n] = s;
                n++;
            }

            if (n == 0)
            {
                score = 0f;
                return null;
            }

            int best = 0;
            float total = 0f;
            for (int i = 0; i < n; i++)
            {
                total += _scores[i];
                if (_scores[i] > _scores[best]) best = i;
            }

            int pick = best;
            if (n > 1 && !rng.Chance(profile.targetSelectionSkill))
            {
                // Score-weighted random pick: sensible most of the time, but not always optimal.
                float r = rng.Value * total;
                for (int i = 0; i < n; i++)
                {
                    r -= _scores[i];
                    if (r <= 0f)
                    {
                        pick = i;
                        break;
                    }
                }
            }

            var chosen = _candidates[pick];
            score = _scores[pick];
            for (int i = 0; i < n; i++) _candidates[i] = null; // do not keep references alive
            return chosen;
        }

        /// <summary>
        /// Best target score available to a thrower standing at <paramref name="origin"/> (no line-of-sight ray, used to
        /// compare pass receivers). Cloak visibility is judged by <paramref name="observer"/> (the deciding bot).
        /// </summary>
        public float EvaluateOpportunity(DodgeballPlayer observer, DodgeballPlayer thrower, Vector3 origin, BotDifficultyProfile profile,
            float maxRange)
        {
            float best = 0f;
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var enemy = all[i];
                if (!PlayerRegistry.AreEnemies(thrower, enemy) || !enemy.IsTargetable) continue;
                if (!BotWorld.IsPerceivable(observer, enemy, profile.cloakDetectionRadius)) continue;
                float s = ScoreTarget(origin, enemy, maxRange);
                if (s > best) best = s;
            }
            return best;
        }

        /// <summary>
        /// Heuristic 0..~1.5 "chance this throw lands" score of <paramref name="enemy"/> for a ball thrown from
        /// <paramref name="origin"/>. 0 = not a valid target.
        /// </summary>
        public static float ScoreTarget(Vector3 origin, DodgeballPlayer enemy, float maxRange)
        {
            if (enemy == null) return 0f;
            if (BotWorld.Has(enemy, StatusEffectType.Invulnerable)) return 0f;

            float distance = BotWorld.PlanarDistance(origin, enemy.Position);
            if (distance > maxRange) return 0f;

            // Close targets have less time to react.
            float proximity = 1f - Mathf.Clamp01((distance - 4f) / Mathf.Max(1f, maxRange - 4f));

            // Slow targets cannot sidestep; fast movers are hard to lead.
            float sprint = enemy.Motor != null && enemy.Motor.Profile != null ? Mathf.Max(1f, enemy.Motor.Profile.sprintSpeed) : 7.4f;
            float slowness = 1f - Mathf.Clamp01(BotWorld.PlanarSpeed(enemy) / sprint);

            // A ball arriving outside the frontal catch cone cannot be caught.
            float cone = enemy.Combat != null && enemy.Combat.Profile != null ? enemy.Combat.Profile.catchConeAngle : 75f;
            var toThrower = BotWorld.PlanarDirection(enemy.Position, origin, -enemy.Forward);
            float facingAngle = Vector3.Angle(enemy.Forward, toThrower);
            float facingAway = Mathf.Clamp01((facingAngle - cone * 0.6f) / Mathf.Max(1f, cone * 0.8f));

            float busy = 0f;
            switch (BotWorld.StateOf(enemy))
            {
                case PlayerStateId.ChargingThrow: busy += 0.35f; break; // committed to a throw, slowed
                case PlayerStateId.Airborne: busy += 0.3f; break;       // cannot change direction
                case PlayerStateId.Sliding: busy += 0.1f; break;        // committed to a line
                case PlayerStateId.Stunned: busy += 0.5f; break;
                case PlayerStateId.Catching: busy -= 0.4f; break;       // armed catch stance
                case PlayerStateId.Incapacitated: busy += 0.45f; break; // frozen / channeling / grabbed
            }
            if (BotWorld.Has(enemy, StatusEffectType.Frozen)) busy += 0.4f;          // Elsa: second hit eliminates
            if (BotWorld.HoldsBall(enemy)) busy += 0.15f;                           // hands full -> cannot catch
            if (enemy.Combat != null && enemy.Combat.IsCatchArmed) busy -= 0.3f;
            if (BotWorld.Has(enemy, StatusEffectType.Slippery) || BotWorld.Has(enemy, StatusEffectType.DodgeDisabled)) busy += 0.15f;
            if (BotWorld.Has(enemy, StatusEffectType.Rooted)) busy += 0.2f;
            if (BotWorld.Has(enemy, StatusEffectType.Obscured)) busy -= 0.08f;      // uncertain which body is real
            if (enemy.Health != null)
            {
                if (enemy.Health.CurrentHp > GameConstants.StandardHitDamage + 0.5f) busy -= 0.12f; // Thick Hide: needs two hits
                if (enemy.Health.IsEliminationPending) busy -= 0.4f;                                  // already going down
            }

            float s = 0.2f + 0.35f * proximity + 0.2f * slowness + 0.25f * facingAway + busy;
            return Mathf.Max(0.01f, s);
        }

        /// <summary>
        /// True when nothing that would stop the ball lies between <paramref name="origin"/> and the target's chest:
        /// court geometry or an enemy <see cref="IBallHittable"/> (Aegis Barrier, turret, clone). Own-team hittables are
        /// transparent (friendly balls pass through them).
        /// </summary>
        public bool HasLineOfSight(DodgeballPlayer self, Vector3 origin, DodgeballPlayer target)
        {
            var to = target.ChestPosition;
            var dir = to - origin;
            float dist = dir.magnitude;
            if (dist < 0.5f) return true;
            dir /= dist;

            int mask = GameLayers.CourtMask | GameLayers.HittableMask;
            int count = Physics.RaycastNonAlloc(origin, dir, _hits, dist - 0.3f, mask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < count; i++)
            {
                var col = _hits[i].collider;
                if (col == null) continue;
                int layer = col.gameObject.layer;
                if (layer == GameLayers.Court)
                {
                    if (col.isTrigger) continue; // zone / out-of-bounds volumes
                    return false;
                }
                if (layer == GameLayers.Hittable)
                {
                    var hittable = col.GetComponentInParent<IBallHittable>();
                    if (hittable != null && hittable.OwnerTeam == self.Team) continue;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Aim point for a throw released now: the ballistic lead intercept blended by
        /// <see cref="BotDifficultyProfile.leadAccuracy"/>, shifted by <paramref name="decoyOffset"/> (mis-targeting an
        /// Obscured enemy) and perturbed by a Gaussian angular error of <see cref="BotDifficultyProfile.aimErrorDegrees"/>.
        /// </summary>
        public static Vector3 ComputeAimPoint(DodgeballPlayer self, DodgeballPlayer target, Vector3 origin, float chargeSeconds,
            BotDifficultyProfile profile, BotRandom rng, Vector3 decoyOffset, bool applyNoise)
        {
            var chest = target.ChestPosition;
            float speed = EstimateThrowSpeed(self, chargeSeconds);
            float gravityScale = self.Combat != null && self.Combat.Profile != null ? self.Combat.Profile.thrownGravityScale : 0.65f;
            float gravity = GameConstants.Gravity * gravityScale;

            var lead = chest;
            if (speed > 0.5f)
            {
                lead = ThrowSolver.PredictInterceptPoint(origin, target, speed, gravity);
                if (!IsFinite(lead) || (lead - chest).sqrMagnitude > 144f) lead = chest; // solver failure guard
            }

            var aim = Vector3.LerpUnclamped(chest, lead, profile.leadAccuracy) + decoyOffset;

            if (applyNoise && profile.aimErrorDegrees > 0f)
            {
                var dir = aim - origin;
                float dist = dir.magnitude;
                if (dist > 0.1f)
                {
                    dir /= dist;
                    var right = Vector3.Cross(Vector3.up, dir);
                    if (right.sqrMagnitude < 1e-4f) right = Vector3.right;
                    right.Normalize();
                    float yaw = rng.Gaussian() * profile.aimErrorDegrees;
                    float pitch = rng.Gaussian() * profile.aimErrorDegrees * 0.5f; // vertical error is smaller for a practised arm
                    aim += right * (Mathf.Tan(yaw * Mathf.Deg2Rad) * dist) + Vector3.up * (Mathf.Tan(pitch * Mathf.Deg2Rad) * dist);
                }
            }
            return aim;
        }

        /// <summary>
        /// Approximate launch speed (m/s) of a throw charged for <paramref name="chargeSeconds"/> - mirrors the throw
        /// pipeline closely enough for lead prediction (charge curve, Overcharge, stealth bonus, counter boost, rally cap).
        /// </summary>
        public static float EstimateThrowSpeed(DodgeballPlayer thrower, float chargeSeconds)
        {
            var combat = thrower != null ? thrower.Combat : null;
            var prof = combat != null ? combat.Profile : null;

            float baseSpeed = (prof != null ? prof.baseThrowSpeedKmh : 88f) * GameConstants.KmhToMs;
            float full = prof != null ? Mathf.Max(0.05f, prof.fullChargeTime) : 0.75f;
            float t = Mathf.Clamp01(chargeSeconds / full);
            float curve = prof != null && prof.chargeCurve != null && prof.chargeCurve.length > 0 ? prof.chargeCurve.Evaluate(t) : t;
            float mult = Mathf.Lerp(prof != null ? prof.minChargeMultiplier : 0.72f, 1f, Mathf.Clamp01(curve));

            if (thrower != null)
            {
                if (thrower.Hero == HeroId.Rayne) mult *= 1f + 0.5f * Mathf.Clamp01(chargeSeconds / 2f); // Overcharge
                if (BotWorld.IsCloaked(thrower)) mult *= 1.3f;                                           // Gale stealth throw
            }
            if (combat != null && combat.HasCounterBoost) mult *= 1f + GameConstants.PerfectCatchCounterBoost;

            int rally = combat != null && combat.HeldBall != null ? combat.HeldBall.RallyCount : 0;
            return RallyMath.ComputeSpeed(baseSpeed * mult, rally);
        }

        /// <summary>
        /// Offset (m) from an Obscured enemy to a plausible clone position beside it, used when the bot mistakes a clone
        /// for the real body.
        /// </summary>
        public static Vector3 ChooseDecoyOffset(Vector3 origin, DodgeballPlayer target, BotRandom rng)
        {
            var toTarget = BotWorld.PlanarDirection(origin, target.Position, Vector3.forward);
            var side = Vector3.Cross(Vector3.up, toTarget).normalized;
            return side * (rng.Sign() * rng.Range(1.2f, 2.4f)) + toTarget * rng.Range(-0.8f, 0.8f);
        }

        private static bool IsFinite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
              float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
    }
}
