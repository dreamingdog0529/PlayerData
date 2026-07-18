using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using MemoryPack;
using UnityEditor;
using UnityEngine;

namespace PlayerData.Unity.Editor.Tests
{
    /// <summary>
    /// Creates a disposable sample save tree under persistentDataPath for manually exercising the
    /// viewer window: a direct save plus a slot_1 save, each containing editable documents, an
    /// unknown key and a corrupt (unreadable) payload under a known key.
    /// </summary>
    public static class SampleSaveMenu
    {
        public const string SampleFolderName = "PlayerData-EditorSample";

        [MenuItem("PlayerData/Tests/Create Sample Save")]
        public static void CreateSampleSaveMenuItem()
        {
            string root = Create(Application.persistentDataPath);
            Debug.Log(
                $"[PlayerData] Sample saves created under: {root} (direct + slot_1). " +
                $"Open '{PlayerData.Unity.Editor.PlayerDataEditorMenu.WindowMenuPath}', select session type " +
                $"'{typeof(SampleEditorSession).FullName}', set the root path to persistentDataPath and press Scan.");
        }

        [MenuItem("PlayerData/Tests/Delete Sample Save")]
        public static void DeleteSampleSaveMenuItem()
        {
            string root = Path.Combine(Application.persistentDataPath, SampleFolderName);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
                Debug.Log($"[PlayerData] Sample saves deleted: {root}. Press Refresh in the viewer.");
            }
            else
            {
                Debug.Log($"[PlayerData] No sample saves to delete under: {root}");
            }
        }

        public static string Create(string baseDirectory)
        {
            string root = Path.Combine(baseDirectory, SampleFolderName);

            new DirectorySaveBackend(root)
                .WriteAsync(new SaveBundle(SaveSession.CurrentFormatVersion, BuildDocuments()))
                .AsTask().GetAwaiter().GetResult();

            new SlotSaveBackend(root, slot: 1)
                .WriteAsync(new SaveBundle(SaveSession.CurrentFormatVersion, BuildDocuments()))
                .AsTask().GetAwaiter().GetResult();

            return root;
        }

        private static Dictionary<string, byte[]> BuildDocuments()
        {
            ConcurrentDictionary<string, SampleItem> items = new ConcurrentDictionary<string, SampleItem>();
            items["potion"] = new SampleItem { ItemId = "potion", Count = 3 };
            items["sword"] = new SampleItem { ItemId = "sword", Count = 1 };

            return new Dictionary<string, byte[]>
            {
                ["SampleProfile"] = MemoryPackSerializer.Serialize(new SampleProfile
                {
                    Name = "hero",
                    Level = 5,
                    Spawn = new SamplePosition { X = 1.5f, Y = -2.25f },
                }),
                ["items-v1"] = MemoryPackSerializer.Serialize(items),
                ["mystery"] = new byte[] { 1, 2, 3, 4 },
                ["Stats"] = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            };
        }
    }
}
