using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Hiccup.Tests
{
    /// <summary>The payload both bridges emit must deserialize into <see cref="HtmlEvent"/> with every field intact.</summary>
    public class HtmlEventTests
    {
        private const string Payload =
            "{\"type\":\"input\",\"id\":\"volume\",\"tag\":\"input\",\"name\":\"vol\",\"action\":\"set\",\"value\":\"0.75\"," +
            "\"isChecked\":true,\"key\":\"a\",\"code\":\"KeyA\",\"x\":12.5,\"y\":3,\"button\":-1," +
            "\"ctrl\":false,\"shift\":true,\"alt\":false,\"meta\":true,\"deltaX\":0,\"deltaY\":-100," +
            "\"pointerId\":7,\"pointerType\":\"pen\",\"pressure\":0.5,\"movementX\":2,\"movementY\":-1," +
            "\"repeat\":true,\"isComposing\":false,\"relatedId\":\"other\",\"editable\":true,\"detail\":\"2\"," +
            "\"path\":\"panel root\",\"dataset\":\"screen=settings\\nslot=3\"}";

        [Test]
        public void ParsesEveryPayloadField()
        {
            var e = HtmlEvent.Parse(Payload, null);
            Assert.IsNotNull(e);
            Assert.AreEqual("input", e.type);
            Assert.AreEqual("volume", e.id);
            Assert.AreEqual("input", e.tag);
            Assert.AreEqual("vol", e.name);
            Assert.AreEqual("set", e.action);
            Assert.AreEqual("0.75", e.value);
            Assert.IsTrue(e.isChecked);
            Assert.AreEqual("a", e.key);
            Assert.AreEqual("KeyA", e.code);
            Assert.AreEqual(12.5f, e.x);
            Assert.AreEqual(3f, e.y);
            Assert.AreEqual(-1, e.button);
            Assert.IsFalse(e.ctrl);
            Assert.IsTrue(e.shift);
            Assert.IsTrue(e.meta);
            Assert.AreEqual(-100f, e.deltaY);
            Assert.AreEqual(7, e.pointerId);
            Assert.AreEqual("pen", e.pointerType);
            Assert.AreEqual(0.5f, e.pressure);
            Assert.AreEqual(2f, e.movementX);
            Assert.AreEqual(-1f, e.movementY);
            Assert.IsTrue(e.repeat);
            Assert.IsFalse(e.isComposing);
            Assert.AreEqual("other", e.relatedId);
            Assert.IsTrue(e.editable);
            Assert.AreEqual("2", e.detail);
            Assert.AreEqual("panel root", e.path);
        }

        [Test]
        public void ValueAndDataHelpers()
        {
            var e = HtmlEvent.Parse(Payload, null);
            Assert.AreEqual(0.75f, e.ValueAsFloat);
            Assert.AreEqual(1, e.ValueAsInt);
            Assert.AreEqual("settings", e.GetData("screen"));
            Assert.AreEqual("3", e.GetData("slot"));
            Assert.IsNull(e.GetData("missing"));
            Assert.IsTrue(e.IsKey("A"), "IsKey ignores case");
            Assert.IsFalse(e.IsKey("b"));
        }

        [Test]
        public void ValueAsFloatIsCultureInvariantAndSafe()
        {
            Assert.AreEqual(1.5f, new HtmlEvent { value = "1.5" }.ValueAsFloat);
            Assert.AreEqual(0f, new HtmlEvent { value = "not a number" }.ValueAsFloat);
            Assert.AreEqual(0f, new HtmlEvent { value = null }.ValueAsFloat);
        }

        [Test]
        public void TargetsWithoutADocumentAreSafeNoOps()
        {
            var e = HtmlEvent.Parse(Payload, null);
            Assert.IsFalse(e.Target.IsValid);
            Assert.IsFalse(e.RelatedTarget.IsValid);
            e.Target.Text = "ignored";   // a None element swallows writes
            Assert.AreEqual(string.Empty, e.Target.Text);
        }

        [Test]
        public void MalformedPayloadIsDroppedWithAWarning()
        {
            LogAssert.Expect(LogType.Warning, new Regex("Could not parse event payload"));
            Assert.IsNull(HtmlEvent.Parse("{this is not json", null));
        }
    }
}
