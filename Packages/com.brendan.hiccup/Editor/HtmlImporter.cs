using System;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Hiccup.Editor
{
    /// <summary>
    /// Imports .html files as TextAssets with the HTML document icon. Unity's own text importer already owns the
    /// extension, so this one is registered as an override and <see cref="HtmlImporterSelector"/> switches every
    /// .html asset over to it as it is imported.
    /// </summary>
    [ScriptedImporter(1, new string[0], new[] { "html", "htm" })]
    public class HtmlImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx) => HtmlAssetImport.ImportText(ctx, HtmlAssetImport.HtmlIcon);
    }

    /// <summary>Routes .html assets to <see cref="HtmlImporter"/> (the result is still a TextAsset, only the icon differs).</summary>
    internal sealed class HtmlImporterSelector : AssetPostprocessor
    {
        private void OnPreprocessAsset()
        {
            if (assetImporter is HtmlImporter)
                return;
            if (!assetPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                !assetPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                return;
            // WebGL template pages are whole documents read by the build pipeline, not UI fragments.
            if (assetPath.IndexOf("/WebGLTemplates/", StringComparison.OrdinalIgnoreCase) >= 0)
                return;
            AssetDatabase.SetImporterOverride<HtmlImporter>(assetPath);
        }
    }

    /// <summary>Shared body of the two text importers: a TextAsset whose Project window icon is one of the package's PNGs.</summary>
    internal static class HtmlAssetImport
    {
        public const string HtmlIcon = "Packages/com.brendan.hiccup/Editor/Icons/HtmlAsset.png";
        public const string CssIcon = "Packages/com.brendan.hiccup/Editor/Icons/CssAsset.png";

        public static void ImportText(AssetImportContext ctx, string iconPath)
        {
            var text = new TextAsset(File.ReadAllText(ctx.assetPath));
            // Import the icon first, and re-import this asset if the icon changes.
            ctx.DependsOnArtifact(iconPath);
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
            ctx.AddObjectToAsset("main", text, icon);
            ctx.SetMainObject(text);
        }
    }
}
