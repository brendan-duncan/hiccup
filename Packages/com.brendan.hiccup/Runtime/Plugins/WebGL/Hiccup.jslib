// Hiccup (HTML-in-Canvas Components Unity Package) - browser side bridge for Unity Web (WebGL / WebGPU).
//
// Uses the Chrome "HTML-in-Canvas" API (https://github.com/WICG/html-in-canvas):
//   * <canvas layoutsubtree>  - DOM children of the Unity canvas get layout + accessibility.
//   * <div drawable>          - the element that can be snapshotted into a texture.
//   * canvas 'paint' event    - fires when a drawable's snapshot changed.
//   * gl.texElementImage2D / gl.texElementSubImage2D            (WebGL)
//   * queue.drawElementImageToTexture / copyElementImageToTexture (WebGPU)
//   * canvas.updateElementGeometry / getElementTransform         (hit testing + a11y geometry)
//
// When the API is not available the bridge falls back to "overlay" mode: the same DOM is placed
// in a fixed-position layer on top of the canvas, so the UI still works, is accessible and
// interactive, it is just not composited into the Unity frame.
//
// Written in ES5 on purpose so the Emscripten JS pre-processor accepts it on every Unity 6 release.

var HiccupLibrary = {

  $HUI: {
    VERSION: '0.1.0',
    canvas: null,
    backend: 0,        // 1 = WebGL2, 2 = WebGPU  (told by C#)
    mode: 0,           // 0 = overlay fallback, 1 = html-in-canvas texture mode
    updateMode: 0,     // 0 = auto, 1 = only on paint events, 2 = every frame
    geometryMode: 0,   // 0 = auto, 1 = updateElementGeometry, 2 = getElementTransform, 3 = CSS matrix3d only
    linear: false,     // project color space is linear -> sRGB texture storage
    debug: false,
    eventCb: 0,
    messageCb: 0,      // (panel, name, data)            HUI.send from page script
    evalCb: 0,         // (panel, requestId, result, failed) Hiccup_PanelEvalAsync completion
    AsyncFunction: undefined,   // resolved lazily; see asyncFunction()
    panels: {},
    nextPanel: 1,
    handles: [null],
    freeHandles: [],
    overlay: null,
    overlayBehind: false,   // overlay placed under a transparent canvas; surfaces cut holes for it
    overlayRect: [0, 0, 0, 0],
    idCounter: 0,
    warned: {},
    frame: 0,
    paintSeen: false,
    paintRequested: false,
    features: null,
    baseStyleInjected: false,

    DEFAULT_EVENTS: ['click', 'dblclick', 'input', 'change', 'submit', 'keydown', 'focusin', 'focusout'],
    BLOCK_EVENTS: ['pointerdown', 'pointerup', 'pointermove', 'pointercancel', 'mousedown', 'mouseup', 'mousemove',
                   'click', 'dblclick', 'contextmenu', 'wheel', 'touchstart', 'touchend', 'touchmove', 'touchcancel',
                   'keydown', 'keyup', 'keypress'],

    // ------------------------------------------------------------------ utils

    log: function (msg) { if (HUI.debug) console.log('[Hiccup] ' + msg); },
    warnOnce: function (key, msg) {
      if (HUI.warned[key]) return;
      HUI.warned[key] = true;
      console.warn('[Hiccup] ' + msg);
    },

    // JS string -> freshly malloc'd UTF8 buffer. The C# side copies it and calls Hiccup_Free.
    cstr: function (s) {
      if (s === null || s === undefined) s = '';
      s = String(s);
      var len = lengthBytesUTF8(s) + 1;
      var p = _malloc(len);
      stringToUTF8(s, p, len);
      return p;
    },

    // How a value crosses back to C#: strings as they are, everything else as JSON, undefined as ''.
    stringify: function (r) {
      return r === undefined ? '' : (typeof r === 'string' ? r : JSON.stringify(r));
    },

    // The AsyncFunction constructor, so EvalAsync bodies may use await. The async keyword is kept inside a
    // string: this file stays ES5 for the Emscripten pre-processor, the page is always a modern Chrome.
    asyncFunction: function () {
      if (HUI.AsyncFunction === undefined) {
        try { HUI.AsyncFunction = new Function('return Object.getPrototypeOf(async function(){}).constructor')(); }
        catch (e) { HUI.AsyncFunction = null; }
      }
      return HUI.AsyncFunction || Function;
    },

    panel: function (id) {
      var p = HUI.panels[id];
      if (!p) HUI.warnOnce('nopanel' + id, 'Unknown panel id ' + id);
      return p;
    },

    handle: function (el) {
      if (!el) return 0;
      if (el.__huiHandle && HUI.handles[el.__huiHandle] === el) return el.__huiHandle;
      var h = HUI.freeHandles.length ? HUI.freeHandles.pop() : HUI.handles.length;
      HUI.handles[h] = el;
      el.__huiHandle = h;
      return h;
    },

    el: function (h) {
      var e = HUI.handles[h];
      if (!e) HUI.warnOnce('nohandle' + h, 'Unknown element handle ' + h);
      return e || null;
    },

    releaseHandle: function (h) {
      var e = HUI.handles[h];
      if (!e) return;
      if (e.__huiHandle === h) delete e.__huiHandle;
      HUI.handles[h] = null;
      HUI.freeHandles.push(h);
    },

    ensureId: function (el) {
      if (!el.id) el.id = 'hui-' + (++HUI.idCounter);
      return el.id;
    },

    getGL: function () {
      if (typeof GLctx !== 'undefined' && GLctx) return GLctx;
      if (Module['ctx']) return Module['ctx'];
      return null;
    },

    // Unity's WebGPU glue keeps its GPU objects in a JS side table (`wgpu`) and passes around integer
    // handles into it. Anything we get from the engine may therefore be a handle rather than the object:
    // Module.WebGPU.device is a GPUDevice up to 6000.7.0a4 and a handle from 6000.7.0a5 on (see
    // PlatformDependent/WebGL/js/WebGPU.js, JS_WebGPU_Setup). Resolve both forms.
    // `wgpu` only exists in WebGPU-enabled players, so it is probed with typeof rather than declared
    // as a jslib dependency, which would fail to link in a WebGL2-only build.
    gpuObject: function (v) {
      if (typeof v === 'number') {
        if (!v || typeof wgpu === 'undefined' || !wgpu) return null;
        v = wgpu[v];
      }
      return v || null;
    },

    getGPUDevice: function () {
      try {
        var d = null;
        if (Module['WebGPU']) {
          if (typeof Module['WebGPU']['getDevice'] === 'function') d = HUI.gpuObject(Module['WebGPU']['getDevice']());
          if (!d) d = HUI.gpuObject(Module['WebGPU']['device']);
        }
        // A device with no queue is not a GPUDevice; keep looking rather than failing every upload later.
        if (d && d.queue) return d;
        if (typeof WebGPU !== 'undefined' && WebGPU['mgrDevice'] && typeof WebGPU['mgrDevice'].get === 'function') {
          d = WebGPU['mgrDevice'].get(1);
          if (d && d.queue) return d;
        }
        if (typeof wgpu !== 'undefined' && wgpu) {
          for (var k in wgpu) {
            var o = wgpu[k];
            if (o && o.queue && (typeof GPUDevice === 'undefined' || o instanceof GPUDevice)) return o;
          }
        }
      } catch (e) { HUI.warnOnce('gpudev', 'Failed to look up the WebGPU device: ' + e); }
      return null;
    },

    getGPUTexture: function (ptr) {
      try {
        var t = HUI.gpuObject(ptr);
        if (t && (typeof GPUTexture === 'undefined' || t instanceof GPUTexture)) return t;
        if (Module['WebGPU'] && Module['WebGPU']['mgrTexture'] && typeof Module['WebGPU']['mgrTexture'].get === 'function') {
          t = Module['WebGPU']['mgrTexture'].get(ptr);
          if (t) return t;
        }
        if (typeof WebGPU !== 'undefined' && WebGPU['mgrTexture'] && typeof WebGPU['mgrTexture'].get === 'function') {
          var t2 = WebGPU['mgrTexture'].get(ptr);
          if (t2) return t2;
        }
        if (Module['WebGPU'] && typeof Module['WebGPU']['getJsObject'] === 'function') return Module['WebGPU']['getJsObject'](ptr);
      } catch (e) { HUI.warnOnce('gputex', 'Failed to look up WebGPU texture ' + ptr + ': ' + e); }
      return null;
    },

    dpr: function () { return window.devicePixelRatio || 1; },

    // ------------------------------------------------------------------ init

    init: function (backend, linear, forceOverlay, debug) {
      HUI.backend = backend;
      HUI.linear = !!linear;
      HUI.debug = !!debug;

      var canvas = Module['canvas'] || document.querySelector('#unity-canvas') || document.querySelector('canvas');
      HUI.canvas = canvas;

      var hic = !!(canvas && (typeof canvas.requestPaint === 'function' || ('onpaint' in canvas)));
      var texApi = '';
      if (backend === 1) {
        var gl = HUI.getGL();
        if (gl && typeof gl.texElementSubImage2D === 'function') texApi = 'texElementSubImage2D';
        else if (gl && typeof gl.texElementImage2D === 'function') texApi = 'texElementImage2D';
      } else if (backend === 2) {
        if (typeof GPUQueue !== 'undefined') {
          if (GPUQueue.prototype.drawElementImageToTexture) texApi = 'drawElementImageToTexture';
          else if (GPUQueue.prototype.copyElementImageToTexture) texApi = 'copyElementImageToTexture';
        }
        if (texApi && !HUI.getGPUDevice()) {
          HUI.warnOnce('nodevice', 'WebGPU device could not be found from JavaScript; using overlay mode.');
          texApi = '';
        }
      }

      HUI.mode = (hic && texApi && !forceOverlay) ? 1 : 0;
      // With an alpha channel on the canvas the overlay can go *behind* it: surfaces then write alpha 0 where
      // their panels are, so the DOM shows through the frame and nearer geometry covers it. Opt in from the
      // template with webglContextAttributes: { alpha: true }.
      HUI.overlayBehind = HUI.mode === 0 && HUI.canvasIsTransparent();

      HUI.injectBaseStyle();

      if (HUI.mode === 1) {
        canvas.setAttribute('layoutsubtree', '');
        HUI.bindPaint();
      } else {
        HUI.createOverlay();
        if (HUI.overlayBehind) HUI.routePointer();
      }

      HUI.guardUnityInput();

      HUI.features = {
        version: HUI.VERSION,
        htmlInCanvas: hic,
        textureApi: texApi,
        geometryApi: ['auto', 'updateElementGeometry', 'getElementTransform', 'cssTransform'][HUI.geometryMode] || 'auto',
        hasUpdateElementGeometry: !!(canvas && typeof canvas.updateElementGeometry === 'function'),
        hasGetElementTransform: !!(canvas && typeof canvas.getElementTransform === 'function'),
        mode: HUI.mode,
        overlayBehindCanvas: !!HUI.overlayBehind,
        backend: backend,
        devicePixelRatio: HUI.dpr(),
        userAgent: navigator.userAgent
      };
      HUI.log('init ' + JSON.stringify(HUI.features));
      return HUI.mode;
    },

    injectBaseStyle: function () {
      if (HUI.baseStyleInjected) return;
      HUI.baseStyleInjected = true;
      var s = document.createElement('style');
      s.setAttribute('data-hui', 'base');
      s.textContent =
        '.hui-panel{position:absolute;left:0;top:0;margin:0;padding:0;box-sizing:border-box;overflow:hidden;' +
        'transform-origin:0 0;background:transparent;isolation:isolate;contain:layout paint style;}' +
        '.hui-panel[hidden]{display:none!important;}' +
        '.hui-content{width:100%;height:100%;box-sizing:border-box;}' +
        '.hui-panel[data-hui-pointer="none"]{pointer-events:none;}' +
        '.hui-panel[data-hui-pointer="none"] *{pointer-events:none;}' +
        '.hui-panel[data-hui-pointer="children"]{pointer-events:none;}' +
        '.hui-panel[data-hui-pointer="children"] .hui-content{pointer-events:none;}' +
        '.hui-panel[data-hui-pointer="children"] .hui-content>*{pointer-events:auto;}' +
        '.hui-live{position:absolute;width:1px;height:1px;margin:-1px;padding:0;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap;border:0;}' +
        '.hui-overlay{position:absolute;left:0;top:0;width:0;height:0;overflow:hidden;pointer-events:none;}' +
        '.hui-overlay .hui-panel{pointer-events:auto;}';
      document.head.appendChild(s);
    },

    // The overlay is a sibling of the canvas with no z-index of its own, so it stacks where the canvas does:
    // just above it (opaque canvas), or just below it (transparent canvas, see overlayBehind) and in both cases
    // below anything the page draws over the canvas, such as a loading screen. A fixed, z-indexed layer on
    // <body> would sit on top of the template's loading cover and show the UI before the game does.
    createOverlay: function () {
      if (HUI.overlay) return;
      var ov = document.createElement('div');
      ov.className = 'hui-overlay';
      ov.setAttribute('data-hui', 'overlay');
      var parent = HUI.canvas && HUI.canvas.parentNode;
      if (!parent) document.body.appendChild(ov);
      else if (HUI.overlayBehind) parent.insertBefore(ov, HUI.canvas);
      else parent.insertBefore(ov, HUI.canvas.nextSibling);
      HUI.overlay = ov;
      HUI.updateOverlay();
    },

    canvasIsTransparent: function () {
      try {
        if (HUI.backend === 1) {
          var gl = HUI.getGL();
          var a = gl && gl.getContextAttributes ? gl.getContextAttributes() : null;
          return !!(a && a.alpha);
        }
        if (HUI.backend === 2 && HUI.canvas) {
          var ctx = HUI.canvas.getContext('webgpu');
          var cfg = ctx && typeof ctx.getConfiguration === 'function' ? ctx.getConfiguration() : null;
          return !!(cfg && cfg.alphaMode && cfg.alphaMode !== 'opaque');
        }
      } catch (e) {}
      return false;
    },

    // With the overlay under the canvas, pointer events would all land on the canvas. Whenever the pointer is
    // over a panel element beneath it, the canvas stops taking pointer events so the DOM receives them natively
    // (hover, focus, selection, tooltips); elsewhere the canvas gets them back for Unity. Geometry drawn in front
    // of a panel does not count here: the pointer goes to the DOM anywhere inside the panel's projected shape.
    routePointer: function () {
      if (HUI.pointerRouted || !HUI.canvas) return;
      HUI.pointerRouted = true;
      var canvas = HUI.canvas, over = false;
      var check = function (e) {
        if (e.clientX === undefined) return;
        var els = document.elementsFromPoint(e.clientX, e.clientY);
        var top = els[0] === canvas ? els[1] : els[0];
        var hit = !!(top && top.closest && top.closest('.hui-panel'));
        if (hit !== over) { over = hit; canvas.style.pointerEvents = hit ? 'none' : ''; }
      };
      window.addEventListener('pointermove', check, true);
      window.addEventListener('pointerdown', check, true);
      window.addEventListener('pointerleave', function () { if (over) { over = false; canvas.style.pointerEvents = ''; } }, true);
    },

    // Cover the canvas exactly, in the coordinate space of the overlay's containing block.
    updateOverlay: function () {
      if (!HUI.overlay || !HUI.canvas) return;
      var r = HUI.canvas.getBoundingClientRect();
      var op = HUI.overlay.offsetParent;
      var left = r.left, top = r.top;
      if (op && op !== document.body && op !== document.documentElement) {
        var b = op.getBoundingClientRect();
        left = r.left - b.left - op.clientLeft + op.scrollLeft;
        top = r.top - b.top - op.clientTop + op.scrollTop;
      } else {
        left += window.pageXOffset; top += window.pageYOffset;
      }
      var o = HUI.overlayRect;
      if (o[0] === left && o[1] === top && o[2] === r.width && o[3] === r.height) return;
      HUI.overlayRect = [left, top, r.width, r.height];
      HUI.overlay.style.left = left + 'px';
      HUI.overlay.style.top = top + 'px';
      HUI.overlay.style.width = r.width + 'px';
      HUI.overlay.style.height = r.height + 'px';
    },

    bindPaint: function () {
      var canvas = HUI.canvas;
      canvas.addEventListener('paint', function (e) {
        HUI.paintSeen = true;
        HUI.paintRequested = false;
        var changed = e && e.changedElements;
        for (var id in HUI.panels) {
          var p = HUI.panels[id];
          if (!changed || !changed.length) { p.dirty = true; continue; }
          for (var i = 0; i < changed.length; i++) {
            var c = changed[i];
            if (c === p.el || p.el.contains(c)) { p.dirty = true; break; }
          }
        }
      });
    },

    requestPaint: function () {
      if (HUI.mode !== 1) return;
      if (HUI.paintRequested) return;
      if (typeof HUI.canvas.requestPaint === 'function') {
        HUI.paintRequested = true;
        try { HUI.canvas.requestPaint(); } catch (e) { HUI.paintRequested = false; }
      }
    },

    // ------------------------------------------------------------------ panels

    createPanel: function (w, h) {
      var id = HUI.nextPanel++;
      var el = document.createElement('div');
      el.className = 'hui-panel';
      el.setAttribute('drawable', '');
      el.setAttribute('data-hui-panel', String(id));
      el.setAttribute('data-hui-pointer', 'children');
      el.style.width = w + 'px';
      el.style.height = h + 'px';

      var style = document.createElement('style');
      style.setAttribute('data-hui', 'panel-style');
      el.appendChild(style);

      var content = document.createElement('div');
      content.className = 'hui-content';
      el.appendChild(content);

      var p = {
        id: id, el: el, style: style, content: content,
        w: w, h: h, scale: 1, texW: Math.max(1, Math.round(w * HUI.dpr())), texH: Math.max(1, Math.round(h * HUI.dpr())),
        glTex: 0, gpuPtr: 0, staging: null, dirty: true, visible: true, mipmaps: true, updated: false,
        premultiply: true, blockInput: true, preventSubmit: true,
        listeners: {}, blockers: [], live: null, lastMatrix: null
      };
      // What Eval / EvalAsync code sees as `HUI`: the bridge, plus this panel's send() and its elements.
      p.api = Object.create(HUI);
      p.api.panel = el;
      p.api.root = content;
      p.api.send = function (name, payload) { HUI.sendMessage(p, name, payload); };
      HUI.panels[id] = p;

      if (HUI.mode === 1) HUI.canvas.appendChild(el); else HUI.overlay.appendChild(el);

      HUI.attachEvents(p);
      HUI.setBlockInput(p, true);
      HUI.guardUnityInput();
      HUI.requestPaint();
      return id;
    },

    destroyPanel: function (id) {
      var p = HUI.panels[id];
      if (!p) return;
      if (p.el.parentNode) p.el.parentNode.removeChild(p.el);
      if (p.glTex) {
        var gl = HUI.getGL();
        if (gl && GL.textures[p.glTex]) { gl.deleteTexture(GL.textures[p.glTex]); GL.textures[p.glTex] = null; }
      }
      if (p.staging) { try { p.staging.destroy(); } catch (e) {} }
      // release element handles that live inside this panel
      for (var h = 1; h < HUI.handles.length; h++) {
        var e = HUI.handles[h];
        if (e && (e === p.el || p.el.contains(e))) HUI.releaseHandle(h);
      }
      delete HUI.panels[id];
    },

    setSize: function (p, w, h) {
      p.w = w; p.h = h;
      p.el.style.width = w + 'px';
      p.el.style.height = h + 'px';
      p.texW = Math.max(1, Math.round(w * HUI.dpr() * p.scale));
      p.texH = Math.max(1, Math.round(h * HUI.dpr() * p.scale));
      if (p.glTex) HUI.allocGLTexture(p);
      if (p.staging) { try { p.staging.destroy(); } catch (e) {} p.staging = null; }
      p.dirty = true;
      HUI.requestPaint();
    },

    setHtml: function (p, html) {
      p.content.innerHTML = html;
      p.dirty = true;
      HUI.requestPaint();
    },

    setCss: function (p, css) {
      p.style.textContent = css;
      p.dirty = true;
      HUI.requestPaint();
    },

    setVisible: function (p, visible) {
      p.visible = !!visible;
      if (p.visible) p.el.removeAttribute('hidden'); else p.el.setAttribute('hidden', '');
      p.el.setAttribute('aria-hidden', p.visible ? 'false' : 'true');
      p.dirty = true;
      HUI.requestPaint();
    },

    // ------------------------------------------------------------------ input / events

    setBlockInput: function (p, block) {
      p.blockInput = !!block;
      for (var i = 0; i < p.blockers.length; i++) p.el.removeEventListener(p.blockers[i].t, p.blockers[i].f);
      p.blockers = [];
      if (!p.blockInput) return;
      var stop = function (e) { e.stopPropagation(); };
      for (var j = 0; j < HUI.BLOCK_EVENTS.length; j++) {
        var t = HUI.BLOCK_EVENTS[j];
        p.el.addEventListener(t, stop);
        p.blockers.push({ t: t, f: stop });
      }
    },

    attachEvents: function (p) {
      for (var i = 0; i < HUI.DEFAULT_EVENTS.length; i++) HUI.listen(p, HUI.DEFAULT_EVENTS[i], true);
    },

    listen: function (p, type, enabled) {
      if (enabled) {
        if (p.listeners[type]) return;
        var f = function (e) { HUI.onDomEvent(p, e); };
        p.listeners[type] = f;
        p.el.addEventListener(type, f);
      } else if (p.listeners[type]) {
        p.el.removeEventListener(type, p.listeners[type]);
        delete p.listeners[type];
      }
    },

    onDomEvent: function (p, e) {
      if (e.type === 'submit' && p.preventSubmit) e.preventDefault();
      if (!HUI.eventCb) return;

      var t = e.target;
      if (!t || !(t instanceof Element)) t = p.content;
      if (t === p.el) t = p.content;
      HUI.ensureId(t);

      var path = [];
      var n = t.parentElement;
      while (n && n !== p.el) { if (n.id) path.push(n.id); n = n.parentElement; }

      var actionEl = (typeof t.closest === 'function') ? t.closest('[data-action]') : null;
      if (actionEl && !p.el.contains(actionEl)) actionEl = null;

      var o = {
        type: e.type,
        id: t.id,
        tag: (t.tagName || '').toLowerCase(),
        name: t.getAttribute('name') || '',
        action: actionEl ? (actionEl.getAttribute('data-action') || '') : '',
        value: '',
        isChecked: false,
        key: '',
        code: '',
        x: 0, y: 0, button: -1,
        ctrl: !!e.ctrlKey, shift: !!e.shiftKey, alt: !!e.altKey,
        path: path.join(' '),
        dataset: ''
      };

      if ('value' in t && t.value !== undefined && t.value !== null) o.value = String(t.value);
      if (t.type === 'checkbox' || t.type === 'radio') o.isChecked = !!t.checked;
      else if (t.tagName === 'DETAILS') o.isChecked = !!t.open;
      else if (t.tagName === 'DIALOG') o.isChecked = !!t.open;
      else if (t.getAttribute('aria-pressed') !== null) o.isChecked = t.getAttribute('aria-pressed') === 'true';
      else if (t.getAttribute('aria-checked') !== null) o.isChecked = t.getAttribute('aria-checked') === 'true';

      if (e.key !== undefined) { o.key = e.key; o.code = e.code || ''; }
      if (e.clientX !== undefined) {
        var r = p.el.getBoundingClientRect();
        o.x = e.clientX - r.left;
        o.y = e.clientY - r.top;
        o.button = (e.button === undefined) ? -1 : e.button;
      }
      // data-* attributes: the [data-action] element's first, overridden by the actual target's.
      var dsMap = {};
      if (actionEl && actionEl.dataset) for (var ka in actionEl.dataset) dsMap[ka] = actionEl.dataset[ka];
      if (t.dataset) for (var kt in t.dataset) dsMap[kt] = t.dataset[kt];
      var ds = [];
      for (var kd in dsMap) ds.push(kd + '=' + dsMap[kd]);
      o.dataset = ds.join('\n');

      var ptr = HUI.cstr(JSON.stringify(o));
      var cb = HUI.eventCb;
      try {
        {{{ makeDynCall('vii', 'cb') }}}(p.id, ptr);
      } finally {
        _free(ptr);
      }
    },

    // ------------------------------------------------------------------ geometry

    // m: 16 floats, column major, panel CSS px (origin top-left, y down) -> Unity clip space (x,y in -1..1, w).
    setGeometry: function (p, m) {
      var same = !!p.lastMatrix;
      if (same) for (var i = 0; i < 16; i++) if (Math.abs(p.lastMatrix[i] - m[i]) > 1e-6) { same = false; break; }
      if (same) return;
      p.lastMatrix = Array.prototype.slice.call(m);

      var cw = HUI.canvas.clientWidth, ch = HUI.canvas.clientHeight;
      // Viewport (column major): NDC -> canvas CSS px, y flipped.
      var v = [cw / 2, 0, 0, 0,   0, -ch / 2, 0, 0,   0, 0, 1, 0,   cw / 2, ch / 2, 0, 1];
      var r = HUI.mul(v, m);
      var perspective = HUI.isPerspective(r);
      var dm;
      try { dm = new DOMMatrix(r); } catch (e) { HUI.warnOnce('dommatrix', 'DOMMatrix unavailable: ' + e); return; }

      // Canvas descendants are not hit testable until the canvas has been given their geometry
      // (updateElementGeometry / getElementTransform); a bare CSS transform draws nothing and catches nothing.
      var applied = false;
      var canvas = HUI.canvas, mode = HUI.geometryMode;
      if (HUI.mode === 1 && mode !== 3) {
        var hasGET = typeof canvas.getElementTransform === 'function' && canvas.getElementTransform.length >= 2;
        var hasUEG = typeof canvas.updateElementGeometry === 'function';
        // getElementTransform(element, matrix) is the API Chrome's WebGL/WebGPU demos use for full MVP matrices;
        // updateElementGeometry only hit-tests affine placement in current builds.
        var useGET = hasGET && (mode === 2 || (mode === 0 && (perspective || !hasUEG)));
        if (useGET) {
          try { p.el.style.transform = canvas.getElementTransform(p.el, dm).toString(); applied = true; }
          catch (e2) { HUI.warnOnce('get', 'getElementTransform failed, falling back: ' + e2); }
        }
        if (!applied && hasUEG && mode !== 2) {
          try {
            if (perspective) {
              // Register the element with an identity canvas transform and let the CSS transform do the
              // projective mapping, which regular DOM hit testing handles correctly.
              canvas.updateElementGeometry(p.el, { canvasTransform: new DOMMatrix() });
              p.el.style.transform = HUI.cssTransformFor(p, r);
            } else {
              canvas.updateElementGeometry(p.el, { canvasTransform: dm });
              p.el.style.transform = '';
            }
            applied = true;
          } catch (e1) { HUI.warnOnce('ueg', 'updateElementGeometry failed, falling back: ' + e1); }
        }
      }
      if (!applied) p.el.style.transform = HUI.cssTransformFor(p, r);
    },

    // Fourth row not (0,0,0,1) -> projective transform.
    isPerspective: function (r) {
      return Math.abs(r[3]) > 1e-9 || Math.abs(r[7]) > 1e-9 || Math.abs(r[11]) > 1e-9 || Math.abs(r[15] - 1) > 1e-6;
    },

    // CSS transform string for a column-major matrix that maps element px -> container px. layoutsubtree lays
    // canvas children out with static positioning, so a second panel starts below the first; the transform is
    // applied at that layout position, which we cancel out here.
    cssTransformFor: function (p, r) {
      var el = p.el, parent = el.parentNode;
      var lx = 0, ly = 0;
      try {
        if (el.offsetParent === parent) { lx = el.offsetLeft; ly = el.offsetTop; }
        else if (parent && el.offsetParent && el.offsetParent === parent.offsetParent) { lx = el.offsetLeft - parent.offsetLeft; ly = el.offsetTop - parent.offsetTop; }
      } catch (e) {}
      var t = r;
      if (lx || ly) t = HUI.mul([1, 0, 0, 0,  0, 1, 0, 0,  0, 0, 1, 0,  -lx, -ly, 0, 1], r);
      return 'matrix3d(' + Array.prototype.join.call(t, ',') + ')';
    },

    // ------------------------------------------------------------------ Unity input isolation

    isInsidePanel: function (node) {
      if (!node || !(node instanceof Element) || typeof node.closest !== 'function') return false;
      var el = node.closest('.hui-panel');
      if (!el) return false;
      var p = HUI.panels[el.getAttribute('data-hui-panel')];
      return !!(p && p.blockInput);
    },

    // Unity registers its keyboard/mouse/touch handlers through Emscripten before any C# runs, possibly in the
    // capture phase, and calls preventDefault() on keys, which kills typing in our inputs. stopPropagation on the
    // panel cannot beat a capture-phase listener, so wrap every Emscripten handler we can see to ignore events
    // that target an HTML panel.
    guardUnityInput: function () {
      if (typeof JSEvents === 'undefined' || !JSEvents || !JSEvents.eventHandlers) return;
      var types = HUI.BLOCK_EVENTS;
      for (var i = 0; i < JSEvents.eventHandlers.length; i++) {
        var h = JSEvents.eventHandlers[i];
        if (!h || h.__huiWrapped || !h.target || !h.handlerFunc || types.indexOf(h.eventTypeString) < 0) continue;
        // Emscripten's registered listener (eventHandler.eventListenerFunc) looks up eventHandler.handlerFunc on
        // every event, so swapping the field is enough; re-registering would run Unity's handler twice.
        (function (handler) {
          var orig = handler.handlerFunc;
          handler.handlerFunc = function (e) {
            if (HUI.isInsidePanel(e.target)) return;
            return orig.call(this, e);
          };
          handler.__huiWrapped = true;
        })(h);
      }
    },

    // column-major 4x4 multiply: a * b
    mul: function (a, b) {
      var o = new Array(16);
      for (var c = 0; c < 4; c++) {
        for (var r = 0; r < 4; r++) {
          o[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
        }
      }
      return o;
    },

    // ------------------------------------------------------------------ textures (WebGL)

    allocGLTexture: function (p) {
      var gl = HUI.getGL();
      if (!gl) return 0;
      if (!p.glTex) {
        var tex = gl.createTexture();
        var id = GL.getNewId(GL.textures);
        tex.name = id;
        GL.textures[id] = tex;
        p.glTex = id;
      }
      var st = HUI.saveGLState(gl);
      gl.bindTexture(gl.TEXTURE_2D, GL.textures[p.glTex]);
      var ifmt = HUI.linear && gl.SRGB8_ALPHA8 ? gl.SRGB8_ALPHA8 : (gl.RGBA8 || gl.RGBA);
      gl.texImage2D(gl.TEXTURE_2D, 0, ifmt, p.texW, p.texH, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
      // Allocate the mip chain up front, otherwise the texture is incomplete (samples opaque black) until the first upload.
      if (p.mipmaps) gl.generateMipmap(gl.TEXTURE_2D);
      HUI.applyGLSampler(gl, p);
      HUI.restoreGLState(gl, st);
      return p.glTex;
    },

    // Trilinear + anisotropic sampling so oblique / minified panels do not shimmer.
    applyGLSampler: function (gl, p) {
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, p.mipmaps ? gl.LINEAR_MIPMAP_LINEAR : gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      if (HUI.anisoExt === undefined) {
        HUI.anisoExt = gl.getExtension('EXT_texture_filter_anisotropic') || gl.getExtension('WEBKIT_EXT_texture_filter_anisotropic') || null;
        HUI.anisoMax = HUI.anisoExt ? gl.getParameter(HUI.anisoExt.MAX_TEXTURE_MAX_ANISOTROPY_EXT) : 1;
      }
      if (HUI.anisoExt) gl.texParameterf(gl.TEXTURE_2D, HUI.anisoExt.TEXTURE_MAX_ANISOTROPY_EXT, Math.min(16, HUI.anisoMax));
    },

    saveGLState: function (gl) {
      var st = {
        active: gl.getParameter(gl.ACTIVE_TEXTURE),
        flip: gl.getParameter(gl.UNPACK_FLIP_Y_WEBGL),
        premul: gl.getParameter(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL),
        cs: gl.getParameter(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL),
        align: gl.getParameter(gl.UNPACK_ALIGNMENT)
      };
      gl.activeTexture(gl.TEXTURE0);
      st.bound = gl.getParameter(gl.TEXTURE_BINDING_2D);
      return st;
    },

    restoreGLState: function (gl, st) {
      gl.bindTexture(gl.TEXTURE_2D, st.bound);
      gl.activeTexture(st.active);
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, st.flip);
      gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, st.premul);
      gl.pixelStorei(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL, st.cs);
      gl.pixelStorei(gl.UNPACK_ALIGNMENT, st.align);
    },

    uploadGL: function (p) {
      var gl = HUI.getGL();
      if (!gl || !p.glTex || !GL.textures[p.glTex]) return false;
      var st = HUI.saveGLState(gl);
      var ok = false;
      try {
        gl.bindTexture(gl.TEXTURE_2D, GL.textures[p.glTex]);
        gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
        gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, !!p.premultiply);
        gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4);
        var ifmtSized = HUI.linear && gl.SRGB8_ALPHA8 ? gl.SRGB8_ALPHA8 : (gl.RGBA8 || gl.RGBA);

        if (typeof gl.texElementSubImage2D === 'function') {
          // Spec form: keeps our allocation and format, writes into level 0.
          gl.texElementSubImage2D(gl.TEXTURE_2D, 0, 0, 0, p.el, { width: p.texW, height: p.texH });
          ok = true;
        } else if (typeof gl.texElementImage2D === 'function') {
          if (gl.texElementImage2D.length === 3) {
            // Chrome 150+ short form.
            gl.texElementImage2D(gl.TEXTURE_2D, ifmtSized, p.el);
          } else {
            // Chrome 138 - 149 long form (mirrors texImage2D).
            gl.texElementImage2D(gl.TEXTURE_2D, 0, ifmtSized, gl.RGBA, gl.UNSIGNED_BYTE, p.el);
          }
          ok = true;
        }
        if (ok) {
          // getError forces a round trip to the GPU process, so it is only worth paying for when debugging.
          if (HUI.debug) {
            var err = gl.getError();
            if (err !== gl.NO_ERROR) { HUI.warnOnce('glerr' + err, 'WebGL error ' + err + ' while uploading HTML texture.'); }
          }
          // Sampler state lives on the texture object and was set by allocGLTexture, which also runs whenever
          // the mipmap flag changes (C# recreates the texture), so re-specifying level 0 needs no sampler update.
          if (p.mipmaps) gl.generateMipmap(gl.TEXTURE_2D);
        }
      } catch (e) {
        // Most likely "no snapshot recorded yet": ask for a paint and try again next frame.
        HUI.warnOnce('glup', 'texElementImage2D failed (will retry): ' + e);
        ok = false;
      }
      HUI.restoreGLState(gl, st);
      return ok;
    },

    // ------------------------------------------------------------------ textures (WebGPU)

    uploadGPU: function (p) {
      var device = HUI.getGPUDevice();
      var tex = HUI.getGPUTexture(p.gpuPtr);
      if (!device || !tex) {
        HUI.warnOnce('gpuobj', 'Could not resolve WebGPU device/texture for panel ' + p.id + '. Overlay mode would be needed.');
        return false;
      }
      var q = device.queue;
      if (!q) {
        HUI.warnOnce('gpuqueue', 'The object the engine reported as its WebGPU device has no queue; ' +
          'HTML panels cannot be composited into the frame.');
        return false;
      }
      var RA = 16, CD = 2, CS = 4, TB = 4;
      if (typeof GPUTextureUsage !== 'undefined') { RA = GPUTextureUsage.RENDER_ATTACHMENT; CD = GPUTextureUsage.COPY_DST; CS = GPUTextureUsage.COPY_SRC; TB = GPUTextureUsage.TEXTURE_BINDING; }

      var dst = tex;
      var usage = (typeof tex.usage === 'number') ? tex.usage : (RA | CD);
      if ((usage & (RA | CD)) !== (RA | CD)) {
        if (!p.staging || p.staging.width !== tex.width || p.staging.height !== tex.height || p.staging.format !== tex.format) {
          if (p.staging) { try { p.staging.destroy(); } catch (e0) {} }
          p.staging = device.createTexture({ size: [tex.width, tex.height, 1], format: tex.format, usage: TB | CD | CS | RA });
        }
        dst = p.staging;
      }

      var w = Math.min(p.texW, tex.width), h = Math.min(p.texH, tex.height);
      var dest = { texture: dst, premultipliedAlpha: !!p.premultiply, colorSpace: 'srgb' };
      // The two entry points take differently shaped destinations (gpu_queue.idl):
      //   drawElementImageToTexture(GPUDrawElementImageSource, GPUDrawElementImageDestination)
      //     destination : GPUImageCopyTextureTagged + { size: GPUExtent3D }      -> texture at the top level
      //   copyElementImageToTexture(GPUCopyElementImageSource, GPUCopyElementImageDestination)
      //     destination : { destination: GPUImageCopyTextureTagged, width, height } -> texture nested
      // Each form is tried newest first; a TypeError means the shape was rejected, so fall through to the
      // next one. Any other exception (typically "no snapshot yet") is reported and retried next frame.
      var forms = [];
      if (typeof q.drawElementImageToTexture === 'function') {
        forms.push(function () {
          var d = { texture: dest.texture, premultipliedAlpha: dest.premultipliedAlpha, colorSpace: dest.colorSpace, size: [w, h, 1] };
          q.drawElementImageToTexture({ source: p.el }, d);
        });
        forms.push(function () { q.drawElementImageToTexture({ source: p.el }, { destination: dest, width: w, height: h }); });
      }
      if (typeof q.copyElementImageToTexture === 'function') {
        forms.push(function () { q.copyElementImageToTexture({ source: p.el }, { destination: dest, width: w, height: h }); });
        forms.push(function () { q.copyElementImageToTexture(p.el, w, h, { texture: dst }); });
      }
      if (forms.length === 0) return false;
      var lastErr = null;
      for (var i = 0; i < forms.length; i++) {
        try { forms[i](); lastErr = null; break; }
        catch (e) {
          lastErr = e;
          if (!(e instanceof TypeError)) break;
        }
      }
      if (lastErr) {
        HUI.warnOnce('gpuup', 'HTML-in-Canvas WebGPU upload failed (will retry): ' + lastErr);
        return false;
      }

      if (dst !== tex) {
        try {
          var enc = device.createCommandEncoder();
          enc.copyTextureToTexture({ texture: dst }, { texture: tex }, [tex.width, tex.height, 1]);
          q.submit([enc.finish()]);
        } catch (e2) {
          HUI.warnOnce('gpucopy', 'copyTextureToTexture into the Unity texture failed: ' + e2);
          return false;
        }
      }
      return true;
    },

    // ------------------------------------------------------------------ per frame

    update: function () {
      HUI.frame++;
      if (HUI.mode !== 1) { HUI.updateOverlay(); return; }

      var everyFrame = HUI.updateMode === 2 || (HUI.updateMode === 0 && !HUI.paintSeen && HUI.frame > 120);
      for (var id in HUI.panels) {
        var p = HUI.panels[id];
        if (!p.visible) continue;
        if (!(p.dirty || everyFrame)) continue;
        if (!p.glTex && !p.gpuPtr) continue;
        var ok = HUI.backend === 2 ? HUI.uploadGPU(p) : HUI.uploadGL(p);
        if (ok) { p.dirty = false; p.updated = true; } else HUI.requestPaint();
      }
    },

    // ------------------------------------------------------------------ misc

    announce: function (p, text, assertive) {
      if (!p.live) {
        var live = document.createElement('div');
        live.className = 'hui-live';
        live.setAttribute('role', 'status');
        p.el.appendChild(live);
        p.live = live;
      }
      p.live.setAttribute('aria-live', assertive ? 'assertive' : 'polite');
      p.live.textContent = '';
      var l = p.live;
      requestAnimationFrame(function () { l.textContent = text; });
    },

    // ------------------------------------------------------------------ page -> C#

    // HUI.send(name, payload) from page script. Delivered synchronously, like a DOM event.
    sendMessage: function (p, name, payload) {
      if (!HUI.messageCb || !HUI.panels[p.id]) return;
      var namePtr = HUI.cstr(name), dataPtr = HUI.cstr(HUI.stringify(payload));
      var cb = HUI.messageCb;
      try {
        {{{ makeDynCall('viii', 'cb') }}}(p.id, namePtr, dataPtr);
      } finally {
        _free(namePtr);
        _free(dataPtr);
      }
    },

    completeEval: function (panelId, requestId, failed, text) {
      if (!HUI.evalCb) return;
      var ptr = HUI.cstr(text);
      var cb = HUI.evalCb;
      try {
        {{{ makeDynCall('viiii', 'cb') }}}(panelId, requestId, ptr, failed ? 1 : 0);
      } finally {
        _free(ptr);
      }
    }
  },

  // ==================================================================== exported C API

  Hiccup_Init: function (backend, linear, forceOverlay, debug, eventCb, messageCb, evalCb) {
    HUI.eventCb = eventCb;
    HUI.messageCb = messageCb;
    HUI.evalCb = evalCb;
    return HUI.init(backend, !!linear, !!forceOverlay, !!debug);
  },

  Hiccup_GetFeatures: function () {
    return HUI.cstr(JSON.stringify(HUI.features || {}));
  },

  Hiccup_GetCanvasInfo: function (outPtr) {
    var c = HUI.canvas;
    var o = outPtr >> 2;
    HEAPF32[o] = c ? c.clientWidth : 0;
    HEAPF32[o + 1] = c ? c.clientHeight : 0;
    HEAPF32[o + 2] = HUI.dpr();
    HEAPF32[o + 3] = c ? c.width : 0;
    HEAPF32[o + 4] = c ? c.height : 0;
  },

  Hiccup_SetUpdateMode: function (mode) { HUI.updateMode = mode | 0; },
  Hiccup_SetGeometryMode: function (mode) {
    HUI.geometryMode = mode | 0;
    for (var id in HUI.panels) HUI.panels[id].lastMatrix = null; // re-apply with the new strategy
  },

  Hiccup_Update: function () { HUI.update(); },

  Hiccup_Free: function (ptr) { if (ptr) _free(ptr); },

  // ---- panels

  Hiccup_PanelCreate: function (w, h) { return HUI.createPanel(w, h); },
  Hiccup_PanelDestroy: function (id) { HUI.destroyPanel(id); },
  // The DOM exists the moment the panel does, so a panel is ready as soon as it is created.
  Hiccup_PanelIsReady: function (id) { return HUI.panel(id) ? 1 : 0; },
  Hiccup_PanelSetHtml: function (id, htmlPtr) { var p = HUI.panel(id); if (p) HUI.setHtml(p, UTF8ToString(htmlPtr)); },
  Hiccup_PanelSetCss: function (id, cssPtr) { var p = HUI.panel(id); if (p) HUI.setCss(p, UTF8ToString(cssPtr)); },
  Hiccup_PanelSetSize: function (id, w, h) { var p = HUI.panel(id); if (p) HUI.setSize(p, w, h); },
  Hiccup_PanelSetVisible: function (id, v) { var p = HUI.panel(id); if (p) HUI.setVisible(p, !!v); },
  Hiccup_PanelSetPointerMode: function (id, mode) {
    var p = HUI.panel(id); if (!p) return;
    p.el.setAttribute('data-hui-pointer', mode === 0 ? 'panel' : (mode === 1 ? 'children' : 'none'));
  },
  Hiccup_PanelSetBlockInput: function (id, block) { var p = HUI.panel(id); if (p) HUI.setBlockInput(p, !!block); },
  Hiccup_PanelSetPremultiplied: function (id, v) { var p = HUI.panel(id); if (p) { p.premultiply = !!v; p.dirty = true; } },
  // Texture density relative to the device pixel ratio (2 = supersample). Takes effect on the next SetSize.
  Hiccup_PanelSetResolutionScale: function (id, scale) { var p = HUI.panel(id); if (p) p.scale = Math.max(0.25, Math.min(4, scale || 1)); },
  Hiccup_PanelSetMipmaps: function (id, v) { var p = HUI.panel(id); if (p) { p.mipmaps = !!v; p.dirty = true; } },
  // Returns 1 once after each successful texture upload (WebGPU mip generation is done from C#).
  Hiccup_PanelTakeUpdated: function (id) { var p = HUI.panel(id); if (!p || !p.updated) return 0; p.updated = false; return 1; },
  Hiccup_PanelSetPreventSubmit: function (id, v) { var p = HUI.panel(id); if (p) p.preventSubmit = !!v; },
  Hiccup_PanelListen: function (id, typePtr, enabled) { var p = HUI.panel(id); if (p) HUI.listen(p, UTF8ToString(typePtr), !!enabled); },
  Hiccup_PanelInvalidate: function (id) { var p = HUI.panel(id); if (p) { p.dirty = true; HUI.requestPaint(); } },
  Hiccup_PanelSetGeometry: function (id, matPtr) {
    var p = HUI.panel(id); if (!p) return;
    HUI.setGeometry(p, HEAPF32.subarray(matPtr >> 2, (matPtr >> 2) + 16));
  },
  Hiccup_PanelGetTextureSize: function (id, outPtr) {
    var p = HUI.panel(id);
    HEAP32[outPtr >> 2] = p ? p.texW : 0;
    HEAP32[(outPtr >> 2) + 1] = p ? p.texH : 0;
  },
  // WebGL: the bridge owns the GL texture, Unity wraps it with Texture2D.CreateExternalTexture.
  Hiccup_PanelCreateGLTexture: function (id) { var p = HUI.panel(id); return p ? HUI.allocGLTexture(p) : 0; },
  // WebGPU: Unity owns the texture, the bridge draws into it.
  Hiccup_PanelBindGPUTexture: function (id, ptr) { var p = HUI.panel(id); if (p) { p.gpuPtr = ptr; p.dirty = true; HUI.requestPaint(); } },
  Hiccup_PanelAnnounce: function (id, textPtr, assertive) { var p = HUI.panel(id); if (p) HUI.announce(p, UTF8ToString(textPtr), !!assertive); },
  Hiccup_PanelEval: function (id, codePtr) {
    var p = HUI.panel(id); if (!p) return HUI.cstr('');
    try {
      var fn = new Function('panel', 'root', 'HUI', UTF8ToString(codePtr));
      var r = fn(p.el, p.content, p.api);
      p.dirty = true; HUI.requestPaint();
      return HUI.cstr(HUI.stringify(r));
    } catch (e) {
      console.error('[Hiccup] Eval failed: ' + e);
      return HUI.cstr('');
    }
  },
  // The body runs as an async function, so `await` is allowed; completion always arrives through evalCb on a
  // later turn, never from inside this call. A throw or rejection completes with failed = 1 and the message.
  Hiccup_PanelEvalAsync: function (id, codePtr, requestId) {
    var p = HUI.panel(id);
    // The task faults with 'Name: message', one line, the same text the Editor preview produces.
    var fail = function (e) {
      console.error('[Hiccup] EvalAsync failed: ' + e);
      HUI.completeEval(id, requestId, true, String(e).split('\n')[0]);
    };
    if (!p) { Promise.resolve().then(function () { fail('Unknown panel ' + id); }); return; }
    var result;
    try {
      var Ctor = HUI.asyncFunction();
      var fn = new Ctor('panel', 'root', 'HUI', UTF8ToString(codePtr));
      result = fn(p.el, p.content, p.api);
    } catch (e) {
      // A syntax error, or a synchronous throw when no AsyncFunction constructor exists.
      Promise.resolve().then(function () { fail(e); });
      return;
    }
    // Stringifying is its own step so a value JSON cannot encode (a cycle, a DOM node) fails the request too.
    Promise.resolve(result).then(function (r) { return HUI.stringify(r); }).then(function (text) {
      if (HUI.panels[id]) { p.dirty = true; HUI.requestPaint(); }
      HUI.completeEval(id, requestId, false, text);
    }, fail);
  },

  // ---- elements (handles)

  Hiccup_Query: function (id, selPtr) {
    var p = HUI.panel(id); if (!p) return 0;
    var sel = UTF8ToString(selPtr);
    var e = null;
    try { e = p.content.querySelector(sel); } catch (ex) { HUI.warnOnce('sel' + sel, 'Bad selector "' + sel + '": ' + ex); }
    return HUI.handle(e);
  },
  Hiccup_QueryAll: function (id, selPtr) {
    var p = HUI.panel(id); if (!p) return HUI.cstr('');
    var out = [];
    try {
      var list = p.content.querySelectorAll(UTF8ToString(selPtr));
      for (var i = 0; i < list.length; i++) out.push(HUI.handle(list[i]));
    } catch (ex) { HUI.warnOnce('selall', 'Bad selector: ' + ex); }
    return HUI.cstr(out.join(','));
  },
  Hiccup_ElemRelease: function (h) { HUI.releaseHandle(h); },
  Hiccup_ElemEnsureId: function (h) { var e = HUI.el(h); return HUI.cstr(e ? HUI.ensureId(e) : ''); },
  Hiccup_ElemGetText: function (h) { var e = HUI.el(h); return HUI.cstr(e ? e.textContent : ''); },
  Hiccup_ElemSetText: function (h, sPtr) { var e = HUI.el(h); if (e) e.textContent = UTF8ToString(sPtr); },
  Hiccup_ElemGetHtml: function (h) { var e = HUI.el(h); return HUI.cstr(e ? e.innerHTML : ''); },
  Hiccup_ElemSetHtml: function (h, sPtr) { var e = HUI.el(h); if (e) e.innerHTML = UTF8ToString(sPtr); },
  Hiccup_ElemInsertHtml: function (h, wherePtr, sPtr) {
    var e = HUI.el(h); if (e) e.insertAdjacentHTML(UTF8ToString(wherePtr), UTF8ToString(sPtr));
  },
  Hiccup_ElemGetAttr: function (h, nPtr) { var e = HUI.el(h); var v = e ? e.getAttribute(UTF8ToString(nPtr)) : null; return HUI.cstr(v === null ? '' : v); },
  Hiccup_ElemHasAttr: function (h, nPtr) { var e = HUI.el(h); return (e && e.hasAttribute(UTF8ToString(nPtr))) ? 1 : 0; },
  Hiccup_ElemSetAttr: function (h, nPtr, vPtr) { var e = HUI.el(h); if (e) e.setAttribute(UTF8ToString(nPtr), UTF8ToString(vPtr)); },
  Hiccup_ElemRemoveAttr: function (h, nPtr) { var e = HUI.el(h); if (e) e.removeAttribute(UTF8ToString(nPtr)); },
  Hiccup_ElemGetProp: function (h, nPtr) { var e = HUI.el(h); var v = e ? e[UTF8ToString(nPtr)] : undefined; return HUI.cstr(v === undefined || v === null ? '' : String(v)); },
  Hiccup_ElemSetProp: function (h, nPtr, vPtr) { var e = HUI.el(h); if (e) e[UTF8ToString(nPtr)] = UTF8ToString(vPtr); },
  Hiccup_ElemSetBoolProp: function (h, nPtr, v) { var e = HUI.el(h); if (e) e[UTF8ToString(nPtr)] = !!v; },
  Hiccup_ElemGetBoolProp: function (h, nPtr) { var e = HUI.el(h); return (e && !!e[UTF8ToString(nPtr)]) ? 1 : 0; },
  Hiccup_ElemSetStyle: function (h, nPtr, vPtr) { var e = HUI.el(h); if (e) e.style.setProperty(UTF8ToString(nPtr), UTF8ToString(vPtr)); },
  Hiccup_ElemGetStyle: function (h, nPtr) { var e = HUI.el(h); return HUI.cstr(e ? getComputedStyle(e).getPropertyValue(UTF8ToString(nPtr)) : ''); },
  Hiccup_ElemAddClass: function (h, cPtr) { var e = HUI.el(h); if (e) e.classList.add(UTF8ToString(cPtr)); },
  Hiccup_ElemRemoveClass: function (h, cPtr) { var e = HUI.el(h); if (e) e.classList.remove(UTF8ToString(cPtr)); },
  Hiccup_ElemToggleClass: function (h, cPtr, force) { var e = HUI.el(h); if (e) e.classList.toggle(UTF8ToString(cPtr), force < 0 ? undefined : !!force); },
  Hiccup_ElemHasClass: function (h, cPtr) { var e = HUI.el(h); return (e && e.classList.contains(UTF8ToString(cPtr))) ? 1 : 0; },
  Hiccup_ElemFocus: function (h) { var e = HUI.el(h); if (e && e.focus) e.focus({ preventScroll: true }); },
  Hiccup_ElemBlur: function (h) { var e = HUI.el(h); if (e && e.blur) e.blur(); },
  Hiccup_ElemClick: function (h) { var e = HUI.el(h); if (e && e.click) e.click(); },
  Hiccup_ElemRemove: function (h) { var e = HUI.el(h); if (e && e.parentNode) e.parentNode.removeChild(e); HUI.releaseHandle(h); },
  Hiccup_ElemShowModal: function (h, v) {
    var e = HUI.el(h); if (!e) return;
    try { if (v) { if (typeof e.showModal === 'function' && !e.open) e.showModal(); else e.setAttribute('open', ''); } else { if (typeof e.close === 'function') e.close(); else e.removeAttribute('open'); } }
    catch (ex) { HUI.warnOnce('modal', 'showModal failed: ' + ex); }
  },
  Hiccup_ElemGetBounds: function (h, outPtr) {
    var e = HUI.el(h);
    var o = outPtr >> 2;
    if (!e) { HEAPF32[o] = HEAPF32[o + 1] = HEAPF32[o + 2] = HEAPF32[o + 3] = 0; return; }
    var pr = (typeof e.closest === 'function' ? e.closest('.hui-panel') : null) || e.parentElement;
    var r = e.getBoundingClientRect(), b = pr ? pr.getBoundingClientRect() : { left: 0, top: 0 };
    HEAPF32[o] = r.left - b.left; HEAPF32[o + 1] = r.top - b.top; HEAPF32[o + 2] = r.width; HEAPF32[o + 3] = r.height;
  },
  Hiccup_ElemQuery: function (h, selPtr) {
    var e = HUI.el(h); if (!e) return 0;
    var r = null;
    try { r = e.querySelector(UTF8ToString(selPtr)); } catch (ex) {}
    return HUI.handle(r);
  },
  Hiccup_ElemParent: function (h) { var e = HUI.el(h); return HUI.handle(e && e.parentElement && !e.parentElement.classList.contains('hui-content') ? e.parentElement : null); },
  Hiccup_ElemMatches: function (h, selPtr) { var e = HUI.el(h); try { return (e && e.matches(UTF8ToString(selPtr))) ? 1 : 0; } catch (ex) { return 0; } },
  Hiccup_ElemScrollIntoView: function (h) { var e = HUI.el(h); if (e && e.scrollIntoView) e.scrollIntoView({ block: 'nearest' }); }
};

autoAddDeps(HiccupLibrary, '$HUI');
mergeInto(LibraryManager.library, HiccupLibrary);
