using System;
using System.Collections.Generic;
using UnityEngine;

namespace DodgeballUltra.Audio
{
    /// <summary>
    /// Optional set of recorded clips for the <see cref="SfxId"/>s (drop real recordings here). Every id without an entry
    /// (or with no clips) keeps the procedural placeholder synthesised by <see cref="ProceduralSfx"/>, so a library can be
    /// filled one sound at a time. Assign it to <see cref="AudioManager.library"/> or place it at
    /// <c>Resources/DodgeballUltra/AudioLibrary</c>.
    /// </summary>
    [CreateAssetMenu(fileName = "AudioLibrary", menuName = "Dodgeball Ultra/Audio Library", order = 30)]
    public sealed class AudioLibrary : ScriptableObject
    {
        /// <summary>Recorded clips for one sound.</summary>
        [Serializable]
        public sealed class Entry
        {
            public SfxId id;

            [Tooltip("Variants: one is picked at random per play (or by index when a variant is requested).")]
            public AudioClip[] clips = Array.Empty<AudioClip>();

            [Tooltip("Optional seamless loop used for sustained playback (hums, crowd ambience).")]
            public AudioClip loop;

            [Tooltip("Gain applied on top of the built-in mix level of this id.")]
            [Range(0f, 2f)] public float volume = 1f;

            [Tooltip("Pitch multiplier applied on top of the requested pitch.")]
            [Range(0.25f, 3f)] public float pitch = 1f;

            public bool HasClips
            {
                get
                {
                    if (clips == null) return false;
                    for (int i = 0; i < clips.Length; i++)
                        if (clips[i] != null) return true;
                    return false;
                }
            }
        }

        [Tooltip("One entry per sound id. Ids without an entry use the procedural placeholder.")]
        public List<Entry> entries = new List<Entry>();

        [NonSerialized] private Entry[] _lookup;

        /// <summary>The entry for <paramref name="id"/> that has at least one clip (or a loop).</summary>
        public bool TryGet(SfxId id, out Entry entry)
        {
            EnsureLookup();
            int i = (int)id;
            entry = i >= 0 && i < _lookup.Length ? _lookup[i] : null;
            return entry != null;
        }

        /// <summary>
        /// Picks a clip for <paramref name="id"/>: <paramref name="variant"/> &gt;= 0 selects by index (wrapped), otherwise a
        /// random one. Returns null when the library has nothing for this id.
        /// </summary>
        public AudioClip PickClip(SfxId id, int variant, out float volume, out float pitch)
        {
            volume = 1f;
            pitch = 1f;
            if (!TryGet(id, out Entry entry) || entry.clips == null || entry.clips.Length == 0) return null;
            volume = entry.volume;
            pitch = entry.pitch;

            int count = entry.clips.Length;
            int start = variant >= 0 ? variant % count : UnityEngine.Random.Range(0, count);
            for (int k = 0; k < count; k++)
            {
                AudioClip clip = entry.clips[(start + k) % count];
                if (clip != null) return clip;
            }
            return null;
        }

        /// <summary>The recorded loop of <paramref name="id"/> (null when none).</summary>
        public AudioClip GetLoop(SfxId id, out float volume, out float pitch)
        {
            volume = 1f;
            pitch = 1f;
            if (!TryGet(id, out Entry entry) || entry.loop == null) return null;
            volume = entry.volume;
            pitch = entry.pitch;
            return entry.loop;
        }

        /// <summary>Call after editing <see cref="entries"/> at runtime.</summary>
        public void InvalidateLookup() => _lookup = null;

        private void OnValidate() => _lookup = null;

        private void OnEnable() => _lookup = null;

        private void EnsureLookup()
        {
            if (_lookup != null) return;
            _lookup = new Entry[Enum.GetValues(typeof(SfxId)).Length];
            if (entries == null) return;
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e == null || (!e.HasClips && e.loop == null)) continue;
                int index = (int)e.id;
                if (index >= 0 && index < _lookup.Length && _lookup[index] == null) _lookup[index] = e;
            }
        }
    }
}
