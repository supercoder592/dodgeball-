using System;
using UnityEngine;

namespace DodgeballUltra.Player
{
    /// <summary>One recorded moment used by time-rewind abilities (Specter's Time Reversal, Chrono's Temporal Reset).</summary>
    public struct RewindSnapshot
    {
        public float Time;          // Time.time when recorded
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public float Hp;            // players only
        public int State;           // BallState (balls) or PlayerStateId (players) as int
        public int HolderId;        // balls: holder PlayerId or -1 (players: BallId of the held ball or -1)
    }

    /// <summary>
    /// CONTRACT (kernel) - ring buffer of <see cref="RewindSnapshot"/>s (default 6 s at 30 Hz, allocation-free after Awake).
    /// Works on players and balls: a <see cref="Capture"/> delegate supplies the snapshot so the recorder stays generic.
    /// <para>Owner module: Player.</para>
    /// <para>
    /// Sampling runs in this component's own LateUpdate on scaled <c>Time.time</c> (so hitstop and slow motion are
    /// recorded faithfully). Lookups binary-search the ring buffer and interpolate continuous fields (position, rotation,
    /// velocity) between the two bracketing samples; discrete fields (state, holder) come from the nearer sample and HP from
    /// the older one (HP changes in steps, it is never blended across a hit).
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TimeRewindRecorder : MonoBehaviour
    {
        [Tooltip("Seconds of history kept (Chrono rewinds 3 s, Specter up to 3 s; 6 s leaves headroom).")]
        [Min(0.5f)] [SerializeField] private float historySeconds = 6f;

        [Tooltip("Samples per second (scaled time). 30 Hz with interpolation is visually exact for rewinds.")]
        [Range(5f, 120f)] [SerializeField] private float sampleRate = 30f;

        /// <summary>Supplies the current state. Must be set by the owner (player or ball) right after AddComponent.</summary>
        public Func<RewindSnapshot> Capture { get; set; }

        public float HistorySeconds => historySeconds;

        // ------------------------------------------------------------------ additional state

        /// <summary>Samples per second.</summary>
        public float SampleRate => sampleRate;

        /// <summary>Number of samples currently stored.</summary>
        public int Count => _count;

        /// <summary>Time.time of the oldest sample (NaN when empty).</summary>
        public float OldestTime => _count > 0 ? _buffer[Physical(0)].Time : float.NaN;

        /// <summary>Time.time of the newest sample (NaN when empty).</summary>
        public float NewestTime => _count > 0 ? _buffer[Physical(_count - 1)].Time : float.NaN;

        /// <summary>Seconds of history actually available right now.</summary>
        public float AvailableSeconds => _count > 0 ? Mathf.Max(0f, UnityEngine.Time.time - _buffer[Physical(0)].Time) : 0f;

        // ------------------------------------------------------------------ internals

        private RewindSnapshot[] _buffer;
        private int _start;     // physical index of the oldest sample
        private int _count;
        private float _nextSampleTime = float.NegativeInfinity;

        private void Awake() => Allocate();

        /// <summary>Changes the history length / sample rate (reallocates and clears; do this at setup time only).</summary>
        public void Configure(float seconds, float samplesPerSecond)
        {
            historySeconds = Mathf.Max(0.5f, seconds);
            sampleRate = Mathf.Clamp(samplesPerSecond, 5f, 120f);
            Allocate();
        }

        private void Allocate()
        {
            historySeconds = Mathf.Max(0.5f, historySeconds);
            sampleRate = Mathf.Clamp(sampleRate, 5f, 120f);
            int capacity = Mathf.CeilToInt(historySeconds * sampleRate) + 2;
            if (_buffer == null || _buffer.Length != capacity) _buffer = new RewindSnapshot[capacity];
            Clear();
        }

        private void LateUpdate()
        {
            if (Paused || Capture == null) return;
            float now = UnityEngine.Time.time;
            if (now < _nextSampleTime) return;

            Record(now);
            // Stay on the sample grid without drifting; when behind (first sample, long pause, hitch) resume from now.
            float interval = 1f / sampleRate;
            _nextSampleTime += interval;
            if (_nextSampleTime <= now) _nextSampleTime = now + interval;
        }

        /// <summary>Takes a sample immediately (e.g. the exact cast moment of Specter's Time Reversal). Ignores <see cref="Paused"/>.</summary>
        public void RecordNow()
        {
            if (Capture == null) return;
            Record(UnityEngine.Time.time);
        }

        private void Record(float now)
        {
            if (_buffer == null) Allocate();

            RewindSnapshot snap;
            try
            {
                snap = Capture();
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
                return;
            }
            snap.Time = now;

            // Same timestamp as the newest sample (RecordNow on a sampling frame): overwrite it.
            if (_count > 0 && _buffer[Physical(_count - 1)].Time >= now)
            {
                _buffer[Physical(_count - 1)] = snap;
                return;
            }

            if (_count < _buffer.Length)
            {
                _buffer[Physical(_count)] = snap;
                _count++;
            }
            else
            {
                _buffer[_start] = snap; // overwrite the oldest
                _start = (_start + 1) % _buffer.Length;
            }
        }

        // ------------------------------------------------------------------ contract

        /// <summary>Interpolated snapshot from <paramref name="secondsAgo"/> seconds ago (clamped to the oldest sample).</summary>
        public bool TryGetSnapshot(float secondsAgo, out RewindSnapshot snapshot) =>
            TryGetSnapshotAt(UnityEngine.Time.time - Mathf.Max(0f, secondsAgo), out snapshot);

        /// <summary>Snapshot recorded closest to absolute <paramref name="time"/> (Time.time).</summary>
        /// <remarks>
        /// Continuous fields are interpolated between the two samples that bracket <paramref name="time"/>; discrete fields
        /// come from the nearer sample (HP from the older one). Times before the oldest / after the newest sample clamp to that sample.
        /// </remarks>
        public bool TryGetSnapshotAt(float time, out RewindSnapshot snapshot)
        {
            snapshot = default;
            if (_count == 0 || _buffer == null || float.IsNaN(time)) return false;

            ref RewindSnapshot oldest = ref _buffer[Physical(0)];
            if (time <= oldest.Time)
            {
                snapshot = oldest;
                return true;
            }

            ref RewindSnapshot newest = ref _buffer[Physical(_count - 1)];
            if (time >= newest.Time)
            {
                snapshot = newest;
                return true;
            }

            // Binary search for the last sample with Time <= time.
            int lo = 0, hi = _count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (_buffer[Physical(mid)].Time <= time) lo = mid;
                else hi = mid;
            }

            ref RewindSnapshot a = ref _buffer[Physical(lo)];
            ref RewindSnapshot b = ref _buffer[Physical(hi)];
            float span = b.Time - a.Time;
            float t = span > 1e-6f ? Mathf.Clamp01((time - a.Time) / span) : 0f;

            snapshot.Time = time;
            snapshot.Position = Vector3.LerpUnclamped(a.Position, b.Position, t);
            snapshot.Rotation = Quaternion.Slerp(Normalize(a.Rotation), Normalize(b.Rotation), t);
            snapshot.Velocity = Vector3.LerpUnclamped(a.Velocity, b.Velocity, t);
            // HP is a discrete game value: never interpolate across a damage step - the older sample was still valid then.
            snapshot.Hp = a.Hp;
            bool nearA = t < 0.5f;
            snapshot.State = nearA ? a.State : b.State;
            snapshot.HolderId = nearA ? a.HolderId : b.HolderId;
            return true;
        }

        /// <summary>Discards history (after teleports/round resets so rewinds cannot cross them).</summary>
        public void Clear()
        {
            _start = 0;
            _count = 0;
            _nextSampleTime = float.NegativeInfinity;
        }

        /// <summary>Pauses recording (e.g. while a rewind is being played back).</summary>
        public bool Paused { get; set; }

        // ------------------------------------------------------------------ helpers

        private int Physical(int logicalIndex)
        {
            int i = _start + logicalIndex;
            int len = _buffer.Length;
            return i >= len ? i - len : i;
        }

        private static Quaternion Normalize(Quaternion q)
        {
            float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (n < 1e-6f || float.IsNaN(n)) return Quaternion.identity;
            return new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n);
        }
    }
}
