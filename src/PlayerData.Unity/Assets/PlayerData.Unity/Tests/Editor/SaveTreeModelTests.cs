using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MemoryPack;
using NUnit.Framework;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class SaveTreeModelTests
    {
        private static readonly IReadOnlyList<LoadedSave> NoSaves = Array.Empty<LoadedSave>();
        private static readonly IReadOnlyList<SaveTreeLiveSession> NoLiveSessions = Array.Empty<SaveTreeLiveSession>();

        private string _root;
        private SessionSchema _schema;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayerDataEditorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _schema = SessionSchemaResolver.Resolve(typeof(SampleEditorSession));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private static Dictionary<string, byte[]> DefaultDocuments()
        {
            ConcurrentDictionary<string, SampleItem> items = new ConcurrentDictionary<string, SampleItem>();
            items["potion"] = new SampleItem { ItemId = "potion", Count = 3 };

            return new Dictionary<string, byte[]>
            {
                ["SampleProfile"] = MemoryPackSerializer.Serialize(new SampleProfile { Name = "hero", Level = 5 }),
                ["items-v1"] = MemoryPackSerializer.Serialize(items),
                ["mystery"] = new byte[] { 1, 2, 3, 4 },
                ["Stats"] = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            };
        }

        private static void WriteSave(string directory, Dictionary<string, byte[]> documents)
        {
            new DirectorySaveBackend(directory)
                .WriteAsync(new SaveBundle(SaveSession.CurrentFormatVersion, documents))
                .AsTask().GetAwaiter().GetResult();
        }

        private List<LoadedSave> ScanAndLoadAll()
        {
            List<LoadedSave> saves = new List<LoadedSave>();
            foreach (SaveLocation location in SaveDataStore.FindSaves(_root))
            {
                LoadedSave? save = SaveDataStore.TryLoad(location, _schema, out string? error);
                Assert.That(save, Is.Not.Null, error);
                saves.Add(save);
            }

            return saves;
        }

        private static List<SaveTreeNode> Flatten(IReadOnlyList<SaveTreeNode> roots)
        {
            List<SaveTreeNode> all = new List<SaveTreeNode>();
            void Visit(SaveTreeNode node)
            {
                all.Add(node);
                foreach (SaveTreeNode child in node.Children)
                    Visit(child);
            }

            foreach (SaveTreeNode root in roots)
                Visit(root);
            return all;
        }

        private static List<SaveTreeLiveSession> SampleLiveSessions() => new List<SaveTreeLiveSession>
        {
            new SaveTreeLiveSession("Main", new List<LiveDocumentDescriptor>
            {
                new LiveDocumentDescriptor("Profile", isCollection: false, typeof(SampleProfile), keyType: null),
                new LiveDocumentDescriptor("Items", isCollection: true, typeof(SampleItem), typeof(string)),
            }),
        };

        [Test]
        public void Build_EmptyRoot_SavedFilesGroupPresentWithNoChildren()
        {
            IReadOnlyList<SaveTreeNode> groups = SaveTreeModel.Build(_root, NoSaves, NoLiveSessions);

            Assert.That(groups, Has.Count.EqualTo(1));
            Assert.That(groups[0].Kind, Is.EqualTo(SaveTreeNodeKind.Group));
            Assert.That(groups[0].DisplayName, Is.EqualTo(ViewerDisplayNames.SavedFilesLabel));
            Assert.That(groups[0].Children, Is.Empty);
            Assert.That(groups[0].IsSelectableForDetail, Is.False);
        }

        [Test]
        public void Build_MultipleSavesIncludingSlot_ListsRelativePathsUnderSavedFiles()
        {
            WriteSave(Path.Combine(_root, "saveA"), DefaultDocuments());
            new SlotSaveBackend(Path.Combine(_root, "saveB"), slot: 2)
                .WriteAsync(new SaveBundle(SaveSession.CurrentFormatVersion, DefaultDocuments()))
                .AsTask().GetAwaiter().GetResult();

            IReadOnlyList<SaveTreeNode> groups = SaveTreeModel.Build(_root, ScanAndLoadAll(), NoLiveSessions);

            SaveTreeNode savedFiles = groups[0];
            Assert.That(savedFiles.Children, Has.Count.EqualTo(2));
            Assert.That(savedFiles.Children.Select(n => n.DisplayName), Is.EqualTo(new[] { "saveA", "saveB/slot_2" }));
            foreach (SaveTreeNode node in savedFiles.Children)
            {
                Assert.That(node.Kind, Is.EqualTo(SaveTreeNodeKind.Save));
                Assert.That(node.Location, Is.Not.Null);
                Assert.That(node.IsSelectableForDetail, Is.False);
            }
        }

        [Test]
        public void Build_RootItselfIsASave_UsesRootLabel()
        {
            WriteSave(_root, DefaultDocuments());

            IReadOnlyList<SaveTreeNode> groups = SaveTreeModel.Build(_root, ScanAndLoadAll(), NoLiveSessions);

            Assert.That(groups[0].Children, Has.Count.EqualTo(1));
            Assert.That(groups[0].Children[0].DisplayName, Is.EqualTo(SaveTreeModel.RootSaveLabel));
        }

        [Test]
        public void Build_DiskDocuments_LeafPerDocumentWithNameStateAndPayload()
        {
            WriteSave(Path.Combine(_root, "save"), DefaultDocuments());
            List<LoadedSave> saves = ScanAndLoadAll();

            IReadOnlyList<SaveTreeNode> groups = SaveTreeModel.Build(_root, saves, NoLiveSessions);

            SaveTreeNode saveNode = groups[0].Children[0];
            Assert.That(saveNode.Children, Has.Count.EqualTo(4));
            Dictionary<string, SaveTreeNode> byKey = saveNode.Children.ToDictionary(n => n.StorageKey);
            foreach (SaveTreeNode leaf in saveNode.Children)
            {
                Assert.That(leaf.Kind, Is.EqualTo(SaveTreeNodeKind.Document));
                Assert.That(leaf.Children, Is.Empty);
                Assert.That(leaf.Location, Is.SameAs(saves[0].Location));
                Assert.That(leaf.IsSelectableForDetail, Is.True);
            }

            Assert.That(byKey["SampleProfile"].DisplayName, Does.StartWith("SampleProfile"));
            Assert.That(byKey["SampleProfile"].DisplayName, Does.Contain(ViewerDisplayNames.StateLabel(DocumentState.Editable)));
            Assert.That(byKey["items-v1"].DisplayName, Does.StartWith("Items"), "property name preferred over storage key");
            Assert.That(byKey["mystery"].DisplayName, Does.StartWith("mystery"), "unknown key falls back to storage key");
            Assert.That(byKey["mystery"].DisplayName, Does.Contain(ViewerDisplayNames.StateLabel(DocumentState.UnknownKey)));
            Assert.That(byKey["Stats"].DisplayName, Does.Contain(ViewerDisplayNames.StateLabel(DocumentState.Unreadable)));
        }

        [Test]
        public void Build_NoLiveSessions_OmitsPlayingNowGroup()
        {
            WriteSave(Path.Combine(_root, "save"), DefaultDocuments());

            IReadOnlyList<SaveTreeNode> groups = SaveTreeModel.Build(_root, ScanAndLoadAll(), NoLiveSessions);

            Assert.That(groups, Has.Count.EqualTo(1));
            Assert.That(groups[0].DisplayName, Is.EqualTo(ViewerDisplayNames.SavedFilesLabel));
        }

        [Test]
        public void Build_LiveSessions_AddsPlayingNowGroupWithDocumentLeaves()
        {
            IReadOnlyList<SaveTreeNode> groups = SaveTreeModel.Build(_root, NoSaves, SampleLiveSessions());

            Assert.That(groups, Has.Count.EqualTo(2));
            SaveTreeNode playingNow = groups[1];
            Assert.That(playingNow.Kind, Is.EqualTo(SaveTreeNodeKind.Group));
            Assert.That(playingNow.DisplayName, Is.EqualTo(SaveTreeModel.PlayingNowLabel));

            SaveTreeNode session = playingNow.Children.Single();
            Assert.That(session.Kind, Is.EqualTo(SaveTreeNodeKind.LiveSession));
            Assert.That(session.DisplayName, Is.EqualTo("Main"));
            Assert.That(session.SessionName, Is.EqualTo("Main"));
            Assert.That(session.IsSelectableForDetail, Is.False);

            Assert.That(session.Children.Select(n => n.DisplayName), Is.EqualTo(new[] { "Profile", "Items" }));
            foreach (SaveTreeNode leaf in session.Children)
            {
                Assert.That(leaf.Kind, Is.EqualTo(SaveTreeNodeKind.LiveDocument));
                Assert.That(leaf.SessionName, Is.EqualTo("Main"));
                Assert.That(leaf.PropertyName, Is.EqualTo(leaf.DisplayName));
                Assert.That(leaf.IsSelectableForDetail, Is.True);
            }
        }

        [Test]
        public void Build_FullTree_IdsAreUnique()
        {
            WriteSave(Path.Combine(_root, "saveA"), DefaultDocuments());
            WriteSave(Path.Combine(_root, "saveB"), DefaultDocuments());

            IReadOnlyList<SaveTreeNode> groups = SaveTreeModel.Build(_root, ScanAndLoadAll(), SampleLiveSessions());

            List<SaveTreeNode> all = Flatten(groups);
            Assert.That(all.Select(n => n.Id).Distinct().Count(), Is.EqualTo(all.Count));
        }
    }
}
