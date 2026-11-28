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
using PlayerData.Persistence;
using PlayerData.Migrations;
using PlayerData.Sessions;

namespace PlayerData.Unity
{
    public static class PlayerDataVContainerExtensions
    {
        // ISaveBackend（UnitySaveBackend で、必要に応じて wrapBackend でラップできます）と TSession を
        // シングルトンとして登録し、開始時に LoadAsync を呼びます。TSession には
        // (ISaveBackend, IEnumerable<ISaveMigration>?) というコンストラクタが必要です。
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

    // 開始時にセッションを読み込むことで、GameSave.OpenAsync と同じ意味合いにしています。
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
