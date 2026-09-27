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
        public int HolderId;        // balls: holder PlayerId or -1
    }

    /// <summary>
    /// CONTRACT (kernel) - ring buffer of <see cref="RewindSnapshot"/>s (default 6 s at 30 Hz, allocation-free after Awake).
    /// Works on players and balls: a <see cref="Capture"/> delegate supplies the snapshot so the recorder stays generic.
    /// <para>Owner module: Player.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TimeRewindRecorder : MonoBehaviour
    {
        [SerializeField] private float historySeconds = 6f;
        [SerializeField] private float sampleRate = 30f;

        /// <summary>Supplies the current state. Must be set by the owner (player or ball) right after AddComponent.</summary>
        public Func<RewindSnapshot> Capture { get; set; }

        public float HistorySeconds => historySeconds;

        // ------------------------------------------------------------------ IMPLEMENT: Player module

        /// <summary>Interpolated snapshot from <paramref name="secondsAgo"/> seconds ago (clamped to the oldest sample).</summary>
        public bool TryGetSnapshot(float secondsAgo, out RewindSnapshot snapshot) => throw new NotImplementedException();

        /// <summary>Snapshot recorded closest to absolute <paramref name="time"/> (Time.time).</summary>
        public bool TryGetSnapshotAt(float time, out RewindSnapshot snapshot) => throw new NotImplementedException();

        /// <summary>Discards history (after teleports/round resets so rewinds cannot cross them).</summary>
        public void Clear() => throw new NotImplementedException();

        /// <summary>Pauses recording (e.g. while a rewind is being played back).</summary>
        public bool Paused { get; set; }
    }
}
