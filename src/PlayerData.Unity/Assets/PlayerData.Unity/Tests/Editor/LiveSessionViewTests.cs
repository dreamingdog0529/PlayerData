using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class LiveSessionViewTests
    {
        private string _root;
        private SampleEditorSession _session;
        private LiveSessionView _view;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayerDataEditorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _session = new SampleEditorSession(new DirectorySaveBackend(_root));
            _view = new LiveSessionView(_session);
        }

        [TearDown]
        public void TearDown()
        {
            _view.Dispose();
            _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private LiveDocumentDescriptor Descriptor(string propertyName)
        {
            foreach (LiveDocumentDescriptor descriptor in _view.Documents)
            {
                if (descriptor.PropertyName == propertyName)
                    return descriptor;
            }

            Assert.Fail($"Descriptor '{propertyName}' not found.");
            return null;
        }

        [Test]
        public void Documents_EnumeratesGeneratedDocAndBagProperties()
        {
            Assert.That(_view.Documents.Count, Is.EqualTo(3));

            LiveDocumentDescriptor profile = Descriptor("SampleProfile");
            Assert.That(profile.IsCollection, Is.False);
            Assert.That(profile.EntityType, Is.EqualTo(typeof(SampleProfile)));
            Assert.That(profile.KeyType, Is.Null);

            LiveDocumentDescriptor stats = Descriptor("Stats");
            Assert.That(stats.IsCollection, Is.False);
            Assert.That(stats.EntityType, Is.EqualTo(typeof(SampleStats)));

            LiveDocumentDescriptor items = Descriptor("Items");
            Assert.That(items.IsCollection, Is.True);
            Assert.That(items.EntityType, Is.EqualTo(typeof(SampleItem)));
            Assert.That(items.KeyType, Is.EqualTo(typeof(string)));
        }

        [Test]
        public void GetJson_SingleDoc_ReturnsCurrentValueAsJson()
        {
            _session.Stats.Replace(new SampleStats { Hp = 77 });

            string json = _view.GetJson("Stats");

            Assert.That(json, Does.Contain("\"Hp\": 77"));
        }

        [Test]
        public void ApplyJson_ValidJson_ReplacesValueAndFiresChangedWithUserWrite()
        {
            DocChange<SampleStats>? observed = null;
            _session.Stats.Changed += change => observed = change;

            bool applied = _view.ApplyJson("Stats", "{ \"Hp\": 42 }", out string? error);

            Assert.That(applied, Is.True, error);
            Assert.That(_session.Stats.Value.Hp, Is.EqualTo(42));
            Assert.That(observed, Is.Not.Null, "Changed did not fire");
            Assert.That(observed.Value.Cause, Is.EqualTo(DataChangeCause.UserWrite));
            Assert.That(observed.Value.Current.Hp, Is.EqualTo(42));
        }

        [Test]
        public void ApplyJson_MalformedJson_ErrorsWithoutMutation()
        {
            _session.Stats.Replace(new SampleStats { Hp = 7 });

            bool applied = _view.ApplyJson("Stats", "{ not valid json", out string? error);

            Assert.That(applied, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(_session.Stats.Value.Hp, Is.EqualTo(7));
        }

        [Test]
        public void ApplyJson_UnknownPropertyName_ErrorsWithoutMutation()
        {
            _session.Stats.Replace(new SampleStats { Hp = 7 });

            bool applied = _view.ApplyJson("Stats", "{ \"Hpp\": 5 }", out string? error);

            Assert.That(applied, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(_session.Stats.Value.Hp, Is.EqualTo(7));
        }

        [Test]
        public void AddEntryJson_NewEntity_UpsertsAndFiresChangedWithUserWrite()
        {
            BagChange<string, SampleItem>? observed = null;
            _session.Items.Changed += change => observed = change;

            bool added = _view.AddEntryJson("Items", "{ \"ItemId\": \"potion\", \"Count\": 3 }", out string? error);

            Assert.That(added, Is.True, error);
            Assert.That(_session.Items.Snapshot["potion"].Count, Is.EqualTo(3));
            Assert.That(observed, Is.Not.Null, "Changed did not fire");
            Assert.That(observed.Value.Kind, Is.EqualTo(PlayerDataChangeKind.Upserted));
            Assert.That(observed.Value.Cause, Is.EqualTo(DataChangeCause.UserWrite));
        }

        [Test]
        public void ApplyEntryJson_SameKey_SetsEntityAndFiresChanged()
        {
            _session.Items.Upsert(new SampleItem { ItemId = "potion", Count = 3 });
            BagChange<string, SampleItem>? observed = null;
            _session.Items.Changed += change => observed = change;

            bool applied = _view.ApplyEntryJson("Items", "potion", "{ \"ItemId\": \"potion\", \"Count\": 5 }", out string? error);

            Assert.That(applied, Is.True, error);
            Assert.That(_session.Items.Snapshot["potion"].Count, Is.EqualTo(5));
            Assert.That(observed, Is.Not.Null, "Changed did not fire");
            Assert.That(observed.Value.Key, Is.EqualTo("potion"));
        }

        [Test]
        public void ApplyEntryJson_KeyChangingPayload_ErrorsWithoutPartialMutation()
        {
            _session.Items.Upsert(new SampleItem { ItemId = "potion", Count = 3 });

            bool applied = _view.ApplyEntryJson("Items", "potion", "{ \"ItemId\": \"elixir\", \"Count\": 9 }", out string? error);

            Assert.That(applied, Is.False);
            Assert.That(error, Is.Not.Null);
            Assert.That(error, Does.Contain("elixir"));
            Assert.That(_session.Items.Snapshot["potion"].Count, Is.EqualTo(3));
            Assert.That(_session.Items.Contains("elixir"), Is.False);
        }

        [Test]
        public void RemoveEntry_ExistingKey_RemovesAndFiresChanged()
        {
            _session.Items.Upsert(new SampleItem { ItemId = "potion", Count = 3 });
            BagChange<string, SampleItem>? observed = null;
            _session.Items.Changed += change => observed = change;

            bool removed = _view.RemoveEntry("Items", "potion");

            Assert.That(removed, Is.True);
            Assert.That(_session.Items.Count, Is.EqualTo(0));
            Assert.That(observed, Is.Not.Null, "Changed did not fire");
            Assert.That(observed.Value.Kind, Is.EqualTo(PlayerDataChangeKind.Removed));
            Assert.That(_view.RemoveEntry("Items", "potion"), Is.False);
        }

        [Test]
        public void GetEntryKeysAndGetEntryJson_ReflectSnapshot()
        {
            _session.Items.Upsert(new SampleItem { ItemId = "potion", Count = 3 });
            _session.Items.Upsert(new SampleItem { ItemId = "elixir", Count = 1 });

            IReadOnlyList<object> keys = _view.GetEntryKeys("Items");
            string json = _view.GetEntryJson("Items", "potion");

            Assert.That(keys.Count, Is.EqualTo(2));
            Assert.That(keys, Has.Member("potion"));
            Assert.That(keys, Has.Member("elixir"));
            Assert.That(json, Does.Contain("\"Count\": 3"));
        }

        [Test]
        public void CanEdit_RoundTrippableDoc_True()
        {
            bool canEdit = _view.CanEdit("Stats", out string? reason);

            Assert.That(canEdit, Is.True, reason);
            Assert.That(reason, Is.Null);
        }

        [Test]
        public void CanEdit_NonSerializableDoc_ViewOnlyWithReasonAndApplyRefused()
        {
            GateSession gate = new GateSession(new SaveSession(new DirectorySaveBackend(_root)));
            using (LiveSessionView view = new LiveSessionView(gate))
            {
                bool canEdit = view.CanEdit("Plain", out string? reason);

                Assert.That(canEdit, Is.False);
                Assert.That(reason, Is.Not.Null.And.Not.Empty);
                // Viewing still works even when editing is gated off.
                Assert.That(view.GetJson("Plain"), Does.Contain("Value"));

                SamplePlainDoc before = gate.Plain.Value;
                bool applied = view.ApplyJson("Plain", "{ \"Value\": 1 }", out string? error);

                Assert.That(applied, Is.False);
                Assert.That(error, Is.Not.Null.And.Not.Empty);
                Assert.That(gate.Plain.Value, Is.SameAs(before));
            }
        }

        [Test]
        public void ConsumeChangeFlag_MutationViaSessionApi_TrueThenFalse()
        {
            _view.ConsumeChangeFlag();

            _session.Stats.Replace(new SampleStats { Hp = 1 });

            Assert.That(_view.ConsumeChangeFlag(), Is.True);
            Assert.That(_view.ConsumeChangeFlag(), Is.False);
        }

        [Test]
        public void ConsumeChangeFlag_BagMutation_True()
        {
            _view.ConsumeChangeFlag();

            _session.Items.Upsert(new SampleItem { ItemId = "potion", Count = 1 });

            Assert.That(_view.ConsumeChangeFlag(), Is.True);
        }

        [Test]
        public void Dispose_MutationsNoLongerSetFlag()
        {
            _view.ConsumeChangeFlag();
            _view.Dispose();

            _session.Stats.Replace(new SampleStats { Hp = 2 });
            _session.Items.Upsert(new SampleItem { ItemId = "potion", Count = 1 });

            Assert.That(_view.ConsumeChangeFlag(), Is.False);
        }

        // Hand-written ISaveSession with a doc whose type MemoryPack cannot serialize: exercises
        // the live editability gate, which generated sessions (always MemoryPackable) cannot.
        private sealed class GateSession : ISaveSession
        {
            private readonly SaveSession _inner;

            public GateSession(SaveSession inner)
            {
                _inner = inner;
                Plain = inner.AddDocument("plain", static () => new SamplePlainDoc());
            }

            public IDoc<SamplePlainDoc> Plain { get; }

            public bool IsDirty => _inner.IsDirty;

            public bool IsLoaded => _inner.IsLoaded;

            public event Action Loaded
            {
                add => _inner.Loaded += value;
                remove => _inner.Loaded -= value;
            }

            public event Action Committed
            {
                add => _inner.Committed += value;
                remove => _inner.Committed -= value;
            }

            public event Action<bool> DirtyChanged
            {
                add => _inner.DirtyChanged += value;
                remove => _inner.DirtyChanged -= value;
            }

            public ValueTask<LoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
                _inner.LoadAsync(cancellationToken);

            public ValueTask CommitAsync(CancellationToken cancellationToken = default) =>
                _inner.CommitAsync(cancellationToken);

            public SuppressionScope SuppressNotifications() => _inner.SuppressNotifications();

            public void AddValidator(ISaveValidator validator) => _inner.AddValidator(validator);

            public void AddValidator(Action<ISaveSession> validate) => _inner.AddValidator(validate);

            public ValueTask DisposeAsync() => _inner.DisposeAsync();
        }
    }
}
