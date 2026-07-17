using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        public const string JsonOnlyHint = "Edit via JSON tab";

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
            return text.Length <= PreviewMaxLength ? text : text.Substring(0, PreviewMaxLength) + "窶ｦ";
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
    /// Builds the UIElements rows for a <see cref="FieldEditorModel"/> and routes control edits
    /// into it. Raises <see cref="Changed"/> after every edit so the host can re-evaluate the
    /// Apply gate (<see cref="FieldEditorModel.HasInvalid"/>).
    /// </summary>
    internal sealed class FieldEditorView
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
                    label.style.minWidth = 120;
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
}
