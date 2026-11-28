using PlayerData.Sessions;
namespace PlayerData.Validation;

// 文書やエンティティの型に任意で付けられる、検証の差し込み口です。SaveSession.CommitAsync は
// 書き込む前に、参加している（dirty なものも clean なものも）すべての IValidatable な値について
// Validate() を呼んでくれます。
public interface IValidatable
{
    // 失敗したときは例外を投げてください（fail-fast で、例外が握りつぶされることはありません）。
    void Validate();
}
