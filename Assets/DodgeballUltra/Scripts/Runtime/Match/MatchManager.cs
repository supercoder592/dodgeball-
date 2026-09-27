using System;
using System.Collections.Generic;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Match
{
    /// <summary>
    /// CONTRACT (kernel) - authoritative match flow: spawning 3v3, rounds (pre-round -> countdown -> playing -> round end),
    /// scoring, infield/outfield moves, revivals (perfect catch, outfield hit), round timer and win conditions.
    /// Reacts to PlayerEliminatedEvent (ragdoll then move to outfield) and BallCaughtEvent (perfect catch revive).
    /// Publishes all match-flow events.
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

        // IMPLEMENT: Match module
        public IReadOnlyList<DodgeballPlayer> Players => throw new NotImplementedException();

        public int GetScore(TeamId team) => throw new NotImplementedException();

        /// <summary>Spawns all players for <paramref name="setup"/> and starts round 1.</summary>
        public void StartMatch(MatchSetup setup, GameConfig config) => throw new NotImplementedException();

        /// <summary>Tear everything down (return to hero select).</summary>
        public void EndMatch() => throw new NotImplementedException();

        /// <summary>Moves an eliminated player to the outfield (after the ragdoll time). Checks round end.</summary>
        public void SendToOutfield(DodgeballPlayer player) => throw new NotImplementedException();

        /// <summary>Returns an outfield player to the infield. Returns false if not possible.</summary>
        public bool ReviveFromOutfield(DodgeballPlayer player, RevivalCause cause, DodgeballPlayer reviver = null) => throw new NotImplementedException();

        /// <summary>Perfect Catch reward: revives the teammate who has been in the outfield the longest. Returns them or null.</summary>
        public DodgeballPlayer ReviveOneOutfieldTeammate(TeamId team, RevivalCause cause, DodgeballPlayer reviver = null) => throw new NotImplementedException();

        public int CountInfield(TeamId team) => throw new NotImplementedException();
    }
}
