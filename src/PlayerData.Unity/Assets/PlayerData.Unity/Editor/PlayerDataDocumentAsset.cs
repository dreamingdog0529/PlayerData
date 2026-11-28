using System;
using UnityEngine;
using PlayerData.Schema;

namespace PlayerData.Unity.Editor
{
    /// <summary>
    /// PlayerData ドキュメント 1 件分のための、エディター専用のフィクスチャです。Project アセットとして置いてあるので、
    /// プランナーや QA の方が Core のバイナリに触らなくても、Inspector でサンプルのペイロードを作れます。
    /// ランタイムのゲームコードからこの型を読み込まないでください（Editor アセンブリの中にあります）。
    /// </summary>
    [CreateAssetMenu(
        fileName = "PlayerData Document",
        menuName = "PlayerData/Document Asset",
        order = 0)]
    public sealed class PlayerDataDocumentAsset : ScriptableObject
    {
        [SerializeField]
        [Tooltip("Full name of a [PlayerDataSession] type (e.g. MyGame.GameSave).")]
        private string _sessionTypeName = string.Empty;

        [SerializeField]
        [Tooltip("Storage key of the document on that session (Key ?? property name).")]
        private string _storageKey = string.Empty;

        [SerializeField]
        [TextArea(8, 32)]
        private string _json = "{\n}";

        public string SessionTypeName
        {
            get => _sessionTypeName;
            set => _sessionTypeName = value ?? string.Empty;
        }

        public string StorageKey
        {
            get => _storageKey;
            set => _storageKey = value ?? string.Empty;
        }

        public string Json
        {
            get => _json;
            set => _json = value ?? string.Empty;
        }
    }
}
