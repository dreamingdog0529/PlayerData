using System;
using global::VitalRouter;

namespace PlayerData.VitalRouter;

public static class PlayerDataCollectionVitalRouterExtensions
{
    public static IDisposable PublishChangesTo<TKey, T>(this IBag<TKey, T> collection, ICommandPublisher publisher)
        where TKey : notnull
        where T : class
    {
        void Handler(BagChange<TKey, T> change) =>
            publisher.PublishAsync(new PlayerDataCollectionChangedCommand<TKey, T>(change));
        collection.Changed += Handler;
        return new ActionDisposable(() => collection.Changed -= Handler);
    }
}
