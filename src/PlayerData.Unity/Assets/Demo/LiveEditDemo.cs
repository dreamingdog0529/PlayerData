using System;
using System.Collections.Generic;
using MemoryPack;
using PlayerData;
using PlayerData.Unity;
using UnityEngine;

namespace PlayerData.Unity.Demo
{
    public enum DemoRank
    {
        Novice = 0,
        Veteran = 1,
        Legend = 2,
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class DemoProfile
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
        public DemoRank Rank { get; set; } = DemoRank.Novice;

        // updater は CAS 下で再実行されうるため、インプレース変更ではなくコピーを返す。
        public DemoProfile WithLevel(int level) => new DemoProfile
        {
            Name = Name,
            Level = level,
            Speed = Speed,
            Invincible = Invincible,
            Rank = Rank,
        };
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class DemoItem
    {
        [PlayerDataKey]
        [MemoryPackOrder(0)]
        public string ItemId { get; set; } = string.Empty;

        [MemoryPackOrder(1)]
        public int Count { get; set; }
    }

    [PlayerDataSession]
    [PlayerDataSingle(typeof(DemoProfile))]
    [PlayerDataCollection(typeof(DemoItem), "Items")]
    public sealed partial class DemoSession
    {
    }

    // ライブ編集のデモ。DemoScene を開いて Play し、
    // Window > PlayerData > Data Viewer の Source で「Demo」を選ぶ。
    public sealed class LiveEditDemo : MonoBehaviour
    {
        DemoSession _session;
        IDisposable _viewerToken;
        bool _autoMutate;
        float _nextAutoMutateAt;
        int _itemSerial;

        async void Start()
        {
            _session = await DemoSession.OpenAsync(new UnitySaveBackend("PlayerDataDemo"));

            _session.DemoProfile.Changed += OnProfileChanged;
            _session.Items.Changed += OnItemsChanged;

            _viewerToken = LiveSessionRegistry.Register("Demo", _session);
            Debug.Log("[LiveEditDemo] session opened and registered");
        }

        void OnProfileChanged(DocChange<DemoProfile> change)
        {
            Debug.Log($"[LiveEditDemo] Profile Changed cause={change.Cause} Level={change.Current.Level} Name={change.Current.Name}");
        }

        void OnItemsChanged(BagChange<string, DemoItem> change)
        {
            Debug.Log($"[LiveEditDemo] Items Changed cause={change.Cause} kind={change.Kind} key={change.Key}");
        }

        void Update()
        {
            if (_autoMutate && Time.time >= _nextAutoMutateAt)
            {
                _nextAutoMutateAt = Time.time + 2f;
                _session?.DemoProfile.Update(p => p.WithLevel(p.Level + 1));
            }
        }

        void OnGUI()
        {
            if (_session == null)
            {
                GUILayout.Label("opening session...");
                return;
            }

            var profile = _session.DemoProfile.Value;
            GUILayout.BeginArea(new Rect(10, 10, 340, 240), GUI.skin.box);
            GUILayout.Label($"Name: {profile.Name}  Level: {profile.Level}  Speed: {profile.Speed}");
            GUILayout.Label($"Invincible: {profile.Invincible}  Rank: {profile.Rank}");
            GUILayout.Label($"Items: {_session.Items.Snapshot.Count}  IsDirty: {_session.IsDirty}");

            if (GUILayout.Button("Level +1 (game-side write)"))
            {
                _session.DemoProfile.Update(p => p.WithLevel(p.Level + 1));
            }

            if (GUILayout.Button("Add Item (game-side write)"))
            {
                _itemSerial++;
                _session.Items.Upsert(new DemoItem { ItemId = $"item-{_itemSerial}", Count = 1 });
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
                _session.DemoProfile.Changed -= OnProfileChanged;
                _session.Items.Changed -= OnItemsChanged;
                await _session.DisposeAsync();
            }
        }
    }
}
