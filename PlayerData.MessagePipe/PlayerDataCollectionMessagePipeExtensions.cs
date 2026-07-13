using System;
using global::MessagePipe;

namespace PlayerData.MessagePipe;

public static class PlayerDataCollectionMessagePipeExtensions
{
    public static IDisposable PublishChangesTo<TKey, T>(this IBag<TKey, T> collection, IPublisher<BagChange<TKey, T>> publisher)
        where TKey : notnull
        where T : class
    {
        void Handler(BagChange<TKey, T> change) => publisher.Publish(change);
        collection.Changed += Handler;
        return new ActionDisposable(() => collection.Changed -= Handler);
    }
}
