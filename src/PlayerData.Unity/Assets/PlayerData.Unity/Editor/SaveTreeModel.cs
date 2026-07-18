using System;
using System.Collections.Generic;

namespace PlayerData.Unity.Editor
{
    public enum SaveTreeNodeKind
    {
        Group,
        Save,
        LiveSession,
        Document,
        LiveDocument,
    }

    /// <summary>
    /// One node of the viewer's save tree. Pure data: no UIToolkit types, so EditMode tests can
    /// assert the whole structure without a window.
    /// </summary>
    public sealed class SaveTreeNode
    {
        internal SaveTreeNode(
            int id,
            string displayName,
            SaveTreeNodeKind kind,
            IReadOnlyList<SaveTreeNode> children,
            SaveLocation? location,
            string? storageKey,
            string? sessionName,
            string? propertyName,
            DocumentState? state = null)
        {
            Id = id;
            DisplayName = displayName;
            Kind = kind;
            Children = children;
            Location = location;
            StorageKey = storageKey;
            SessionName = sessionName;
            PropertyName = propertyName;
            State = state;
        }

        /// <summary>Unique within one Build result; stable across builds for identical inputs.</summary>
        public int Id { get; }

        public string DisplayName { get; }

        public SaveTreeNodeKind Kind { get; }

        public IReadOnlyList<SaveTreeNode> Children { get; }

        /// <summary>Set on Save and Document nodes.</summary>
        public SaveLocation? Location { get; }

        /// <summary>Set on Document nodes.</summary>
        public string? StorageKey { get; }

        /// <summary>Set on LiveSession and LiveDocument nodes.</summary>
        public string? SessionName { get; }

        /// <summary>Set on LiveDocument nodes.</summary>
        public string? PropertyName { get; }

        /// <summary>The on-disk document state; set on Document nodes only, drives the tree's status dot.</summary>
        public DocumentState? State { get; }

        /// <summary>Only document leaves open the detail pane; groups/saves/sessions just expand.</summary>
        public bool IsSelectableForDetail =>
            Kind is SaveTreeNodeKind.Document or SaveTreeNodeKind.LiveDocument;
    }

    /// <summary>Live session input for the tree: registry name plus its discovered documents.</summary>
    public sealed class SaveTreeLiveSession
    {
        public SaveTreeLiveSession(string name, IReadOnlyList<LiveDocumentDescriptor> documents)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Documents = documents ?? throw new ArgumentNullException(nameof(documents));
        }

        public string Name { get; }

        public IReadOnlyList<LiveDocumentDescriptor> Documents { get; }
    }

    /// <summary>
    /// Maps disk scan results and live sessions to the viewer's tree: root groups → saves /
    /// sessions → document leaves. Pure and synchronous so tests can drive it without UI.
    /// </summary>
    public static class SaveTreeModel
    {
        public const string PlayingNowLabel = "Playing now";

        /// <summary>Label for a save whose directory is the scanned root itself.</summary>
        public const string RootSaveLabel = "(root)";

        /// <summary>
        /// The "Saved files" group is always first and always present (empty root = zero
        /// children); the "Playing now" group exists only when live sessions do.
        /// </summary>
        public static IReadOnlyList<SaveTreeNode> Build(
            string rootDirectory,
            IReadOnlyList<LoadedSave> saves,
            IReadOnlyList<SaveTreeLiveSession> liveSessions)
        {
            if (rootDirectory is null) throw new ArgumentNullException(nameof(rootDirectory));
            if (saves is null) throw new ArgumentNullException(nameof(saves));
            if (liveSessions is null) throw new ArgumentNullException(nameof(liveSessions));

            int nextId = 0;
            List<SaveTreeNode> groups = new List<SaveTreeNode>();

            List<SaveTreeNode> saveNodes = new List<SaveTreeNode>(saves.Count);
            foreach (LoadedSave save in saves)
                saveNodes.Add(BuildSaveNode(rootDirectory, save, ref nextId));
            groups.Add(new SaveTreeNode(
                nextId++, ViewerDisplayNames.SavedFilesLabel, SaveTreeNodeKind.Group, saveNodes,
                location: null, storageKey: null, sessionName: null, propertyName: null));

            if (liveSessions.Count > 0)
            {
                List<SaveTreeNode> sessionNodes = new List<SaveTreeNode>(liveSessions.Count);
                foreach (SaveTreeLiveSession session in liveSessions)
                    sessionNodes.Add(BuildLiveSessionNode(session, ref nextId));
                groups.Add(new SaveTreeNode(
                    nextId++, PlayingNowLabel, SaveTreeNodeKind.Group, sessionNodes,
                    location: null, storageKey: null, sessionName: null, propertyName: null));
            }

            return groups;
        }

        private static SaveTreeNode BuildSaveNode(string rootDirectory, LoadedSave save, ref int nextId)
        {
            List<SaveTreeNode> documents = new List<SaveTreeNode>(save.Documents.Count);
            foreach (DocumentEntry entry in save.Documents)
            {
                documents.Add(new SaveTreeNode(
                    nextId++,
                    // Name only: the state now reads from the row's status dot, not the label text.
                    ViewerDisplayNames.DocumentDisplayName(
                        entry.StorageKey, entry.Descriptor?.PropertyName, entry.Descriptor?.DocumentType.Name),
                    SaveTreeNodeKind.Document,
                    Array.Empty<SaveTreeNode>(),
                    save.Location,
                    entry.StorageKey,
                    sessionName: null,
                    propertyName: null,
                    entry.State));
            }

            return new SaveTreeNode(
                nextId++, SaveDisplayName(rootDirectory, save.Location), SaveTreeNodeKind.Save, documents,
                save.Location, storageKey: null, sessionName: null, propertyName: null);
        }

        private static SaveTreeNode BuildLiveSessionNode(SaveTreeLiveSession session, ref int nextId)
        {
            List<SaveTreeNode> documents = new List<SaveTreeNode>(session.Documents.Count);
            foreach (LiveDocumentDescriptor descriptor in session.Documents)
            {
                documents.Add(new SaveTreeNode(
                    nextId++,
                    descriptor.PropertyName,
                    SaveTreeNodeKind.LiveDocument,
                    Array.Empty<SaveTreeNode>(),
                    location: null,
                    storageKey: null,
                    session.Name,
                    descriptor.PropertyName));
            }

            return new SaveTreeNode(
                nextId++, session.Name, SaveTreeNodeKind.LiveSession, documents,
                location: null, storageKey: null, session.Name, propertyName: null);
        }

        private static string SaveDisplayName(string rootDirectory, SaveLocation location)
        {
            string root = Normalize(rootDirectory);
            string directory = Normalize(location.Directory);
            if (string.Equals(directory, root, StringComparison.Ordinal))
                return RootSaveLabel;

            string prefix = root + "/";
            if (directory.StartsWith(prefix, StringComparison.Ordinal))
                return directory.Substring(prefix.Length);

            // A location outside the scanned root is a caller bug; showing the full path makes
            // the mismatch visible instead of fabricating a relative one.
            return directory;
        }

        private static string Normalize(string path) =>
            path.Replace('\\', '/').TrimEnd('/');
    }
}
