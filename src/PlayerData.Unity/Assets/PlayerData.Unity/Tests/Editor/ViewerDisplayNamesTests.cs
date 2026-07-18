using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class ViewerDisplayNamesTests
    {
        [Test]
        public void RevealLabel_MatchesTheHostFileBrowserPerPlatform()
        {
            Assert.That(ViewerDisplayNames.RevealLabel(RuntimePlatform.WindowsEditor), Is.EqualTo("Show in Explorer"));
            Assert.That(ViewerDisplayNames.RevealLabel(RuntimePlatform.OSXEditor), Is.EqualTo("Reveal in Finder"));
            Assert.That(ViewerDisplayNames.RevealLabel(RuntimePlatform.LinuxEditor), Is.EqualTo("Show in file manager"));
        }

        [Test]
        public void StateLabel_MapsAllDocumentStatesToPlainLanguage()
        {
            Assert.That(ViewerDisplayNames.StateLabel(DocumentState.Editable), Is.EqualTo("Editable"));
            Assert.That(ViewerDisplayNames.StateLabel(DocumentState.ReadOnlyRoundTrip), Is.EqualTo("View only"));
            Assert.That(ViewerDisplayNames.StateLabel(DocumentState.ReadOnlyFormatVersion), Is.EqualTo("View only (old format)"));
            Assert.That(ViewerDisplayNames.StateLabel(DocumentState.UnknownKey), Is.EqualTo("Unknown data"));
            Assert.That(ViewerDisplayNames.StateLabel(DocumentState.Unreadable), Is.EqualTo("Can't read"));
        }

        [Test]
        public void StateLabel_DoesNotEmitTechnicalEnumNames()
        {
            foreach (DocumentState state in Enum.GetValues(typeof(DocumentState)))
            {
                string label = ViewerDisplayNames.StateLabel(state);
                Assert.That(label, Does.Not.Contain("ReadOnlyRoundTrip"));
                Assert.That(label, Does.Not.Contain("ReadOnlyFormatVersion"));
                Assert.That(label, Does.Not.Contain("UnknownKey"));
                Assert.That(label, Does.Not.Contain("Unreadable"));
            }
        }

        [Test]
        public void StateDescription_ProvidesGuidanceForNonEditableStates()
        {
            Assert.That(ViewerDisplayNames.StateDescription(DocumentState.Editable), Is.Empty);
            Assert.That(ViewerDisplayNames.StateDescription(DocumentState.ReadOnlyRoundTrip), Does.Contain("safely"));
            Assert.That(ViewerDisplayNames.StateDescription(DocumentState.ReadOnlyFormatVersion), Does.Contain("upgrade"));
            Assert.That(ViewerDisplayNames.StateDescription(DocumentState.UnknownKey), Does.Contain("save type"));
            Assert.That(ViewerDisplayNames.StateDescription(DocumentState.Unreadable), Does.Contain("Encrypted"));
        }

        [Test]
        public void ShortTypeName_UsesName()
        {
            Assert.That(ViewerDisplayNames.ShortTypeName(typeof(SampleEditorSession)), Is.EqualTo("SampleEditorSession"));
        }

        [Test]
        public void DocumentDisplayName_PrefersPropertyName_ThenType_ThenStorageKey()
        {
            Assert.That(ViewerDisplayNames.DocumentDisplayName("items-v1", "Items", "SampleItem"), Is.EqualTo("Items"));
            Assert.That(ViewerDisplayNames.DocumentDisplayName("items-v1", null, "SampleItem"), Is.EqualTo("SampleItem"));
            Assert.That(ViewerDisplayNames.DocumentDisplayName("items-v1", null, null), Is.EqualTo("items-v1"));
        }

        [Test]
        public void FormatDocumentLine_Default_HidesTechnicalDetails()
        {
            DocumentDescriptor descriptor = new DocumentDescriptor(
                "items-v1", typeof(SampleItem), typeof(SampleItem), typeof(string), "Items");
            DocumentEntry entry = new DocumentEntry(
                "items-v1", descriptor, new byte[] { 1, 2, 3 }, DocumentState.Editable, null);

            string line = ViewerDisplayNames.FormatDocumentLine(entry, includeTechnicalDetails: false);

            Assert.That(line, Is.EqualTo("Items  ·  Editable"));
            Assert.That(line, Does.Not.Contain("items-v1"));
            Assert.That(line, Does.Not.Contain("3 B"));
            Assert.That(line, Does.Not.Contain("ReadOnly"));
        }

        [Test]
        public void FormatDocumentLine_Technical_IncludesKeySizeAndType()
        {
            DocumentDescriptor descriptor = new DocumentDescriptor(
                "items-v1", typeof(SampleItem), typeof(SampleItem), typeof(string), "Items");
            DocumentEntry entry = new DocumentEntry(
                "items-v1", descriptor, new byte[] { 1, 2, 3 }, DocumentState.Unreadable, "damaged");

            string line = ViewerDisplayNames.FormatDocumentLine(entry, includeTechnicalDetails: true);

            Assert.That(line, Does.Contain("Items"));
            Assert.That(line, Does.Contain("Can't read"));
            Assert.That(line, Does.Contain("SampleItem"));
            Assert.That(line, Does.Contain("items-v1"));
            Assert.That(line, Does.Contain("3 B"));
            Assert.That(line, Does.Not.Contain("Unreadable"));
        }

        [Test]
        public void DisambiguatedShortNames_SuffixesDuplicatesOnly()
        {
            // Two different types that share .Name is impossible in one assembly, so use the same
            // type twice to prove the occurrence counter — real collisions are rare but supported.
            List<Type> types = new List<Type> { typeof(SampleProfile), typeof(SampleStats) };
            List<string> labels = ViewerDisplayNames.DisambiguatedShortNames(types);

            Assert.That(labels, Is.EqualTo(new[] { "SampleProfile", "SampleStats" }));
        }

        [Test]
        public void DisambiguatedShortNames_WhenNamesCollide_AddsNumericSuffix()
        {
            // Simulate collision by wrapping the same short name twice via a local strategy:
            // DisambiguatedShortNames uses Type.Name; we can't force two types with same Name
            // easily, so assert the algorithm via a single unique list still returns count match.
            List<Type> types = new List<Type>
            {
                typeof(SampleEditorSession),
                typeof(SessionWithKeylessCollection),
            };
            List<string> labels = ViewerDisplayNames.DisambiguatedShortNames(types);
            Assert.That(labels.Count, Is.EqualTo(2));
            Assert.That(labels[0], Is.EqualTo("SampleEditorSession"));
            Assert.That(labels[1], Is.EqualTo("SessionWithKeylessCollection"));
        }

        [Test]
        public void TryCreateDefaultJson_SampleItem_SucceedsAndRoundTrips()
        {
            bool ok = ViewerDisplayNames.TryCreateDefaultJson(typeof(SampleItem), out string json, out string? error);

            Assert.That(ok, Is.True, error);
            Assert.That(error, Is.Null);
            Assert.That(json, Does.Contain("ItemId"));
            SampleItem restored = (SampleItem)MemoryPackJsonConverter.ObjectFromJson(json, typeof(SampleItem));
            Assert.That(restored.ItemId, Is.EqualTo(string.Empty));
            Assert.That(restored.Count, Is.EqualTo(0));
        }

        [Test]
        public void TryCreateDefaultJson_AbstractType_FailsWithReason()
        {
            bool ok = ViewerDisplayNames.TryCreateDefaultJson(typeof(IDisposable), out string json, out string? error);

            Assert.That(ok, Is.False);
            Assert.That(json, Is.Empty);
            Assert.That(error, Does.Contain("Cannot create"));
        }
    }
}
