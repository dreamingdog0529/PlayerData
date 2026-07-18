using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PlayerData.Unity
{
    public readonly struct LiveSessionEntry
    {
        public string Name { get; }
        public ISaveSession Session { get; }

        public LiveSessionEntry(string name, ISaveSession session)
        {
            Name = name;
            Session = session;
        }
    }

    // Opt-in seam: game code registers its live ISaveSession instances here so editor tooling
    // can discover them during play mode. Outside the editor nothing is stored - Register only
    // validates arguments and returns a shared no-op token, so player builds pay nothing.
    public static class LiveSessionRegistry
    {
#if UNITY_EDITOR
        private static readonly object Gate = new object();
        private static readonly List<Registration> Registrations = new List<Registration>();

        public static event Action? Changed;
#else
        // Never raised in player builds; explicit accessors avoid the unused-event warning.
        public static event Action? Changed { add { } remove { } }
#endif

        public static IDisposable Register(string name, ISaveSession session)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name must be a non-empty, non-whitespace string.", nameof(name));
            if (session is null)
                throw new ArgumentNullException(nameof(session));

#if UNITY_EDITOR
            var registration = new Registration(name, session);
            lock (Gate)
            {
                Registrations.Add(registration);
            }
            Changed?.Invoke();
            return registration;
#else
            return NoOpToken.Instance;
#endif
        }

        public static IReadOnlyList<LiveSessionEntry> Entries
        {
            get
            {
#if UNITY_EDITOR
                lock (Gate)
                {
                    var snapshot = new LiveSessionEntry[Registrations.Count];
                    for (int i = 0; i < snapshot.Length; i++)
                        snapshot[i] = new LiveSessionEntry(Registrations[i].Name, Registrations[i].Session);
                    return snapshot;
                }
#else
                return Array.Empty<LiveSessionEntry>();
#endif
            }
        }

#if UNITY_EDITOR
        internal static void ClearForTests() => Clear();

        [InitializeOnLoadMethod]
        private static void HookPlayModeCleanup()
        {
            // Clearing on ExitingPlayMode guards against stale entries when domain reload is
            // disabled (Enter Play Mode Options), where static state survives play sessions.
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode)
                    Clear();
            };
        }

        private static void Clear()
        {
            bool removedAny;
            lock (Gate)
            {
                removedAny = Registrations.Count > 0;
                Registrations.Clear();
            }
            if (removedAny)
                Changed?.Invoke();
        }

        private sealed class Registration : IDisposable
        {
            public readonly string Name;
            public readonly ISaveSession Session;

            public Registration(string name, ISaveSession session)
            {
                Name = name;
                Session = session;
            }

            public void Dispose()
            {
                bool removed;
                lock (Gate)
                {
                    // Remove returning false covers both double-Dispose and dispose-after-Clear.
                    removed = Registrations.Remove(this);
                }
                if (removed)
                    Changed?.Invoke();
            }
        }
#else
        private sealed class NoOpToken : IDisposable
        {
            public static readonly NoOpToken Instance = new NoOpToken();

            private NoOpToken()
            {
            }

            public void Dispose()
            {
            }
        }
#endif
    }
}
