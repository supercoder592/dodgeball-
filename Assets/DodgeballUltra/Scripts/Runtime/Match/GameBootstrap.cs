using System;
using UnityEngine;

namespace DodgeballUltra.Match
{
    /// <summary>
    /// CONTRACT (kernel) - scene entry point. Ensures every manager exists (MatchManager, BallManager, JuiceManager,
    /// VfxManager, AudioManager, camera rig, HUD, Court), configures GameLayers, loads GameConfig (or builds the default
    /// in-memory roster via HeroRosterFactory), builds the runtime arena if the scene has none, then shows hero select
    /// (or auto-starts when <see cref="autoStart"/>).
    /// <para>Owner module: Match.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-1000)]
    public sealed class GameBootstrap : MonoBehaviour
    {
        public GameConfig config;
        [Tooltip("Skip hero select and start immediately with the given hero (useful for testing).")]
        public bool autoStart;
        public HeroId autoStartHero = HeroId.Rayne;
        public AI.BotDifficulty autoStartDifficulty = AI.BotDifficulty.Normal;

        public static GameBootstrap Instance { get; private set; }
        public GameConfig ActiveConfig { get; private set; }

        // IMPLEMENT: Match module
        public void StartMatch(MatchSetup setup) => throw new NotImplementedException();
        public void ReturnToHeroSelect() => throw new NotImplementedException();
    }
}
