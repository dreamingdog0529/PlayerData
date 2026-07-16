using global::VitalRouter;
using NUnit.Framework;
using PlayerData;

namespace PlayerData.VitalRouter.Tests;

public class PlayerDataStoreVitalRouterExtensionsTests
{
    [Test]
    public void PublishChangesTo_StoreUpdate_SubscriberReceivesCommand()
    {
        var router = new Router();
        SampleData? received = null;
        using var routerSubscription = router.Subscribe<PlayerDataChangedCommand<SampleData>>((cmd, _) => received = cmd.Value);

        var store = new DocumentStore<SampleData>(() => new SampleData("x", 0));
        using var wiring = store.PublishChangesTo(router);

        store.Update(_ => new SampleData("x", 42));

        Assert.That(received, Is.EqualTo(new SampleData("x", 42)));
    }

    [Test]
    public void PublishChangesTo_Collection_UpsertAndRemove_SubscriberReceivesCommands()
    {
        var router = new Router();
        var received = new System.Collections.Generic.List<PlayerDataChangeKind>();
        using var routerSubscription = router.Subscribe<PlayerDataCollectionChangedCommand<string, SampleData>>((cmd, _) => received.Add(cmd.Change.Kind));

        var collection = new KeyedDocumentStore<string, SampleData>(d => d.Id);
        using var wiring = collection.PublishChangesTo(router);

        collection.Upsert(new SampleData("a", 1));
        collection.Remove("a");

        Assert.That(received, Is.EqualTo(new[] { PlayerDataChangeKind.Upserted, PlayerDataChangeKind.Removed }));
    }

    [Test]
    public void PublishChangesTo_Disposed_StopsForwardingChanges()
    {
        var router = new Router();
        var receivedCount = 0;
        using var routerSubscription = router.Subscribe<PlayerDataChangedCommand<SampleData>>((_, _) => receivedCount++);

        var store = new DocumentStore<SampleData>(() => new SampleData("x", 0));
        var wiring = store.PublishChangesTo(router);

        store.Update(_ => new SampleData("x", 1));
        wiring.Dispose();
        store.Update(_ => new SampleData("x", 2));

        Assert.That(receivedCount, Is.EqualTo(1));
    }

    [Test]
    public void PublishChangesTo_DoubleDispose_IsSafe()
    {
        var router = new Router();
        var store = new DocumentStore<SampleData>(() => new SampleData("x", 0));
        var wiring = store.PublishChangesTo(router);

        wiring.Dispose();
        Assert.DoesNotThrow(() => wiring.Dispose());
    }

    [Test]
    public void PublishChangesTo_Collection_Clear_SubscriberReceivesCleared()
    {
        var router = new Router();
        var received = new System.Collections.Generic.List<PlayerDataChangeKind>();
        using var routerSubscription = router.Subscribe<PlayerDataCollectionChangedCommand<string, SampleData>>((cmd, _) => received.Add(cmd.Change.Kind));

        var collection = new KeyedDocumentStore<string, SampleData>(d => d.Id);
        using var wiring = collection.PublishChangesTo(router);

        collection.Upsert(new SampleData("a", 1));
        collection.Clear();

        Assert.That(received, Is.EqualTo(new[] { PlayerDataChangeKind.Upserted, PlayerDataChangeKind.Cleared }));
    }

    [Test]
    public void PublishChangesTo_Collection_Disposed_StopsForwardingChanges()
    {
        var router = new Router();
        var receivedCount = 0;
        using var routerSubscription = router.Subscribe<PlayerDataCollectionChangedCommand<string, SampleData>>((_, _) => receivedCount++);

        var collection = new KeyedDocumentStore<string, SampleData>(d => d.Id);
        var wiring = collection.PublishChangesTo(router);

        collection.Upsert(new SampleData("a", 1));
        wiring.Dispose();
        collection.Upsert(new SampleData("b", 2));

        Assert.That(receivedCount, Is.EqualTo(1));
    }
}
