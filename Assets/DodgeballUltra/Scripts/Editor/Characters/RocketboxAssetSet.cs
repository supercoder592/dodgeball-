using System;
using System.Collections.Generic;
using System.IO;
using DodgeballUltra.Characters;
using UnityEngine;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>What a curated motion-capture clip is used for in the generated AnimatorControllers.</summary>
    public enum RocketboxClipRole
    {
        /// <summary>Locomotion blend tree at Speed 0 (standing idle).</summary>
        Idle = 0,
        /// <summary>Locomotion blend tree at AnimatorParams.WalkSpeed.</summary>
        Walk,
        /// <summary>Locomotion blend tree at AnimatorParams.RunSpeed.</summary>
        Run,
        /// <summary>Locomotion blend tree at AnimatorParams.SprintSpeed.</summary>
        Sprint,
        /// <summary>Crouch state (slides, catch stance) and the tucked Airborne pose.</summary>
        Crouch,
        /// <summary>Stunned state (dizzy sway).</summary>
        Stunned,
        /// <summary>Cheer state (round / match won).</summary>
        Cheer,
        /// <summary>Defeat state (shrug, round / match lost).</summary>
        Defeat,
    }

    /// <summary>A curated Rocketbox motion-capture clip (downloaded for both the m_ and f_ skeletons).</summary>
    public readonly struct RocketboxClip
    {
        /// <summary>Animator usage.</summary>
        public readonly RocketboxClipRole Role;
        /// <summary>
        /// Repository folder suffix: "static" (in place, <c>all_animations_max_motextr_static</c>) or "xy" (planar root
        /// motion, <c>all_animations_max_motextr_xy</c>).
        /// </summary>
        public readonly string Folder;
        /// <summary>File base name without gender prefix and ".max.fbx", e.g. "walk_neutral_01".</summary>
        public readonly string BaseName;
        /// <summary>Loop the clip (Loop Time + Loop Pose).</summary>
        public readonly bool Loop;

        public RocketboxClip(RocketboxClipRole role, string folder, string baseName, bool loop)
        {
            Role = role;
            Folder = folder;
            BaseName = baseName;
            Loop = loop;
        }
    }

    /// <summary>
    /// Single source of truth for which Rocketbox files the game uses and where they live in the project.
    /// Mirrored by <c>Tools/fetch_rocketbox.py</c> (HERO_AVATARS / CURATED_ANIMATIONS) - keep both in sync.
    /// <para>Layout under <see cref="CharacterPipeline.DownloadRoot"/>:</para>
    /// <code>
    /// Avatars/&lt;Name&gt;/Export/&lt;Name&gt;.fbx        rigged model (3ds Max Biped, hipoly 81 bones)
    /// Avatars/&lt;Name&gt;/Textures/*.tga               color / normal / specular (+ opacity RGBA for hair cards)
    /// Avatars/&lt;Name&gt;/&lt;Name&gt;.png                portrait (imported as Sprite)
    /// Animations/{m_,f_}&lt;clip&gt;.max.fbx             curated motion-capture clips
    /// LICENSE-Rocketbox.txt                          MIT license text (Copyright (c) 2020 Microsoft)
    /// </code>
    /// </summary>
    public static class RocketboxAssetSet
    {
        /// <summary>Downloaded avatars folder.</summary>
        public const string AvatarsFolder = CharacterPipeline.DownloadRoot + "/Avatars";
        /// <summary>Downloaded animation clips folder.</summary>
        public const string AnimationsFolder = CharacterPipeline.DownloadRoot + "/Animations";
        /// <summary>License file written next to the downloaded content.</summary>
        public const string LicenseFileName = "LICENSE-Rocketbox.txt";
        /// <summary>Suffix of every Rocketbox animation file.</summary>
        public const string AnimationSuffix = ".max.fbx";
        /// <summary>Generated animator controllers / avatar masks.</summary>
        public const string GeneratedAnimationFolder = "Assets/DodgeballUltra/Generated/Animation";
        /// <summary>Folder scanned for optional authored clips (names containing "throw" / "catch").</summary>
        public const string CustomAnimationFolder = "Assets/DodgeballUltra/Art/Animations/Custom";
        /// <summary>Manifest path relative to the project root.</summary>
        public const string ManifestRelativePath = "Tools/rocketbox_manifest.json";

        /// <summary>Repository folder prefix of the motion-capture clips (followed by "static" / "xy" / "xyz").</summary>
        public const string AnimationRepositoryPrefix = "Assets/Animations/all_animations_max_motextr_";

        /// <summary>
        /// Curated motion-capture set. Each clip exists for the male (m_) and female (f_) skeleton in the pinned commit:
        /// idle / crouch / drunk / cheer / shrug from <c>all_animations_max_motextr_static</c> (in place), walk / run / sprint
        /// from <c>all_animations_max_motextr_xy</c> (planar root motion, kept as root motion so the clips play in place
        /// with applyRootMotion off while their natural stride speed stays measurable).
        /// </summary>
        public static readonly RocketboxClip[] CuratedClips =
        {
            new RocketboxClip(RocketboxClipRole.Idle, "static", "idle_neutral_01", true),
            new RocketboxClip(RocketboxClipRole.Walk, "xy", "walk_neutral_01", true),
            new RocketboxClip(RocketboxClipRole.Run, "xy", "run_neutral_01", true),
            new RocketboxClip(RocketboxClipRole.Sprint, "xy", "run_fast_01", true),
            new RocketboxClip(RocketboxClipRole.Crouch, "static", "crouch_idle", true),
            new RocketboxClip(RocketboxClipRole.Stunned, "static", "idle_drunk_01", true),
            new RocketboxClip(RocketboxClipRole.Cheer, "static", "cheer_01", false),
            new RocketboxClip(RocketboxClipRole.Defeat, "static", "gestic_shrug_01", false),
        };

        private static readonly string[] s_textureExtensions = { ".tga", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".psd", ".exr" };

        /// <summary>Wrinkle normal maps are for Rocketbox's facial-animation shader; our materials never sample them.</summary>
        private static readonly string[] s_skippedTextureMarkers = { "_wrinkle", " - wr" };

        // ----------------------------------------------------------------------------------------------- manifest

        private static RocketboxManifest s_manifest;
        private static DateTime s_manifestStamp;
        private static string s_manifestError;

        /// <summary>Absolute path of the Unity project root (parent of Assets/).</summary>
        public static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

        /// <summary>Absolute path of <see cref="ManifestRelativePath"/>.</summary>
        public static string ManifestAbsolutePath => Path.Combine(ProjectRoot, ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Last manifest load error (null when the manifest loaded).</summary>
        public static string ManifestError => s_manifestError;

        /// <summary>
        /// The manifest, cached and reloaded when the file changes. Returns null (and sets <see cref="ManifestError"/>)
        /// when the file is missing or malformed - callers report instead of throwing.
        /// </summary>
        public static RocketboxManifest LoadManifest()
        {
            string path = ManifestAbsolutePath;
            try
            {
                if (!File.Exists(path))
                {
                    s_manifestError = $"Rocketbox manifest not found at '{ManifestRelativePath}'.";
                    s_manifest = null;
                    return null;
                }

                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (s_manifest != null && stamp == s_manifestStamp) return s_manifest;

                s_manifest = RocketboxManifest.LoadFromFile(path);
                s_manifestStamp = stamp;
                s_manifestError = null;
                return s_manifest;
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is UnauthorizedAccessException)
            {
                s_manifestError = $"Rocketbox manifest '{ManifestRelativePath}' could not be read: {ex.Message}";
                s_manifest = null;
                return null;
            }
        }

        // ---------------------------------------------------------------------------------------------- avatar paths

        /// <summary>Project folder of an avatar (e.g. Assets/ThirdParty/Rocketbox/Avatars/Sports_Male_02).</summary>
        public static string AvatarFolder(string avatar) => AvatarsFolder + "/" + avatar;

        /// <summary>Textures folder of an avatar.</summary>
        public static string AvatarTexturesFolder(string avatar) => AvatarFolder(avatar) + "/Textures";

        /// <summary>
        /// Model asset path. Uses the manifest's model path when available, otherwise the Rocketbox convention
        /// Export/&lt;Name&gt;.fbx.
        /// </summary>
        public static string ModelAssetPath(string avatar, RocketboxAvatarEntry entry = null)
            => AvatarFolder(avatar) + "/" + (entry != null && !string.IsNullOrEmpty(entry.model) ? entry.model : "Export/" + avatar + ".fbx");

        /// <summary>Portrait asset path (&lt;Name&gt;.png next to Export/).</summary>
        public static string PortraitAssetPath(string avatar, RocketboxAvatarEntry entry = null)
            => AvatarFolder(avatar) + "/" + (entry != null && !string.IsNullOrEmpty(entry.portrait) ? entry.portrait : avatar + ".png");

        /// <summary>Generated per-avatar folder (materials, mask maps, stripped meshes).</summary>
        public static string GeneratedAvatarFolder(string avatar) => CharacterPipeline.GeneratedRoot + "/" + avatar;

        /// <summary>True for an image texture the pipeline downloads/uses (skips .mat/.shader files and wrinkle maps).</summary>
        public static bool IsWantedTexture(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return false;
            string lower = relativePath.ToLowerInvariant();
            bool image = false;
            foreach (string ext in s_textureExtensions)
            {
                if (lower.EndsWith(ext, StringComparison.Ordinal))
                {
                    image = true;
                    break;
                }
            }
            if (!image) return false;
            foreach (string marker in s_skippedTextureMarkers)
                if (lower.Contains(marker)) return false;
            return true;
        }

        /// <summary>
        /// Files of one avatar as (repository path, project asset path) pairs: model, wanted textures and (optionally) portrait.
        /// </summary>
        public static List<KeyValuePair<string, string>> GetAvatarFiles(RocketboxAvatarEntry entry, bool includePortrait)
        {
            var files = new List<KeyValuePair<string, string>>();
            if (entry == null) return files;
            string folder = AvatarFolder(entry.name);
            files.Add(new KeyValuePair<string, string>(entry.RepositoryPath(entry.model), folder + "/" + entry.model));
            foreach (string tex in entry.textures)
                if (IsWantedTexture(tex))
                    files.Add(new KeyValuePair<string, string>(entry.RepositoryPath(tex), folder + "/" + tex));
            if (includePortrait && !string.IsNullOrEmpty(entry.portrait))
                files.Add(new KeyValuePair<string, string>(entry.RepositoryPath(entry.portrait), folder + "/" + entry.portrait));
            return files;
        }

        // --------------------------------------------------------------------------------------------------- clips

        /// <summary>Gender prefix of Rocketbox clip files.</summary>
        public static string GenderPrefix(BodyType body) => body == BodyType.Female ? "f_" : "m_";

        /// <summary>Clip file name, e.g. m_walk_neutral_01.max.fbx.</summary>
        public static string ClipFileName(BodyType body, string baseName) => GenderPrefix(body) + baseName + AnimationSuffix;

        /// <summary>Project asset path of a curated clip file.</summary>
        public static string ClipAssetPath(BodyType body, string baseName) => AnimationsFolder + "/" + ClipFileName(body, baseName);

        /// <summary>
        /// Repository path of a curated clip for <paramref name="body"/>: the manifest entry when the manifest lists the file
        /// (in the clip's folder first, then anywhere), otherwise the conventional
        /// <c>Assets/Animations/all_animations_max_motextr_&lt;folder&gt;/&lt;file&gt;</c>.
        /// </summary>
        public static string ClipRepositoryPath(BodyType body, RocketboxClip clip, RocketboxManifest manifest)
        {
            string file = ClipFileName(body, clip.BaseName);
            string conventional = AnimationRepositoryPrefix + clip.Folder + "/" + file;
            if (manifest == null || manifest.animations == null || manifest.animations.Count == 0) return conventional;
            foreach (string path in manifest.animations)
                if (string.Equals(path, conventional, StringComparison.OrdinalIgnoreCase)) return path;
            return manifest.FindAnimationPath(file) ?? conventional;
        }

        /// <summary>
        /// Curated clip files for both skeletons as (repository path, project asset path) pairs.
        /// </summary>
        public static List<KeyValuePair<string, string>> GetAnimationFiles(RocketboxManifest manifest)
        {
            var files = new List<KeyValuePair<string, string>>(CuratedClips.Length * 2);
            foreach (RocketboxClip c in CuratedClips)
            {
                foreach (BodyType body in new[] { BodyType.Male, BodyType.Female })
                    files.Add(new KeyValuePair<string, string>(ClipRepositoryPath(body, c, manifest), ClipAssetPath(body, c.BaseName)));
            }
            return files;
        }

        /// <summary>Curated clip definition for a role.</summary>
        public static RocketboxClip GetClip(RocketboxClipRole role)
        {
            foreach (RocketboxClip c in CuratedClips)
                if (c.Role == role) return c;
            throw new ArgumentOutOfRangeException(nameof(role), role, "No curated clip for this role.");
        }

        /// <summary>All curated clip file names (male and female).</summary>
        public static List<string> GetCuratedClipFileNames()
        {
            var list = new List<string>(CuratedClips.Length * 2);
            foreach (RocketboxClip c in CuratedClips)
            {
                list.Add(ClipFileName(BodyType.Male, c.BaseName));
                list.Add(ClipFileName(BodyType.Female, c.BaseName));
            }
            return list;
        }

        /// <summary>
        /// Clip name used inside Unity for a Rocketbox clip file ("m_walk_neutral_01.max.fbx" -> "m_walk_neutral_01").
        /// </summary>
        public static string ClipNameFromFile(string fileOrPath)
        {
            string file = Path.GetFileName(fileOrPath) ?? string.Empty;
            if (file.EndsWith(AnimationSuffix, StringComparison.OrdinalIgnoreCase)) return file.Substring(0, file.Length - AnimationSuffix.Length);
            return Path.GetFileNameWithoutExtension(file);
        }

        /// <summary>Body type of a Rocketbox clip file from its m_/f_ prefix (null when unknown).</summary>
        public static BodyType? BodyTypeOfClipFile(string fileOrPath)
        {
            string file = Path.GetFileName(fileOrPath) ?? string.Empty;
            if (file.StartsWith("f_", StringComparison.OrdinalIgnoreCase)) return BodyType.Female;
            if (file.StartsWith("m_", StringComparison.OrdinalIgnoreCase)) return BodyType.Male;
            return null;
        }

        /// <summary>
        /// True when a clip file should loop. Curated clips use their table value; unknown clips loop unless their name
        /// suggests a one-shot gesture (cheer, wave, clap, start/stop, turn, sit down/stand up).
        /// </summary>
        public static bool IsLoopingClipFile(string fileOrPath)
        {
            string name = ClipNameFromFile(fileOrPath).ToLowerInvariant();
            if (name.Length > 2 && name[1] == '_') name = name.Substring(2);
            foreach (RocketboxClip c in CuratedClips)
                if (name == c.BaseName) return c.Loop;
            string[] oneShotTokens = { "cheer", "wave", "clap", "turn_", "sit_down", "stand_up", "_to_" };
            foreach (string s in oneShotTokens)
                if (name.Contains(s)) return false;
            string[] oneShotSuffixes = { "_start", "_stop", "_in", "_out" };
            foreach (string s in oneShotSuffixes)
                if (name.EndsWith(s, StringComparison.Ordinal)) return false;
            return true;
        }

        // ----------------------------------------------------------------------------------------------- paths/IO

        /// <summary>Absolute file-system path of a project-relative asset path.</summary>
        public static string ToAbsolutePath(string assetPath)
            => Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>True when the asset file exists on disk and is not empty.</summary>
        public static bool FileExistsNonEmpty(string assetPath)
        {
            try
            {
                var info = new FileInfo(ToAbsolutePath(assetPath));
                return info.Exists && info.Length > 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>True for any path inside the downloaded Rocketbox folder.</summary>
        public static bool IsRocketboxPath(string assetPath)
            => !string.IsNullOrEmpty(assetPath) && assetPath.StartsWith(CharacterPipeline.DownloadRoot + "/", StringComparison.OrdinalIgnoreCase);

        /// <summary>True for a downloaded avatar model (Avatars/&lt;Name&gt;/.../*.fbx).</summary>
        public static bool IsAvatarModelPath(string assetPath)
            => !string.IsNullOrEmpty(assetPath)
               && assetPath.StartsWith(AvatarsFolder + "/", StringComparison.OrdinalIgnoreCase)
               && assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)
               && !assetPath.EndsWith("_facial.fbx", StringComparison.OrdinalIgnoreCase);

        /// <summary>True for a downloaded animation clip file (Animations/*.fbx).</summary>
        public static bool IsAnimationPath(string assetPath)
            => !string.IsNullOrEmpty(assetPath)
               && assetPath.StartsWith(AnimationsFolder + "/", StringComparison.OrdinalIgnoreCase)
               && assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);

        /// <summary>Avatar folder name of a path inside Avatars/ (null otherwise).</summary>
        public static string AvatarNameFromPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith(AvatarsFolder + "/", StringComparison.OrdinalIgnoreCase)) return null;
            string rest = assetPath.Substring(AvatarsFolder.Length + 1);
            int slash = rest.IndexOf('/');
            return slash > 0 ? rest.Substring(0, slash) : null;
        }

        /// <summary>True when <paramref name="assetPath"/> is an avatar's portrait (Avatars/&lt;Name&gt;/&lt;Name&gt;.png).</summary>
        public static bool IsPortraitPath(string assetPath)
        {
            string avatar = AvatarNameFromPath(assetPath);
            return avatar != null && string.Equals(assetPath, AvatarFolder(avatar) + "/" + avatar + ".png", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The body type of an avatar: manifest gender, else a "_Female_" name token, else male.</summary>
        public static BodyType GetAvatarBodyType(string avatar, RocketboxManifest manifest = null)
        {
            RocketboxAvatarEntry entry = (manifest ?? LoadManifest())?.FindAvatar(avatar);
            if (entry != null && !string.IsNullOrEmpty(entry.gender)) return entry.BodyType;
            return avatar != null && avatar.IndexOf("female", StringComparison.OrdinalIgnoreCase) >= 0 ? BodyType.Female : BodyType.Male;
        }

        /// <summary>
        /// Default avatar of a hero - the casting of <see cref="HeroRosterFactory.GetDefaultAvatar"/> (single source of truth,
        /// also mirrored by <c>Tools/fetch_rocketbox.py</c> HERO_AVATARS).
        /// </summary>
        public static string GetDefaultAvatar(HeroId hero) => HeroRosterFactory.GetDefaultAvatar(hero);

        /// <summary>Replaces characters that are invalid in file names.</summary>
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Unnamed";
            char[] invalid = Path.GetInvalidFileNameChars();
            var chars = name.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == '/' || chars[i] == '\\') chars[i] = '_';
            return new string(chars);
        }
    }
}
