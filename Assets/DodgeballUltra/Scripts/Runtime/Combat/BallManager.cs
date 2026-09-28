using System;
using System.Collections.Generic;
using DodgeballUltra.Events;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Combat
{
    /// <summary>
    /// CONTRACT (kernel) - spawns, pools and queries balls; applies <see cref="IBallFieldEffect"/>s; respawns balls that
    /// leave the arena (publishes BallOutOfBoundsEvent). Match balls are persistent; ability balls are pooled.
    /// <para>
    /// Responsibilities:
    /// <list type="bullet">
    /// <item>Match balls (created once, reused every round) and a pool of ability projectiles (meteor, beam, glue, freeze,
    /// turret shots) so abilities never instantiate at runtime after warm-up.</item>
    /// <item>Queries used by players, AI and abilities: <see cref="FindNearestBall"/>, <see cref="GetIncomingLiveBalls"/>.</item>
    /// <item>The registry of <see cref="IBallFieldEffect"/>s applied by every live ball in its FixedUpdate.</item>
    /// <item>Out-of-arena detection every FixedUpdate (<see cref="Court.IsOutOfArena"/>): the ball is despawned and
    /// reappears on the centre line after <see cref="MatchRules.outOfBoundsRespawnDelay"/>.</item>
    /// <item>Dead-ball recovery: a free ball resting where no player can reach it (run-off area, an empty outfield strip) is
    /// first nudged back toward the nearest populated zone and, if it is still stranded, returned there — like a ball
    /// retriever at a real match.</item>
    /// </list>
    /// </para>
    /// <para>Owner module: Combat.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BallManager : MonoBehaviour
    {
        public static BallManager Instance { get; private set; }

        // ------------------------------------------------------------------ static visual settings

        /// <summary>
        /// Material for match balls (e.g. a scanned/authored ball material). When null, <c>GameConfig.ballMaterial</c> of
        /// the active <see cref="GameBootstrap"/> is used, then the procedural pebbled red rubber.
        /// Set it before the match balls are created.
        /// </summary>
        public static Material MatchBallMaterialOverride { get; set; }

        /// <summary>
        /// Prefab replacing the procedural match-ball visual (colliders are stripped). When null,
        /// <c>GameConfig.ballVisualPrefab</c> of the active <see cref="GameBootstrap"/> is used.
        /// </summary>
        public static GameObject BallVisualPrefabOverride { get; set; }

        private static int s_nextBallId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
            MatchBallMaterialOverride = null;
            BallVisualPrefabOverride = null;
            s_nextBallId = 0;
        }

        // ------------------------------------------------------------------ tuning

        [Tooltip("Physical and flight tuning shared by every ball.")]
        [SerializeField] private BallPhysicsTuning tuning = new BallPhysicsTuning();

        [Header("Ability ball pool")]
        [Tooltip("Ability balls created up-front so the first ability cast does not allocate.")]
        [Min(0)] [SerializeField] private int abilityBallPrewarm = 4;

        [Tooltip("Maximum pooled (inactive) ability balls kept; extras are destroyed when recycled.")]
        [Min(1)] [SerializeField] private int maxPooledAbilityBalls = 24;

        [Header("Out of bounds")]
        [Tooltip("Respawn delay (s) used when no MatchRules are available.")]
        [Min(0f)] [SerializeField] private float fallbackRespawnDelay = 2f;

        [Tooltip("Balls below this height (m) are out of bounds even without a Court (fell through the world).")]
        [SerializeField] private float killPlaneY = -25f;

        [Tooltip("Height (m) above the floor at which respawned balls appear (they drop onto the centre line).")]
        [Range(0f, 2f)] [SerializeField] private float respawnDropHeight = 0.35f;

        [Tooltip("Distance (m) kept from the sidelines when spreading respawned balls along the centre line.")]
        [Min(0f)] [SerializeField] private float centreLineEdgeMargin = 0.75f;

        [Header("Dead-ball recovery")]
        [Tooltip("Return free balls that end up where no player can reach them (run-off area, an empty outfield strip).")]
        [SerializeField] private bool recoverUnreachableBalls = true;

        [Tooltip("How far (m) a player can reach past the edge of their zone to pick a ball up.")]
        [Min(0f)] [SerializeField] private float reachMargin = 0.6f;

        [Tooltip("Seconds a ball may stay out of reach before it is nudged back toward play.")]
        [Min(0f)] [SerializeField] private float nudgeAfter = 1.5f;

        [Tooltip("Seconds a ball may stay out of reach before it is returned into the nearest populated zone.")]
        [Min(0f)] [SerializeField] private float returnAfter = 5f;

        [Tooltip("Maximum roll speed (m/s) of the nudge.")]
        [Min(0f)] [SerializeField] private float maxNudgeSpeed = 4.5f;

        [Tooltip("Despawn time (s) of a returned ball before it reappears inside the zone.")]
        [Min(0f)] [SerializeField] private float returnDespawnTime = 0.5f;

        [Tooltip("Distance (m) inside the zone edge where returned balls are placed.")]
        [Min(0f)] [SerializeField] private float returnInset = 0.5f;

        // ------------------------------------------------------------------ state

        private readonly List<DodgeBall> _matchBalls = new List<DodgeBall>(8);
        private readonly List<DodgeBall> _activeBalls = new List<DodgeBall>(16);
        private readonly Stack<DodgeBall> _pool = new Stack<DodgeBall>(16);
        private readonly List<IBallFieldEffect> _fieldEffects = new List<IBallFieldEffect>(4);
        private IBallFieldEffect[] _fieldEffectSnapshot = Array.Empty<IBallFieldEffect>();
        private bool _fieldEffectsDirty;

        private static readonly TeamId[] s_teams = { TeamId.Home, TeamId.Away };
        private static readonly CourtZone[] s_zones = { CourtZone.Infield, CourtZone.Outfield };

        /// <summary>Physical and flight tuning shared by every ball.</summary>
        public BallPhysicsTuning Tuning => tuning ??= new BallPhysicsTuning();

        /// <summary>The persistent match balls.</summary>
        public IReadOnlyList<DodgeBall> MatchBalls => _matchBalls;

        /// <summary>Every active ball (match + ability balls).</summary>
        public IReadOnlyList<DodgeBall> ActiveBalls => _activeBalls;

        public IReadOnlyList<IBallFieldEffect> FieldEffects => _fieldEffects;

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[Dodgeball Ultra] Duplicate BallManager on '{name}' removed; '{Instance.name}' is active.", this);
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            if (Instance != this) return;
            // Warm the pool (also generates the shared ball textures/materials once, outside of gameplay).
            for (int i = _pool.Count; i < abilityBallPrewarm; i++)
            {
                var ball = CreateBall(true, BallStyle.Standard);
                ball.ReturnToPool();
                _pool.Push(ball);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void FixedUpdate()
        {
            var court = Court.Instance;
            float dt = Time.fixedDeltaTime;

            for (int i = _activeBalls.Count - 1; i >= 0; i--)
            {
                if (i >= _activeBalls.Count) continue; // a recycle earlier in this loop shrank the list
                var ball = _activeBalls[i];
                if (ball == null)
                {
                    _activeBalls.RemoveAt(i);
                    continue;
                }
                if (!ball.isActiveAndEnabled) continue;

                var state = ball.State;
                if (state == BallState.Held || state == BallState.Despawned)
                {
                    ball.UnreachableTime = 0f;
                    ball.UnreachableNudged = false;
                    continue;
                }

                Vector3 position = ball.FlightPosition;
                bool outOfArena = position.y < killPlaneY || (court != null && court.IsOutOfArena(position));
                if (outOfArena)
                {
                    HandleOutOfBounds(ball, position);
                    continue;
                }

                if (recoverUnreachableBalls && court != null && state == BallState.Free && !ball.IsAbilityBall)
                    UpdateDeadBallRecovery(ball, position, court, dt);
            }
        }

        // ------------------------------------------------------------------ contract API

        /// <summary>Creates <paramref name="positions"/>.Count match balls (reuses existing ones) and places them Free.</summary>
        public void SetupMatchBalls(IList<Vector3> positions)
        {
            if (positions == null) return;
            PruneDestroyed();

            int count = positions.Count;
            while (_matchBalls.Count < count) _matchBalls.Add(CreateBall(false, BallStyle.Standard));

            for (int i = _matchBalls.Count - 1; i >= count; i--)
            {
                var extra = _matchBalls[i];
                _matchBalls.RemoveAt(i);
                _activeBalls.Remove(extra);
                if (extra != null)
                {
                    extra.PrepareForRemoval();
                    Destroy(extra.gameObject);
                }
            }

            for (int i = 0; i < count; i++)
            {
                var ball = _matchBalls[i];
                ball.ResetTo(RestingPosition(positions[i]));
                if (!_activeBalls.Contains(ball)) _activeBalls.Add(ball);
            }
        }

        /// <summary>Round reset: every match ball back to <paramref name="positions"/>; ability balls recycled.</summary>
        public void ResetForRound(IList<Vector3> positions)
        {
            for (int i = _activeBalls.Count - 1; i >= 0; i--)
            {
                if (i >= _activeBalls.Count) continue;
                var ball = _activeBalls[i];
                if (ball != null && ball.IsAbilityBall) Recycle(ball);
            }
            SetupMatchBalls(positions);
        }

        /// <summary>Gets a temporary projectile from the pool, positioned at <paramref name="position"/>. Launch it with DodgeBall.Launch.</summary>
        public DodgeBall SpawnAbilityBall(Vector3 position, BallStyle style)
        {
            DodgeBall ball = null;
            while (_pool.Count > 0 && ball == null) ball = _pool.Pop(); // skips pooled balls destroyed externally
            if (ball == null) ball = CreateBall(true, style);

            ball.PrepareAbilitySpawn(position, style);
            if (!_activeBalls.Contains(ball)) _activeBalls.Add(ball);
            return ball;
        }

        /// <summary>Returns an ability ball to the pool (match balls are ignored).</summary>
        public void Recycle(DodgeBall ball)
        {
            if (ball == null || !ball.IsAbilityBall || ball.IsPooled) return;
            ball.ReturnToPool();
            _activeBalls.Remove(ball);
            if (_pool.Count < maxPooledAbilityBalls) _pool.Push(ball);
            else Destroy(ball.gameObject);
        }

        /// <summary>Nearest match ball that <paramref name="filter"/> accepts (null filter = any Free ball).</summary>
        public DodgeBall FindNearestBall(Vector3 position, Predicate<DodgeBall> filter = null, float maxDistance = float.PositiveInfinity)
        {
            DodgeBall best = null;
            float bestSqr = float.IsPositiveInfinity(maxDistance) ? float.PositiveInfinity : maxDistance * maxDistance;
            for (int i = 0; i < _matchBalls.Count; i++)
            {
                var ball = _matchBalls[i];
                if (ball == null || !ball.isActiveAndEnabled) continue;
                if (filter != null ? !filter(ball) : !ball.IsFree) continue;
                float d = (ball.FlightPosition - position).sqrMagnitude;
                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = ball;
                }
            }
            return best;
        }

        /// <summary>Fills <paramref name="results"/> with live balls whose thrower is an enemy of <paramref name="player"/>.</summary>
        public void GetIncomingLiveBalls(DodgeballPlayer player, List<DodgeBall> results)
        {
            if (results == null) return;
            results.Clear();
            if (player == null) return;
            for (int i = 0; i < _activeBalls.Count; i++)
            {
                var ball = _activeBalls[i];
                if (ball == null || !ball.IsLive) continue;
                var thrower = ball.LastThrower;
                // Thrower-less live balls (environment, rewinds) threaten everybody, exactly like the ball's own sweep.
                if (thrower == null || PlayerRegistry.AreEnemies(thrower, player)) results.Add(ball);
            }
        }

        public void RegisterFieldEffect(IBallFieldEffect effect)
        {
            if (effect == null || _fieldEffects.Contains(effect)) return;
            _fieldEffects.Add(effect);
            _fieldEffectsDirty = true;
        }

        public void UnregisterFieldEffect(IBallFieldEffect effect)
        {
            if (effect == null) return;
            if (_fieldEffects.Remove(effect)) _fieldEffectsDirty = true;
        }

        /// <summary>Called by DodgeBall itself (FixedUpdate) to apply every registered field effect.</summary>
        public void ApplyFieldEffects(DodgeBall ball, float fixedDeltaTime)
        {
            if (ball == null || _fieldEffects.Count == 0) return;

            // Copy-on-write snapshot: effects may (un)register themselves or others while being applied.
            if (_fieldEffectsDirty)
            {
                _fieldEffectsDirty = false;
                _fieldEffectSnapshot = _fieldEffects.ToArray();
            }

            var snapshot = _fieldEffectSnapshot;
            for (int i = 0; i < snapshot.Length; i++)
            {
                var effect = snapshot[i];
                if (effect == null) continue;
                if (effect is UnityEngine.Object unityObject && unityObject == null)
                {
                    // A destroyed component that forgot to unregister.
                    UnregisterFieldEffect(effect);
                    continue;
                }

                try { effect.ApplyToBall(ball, fixedDeltaTime); }
                catch (Exception e) { Debug.LogException(e, this); }

                if (!ball.IsLive) break; // caught, frozen or dropped by this effect
            }
        }

        // ------------------------------------------------------------------ additional API

        /// <summary>Globally unique id for a new ball (also used by hand-placed scene balls).</summary>
        public static int AllocateBallId() => s_nextBallId++;

        /// <summary>Material for Standard match balls: explicit override, then GameConfig.ballMaterial, else null (procedural).</summary>
        public static Material ResolveMatchBallMaterial()
        {
            if (MatchBallMaterialOverride != null) return MatchBallMaterialOverride;
            var config = ActiveConfig;
            return config != null && config.ballMaterial != null ? config.ballMaterial : null;
        }

        /// <summary>Visual prefab for match balls: explicit override, then GameConfig.ballVisualPrefab, else null (procedural).</summary>
        public static GameObject ResolveBallVisualPrefab()
        {
            if (BallVisualPrefabOverride != null) return BallVisualPrefabOverride;
            var config = ActiveConfig;
            return config != null && config.ballVisualPrefab != null ? config.ballVisualPrefab : null;
        }

        /// <summary>Registers a hand-placed scene ball as a match ball.</summary>
        public void AdoptBall(DodgeBall ball)
        {
            if (ball == null) return;
            if (!ball.IsAbilityBall && !_matchBalls.Contains(ball)) _matchBalls.Add(ball);
            if (!_activeBalls.Contains(ball)) _activeBalls.Add(ball);
        }

        /// <summary>Centre-line respawn point reserved for <paramref name="ball"/> (each match ball has its own slot).</summary>
        public Vector3 GetCentreLineRespawnPoint(DodgeBall ball)
        {
            var court = Court.Instance;
            int index = Mathf.Max(0, _matchBalls.IndexOf(ball));
            int count = Mathf.Max(1, _matchBalls.Count);
            float halfWidth = court != null ? Mathf.Max(0f, court.width * 0.5f - centreLineEdgeMargin) : 3.75f;
            float x = count == 1 ? 0f : Mathf.Lerp(-halfWidth, halfWidth, index / (float)(count - 1));
            Vector3 centre = court != null ? court.Center : Vector3.zero;
            float floor = court != null ? court.FloorY : 0f;
            return new Vector3(centre.x + x, floor + Core.GameConstants.BallRadius + respawnDropHeight, centre.z);
        }

        internal void NotifyBallDestroyed(DodgeBall ball)
        {
            _matchBalls.Remove(ball);
            _activeBalls.Remove(ball);
        }

        // ------------------------------------------------------------------ internals

        private static GameConfig ActiveConfig
        {
            get
            {
                var bootstrap = GameBootstrap.Instance;
                return bootstrap != null ? bootstrap.ActiveConfig : null;
            }
        }

        private DodgeBall CreateBall(bool isAbilityBall, BallStyle style)
        {
            int id = AllocateBallId();
            var go = new GameObject(isAbilityBall ? "AbilityBall_" + id : "DodgeBall_" + id) { layer = GameLayers.Ball };
            go.transform.SetParent(transform, false);
            var ball = go.AddComponent<DodgeBall>();
            ball.Initialize(id, isAbilityBall, style);
            return ball;
        }

        private void PruneDestroyed()
        {
            for (int i = _matchBalls.Count - 1; i >= 0; i--)
                if (_matchBalls[i] == null) _matchBalls.RemoveAt(i);
            for (int i = _activeBalls.Count - 1; i >= 0; i--)
                if (_activeBalls[i] == null) _activeBalls.RemoveAt(i);
        }

        /// <summary>Lifts a (floor-level) spawn point so the ball rests on the court instead of intersecting it.</summary>
        private static Vector3 RestingPosition(Vector3 position)
        {
            float radius = Core.GameConstants.BallRadius;
            var court = Court.Instance;
            if (court != null)
            {
                float minY = court.FloorY + radius + 0.002f;
                if (position.y < minY) position.y = minY;
                return position;
            }
            if (Physics.Raycast(position + Vector3.up * 0.5f, Vector3.down, out var hit, 3f, GameLayers.GroundMask, QueryTriggerInteraction.Ignore))
            {
                float minY = hit.point.y + radius + 0.002f;
                if (position.y < minY) position.y = minY;
            }
            return position;
        }

        private void HandleOutOfBounds(DodgeBall ball, Vector3 lastPosition)
        {
            GameEvents.Publish(new BallOutOfBoundsEvent { Ball = ball, LastPosition = lastPosition });
            if (ball.IsAbilityBall)
            {
                Recycle(ball);
                return;
            }

            var rules = CombatRuleValues.Rules;
            float delay = rules != null ? rules.outOfBoundsRespawnDelay : fallbackRespawnDelay;
            ball.UnreachableTime = 0f;
            ball.UnreachableNudged = false;
            ball.Despawn(delay, GetCentreLineRespawnPoint(ball));
        }

        /// <summary>Nudges, then returns, free balls that no player can reach.</summary>
        private void UpdateDeadBallRecovery(DodgeBall ball, Vector3 position, Court court, float dt)
        {
            if (IsReachable(position, court, out Vector3 nearestPlayable))
            {
                ball.UnreachableTime = 0f;
                ball.UnreachableNudged = false;
                return;
            }

            ball.UnreachableTime += dt;

            if (!ball.UnreachableNudged && ball.UnreachableTime >= nudgeAfter && ball.Body != null && !ball.Body.isKinematic)
            {
                // Roll it back toward play (a ball retriever's gentle kick): speed grows with the distance to cover.
                ball.UnreachableNudged = true;
                Vector3 toPlay = nearestPlayable - position;
                toPlay.y = 0f;
                float distance = toPlay.magnitude;
                if (distance > 0.05f)
                {
                    float speed = Mathf.Min(maxNudgeSpeed, 0.8f + 1.1f * Mathf.Sqrt(distance));
                    Vector3 v = ball.Body.GetVelocity();
                    ball.SetVelocity(new Vector3(0f, v.y, 0f) + toPlay / distance * speed);
                }
            }

            if (ball.UnreachableTime >= returnAfter)
            {
                ball.UnreachableTime = 0f;
                ball.UnreachableNudged = false;
                Vector3 target = nearestPlayable;
                target.y = court.FloorY + Core.GameConstants.BallRadius + respawnDropHeight;
                ball.Despawn(returnDespawnTime, target);
            }
        }

        /// <summary>
        /// True when a player could reach <paramref name="position"/>: it lies (within <see cref="reachMargin"/>) inside a
        /// zone that currently has players (a team's infield half or outfield strip). Otherwise
        /// <paramref name="nearestPlayable"/> is the closest point inside the nearest populated zone.
        /// </summary>
        private bool IsReachable(Vector3 position, Court court, out Vector3 nearestPlayable)
        {
            nearestPlayable = position;
            bool anyPlayers = PlayerRegistry.All.Count > 0;
            float bestSqr = float.PositiveInfinity;
            bool found = false;

            for (int t = 0; t < s_teams.Length; t++)
            {
                for (int z = 0; z < s_zones.Length; z++)
                {
                    var team = s_teams[t];
                    var zone = s_zones[z];
                    // A zone nobody plays in (e.g. an empty outfield strip early in a round) cannot return a ball.
                    if (anyPlayers && PlayerRegistry.CountTeam(team, zone) == 0) continue;

                    Bounds bounds;
                    try { bounds = court.GetConfinement(team, zone); }
                    catch (Exception) { continue; }

                    Vector3 min = bounds.min, max = bounds.max;
                    if (position.x >= min.x - reachMargin && position.x <= max.x + reachMargin &&
                        position.z >= min.z - reachMargin && position.z <= max.z + reachMargin)
                    {
                        nearestPlayable = position;
                        return true;
                    }

                    float insetX = Mathf.Min(returnInset, bounds.extents.x);
                    float insetZ = Mathf.Min(returnInset, bounds.extents.z);
                    var clamped = new Vector3(
                        Mathf.Clamp(position.x, min.x + insetX, max.x - insetX),
                        position.y,
                        Mathf.Clamp(position.z, min.z + insetZ, max.z - insetZ));
                    float d = (clamped - position).sqrMagnitude;
                    if (d < bestSqr)
                    {
                        bestSqr = d;
                        nearestPlayable = clamped;
                        found = true;
                    }
                }
            }

            if (!found) nearestPlayable = court.Center; // no populated zone at all: back to the centre line
            return false;
        }
    }
}
