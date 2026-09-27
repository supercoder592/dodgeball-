using System;
using System.Collections.Generic;
using System.IO;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>
    /// Typed view of <c>Tools/rocketbox_manifest.json</c>: the pinned Microsoft Rocketbox commit, the raw download base
    /// URL, every avatar (model, portrait, textures, gender) and every motion-capture clip path in the repository.
    /// <para>
    /// The classes are <see cref="SerializableAttribute">[Serializable]</see> (so they can be inspected, cached or dumped
    /// with <c>JsonUtility.ToJson</c>), but reading goes through <see cref="MiniJsonReader"/> because the manifest keys
    /// avatars by name (a JSON object/dictionary, which <c>JsonUtility</c> cannot read).
    /// </para>
    /// <para>Pure C# (no UnityEngine): parsing is verified against the real manifest outside the editor.</para>
    /// </summary>
    [Serializable]
    public sealed class RocketboxManifest
    {
        /// <summary>Repository URL (informational).</summary>
        public string source;
        /// <summary>License statement (informational).</summary>
        public string license;
        /// <summary>Pinned commit hash every file is fetched from.</summary>
        public string commit;
        /// <summary>Base URL for raw file downloads, always ending with '/'.</summary>
        public string rawBaseUrl;
        /// <summary>Avatars in manifest order.</summary>
        public List<RocketboxAvatarEntry> avatars = new List<RocketboxAvatarEntry>();
        /// <summary>Repository-relative paths of every animation FBX.</summary>
        public List<string> animations = new List<string>();

        [NonSerialized] private Dictionary<string, RocketboxAvatarEntry> _avatarIndex;
        [NonSerialized] private Dictionary<string, string> _animationIndex;

        /// <summary>Avatar entry by folder name (case-insensitive), or null.</summary>
        public RocketboxAvatarEntry FindAvatar(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_avatarIndex == null)
            {
                _avatarIndex = new Dictionary<string, RocketboxAvatarEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (RocketboxAvatarEntry a in avatars)
                    if (a != null && !string.IsNullOrEmpty(a.name) && !_avatarIndex.ContainsKey(a.name)) _avatarIndex.Add(a.name, a);
            }
            return _avatarIndex.TryGetValue(name.Trim(), out RocketboxAvatarEntry e) ? e : null;
        }

        /// <summary>
        /// Repository path of an animation by file name (e.g. <c>m_walk_neutral_01.max.fbx</c>), or null. When a file name
        /// exists in several folders the first listed wins; the manifest lists the in-place ("motextr_static") folder first.
        /// </summary>
        public string FindAnimationPath(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            if (_animationIndex == null)
            {
                _animationIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in animations)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    string file = path.Substring(path.LastIndexOf('/') + 1);
                    if (!_animationIndex.ContainsKey(file)) _animationIndex.Add(file, path);
                }
            }
            return _animationIndex.TryGetValue(fileName, out string p) ? p : null;
        }

        /// <summary>Full download URL of a repository-relative path (each segment URL-escaped: some names contain spaces).</summary>
        public string GetUrl(string repositoryPath)
        {
            string[] segments = repositoryPath.Split('/');
            for (int i = 0; i < segments.Length; i++) segments[i] = Uri.EscapeDataString(segments[i]);
            return rawBaseUrl + string.Join("/", segments);
        }

        /// <summary>Reads and parses a manifest file. Throws <see cref="IOException"/> / <see cref="FormatException"/>.</summary>
        public static RocketboxManifest LoadFromFile(string path) => Parse(File.ReadAllText(path));

        /// <summary>Parses manifest JSON text. Throws <see cref="FormatException"/> when required members are missing.</summary>
        public static RocketboxManifest Parse(string json)
        {
            if (!(MiniJsonReader.Parse(json) is IDictionary<string, object> root))
                throw new FormatException("Rocketbox manifest: the document root is not a JSON object.");

            var manifest = new RocketboxManifest
            {
                source = MiniJsonReader.GetString(root, "source"),
                license = MiniJsonReader.GetString(root, "license"),
                commit = MiniJsonReader.GetString(root, "commit"),
                rawBaseUrl = MiniJsonReader.GetString(root, "rawBaseUrl"),
                animations = MiniJsonReader.GetStringList(root, "animations"),
            };

            if (string.IsNullOrEmpty(manifest.rawBaseUrl))
                throw new FormatException("Rocketbox manifest: 'rawBaseUrl' is missing.");
            if (!manifest.rawBaseUrl.EndsWith("/", StringComparison.Ordinal)) manifest.rawBaseUrl += "/";

            IDictionary<string, object> avatarObject = MiniJsonReader.GetObject(root, "avatars");
            if (avatarObject == null) throw new FormatException("Rocketbox manifest: 'avatars' object is missing.");

            // Enumerate in document order when available (stable listings in the wizard and reports).
            IEnumerable<string> keys = avatarObject is MiniJsonReader.JsonObject ordered ? ordered.OrderedKeys : avatarObject.Keys;
            foreach (string name in keys)
            {
                if (!(avatarObject[name] is IDictionary<string, object> a)) continue;
                var entry = new RocketboxAvatarEntry
                {
                    name = name,
                    category = MiniJsonReader.GetString(a, "category"),
                    path = (MiniJsonReader.GetString(a, "path") ?? string.Empty).TrimEnd('/'),
                    model = MiniJsonReader.GetString(a, "model"),
                    portrait = MiniJsonReader.GetString(a, "portrait"),
                    gender = MiniJsonReader.GetString(a, "gender"),
                    textures = MiniJsonReader.GetStringList(a, "textures"),
                };
                if (string.IsNullOrEmpty(entry.path) || string.IsNullOrEmpty(entry.model)) continue; // unusable entry
                manifest.avatars.Add(entry);
            }
            return manifest;
        }
    }

    /// <summary>One Rocketbox avatar of the manifest.</summary>
    [Serializable]
    public sealed class RocketboxAvatarEntry
    {
        /// <summary>Folder name, e.g. <c>Sports_Male_02</c> (the key in the manifest).</summary>
        public string name;
        /// <summary>Library category, e.g. <c>Professions</c>.</summary>
        public string category;
        /// <summary>Repository folder, e.g. <c>Assets/Avatars/Professions/Sports_Male_02</c>.</summary>
        public string path;
        /// <summary>Model path relative to <see cref="path"/>, e.g. <c>Export/Sports_Male_02.fbx</c>.</summary>
        public string model;
        /// <summary>Portrait PNG relative to <see cref="path"/>.</summary>
        public string portrait;
        /// <summary>"male" / "female".</summary>
        public string gender;
        /// <summary>Texture paths relative to <see cref="path"/> (may include non-image files: filter with RocketboxAssetSet).</summary>
        public List<string> textures = new List<string>();

        /// <summary>True for female avatars (selects the f_ motion-capture set).</summary>
        public bool IsFemale => string.Equals(gender, "female", StringComparison.OrdinalIgnoreCase);

        /// <summary>Body type matching <see cref="gender"/>.</summary>
        public BodyType BodyType => IsFemale ? BodyType.Female : BodyType.Male;

        /// <summary>Repository path of a file relative to this avatar's folder.</summary>
        public string RepositoryPath(string relative) => path + "/" + relative;
    }
}
