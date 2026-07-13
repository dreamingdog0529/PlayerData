using System;
using global::VitalRouter;

namespace PlayerData.VitalRouter;

public static class PlayerDataStoreVitalRouterExtensions
{
    public static IDisposable PublishChangesTo<T>(this IDoc<T> store, ICommandPublisher publisher) where T : class
    {
        void Handler(DocChange<T> change) =>
            publisher.PublishAsync(new PlayerDataChangedCommand<T>(change.Current, change.Previous));
        store.Changed += Handler;
        return new ActionDisposable(() => store.Changed -= Handler);
    }
}
