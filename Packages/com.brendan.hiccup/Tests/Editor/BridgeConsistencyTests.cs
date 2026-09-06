using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Hiccup.Editor.Cdp;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace Hiccup.Tests
{
    /// <summary>
    /// The runtime talks to two bridges, Hiccup.jslib in a web build and the script the Editor preview injects,
    /// through one C ABI. These tests read the sources and check the three stay in step, which no compiler does.
    /// </summary>
    public class BridgeConsistencyTests
    {
        private static string PackageRoot => PackageInfo.FindForAssembly(typeof(HtmlDocument).Assembly).resolvedPath;
        private static string Read(string relative) => File.ReadAllText(Path.Combine(PackageRoot, relative));

        [Test]
        public void EveryNativeImportHasAJslibExportAndAnEditorStub()
        {
            var native = Read("Runtime/HtmlNative.cs");
            var jslib = Read("Runtime/Plugins/WebGL/Hiccup.jslib");

            var imports = Regex.Matches(native, @"\[DllImport\(""__Internal""\)\][^;]*?(Hiccup_\w+)\(").Cast<Match>().Select(m => m.Groups[1].Value).ToHashSet();
            var stubSection = native.Substring(native.LastIndexOf("#else", System.StringComparison.Ordinal));
            var stubs = Regex.Matches(stubSection, @"public static \w+ (Hiccup_\w+)\(").Cast<Match>().Select(m => m.Groups[1].Value).ToHashSet();
            var exports = Regex.Matches(jslib, @"^\s{2}(Hiccup_\w+):", RegexOptions.Multiline).Cast<Match>().Select(m => m.Groups[1].Value).ToHashSet();

            Assert.IsNotEmpty(imports);
            CollectionAssert.IsEmpty(imports.Except(exports), "DllImports without a jslib export");
            CollectionAssert.IsEmpty(imports.Except(stubs), "DllImports without an Editor stub");
            CollectionAssert.IsEmpty(exports.Except(imports), "jslib exports nothing imports");
        }

        private static List<string> PayloadFields(string js)
        {
            var body = Regex.Match(js, @"var o = \{(.*?)\};", RegexOptions.Singleline);
            Assert.IsTrue(body.Success, "payload object literal not found");
            return Regex.Matches(body.Groups[1].Value, @"(\w+):").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
        }

        [Test]
        public void BothBridgesBuildTheSameEventPayload()
        {
            var jslib = PayloadFields(Read("Runtime/Plugins/WebGL/Hiccup.jslib"));
            var preview = PayloadFields(CdpBridgeJs.Source);
            CollectionAssert.AreEqual(jslib, preview);

            // And every field the bridges send is a field HtmlEvent declares, so nothing is silently dropped.
            var declared = typeof(HtmlEvent).GetFields().Select(f => f.Name).ToHashSet();
            CollectionAssert.IsEmpty(jslib.Except(declared), "payload fields HtmlEvent does not declare");
        }

        [Test]
        public void PreviewBridgeSourceContainsNoDoubleQuotes()
        {
            // It lives in a C# verbatim string; a stray double quote would silently end it.
            Assert.IsFalse(CdpBridgeJs.Source.Contains("\""));
        }

        [Test]
        public void JslibStaysEs5ForTheEmscriptenPreprocessor()
        {
            var jslib = Read("Runtime/Plugins/WebGL/Hiccup.jslib");
            Assert.IsFalse(jslib.Contains("=>"), "arrow function in the jslib");
            Assert.IsFalse(Regex.IsMatch(jslib, @"^\s*(let|const)\s", RegexOptions.Multiline), "let/const in the jslib");
            Assert.IsFalse(Regex.IsMatch(jslib, @"^\s*async\s", RegexOptions.Multiline), "async function in the jslib (keep the keyword inside a string)");
        }
    }
}
