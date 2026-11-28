using System;
using System.Threading;
using System.Threading.Tasks;
using PlayerData.Validation;

namespace PlayerData.Sessions;

public readonly record struct LoadResult(bool Found, int FormatVersion)
{
    public static LoadResult NotFound { get; } = new(false, 0);
}

public interface ISaveSession : IAsyncDisposable
{
    bool IsDirty { get; }

    bool IsLoaded { get; }

    // Found = false のときはディスク上にセーブがないという意味で、文書はコンストラクターで用意した初期値のままになります。
    ValueTask<LoadResult> LoadAsync(CancellationToken cancellationToken = default);

    // !IsDirty のときは何もしません。バリデーターを走らせてから、dirty な文書をシリアライズしてバックエンド経由で書き込みます。
    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    // 破棄されるまでの間、ストアの Changed と DirtyChanged を抑えておき、破棄のタイミングで
    // まとめた通知を流します。スコープを開くときにアロケーションが起きないよう、戻り値は具象の
    // struct にしています（IDisposable にするとボックス化してしまうためです）。各スコープは
    // ちょうど1回だけ破棄してください。`using` を使えば自然にそうなります。
    SuppressionScope SuppressNotifications();

    // セッション単位のコミット用バリデーターです（文書の値に対する IValidatable とは別に働きます）。
    void AddValidator(ISaveValidator validator);

    void AddValidator(Action<ISaveSession> validate);

    event Action? Loaded;
    event Action? Committed;

    // IsDirty が切り替わるときに発火します（最初の dirty な書き込みのあとは true、きれいにコミットできたあとは false です）。
    event Action<bool>? DirtyChanged;
}
