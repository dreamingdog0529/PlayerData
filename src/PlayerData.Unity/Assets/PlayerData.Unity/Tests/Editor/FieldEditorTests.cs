using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class FieldEditorTests
    {
        // Plain fixture for the editor-kind matrix. FieldEditorModel is serializer-agnostic
        // (JSON + contract only), so this type does not need to be MemoryPackable.
        private sealed class FieldMatrixDoc
        {
            public bool Flag { get; set; }

            public string Name { get; set; } = string.Empty;

            public int IntValue { get; set; }

            public float FloatValue { get; set; }

            public decimal DecimalValue { get; set; }

            public SampleRank Rank { get; set; }

            public DateTime Timestamp { get; set; }

            public int? MaybeInt { get; set; }

            public List<int> Numbers { get; set; } = new List<int>();
        }

        private const string MatrixJson = @"{
  ""Flag"": true,
  ""Name"": ""hero"",
  ""IntValue"": 3,
  ""FloatValue"": 1.5,
  ""DecimalValue"": 2.25,
  ""Rank"": 1,
  ""Timestamp"": ""2026-07-17T00:00:00Z"",
  ""MaybeInt"": null,
  ""Numbers"": [1, 2]
}";

        private static FieldEditorModel CreateMatrixModel() =>
            FieldEditorModel.Create(MatrixJson, typeof(FieldMatrixDoc));

        private static FieldRow Row(FieldEditorModel model, string memberName)
        {
            foreach (FieldRow row in model.Rows)
            {
                if (row.MemberName == memberName)
                    return row;
            }

            Assert.Fail($"Row '{memberName}' not found.");
            return null;
        }

        // ---- Row construction / editor-kind mapping ----

        [Test]
        public void Create_MatrixDocument_MapsMembersToExpectedEditorKinds()
        {
            FieldEditorModel model = CreateMatrixModel();

            Assert.That(Row(model, "Flag").Kind, Is.EqualTo(FieldEditorKind.Toggle));
            Assert.That(Row(model, "Name").Kind, Is.EqualTo(FieldEditorKind.Text));
            Assert.That(Row(model, "IntValue").Kind, Is.EqualTo(FieldEditorKind.Number));
            Assert.That(Row(model, "FloatValue").Kind, Is.EqualTo(FieldEditorKind.Number));
            Assert.That(Row(model, "DecimalValue").Kind, Is.EqualTo(FieldEditorKind.Number));
            Assert.That(Row(model, "Rank").Kind, Is.EqualTo(FieldEditorKind.Enum));
            Assert.That(Row(model, "Timestamp").Kind, Is.EqualTo(FieldEditorKind.ReadOnly));
            Assert.That(Row(model, "MaybeInt").Kind, Is.EqualTo(FieldEditorKind.ReadOnly), "nullable primitives are read-only in v1");
            Assert.That(Row(model, "Numbers").Kind, Is.EqualTo(FieldEditorKind.ReadOnly));
        }

        [Test]
        public void Create_RecordWithNestedRecord_MapsNestedToReadOnly()
        {
            SampleProfile profile = new SampleProfile
            {
                Name = "hero",
                Level = 5,
                Spawn = new SamplePosition { X = 1.5f, Y = -2.25f },
            };
            string json = MemoryPackJsonConverter.ToJson(profile, typeof(SampleProfile));

            FieldEditorModel model = FieldEditorModel.Create(json, typeof(SampleProfile));

            Assert.That(Row(model, "Name").Kind, Is.EqualTo(FieldEditorKind.Text));
            Assert.That(Row(model, "Level").Kind, Is.EqualTo(FieldEditorKind.Number));
            Assert.That(Row(model, "Spawn").Kind, Is.EqualTo(FieldEditorKind.ReadOnly));
            Assert.That(Row(model, "Name").DisplayValue, Is.EqualTo("hero"));
            Assert.That(Row(model, "Level").DisplayValue, Is.EqualTo("5"));
        }

        // ---- Member selection matches MemoryPackContractResolver ----

        [Test]
        public void Create_MemoryPackIgnoredMember_IsAbsent()
        {
            string json = MemoryPackJsonConverter.ToJson(new SampleWithIgnored { BaseValue = 4 }, typeof(SampleWithIgnored));

            FieldEditorModel model = FieldEditorModel.Create(json, typeof(SampleWithIgnored));

            Assert.That(model.Rows.Count, Is.EqualTo(1));
            Assert.That(model.Rows[0].MemberName, Is.EqualTo("BaseValue"));
        }

        [Test]
        public void Create_MemoryPackIncludedPrivateMember_IsPresentAndEditable()
        {
            SampleWithPrivateIncluded value = new SampleWithPrivateIncluded { Secret = 9 };
            string json = MemoryPackJsonConverter.ToJson(value, typeof(SampleWithPrivateIncluded));

            FieldEditorModel model = FieldEditorModel.Create(json, typeof(SampleWithPrivateIncluded));

            Assert.That(model.Rows.Count, Is.EqualTo(1));
            FieldRow row = Row(model, "_secret");
            Assert.That(row.Kind, Is.EqualTo(FieldEditorKind.Number));
            Assert.That(row.DisplayValue, Is.EqualTo("9"));

            Assert.That(model.TrySetNumber("_secret", "5"), Is.True);
            SampleWithPrivateIncluded updated =
                (SampleWithPrivateIncluded)MemoryPackJsonConverter.ObjectFromJson(model.ToJson(), typeof(SampleWithPrivateIncluded));
            Assert.That(updated.Secret, Is.EqualTo(5));
        }

        // ---- Edits produce the updated document ----

        [Test]
        public void Edits_ProduceUpdatedObject_AndPreserveUntouchedMembers()
        {
            SampleStats stats = new SampleStats
            {
                Hp = 120,
                Shield = 4,
                Rank = SampleRank.Gold,
                Titles = new List<string> { "hero", "champion" },
                Counters = new Dictionary<string, int> { ["kills"] = 3 },
            };
            string json = MemoryPackJsonConverter.ToJson(stats, typeof(SampleStats));
            FieldEditorModel model = FieldEditorModel.Create(json, typeof(SampleStats));

            Assert.That(model.TrySetNumber("Hp", "42"), Is.True);
            model.SetEnum("Rank", nameof(SampleRank.Bronze));

            SampleStats updated = (SampleStats)MemoryPackJsonConverter.ObjectFromJson(model.ToJson(), typeof(SampleStats));
            Assert.That(updated.Hp, Is.EqualTo(42));
            Assert.That(updated.Rank, Is.EqualTo(SampleRank.Bronze));
            Assert.That(updated.Shield, Is.EqualTo(4), "untouched member must be preserved");
            Assert.That(updated.Titles, Is.EqualTo(new List<string> { "hero", "champion" }));
            Assert.That(updated.Counters["kills"], Is.EqualTo(3));
        }

        [Test]
        public void SetString_AndSetBool_UpdateWorkingJson()
        {
            FieldEditorModel model = CreateMatrixModel();

            model.SetString("Name", "renamed");
            model.SetBool("Flag", false);

            JObject result = JObject.Parse(model.ToJson());
            Assert.That((string)result["Name"], Is.EqualTo("renamed"));
            Assert.That((bool)result["Flag"], Is.False);
            Assert.That((int)result["IntValue"], Is.EqualTo(3), "untouched member must be preserved");
        }

        // ---- Numeric parse validation ----

        [Test]
        public void TrySetNumber_InvalidText_FlagsRowAndBlocksApply()
        {
            FieldEditorModel model = CreateMatrixModel();

            Assert.That(model.TrySetNumber("IntValue", "abc"), Is.False);
            Assert.That(Row(model, "IntValue").IsValid, Is.False);
            Assert.That(model.HasInvalid, Is.True, "Apply must be blocked while input is invalid");
            Assert.That((int)JObject.Parse(model.ToJson())["IntValue"], Is.EqualTo(3), "working copy keeps the last valid value");

            Assert.That(model.TrySetNumber("IntValue", "7"), Is.True);
            Assert.That(Row(model, "IntValue").IsValid, Is.True);
            Assert.That(model.HasInvalid, Is.False);
        }

        [Test]
        public void TrySetNumber_UsesInvariantCulture()
        {
            FieldEditorModel model = CreateMatrixModel();

            Assert.That(model.TrySetNumber("FloatValue", "2.5"), Is.True);
            Assert.That(model.TrySetNumber("FloatValue", "2,5"), Is.False);
        }

        [Test]
        public void Model_TracksDirtyState_IncludingFailedParses()
        {
            FieldEditorModel model = CreateMatrixModel();
            Assert.That(model.IsDirty, Is.False);

            model.TrySetNumber("IntValue", "abc");

            Assert.That(model.IsDirty, Is.True, "unparseable text sitting in a field is still an unapplied edit");
        }

        // ---- Enum editing ----

        [Test]
        public void EnumRow_ListsDefinedValues_WithCurrentSelected()
        {
            FieldEditorModel model = CreateMatrixModel();

            FieldRow rank = Row(model, "Rank");
            Assert.That(rank.EnumChoices.Count, Is.EqualTo(3));
            Assert.That(rank.EnumChoices, Has.Member("Bronze"));
            Assert.That(rank.EnumChoices, Has.Member("Silver"));
            Assert.That(rank.EnumChoices, Has.Member("Gold"));
            Assert.That(rank.EnumValue, Is.EqualTo("Silver"), "Rank: 1 must preselect Silver");
        }

        [Test]
        public void SetEnum_WritesUnderlyingValue()
        {
            FieldEditorModel model = CreateMatrixModel();

            model.SetEnum("Rank", "Gold");

            Assert.That((int)JObject.Parse(model.ToJson())["Rank"], Is.EqualTo((int)SampleRank.Gold));
        }

        // ---- Read-only rows ----

        [Test]
        public void ReadOnlyRows_ExposeJsonTabHint()
        {
            FieldEditorModel model = CreateMatrixModel();

            Assert.That(Row(model, "Timestamp").Hint, Is.EqualTo(FieldEditorModel.JsonOnlyHint));
            Assert.That(Row(model, "MaybeInt").Hint, Is.EqualTo(FieldEditorModel.JsonOnlyHint));
            Assert.That(Row(model, "Numbers").Hint, Is.EqualTo(FieldEditorModel.JsonOnlyHint));
            Assert.That(Row(model, "Name").Hint, Is.Null, "editable rows carry no hint");
        }

        // ---- View construction and validation feedback ----

        [Test]
        public void View_BuildsExpectedControls()
        {
            FieldEditorModel model = CreateMatrixModel();
            FieldEditorView view = new FieldEditorView(model);

            Assert.That(view.Root.Q<Toggle>(FieldEditorView.FieldName("Flag")), Is.Not.Null);
            Assert.That(view.Root.Q<TextField>(FieldEditorView.FieldName("Name")), Is.Not.Null);
            Assert.That(view.Root.Q<TextField>(FieldEditorView.FieldName("IntValue")), Is.Not.Null);

            DropdownField rank = view.Root.Q<DropdownField>(FieldEditorView.FieldName("Rank"));
            Assert.That(rank, Is.Not.Null);
            Assert.That(rank.choices.Count, Is.EqualTo(3));
            Assert.That(rank.value, Is.EqualTo("Silver"));

            Label hint = view.Root.Q<Label>(FieldEditorView.HintName("Numbers"));
            Assert.That(hint, Is.Not.Null);
            Assert.That(hint.text, Is.EqualTo(FieldEditorModel.JsonOnlyHint));
        }

        [Test]
        public void View_InvalidNumberInput_AddsInvalidClass_AndClearsOnFix()
        {
            FieldEditorModel model = CreateMatrixModel();
            FieldEditorView view = new FieldEditorView(model);
            int changedCount = 0;
            view.Changed += () => changedCount++;
            TextField field = view.Root.Q<TextField>(FieldEditorView.FieldName("IntValue"));

            view.SetTextForTests("IntValue", "abc");

            Assert.That(field.ClassListContains(FieldEditorView.InvalidClassName), Is.True);
            Assert.That(model.HasInvalid, Is.True);
            Assert.That(changedCount, Is.EqualTo(1));

            view.SetTextForTests("IntValue", "8");

            Assert.That(field.ClassListContains(FieldEditorView.InvalidClassName), Is.False);
            Assert.That(model.HasInvalid, Is.False);
            Assert.That(changedCount, Is.EqualTo(2));
        }

        [Test]
        public void View_ToggleAndEnumEdits_ReachTheModel()
        {
            FieldEditorModel model = CreateMatrixModel();
            FieldEditorView view = new FieldEditorView(model);

            view.SetToggleForTests("Flag", false);
            view.SetEnumForTests("Rank", "Gold");

            JObject result = JObject.Parse(model.ToJson());
            Assert.That((bool)result["Flag"], Is.False);
            Assert.That((int)result["Rank"], Is.EqualTo((int)SampleRank.Gold));
        }
    }

    // Window-level integration: tabs, the disk pipeline, the live pipeline and stale-hint
    // behavior. Mirrors the fixture style of PlayerDataViewerLiveModeTests.
    public sealed class FieldEditorWindowTests
    {
        private string _root;
        private SampleEditorSession _session;
        private PlayerDataViewerController _controller;
        private VisualElement _rootElement;
        private ViewerPanel _panel;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayerDataEditorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            LiveSessionRegistry.ClearForTests();
            _session = new SampleEditorSession(new DirectorySaveBackend(_root));
            _controller = new PlayerDataViewerController();
            _rootElement = new VisualElement();
            _panel = ViewerUI.BuildInto(_rootElement, _controller, _root);
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

        private Button FieldsTab => _rootElement.Q<Button>(ViewerUI.FieldsTabButtonName);

        private Button JsonTab => _rootElement.Q<Button>(ViewerUI.JsonTabButtonName);

        private ScrollView FieldsScroll => _rootElement.Q<ScrollView>(ViewerUI.FieldsScrollName);

        private ScrollView JsonScroll => _rootElement.Q<ScrollView>(ViewerUI.JsonScrollName);

        private Label FieldsHint => _rootElement.Q<Label>(ViewerUI.FieldsHintName);

        private Label StaleHint => _rootElement.Q<Label>(ViewerUI.StaleHintName);

        private Button ApplyButton => _rootElement.Q<Button>(ViewerUI.ApplyButtonName);

        private TextField Json => _rootElement.Q<TextField>(ViewerUI.DocumentJsonName);

        private int SessionTypeIndex(Type sessionType)
        {
            for (int i = 0; i < _controller.SessionTypes.Count; i++)
            {
                if (_controller.SessionTypes[i] == sessionType)
                    return i;
            }

            Assert.Fail($"Session type '{sessionType.Name}' not found.");
            return -1;
        }

        private int DocumentIndex(string storageKey)
        {
            IList items = _rootElement.Q<ListView>(ViewerUI.DocumentsListName).itemsSource;
            for (int i = 0; i < items.Count; i++)
            {
                if (((DocumentEntry)items[i]).StorageKey == storageKey)
                    return i;
            }

            Assert.Fail($"Document '{storageKey}' not found.");
            return -1;
        }

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

        private void SelectDiskDocument(string storageKey)
        {
            string saveRoot = SampleSaveMenu.Create(_root);
            _panel.SelectSessionForTests(SessionTypeIndex(typeof(SampleEditorSession)));
            _panel.ScanForTests(saveRoot);
            _panel.SelectSaveForTests(0);
            _panel.SelectDocumentForTests(DocumentIndex(storageKey));
        }

        [Test]
        public void Tabs_Exist_FieldsIsDefault_AndSwitchingShowsJson()
        {
            Assert.That(FieldsTab, Is.Not.Null);
            Assert.That(JsonTab, Is.Not.Null);
            // Fields stay on the primary surface; JSON is under Advanced.
            Assert.That(FieldsScroll.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(JsonScroll.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(FieldsTab.enabledSelf, Is.False, "active tab button is disabled");
            Assert.That(JsonTab.enabledSelf, Is.True);

            _panel.SelectTabForTests(showFields: false);

            Assert.That(FieldsScroll.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(JsonScroll.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(_panel.AdvancedOpenForTests, Is.True);
            Assert.That(FieldsTab.enabledSelf, Is.True);
            Assert.That(JsonTab.enabledSelf, Is.False);
        }

        [Test]
        public void DiskDocument_FieldsApply_FlowsThroughExistingPipeline()
        {
            SelectDiskDocument("SampleProfile");
            FieldEditorView view = _panel.FieldsViewForTests;
            Assert.That(view, Is.Not.Null);
            Assert.That(ApplyButton.enabledSelf, Is.False, "Apply stays disabled until an edit is made");

            view.SetTextForTests("Name", "edited-name");
            Assert.That(ApplyButton.enabledSelf, Is.True);
            _panel.ApplyForTests();

            DocumentView reloaded = _controller.GetDocumentView("SampleProfile");
            Assert.That(reloaded, Is.Not.Null);
            Assert.That(reloaded.Json, Does.Contain("edited-name"));
            Assert.That(reloaded.Json, Does.Contain("\"Level\": 5"), "untouched member must be preserved");
        }

        [Test]
        public void DiskDocument_InvalidFieldInput_DisablesApply_UntilFixed()
        {
            SelectDiskDocument("SampleProfile");
            FieldEditorView view = _panel.FieldsViewForTests;

            view.SetTextForTests("Level", "abc");
            Assert.That(ApplyButton.enabledSelf, Is.False, "invalid numeric input must block Apply");

            view.SetTextForTests("Level", "7");
            Assert.That(ApplyButton.enabledSelf, Is.True);
        }

        [Test]
        public void DiskCollection_FieldsTab_ShowsJsonOnlyHint()
        {
            SelectDiskDocument("items-v1");

            Assert.That(_panel.FieldsViewForTests, Is.Null);
            Assert.That(FieldsHint.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(FieldsHint.text, Does.Contain(FieldEditorModel.JsonOnlyHint));
            Assert.That(ApplyButton.enabledSelf, Is.False, "no Fields payload exists for a collection");

            _panel.SelectTabForTests(showFields: false);

            Assert.That(ApplyButton.enabledSelf, Is.False, "no edit yet: Apply stays disabled on the JSON tab too");
            _panel.SetJsonTextForTests(Json.value + " ");
            Assert.That(ApplyButton.enabledSelf, Is.True, "the JSON tab still edits collection payloads");
        }

        [Test]
        public void LiveDocument_FieldsEditThenTick_ShowsStaleHint_AndKeepsEditor()
        {
            using IDisposable token = LiveSessionRegistry.Register("game", _session);
            _panel.SelectSourceForTests(1);
            _panel.SelectLiveDocumentForTests(LiveDocumentIndex("Stats"));
            FieldEditorView view = _panel.FieldsViewForTests;
            Assert.That(view, Is.Not.Null);

            view.SetTextForTests("Hp", "123");
            _session.Stats.Replace(new SampleStats { Hp = 5 });
            _panel.Tick(100.0);

            Assert.That(StaleHint.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(_panel.FieldsViewForTests, Is.SameAs(view), "tick must not rebuild a Fields editor mid-edit");
            Assert.That(view.Root.Q<TextField>(FieldEditorView.FieldName("Hp")).value, Is.EqualTo("123"));
        }

        [Test]
        public void LiveDocument_WithoutFieldEdits_TickRefreshesFieldsView()
        {
            using IDisposable token = LiveSessionRegistry.Register("game", _session);
            _panel.SelectSourceForTests(1);
            _panel.SelectLiveDocumentForTests(LiveDocumentIndex("Stats"));

            _session.Stats.Replace(new SampleStats { Hp = 77 });
            _panel.Tick(100.0);

            Assert.That(StaleHint.style.display.value, Is.EqualTo(DisplayStyle.None));
            TextField hp = _panel.FieldsViewForTests.Root.Q<TextField>(FieldEditorView.FieldName("Hp"));
            Assert.That(hp.value, Is.EqualTo("77"));
        }

        [Test]
        public void LiveDocument_FieldsApply_ReplacesThroughSession()
        {
            using IDisposable token = LiveSessionRegistry.Register("game", _session);
            _panel.SelectSourceForTests(1);
            _panel.SelectLiveDocumentForTests(LiveDocumentIndex("Stats"));

            _panel.FieldsViewForTests.SetTextForTests("Hp", "321");
            _panel.ApplyForTests();

            Assert.That(_session.Stats.Value.Hp, Is.EqualTo(321));
            Assert.That(StaleHint.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(_panel.FieldsModelForTests.IsDirty, Is.False, "apply reloads a clean working copy");
            Assert.That(Json.value, Does.Contain("\"Hp\": 321"));
        }

        [Test]
        public void LiveBagEntry_FieldsApply_SetsThroughSession()
        {
            _session.Items.Upsert(new SampleItem { ItemId = "potion", Count = 3 });
            using IDisposable token = LiveSessionRegistry.Register("game", _session);
            _panel.SelectSourceForTests(1);
            _panel.SelectLiveDocumentForTests(LiveDocumentIndex("Items"));
            _panel.SelectLiveEntryForTests(0);
            FieldEditorView view = _panel.FieldsViewForTests;
            Assert.That(view, Is.Not.Null, "bag entries get field editors too");

            view.SetTextForTests("Count", "9");
            _panel.ApplyForTests();

            Assert.That(_session.Items.Get("potion").Count, Is.EqualTo(9));
        }
    }
}
