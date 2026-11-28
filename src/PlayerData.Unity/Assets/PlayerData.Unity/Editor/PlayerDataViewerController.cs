using System;
using System.Collections.Generic;
using System.Reflection;

namespace PlayerData.Unity.Editor
{
    /// <summary>ドキュメント 1 件のビューの状態です。表示用に準備してあります。</summary>
    public sealed class DocumentView
    {
        public DocumentView(DocumentEntry entry, string? json, string? jsonError)
        {
            Entry = entry;
            Json = json;
            JsonError = jsonError;
        }

        public DocumentEntry Entry { get; }

        /// <summary>整えて表示した JSON です。ペイロードを表示できないときは null になります。</summary>
        public string? Json { get; }

        public string? JsonError { get; }

        public bool CanEdit => Entry.State == DocumentState.Editable;
    }

    /// <summary>
    /// PlayerDataViewerWindow の裏側にある、UI に依存しない状態です。EditMode テストがウィンドウなしで、
    /// セッション → スキャン → 読み込み → 表示という一連の流れを動かせるよう、分けてあります。
    /// </summary>
    public sealed class PlayerDataViewerController
    {
        public IReadOnlyList<Type> SessionTypes { get; private set; } = Array.Empty<Type>();

        public SessionSchema? Schema { get; private set; }

        public IReadOnlyList<SaveLocation> Saves { get; private set; } = Array.Empty<SaveLocation>();

        public SaveLocation? SelectedSave { get; private set; }

        public LoadedSave? CurrentSave { get; private set; }

        public string? LoadError { get; private set; }

        public void RefreshSessionTypes()
        {
            List<Type> discovered = SessionSchemaResolver.FindSessionTypes();
            List<Type> result = new List<Type>(discovered.Count);
            foreach (Type type in discovered)
            {
                // テスト専用のセッションフィクスチャ（UNITY_INCLUDE_TESTS の下でコンパイルされます）が、そのままだと
                // このパッケージ自身の開発プロジェクトのドロップダウンを煩雑にしてしまいます。パッケージを使う側では、
                // そのテストアセンブリはコンパイルされないので目にすることはありません。つまり、これは開発中にだけ影響します。
                if (!IsFromTestAssembly(type))
                    result.Add(type);
            }

            SessionTypes = result;
        }

        // Unity の EditMode/PlayMode のテストアセンブリはどれも NUnit フレームワークを参照していて、本番の
        // アセンブリは参照していません。なので、これは「テスト用の型かどうか」の確かな目印になります。
        private static bool IsFromTestAssembly(Type type)
        {
            foreach (AssemblyName referenced in type.Assembly.GetReferencedAssemblies())
            {
                if (string.Equals(referenced.Name, "nunit.framework", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public void SelectSession(Type sessionType)
        {
            if (sessionType is null) throw new ArgumentNullException(nameof(sessionType));

            Schema = SessionSchemaResolver.Resolve(sessionType);

            // 分類はスキーマに依存するので、すでに選ばれているセーブは読み直します。
            if (SelectedSave is not null)
                LoadSelected();
        }

        public void Scan(string rootPath)
        {
            Saves = SaveDataStore.FindSaves(rootPath);
            SelectedSave = null;
            CurrentSave = null;
            LoadError = null;
        }

        /// <summary>
        /// ツリービュー向けに、現在のスキーマでスキャン済みのセーブをすべて読み込みます。マニフェストを
        /// 読めない場所は省きます。スキーマがないと、どのセーブも分類できないので、
        /// 結果は空になります。呼び出すたびに、新しいリストを返します。
        /// </summary>
        public IReadOnlyList<LoadedSave> LoadScannedSaves()
        {
            List<LoadedSave> result = new List<LoadedSave>();
            if (Schema is null)
                return result;

            foreach (SaveLocation location in Saves)
            {
                LoadedSave? save = SaveDataStore.TryLoad(location, Schema, out _);
                if (save is not null)
                    result.Add(save);
            }

            return result;
        }

        public void SelectSave(SaveLocation location)
        {
            SelectedSave = location ?? throw new ArgumentNullException(nameof(location));
            LoadSelected();
        }

        public void Reload()
        {
            if (SelectedSave is not null)
                LoadSelected();
        }

        public DocumentView? GetDocumentView(string storageKey)
        {
            if (CurrentSave is null)
                return null;

            foreach (DocumentEntry entry in CurrentSave.Documents)
            {
                if (!string.Equals(entry.StorageKey, storageKey, StringComparison.Ordinal))
                    continue;

                if (entry.Descriptor is null || entry.State == DocumentState.Unreadable)
                    return new DocumentView(entry, json: null, jsonError: null);

                try
                {
                    string json = MemoryPackJsonConverter.ToJson(entry.Bytes, entry.Descriptor.PayloadType);
                    return new DocumentView(entry, json, jsonError: null);
                }
                catch (Exception ex)
                {
                    return new DocumentView(entry, json: null, jsonError: ex.Message);
                }
            }

            return null;
        }

        /// <summary>
        /// 編集された JSON をパースし、ドキュメントを書き戻して、セーブを読み直します。JSON が無効なときや、
        /// ドキュメントが編集できないときは、ディスクには触らず、エラーとともに false を返します。
        ///
        /// </summary>
        public bool ApplyJson(string storageKey, string json, out string? error)
        {
            error = null;
            if (CurrentSave is null || Schema is null)
            {
                error = "No save is loaded.";
                return false;
            }

            DocumentEntry? target = null;
            foreach (DocumentEntry entry in CurrentSave.Documents)
            {
                if (string.Equals(entry.StorageKey, storageKey, StringComparison.Ordinal))
                {
                    target = entry;
                    break;
                }
            }

            if (target is null)
            {
                error = $"Document '{storageKey}' does not exist in this save.";
                return false;
            }

            if (target.State != DocumentState.Editable)
            {
                error = $"Document '{storageKey}' is not editable ({target.State}).";
                return false;
            }

            byte[] newBytes;
            try
            {
                newBytes = MemoryPackJsonConverter.FromJson(json, target.Descriptor!.PayloadType);
            }
            catch (Exception ex)
            {
                error = $"JSON error: {ex.Message}";
                return false;
            }

            try
            {
                CurrentSave = SaveDataStore.WriteDocument(CurrentSave, storageKey, newBytes, Schema);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            return true;
        }

        private void LoadSelected()
        {
            if (Schema is null)
            {
                CurrentSave = null;
                LoadError = "Select a session type first.";
                return;
            }

            CurrentSave = SaveDataStore.TryLoad(SelectedSave!, Schema, out string? error);
            LoadError = CurrentSave is null ? (error ?? "No save found (manifest.bin is missing).") : null;
        }
    }
}
