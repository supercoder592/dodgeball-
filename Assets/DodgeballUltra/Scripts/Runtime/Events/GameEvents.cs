using System;
using System.Collections.Generic;
using UnityEngine;

namespace DodgeballUltra.Events
{
    /// <summary>Marker interface for every event that travels through <see cref="GameEvents"/>.</summary>
    public interface IGameEvent
    {
    }

    /// <summary>
    /// Decoupled, strongly-typed, allocation-free publish/subscribe bus (Subject-Observer).
    /// <para>
    /// Combat code publishes facts ("a ball hit a player", "a perfect catch happened"); the UI, audio, VFX and the
    /// <c>JuiceManager</c> observe them without any compile-time dependency on the publisher.
    /// </para>
    /// <example><code>
    /// void OnEnable()  => GameEvents.Subscribe&lt;BallHitPlayerEvent&gt;(OnBallHit);
    /// void OnDisable() => GameEvents.Unsubscribe&lt;BallHitPlayerEvent&gt;(OnBallHit);
    /// GameEvents.Publish(new BallHitPlayerEvent { ... });
    /// </code></example>
    /// Rules:
    /// <list type="bullet">
    /// <item>Events are structs; handlers receive a copy (no GC).</item>
    /// <item>Subscribing/unsubscribing while an event is being published is safe (copy-on-write handler arrays).</item>
    /// <item>An exception in one handler is logged and does not stop the other handlers.</item>
    /// <item>All channels are cleared on domain reload / entering play mode (supports "Enter Play Mode Options").</item>
    /// </list>
    /// </summary>
    public static class GameEvents
    {
        private static readonly List<Action> s_clearers = new List<Action>();

        /// <summary>Registers <paramref name="handler"/> for events of type <typeparamref name="T"/>.</summary>
        public static void Subscribe<T>(Action<T> handler) where T : struct, IGameEvent => Channel<T>.Add(handler);

        /// <summary>Removes <paramref name="handler"/>. Safe to call even if it was never subscribed.</summary>
        public static void Unsubscribe<T>(Action<T> handler) where T : struct, IGameEvent => Channel<T>.Remove(handler);

        /// <summary>Synchronously delivers <paramref name="evt"/> to every subscriber of <typeparamref name="T"/>.</summary>
        public static void Publish<T>(in T evt) where T : struct, IGameEvent => Channel<T>.Raise(evt);

        /// <summary>Number of subscribers for <typeparamref name="T"/> (diagnostics / tests).</summary>
        public static int SubscriberCount<T>() where T : struct, IGameEvent => Channel<T>.Count;

        /// <summary>Removes every subscriber of every event type.</summary>
        public static void ClearAll()
        {
            lock (s_clearers)
            {
                for (int i = 0; i < s_clearers.Count; i++) s_clearers[i]();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnDomainReload() => ClearAll();

        private static void RegisterClearer(Action clearer)
        {
            lock (s_clearers) s_clearers.Add(clearer);
        }

        private static class Channel<T> where T : struct, IGameEvent
        {
            private static Action<T>[] s_handlers = Array.Empty<Action<T>>();

            static Channel() => RegisterClearer(() => s_handlers = Array.Empty<Action<T>>());

            public static int Count => s_handlers.Length;

            public static void Add(Action<T> handler)
            {
                if (handler == null) return;
                var current = s_handlers;
                if (Array.IndexOf(current, handler) >= 0) return; // no duplicate subscriptions
                var next = new Action<T>[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = handler;
                s_handlers = next;
            }

            public static void Remove(Action<T> handler)
            {
                if (handler == null) return;
                var current = s_handlers;
                int index = Array.IndexOf(current, handler);
                if (index < 0) return;
                if (current.Length == 1)
                {
                    s_handlers = Array.Empty<Action<T>>();
                    return;
                }
                var next = new Action<T>[current.Length - 1];
                if (index > 0) Array.Copy(current, 0, next, 0, index);
                if (index < current.Length - 1) Array.Copy(current, index + 1, next, index, current.Length - index - 1);
                s_handlers = next;
            }

            public static void Raise(in T evt)
            {
                var snapshot = s_handlers; // copy-on-write: safe against (un)subscription during dispatch
                for (int i = 0; i < snapshot.Length; i++)
                {
                    try
                    {
                        snapshot[i](evt);
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }
            }
        }
    }
}
