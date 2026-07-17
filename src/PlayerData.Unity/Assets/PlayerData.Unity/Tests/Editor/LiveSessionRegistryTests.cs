using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class LiveSessionRegistryTests
    {
        private string _root;
        private SaveSession _session;
        private int _changedCount;
        private Action _changedHandler;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayerDataEditorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _session = new SaveSession(new DirectorySaveBackend(_root));

            LiveSessionRegistry.ClearForTests();
            _changedCount = 0;
            _changedHandler = () => _changedCount++;
            LiveSessionRegistry.Changed += _changedHandler;
        }

        [TearDown]
        public void TearDown()
        {
            LiveSessionRegistry.Changed -= _changedHandler;
            LiveSessionRegistry.ClearForTests();
            _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void Register_AddsEntryWithNameAndSameSessionInstance()
        {
            using IDisposable token = LiveSessionRegistry.Register("main", _session);

            IReadOnlyList<LiveSessionEntry> entries = LiveSessionRegistry.Entries;
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0].Name, Is.EqualTo("main"));
            Assert.That(entries[0].Session, Is.SameAs(_session));
        }

        [Test]
        public void Register_FiresChanged()
        {
            using IDisposable token = LiveSessionRegistry.Register("main", _session);

            Assert.That(_changedCount, Is.EqualTo(1));
        }

        [Test]
        public void TokenDispose_RemovesEntryAndFiresChanged()
        {
            IDisposable token = LiveSessionRegistry.Register("main", _session);
            int countAfterRegister = _changedCount;

            token.Dispose();

            Assert.That(LiveSessionRegistry.Entries, Is.Empty);
            Assert.That(_changedCount, Is.EqualTo(countAfterRegister + 1));
        }

        [Test]
        public void TokenDispose_Twice_DoesNotThrowAndFiresChangedOnce()
        {
            IDisposable token = LiveSessionRegistry.Register("main", _session);
            int countAfterRegister = _changedCount;

            token.Dispose();
            Assert.DoesNotThrow(() => token.Dispose());

            Assert.That(_changedCount, Is.EqualTo(countAfterRegister + 1));
        }

        [Test]
        public void Register_MultipleWithDuplicateNames_PreservesOrderAndAllowsDuplicates()
        {
            using IDisposable a = LiveSessionRegistry.Register("alpha", _session);
            using IDisposable b = LiveSessionRegistry.Register("beta", _session);
            using IDisposable c = LiveSessionRegistry.Register("alpha", _session);

            IReadOnlyList<LiveSessionEntry> entries = LiveSessionRegistry.Entries;
            Assert.That(entries.Count, Is.EqualTo(3));
            Assert.That(entries[0].Name, Is.EqualTo("alpha"));
            Assert.That(entries[1].Name, Is.EqualTo("beta"));
            Assert.That(entries[2].Name, Is.EqualTo("alpha"));
        }

        [Test]
        public void Entries_IsSnapshot_NotAffectedByLaterRegistration()
        {
            using IDisposable a = LiveSessionRegistry.Register("first", _session);
            IReadOnlyList<LiveSessionEntry> snapshot = LiveSessionRegistry.Entries;

            using IDisposable b = LiveSessionRegistry.Register("second", _session);

            Assert.That(snapshot.Count, Is.EqualTo(1));
            Assert.That(LiveSessionRegistry.Entries.Count, Is.EqualTo(2));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void Register_NullEmptyOrWhitespaceName_ThrowsArgumentException(string name)
        {
            Assert.Throws<ArgumentException>(() => LiveSessionRegistry.Register(name, _session));
        }

        [Test]
        public void Register_NullSession_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => LiveSessionRegistry.Register("main", null));
        }
    }
}
