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
    /// ビューワーのセーブツリーのノード 1 件です。純粋なデータで、UIToolkit の型は入っていません。なので EditMode テストでは、
    /// ウィンドウなしで構造全体を確認できます。
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

        /// <summary>1 回の Build の結果の中では一意です。入力が同じなら、ビルドをまたいでも安定しています。</summary>
        public int Id { get; }

        public string DisplayName { get; }

        public SaveTreeNodeKind Kind { get; }

        public IReadOnlyList<SaveTreeNode> Children { get; }

        /// <summary>Save ノードと Document ノードに設定されます。</summary>
        public SaveLocation? Location { get; }

        /// <summary>Document ノードに設定されます。</summary>
        public string? StorageKey { get; }

        /// <summary>LiveSession ノードと LiveDocument ノードに設定されます。</summary>
        public string? SessionName { get; }

        /// <summary>LiveDocument ノードに設定されます。</summary>
        public string? PropertyName { get; }

        /// <summary>ディスク上のドキュメントの状態です。Document ノードにだけ設定され、ツリーのステータスドットを決めます。</summary>
        public DocumentState? State { get; }

        /// <summary>詳細ペインを開くのはドキュメントのリーフだけです。グループ、セーブ、セッションは展開するだけです。</summary>
        public bool IsSelectableForDetail =>
            Kind is SaveTreeNodeKind.Document or SaveTreeNodeKind.LiveDocument;
    }

    /// <summary>ツリー向けのライブセッションの入力です。レジストリ名と、そこで見つけたドキュメントを合わせたものです。</summary>
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
    /// ディスクのスキャン結果とライブセッションを、ビューワーのツリーに対応付けます。ルートのグループ → セーブ /
    /// セッション → ドキュメントのリーフ、という形です。純粋で同期的なので、テストでは UI なしで動かせます。
    /// </summary>
    public static class SaveTreeModel
    {
        public const string PlayingNowLabel = "Playing now";

        /// <summary>ディレクトリがスキャンしたルートそのものであるセーブ向けのラベルです。</summary>
        public const string RootSaveLabel = "(root)";

        /// <summary>
        /// 「Saved files」グループはいつも最初にあり、いつも存在します（空のルートでは子がゼロになります）。
        /// 「Playing now」グループは、ライブセッションがあるときにだけ存在します。
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
                    // 名前だけにします。状態はラベルのテキストではなく、行のステータスドットから読むようになったためです。
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

            // スキャンしたルートの外にある場所は、呼び出し側のバグです。フルパスを表示することで、相対パスを
            // でっち上げるのではなく、不一致が見えるようにします。
            return directory;
        }

        private static string Normalize(string path) =>
            path.Replace('\\', '/').TrimEnd('/');
    }
}
