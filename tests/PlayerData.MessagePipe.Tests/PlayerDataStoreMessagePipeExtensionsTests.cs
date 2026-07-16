using global::MessagePipe;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using PlayerData;

namespace PlayerData.MessagePipe.Tests;

public class PlayerDataStoreMessagePipeExtensionsTests
{
    [Test]
    public void PublishChangesTo_StoreUpdate_SubscriberReceivesNewValue()
    {
        var services = new ServiceCollection();
        services.AddMessagePipe();
        using var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IPublisher<SampleData>>();
        var subscriber = provider.GetRequiredService<ISubscriber<SampleData>>();

        var store = new DocumentStore<SampleData>(() => new SampleData("x", 0));
        using var wiring = store.PublishChangesTo(publisher);

        SampleData? received = null;
        using var subscription = subscriber.Subscribe(value => received = value);

        store.Update(_ => new SampleData("x", 42));

        Assert.That(received, Is.EqualTo(new SampleData("x", 42)));
    }

    [Test]
    public void PublishChangesTo_Disposed_StopsForwardingChanges()
    {
        var services = new ServiceCollection();
        services.AddMessagePipe();
        using var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IPublisher<SampleData>>();
        var subscriber = provider.GetRequiredService<ISubscriber<SampleData>>();

        var store = new DocumentStore<SampleData>(() => new SampleData("x", 0));
        var wiring = store.PublishChangesTo(publisher);

        var receivedCount = 0;
        using var subscription = subscriber.Subscribe(_ => receivedCount++);

        store.Update(_ => new SampleData("x", 1));
        wiring.Dispose();
        store.Update(_ => new SampleData("x", 2));

        Assert.That(receivedCount, Is.EqualTo(1));
    }
}
