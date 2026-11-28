using PlayerData.Persistence;
namespace PlayerData.Migrations;

/// <summary>セーブバンドルを <see cref="FromVersion"/> から <see cref="ToVersion"/> へ移行します。</summary>
/// <remarks>所有権について：入力バンドルの所有権は移行処理側へ移りますので、文書をその場で
/// 書き換えても、同じインスタンスをそのまま返しても大丈夫です。返したバンドルの所有権は
/// 呼び出し側へ戻りますので、移行処理側でその参照を持ち続けないようにしてください。</remarks>
public interface ISaveMigration
{
    int FromVersion { get; }
    int ToVersion { get; }
    SaveBundle Migrate(SaveBundle bundle);
}
