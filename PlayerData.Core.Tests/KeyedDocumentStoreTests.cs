using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PlayerData.Core.Tests;

public class KeyedDocumentStoreTests
{
    private static KeyedDocumentStore<string, SampleItem> CreateStore() =>
        new(item => item.ItemId);

    [Test]
    public void Upsert_NewKey_AddsValue()
    {
        var store = CreateStore();

        var result = store.Upsert(new SampleItem("sword", 1));

        Assert.That(result, Is.EqualTo(new SampleItem("sword", 1)));
        Assert.That(store.TryGet("sword", out var value), Is.True);
        Assert.That(value, Is.EqualTo(result));
    }

    [Test]
    public void Update_ExistingKey_RunsUpdater()
    {
        var store = CreateStore();
        store.Upsert(new SampleItem("sword", 1));

        var result = store.Update("sword", existing => existing with { Count = existing.Count + 1 });

        Assert.That(result.Count, Is.EqualTo(2));
    }

    [Test]
    public void Update_MissingKey_Throws()
    {
        var store = CreateStore();
        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() =>
            store.Update("missing", e => e));
    }

    [Test]
    public void Set_KeyMismatch_Throws()
    {
        var store = CreateStore();
        Assert.Throws<InvalidOperationException>(() =>
            store.Set("sword", new SampleItem("other", 1)));
    }

    [Test]
    public void GetOrAdd_CreatesOnce()
    {
        var store = CreateStore();
        var first = store.GetOrAdd("potion", id => new SampleItem(id, 1));
        var second = store.GetOrAdd("potion", _ => throw new InvalidOperationException("must not create"));
        Assert.That(first, Is.EqualTo(second));
        Assert.That(store.Count, Is.EqualTo(1));
    }

    [Test]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        var store = CreateStore();
        Assert.That(store.TryGet("missing", out _), Is.False);
    }

    [Test]
    public void Remove_ExistingKey_RemovesAndReturnsTrue()
    {
        var store = CreateStore();
        store.Upsert(new SampleItem("sword", 1));

        Assert.That(store.Remove("sword"), Is.True);
        Assert.That(store.TryGet("sword", out _), Is.False);
    }

    [Test]
    public void Remove_MissingKey_ReturnsFalse()
    {
        var store = CreateStore();
        Assert.That(store.Remove("missing"), Is.False);
    }

    [Test]
    public void Snapshot_ReflectsCurrentItems()
    {
        var store = CreateStore();
        store.Upsert(new SampleItem("a", 1));
        store.Upsert(new SampleItem("b", 2));

        Assert.That(store.Snapshot.Count, Is.EqualTo(2));
        Assert.That(store.Snapshot.Keys.OrderBy(k => k), Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void Clear_EmptiesBag()
    {
        var store = CreateStore();
        store.Upsert(new SampleItem("a", 1));
        store.Clear();
        Assert.That(store.Count, Is.EqualTo(0));
    }

    [Test]
    public void Upsert_ConcurrentDistinctKeys_NoLostUpdates()
    {
        var store = CreateStore();
        const int keyCount = 200;

        Parallel.For(0, keyCount, i =>
        {
            var key = "k" + i;
            store.Upsert(new SampleItem(key, i));
        });

        Assert.That(store.Snapshot.Count, Is.EqualTo(keyCount));
    }

    [Test]
    public void Changed_FiresUpsertedAndRemoved()
    {
        var store = CreateStore();
        var events = new System.Collections.Generic.List<PlayerDataChangeKind>();
        store.Changed += change => events.Add(change.Kind);

        store.Upsert(new SampleItem("sword", 1));
        store.Remove("sword");

        Assert.That(events, Is.EqualTo(new[] { PlayerDataChangeKind.Upserted, PlayerDataChangeKind.Removed }));
    }

    [Test]
    public void TryUpdate_WithState_AppliesUpdaterAndThreadsState()
    {
        var store = CreateStore();
        store.Upsert(new SampleItem("sword", 1));

        var found = store.TryUpdate("sword", 3, static (amount, existing) => existing with { Count = existing.Count + amount }, out var updated);

        Assert.That(found, Is.True);
        Assert.That(updated.Count, Is.EqualTo(4));
    }

    [Test]
    public void TryUpdate_WithState_MissingKey_ReturnsFalse()
    {
        var store = CreateStore();
        var found = store.TryUpdate("missing", 3, static (amount, existing) => existing with { Count = existing.Count + amount }, out _);
        Assert.That(found, Is.False);
    }

    [Test]
    public void Update_WithState_ExistingKey_RunsUpdater()
    {
        var store = CreateStore();
        store.Upsert(new SampleItem("sword", 1));

        var result = store.Update("sword", 3, static (amount, existing) => existing with { Count = existing.Count + amount });

        Assert.That(result.Count, Is.EqualTo(4));
    }

    [Test]
    public void Update_WithState_MissingKey_Throws()
    {
        var store = CreateStore();
        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() =>
            store.Update("missing", 3, static (amount, existing) => existing with { Count = existing.Count + amount }));
    }

    [Test]
    public void GetOrAdd_WithState_CreatesOnceUsingState()
    {
        var store = CreateStore();
        var first = store.GetOrAdd("potion", 5, static (id, count) => new SampleItem(id, count));
        var second = store.GetOrAdd("potion", 999, static (id, count) => new SampleItem(id, count));

        Assert.That(first, Is.EqualTo(new SampleItem("potion", 5)));
        Assert.That(second, Is.EqualTo(first));
        Assert.That(store.Count, Is.EqualTo(1));
    }

    [Test]
    public void Upsert_NewKey_ChangedEventReportsNullPrevious()
    {
        var store = CreateStore();
        BagChange<string, SampleItem>? captured = null;
        store.Changed += change => captured = change;

        store.Upsert(new SampleItem("sword", 1));

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Value.Previous, Is.Null);
        Assert.That(captured.Value.Value, Is.EqualTo(new SampleItem("sword", 1)));
    }

    [Test]
    public void Upsert_ExistingKey_ChangedEventReportsActualPreviousValue()
    {
        var store = CreateStore();
        store.Upsert(new SampleItem("sword", 1));

        BagChange<string, SampleItem>? captured = null;
        store.Changed += change => captured = change;
        store.Upsert(new SampleItem("sword", 2));

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Value.Previous, Is.EqualTo(new SampleItem("sword", 1)));
        Assert.That(captured.Value.Value, Is.EqualTo(new SampleItem("sword", 2)));
    }

    // Regression test for the [ThreadStatic] capture slot SetCore uses to hand AddOrUpdate's
    // existing value back out without a separate TryGetValue lookup (see KeyedDocumentStore.cs).
    // A plain (non-thread-local) static field would let one thread's captured value leak into
    // another thread's Changed event between the two threads' own AddOrUpdate calls. Each thread
    // writes globally-unique Count values, so any Previous the store reports can be traced back
    // to exactly the write that produced it; a crossed-thread bug would show up as a Previous
    // value nothing ever legitimately wrote.
    [Test]
    public void Upsert_ConcurrentSameKey_PreviousNeverCrossesThreads()
    {
        var store = CreateStore();
        const int seedValue = -1;
        store.Upsert(new SampleItem("sword", seedValue));

        const int threads = 8;
        const int perThread = 2000;
        var seenPrevious = new System.Collections.Concurrent.ConcurrentBag<int>();
        store.Changed += change =>
        {
            if (change.Kind == PlayerDataChangeKind.Upserted)
                seenPrevious.Add(change.Previous?.Count ?? seedValue);
        };

        Parallel.For(0, threads, threadIndex =>
        {
            for (var i = 0; i < perThread; i++)
            {
                var value = threadIndex * perThread + i;
                store.Upsert(new SampleItem("sword", value));
            }
        });

        var writtenValues = new System.Collections.Generic.HashSet<int> { seedValue };
        for (var t = 0; t < threads; t++)
            for (var i = 0; i < perThread; i++)
                writtenValues.Add(t * perThread + i);

        Assert.That(seenPrevious, Has.Count.EqualTo(threads * perThread));
        foreach (var previous in seenPrevious)
            Assert.That(writtenValues, Does.Contain(previous));
    }

    // Regression test for the ABA hazard the ConcurrentDictionary-backed rewrite must avoid:
    // ConcurrentDictionary.TryUpdate(key, newValue, comparisonValue) compares via
    // EqualityComparer<T>.Default, which is structural equality for record T. Under heavy
    // contention on one key, a stale-but-structurally-plausible read could otherwise silently
    // win a compare-and-swap and discard concurrent updates. KeyedDocumentStore.TryUpdate must
    // instead go through AddOrUpdate, whose updateValueFactory always sees the live value, so no
    // update is ever lost regardless of how many times the count structurally revisits a value
    // another thread already produced.
    [Test]
    public void TryUpdate_ConcurrentSameKey_RecordType_NoLostUpdates()
    {
        var store = CreateStore();
        store.Upsert(new SampleItem("sword", 0));

        const int threads = 8;
        const int perThread = 5000;

        Parallel.For(0, threads, threadIndex =>
        {
            for (var i = 0; i < perThread; i++)
            {
                store.TryUpdate("sword", 1, static (amount, existing) => existing with { Count = existing.Count + amount }, out _);
            }
        });

        Assert.That(store.Get("sword").Count, Is.EqualTo(threads * perThread));
    }
}
