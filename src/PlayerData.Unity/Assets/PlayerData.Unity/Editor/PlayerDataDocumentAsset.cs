using System;
using UnityEngine;

namespace PlayerData.Unity.Editor
{
    /// <summary>
    /// Editor-only fixture for a single PlayerData document. Stored as a Project asset so
    /// planners/QA can author sample payloads in the Inspector without touching Core binaries.
    /// Runtime game code must not load this type (it lives in the Editor assembly).
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
