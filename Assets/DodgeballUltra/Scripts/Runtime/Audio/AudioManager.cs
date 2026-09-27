using System;
using UnityEngine;

namespace DodgeballUltra.Audio
{
    public enum SfxId
    {
        Throw = 0,
        ThrowHeavy,
        BallHitPlayer,
        BallHitHeavy,
        BallBounceFloor,
        BallBounceWall,
        Catch,
        PerfectCatch,
        CatchWhiff,
        Pickup,
        Pass,
        Footstep,
        Jump,
        Land,
        Slide,
        Whistle,
        Countdown,
        RoundStart,
        Elimination,
        Revive,
        AbilityCast,
        UltimateCast,
        UltimateReady,
        Shockwave,
        Beam,
        Freeze,
        Teleport,
        Glue,
        Turret,
        Shield,
        Magnet,
        Stasis,
        Rewind,
        Cloak,
        Clone,
        Earthquake,
        CrowdCheer,
        CrowdGasp,
        CrowdAmbience,
        UiClick,
        UiConfirm,
    }

    /// <summary>
    /// CONTRACT (kernel) - 3D/2D sound playback with pooled AudioSources. Reacts to game events on its own (hits, catches,
    /// throws, bounces, round flow). Uses clips from an AudioLibrary asset when present, otherwise synthesises realistic-ish
    /// placeholder clips at startup (thumps, whooshes, slaps, whistle, crowd noise) so the game is never silent.
    /// <para>Owner module: Audio.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AudioManager : MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }

        // IMPLEMENT: Audio module
        public void Play(SfxId id, Vector3 position, float volume = 1f, float pitch = 1f) => throw new NotImplementedException();
        public void Play2D(SfxId id, float volume = 1f, float pitch = 1f) => throw new NotImplementedException();

        /// <summary>Null-safe shortcut.</summary>
        public static void PlayAt(SfxId id, Vector3 position, float volume = 1f, float pitch = 1f)
        {
            if (Instance != null) Instance.Play(id, position, volume, pitch);
        }

        /// <summary>Null-safe shortcut.</summary>
        public static void PlayUi(SfxId id, float volume = 1f, float pitch = 1f)
        {
            if (Instance != null) Instance.Play2D(id, volume, pitch);
        }
    }
}
