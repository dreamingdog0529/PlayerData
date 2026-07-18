using System;
using System.Collections.Generic;
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

        // ---- Collection sub-forms ----

        private const string TwoItemsJson =
            "{ \"potion\": { \"ItemId\": \"potion\", \"Count\": 3 }," +
            " \"sword\": { \"ItemId\": \"sword\", \"Count\": 1 } }";

        [Test]
        public void CollectionEditor_BuildsOneSubFormPerEntry_WithEntityRows()
        {
            CollectionFieldsEditor editor = new CollectionFieldsEditor(TwoItemsJson, typeof(SampleItem));

            Assert.That(editor.EntryKeysForTests.Count, Is.EqualTo(2));
            Assert.That(editor.EntryKeysForTests, Has.Member("potion"));
            Assert.That(editor.EntryKeysForTests, Has.Member("sword"));
            Assert.That(editor.EntryModelForTests("potion").Rows.Count, Is.EqualTo(2), "each entry exposes the entity's members");
            Assert.That(editor.Root.Q<VisualElement>(CollectionFieldsEditor.EntrySectionName("potion")), Is.Not.Null);
        }

        [Test]
        public void CollectionEditor_ToJson_RecombinesEditedAndUntouchedEntries()
        {
            CollectionFieldsEditor editor = new CollectionFieldsEditor(TwoItemsJson, typeof(SampleItem));

            editor.EntryViewForTests("potion").SetTextForTests("Count", "42");

            JObject result = JObject.Parse(editor.ToJson());
            Assert.That((int)result["potion"]["Count"], Is.EqualTo(42));
            Assert.That((int)result["sword"]["Count"], Is.EqualTo(1), "untouched entry must be preserved");
        }

        [Test]
        public void CollectionEditor_InvalidEntryField_SetsHasInvalid()
        {
            CollectionFieldsEditor editor = new CollectionFieldsEditor(TwoItemsJson, typeof(SampleItem));
            Assert.That(editor.HasInvalid, Is.False);

            editor.EntryViewForTests("potion").SetTextForTests("Count", "abc");

            Assert.That(editor.HasInvalid, Is.True);
        }

        [Test]
        public void CollectionEditor_EntryEdit_RaisesChanged()
        {
            CollectionFieldsEditor editor = new CollectionFieldsEditor(TwoItemsJson, typeof(SampleItem));
            int changed = 0;
            editor.Changed += () => changed++;

            editor.EntryViewForTests("sword").SetTextForTests("Count", "5");

            Assert.That(changed, Is.EqualTo(1));
        }

        [Test]
        public void CollectionEditor_EmptyCollection_ShowsEmptyLabel_AndEmptyJson()
        {
            CollectionFieldsEditor editor = new CollectionFieldsEditor("{}", typeof(SampleItem));

            Assert.That(editor.EntryKeysForTests.Count, Is.EqualTo(0));
            Assert.That(editor.Root.Q<Label>(CollectionFieldsEditor.EmptyLabelName), Is.Not.Null);
            Assert.That(editor.ToJson().Trim(), Is.EqualTo("{}"));
        }
    }

    // The former FieldEditorWindowTests (window-level Fields integration) was removed with the
    // staged flow it drove; the detail-pane and live-integration steps rebuild that coverage.
}
