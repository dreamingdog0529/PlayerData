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

    [Test]
    public void PublishChangesTo_DoubleDispose_IsSafe()
    {
        var services = new ServiceCollection();
        services.AddMessagePipe();
        using var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IPublisher<SampleData>>();
        var store = new DocumentStore<SampleData>(() => new SampleData("x", 0));
        var wiring = store.PublishChangesTo(publisher);

        wiring.Dispose();
        Assert.DoesNotThrow(() => wiring.Dispose());
    }

    [Test]
    public void PublishChangesTo_Collection_UpsertAndRemove_SubscriberReceivesChanges()
    {
        var services = new ServiceCollection();
        services.AddMessagePipe();
        using var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IPublisher<BagChange<string, SampleData>>>();
        var subscriber = provider.GetRequiredService<ISubscriber<BagChange<string, SampleData>>>();

        var collection = new KeyedDocumentStore<string, SampleData>(d => d.Id);
        using var wiring = collection.PublishChangesTo(publisher);

        var received = new System.Collections.Generic.List<PlayerDataChangeKind>();
        using var subscription = subscriber.Subscribe(change => received.Add(change.Kind));

        collection.Upsert(new SampleData("a", 1));
        collection.Remove("a");
        collection.Upsert(new SampleData("b", 2));
        collection.Clear();

        Assert.That(received, Is.EqualTo(new[]
        {
            PlayerDataChangeKind.Upserted,
            PlayerDataChangeKind.Removed,
            PlayerDataChangeKind.Upserted,
            PlayerDataChangeKind.Cleared,
        }));
    }

    [Test]
    public void PublishChangesTo_Collection_Disposed_StopsForwardingChanges()
    {
        var services = new ServiceCollection();
        services.AddMessagePipe();
        using var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IPublisher<BagChange<string, SampleData>>>();
        var subscriber = provider.GetRequiredService<ISubscriber<BagChange<string, SampleData>>>();

        var collection = new KeyedDocumentStore<string, SampleData>(d => d.Id);
        var wiring = collection.PublishChangesTo(publisher);

        var receivedCount = 0;
        using var subscription = subscriber.Subscribe(_ => receivedCount++);

        collection.Upsert(new SampleData("a", 1));
        wiring.Dispose();
        collection.Upsert(new SampleData("b", 2));

        Assert.That(receivedCount, Is.EqualTo(1));
    }
}
