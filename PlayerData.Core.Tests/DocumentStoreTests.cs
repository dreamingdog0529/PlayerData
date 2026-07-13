using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PlayerData.Core.Tests;

public class DocumentStoreTests
{
    [Test]
    public void Update_ReturnsAndAppliesUpdatedValue()
    {
        var store = new DocumentStore<SamplePlayerData>(() => new SamplePlayerData(1, "New"));

        var result = store.Update(current => current with { Level = current.Level + 1 });

        Assert.That(result.Level, Is.EqualTo(2));
        Assert.That(store.Value.Level, Is.EqualTo(2));
    }

    [Test]
    public void Replace_SetsValue()
    {
        var store = new DocumentStore<SamplePlayerData>(() => new SamplePlayerData(1, "New"));
        store.Replace(new SamplePlayerData(9, "X"));
        Assert.That(store.Value, Is.EqualTo(new SamplePlayerData(9, "X")));
    }

    [Test]
    public void Update_ConcurrentIncrements_NoLostUpdates()
    {
        var store = new DocumentStore<SamplePlayerData>(() => new SamplePlayerData(1, "New"));
        const int threadCount = 8;
        const int incrementsPerThread = 500;

        Parallel.For(0, threadCount, _ =>
        {
            for (var i = 0; i < incrementsPerThread; i++)
                store.Update(current => current with { Level = current.Level + 1 });
        });

        Assert.That(store.Value.Level, Is.EqualTo(1 + threadCount * incrementsPerThread));
    }

    [Test]
    public void Changed_FiresWithPreviousAndCurrent()
    {
        var store = new DocumentStore<SamplePlayerData>(() => new SamplePlayerData(1, "New"));
        DocChange<SamplePlayerData>? observed = null;
        store.Changed += value => observed = value;

        store.Update(current => current with { Level = 99 });

        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.Value.Previous!.Level, Is.EqualTo(1));
        Assert.That(observed.Value.Current.Level, Is.EqualTo(99));
        Assert.That(observed.Value.Cause, Is.EqualTo(DataChangeCause.UserWrite));
    }

    [Test]
    public void Update_NullUpdater_Throws()
    {
        var store = new DocumentStore<SamplePlayerData>(() => new SamplePlayerData(1, "New"));
        Assert.Throws<ArgumentNullException>(() => store.Update(null!));
    }

    [Test]
    public void Update_WithState_AppliesUpdaterAndThreadsState()
    {
        var store = new DocumentStore<SamplePlayerData>(() => new SamplePlayerData(1, "New"));

        var result = store.Update(5, static (amount, current) => current with { Level = current.Level + amount });

        Assert.That(result.Level, Is.EqualTo(6));
        Assert.That(store.Value.Level, Is.EqualTo(6));
    }

    [Test]
    public void Update_WithState_NullUpdater_Throws()
    {
        var store = new DocumentStore<SamplePlayerData>(() => new SamplePlayerData(1, "New"));
        Assert.Throws<ArgumentNullException>(() => store.Update<int>(5, null!));
    }
}
