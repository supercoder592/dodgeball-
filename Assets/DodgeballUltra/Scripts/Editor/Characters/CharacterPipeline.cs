using System;
using System.Collections.Generic;
using DodgeballUltra.Characters;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>
    /// CONTRACT (kernel) - entry points of the realistic-human character pipeline (Microsoft Rocketbox, MIT):
    /// download -> Humanoid import (Biped map + enforced T-pose) -> PBR materials (skin SSS, hair alpha, smoothness from
    /// specular) -> LODGroup prefab -> generated motion-capture AnimatorControllers -> links into CharacterData.
    /// Downloads go to Assets/ThirdParty/Rocketbox/ (git-ignored); generated assets to Assets/DodgeballUltra/Generated/.
    /// <para>Owner module: Editor/Characters. Called by the Setup Wizard.</para>
    /// </summary>
    public static class CharacterPipeline
    {
        public const string DownloadRoot = "Assets/ThirdParty/Rocketbox";
        public const string GeneratedRoot = "Assets/DodgeballUltra/Generated/Characters";

        // IMPLEMENT: Editor/Characters module

        /// <summary>Avatar folder names needed by <paramref name="roster"/> (CharacterData.rocketboxAvatar).</summary>
        public static List<string> GetRequiredAvatars(IList<CharacterData> roster) => throw new NotImplementedException();

        /// <summary>True when every avatar (FBX + textures) and the animation set are present on disk.</summary>
        public static bool AreAssetsDownloaded(IList<string> avatarNames) => throw new NotImplementedException();

        /// <summary>
        /// Downloads avatars (FBX + textures + portrait) and the curated animation set, pinned to the manifest commit.
        /// Non-blocking (EditorApplication.update driven). <paramref name="onProgress"/> (0..1, message);
        /// <paramref name="onComplete"/> (success, message). Already-present files are skipped.
        /// </summary>
        public static void DownloadAsync(IList<string> avatarNames, bool includeAnimations, Action<float, string> onProgress,
            Action<bool, string> onComplete) => throw new NotImplementedException();

        /// <summary>Cancels a running download.</summary>
        public static void CancelDownload() => throw new NotImplementedException();

        public static bool IsDownloading => throw new NotImplementedException();

        /// <summary>
        /// Imports/configures everything for <paramref name="roster"/> and assigns modelPrefab, animatorController and
        /// portrait on each CharacterData (and marks them dirty). Returns a human-readable report.
        /// </summary>
        public static string BuildCharacters(IList<CharacterData> roster) => throw new NotImplementedException();
    }
}
