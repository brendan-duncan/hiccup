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

    /// <summary>
    /// Imports .js files as TextAssets with the script icon, so they can be assigned to HtmlDocument.Scripts.
    /// Registered as an override like <see cref="HtmlImporter"/>, so it never competes for the extension; the
    /// selector below applies it to every .js asset outside WebGL templates and plugin folders.
    /// </summary>
    [ScriptedImporter(1, new string[0], new[] { "js" })]
    public class JsImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx) => HtmlAssetImport.ImportText(ctx, HtmlAssetImport.JsIcon);
    }

    /// <summary>Routes .html assets to <see cref="HtmlImporter"/> and .js assets to <see cref="JsImporter"/> (the result is still a TextAsset, only the icon differs).</summary>
    internal sealed class HtmlImporterSelector : AssetPostprocessor
    {
        private void OnPreprocessAsset()
        {
            // WebGL template pages and scripts are whole files read by the build pipeline, not UI fragments;
            // plugin folders hold browser-side code the player links in.
            if (assetPath.IndexOf("/WebGLTemplates/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                assetPath.IndexOf("/Plugins/", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            if (assetPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                assetPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            {
                if (!(assetImporter is HtmlImporter))
                    AssetDatabase.SetImporterOverride<HtmlImporter>(assetPath);
            }
            else if (assetPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                if (!(assetImporter is JsImporter))
                    AssetDatabase.SetImporterOverride<JsImporter>(assetPath);
            }
        }
    }

    /// <summary>Shared body of the text importers: a TextAsset whose Project window icon is one of the package's PNGs.</summary>
    internal static class HtmlAssetImport
    {
        public const string HtmlIcon = "Packages/com.brendan.hiccup/Editor/Icons/HtmlAsset.png";
        public const string CssIcon = "Packages/com.brendan.hiccup/Editor/Icons/CssAsset.png";
        public const string JsIcon = "Packages/com.brendan.hiccup/Editor/Icons/JsAsset.png";

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
