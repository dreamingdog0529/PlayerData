using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class PlayerDataViewerLiveModeTests
    {
        private string _root;
        private SampleEditorSession _session;
        private VisualElement _rootElement;
        private ViewerPanel _panel;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayerDataEditorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            LiveSessionRegistry.ClearForTests();
            _session = new SampleEditorSession(new DirectorySaveBackend(_root));
            _rootElement = new VisualElement();
            _panel = ViewerUI.BuildInto(_rootElement, new PlayerDataViewerController(), _root);
        }

        [TearDown]
        public void TearDown()
        {
            _panel.Dispose();
            LiveSessionRegistry.ClearForTests();
            _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private DropdownField Source => _rootElement.Q<DropdownField>(ViewerUI.SourceDropdownName);

        private TextField Json => _rootElement.Q<TextField>(ViewerUI.DocumentJsonName);

        private Label StaleHint => _rootElement.Q<Label>(ViewerUI.StaleHintName);

        private VisualElement DiskSection => _rootElement.Q<VisualElement>(ViewerUI.DiskSectionName);

        private VisualElement LiveSection => _rootElement.Q<VisualElement>(ViewerUI.LiveSectionName);

        private int LiveDocumentIndex(string propertyName)
        {
            IList items = _rootElement.Q<ListView>(ViewerUI.LiveDocumentsListName).itemsSource;
            for (int i = 0; i < items.Count; i++)
            {
                if (((LiveDocumentDescriptor)items[i]).PropertyName == propertyName)
                    return i;
            }

            Assert.Fail($"Live document '{propertyName}' not found.");
            return -1;
        }

        [Test]
        public void BuildInto_CreatesLiveModeElements()
        {
            Assert.That(Source, Is.Not.Null);
            Assert.That(Source.choices.Count, Is.EqualTo(1));
            Assert.That(Source.choices[0], Is.EqualTo(ViewerUI.DiskSourceLabel));
            Assert.That(Source.value, Is.EqualTo(ViewerUI.DiskSourceLabel));

            Assert.That(DiskSection, Is.Not.Null);
            Assert.That(LiveSection, Is.Not.Null);
            Assert.That(LiveSection.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(_rootElement.Q<ListView>(ViewerUI.LiveDocumentsListName), Is.Not.Null);
            Assert.That(_rootElement.Q<ListView>(ViewerUI.LiveEntriesListName), Is.Not.Null);
            Assert.That(_rootElement.Q<TextField>(ViewerUI.AddEntryJsonName), Is.Not.Null);
            Assert.That(_rootElement.Q<Button>(ViewerUI.AddEntryButtonName), Is.Not.Null);
            Assert.That(_rootElement.Q<Button>(ViewerUI.RemoveEntryButtonName), Is.Not.Null);
            Assert.That(StaleHint, Is.Not.Null);
            Assert.That(StaleHint.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(_rootElement.Q<Label>(ViewerUI.LiveIndicatorName), Is.Not.Null);
        }

        [Test]
        public void Register_AddsSourceChoice_AndTokenDisposeRemovesIt()
        {
            IDisposable token = LiveSessionRegistry.Register("game", _session);

            Assert.That(Source.choices.Count, Is.EqualTo(2));
            Assert.That(Source.choices, Has.Member("game"));

            token.Dispose();

            Assert.That(Source.choices.Count, Is.EqualTo(1));
            Assert.That(Source.value, Is.EqualTo(ViewerUI.DiskSourceLabel));
        }

        [Test]
        public void Register_DuplicateNames_GetIndexSuffixes()
        {
            using IDisposable a = LiveSessionRegistry.Register("game", _session);
            using IDisposable b = LiveSessionRegistry.Register("game", _session);

            Assert.That(Source.choices.Count, Is.EqualTo(3));
            Assert.That(Source.choices, Has.Member("game (1)"));
            Assert.That(Source.choices, Has.Member("game (2)"));
        }

        [Test]
        public void SelectLiveSource_ShowsSessionDocumentsAndHidesDiskSection()
        {
            using IDisposable token = LiveSessionRegistry.Register("game", _session);

            _panel.SelectSourceForTests(1);

            Assert.That(Source.value, Is.EqualTo("game"));
            Assert.That(DiskSection.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(LiveSection.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(_rootElement.Q<Label>(ViewerUI.LiveIndicatorName).style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(_rootElement.Q<HelpBox>(ViewerUI.PlayModeWarningName).style.display.value, Is.EqualTo(DisplayStyle.None));

            IList documents = _rootElement.Q<ListView>(ViewerUI.LiveDocumentsListName).itemsSource;
            Assert.That(documents.Count, Is.EqualTo(3));
            LiveDocumentIndex("SampleProfile");
            LiveDocumentIndex("Stats");
            LiveDocumentIndex("Items");
        }

        [Test]
        public void SelectedLiveSourceDisappears_FallsBackToDisk()
        {
            IDisposable token = LiveSessionRegistry.Register("game", _session);
            _panel.SelectSourceForTests(1);

            token.Dispose();

            Assert.That(Source.value, Is.EqualTo(ViewerUI.DiskSourceLabel));
            Assert.That(Source.choices.Count, Is.EqualTo(1));
            Assert.That(DiskSection.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(LiveSection.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(Json.isReadOnly, Is.True);

            // The live view was disposed with the fallback: mutations and ticks are inert.
            Assert.DoesNotThrow(() => _session.Stats.Replace(new SampleStats { Hp = 5 }));
            Assert.DoesNotThrow(() => _panel.Tick(100.0));
        }

        [Test]
        public void SelectLiveDocument_ShowsJson_AndTickRefreshesAfterMutation()
        {
            using IDisposable token = LiveSessionRegistry.Register("game", _session);
            _panel.SelectSourceForTests(1);

            _panel.SelectLiveDocumentForTests(LiveDocumentIndex("Stats"));

            Assert.That(Json.value, Does.Contain("\"Hp\": 0"));
            Assert.That(Json.isReadOnly, Is.False);

            _session.Stats.Replace(new SampleStats { Hp = 77 });
            _panel.Tick(100.0);

            Assert.That(Json.value, Does.Contain("\"Hp\": 77"));
            Assert.That(StaleHint.style.display.value, Is.EqualTo(DisplayStyle.None));
        }

        [Test]
        public void Tick_WithinThrottleWindow_DefersRefreshUntilIntervalElapses()
        {
            using IDisposable token = LiveSessionRegistry.Register("game", _session);
            _panel.SelectSourceForTests(1);
            _panel.SelectLiveDocumentForTests(LiveDocumentIndex("Stats"));

            _session.Stats.Replace(new SampleStats { Hp = 1 });
            _panel.Tick(100.0);
            Assert.That(Json.value, Does.Contain("\"Hp\": 1"));

            _session.Stats.Replace(new SampleStats { Hp = 2 });
            _panel.Tick(100.2);
            Assert.That(Json.value, Does.Contain("\"Hp\": 1"), "refresh must be throttled");

            _panel.Tick(100.5);
            Assert.That(Json.value, Does.Contain("\"Hp\": 2"));
        }

        [Test]
        public void Tick_WithUnappliedEdits_KeepsTextAndShowsStaleHint()
        {
            using IDisposable token = LiveSessionRegistry.Register("game", _session);
            _panel.SelectSourceForTests(1);
            _panel.SelectLiveDocumentForTests(LiveDocumentIndex("Stats"));

            Json.value = "{ \"Hp\": 999 }";
            _session.Stats.Replace(new SampleStats { Hp = 3 });
            _panel.Tick(100.0);

            Assert.That(Json.value, Is.EqualTo("{ \"Hp\": 999 }"));
            Assert.That(StaleHint.style.display.value, Is.EqualTo(DisplayStyle.Flex));
        }

        [Test]
        public void SelectCollection_ShowsEntryKeysAndEntryJson()
        {
            _session.Items.Upsert(new SampleItem { ItemId = "potion", Count = 3 });
            using IDisposable token = LiveSessionRegistry.Register("game", _session);
            _panel.SelectSourceForTests(1);

            _panel.SelectLiveDocumentForTests(LiveDocumentIndex("Items"));

            VisualElement entrySection = _rootElement.Q<VisualElement>(ViewerUI.LiveEntrySectionName);
            Assert.That(entrySection.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            IList keys = _rootElement.Q<ListView>(ViewerUI.LiveEntriesListName).itemsSource;
            Assert.That(keys.Count, Is.EqualTo(1));
            Assert.That(keys[0], Is.EqualTo("potion"));

            _panel.SelectLiveEntryForTests(0);

            Assert.That(Json.value, Does.Contain("\"Count\": 3"));
            Assert.That(Json.isReadOnly, Is.False);
        }

        [Test]
        public void Dispose_UnsubscribesRegistryAndIgnoresLaterActivity()
        {
            _panel.Dispose();

            using IDisposable token = LiveSessionRegistry.Register("late", _session);

            Assert.That(Source.choices.Count, Is.EqualTo(1), "choices must not be rebuilt after Dispose");
            Assert.DoesNotThrow(() => _session.Stats.Replace(new SampleStats { Hp = 9 }));
            Assert.DoesNotThrow(() => _panel.Tick(50.0));
        }

        [Test]
        public void DisposeWhileLiveSelected_DetachesFromSession()
        {
            using IDisposable token = LiveSessionRegistry.Register("game", _session);
            _panel.SelectSourceForTests(1);
            _panel.SelectLiveDocumentForTests(LiveDocumentIndex("Stats"));

            _panel.Dispose();
            string before = Json.value;

            Assert.DoesNotThrow(() => _session.Stats.Replace(new SampleStats { Hp = 41 }));
            Assert.DoesNotThrow(() => _panel.Tick(100.0));
            Assert.That(Json.value, Is.EqualTo(before), "a disposed panel must not refresh");
        }
    }
}
