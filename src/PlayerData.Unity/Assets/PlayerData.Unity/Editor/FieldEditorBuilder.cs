using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.UIElements;

namespace PlayerData.Unity.Editor
{
    public enum FieldEditorKind
    {
        Toggle,
        Text,
        Number,
        Enum,
        ReadOnly,
    }

    /// <summary>One top-level serializable member of a document, prepared for inline editing.</summary>
    public sealed class FieldRow
    {
        internal FieldRow(
            string memberName,
            Type memberType,
            FieldEditorKind kind,
            string displayValue,
            bool initialBool,
            IReadOnlyList<string> enumChoices,
            string enumValue,
            string? hint)
        {
            MemberName = memberName;
            MemberType = memberType;
            Kind = kind;
            DisplayValue = displayValue;
            InitialBool = initialBool;
            EnumChoices = enumChoices;
            EnumValue = enumValue;
            Hint = hint;
        }

        public string MemberName { get; }

        public Type MemberType { get; }

        public FieldEditorKind Kind { get; }

        /// <summary>Initial text: editor seed for Text/Number rows, compact preview for ReadOnly rows.</summary>
        public string DisplayValue { get; }

        public bool InitialBool { get; }

        public IReadOnlyList<string> EnumChoices { get; }

        /// <summary>Currently selected enum member name; empty for non-enum rows.</summary>
        public string EnumValue { get; }

        /// <summary>Non-null on ReadOnly rows: how to edit this member instead.</summary>
        public string? Hint { get; }

        /// <summary>False after a failed numeric parse until valid text is entered.</summary>
        public bool IsValid { get; internal set; } = true;
    }

    /// <summary>
    /// UI-independent working copy behind the Fields tab. Edits mutate a JObject loaded from the
    /// document's current JSON; <see cref="ToJson"/> produces the payload for the existing Apply
    /// pipelines. Going through JSON (instead of reflection writes) sidesteps records/init-only
    /// members entirely and keeps MissingMemberHandling guarding the final deserialization.
    /// Member selection is single-sourced from MemoryPackJsonConverter's contract resolver.
    /// </summary>
    public sealed class FieldEditorModel
    {
        public const string JsonOnlyHint = ViewerDisplayNames.JsonOnlyHint;

        private const int PreviewMaxLength = 120;

        private readonly JObject _root;
        private readonly List<FieldRow> _rows = new List<FieldRow>();
        private readonly HashSet<string> _invalidMembers = new HashSet<string>(StringComparer.Ordinal);
        private bool _dirty;

        private FieldEditorModel(JObject root, Type documentType)
        {
            _root = root;
            JsonObjectContract contract = MemoryPackJsonConverter.ResolveObjectContract(documentType);
            foreach (JsonProperty property in contract.Properties)
            {
                if (property.Ignored || !property.Readable || property.PropertyName is null || property.PropertyType is null)
                    continue;
                _rows.Add(CreateRow(property));
            }
        }

        public static FieldEditorModel Create(string json, Type documentType)
        {
            if (json is null) throw new ArgumentNullException(nameof(json));
            if (documentType is null) throw new ArgumentNullException(nameof(documentType));

            JObject root;
            // DateParseHandling.None mirrors MemoryPackJsonConverter: date-looking strings stay strings.
            using (JsonTextReader reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                root = JObject.Load(reader);
            return new FieldEditorModel(root, documentType);
        }

        public IReadOnlyList<FieldRow> Rows => _rows;

        /// <summary>True while any numeric row holds unparseable text; callers must block Apply.</summary>
        public bool HasInvalid => _invalidMembers.Count > 0;

        /// <summary>True after any edit (including a failed numeric parse still sitting in a field).</summary>
        public bool IsDirty => _dirty;

        public void SetBool(string memberName, bool value)
        {
            RequireRow(memberName, FieldEditorKind.Toggle);
            _root[memberName] = value;
            _dirty = true;
        }

        public void SetString(string memberName, string value)
        {
            RequireRow(memberName, FieldEditorKind.Text);
            _root[memberName] = value ?? string.Empty;
            _dirty = true;
        }

        /// <summary>
        /// Parses with the member type's own parser (InvariantCulture). On failure the row is
        /// flagged invalid and the working copy keeps its previous value.
        /// </summary>
        public bool TrySetNumber(string memberName, string text)
        {
            FieldRow row = RequireRow(memberName, FieldEditorKind.Number);
            if (TryParseNumber(row.MemberType, text, out JValue? parsed))
            {
                _root[memberName] = parsed;
                row.IsValid = true;
                _invalidMembers.Remove(memberName);
                _dirty = true;
                return true;
            }

            row.IsValid = false;
            _invalidMembers.Add(memberName);
            _dirty = true;
            return false;
        }

        public void SetEnum(string memberName, string valueName)
        {
            FieldRow row = RequireRow(memberName, FieldEditorKind.Enum);
            object value = Enum.Parse(row.MemberType, valueName);
            // FromObject with the default serializer writes the underlying number, matching how
            // MemoryPackJsonConverter serializes enums (no StringEnumConverter in its settings).
            _root[memberName] = JToken.FromObject(value);
            _dirty = true;
        }

        /// <summary>The edited document as a JSON payload for the existing Apply pipelines.</summary>
        public string ToJson() => _root.ToString(Formatting.Indented);

        /// <summary>
        /// Current working-copy token for a member, or null when absent. Read-only peek used by
        /// the collection surface to derive an entry's key from its [PlayerDataKey] member.
        /// </summary>
        internal JToken? GetMemberToken(string memberName) => _root[memberName];

        private FieldRow CreateRow(JsonProperty property)
        {
            Type type = property.PropertyType!;
            string name = property.PropertyName!;
            JToken? token = _root[name];
            FieldEditorKind kind = Classify(type, property.Writable);

            switch (kind)
            {
                case FieldEditorKind.Toggle:
                {
                    bool current = token is JValue boolValue && boolValue.Type == JTokenType.Boolean && (bool)boolValue;
                    return new FieldRow(name, type, kind, current.ToString(), current, Array.Empty<string>(), string.Empty, hint: null);
                }
                case FieldEditorKind.Text:
                case FieldEditorKind.Number:
                    return new FieldRow(name, type, kind, TokenToText(token), initialBool: false, Array.Empty<string>(), string.Empty, hint: null);
                case FieldEditorKind.Enum:
                {
                    string current = EnumNameFromToken(type, token);
                    List<string> choices = new List<string>(Enum.GetNames(type));
                    if (!choices.Contains(current))
                        choices.Insert(0, current); // undefined stored value: keep it selectable so building never throws
                    return new FieldRow(name, type, kind, current, initialBool: false, choices, current, hint: null);
                }
                default:
                    return new FieldRow(name, type, kind, Preview(token), initialBool: false, Array.Empty<string>(), string.Empty, JsonOnlyHint);
            }
        }

        private static FieldEditorKind Classify(Type type, bool writable)
        {
            if (!writable)
                return FieldEditorKind.ReadOnly;
            if (type == typeof(bool))
                return FieldEditorKind.Toggle;
            if (type == typeof(string))
                return FieldEditorKind.Text;
            if (type.IsEnum)
                return FieldEditorKind.Enum;
            if (IsSupportedNumber(type))
                return FieldEditorKind.Number;
            // Everything else (nested objects, collections, DateTime, nullable structs, ...):
            // read-only preview, edited via the JSON tab.
            return FieldEditorKind.ReadOnly;
        }

        private static bool IsSupportedNumber(Type type)
        {
            if (type.IsEnum)
                return false;
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryParseNumber(Type type, string text, out JValue? value)
        {
            value = null;
            if (text is null)
                return false;
            text = text.Trim();
            CultureInfo culture = CultureInfo.InvariantCulture;

            switch (Type.GetTypeCode(type))
            {
                case TypeCode.SByte:
                    if (sbyte.TryParse(text, NumberStyles.Integer, culture, out sbyte i8)) value = new JValue(i8);
                    break;
                case TypeCode.Byte:
                    if (byte.TryParse(text, NumberStyles.Integer, culture, out byte u8)) value = new JValue(u8);
                    break;
                case TypeCode.Int16:
                    if (short.TryParse(text, NumberStyles.Integer, culture, out short i16)) value = new JValue(i16);
                    break;
                case TypeCode.UInt16:
                    if (ushort.TryParse(text, NumberStyles.Integer, culture, out ushort u16)) value = new JValue(u16);
                    break;
                case TypeCode.Int32:
                    if (int.TryParse(text, NumberStyles.Integer, culture, out int i32)) value = new JValue(i32);
                    break;
                case TypeCode.UInt32:
                    if (uint.TryParse(text, NumberStyles.Integer, culture, out uint u32)) value = new JValue(u32);
                    break;
                case TypeCode.Int64:
                    if (long.TryParse(text, NumberStyles.Integer, culture, out long i64)) value = new JValue(i64);
                    break;
                case TypeCode.UInt64:
                    if (ulong.TryParse(text, NumberStyles.Integer, culture, out ulong u64)) value = new JValue(u64);
                    break;
                case TypeCode.Single:
                    if (float.TryParse(text, NumberStyles.Float, culture, out float f32)) value = new JValue(f32);
                    break;
                case TypeCode.Double:
                    if (double.TryParse(text, NumberStyles.Float, culture, out double f64)) value = new JValue(f64);
                    break;
                case TypeCode.Decimal:
                    if (decimal.TryParse(text, NumberStyles.Float, culture, out decimal dec)) value = new JValue(dec);
                    break;
            }

            return value is not null;
        }

        private static string TokenToText(JToken? token)
        {
            if (token is not JValue value || value.Type == JTokenType.Null || value.Value is null)
                return string.Empty;
            return Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string EnumNameFromToken(Type enumType, JToken? token)
        {
            if (token is JValue value && value.Value is not null)
            {
                if (value.Type == JTokenType.Integer)
                    return Enum.ToObject(enumType, value.Value).ToString();
                if (value.Type == JTokenType.String)
                    return (string)value.Value;
            }

            return Enum.ToObject(enumType, 0).ToString();
        }

        private static string Preview(JToken? token)
        {
            if (token is null)
                return string.Empty;
            string text = token.ToString(Formatting.None);
            return text.Length <= PreviewMaxLength ? text : text.Substring(0, PreviewMaxLength) + "…";
        }

        private FieldRow RequireRow(string memberName, FieldEditorKind kind)
        {
            if (memberName is null) throw new ArgumentNullException(nameof(memberName));

            foreach (FieldRow row in _rows)
            {
                if (!string.Equals(row.MemberName, memberName, StringComparison.Ordinal))
                    continue;
                if (row.Kind != kind)
                    throw new InvalidOperationException($"Member '{memberName}' is a {row.Kind} field, not {kind}.");
                return row;
            }

            throw new KeyNotFoundException($"No serializable member named '{memberName}' exists on this document.");
        }
    }

    /// <summary>
    /// The Fields-tab editing surface for one document: a UIElements root plus the working-copy
    /// operations the viewer needs to serialize, gate Apply and detect edits. Single documents
    /// (<see cref="FieldEditorView"/>) and collection documents
    /// (<see cref="CollectionFieldsEditor"/>) each supply their own implementation so the panel
    /// treats both uniformly.
    /// </summary>
    internal interface IFieldsEditor
    {
        VisualElement Root { get; }

        /// <summary>The edited document as a JSON payload for the existing Apply pipelines.</summary>
        string ToJson();

        /// <summary>True while any field holds unparseable input; callers must block Apply.</summary>
        bool HasInvalid { get; }

        event Action Changed;
    }

    /// <summary>
    /// Builds the UIElements rows for a <see cref="FieldEditorModel"/> and routes control edits
    /// into it. Raises <see cref="Changed"/> after every edit so the host can re-evaluate the
    /// Apply gate (<see cref="FieldEditorModel.HasInvalid"/>).
    /// </summary>
    internal sealed class FieldEditorView : IFieldsEditor
    {
        internal const string InvalidClassName = "playerdata-field-invalid";
        internal const string HintClassName = "playerdata-field-hint";

        private readonly FieldEditorModel _model;
        private readonly Dictionary<string, Action<string>> _textSetters = new Dictionary<string, Action<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Action<bool>> _toggleSetters = new Dictionary<string, Action<bool>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Action<string>> _enumSetters = new Dictionary<string, Action<string>>(StringComparer.Ordinal);

        internal FieldEditorView(FieldEditorModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            Root = new VisualElement();
            foreach (FieldRow row in model.Rows)
                Root.Add(BuildRow(row));
        }

        public VisualElement Root { get; }

        /// <summary>The working copy this view edits; exposed for the collection surface and tests.</summary>
        internal FieldEditorModel Model => _model;

        public string ToJson() => _model.ToJson();

        public bool HasInvalid => _model.HasInvalid;

        public event Action? Changed;

        internal static string FieldName(string memberName) => "field-" + memberName;

        internal static string HintName(string memberName) => "field-" + memberName + "-hint";

        // ---- Test hooks ----
        // Detached elements (no panel) do not dispatch ChangeEvent, so EditMode tests drive the
        // same handlers the controls are wired to.

        internal void SetTextForTests(string memberName, string text) => RequireSetter(_textSetters, memberName)(text);

        internal void SetToggleForTests(string memberName, bool value) => RequireSetter(_toggleSetters, memberName)(value);

        internal void SetEnumForTests(string memberName, string choice) => RequireSetter(_enumSetters, memberName)(choice);

        private static Action<T> RequireSetter<T>(Dictionary<string, Action<T>> setters, string memberName)
        {
            if (setters.TryGetValue(memberName, out Action<T> setter))
                return setter;
            throw new KeyNotFoundException($"No editor of the requested kind exists for member '{memberName}'.");
        }

        private VisualElement BuildRow(FieldRow row)
        {
            string member = row.MemberName;
            switch (row.Kind)
            {
                case FieldEditorKind.Toggle:
                {
                    Toggle toggle = new Toggle(member) { value = row.InitialBool };
                    toggle.name = FieldName(member);
                    toggle.RegisterValueChangedCallback(e => ApplyToggle(member, e.newValue));
                    _toggleSetters[member] = value =>
                    {
                        toggle.SetValueWithoutNotify(value);
                        ApplyToggle(member, value);
                    };
                    return toggle;
                }
                case FieldEditorKind.Text:
                {
                    TextField text = new TextField(member) { value = row.DisplayValue };
                    text.name = FieldName(member);
                    text.RegisterValueChangedCallback(e => ApplyText(member, e.newValue));
                    _textSetters[member] = value =>
                    {
                        text.SetValueWithoutNotify(value);
                        ApplyText(member, value);
                    };
                    return text;
                }
                case FieldEditorKind.Number:
                {
                    TextField number = new TextField(member) { value = row.DisplayValue };
                    number.name = FieldName(member);
                    number.RegisterValueChangedCallback(e => ApplyNumber(number, member, e.newValue));
                    _textSetters[member] = value =>
                    {
                        number.SetValueWithoutNotify(value);
                        ApplyNumber(number, member, value);
                    };
                    return number;
                }
                case FieldEditorKind.Enum:
                {
                    List<string> choices = new List<string>(row.EnumChoices);
                    int index = choices.IndexOf(row.EnumValue);
                    DropdownField dropdown = new DropdownField(member, choices, index < 0 ? 0 : index);
                    dropdown.name = FieldName(member);
                    dropdown.RegisterValueChangedCallback(e => ApplyEnum(member, e.newValue));
                    _enumSetters[member] = value =>
                    {
                        dropdown.SetValueWithoutNotify(value);
                        ApplyEnum(member, value);
                    };
                    return dropdown;
                }
                default:
                {
                    VisualElement readOnlyRow = new VisualElement();
                    readOnlyRow.name = FieldName(member);
                    readOnlyRow.style.flexDirection = FlexDirection.Row;

                    Label label = new Label(member);
                    label.AddToClassList("playerdata-field__label");
                    readOnlyRow.Add(label);

                    Label preview = new Label(row.DisplayValue);
                    preview.style.flexShrink = 1;
                    readOnlyRow.Add(preview);

                    Label hint = new Label(row.Hint ?? string.Empty);
                    hint.name = HintName(member);
                    hint.AddToClassList(HintClassName);
                    hint.style.unityFontStyleAndWeight = FontStyle.Italic;
                    hint.style.marginLeft = 8;
                    readOnlyRow.Add(hint);
                    return readOnlyRow;
                }
            }
        }

        private void ApplyToggle(string member, bool value)
        {
            _model.SetBool(member, value);
            Changed?.Invoke();
        }

        private void ApplyText(string member, string value)
        {
            _model.SetString(member, value);
            Changed?.Invoke();
        }

        private void ApplyNumber(TextField field, string member, string text)
        {
            bool valid = _model.TrySetNumber(member, text);
            field.EnableInClassList(InvalidClassName, !valid);
            SetInvalidStyle(field, !valid);
            Changed?.Invoke();
        }

        private void ApplyEnum(string member, string choice)
        {
            _model.SetEnum(member, choice);
            Changed?.Invoke();
        }

        private static void SetInvalidStyle(TextField field, bool invalid)
        {
            if (invalid)
            {
                Color red = new Color(0.9f, 0.25f, 0.25f);
                field.style.borderTopColor = red;
                field.style.borderBottomColor = red;
                field.style.borderLeftColor = red;
                field.style.borderRightColor = red;
                field.style.borderTopWidth = 1;
                field.style.borderBottomWidth = 1;
                field.style.borderLeftWidth = 1;
                field.style.borderRightWidth = 1;
            }
            else
            {
                field.style.borderTopColor = StyleKeyword.Null;
                field.style.borderBottomColor = StyleKeyword.Null;
                field.style.borderLeftColor = StyleKeyword.Null;
                field.style.borderRightColor = StyleKeyword.Null;
                field.style.borderTopWidth = StyleKeyword.Null;
                field.style.borderBottomWidth = StyleKeyword.Null;
                field.style.borderLeftWidth = StyleKeyword.Null;
                field.style.borderRightWidth = StyleKeyword.Null;
            }
        }
    }

    /// <summary>
    /// The Fields-tab surface for a collection document: one sub-form per entry, reusing
    /// <see cref="FieldEditorModel"/>/<see cref="FieldEditorView"/> for each entry's entity, plus
    /// Inspector-style add/remove. An entry's key is the value of the entity's [PlayerDataKey]
    /// member — the single source of truth the runtime bag also keys on — so editing that member
    /// renames the entry and no separate key can drift out of sync. <see cref="ToJson"/> recombines
    /// the entries into the same {key: entity} JSON the JSON tab and the Apply pipelines speak, so
    /// editing here is byte-identical to editing in the JSON view. Duplicate keys are flagged and
    /// block Apply via <see cref="HasInvalid"/>.
    /// </summary>
    internal sealed class CollectionFieldsEditor : IFieldsEditor
    {
        internal const string EmptyLabelName = "collection-empty";
        internal const string AddButtonName = "collection-add";
        internal const string RemoveButtonName = "collection-remove";
        internal const string DuplicateWarningName = "collection-dup-warning";

        // Indents each entry's fields under its key header so the grouping reads at a glance.
        private const int EntryIndent = 12;
        private const string DefaultNewStringKey = "newEntry";

        private readonly Type _entityType;
        private readonly Type? _keyType;
        private readonly string? _keyMemberName;
        private readonly bool _keyIsNumeric;
        private readonly VisualElement _entriesContainer;
        private readonly Label _emptyLabel;
        private readonly Button _addButton;
        private readonly string? _defaultEntityJson;
        private readonly List<Entry> _entries = new List<Entry>();
        private bool _hasDuplicate;

        internal CollectionFieldsEditor(string json, Type entityType, Type? keyType)
        {
            if (json is null) throw new ArgumentNullException(nameof(json));
            _entityType = entityType ?? throw new ArgumentNullException(nameof(entityType));
            _keyType = keyType;
            _keyMemberName = ResolveKeyMemberJsonName(entityType);
            _keyIsNumeric = keyType is not null && IsNumericKey(keyType);
            _defaultEntityJson = TryBuildDefaultEntityJson(entityType);

            JObject dictionary;
            // DateParseHandling.None mirrors FieldEditorModel/MemoryPackJsonConverter: date-looking
            // strings stay strings so untouched entries round-trip unchanged.
            using (JsonTextReader reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                dictionary = JObject.Load(reader);

            Root = new VisualElement();
            _entriesContainer = new VisualElement();
            Root.Add(_entriesContainer);

            _emptyLabel = new Label(ViewerDisplayNames.EmptyCollectionLabel);
            _emptyLabel.name = EmptyLabelName;
            _emptyLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            _entriesContainer.Add(_emptyLabel);

            foreach (JProperty property in dictionary.Properties())
            {
                Entry entry = BuildEntry(property.Name, property.Value);
                _entries.Add(entry);
                _entriesContainer.Add(entry.Section);
            }

            _addButton = new Button(AddEntry) { text = ViewerDisplayNames.AddEntryLabel };
            _addButton.name = AddButtonName;
            if (_defaultEntityJson is null)
            {
                // A non-constructible entity has no template to append; keep the button visible but
                // disabled so the JSON tab stays the way to add entries.
                _addButton.SetEnabled(false);
                _addButton.tooltip = $"Cannot create a default {ViewerDisplayNames.ShortTypeName(entityType)}.";
            }

            Root.Add(_addButton);

            RecomputeKeys();
        }

        public VisualElement Root { get; }

        public event Action? Changed;

        public bool HasInvalid
        {
            get
            {
                if (_hasDuplicate)
                    return true;
                foreach (Entry entry in _entries)
                {
                    if (entry.Model is not null && entry.Model.HasInvalid)
                        return true;
                }

                return false;
            }
        }

        public string ToJson()
        {
            JObject root = new JObject();
            foreach (Entry entry in _entries)
            {
                // Editable entries re-serialize through their model, keyed by the current key
                // member; non-object entries (which cannot back a field form) round-trip untouched.
                // A duplicate key overwrites here, but HasInvalid blocks Apply before that matters.
                if (entry.Model is not null)
                    root[entry.DerivedKey] = JToken.Parse(entry.Model.ToJson());
                else
                    root[entry.DerivedKey] = entry.RawValue.DeepClone();
            }

            return root.ToString(Formatting.Indented);
        }

        internal static string EntrySectionName(string key) => "collection-entry-" + key;

        // ---- Test hooks ----

        internal IReadOnlyList<string> EntryKeysForTests
        {
            get
            {
                List<string> keys = new List<string>(_entries.Count);
                foreach (Entry entry in _entries)
                    keys.Add(entry.DerivedKey);
                return keys;
            }
        }

        internal bool HasDuplicateKeysForTests => _hasDuplicate;

        internal FieldEditorView EntryViewForTests(string key)
        {
            Entry entry = RequireEntry(key);
            return entry.View ?? throw new InvalidOperationException($"Collection entry '{key}' has no editable fields.");
        }

        internal FieldEditorModel EntryModelForTests(string key)
        {
            Entry entry = RequireEntry(key);
            return entry.Model ?? throw new InvalidOperationException($"Collection entry '{key}' has no editable fields.");
        }

        internal void AddEntryForTests() => AddEntry();

        internal void RemoveEntryForTests(string key) => RemoveEntry(RequireEntry(key));

        private Entry RequireEntry(string key)
        {
            foreach (Entry entry in _entries)
            {
                if (string.Equals(entry.DerivedKey, key, StringComparison.Ordinal))
                    return entry;
            }

            throw new KeyNotFoundException($"No collection entry named '{key}'.");
        }

        private Entry BuildEntry(string key, JToken value)
        {
            VisualElement section = new VisualElement();
            section.name = EntrySectionName(key);
            section.AddToClassList("playerdata-viewer__entry-card");

            VisualElement headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.alignItems = Align.Center;

            Label header = new Label(key);
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            headerRow.Add(header);

            Label duplicateWarning = new Label(ViewerDisplayNames.DuplicateKeyWarning);
            duplicateWarning.name = DuplicateWarningName;
            duplicateWarning.style.marginLeft = 8;
            duplicateWarning.style.color = new Color(0.9f, 0.4f, 0.4f);
            duplicateWarning.style.display = DisplayStyle.None;
            headerRow.Add(duplicateWarning);

            VisualElement spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            headerRow.Add(spacer);

            Entry entry;
            // Only object entries can back a field form. A null or scalar entry (unusual, but the
            // JSON may hold anything) is shown as a read-only preview and round-trips untouched.
            if (!(value is JObject entityJson))
            {
                section.Add(headerRow);
                Label preview = new Label(value is null ? "null" : value.ToString(Formatting.None));
                preview.style.marginLeft = EntryIndent;
                section.Add(preview);
                entry = new Entry(key, model: null, view: null, value ?? JValue.CreateNull(), section, header, duplicateWarning);
            }
            else
            {
                FieldEditorModel model = FieldEditorModel.Create(entityJson.ToString(Formatting.Indented), _entityType);
                FieldEditorView view = new FieldEditorView(model);
                view.Changed += OnEntryChanged;
                view.Root.style.marginLeft = EntryIndent;
                section.Add(headerRow);
                section.Add(view.Root);
                entry = new Entry(key, model, view, value, section, header, duplicateWarning);
            }

            Button remove = new Button(() => RemoveEntry(entry)) { text = ViewerDisplayNames.RemoveEntryLabel };
            remove.name = RemoveButtonName;
            headerRow.Add(remove);
            return entry;
        }

        private void AddEntry()
        {
            if (_defaultEntityJson is null)
                return;

            JObject entityJson = JObject.Parse(_defaultEntityJson);
            AssignUniqueKey(entityJson);

            Entry entry = BuildEntry(DeriveKey(entityJson), entityJson);
            _entries.Add(entry);
            _entriesContainer.Add(entry.Section);
            RecomputeKeys();
            RaiseChanged();
        }

        private void RemoveEntry(Entry entry)
        {
            if (!_entries.Remove(entry))
                return;

            if (entry.View is not null)
                entry.View.Changed -= OnEntryChanged;
            _entriesContainer.Remove(entry.Section);
            RecomputeKeys();
            RaiseChanged();
        }

        private void OnEntryChanged()
        {
            // A key member edit renames an entry, which can create or clear a duplicate, so keys are
            // recomputed on every edit before the host re-evaluates the Apply gate.
            RecomputeKeys();
            RaiseChanged();
        }

        // Re-derives every entry's key, refreshes headers, and flags duplicate keys. Kept cheap
        // (linear over the entries) because it runs on each edit; collections are small.
        private void RecomputeKeys()
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Entry entry in _entries)
            {
                entry.DerivedKey = entry.Model is not null ? DeriveKey(entry.Model) : entry.OriginalKey;
                counts[entry.DerivedKey] = counts.TryGetValue(entry.DerivedKey, out int count) ? count + 1 : 1;
            }

            _hasDuplicate = false;
            foreach (Entry entry in _entries)
            {
                bool duplicate = counts[entry.DerivedKey] > 1;
                _hasDuplicate |= duplicate;
                entry.RefreshHeader(duplicate);
            }

            _emptyLabel.style.display = _entries.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private string DeriveKey(FieldEditorModel model) =>
            _keyMemberName is null ? string.Empty : KeyString(model.GetMemberToken(_keyMemberName));

        private string DeriveKey(JObject entityJson) =>
            _keyMemberName is null ? string.Empty : KeyString(entityJson[_keyMemberName]);

        private static string KeyString(JToken? token)
        {
            if (token is JValue value && value.Value is not null)
                return Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            return string.Empty;
        }

        // Seeds a new entry's key member with a value not already used, so consecutive adds do not
        // pile up under one duplicate key. String keys count up from "newEntry"; numeric keys take
        // max + 1. Other key types keep the template default and rely on the duplicate warning.
        private void AssignUniqueKey(JObject entityJson)
        {
            if (_keyMemberName is null)
                return;

            HashSet<string> existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (Entry entry in _entries)
                existing.Add(entry.DerivedKey);

            if (_keyIsNumeric)
            {
                long max = -1;
                foreach (string key in existing)
                {
                    if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                        max = Math.Max(max, parsed);
                }

                entityJson[_keyMemberName] = new JValue(max + 1);
                return;
            }

            if (_keyType == typeof(string))
            {
                string candidate = DefaultNewStringKey;
                int suffix = 2;
                while (existing.Contains(candidate))
                    candidate = DefaultNewStringKey + suffix++;
                entityJson[_keyMemberName] = new JValue(candidate);
            }
        }

        private static bool IsNumericKey(Type keyType)
        {
            switch (Type.GetTypeCode(keyType))
            {
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                    return true;
                default:
                    return false;
            }
        }

        // Resolves the JSON property name of the entity's [PlayerDataKey] member, matching the
        // contract MemoryPackJsonConverter/FieldEditorModel use. Null when it cannot be determined,
        // which disables key derivation and add (the entries then keep their original keys).
        private static string? ResolveKeyMemberJsonName(Type entityType)
        {
            const BindingFlags declaredInstance =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            string? memberName = null;
            foreach (PropertyInfo property in entityType.GetProperties(declaredInstance))
            {
                if (property.IsDefined(typeof(PlayerDataKeyAttribute), inherit: false))
                {
                    memberName = property.Name;
                    break;
                }
            }

            if (memberName is null)
            {
                foreach (FieldInfo field in entityType.GetFields(declaredInstance))
                {
                    if (field.IsDefined(typeof(PlayerDataKeyAttribute), inherit: false))
                    {
                        memberName = field.Name;
                        break;
                    }
                }
            }

            if (memberName is null)
                return null;

            try
            {
                JsonObjectContract contract = MemoryPackJsonConverter.ResolveObjectContract(entityType);
                foreach (JsonProperty property in contract.Properties)
                {
                    if (string.Equals(property.UnderlyingName, memberName, StringComparison.Ordinal))
                        return property.PropertyName ?? memberName;
                }
            }
            catch (Exception)
            {
                // Fall through to the raw member name; the contract mirrors member names anyway.
            }

            return memberName;
        }

        private static string? TryBuildDefaultEntityJson(Type entityType)
        {
            return ViewerDisplayNames.TryCreateDefaultJson(entityType, out string json, out _) ? json : null;
        }

        private void RaiseChanged() => Changed?.Invoke();

        private sealed class Entry
        {
            private readonly Label _header;
            private readonly Label _duplicateWarning;

            public Entry(
                string originalKey,
                FieldEditorModel? model,
                FieldEditorView? view,
                JToken rawValue,
                VisualElement section,
                Label header,
                Label duplicateWarning)
            {
                OriginalKey = originalKey;
                DerivedKey = originalKey;
                Model = model;
                View = view;
                RawValue = rawValue;
                Section = section;
                _header = header;
                _duplicateWarning = duplicateWarning;
            }

            /// <summary>Dict key this entry loaded under; the fallback key when none can be derived.</summary>
            public string OriginalKey { get; }

            /// <summary>Current key: the entity's [PlayerDataKey] member value, recomputed on edit.</summary>
            public string DerivedKey { get; set; }

            /// <summary>Non-null for object entries backed by a field form; null for passthrough entries.</summary>
            public FieldEditorModel? Model { get; }

            public FieldEditorView? View { get; }

            /// <summary>Original token, re-emitted verbatim for passthrough entries.</summary>
            public JToken RawValue { get; }

            public VisualElement Section { get; }

            public void RefreshHeader(bool duplicate)
            {
                _header.text = string.IsNullOrEmpty(DerivedKey) ? "(empty key)" : DerivedKey;
                _duplicateWarning.style.display = duplicate ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }
    }
}
