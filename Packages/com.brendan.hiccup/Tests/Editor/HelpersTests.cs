using System.Collections;
using System.Collections.Generic;
using Hiccup.Editor.Cdp;
using NUnit.Framework;
using UnityEngine;

namespace Hiccup.Tests
{
    public class HelpersTests
    {
        [Test]
        public void EscapeReplacesMarkupCharacters()
        {
            Assert.AreEqual("&lt;a href=&quot;x&quot;&gt;Tom &amp; Jerry&#39;s&lt;/a&gt;", Html.Escape("<a href=\"x\">Tom & Jerry's</a>"));
            Assert.AreEqual(string.Empty, Html.Escape(null));
            const string plain = "nothing to escape";
            Assert.AreSame(plain, Html.Escape(plain), "text without markup characters is returned as is");
        }

        [Test]
        public void PngEncodingRoundTripsAReadableTexture()
        {
            var tex = new Texture2D(3, 2, TextureFormat.RGBA32, false);
            var px = new Color32[]
            {
                new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255), new Color32(0, 0, 255, 255),
                new Color32(10, 20, 30, 40), new Color32(200, 100, 50, 255), new Color32(0, 0, 0, 0)
            };
            tex.SetPixels32(px);
            tex.Apply();
            try
            {
                Assert.AreEqual("image/png", HtmlImageEncoder.MimeType(HtmlImageFormat.Png));
                var bytes = HtmlImageEncoder.Encode(tex, new RectInt(0, 0, 3, 2), HtmlImageFormat.Png, 85);
                Assert.IsNotNull(bytes);

                var decoded = new Texture2D(1, 1);
                Assert.IsTrue(decoded.LoadImage(bytes));
                Assert.AreEqual(3, decoded.width);
                Assert.AreEqual(2, decoded.height);
                CollectionAssert.AreEqual(px, decoded.GetPixels32());
                Object.DestroyImmediate(decoded);

                // A sub-rectangle, bottom-left origin: the right two pixels of both rows.
                bytes = HtmlImageEncoder.Encode(tex, new RectInt(1, 0, 2, 2), HtmlImageFormat.Png, 85);
                decoded = new Texture2D(1, 1);
                Assert.IsTrue(decoded.LoadImage(bytes));
                Assert.AreEqual(2, decoded.width);
                CollectionAssert.AreEqual(new[] { px[1], px[2], px[4], px[5] }, decoded.GetPixels32());
                Object.DestroyImmediate(decoded);

                // Out-of-range rectangles clamp rather than throw.
                bytes = HtmlImageEncoder.Encode(tex, new RectInt(2, 1, 10, 10), HtmlImageFormat.Png, 85);
                decoded = new Texture2D(1, 1);
                Assert.IsTrue(decoded.LoadImage(bytes));
                Assert.AreEqual(1, decoded.width);
                Assert.AreEqual(1, decoded.height);
                Object.DestroyImmediate(decoded);

                var jpeg = HtmlImageEncoder.Encode(tex, new RectInt(0, 0, 3, 2), HtmlImageFormat.Jpeg, 70);
                Assert.AreEqual("image/jpeg", HtmlImageEncoder.MimeType(HtmlImageFormat.Jpeg));
                Assert.AreEqual(0xFF, jpeg[0]);
                Assert.AreEqual(0xD8, jpeg[1]);

                Assert.IsNull(HtmlImageEncoder.Encode(null, new RectInt(0, 0, 1, 1), HtmlImageFormat.Png, 85));
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void PreviewJsonReaderHandlesNestedValues()
        {
            var d = Json.Parse("{\"a\":[1,true,\"x\\ny\"],\"b\":null,\"c\":{\"d\":-2.5,\"e\":\"\\u00e9\"}}") as Dictionary<string, object>;
            Assert.IsNotNull(d);
            var a = d["a"] as IList;
            Assert.IsNotNull(a);
            Assert.AreEqual(3, a.Count);
            Assert.AreEqual(1.0, a[0]);
            Assert.AreEqual(true, a[1]);
            Assert.AreEqual("x\ny", a[2]);
            Assert.IsNull(d["b"]);
            var c = Json.Dict(d, "c");
            Assert.AreEqual(-2.5, Json.Num(c, "d"));
            Assert.AreEqual("\u00e9", Json.Str(c, "e"));
            Assert.AreEqual("fallback", Json.Str(c, "missing", "fallback"));
        }

        [Test]
        public void PreviewJsonQuoteRoundTrips()
        {
            const string s = "quote \" backslash \\ newline \n tab \t control \u0001 unicode \u2192";
            Assert.AreEqual(s, Json.Parse(Json.Quote(s)));
        }
    }
}
