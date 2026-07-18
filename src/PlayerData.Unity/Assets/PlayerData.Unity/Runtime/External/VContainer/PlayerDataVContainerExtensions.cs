#if PLAYERDATA_VCONTAINER_SUPPORT

using System;
using System.Collections.Generic;
using System.Threading;
#if VCONTAINER_UNITASK_INTEGRATION
using Awaitable = Cysharp.Threading.Tasks.UniTask;
#elif UNITY_2023_1_OR_NEWER
using UnityEngine;
#else
using Awaitable = System.Threading.Tasks.Task;
#endif
using VContainer;
using VContainer.Unity;

namespace PlayerData.Unity
{
    public static class PlayerDataVContainerExtensions
    {
        // Registers ISaveBackend (UnitySaveBackend, optionally wrapped by wrapBackend) and TSession
        // as singletons, then LoadAsync on start. TSession must expose a constructor
        // (ISaveBackend, IEnumerable<ISaveMigration>?).
        public static void RegisterPlayerDataSession<TSession>(
            this IContainerBuilder builder,
            string relativeFolder = "PlayerData",
            int? slot = null,
            IEnumerable<ISaveMigration>? migrations = null,
            Func<ISaveBackend, ISaveBackend>? wrapBackend = null)
            where TSession : class, ISaveSession
        {
            if (builder is null) throw new ArgumentNullException(nameof(builder));

            builder.Register<ISaveBackend>(
                _ =>
                {
                    ISaveBackend backend = UnitySaveBackend.Create(relativeFolder, slot);
                    return wrapBackend is null ? backend : wrapBackend(backend);
                },
                Lifetime.Singleton);

            builder.Register<TSession>(
                resolver =>
                {
                    var backend = resolver.Resolve<ISaveBackend>();
                    var session = (TSession)Activator.CreateInstance(
                        typeof(TSession),
                        backend,
                        migrations)!;
                    return session;
                },
                Lifetime.Singleton);

            builder.RegisterEntryPoint<PlayerDataSessionLoader<TSession>>();
        }
    }

    // Loads the session on start so semantics match GameSave.OpenAsync.
    internal sealed class PlayerDataSessionLoader<TSession> : IAsyncStartable
        where TSession : class, ISaveSession
    {
        private readonly TSession _session;

        public PlayerDataSessionLoader(TSession session) => _session = session;

        public async Awaitable StartAsync(CancellationToken cancellation = default)
        {
            await _session.LoadAsync(cancellation).ConfigureAwait(true);
        }
    }
}

#endif
