using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PlayerData.Unity.Editor
{
    /// <summary>
    /// NuGetForUnity は、Roslyn のソースジェネレーター（PlayerData.SourceGenerator、MemoryPack.Generator、
    /// など）を <c>**/analyzers/**</c> の下に復元します。Unity はそれらの DLL をマネージドプラグインとして扱ってしまい、
    /// <c>Microsoft.CodeAnalysis</c> の参照検証に失敗します。そのため、それらのアセンブリが Roslyn のアナライザーや
    /// ソースジェネレーターとしてだけ使われているのに、コンソールが煩くなってしまいます。
    /// <para>
    /// この fixer は、プラグインの参照検証を無効にし（アナライザー専用のインポート設定も改めて適用し）、
    /// ジェネレーターが「Unable to resolve reference」という誤ったエラーなしで動き続けられるようにします。
    /// </para>
    /// </summary>
    internal sealed class RoslynAnalyzerImportFixer : AssetPostprocessor
    {
        private const string FixedLabel = "PlayerData.AnalyzerImportFixed";
        private const string RoslynAnalyzerLabel = "RoslynAnalyzer";

        private static readonly PropertyInfo? ValidateReferencesProperty =
            typeof(PluginImporter).GetProperty(
                "ValidateReferences",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (importedAssets == null || importedAssets.Length == 0)
            {
                return;
            }

            foreach (var path in importedAssets)
            {
                TryFix(path);
            }
        }

        [InitializeOnLoadMethod]
        private static void FixExistingAnalyzersOnLoad()
        {
            // AssetDatabase の準備ができてからにし、起動時のほかのインポーターと競合しないよう、遅らせています。
            EditorApplication.delayCall += () =>
            {
                try
                {
                    foreach (var path in EnumerateAnalyzerDllAssetPaths())
                    {
                        TryFix(path);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PlayerData] Failed to fix Roslyn analyzer import settings: {ex.Message}");
                }
            };
        }

        private static System.Collections.Generic.IEnumerable<string> EnumerateAnalyzerDllAssetPaths()
        {
            if (!Directory.Exists("Assets"))
            {
                yield break;
            }

            foreach (var fullPath in Directory.EnumerateFiles("Assets", "*.dll", SearchOption.AllDirectories))
            {
                var assetPath = fullPath.Replace('\\', '/');
                if (IsAnalyzerDllPath(assetPath))
                {
                    yield return assetPath;
                }
            }
        }

        private static bool IsAnalyzerDllPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) ||
                !assetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var normalized = assetPath.Replace('\\', '/');
            return normalized.IndexOf("/analyzers/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void TryFix(string assetPath)
        {
            if (!IsAnalyzerDllPath(assetPath))
            {
                return;
            }

            if (AssetImporter.GetAtPath(assetPath) is not PluginImporter plugin)
            {
                return;
            }

            var labels = AssetDatabase.GetLabels(plugin);
            // FixedLabel が再インポートのガードです。一度設定したら、そのアセットでは二度と SaveAndReimport しません。
            if (labels.Contains(FixedLabel))
            {
                return;
            }

            plugin.SetCompatibleWithAnyPlatform(false);
            plugin.SetCompatibleWithEditor(false);
            SetValidateReferences(plugin, false);

            var newLabels = labels
                .Append(RoslynAnalyzerLabel)
                .Append(FixedLabel)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            AssetDatabase.SetLabels(plugin, newLabels);

            // ラベルと ValidateReferences を .meta に保存し、Unity の読み込み失敗の状態を消します。
            plugin.SaveAndReimport();
        }

        private static void SetValidateReferences(PluginImporter plugin, bool value)
        {
            if (ValidateReferencesProperty == null)
            {
                Debug.LogWarning(
                    "[PlayerData] PluginImporter.ValidateReferences is unavailable; " +
                    "disable Validate References manually on analyzer DLLs under Assets/Packages/**/analyzers/**.");
                return;
            }

            ValidateReferencesProperty.SetValue(plugin, value);
        }
    }
}
