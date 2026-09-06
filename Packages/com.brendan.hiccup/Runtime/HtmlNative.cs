using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Hiccup
{
    /// <summary>
    /// Raw bindings to Hiccup.jslib. Only WebGL/WebGPU player builds have a real implementation;
    /// in the Editor and on other platforms every call is a harmless no-op so scenes still load.
    /// </summary>
    internal static class HtmlNative
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void EventCallback(int panel, IntPtr json);
        /// <summary>A page-to-C# message: <c>HUI.send(name, payload)</c> in the page.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void MessageCallback(int panel, IntPtr name, IntPtr data);
        /// <summary>Completion of <c>Hiccup_PanelEvalAsync</c>: <paramref name="failed"/> is non-zero when <paramref name="result"/> is an error message.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void EvalCallback(int panel, int requestId, IntPtr result, int failed);

        /// <summary>Reads a UTF8 string returned by the bridge and frees it.</summary>
        public static string TakeString(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
                return string.Empty;
            try
            {
                return ReadUtf8(ptr);
            }
            finally
            {
                Hiccup_Free(ptr);
            }
        }

        public static string ReadUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
                return string.Empty;
#if NET_STANDARD_2_1
            return Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
#else
            // The .NET Framework profile has no PtrToStringUTF8: find the terminator, then decode.
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0)
                len++;
            if (len == 0)
                return string.Empty;
            var bytes = new byte[len];
            Marshal.Copy(ptr, bytes, 0, len);
            return Encoding.UTF8.GetString(bytes);
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        public static bool Available => true;

        [DllImport("__Internal")] public static extern int Hiccup_Init(int backend, int linear, int forceOverlay, int debug, EventCallback cb, MessageCallback messageCb, EvalCallback evalCb);
        [DllImport("__Internal")] public static extern IntPtr Hiccup_GetFeatures();
        [DllImport("__Internal")] public static extern void Hiccup_GetCanvasInfo(float[] outInfo);
        [DllImport("__Internal")] public static extern void Hiccup_SetUpdateMode(int mode);
        [DllImport("__Internal")] public static extern void Hiccup_SetGeometryMode(int mode);
        [DllImport("__Internal")] public static extern void Hiccup_Update();
        [DllImport("__Internal")] public static extern void Hiccup_Free(IntPtr ptr);

        [DllImport("__Internal")] public static extern int Hiccup_PanelCreate(int w, int h);
        [DllImport("__Internal")] public static extern void Hiccup_PanelDestroy(int id);
        [DllImport("__Internal")] public static extern int Hiccup_PanelIsReady(int id);
        [DllImport("__Internal")] public static extern void Hiccup_PanelSetHtml(int id, string html);
        [DllImport("__Internal")] public static extern void Hiccup_PanelSetCss(int id, string css);
        [DllImport("__Internal")] public static extern void Hiccup_PanelSetSize(int id, int w, int h);
        [DllImport("__Internal")] public static extern void Hiccup_PanelSetVisible(int id, int visible);
        [DllImport("__Internal")] public static extern void Hiccup_PanelSetPointerMode(int id, int mode);
        [DllImport("__Internal")] public static extern void Hiccup_PanelSetBlockInput(int id, int block);
        [DllImport("__Internal")] public static extern void Hiccup_PanelSetPremultiplied(int id, int v);
        [DllImport("__Internal")] public static extern void Hiccup_PanelSetPreventSubmit(int id, int v);
        [DllImport("__Internal")] public static extern void Hiccup_PanelSetResolutionScale(int id, float scale);
        [DllImport("__Internal")] public static extern void Hiccup_PanelSetMipmaps(int id, int v);
        [DllImport("__Internal")] public static extern int Hiccup_PanelTakeUpdated(int id);
        [DllImport("__Internal")] public static extern void Hiccup_PanelListen(int id, string type, int enabled);
        [DllImport("__Internal")] public static extern void Hiccup_PanelInvalidate(int id);
        [DllImport("__Internal")] public static extern void Hiccup_PanelSetGeometry(int id, float[] columnMajor16);
        [DllImport("__Internal")] public static extern void Hiccup_PanelGetTextureSize(int id, int[] outWH);
        [DllImport("__Internal")] public static extern int Hiccup_PanelCreateGLTexture(int id);
        [DllImport("__Internal")] public static extern void Hiccup_PanelBindGPUTexture(int id, IntPtr texturePtr);
        [DllImport("__Internal")] public static extern void Hiccup_PanelAnnounce(int id, string text, int assertive);
        [DllImport("__Internal")] public static extern void Hiccup_PanelSetImage(int id, string name, byte[] data, int length, string mime);
        [DllImport("__Internal")] public static extern IntPtr Hiccup_PanelEval(int id, string code);
        [DllImport("__Internal")] public static extern void Hiccup_PanelEvalAsync(int id, string code, int requestId);

        [DllImport("__Internal")] public static extern int Hiccup_Query(int id, string selector);
        [DllImport("__Internal")] public static extern IntPtr Hiccup_QueryAll(int id, string selector);
        [DllImport("__Internal")] public static extern void Hiccup_ElemRelease(int h);
        [DllImport("__Internal")] public static extern IntPtr Hiccup_ElemEnsureId(int h);
        [DllImport("__Internal")] public static extern IntPtr Hiccup_ElemGetText(int h);
        [DllImport("__Internal")] public static extern void Hiccup_ElemSetText(int h, string s);
        [DllImport("__Internal")] public static extern IntPtr Hiccup_ElemGetHtml(int h);
        [DllImport("__Internal")] public static extern void Hiccup_ElemSetHtml(int h, string s);
        [DllImport("__Internal")] public static extern void Hiccup_ElemInsertHtml(int h, string where, string s);
        [DllImport("__Internal")] public static extern IntPtr Hiccup_ElemGetAttr(int h, string name);
        [DllImport("__Internal")] public static extern int Hiccup_ElemHasAttr(int h, string name);
        [DllImport("__Internal")] public static extern void Hiccup_ElemSetAttr(int h, string name, string value);
        [DllImport("__Internal")] public static extern void Hiccup_ElemRemoveAttr(int h, string name);
        [DllImport("__Internal")] public static extern IntPtr Hiccup_ElemGetProp(int h, string name);
        [DllImport("__Internal")] public static extern void Hiccup_ElemSetProp(int h, string name, string value);
        [DllImport("__Internal")] public static extern void Hiccup_ElemSetBoolProp(int h, string name, int value);
        [DllImport("__Internal")] public static extern int Hiccup_ElemGetBoolProp(int h, string name);
        [DllImport("__Internal")] public static extern void Hiccup_ElemSetStyle(int h, string name, string value);
        [DllImport("__Internal")] public static extern IntPtr Hiccup_ElemGetStyle(int h, string name);
        [DllImport("__Internal")] public static extern void Hiccup_ElemAddClass(int h, string c);
        [DllImport("__Internal")] public static extern void Hiccup_ElemRemoveClass(int h, string c);
        [DllImport("__Internal")] public static extern void Hiccup_ElemToggleClass(int h, string c, int force);
        [DllImport("__Internal")] public static extern int Hiccup_ElemHasClass(int h, string c);
        [DllImport("__Internal")] public static extern void Hiccup_ElemFocus(int h);
        [DllImport("__Internal")] public static extern void Hiccup_ElemBlur(int h);
        [DllImport("__Internal")] public static extern void Hiccup_ElemClick(int h);
        [DllImport("__Internal")] public static extern void Hiccup_ElemRemove(int h);
        [DllImport("__Internal")] public static extern void Hiccup_ElemShowModal(int h, int show);
        [DllImport("__Internal")] public static extern void Hiccup_ElemGetBounds(int h, float[] outXYWH);
        [DllImport("__Internal")] public static extern int Hiccup_ElemQuery(int h, string selector);
        [DllImport("__Internal")] public static extern IntPtr Hiccup_ElemQueryAll(int h, string selector);
        [DllImport("__Internal")] public static extern int Hiccup_ElemClosest(int h, string selector);
        [DllImport("__Internal")] public static extern void Hiccup_ElemCall(int h, string method);
        [DllImport("__Internal")] public static extern int Hiccup_ElemParent(int h);
        [DllImport("__Internal")] public static extern int Hiccup_ElemMatches(int h, string selector);
        [DllImport("__Internal")] public static extern void Hiccup_ElemScrollIntoView(int h);
#else
        /// <summary>
        /// True once an <see cref="IHtmlBackend"/> is registered (the Editor preview). Without one every call below
        /// is a harmless no-op so scenes still load on platforms that have no bridge at all.
        /// </summary>
        public static bool Available => HtmlBackend.Current != null;

        // ---- Editor / non-web stubs. They forward to the registered backend, or keep just enough
        //      state for the API to stay usable (and testable) when there is none.
        private static int _nextPanel = 1;

        /// <summary>Non-null string returned by an unimplemented backend getter, so callers never see null.</summary>
        private static IntPtr AllocUtf8(string s)
        {
            if (string.IsNullOrEmpty(s))
                return IntPtr.Zero;
            var bytes = Encoding.UTF8.GetBytes(s);
            var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteByte(ptr, bytes.Length, 0);
            return ptr;
        }

        public static int Hiccup_Init(int backend, int linear, int forceOverlay, int debug, EventCallback cb, MessageCallback messageCb, EvalCallback evalCb)
            => HtmlBackend.Current?.Init(backend, linear, forceOverlay, debug) ?? -1;
        public static IntPtr Hiccup_GetFeatures() => IntPtr.Zero;
        public static void Hiccup_GetCanvasInfo(float[] outInfo)
        {
            var b = HtmlBackend.Current;
            if (b != null)
            {
                b.GetCanvasInfo(outInfo);
                return;
            }
            outInfo[0] = UnityEngine.Screen.width; outInfo[1] = UnityEngine.Screen.height; outInfo[2] = 1f;
            outInfo[3] = UnityEngine.Screen.width; outInfo[4] = UnityEngine.Screen.height;
        }
        public static void Hiccup_SetUpdateMode(int mode) { }
        public static void Hiccup_SetGeometryMode(int mode) { }
        public static void Hiccup_Update() => HtmlBackend.Current?.Update();
        public static void Hiccup_Free(IntPtr ptr)
        {
            if (ptr != IntPtr.Zero)
                Marshal.FreeHGlobal(ptr);
        }

        public static int Hiccup_PanelCreate(int w, int h) => HtmlBackend.Current?.PanelCreate(w, h) ?? _nextPanel++;
        public static void Hiccup_PanelDestroy(int id) => HtmlBackend.Current?.PanelDestroy(id);
        // No backend: the no-op panel is "ready" at once, so IsCreated and Created keep their old timing.
        public static int Hiccup_PanelIsReady(int id) => (HtmlBackend.Current?.PanelIsReady(id) ?? true) ? 1 : 0;
        public static void Hiccup_PanelSetHtml(int id, string html) => HtmlBackend.Current?.PanelSetHtml(id, html);
        public static void Hiccup_PanelSetCss(int id, string css) => HtmlBackend.Current?.PanelSetCss(id, css);
        public static void Hiccup_PanelSetSize(int id, int w, int h) => HtmlBackend.Current?.PanelSetSize(id, w, h);
        public static void Hiccup_PanelSetVisible(int id, int visible) => HtmlBackend.Current?.PanelSetVisible(id, visible != 0);
        public static void Hiccup_PanelSetPointerMode(int id, int mode) { }
        public static void Hiccup_PanelSetBlockInput(int id, int block) { }
        public static void Hiccup_PanelSetPremultiplied(int id, int v) { }
        public static void Hiccup_PanelSetPreventSubmit(int id, int v) { }
        public static void Hiccup_PanelSetResolutionScale(int id, float scale) => HtmlBackend.Current?.PanelSetResolutionScale(id, scale);
        public static void Hiccup_PanelSetMipmaps(int id, int v) => HtmlBackend.Current?.PanelSetMipmaps(id, v != 0);
        public static int Hiccup_PanelTakeUpdated(int id) => 0;
        public static void Hiccup_PanelListen(int id, string type, int enabled) => HtmlBackend.Current?.PanelListen(id, type, enabled != 0);
        public static void Hiccup_PanelInvalidate(int id) => HtmlBackend.Current?.PanelInvalidate(id);
        public static void Hiccup_PanelSetGeometry(int id, float[] columnMajor16) => HtmlBackend.Current?.PanelSetGeometry(id, columnMajor16);
        public static void Hiccup_PanelGetTextureSize(int id, int[] outWH)
        {
            var b = HtmlBackend.Current;
            if (b != null)
            {
                b.PanelGetTextureSize(id, outWH);
                return;
            }
            outWH[0] = 0; outWH[1] = 0;
        }
        public static int Hiccup_PanelCreateGLTexture(int id) => 0;
        public static void Hiccup_PanelBindGPUTexture(int id, IntPtr texturePtr) { }
        public static void Hiccup_PanelAnnounce(int id, string text, int assertive) => HtmlBackend.Current?.PanelAnnounce(id, text, assertive != 0);
        public static void Hiccup_PanelSetImage(int id, string name, byte[] data, int length, string mime) => HtmlBackend.Current?.PanelSetImage(id, name, data, length, mime);
        public static IntPtr Hiccup_PanelEval(int id, string code) => AllocUtf8(HtmlBackend.Current?.PanelEval(id, code));
        public static void Hiccup_PanelEvalAsync(int id, string code, int requestId)
        {
            var b = HtmlBackend.Current;
            if (b != null)
                b.PanelEvalAsync(id, code, requestId);
            else
                HtmlBackend.CompleteEval(id, requestId, string.Empty, null);   // no browser: Eval returns empty here too
        }

        public static int Hiccup_Query(int id, string selector) => HtmlBackend.Current?.Query(id, selector) ?? 0;
        public static IntPtr Hiccup_QueryAll(int id, string selector) => AllocUtf8(HtmlBackend.Current?.QueryAll(id, selector));
        public static void Hiccup_ElemRelease(int h) => HtmlBackend.Current?.ElemRelease(h);
        public static IntPtr Hiccup_ElemEnsureId(int h) => AllocUtf8(HtmlBackend.Current?.ElemEnsureId(h));
        public static IntPtr Hiccup_ElemGetText(int h) => AllocUtf8(HtmlBackend.Current?.ElemGetText(h));
        public static void Hiccup_ElemSetText(int h, string s) => HtmlBackend.Current?.ElemSetText(h, s);
        public static IntPtr Hiccup_ElemGetHtml(int h) => AllocUtf8(HtmlBackend.Current?.ElemGetHtml(h));
        public static void Hiccup_ElemSetHtml(int h, string s) => HtmlBackend.Current?.ElemSetHtml(h, s);
        public static void Hiccup_ElemInsertHtml(int h, string where, string s) => HtmlBackend.Current?.ElemInsertHtml(h, where, s);
        public static IntPtr Hiccup_ElemGetAttr(int h, string name) => AllocUtf8(HtmlBackend.Current?.ElemGetAttr(h, name));
        public static int Hiccup_ElemHasAttr(int h, string name) => (HtmlBackend.Current?.ElemHasAttr(h, name) ?? false) ? 1 : 0;
        public static void Hiccup_ElemSetAttr(int h, string name, string value) => HtmlBackend.Current?.ElemSetAttr(h, name, value);
        public static void Hiccup_ElemRemoveAttr(int h, string name) => HtmlBackend.Current?.ElemRemoveAttr(h, name);
        public static IntPtr Hiccup_ElemGetProp(int h, string name) => AllocUtf8(HtmlBackend.Current?.ElemGetProp(h, name));
        public static void Hiccup_ElemSetProp(int h, string name, string value) => HtmlBackend.Current?.ElemSetProp(h, name, value);
        public static void Hiccup_ElemSetBoolProp(int h, string name, int value) => HtmlBackend.Current?.ElemSetBoolProp(h, name, value != 0);
        public static int Hiccup_ElemGetBoolProp(int h, string name) => (HtmlBackend.Current?.ElemGetBoolProp(h, name) ?? false) ? 1 : 0;
        public static void Hiccup_ElemSetStyle(int h, string name, string value) => HtmlBackend.Current?.ElemSetStyle(h, name, value);
        public static IntPtr Hiccup_ElemGetStyle(int h, string name) => AllocUtf8(HtmlBackend.Current?.ElemGetStyle(h, name));
        public static void Hiccup_ElemAddClass(int h, string c) => HtmlBackend.Current?.ElemAddClass(h, c);
        public static void Hiccup_ElemRemoveClass(int h, string c) => HtmlBackend.Current?.ElemRemoveClass(h, c);
        public static void Hiccup_ElemToggleClass(int h, string c, int force) => HtmlBackend.Current?.ElemToggleClass(h, c, force);
        public static int Hiccup_ElemHasClass(int h, string c) => (HtmlBackend.Current?.ElemHasClass(h, c) ?? false) ? 1 : 0;
        public static void Hiccup_ElemFocus(int h) => HtmlBackend.Current?.ElemFocus(h);
        public static void Hiccup_ElemBlur(int h) => HtmlBackend.Current?.ElemBlur(h);
        public static void Hiccup_ElemClick(int h) => HtmlBackend.Current?.ElemClick(h);
        public static void Hiccup_ElemRemove(int h) => HtmlBackend.Current?.ElemRemove(h);
        public static void Hiccup_ElemShowModal(int h, int show) => HtmlBackend.Current?.ElemShowModal(h, show != 0);
        public static void Hiccup_ElemGetBounds(int h, float[] outXYWH)
        {
            var b = HtmlBackend.Current;
            if (b != null)
            {
                b.ElemGetBounds(h, outXYWH);
                return;
            }
            outXYWH[0] = outXYWH[1] = outXYWH[2] = outXYWH[3] = 0;
        }
        public static int Hiccup_ElemQuery(int h, string selector) => HtmlBackend.Current?.ElemQuery(h, selector) ?? 0;
        public static IntPtr Hiccup_ElemQueryAll(int h, string selector) => AllocUtf8(HtmlBackend.Current?.ElemQueryAll(h, selector));
        public static int Hiccup_ElemClosest(int h, string selector) => HtmlBackend.Current?.ElemClosest(h, selector) ?? 0;
        public static void Hiccup_ElemCall(int h, string method) => HtmlBackend.Current?.ElemCall(h, method);
        public static int Hiccup_ElemParent(int h) => HtmlBackend.Current?.ElemParent(h) ?? 0;
        public static int Hiccup_ElemMatches(int h, string selector) => (HtmlBackend.Current?.ElemMatches(h, selector) ?? false) ? 1 : 0;
        public static void Hiccup_ElemScrollIntoView(int h) => HtmlBackend.Current?.ElemScrollIntoView(h);
#endif
    }
}
