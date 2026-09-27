using System;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.AI
{
    /// <summary>
    /// CONTRACT (kernel) - utility-AI controller implementing <see cref="IIntentSource"/>, so bots use exactly the same
    /// code paths as humans. Behaviours: grab loose balls (opening rush), pick targets, charge &amp; throw with human-like
    /// reaction times, dodge (sidestep/jump/slide) predicted impacts, attempt timed catches (skill-dependent timing noise
    /// around the perfect window), pass, play from the outfield, and use abilities via AbilityBase.EvaluateAIUtility.
    /// <para>Owner module: AI.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BotBrain : MonoBehaviour, IIntentSource
    {
        public BotDifficulty Difficulty { get; private set; } = BotDifficulty.Normal;

        // IMPLEMENT: AI module
        public void Initialize(DodgeballPlayer owner, BotDifficulty difficulty) => throw new NotImplementedException();

        public PlayerIntent Sample(DodgeballPlayer player, float deltaTime) => throw new NotImplementedException();
    }
}
