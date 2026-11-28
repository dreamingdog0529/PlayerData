using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using MemoryPack;
using PlayerData.Persistence;
using PlayerData.Sessions;

namespace PlayerData.Unity.Editor.Tests
{
    /// <summary>
    /// persistentDataPath の下に、使い捨てのサンプルセーブツリーを作ります。ビューアウィンドウを手で試すためのもので、
    /// 直接のセーブと slot_1 のセーブが入っていて、それぞれに編集できるドキュメントと、
    /// 不明なキー、そして既知のキーの下にある壊れた（読めない）ペイロードが含まれます。
    /// </summary>
    public static class SampleSaveMenu
    {
        public const string SampleFolderName = "PlayerData-EditorSample";

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
