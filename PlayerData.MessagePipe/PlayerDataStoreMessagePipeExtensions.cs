using System;
using global::MessagePipe;

namespace PlayerData.MessagePipe;

public static class PlayerDataStoreMessagePipeExtensions
{
    public static IDisposable PublishChangesTo<T>(this IDoc<T> store, IPublisher<T> publisher) where T : class
    {
        void Handler(DocChange<T> change) => publisher.Publish(change.Current);
        store.Changed += Handler;
        return new ActionDisposable(() => store.Changed -= Handler);
    }

    public static IDisposable PublishChangesTo<T>(this IDoc<T> store, IPublisher<DocChange<T>> publisher) where T : class
    {
        void Handler(DocChange<T> change) => publisher.Publish(change);
        store.Changed += Handler;
        return new ActionDisposable(() => store.Changed -= Handler);
    }
}
