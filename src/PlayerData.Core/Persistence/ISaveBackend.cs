using System.Threading;
using System.Threading.Tasks;

namespace PlayerData.Persistence;

/// <summary><see cref="SaveBundle"/> を保存したり復元したりします。</summary>
/// <remarks>
/// 所有権の約束ごとです：
/// <list type="bullet">
/// <item><description><see cref="ReadAsync"/>：返したバンドルの所有権（その Documents
/// ディクショナリと、中のすべての byte[] の値を含みます）は呼び出し側へ移ります。実装側は
/// 返したあとにそれらへの参照を持ち続けたり再利用したりしないでください。呼び出し側は
/// 中身をその場で書き換えても大丈夫です。</description></item>
/// <item><description><see cref="WriteAsync"/>：バンドルの引数（その Documents ディクショナリと
/// byte[] の値を含みます）は読み取り専用で、呼び出しの間だけ有効です。呼び出しが終われば
/// 呼び出し側はそれらのオブジェクトを再利用してかまいませんので、データを手元に残したい
/// 実装はコピーを取るようにしてください。</description></item>
/// </list>
/// </remarks>
public interface ISaveBackend
{
    /// <summary>保存されているバンドルを読みます。セーブがないときは null です。返したバンドルの
    /// 所有権は呼び出し側へ移ります（このインターフェースの所有権の約束ごとをご覧ください）。</summary>
    ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>バンドルを保存します。バンドルは呼び出しの間だけ有効です
    /// （このインターフェースの所有権の約束ごとをご覧ください）。</summary>
    ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default);
}
