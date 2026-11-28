using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PlayerData.Persistence;
using PlayerData.Sessions;

namespace PlayerData.Benchmarks;

// SaveSession のベンチマークからディスク入出力を切り離し、測る時間や確保量が SaveSession 自身の
// コミットや読み込みの処理だけを映すようにします。そのうえで、ISaveBackend の所有権の決まりは守ります。
// WriteAsync は保持するディクショナリをコピーします（呼び出し側は、呼び出しの直後にバンドルの
// ディクショナリを再利用するためです）。ReadAsync は深いコピーを渡します（返されたバンドルの所有権は
// 呼び出し側へ移り、配列をその場で書き換えてもよいことになっています）。
internal sealed class InMemorySaveBackend : ISaveBackend
{
    private SaveBundle? _bundle;

    public ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_bundle is null) return new((SaveBundle?)null);

        // byte[] の中身もコピーしています。呼び出し側（難読化するラッパーなど）が返された配列を
        // その場で変換することがあり、保持しているコピーを壊してはいけないためです。
        var documents = new Dictionary<string, byte[]>(_bundle.Documents.Count);
        foreach (var pair in _bundle.Documents)
        {
            var copy = new byte[pair.Value.Length];
            Buffer.BlockCopy(pair.Value, 0, copy, 0, pair.Value.Length);
            documents[pair.Key] = copy;
        }
        return new ValueTask<SaveBundle?>(new SaveBundle(_bundle.FormatVersion, documents));
    }

    public ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default)
    {
        // コピーが必要なのはディクショナリだけです。分かっている書き込み側（SaveSession やラッパーの
        // バックエンド）は、呼び出しの合間にディクショナリを再利用しますが、渡した byte[] の中身を
        // 書き換えることはありません。そのため配列を共有しても、書き込みのベンチマークにノイズは入りません。
        var documents = new Dictionary<string, byte[]>(bundle.Documents.Count);
        foreach (var pair in bundle.Documents)
            documents[pair.Key] = pair.Value;
        _bundle = new SaveBundle(bundle.FormatVersion, documents);
        return default;
    }
}
