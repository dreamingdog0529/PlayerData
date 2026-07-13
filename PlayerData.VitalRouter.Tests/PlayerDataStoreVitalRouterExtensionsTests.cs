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
}
