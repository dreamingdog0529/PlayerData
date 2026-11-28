// Unity の C# 9 コンパイラで init 専用セッターやレコードを使うために必要なポリフィルです
// （netstandard2.1 にはこの型が定義されていないためです）。
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
