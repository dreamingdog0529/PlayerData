using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PlayerData.Unity.Editor
{
    /// <summary>
    /// NuGetForUnity restores Roslyn source generators (PlayerData.SourceGenerator, MemoryPack.Generator,
    /// …) under <c>**/analyzers/**</c>. Unity still treats those DLLs as managed plugins and fails
    /// reference validation for <c>Microsoft.CodeAnalysis</c>, which spams the console even though the
    /// assemblies are only used as Roslyn analyzers / source generators.
    /// <para>
    /// This fixer disables plugin reference validation (and re-asserts analyzer-only import settings)
    /// so the generators keep working without the false "Unable to resolve reference" errors.
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
            // Defer so AssetDatabase is ready and we do not contend with other startup importers.
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
            // FixedLabel is the reimport guard: once set we never SaveAndReimport again for this asset.
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

            // Persist labels / ValidateReferences into the .meta and clear Unity's failed-load state.
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
