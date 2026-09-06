namespace Hiccup.Editor.Cdp
{
    /// <summary>
    /// The script injected into each preview page. It builds the same DOM shape as <c>Hiccup.jslib</c>
    /// (<c>.hui-panel &gt; style + .hui-content</c>) and emits the same event payload, so HTML and CSS
    /// authored against the runtime behave identically in the Editor preview.
    /// </summary>
    /// <remarks>
    /// Everything the jslib does with WebGL/WebGPU textures and canvas geometry is absent here: the page
    /// is captured by DevTools instead of composited into the Unity canvas. Written without double quotes
    /// so it can live in a C# verbatim string.
    /// </remarks>
    internal static class CdpBridgeJs
    {
        /// <summary>Name of the DevTools binding the page calls to deliver DOM events to Unity.</summary>
        public const string EventBinding = "HUI_Event";
        /// <summary>Name of the DevTools binding <c>HUI.send</c> delivers page messages through, as <c>{name, data}</c> JSON.</summary>
        public const string MessageBinding = "HUI_Message";

        public const string Source = @"
(function () {
  if (window.__HUI) return;

  var HUI = {
    panelId: 0,
    panel: null,
    style: null,
    content: null,
    live: null,
    idCounter: 0,
    listeners: {},
    images: {},
    preventSubmit: true,
    DEFAULT_EVENTS: ['click', 'dblclick', 'input', 'change', 'submit', 'keydown', 'focusin', 'focusout'],

    init: function (panelId) {
      HUI.panelId = panelId | 0;
      var d = document;
      var page = 'margin:0;padding:0;height:100%;background:transparent;overflow:hidden;';
      d.documentElement.style.cssText = page;
      d.body.style.cssText = page;

      var base = d.createElement('style');
      base.setAttribute('data-hui', 'base');
      base.textContent =
        '.hui-panel{position:absolute;left:0;top:0;width:100%;height:100%;margin:0;padding:0;box-sizing:border-box;' +
        'overflow:hidden;transform-origin:0 0;background:transparent;isolation:isolate;}' +
        '.hui-panel[hidden]{display:none!important;}' +
        '.hui-content{width:100%;height:100%;box-sizing:border-box;}' +
        '.hui-panel[data-hui-pointer=none]{pointer-events:none;}' +
        '.hui-panel[data-hui-pointer=none] *{pointer-events:none;}' +
        '.hui-panel[data-hui-pointer=children]{pointer-events:none;}' +
        '.hui-panel[data-hui-pointer=children] .hui-content{pointer-events:none;}' +
        '.hui-panel[data-hui-pointer=children] .hui-content>*{pointer-events:auto;}' +
        '.hui-live{position:absolute;width:1px;height:1px;margin:-1px;padding:0;overflow:hidden;' +
        'clip:rect(0 0 0 0);white-space:nowrap;border:0;}';
      d.head.appendChild(base);

      var el = d.createElement('div');
      el.className = 'hui-panel';
      el.setAttribute('data-hui-panel', String(HUI.panelId));
      el.setAttribute('data-hui-pointer', 'children');

      HUI.style = d.createElement('style');
      HUI.style.setAttribute('data-hui', 'panel-style');
      el.appendChild(HUI.style);

      HUI.content = d.createElement('div');
      HUI.content.className = 'hui-content';
      el.appendChild(HUI.content);

      d.body.appendChild(el);
      HUI.panel = el;

      HUI.live = d.createElement('div');
      HUI.live.className = 'hui-live';
      HUI.live.setAttribute('aria-live', 'polite');
      d.body.appendChild(HUI.live);

      for (var i = 0; i < HUI.DEFAULT_EVENTS.length; i++) HUI.listen(HUI.DEFAULT_EVENTS[i], true);
      HUI.observeImages();
      return true;
    },

    // ---------------------------------------------------------------- images
    // Mirrors the jslib, with data: URLs from the backend instead of blob: URLs.

    setImage: function (name, url) {
      if (url) HUI.images[name] = url; else delete HUI.images[name];
      if (url) HUI.panel.style.setProperty('--hui-image-' + name, 'url(' + JSON.stringify(url) + ')');
      else HUI.panel.style.removeProperty('--hui-image-' + name);
      var els = HUI.content.querySelectorAll('[data-hui-image=' + JSON.stringify(name) + ']');
      for (var i = 0; i < els.length; i++) HUI.applyImage(els[i], url);
    },
    applyImage: function (el, url) {
      if (el.tagName === 'IMG') { if (url) el.src = url; else el.removeAttribute('src'); }
      else el.style.backgroundImage = url ? 'url(' + JSON.stringify(url) + ')' : '';
    },
    bindImage: function (el) {
      var name = el.getAttribute('data-hui-image');
      if (name && HUI.images[name]) HUI.applyImage(el, HUI.images[name]);
    },
    observeImages: function () {
      var observer = new MutationObserver(function (records) {
        for (var r = 0; r < records.length; r++) {
          var rec = records[r];
          if (rec.type === 'attributes') { HUI.bindImage(rec.target); continue; }
          for (var n = 0; n < rec.addedNodes.length; n++) {
            var node = rec.addedNodes[n];
            if (node.nodeType !== 1) continue;
            if (node.hasAttribute('data-hui-image')) HUI.bindImage(node);
            var list = node.querySelectorAll('[data-hui-image]');
            for (var i = 0; i < list.length; i++) HUI.bindImage(list[i]);
          }
        }
      });
      observer.observe(HUI.content, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-hui-image'] });
    },

    ensureId: function (el) {
      if (!el.id) el.id = 'hui-' + (++HUI.idCounter);
      return el.id;
    },

    setHtml: function (html) { HUI.content.innerHTML = html; },
    setCss: function (css) { HUI.style.textContent = css; },
    setPointerMode: function (mode) {
      HUI.panel.setAttribute('data-hui-pointer', mode === 2 ? 'none' : (mode === 1 ? 'children' : 'panel'));
    },
    setVisible: function (visible) {
      if (visible) HUI.panel.removeAttribute('hidden'); else HUI.panel.setAttribute('hidden', '');
      HUI.panel.setAttribute('aria-hidden', visible ? 'false' : 'true');
    },
    announce: function (text, assertive) {
      HUI.live.setAttribute('aria-live', assertive ? 'assertive' : 'polite');
      HUI.live.textContent = '';
      window.setTimeout(function () { HUI.live.textContent = text; }, 10);
    },

    // ---------------------------------------------------------------- Eval / EvalAsync / send
    // Mirrors the jslib: a value crosses back as a string as-is, anything else as JSON, undefined as ''.

    stringify: function (r) {
      return r === undefined ? '' : (typeof r === 'string' ? r : JSON.stringify(r));
    },
    run: function (fn) { return HUI.stringify(fn(HUI.panel, HUI.content, HUI)); },
    runAsync: function (fn) { return Promise.resolve(fn(HUI.panel, HUI.content, HUI)).then(HUI.stringify); },
    send: function (name, payload) {
      if (typeof window.HUI_Message !== 'function') return;
      try { window.HUI_Message(JSON.stringify({ name: String(name), data: HUI.stringify(payload) })); } catch (err) { }
    },

    // ---------------------------------------------------------------- elements
    // Unity holds handles that are descriptions, not pointers: {s: selector, i: index, up: parent, p: parent spec}.
    // Resolving on every operation costs a querySelector and keeps the page from accumulating stale references.

    resolve: function (spec) {
      if (!spec) return null;
      var base = spec.p ? HUI.resolve(spec.p) : HUI.content;
      if (!base) return null;
      if (spec.up) return base.parentElement;
      if (!spec.s) return base;
      if (spec.i !== undefined && spec.i !== null) return base.querySelectorAll(spec.s)[spec.i] || null;
      return base.querySelector(spec.s);
    },

    // One batch of writes per Unity frame; ops that resolve to nothing are skipped, as they are in the jslib.
    apply: function (ops) {
      for (var i = 0; i < ops.length; i++) {
        var op = ops[i];
        var el = HUI.resolve(op.h);
        if (el) HUI.write(el, op);
      }
      return ops.length;
    },

    write: function (el, op) {
      var a = op.a, b = op.b;
      switch (op.o) {
        case 'text': el.textContent = a; break;
        case 'html': el.innerHTML = a; break;
        case 'insert': el.insertAdjacentHTML(a, b); break;
        case 'attr': el.setAttribute(a, b); break;
        case 'rmattr': el.removeAttribute(a); break;
        case 'prop': el[a] = b; break;
        case 'boolprop': el[a] = !!b; break;
        case 'style': el.style.setProperty(a, b); break;
        case 'addcls': el.classList.add(a); break;
        case 'rmcls': el.classList.remove(a); break;
        case 'tglcls': if (b === -1) el.classList.toggle(a); else el.classList.toggle(a, !!b); break;
        case 'focus': if (el.focus) el.focus(); break;
        case 'blur': if (el.blur) el.blur(); break;
        case 'click': if (el.click) el.click(); break;
        case 'remove': if (el.parentNode) el.parentNode.removeChild(el); break;
        case 'scroll': if (el.scrollIntoView) el.scrollIntoView({ block: 'nearest' }); break;
        case 'modal':
          if (a) { if (!el.open && el.showModal) el.showModal(); }
          else if (el.open && el.close) el.close();
          break;
      }
    },

    read: function (spec, op, a) {
      if (op === 'count') {
        var scope = spec.p ? HUI.resolve(spec.p) : HUI.content;
        return scope ? scope.querySelectorAll(spec.s).length : 0;
      }

      var el = HUI.resolve(spec);
      if (!el) return op === 'hasattr' || op === 'boolprop' || op === 'hascls' || op === 'matches' ? false : '';

      switch (op) {
        case 'exists': return true;
        case 'ensureid': return HUI.ensureId(el);
        case 'text': return el.textContent;
        case 'html': return el.innerHTML;
        case 'attr': var v = el.getAttribute(a); return v === null ? '' : v;
        case 'hasattr': return el.hasAttribute(a);
        case 'prop': var p = el[a]; return (p === undefined || p === null) ? '' : String(p);
        case 'boolprop': return !!el[a];
        case 'style': return window.getComputedStyle(el).getPropertyValue(a);
        case 'hascls': return el.classList.contains(a);
        case 'matches': return !!(el.matches && el.matches(a));
        case 'bounds':
          var r = el.getBoundingClientRect(), pr = HUI.panel.getBoundingClientRect();
          return [r.left - pr.left, r.top - pr.top, r.width, r.height].join(',');
      }
      return '';
    },

    listen: function (type, enabled) {
      if (enabled) {
        if (HUI.listeners[type]) return;
        var f = function (e) { HUI.onDomEvent(e); };
        HUI.listeners[type] = f;
        HUI.panel.addEventListener(type, f);
      } else if (HUI.listeners[type]) {
        HUI.panel.removeEventListener(type, HUI.listeners[type]);
        delete HUI.listeners[type];
      }
    },

    // Mirrors HUI.onDomEvent in Hiccup.jslib: the payload must deserialize into Hiccup.HtmlEvent.
    onDomEvent: function (e) {
      if (e.type === 'submit' && HUI.preventSubmit) e.preventDefault();

      var t = e.target;
      if (!t || !(t instanceof Element)) t = HUI.content;
      if (t === HUI.panel) t = HUI.content;
      HUI.ensureId(t);

      var path = [];
      var n = t.parentElement;
      while (n && n !== HUI.panel) { if (n.id) path.push(n.id); n = n.parentElement; }

      var actionEl = (typeof t.closest === 'function') ? t.closest('[data-action]') : null;
      if (actionEl && !HUI.panel.contains(actionEl)) actionEl = null;

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
        var r = HUI.panel.getBoundingClientRect();
        o.x = e.clientX - r.left;
        o.y = e.clientY - r.top;
        o.button = (e.button === undefined) ? -1 : e.button;
      }

      var dsMap = {};
      if (actionEl && actionEl.dataset) for (var ka in actionEl.dataset) dsMap[ka] = actionEl.dataset[ka];
      if (t.dataset) for (var kt in t.dataset) dsMap[kt] = t.dataset[kt];
      var ds = [];
      for (var kd in dsMap) ds.push(kd + '=' + dsMap[kd]);
      o.dataset = ds.join('\n');

      if (typeof window.HUI_Event === 'function') {
        try { window.HUI_Event(JSON.stringify(o)); } catch (err) { }
      }
    }
  };

  window.__HUI = HUI;
})();
";
    }
}
