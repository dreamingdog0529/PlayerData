using PlayerData.Sessions;
namespace PlayerData.Validation;

// セッション単位の、コミット前のチェック役です。SaveSession.AddValidator / ISaveSession.AddValidator から登録します。
public interface ISaveValidator
{
    // 失敗したときは例外を投げてください（fail-fast で、例外が握りつぶされることはありません）。
    void Validate(ISaveSession session);
}
