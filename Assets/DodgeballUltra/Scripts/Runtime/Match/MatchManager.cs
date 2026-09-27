using System;
using System.Collections;
using System.Collections.Generic;
using DodgeballUltra.Abilities;
using DodgeballUltra.AI;
using DodgeballUltra.CameraSystem;
using DodgeballUltra.Characters;
using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using DodgeballUltra.Events;
using DodgeballUltra.InputHandling;
using DodgeballUltra.Player;
using DodgeballUltra.VFX;
using UnityEngine;

namespace DodgeballUltra.Match
{
    /// <summary>
    /// CONTRACT (kernel) - authoritative match flow: spawning 3v3, rounds (pre-round -> countdown -> playing -> round end),
    /// scoring, infield/outfield moves, revivals (perfect catch, outfield hit), round timer and win conditions.
    /// Reacts to PlayerEliminatedEvent (ragdoll then move to outfield) and BallCaughtEvent (perfect catch revive).
    /// Publishes all match-flow events.
    /// <para>
    /// Flow (one coroutine, gameplay/scaled time so hitstop and pause freeze it):
    /// <code>
    /// StartMatch -> spawn 3v3 -> [ PreRound (reset players + balls, input locked, cinematic intro, MatchStartedEvent in round 1)
    ///                              -> Countdown (RoundCountdownEvent 3..1)
    ///                              -> Playing   (RoundStartedEvent, input unlocked, round timer)
    ///                              -> RoundEnd  (RoundEndedEvent, input locked, winners cheer) ] until a team has roundsToWin
    ///            -> MatchEnd (MatchEndedEvent, victory orbit) -> GameBootstrap.ReturnToHeroSelect()
    /// </code>
    /// Infield / outfield (Taiwanese 內場 / 外場): an eliminated player ragdolls for <see cref="MatchRules.eliminationRagdollTime"/>
    /// and is then moved to the outfield strip behind the opponent's baseline, from where they keep throwing. A perfect catch
    /// brings back the teammate who has been out the longest; an outfield player who lands a hit returns to the infield
    /// (<see cref="MatchRules.outfieldHitRevives"/>). A round ends the moment a team has no infield players (players with a
    /// pending delayed elimination still count), or at time-up (more infield players, then more total infield HP, else a
    /// replayed draw).
    /// </para>
    /// <para>Owner module: Match.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MatchManager : MonoBehaviour
    {
        public static MatchManager Instance { get; private set; }

        public MatchPhase Phase { get; private set; } = MatchPhase.None;
        public bool IsPlaying => Phase == MatchPhase.Playing;
        public int RoundNumber { get; private set; }
        public float RoundTimeRemaining { get; private set; }
        public MatchRules Rules { get; private set; }
        public Court Court { get; private set; }
        public DodgeballPlayer LocalPlayer { get; private set; }

        public event Action<MatchPhase, MatchPhase> PhaseChanged;

        // ------------------------------------------------------------------ tuning (presentation only; rules live in MatchRules)

        [Header("Camera")]
        [Tooltip("Orbit distance (m) of the cinematic camera during the pre-round intro.")]
        [Min(2f)] public float introCameraDistance = 11f;
        [Tooltip("Orbit distance (m) of the camera while spectating (no human player: attract mode / AI vs AI).")]
        [Min(2f)] public float spectateCameraDistance = 16f;
        [Tooltip("Orbit distance (m) of the victory camera around the winning team.")]
        [Min(2f)] public float victoryCameraDistance = 6.5f;
        [Tooltip("Height (m) above the floor of cinematic focus points (roughly chest height).")]
        [Range(0f, 3f)] public float cinematicFocusHeight = 1.2f;

        [Header("Flow")]
        [Tooltip("Seconds the match-end celebration is shown before returning to hero select.")]
        [Min(0f)] public float matchEndReturnDelay = 7f;
        [Tooltip("Without a GameBootstrap, a spectated (AI vs AI) match restarts itself after the match end (attract mode).")]
        public bool loopSpectatorMatches = true;
        [Tooltip("At time-up, total infield HP differences at or below this value (HP points) count as a tie -> draw.")]
        [Min(0f)] public float hpTieTolerance = 0.5f;

        [Header("Rewards")]
        [Tooltip("Grant MatchRules.ultGainOnHit / ultGainOnEliminate to the thrower of a landed hit and MatchRules.ultGainOnCatch " +
                 "for a normal catch. (The +15% Perfect Catch ultimate reward is granted by the catcher's combat controller.)")]
        public bool awardUltimateForPlays = true;

        [Header("Effects")]
        [Tooltip("Scale of the ReviveBeam effect played where a revived player re-enters the infield.")]
        [Min(0.1f)] public float reviveVfxScale = 1f;
        [Tooltip("Impulse (N*s) applied to a thrower eliminated by the classic 'catch eliminates thrower' rule, so the ragdoll " +
                 "slumps backwards instead of collapsing in place.")]
        [Min(0f)] public float caughtThrowerImpulse = 45f;

        // ------------------------------------------------------------------ state

        /// <summary>Per-player match bookkeeping (infield/outfield timing, spawn slot).</summary>
        private sealed class PlayerRecord
        {
            public DodgeballPlayer Player;
            public TeamId Team;
            public int Slot;
            /// <summary>Time.time of this round's elimination (-1 = not eliminated).</summary>
            public float EliminatedAt = -1f;
            /// <summary>Time.time at which the ragdoll ends and the player moves to the outfield (-1 = no move pending).</summary>
            public float OutfieldTransferAt = -1f;
            /// <summary>Time.time the player arrived in the outfield (-1 = not in the outfield).</summary>
            public float OutfieldSince = -1f;
            /// <summary>Index of the outfield spot occupied (-1 = none).</summary>
            public int OutfieldSpot = -1;

            public bool IsAwaitingOutfield => OutfieldTransferAt >= 0f;

            public void ClearRoundState()
            {
                EliminatedAt = -1f;
                OutfieldTransferAt = -1f;
                OutfieldSince = -1f;
                OutfieldSpot = -1;
            }
        }

        private readonly List<DodgeballPlayer> _players = new List<DodgeballPlayer>(6);
        private readonly List<PlayerRecord> _records = new List<PlayerRecord>(6);
        private readonly int[] _scores = new int[2];
        private readonly List<Vector3> _ballPositions = new List<Vector3>(8);
        private readonly List<HeroId> _heroPool = new List<HeroId>(10);
        private readonly Dictionary<HeroId, CharacterData> _fallbackHeroes = new Dictionary<HeroId, CharacterData>();

        private static readonly WaitForSeconds s_oneSecond = new WaitForSeconds(1f);

        private GameConfig _config;
        private MatchSetup _setup;
        private MatchRules _defaultRules;
        private Transform _playersRoot;
        private HumanInputSource _humanInput;
        private bool _flowRunning;
        private bool _roundEndRequested;
        private bool _roundTimeUp;
        private TeamId _forfeitingTeam = TeamId.None;

        // Perfect-catch revive de-duplication: the combat controller may call ReviveOneOutfieldTeammate directly while this
        // manager also observes the BallCaughtEvent of the same catch. Only one teammate may come back per perfect catch.
        private int _lastPerfectReviveFrame = -1;
        private DodgeballPlayer _lastPerfectReviver;
        private DodgeballPlayer _lastPerfectRevived;

        // ------------------------------------------------------------------ public queries

        /// <summary>Every player of the current match (Home slots first, then Away).</summary>
        public IReadOnlyList<DodgeballPlayer> Players => _players;

        /// <summary>The GameConfig of the running match (null before the first StartMatch).</summary>
        public GameConfig Config => _config;

        /// <summary>The sanitised setup the running match was started with.</summary>
        public MatchSetup Setup => _setup;

        /// <summary>True when no human plays (AI vs AI / attract mode).</summary>
        public bool IsSpectating => _setup.Spectate;

        /// <summary>A match exists (from the first pre-round to the end of the match-end celebration).</summary>
        public bool IsMatchInProgress => Phase >= MatchPhase.PreRound && Phase <= MatchPhase.MatchEnd;

        /// <summary>Winner of the last finished round (None for a draw or before the first round ended).</summary>
        public TeamId LastRoundWinner { get; private set; } = TeamId.None;

        /// <summary>Winner of the match once decided (None until then).</summary>
        public TeamId MatchWinner { get; private set; } = TeamId.None;

        public int GetScore(TeamId team) => team.IsValid() ? _scores[team.Index()] : 0;

        /// <summary>
        /// Infield players of <paramref name="team"/> still in the round: in the infield, not ragdolling toward the outfield
        /// and not eliminated. Players whose elimination is pending (Chrono's Delayed Impact) still count.
        /// </summary>
        public int CountInfield(TeamId team)
        {
            int count = 0;
            for (int i = 0; i < _records.Count; i++)
            {
                var r = _records[i];
                if (r.Team == team && IsInfieldAlive(r)) count++;
            }
            return count;
        }

        /// <summary>Players of <paramref name="team"/> currently standing in the outfield strip.</summary>
        public int CountOutfield(TeamId team)
        {
            int count = 0;
            for (int i = 0; i < _records.Count; i++)
            {
                var r = _records[i];
                if (r.Team == team && r.Player != null && r.Player.Zone == CourtZone.Outfield) count++;
            }
            return count;
        }

        /// <summary>Sum of the current HP of <paramref name="team"/>'s infield players (time-up tie-breaker).</summary>
        public float GetInfieldHp(TeamId team)
        {
            float hp = 0f;
            for (int i = 0; i < _records.Count; i++)
            {
                var r = _records[i];
                if (r.Team != team || !IsInfieldAlive(r)) continue;
                if (r.Player.Health != null) hp += Mathf.Max(0f, r.Player.Health.CurrentHp);
            }
            return hp;
        }

        /// <summary>True while <paramref name="player"/> ragdolls between elimination and the move to the outfield.</summary>
        public bool IsAwaitingOutfield(DodgeballPlayer player)
        {
            var r = FindRecord(player);
            return r != null && r.IsAwaitingOutfield;
        }

        /// <summary>Seconds <paramref name="player"/> has spent in the outfield this round (0 when infield).</summary>
        public float GetOutfieldTime(DodgeballPlayer player)
        {
            var r = FindRecord(player);
            if (r == null || r.OutfieldSince < 0f || player.Zone != CourtZone.Outfield) return 0f;
            return Mathf.Max(0f, Time.time - r.OutfieldSince);
        }

        /// <summary>Accent colour of <paramref name="team"/> from the active config (sensible defaults without one).</summary>
        public Color GetTeamColor(TeamId team)
        {
            if (_config != null) return _config.GetTeamColor(team);
            switch (team)
            {
                case TeamId.Home: return new Color(0.16f, 0.38f, 0.86f);
                case TeamId.Away: return new Color(0.82f, 0.16f, 0.14f);
                default: return new Color(0.75f, 0.75f, 0.75f);
            }
        }

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[Dodgeball Ultra] Duplicate MatchManager on '{name}' removed; '{Instance.name}' stays authoritative.", this);
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void OnEnable()
        {
            if (Instance == null) Instance = this;
            if (Instance != this) return;
            GameEvents.Subscribe<PlayerEliminatedEvent>(OnPlayerEliminated);
            GameEvents.Subscribe<BallHitPlayerEvent>(OnBallHitPlayer);
            GameEvents.Subscribe<BallCaughtEvent>(OnBallCaught);
            GameEvents.Subscribe<PlayerRevivedEvent>(OnPlayerRevived);
        }

        private void OnDisable()
        {
            GameEvents.Unsubscribe<PlayerEliminatedEvent>(OnPlayerEliminated);
            GameEvents.Unsubscribe<BallHitPlayerEvent>(OnBallHitPlayer);
            GameEvents.Unsubscribe<BallCaughtEvent>(OnBallCaught);
            GameEvents.Unsubscribe<PlayerRevivedEvent>(OnPlayerRevived);
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            Instance = null;
            if (_playersRoot != null) Destroy(_playersRoot.gameObject);
            if (_defaultRules != null) Destroy(_defaultRules);
        }

        private void Update()
        {
            // Complete ragdoll -> outfield moves whose timer ran out (scaled time: hitstop and pause hold the ragdoll).
            float now = Time.time;
            for (int i = 0; i < _records.Count; i++)
            {
                var r = _records[i];
                if (!r.IsAwaitingOutfield || now < r.OutfieldTransferAt) continue;
                CompletePendingTransfer(r);
            }
        }

        // ------------------------------------------------------------------ match lifecycle

        /// <summary>Spawns all players for <paramref name="setup"/> and starts round 1.</summary>
        public void StartMatch(MatchSetup setup, GameConfig config)
        {
            if (config == null && GameBootstrap.Instance != null) config = GameBootstrap.Instance.ActiveConfig;
            if (config == null)
            {
                Debug.LogError("[Dodgeball Ultra] MatchManager.StartMatch needs a GameConfig (none given and no GameBootstrap).", this);
                return;
            }

            TearDown();

            _config = config;
            Rules = config.rules != null ? config.rules : DefaultRules;
            Court = ResolveCourt();
            if (Court == null)
            {
                Debug.LogError("[Dodgeball Ultra] No Court in the scene and the runtime arena could not be built; match not started.", this);
                return;
            }

            config.GetAvailableHeroes(_heroPool);
            _setup = MatchSetupUtil.Sanitize(setup, Rules.playersPerTeam, _heroPool);

            _scores[0] = _scores[1] = 0;
            RoundNumber = 0;
            RoundTimeRemaining = Rules.roundDuration;
            LastRoundWinner = TeamId.None;
            MatchWinner = TeamId.None;

            SpawnTeam(TeamId.Home, _setup.HomeHeroes);
            SpawnTeam(TeamId.Away, _setup.AwayHeroes);

            // Match balls exist from the start so the intro shows them lined up on the centre line.
            Court.GetOpeningBallPositions(Rules.ballCount, _ballPositions);
            var balls = BallManager.Instance;
            if (balls != null)
            {
                try { balls.SetupMatchBalls(_ballPositions); }
                catch (Exception e) { Debug.LogException(e, balls); }
            }

            var rig = ThirdPersonCameraRig.Instance;
            if (rig != null && LocalPlayer != null) rig.SetTarget(LocalPlayer, true);
            SetHumanCursorLocked(true);

            _flowRunning = true;
            StartCoroutine(MatchFlow());
        }

        /// <summary>Tear everything down (return to hero select).</summary>
        public void EndMatch()
        {
            TearDown();

            // A pause menu or an interrupted hitstop must never leave the menus frozen.
            if (Time.timeScale < 1f) Time.timeScale = 1f;

            // Leave a clean court behind the menus: match balls back on the centre line.
            var balls = BallManager.Instance;
            if (balls != null && Court != null && Rules != null)
            {
                Court.GetOpeningBallPositions(Rules.ballCount, _ballPositions);
                try { balls.ResetForRound(_ballPositions); }
                catch (Exception e) { Debug.LogException(e, balls); }
            }

            SetPhase(MatchPhase.None);
        }

        /// <summary>
        /// Enters the hero-select phase (ends a running match first) and points the camera at the empty court.
        /// Called by <see cref="GameBootstrap"/> whenever the hero select screen is shown.
        /// </summary>
        public void EnterHeroSelect()
        {
            if (IsMatchInProgress) EndMatch();
            if (Court == null) Court = ResolveCourt();

            var rig = ThirdPersonCameraRig.Instance;
            if (rig != null && Court != null) rig.SetCinematicFocus(Court.Center + Vector3.up * cinematicFocusHeight, introCameraDistance);
            SetPhase(MatchPhase.HeroSelect);
        }

        /// <summary>
        /// <paramref name="team"/> concedes: the current round ends with <see cref="RoundEndReason.Forfeit"/> and the opponent is
        /// awarded the match.
        /// </summary>
        public void Forfeit(TeamId team)
        {
            if (!team.IsValid() || !IsMatchInProgress || Phase == MatchPhase.MatchEnd || _forfeitingTeam.IsValid()) return;
            _forfeitingTeam = team;
            if (Phase == MatchPhase.Playing) return; // the live-round loop resolves it on its next frame

            StopFlow();
            _flowRunning = true;
            StartCoroutine(ForfeitFlow());
        }

        // ------------------------------------------------------------------ infield / outfield

        /// <summary>Moves an eliminated player to the outfield (after the ragdoll time). Checks round end.</summary>
        public void SendToOutfield(DodgeballPlayer player)
        {
            var r = FindRecord(player);
            if (r == null || Court == null) return;
            if (player.Zone == CourtZone.Outfield && !r.IsAwaitingOutfield) return; // already out there
            MoveToOutfield(r);
        }

        /// <summary>Performs the actual infield -> outfield move for <paramref name="r"/>.</summary>
        private void MoveToOutfield(PlayerRecord r)
        {
            var player = r.Player;
            r.OutfieldTransferAt = -1f;

            int spot = FindFreeOutfieldSpot(r);
            var position = Court.GetOutfieldSpot(r.Team, spot);
            // Outfield players face back toward the enemy infield they attack (the opposite of the team's attack direction).
            var rotation = Quaternion.LookRotation(-Court.AttackDirection(r.Team), Vector3.up);

            try
            {
                if (player.Visual != null) player.Visual.ResetVisual(); // end the ragdoll, back to animation
                if (player.Motor != null) player.Motor.SetConfinement(Court.GetConfinement(r.Team, CourtZone.Outfield));
                player.Teleport(position, rotation);
                player.SetZone(CourtZone.Outfield);
                ClearDebuffs(player);

                var sm = player.StateMachine;
                if (sm != null)
                {
                    sm.ReleaseIncapacitation(IncapacitationReason.Eliminated);
                    // Eliminated while frozen/grabbed: the other lock has no meaning in the outfield.
                    if (sm.IsIn(PlayerStateId.Incapacitated)) sm.ResetToGrounded();
                }
                if (player.Rewind != null) player.Rewind.Clear(); // rewinds must never pull a player back across zones
                player.InputLocked = Phase != MatchPhase.Playing;
            }
            catch (Exception e)
            {
                Debug.LogException(e, player);
            }

            if (r.EliminatedAt < 0f) r.EliminatedAt = Time.time;
            r.OutfieldSince = Time.time;
            r.OutfieldSpot = spot;

            if (Phase == MatchPhase.Playing) CheckRoundEnd();
        }

        /// <summary>
        /// Returns an outfield player to the infield. Returns false if not possible. A player still ragdolling on the way to
        /// the outfield can also be brought back (a last-second save). Only during live play, except for
        /// <see cref="RevivalCause.RoundReset"/>.
        /// </summary>
        public bool ReviveFromOutfield(DodgeballPlayer player, RevivalCause cause, DodgeballPlayer reviver = null)
        {
            var r = FindRecord(player);
            if (r == null || Court == null || Rules == null) return false;
            if (cause != RevivalCause.RoundReset && Phase != MatchPhase.Playing) return false;

            bool inOutfield = player.Zone == CourtZone.Outfield;
            bool awaiting = r.IsAwaitingOutfield;
            if (!inOutfield && !awaiting) return false;

            r.OutfieldTransferAt = -1f;
            var position = FindFreeInfieldSpawn(r);
            var rotation = Court.GetSpawnRotation(r.Team);

            try
            {
                if (player.Visual != null) player.Visual.ResetVisual(); // un-ragdoll / clear any outfield pose
                if (player.Motor != null) player.Motor.SetConfinement(Court.GetConfinement(r.Team, CourtZone.Infield));
                player.Teleport(position, rotation);
                ClearDebuffs(player);
                if (player.Health != null) player.Health.Revive(Rules.reviveHpFraction);
                if (player.Zone != CourtZone.Infield) player.SetZone(CourtZone.Infield);
                if (player.StateMachine != null) player.StateMachine.ResetToGrounded();
                if (player.Rewind != null) player.Rewind.Clear();
                player.InputLocked = Phase != MatchPhase.Playing;
            }
            catch (Exception e)
            {
                Debug.LogException(e, player);
            }

            r.ClearRoundState();

            GameEvents.Publish(new PlayerRevivedEvent { Player = player, Reviver = reviver, Cause = cause });
            VfxManager.Spawn(VfxId.ReviveBeam, position, Quaternion.identity, reviveVfxScale, GetTeamColor(r.Team));
            return true;
        }

        /// <summary>Perfect Catch reward: revives the teammate who has been in the outfield the longest. Returns them or null.</summary>
        public DodgeballPlayer ReviveOneOutfieldTeammate(TeamId team, RevivalCause cause, DodgeballPlayer reviver = null)
        {
            if (!team.IsValid()) return null;

            if (cause == RevivalCause.PerfectCatch && reviver != null)
            {
                // One revive per perfect catch, however many systems report it this frame.
                if (_lastPerfectReviveFrame == Time.frameCount && _lastPerfectReviver == reviver) return _lastPerfectRevived;
                _lastPerfectReviveFrame = Time.frameCount;
                _lastPerfectReviver = reviver;
                _lastPerfectRevived = null;
            }

            // 1) The teammate standing in the outfield the longest.
            PlayerRecord best = null;
            float bestSince = float.MaxValue;
            for (int i = 0; i < _records.Count; i++)
            {
                var r = _records[i];
                if (r.Team != team || r.Player == null || r.Player == reviver || r.Player.Zone != CourtZone.Outfield) continue;
                float since = r.OutfieldSince >= 0f ? r.OutfieldSince : float.MaxValue * 0.5f;
                if (best == null || since < bestSince)
                {
                    best = r;
                    bestSince = since;
                }
            }

            // 2) Nobody out there yet: the teammate still falling (eliminated the earliest) gets a last-second save.
            if (best == null)
            {
                float bestElim = float.MaxValue;
                for (int i = 0; i < _records.Count; i++)
                {
                    var r = _records[i];
                    if (r.Team != team || r.Player == null || r.Player == reviver || !r.IsAwaitingOutfield) continue;
                    if (best == null || r.EliminatedAt < bestElim)
                    {
                        best = r;
                        bestElim = r.EliminatedAt;
                    }
                }
            }

            if (best == null || !ReviveFromOutfield(best.Player, cause, reviver)) return null;
            if (cause == RevivalCause.PerfectCatch && reviver != null) _lastPerfectRevived = best.Player;
            return best.Player;
        }

        // ------------------------------------------------------------------ event observers

        private void OnPlayerEliminated(PlayerEliminatedEvent evt)
        {
            var r = FindRecord(evt.Player);
            if (r == null || Rules == null) return;
            if (evt.Player.Zone == CourtZone.Outfield || r.IsAwaitingOutfield) return; // already out / duplicate report

            float now = Time.time;
            r.EliminatedAt = now;
            r.OutfieldTransferAt = now + Mathf.Max(0.2f, Rules.eliminationRagdollTime);

            // The Player module incapacitates on elimination; make sure the victim can never act while ragdolling.
            var sm = evt.Player.StateMachine;
            if (sm != null && !sm.IsIn(PlayerStateId.Incapacitated))
            {
                try { sm.Incapacitate(IncapacitationReason.Eliminated); }
                catch (Exception e) { Debug.LogException(e, evt.Player); }
            }

            // Immediate check: the round ends the moment a team has nobody left in the infield.
            if (Phase == MatchPhase.Playing) CheckRoundEnd();
        }

        private void OnPlayerRevived(PlayerRevivedEvent evt)
        {
            // Revivals performed by abilities (not through ReviveFromOutfield) while the player was still ragdolling toward the
            // outfield: cancel the pending move right away so the player counts as infield again this very frame.
            var r = FindRecord(evt.Player);
            if (r == null || !r.IsAwaitingOutfield) return;
            var p = evt.Player;
            if (p.Zone != CourtZone.Infield || p.Health == null || p.Health.IsEliminated) return;
            r.OutfieldTransferAt = -1f;
            r.EliminatedAt = -1f;
            p.InputLocked = Phase != MatchPhase.Playing;
        }

        private void OnBallHitPlayer(BallHitPlayerEvent evt)
        {
            if (Phase != MatchPhase.Playing || Rules == null) return;
            var attacker = evt.Attacker;
            if (attacker == null || FindRecord(attacker) == null) return;

            bool landed = evt.Outcome == HitOutcome.Damaged || evt.Outcome == HitOutcome.Eliminated;
            if (!landed || !PlayerRegistry.AreEnemies(attacker, evt.Victim)) return;

            if (awardUltimateForPlays && attacker.Abilities != null)
            {
                float gain = Rules.ultGainOnHit + (evt.Outcome == HitOutcome.Eliminated ? Rules.ultGainOnEliminate : 0f);
                if (gain > 0f) attacker.Abilities.AddUltimateCharge(gain, UltGainReason.HitLanded);
            }

            // Taiwanese outfield rule: an outfield player who hits an infield enemy returns to the infield.
            if (Rules.outfieldHitRevives && attacker.Zone == CourtZone.Outfield)
                ReviveFromOutfield(attacker, RevivalCause.OutfieldHit);
        }

        private void OnBallCaught(BallCaughtEvent evt)
        {
            if (Phase != MatchPhase.Playing || Rules == null) return;
            var catcher = evt.Catcher;
            if (catcher == null || FindRecord(catcher) == null) return;

            if (evt.Quality == CatchQuality.Perfect)
            {
                // Perfect Catch reward: revive the teammate who has been out the longest (idempotent per catch).
                ReviveOneOutfieldTeammate(catcher.Team, RevivalCause.PerfectCatch, catcher);
            }
            else if (evt.Quality == CatchQuality.Normal && awardUltimateForPlays && catcher.Abilities != null && Rules.ultGainOnCatch > 0f)
            {
                catcher.Abilities.AddUltimateCharge(Rules.ultGainOnCatch, UltGainReason.Catch);
            }

            // Optional classic rule: catching a ball eliminates its (infield) thrower.
            var thrower = evt.Thrower;
            if (Rules.catchEliminatesThrower && evt.Quality != CatchQuality.Miss && thrower != null && thrower.Health != null &&
                PlayerRegistry.AreEnemies(catcher, thrower) && thrower.IsTargetable)
            {
                var away = thrower.Position - catcher.Position;
                away.y = 0f;
                var impulse = (away.sqrMagnitude > 1e-4f ? away.normalized : -thrower.Forward) * caughtThrowerImpulse;
                thrower.Health.Eliminate(EliminationCause.BallHit, catcher, impulse, thrower.ChestPosition);
            }
        }

        // ------------------------------------------------------------------ round flow

        private IEnumerator MatchFlow()
        {
            while (true)
            {
                RoundNumber++;
                yield return PreRoundPhase();
                yield return CountdownPhase();
                yield return PlayingPhase();
                ResolveRound(_roundTimeUp);
                if (Rules.roundEndDuration > 0f) yield return new WaitForSeconds(Rules.roundEndDuration);
                if (MatchWinner.IsValid()) break;
            }
            yield return MatchEndPhase();
        }

        private IEnumerator ForfeitFlow()
        {
            if (Phase == MatchPhase.RoundEnd)
            {
                // Between rounds: the result is already announced; only the match outcome changes.
                AwardForfeit();
            }
            else
            {
                ResolveRound(false);
                if (Rules.roundEndDuration > 0f) yield return new WaitForSeconds(Rules.roundEndDuration);
            }
            yield return MatchEndPhase();
        }

        private IEnumerator PreRoundPhase()
        {
            SetPhase(MatchPhase.PreRound);
            _roundEndRequested = false;
            _roundTimeUp = false;
            RoundTimeRemaining = Rules.roundDuration;

            ResetPlayersForRound();

            Court.GetOpeningBallPositions(Rules.ballCount, _ballPositions);
            var balls = BallManager.Instance;
            if (balls != null)
            {
                try { balls.ResetForRound(_ballPositions); }
                catch (Exception e) { Debug.LogException(e, balls); }
            }

            // Cinematic intro orbit around the centre line (the whole line-up and the opening balls in frame).
            var rig = ThirdPersonCameraRig.Instance;
            if (rig != null) rig.SetCinematicFocus(Court.Center + Vector3.up * cinematicFocusHeight, introCameraDistance);

            if (RoundNumber == 1)
            {
                GameEvents.Publish(new MatchStartedEvent { RoundsToWin = Rules.roundsToWin, PlayersPerTeam = Rules.playersPerTeam });
            }

            if (Rules.preRoundDuration > 0f) yield return new WaitForSeconds(Rules.preRoundDuration);
        }

        private IEnumerator CountdownPhase()
        {
            SetPhase(MatchPhase.Countdown);
            FocusCameraForPlay();

            for (int s = Rules.countdownSeconds; s >= 1; s--)
            {
                GameEvents.Publish(new RoundCountdownEvent { Round = RoundNumber, SecondsLeft = s });
                yield return s_oneSecond;
            }
        }

        private IEnumerator PlayingPhase()
        {
            SetPhase(MatchPhase.Playing);
            SetAllInputLocked(false);
            GameEvents.Publish(new RoundStartedEvent { Round = RoundNumber, Duration = Rules.roundDuration });

            while (true)
            {
                yield return null;

                if (_forfeitingTeam.IsValid()) break;

                // Elimination events request an immediate check; polling the counts as well keeps the round honest even if a
                // player leaves the infield without an event (destroyed, moved by an ability...).
                if (_roundEndRequested || IsAnyTeamWipedOut())
                {
                    if (IsAnyTeamWipedOut()) break;
                    _roundEndRequested = false; // saved in the same frame (revived): play on
                }

                RoundTimeRemaining = Mathf.Max(0f, RoundTimeRemaining - Time.deltaTime);
                if (RoundTimeRemaining <= 0f)
                {
                    _roundTimeUp = true;
                    break;
                }
            }
        }

        /// <summary>Decides the round, updates the score, announces it and plays the celebrations.</summary>
        private void ResolveRound(bool timeUp)
        {
            int home = CountInfield(TeamId.Home);
            int away = CountInfield(TeamId.Away);

            TeamId winner;
            RoundEndReason reason;
            if (_forfeitingTeam.IsValid())
            {
                winner = _forfeitingTeam.Opponent();
                reason = RoundEndReason.Forfeit;
            }
            else if (!timeUp)
            {
                if (home == 0 && away == 0)
                {
                    winner = TeamId.None; // double knock-out in the same frame
                    reason = RoundEndReason.Draw;
                }
                else
                {
                    winner = home == 0 ? TeamId.Away : TeamId.Home;
                    reason = RoundEndReason.AllEliminated;
                }
            }
            else if (home != away)
            {
                winner = home > away ? TeamId.Home : TeamId.Away;
                reason = RoundEndReason.TimeUp;
            }
            else
            {
                float hpDiff = GetInfieldHp(TeamId.Home) - GetInfieldHp(TeamId.Away);
                if (Mathf.Abs(hpDiff) <= hpTieTolerance)
                {
                    winner = TeamId.None; // no score, the round is replayed
                    reason = RoundEndReason.Draw;
                }
                else
                {
                    winner = hpDiff > 0f ? TeamId.Home : TeamId.Away;
                    reason = RoundEndReason.TimeUp;
                }
            }

            if (reason == RoundEndReason.Forfeit) AwardForfeit();
            else if (winner.IsValid())
            {
                _scores[winner.Index()]++;
                if (_scores[winner.Index()] >= Mathf.Max(1, Rules.roundsToWin)) MatchWinner = winner;
            }
            LastRoundWinner = winner;

            SetPhase(MatchPhase.RoundEnd);
            FreezePlayersForBreak();
            PlayCelebrations(winner);

            GameEvents.Publish(new RoundEndedEvent
            {
                Round = RoundNumber,
                Winner = winner,
                Reason = reason,
                HomeScore = _scores[0],
                AwayScore = _scores[1],
            });
        }

        private IEnumerator MatchEndPhase()
        {
            SetPhase(MatchPhase.MatchEnd);
            FreezePlayersForBreak();
            PlayCelebrations(MatchWinner);
            FocusCameraOnTeam(MatchWinner);
            SetHumanCursorLocked(false);

            GameEvents.Publish(new MatchEndedEvent { Winner = MatchWinner, HomeScore = _scores[0], AwayScore = _scores[1] });

            if (matchEndReturnDelay > 0f) yield return new WaitForSeconds(matchEndReturnDelay);
            _flowRunning = false;

            var bootstrap = GameBootstrap.Instance;
            if (bootstrap != null)
            {
                bootstrap.ReturnToHeroSelect();
            }
            else if (loopSpectatorMatches && _setup.Spectate && _config != null)
            {
                StartMatch(_setup, _config); // attract mode: keep the AI match going
            }
        }

        private void AwardForfeit()
        {
            var winner = _forfeitingTeam.Opponent();
            if (!winner.IsValid()) return;
            int idx = winner.Index();
            _scores[idx] = Mathf.Max(_scores[idx], Mathf.Max(1, Rules.roundsToWin));
            MatchWinner = winner;
        }

        private void CheckRoundEnd()
        {
            if (Phase == MatchPhase.Playing && IsAnyTeamWipedOut()) _roundEndRequested = true;
        }

        private bool IsAnyTeamWipedOut() => CountInfield(TeamId.Home) == 0 || CountInfield(TeamId.Away) == 0;

        // ------------------------------------------------------------------ spawning

        private void SpawnTeam(TeamId team, HeroId[] heroes)
        {
            if (heroes == null) return;
            for (int slot = 0; slot < heroes.Length; slot++)
            {
                bool isHuman = !_setup.Spectate && team == _setup.LocalTeam && slot == 0;
                SpawnPlayer(team, slot, heroes.Length, heroes[slot], isHuman);
            }
        }

        private void SpawnPlayer(TeamId team, int slot, int teamSize, HeroId hero, bool isHuman)
        {
            int id = _players.Count + 1;
            var character = ResolveCharacter(hero);
            var position = Court.GetSpawnPoint(team, slot, teamSize);
            var rotation = Court.GetSpawnRotation(team);

            var go = new GameObject($"P{id}_{hero}_{team}");
            go.transform.SetParent(PlayersRoot, false);
            go.transform.SetPositionAndRotation(position, rotation);

            var player = go.AddComponent<DodgeballPlayer>();
            IIntentSource source;
            BotBrain brain = null;
            if (isHuman)
            {
                _humanInput = go.AddComponent<HumanInputSource>();
                source = _humanInput;
            }
            else
            {
                brain = go.AddComponent<BotBrain>();
                source = brain;
            }

            var info = new PlayerSpawnInfo
            {
                PlayerId = id,
                DisplayName = character != null && !string.IsNullOrEmpty(character.displayName) ? character.displayName : hero.ToString(),
                Team = team,
                Character = character,
                IsHuman = isHuman,
                IsLocal = isHuman,
                IntentSource = source,
                Position = position,
                Rotation = rotation,
            };

            try
            {
                player.Initialize(info);
            }
            catch (Exception e)
            {
                Debug.LogException(e, player);
            }

            if (brain != null)
            {
                try { brain.Initialize(player, _setup.Difficulty); }
                catch (Exception e) { Debug.LogException(e, brain); }
            }

            try
            {
                if (player.Abilities != null) player.Abilities.PassiveUltGainPerSecond = Rules.ultGainPerSecond;
                if (player.Motor != null) player.Motor.SetConfinement(Court.GetConfinement(team, CourtZone.Infield));
                if (player.Visual != null) player.Visual.SetTeamColor(GetTeamColor(team));
            }
            catch (Exception e)
            {
                Debug.LogException(e, player);
            }
            player.InputLocked = true;

            _players.Add(player);
            _records.Add(new PlayerRecord { Player = player, Team = team, Slot = slot });
            if (isHuman) LocalPlayer = player;
        }

        private CharacterData ResolveCharacter(HeroId hero)
        {
            var data = _config != null ? _config.GetHero(hero) : null;
            if (data != null) return data;
            if (_fallbackHeroes.TryGetValue(hero, out data) && data != null) return data;

            try
            {
                data = HeroRosterFactory.CreateHero(hero);
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }

            if (data == null)
            {
                // Last resort: a stat-only hero so the match still runs (the visual shows the labelled placeholder).
                data = ScriptableObject.CreateInstance<CharacterData>();
                data.heroId = hero;
                data.displayName = hero.ToString();
                data.name = $"Hero_{hero} (Fallback)";
            }
            Debug.LogWarning($"[Dodgeball Ultra] '{hero}' is not in the GameConfig roster; using an in-memory definition.", this);
            _fallbackHeroes[hero] = data;
            return data;
        }

        // ------------------------------------------------------------------ round helpers

        private void ResetPlayersForRound()
        {
            for (int i = 0; i < _records.Count; i++)
            {
                var r = _records[i];
                r.ClearRoundState();
                var p = r.Player;
                if (p == null) continue;

                var position = Court.GetSpawnPoint(r.Team, r.Slot, TeamSize(r.Team));
                var rotation = Court.GetSpawnRotation(r.Team);
                try
                {
                    if (p.Motor != null) p.Motor.SetConfinement(Court.GetConfinement(r.Team, CourtZone.Infield));
                    p.ResetForRound(position, rotation);
                    if (p.Zone != CourtZone.Infield) p.SetZone(CourtZone.Infield);
                    if (p.Abilities != null)
                    {
                        p.Abilities.PassiveUltGainPerSecond = Rules.ultGainPerSecond;
                        if (!Rules.keepUltimateBetweenRounds && RoundNumber > 1) p.Abilities.SetUltimateCharge(0f);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogException(e, p);
                }
                p.InputLocked = true;
            }
        }

        /// <summary>Round/match break: nobody can act, pending throws/catches are cancelled and abilities stop.</summary>
        private void FreezePlayersForBreak()
        {
            for (int i = 0; i < _players.Count; i++)
            {
                var p = _players[i];
                if (p == null) continue;
                p.InputLocked = true;
                try
                {
                    // A neutral intent would otherwise read as "throw released" and launch a ball after the whistle.
                    if (p.Combat != null)
                    {
                        if (p.Combat.IsCharging) p.Combat.CancelCharge();
                        if (p.Combat.IsCatchArmed) p.Combat.CancelCatch();
                    }
                    if (p.Abilities != null) p.Abilities.InterruptAll(InterruptReason.RoundEnded);
                }
                catch (Exception e)
                {
                    Debug.LogException(e, p);
                }
            }
        }

        /// <summary>Winners cheer, standing losers show defeat (ragdolling players are left alone).</summary>
        private void PlayCelebrations(TeamId winner)
        {
            if (!winner.IsValid()) return;
            for (int i = 0; i < _players.Count; i++)
            {
                var p = _players[i];
                if (p == null || p.Visual == null || p.Visual.AnimatorDriver == null) continue;
                if (p.Visual.Ragdoll != null && p.Visual.Ragdoll.IsRagdolled) continue;
                try
                {
                    if (p.Team == winner) p.Visual.AnimatorDriver.TriggerCheer();
                    else p.Visual.AnimatorDriver.TriggerDefeat();
                }
                catch (Exception e)
                {
                    Debug.LogException(e, p);
                }
            }
        }

        private void SetAllInputLocked(bool locked)
        {
            for (int i = 0; i < _records.Count; i++)
            {
                var r = _records[i];
                if (r.Player == null) continue;
                // Ragdolling players stay locked until they reach the outfield.
                r.Player.InputLocked = locked || r.IsAwaitingOutfield;
            }
        }

        private void CompletePendingTransfer(PlayerRecord r)
        {
            r.OutfieldTransferAt = -1f;
            var p = r.Player;
            if (p == null) return;

            // Saved during the ragdoll (e.g. an ability revived them): they stay in the infield.
            if (p.Zone == CourtZone.Infield && p.Health != null && !p.Health.IsEliminated)
            {
                r.EliminatedAt = -1f;
                return;
            }
            if (Court == null) return;

            MoveToOutfield(r);
        }

        private Vector3 FindFreeInfieldSpawn(PlayerRecord target)
        {
            // Candidate slots = the team's spawn line; pick the one farthest from everybody already on that half,
            // with a small preference for the player's own slot.
            int slots = Mathf.Max(3, TeamSize(target.Team));
            var best = Court.GetSpawnPoint(target.Team, target.Slot % slots, slots);
            float bestScore = float.MinValue;

            for (int s = 0; s < slots; s++)
            {
                var candidate = Court.GetSpawnPoint(target.Team, s, slots);
                float nearestSqr = float.MaxValue;
                for (int i = 0; i < _records.Count; i++)
                {
                    var other = _records[i];
                    if (other == target || other.Player == null || other.Player.Zone != CourtZone.Infield) continue;
                    var d = other.Player.Position - candidate;
                    d.y = 0f;
                    nearestSqr = Mathf.Min(nearestSqr, d.sqrMagnitude);
                }

                float score = Mathf.Sqrt(Mathf.Min(nearestSqr, 1e6f)) + (s == target.Slot ? 0.5f : 0f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            return best;
        }

        private int FindFreeOutfieldSpot(PlayerRecord target)
        {
            // Smallest spot index not used by a teammate already in the outfield.
            for (int spot = 0; spot < 32; spot++)
            {
                bool used = false;
                for (int i = 0; i < _records.Count; i++)
                {
                    var r = _records[i];
                    if (r == target || r.Team != target.Team || r.OutfieldSpot != spot) continue;
                    if (r.Player != null && r.Player.Zone == CourtZone.Outfield)
                    {
                        used = true;
                        break;
                    }
                }
                if (!used) return spot;
            }
            return 0;
        }

        private int TeamSize(TeamId team)
        {
            int n = 0;
            for (int i = 0; i < _records.Count; i++) if (_records[i].Team == team) n++;
            return Mathf.Max(1, n);
        }

        private static bool IsInfieldAlive(PlayerRecord r)
        {
            var p = r.Player;
            if (p == null || !p.isActiveAndEnabled) return false;
            if (p.Zone != CourtZone.Infield || r.IsAwaitingOutfield) return false;
            var h = p.Health;
            // A pending (delayed) elimination still counts as infield until it resolves.
            if (h != null && h.IsEliminated && !h.IsEliminationPending) return false;
            return true;
        }

        private static void ClearDebuffs(DodgeballPlayer player)
        {
            var status = player.Status;
            if (status == null) return;
            // Only harmful, positional effects: passives such as Silent Footsteps must survive a zone change.
            status.Remove(StatusEffectType.Frozen);
            status.Remove(StatusEffectType.Slow);
            status.Remove(StatusEffectType.Stunned);
            status.Remove(StatusEffectType.Rooted);
            status.Remove(StatusEffectType.Slippery);
            status.Remove(StatusEffectType.DodgeDisabled);
        }

        private PlayerRecord FindRecord(DodgeballPlayer player)
        {
            if (player == null) return null;
            for (int i = 0; i < _records.Count; i++)
                if (_records[i].Player == player) return _records[i];
            return null;
        }

        // ------------------------------------------------------------------ camera / input helpers

        private void FocusCameraForPlay()
        {
            var rig = ThirdPersonCameraRig.Instance;
            if (rig == null) return;
            if (LocalPlayer != null)
            {
                rig.SetCinematicFocus(null);
                rig.SetTarget(LocalPlayer, true);
            }
            else
            {
                // Spectating: a wide orbit over the whole court.
                rig.SetCinematicFocus(Court.Center + Vector3.up * cinematicFocusHeight, spectateCameraDistance);
            }
        }

        private void FocusCameraOnTeam(TeamId team)
        {
            var rig = ThirdPersonCameraRig.Instance;
            if (rig == null || Court == null) return;

            var focus = Court.Center;
            if (team.IsValid())
            {
                var sum = Vector3.zero;
                int n = 0;
                for (int i = 0; i < _players.Count; i++)
                {
                    var p = _players[i];
                    if (p == null || p.Team != team) continue;
                    sum += p.Position;
                    n++;
                }
                if (n > 0) focus = sum / n;
            }
            focus.y = Court.FloorY + cinematicFocusHeight;
            rig.SetCinematicFocus(focus, team.IsValid() ? victoryCameraDistance : introCameraDistance);
        }

        private void SetHumanCursorLocked(bool locked)
        {
            if (_humanInput == null) return;
            try { _humanInput.SetCursorLocked(locked); }
            catch (Exception e) { Debug.LogException(e, _humanInput); }
        }

        // ------------------------------------------------------------------ internals

        private void SetPhase(MatchPhase next)
        {
            if (Phase == next) return;
            var previous = Phase;
            Phase = next;
            GameEvents.Publish(new MatchPhaseChangedEvent { Previous = previous, Current = next });
            try
            {
                PhaseChanged?.Invoke(previous, next);
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        private void StopFlow()
        {
            // Nested phase coroutines are children of the flow; this component runs no other coroutine.
            StopAllCoroutines();
            _flowRunning = false;
        }

        /// <summary>Stops the flow and destroys every spawned player (balls are left to the BallManager).</summary>
        private void TearDown()
        {
            StopFlow();
            SetHumanCursorLocked(false);

            for (int i = 0; i < _players.Count; i++)
            {
                var p = _players[i];
                if (p == null) continue;
                try
                {
                    // Release a held ball first so a match ball parented to a hand socket is not destroyed with the body.
                    if (p.Combat != null && p.Combat.HasBall) p.Combat.DropBall(Vector3.zero);
                }
                catch (Exception e)
                {
                    Debug.LogException(e, p);
                }
                // Leave the registry and the physics scene now; Destroy only completes at the end of the frame.
                PlayerRegistry.Unregister(p);
                p.gameObject.SetActive(false);
                Destroy(p.gameObject);
            }

            _players.Clear();
            _records.Clear();
            _humanInput = null;
            LocalPlayer = null;
            _roundEndRequested = false;
            _roundTimeUp = false;
            _forfeitingTeam = TeamId.None;
            _lastPerfectReviveFrame = -1;
            _lastPerfectReviver = null;
            _lastPerfectRevived = null;
            RoundTimeRemaining = 0f;
        }

        private Court ResolveCourt()
        {
            if (Court != null) return Court;
            var court = Court.Instance != null ? Court.Instance : FindFirstObjectByType<Court>();
            if (court != null) return court;

            try
            {
                return Arena.RuntimeArenaBuilder.Build();
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
                return null;
            }
        }

        private MatchRules DefaultRules
        {
            get
            {
                if (_defaultRules == null)
                {
                    _defaultRules = ScriptableObject.CreateInstance<MatchRules>();
                    _defaultRules.name = "MatchRules (Default)";
                }
                return _defaultRules;
            }
        }

        private Transform PlayersRoot
        {
            get
            {
                if (_playersRoot == null) _playersRoot = new GameObject("Match Players").transform;
                return _playersRoot;
            }
        }

        /// <summary>True while the round-flow coroutine is running (diagnostics / tests).</summary>
        public bool IsFlowRunning => _flowRunning;
    }
}
