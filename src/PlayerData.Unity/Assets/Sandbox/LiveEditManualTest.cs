using System;
using System.Collections.Generic;
using MemoryPack;
using PlayerData;
using PlayerData.Unity;
using UnityEngine;

namespace PlayerData.Unity.Sandbox
{
    public enum ManualRank
    {
        Novice = 0,
        Veteran = 1,
        Legend = 2,
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class ManualProfile
    {
        [MemoryPackOrder(0)]
        public string Name { get; set; } = "Hero";

        [MemoryPackOrder(1)]
        public int Level { get; set; } = 1;

        [MemoryPackOrder(2)]
        public float Speed { get; set; } = 3.5f;

        [MemoryPackOrder(3)]
        public bool Invincible { get; set; }

        [MemoryPackOrder(4)]
        public ManualRank Rank { get; set; } = ManualRank.Novice;

        // updater は CAS 下で再実行されうるため、インプレース変更ではなくコピーを返す。
        public ManualProfile WithLevel(int level) => new ManualProfile
        {
            Name = Name,
            Level = level,
            Speed = Speed,
            Invincible = Invincible,
            Rank = Rank,
        };
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class ManualItem
    {
        [PlayerDataKey]
        [MemoryPackOrder(0)]
        public string ItemId { get; set; } = string.Empty;

        [MemoryPackOrder(1)]
        public int Count { get; set; }
    }

    [PlayerDataSession]
    [PlayerDataSingle(typeof(ManualProfile))]
    [PlayerDataCollection(typeof(ManualItem), "Items")]
    public sealed partial class ManualLiveEditSession
    {
    }

    // 手動チェック用サンドボックス。シーンも GameObject も用意せず Play を押すだけで動く
    // (このリポジトリには再生対象のシーンが 1 つも無いため自動生成する)。
    // 再生したら Window > PlayerData > Data Viewer の Source で「ManualLiveEdit」を選ぶ。
    public sealed class LiveEditManualTest : MonoBehaviour
    {
        ManualLiveEditSession _session;
        IDisposable _viewerToken;
        bool _autoMutate;
        float _nextAutoMutateAt;
        int _itemSerial;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void SpawnOnPlay()
        {
            var go = new GameObject(nameof(LiveEditManualTest));
            go.AddComponent<LiveEditManualTest>();
        }

        async void Start()
        {
            _session = await ManualLiveEditSession.OpenAsync(new UnitySaveBackend("ManualLiveEdit"));

            _session.ManualProfile.Changed += OnProfileChanged;
            _session.Items.Changed += OnItemsChanged;

            _viewerToken = LiveSessionRegistry.Register("ManualLiveEdit", _session);
            Debug.Log("[LiveEditManualTest] session opened and registered");
        }

        void OnProfileChanged(DocChange<ManualProfile> change)
        {
            Debug.Log($"[LiveEditManualTest] Profile Changed cause={change.Cause} Level={change.Current.Level} Name={change.Current.Name}");
        }

        void OnItemsChanged(BagChange<string, ManualItem> change)
        {
            Debug.Log($"[LiveEditManualTest] Items Changed cause={change.Cause} kind={change.Kind} key={change.Key}");
        }

        void Update()
        {
            if (_autoMutate && Time.time >= _nextAutoMutateAt)
            {
                _nextAutoMutateAt = Time.time + 2f;
                _session?.ManualProfile.Update(p => p.WithLevel(p.Level + 1));
            }
        }

        void OnGUI()
        {
            if (_session == null)
            {
                GUILayout.Label("opening session...");
                return;
            }

            var profile = _session.ManualProfile.Value;
            GUILayout.BeginArea(new Rect(10, 10, 340, 240), GUI.skin.box);
            GUILayout.Label($"Name: {profile.Name}  Level: {profile.Level}  Speed: {profile.Speed}");
            GUILayout.Label($"Invincible: {profile.Invincible}  Rank: {profile.Rank}");
            GUILayout.Label($"Items: {_session.Items.Snapshot.Count}  IsDirty: {_session.IsDirty}");

            if (GUILayout.Button("Level +1 (game-side write)"))
            {
                _session.ManualProfile.Update(p => p.WithLevel(p.Level + 1));
            }

            if (GUILayout.Button("Add Item (game-side write)"))
            {
                _itemSerial++;
                _session.Items.Upsert(new ManualItem { ItemId = $"item-{_itemSerial}", Count = 1 });
            }

            _autoMutate = GUILayout.Toggle(_autoMutate, " Auto Level+1 every 2s");

            if (GUILayout.Button("CommitAsync"))
            {
                _ = _session.CommitAsync();
            }

            GUILayout.EndArea();
        }

        async void OnDestroy()
        {
            _viewerToken?.Dispose();
            if (_session != null)
            {
                _session.ManualProfile.Changed -= OnProfileChanged;
                _session.Items.Changed -= OnItemsChanged;
                await _session.DisposeAsync();
            }
        }
    }
}
