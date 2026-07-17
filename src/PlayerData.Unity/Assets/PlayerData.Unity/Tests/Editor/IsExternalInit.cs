// Polyfill required for init-only setters / records under Unity's C# 9 compiler
// (netstandard2.1 does not define this type).
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
