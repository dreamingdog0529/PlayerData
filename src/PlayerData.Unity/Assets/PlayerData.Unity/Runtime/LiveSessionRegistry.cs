using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
using PlayerData.Sessions;
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

    // 任意で使うためのつなぎ目です。ゲーム側のコードが、動いている ISaveSession をここに登録しておくと、エディタ側のツールが
    // 再生モード中にそれを見つけられるようになります。エディタの外では何も保存されません。Register は
    // 引数をチェックして、共有の何もしないトークンを返すだけなので、プレイヤービルド側の負担はありません。
    public static class LiveSessionRegistry
    {
#if UNITY_EDITOR
        private static readonly object Gate = new object();
        private static readonly List<Registration> Registrations = new List<Registration>();

        public static event Action? Changed;
#else
        // プレイヤービルドでは発火しません。明示的なアクセサにしておくことで、未使用イベントの警告を避けています。
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
            // ExitingPlayMode でクリアしておくと、ドメインリロードが無効な場合でも古いエントリが残りません。
            // 無効になるのは Enter Play Mode Options の設定のことで、静的な状態が再生セッションをまたいで残ってしまうケースです。
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
                    // Remove が false を返すケースで、二重の Dispose と、Clear のあとの Dispose の両方をカバーしています。
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
