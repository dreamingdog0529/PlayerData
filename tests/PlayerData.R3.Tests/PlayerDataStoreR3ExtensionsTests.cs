using NUnit.Framework;
using PlayerData;
using R3;

namespace PlayerData.R3.Tests;

public class PlayerDataStoreR3ExtensionsTests
{
    [Test]
    public void AsObservable_DefaultReplay_EmitsCurrentThenUpdates()
    {
        var store = new DocumentStore<SampleData>(() => new SampleData("x", 0));
        var received = new System.Collections.Generic.List<SampleData>();
        using var subscription = store.AsObservable().Subscribe(value => received.Add(value));

        store.Update(_ => new SampleData("x", 42));

        Assert.That(received, Is.EqualTo(new[] { new SampleData("x", 0), new SampleData("x", 42) }));
    }

    [Test]
    public void AsObservable_ReplayFalse_EmitsOnlyUpdates()
    {
        var store = new DocumentStore<SampleData>(() => new SampleData("x", 0));
        SampleData? received = null;
        using var subscription = store.AsObservable(replayCurrent: false).Subscribe(value => received = value);

        store.Update(_ => new SampleData("x", 42));

        Assert.That(received, Is.EqualTo(new SampleData("x", 42)));
    }

    [Test]
    public void AsObservable_Collection_UpsertAndRemove_EmitsChanges()
    {
        var collection = new KeyedDocumentStore<string, SampleData>(d => d.Id);
        var received = new System.Collections.Generic.List<BagChange<string, SampleData>>();
        using var subscription = collection.AsObservable().Subscribe(change => received.Add(change));

        collection.Upsert(new SampleData("a", 1));
        collection.Remove("a");

        Assert.That(received, Has.Count.EqualTo(2));
        Assert.That(received[0].Kind, Is.EqualTo(PlayerDataChangeKind.Upserted));
        Assert.That(received[1].Kind, Is.EqualTo(PlayerDataChangeKind.Removed));
    }

    [Test]
    public void AsObservable_SubscriptionDisposed_StopsReceivingAndDetachesFromChanged()
    {
        var store = new DocumentStore<SampleData>(() => new SampleData("x", 0));
        var observable = store.AsObservable(replayCurrent: false);

        var firstReceivedCount = 0;
        var subscription = observable.Subscribe(_ => firstReceivedCount++);
        store.Update(_ => new SampleData("x", 1));
        subscription.Dispose();
        store.Update(_ => new SampleData("x", 2));

        Assert.That(firstReceivedCount, Is.EqualTo(1));

        SampleData? secondReceived = null;
        using var secondSubscription = observable.Subscribe(value => secondReceived = value);
        store.Update(_ => new SampleData("x", 3));

        Assert.That(secondReceived, Is.EqualTo(new SampleData("x", 3)));
    }

    [Test]
    public void AsObservable_Collection_Clear_EmitsCleared()
    {
        var collection = new KeyedDocumentStore<string, SampleData>(d => d.Id);
        collection.Upsert(new SampleData("a", 1));

        var received = new System.Collections.Generic.List<PlayerDataChangeKind>();
        using var subscription = collection.AsObservable().Subscribe(change => received.Add(change.Kind));

        collection.Clear();

        Assert.That(received, Is.EqualTo(new[] { PlayerDataChangeKind.Cleared }));
    }

    [Test]
    public void AsObservable_Collection_SubscriptionDisposed_StopsReceiving()
    {
        var collection = new KeyedDocumentStore<string, SampleData>(d => d.Id);
        var receivedCount = 0;
        var subscription = collection.AsObservable().Subscribe(_ => receivedCount++);

        collection.Upsert(new SampleData("a", 1));
        subscription.Dispose();
        collection.Upsert(new SampleData("b", 2));

        Assert.That(receivedCount, Is.EqualTo(1));
    }

    [Test]
    public void AsObservable_MultipleSubscribers_AllReceiveUpdates()
    {
        var store = new DocumentStore<SampleData>(() => new SampleData("x", 0));
        var a = new System.Collections.Generic.List<int>();
        var b = new System.Collections.Generic.List<int>();

        using var subA = store.AsObservable(replayCurrent: false).Subscribe(v => a.Add(v.Value));
        using var subB = store.AsObservable(replayCurrent: false).Subscribe(v => b.Add(v.Value));

        store.Update(_ => new SampleData("x", 1));
        store.Update(_ => new SampleData("x", 2));

        Assert.That(a, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(b, Is.EqualTo(new[] { 1, 2 }));
    }
}
