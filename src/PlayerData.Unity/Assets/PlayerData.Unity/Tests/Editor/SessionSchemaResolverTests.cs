using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class SessionSchemaResolverTests
    {
        [Test]
        public void FindSessionTypes_DiscoversSessionAttributedTypesOnly()
        {
            var types = SessionSchemaResolver.FindSessionTypes();

            Assert.That(types, Has.Member(typeof(SampleEditorSession)));
            Assert.That(types, Has.No.Member(typeof(SessionWithDuplicateStorageKey)));
        }

        [Test]
        public void Resolve_ValidSession_ResolvesStorageKeysLikeTheGenerator()
        {
            SessionSchema schema = SessionSchemaResolver.Resolve(typeof(SampleEditorSession));

            Assert.That(schema.Diagnostics, Is.Empty);
            // Same rule as the source generator: Key ?? propertyName ?? DocumentType.Name.
            Assert.That(
                schema.Documents.Select(d => d.StorageKey),
                Is.EqualTo(new[] { "SampleProfile", "Stats", "items-v1" }));
        }

        [Test]
        public void Resolve_ValidSession_ExposesPropertyNamesForFriendlyLabels()
        {
            SessionSchema schema = SessionSchemaResolver.Resolve(typeof(SampleEditorSession));

            Assert.That(
                schema.Documents.Select(d => d.PropertyName),
                Is.EqualTo(new[] { "SampleProfile", "Stats", "Items" }));
            Assert.That(
                schema.Documents.Single(d => d.StorageKey == "items-v1").PropertyName,
                Is.EqualTo("Items"));
        }

        [Test]
        public void Resolve_SingleDocument_PayloadTypeIsDocumentType()
        {
            SessionSchema schema = SessionSchemaResolver.Resolve(typeof(SampleEditorSession));

            DocumentDescriptor document = schema.Documents.Single(d => d.StorageKey == "Stats");
            Assert.That(document.IsCollection, Is.False);
            Assert.That(document.DocumentType, Is.EqualTo(typeof(SampleStats)));
            Assert.That(document.PayloadType, Is.EqualTo(typeof(SampleStats)));
            Assert.That(document.KeyType, Is.Null);
        }

        [Test]
        public void Resolve_CollectionDocument_PayloadTypeIsConcurrentDictionary()
        {
            SessionSchema schema = SessionSchemaResolver.Resolve(typeof(SampleEditorSession));

            DocumentDescriptor document = schema.Documents.Single(d => d.StorageKey == "items-v1");
            Assert.That(document.IsCollection, Is.True);
            Assert.That(document.DocumentType, Is.EqualTo(typeof(SampleItem)));
            Assert.That(document.KeyType, Is.EqualTo(typeof(string)));
            Assert.That(document.PayloadType, Is.EqualTo(typeof(ConcurrentDictionary<string, SampleItem>)));
        }

        [Test]
        public void Resolve_CollectionWithoutKeyMember_SkipsDocumentWithDiagnostic()
        {
            SessionSchema schema = SessionSchemaResolver.Resolve(typeof(SessionWithKeylessCollection));

            Assert.That(schema.Documents, Is.Empty);
            Assert.That(schema.Diagnostics, Has.Count.EqualTo(1));
            Assert.That(schema.Diagnostics[0], Does.Contain("SampleKeyless").And.Contain("0"));
        }

        [Test]
        public void Resolve_CollectionWithTwoKeyMembers_SkipsDocumentWithDiagnostic()
        {
            SessionSchema schema = SessionSchemaResolver.Resolve(typeof(SessionWithDoubleKeyedCollection));

            Assert.That(schema.Documents, Is.Empty);
            Assert.That(schema.Diagnostics, Has.Count.EqualTo(1));
            Assert.That(schema.Diagnostics[0], Does.Contain("SampleDoubleKeyed").And.Contain("2"));
        }

        [Test]
        public void Resolve_DuplicateStorageKey_KeepsFirstAndSkipsSecond()
        {
            SessionSchema schema = SessionSchemaResolver.Resolve(typeof(SessionWithDuplicateStorageKey));

            Assert.That(schema.Documents, Has.Count.EqualTo(1));
            Assert.That(schema.Documents[0].StorageKey, Is.EqualTo("dup"));
            Assert.That(schema.Documents[0].DocumentType, Is.EqualTo(typeof(SampleProfile)));
            Assert.That(schema.Diagnostics, Has.Count.EqualTo(1));
            Assert.That(schema.Diagnostics[0], Does.Contain("dup"));
        }

        [Test]
        public void Resolve_DocumentWithoutVersionTolerantMemoryPackable_SkipsWithDiagnostic()
        {
            SessionSchema schema = SessionSchemaResolver.Resolve(typeof(SessionWithNonMemoryPackableDocument));

            Assert.That(schema.Documents, Is.Empty);
            Assert.That(schema.Diagnostics, Has.Count.EqualTo(1));
            Assert.That(schema.Diagnostics[0], Does.Contain("SamplePlainDoc"));
        }

        [Test]
        public void Resolve_ReservedPropertyNames_SkipsDocumentsWithDiagnostics()
        {
            SessionSchema schema = SessionSchemaResolver.Resolve(typeof(SessionWithReservedNames));

            Assert.That(schema.Documents, Is.Empty);
            Assert.That(schema.Diagnostics, Has.Count.EqualTo(2));
            // "Session" collides with the generator-emitted "_session" backing field.
            Assert.That(schema.Diagnostics[0], Does.Contain("Session"));
            Assert.That(schema.Diagnostics[1], Does.Contain("IsDirty"));
        }

        [Test]
        public void SourceGenerator_RunsInUnity_GeneratesSessionMembers()
        {
            PropertyInfo statsProperty = typeof(SampleEditorSession).GetProperty("Stats");
            MethodInfo openAsync = typeof(SampleEditorSession).GetMethod(
                "OpenAsync", BindingFlags.Public | BindingFlags.Static);

            Assert.That(statsProperty, Is.Not.Null, "generated IDoc property missing");
            Assert.That(typeof(IDoc<SampleStats>).IsAssignableFrom(statsProperty.PropertyType), Is.True);
            Assert.That(openAsync, Is.Not.Null, "generated OpenAsync missing");
        }
    }
}
