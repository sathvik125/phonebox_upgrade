// Container queries for Android System WebViews older than Chromium 105.
//
// Widgets size their text, padding and dials in container query units (cqh, cqw, cqmin) and hide parts of
// themselves with @container rules. A WebView that doesn't know them drops every such declaration and rule, so the
// cards fall back to the browser's default sizes: big labels, cut-off subtitles, parts that should hide don't.
// Android 5 stops at WebView 95, and some Android 6 phones (a vivo 1606) are stuck there too.
//
// On those WebViews this reads the panel's own stylesheets once, while the page is still parsing, and swaps them for
// a rewritten copy that keeps the same order:
//   - 12cqh becomes calc(12 * var(--cqh)), and the variables are set from each container's content box, measured
//     with a ResizeObserver. They are set on the container's children, so a container's own padding still follows
//     the container around it, as the spec says.
//   - @container (max-height: 80px) { .w-clock .date { … } } becomes .w-clock[data-cq~="3"] .date { … } (and the
//     same with the mark on an ancestor), and data-cq on each container lists the queries it matches right now.
//   - container-type is removed, so the layout is the same one the old WebView would have drawn anyway (and the
//     fallback can be tried in a current browser with ?cqcompat).
// Loaded as a classic script right after the stylesheet links, before anything renders.
(function () {
  'use strict';
  try {
    var forced = /[?&]cqcompat(?:[=&]|$)/.test(location.search);
    if (!forced && window.CSS && typeof CSS.supports === 'function' && CSS.supports('font-size', '1cqw')) return;
    if (typeof ResizeObserver !== 'function' || typeof MutationObserver !== 'function') return;

    var SHEET = /(?:^|\/)(style|controls|appearance)\.css(?:[?#]|$)/;
    var containers = []; // { sel, inline }
    var queries = []; // [{ min, axis, px }]

    function readText(href) {
      var xhr = new XMLHttpRequest();
      xhr.open('GET', href, false); // synchronous on purpose: the swap has to land before the first paint
      xhr.send(null);
      return xhr.status === 200 || (xhr.status === 0 && xhr.responseText) ? xhr.responseText : null;
    }

    // index of the brace that closes the one at `open`, skipping strings
    function closing(text, open) {
      var depth = 0, quote = null;
      for (var i = open; i < text.length; i++) {
        var c = text.charAt(i);
        if (quote) { if (c === '\\') i++; else if (c === quote) quote = null; continue; }
        if (c === '"' || c === "'") quote = c;
        else if (c === '{') depth++;
        else if (c === '}' && --depth === 0) return i;
      }
      return text.length - 1;
    }

    // top-level split, ignoring separators inside (), [] and strings
    function splitTop(text, sep) {
      var parts = [], cur = '', depth = 0, quote = null;
      for (var i = 0; i < text.length; i++) {
        var c = text.charAt(i);
        if (quote) { cur += c; if (c === '\\') { cur += text.charAt(++i); } else if (c === quote) quote = null; continue; }
        if (c === '"' || c === "'") quote = c;
        else if (c === '(' || c === '[') depth++;
        else if (c === ')' || c === ']') depth--;
        else if (c === sep && depth === 0) { parts.push(cur); cur = ''; continue; }
        cur += c;
      }
      parts.push(cur);
      return parts;
    }

    // ".a .b > .c" -> [{ c: '.a' }, { k: ' ' }, { c: '.b' }, { k: '>' }, { c: '.c' }]
    function compounds(sel) {
      var out = [], cur = '', depth = 0, quote = null, pending = null;
      for (var i = 0; i < sel.length; i++) {
        var c = sel.charAt(i);
        if (quote) { cur += c; if (c === '\\') { cur += sel.charAt(++i); } else if (c === quote) quote = null; continue; }
        var sep = depth === 0 && (c === ' ' || c === '\n' || c === '\t' || c === '\r' || c === '>' || c === '+' || c === '~');
        if (sep) {
          if (cur) { out.push({ c: cur }); cur = ''; }
          if (c === '>' || c === '+' || c === '~') pending = c; else if (!pending) pending = ' ';
          continue;
        }
        if (!cur && out.length && pending) out.push({ k: pending });
        pending = null;
        if (c === '"' || c === "'") quote = c;
        else if (c === '(' || c === '[') depth++;
        else if (c === ')' || c === ']') depth--;
        cur += c;
      }
      if (cur) out.push({ c: cur });
      return out;
    }

    function join(parts) {
      return parts.map(function (p) { return p.c !== undefined ? p.c : (p.k === ' ' ? ' ' : ' ' + p.k + ' '); }).join('');
    }

    // The query's container is the nearest container around the element the selector picks, so the mark can sit on
    // any compound before the last one, or on an element above them all.
    function markSelectors(list, mark) {
      var out = [];
      splitTop(list, ',').forEach(function (raw) {
        var sel = raw.trim();
        if (!sel) return;
        var parts = compounds(sel);
        out.push(mark + ' ' + sel);
        var seen = 0, last = parts.filter(function (p) { return p.c !== undefined; }).length - 1;
        for (var i = 0; i < parts.length; i++) {
          if (parts[i].c === undefined) continue;
          if (seen++ >= last) break;
          var copy = parts.slice();
          copy[i] = { c: parts[i].c + mark };
          out.push(join(copy));
        }
      });
      return out.join(',');
    }

    function markBlock(inner, mark) {
      var out = '', i = 0;
      for (;;) {
        var open = inner.indexOf('{', i);
        if (open < 0) break;
        var sel = inner.slice(i, open).trim(), end = closing(inner, open), body = inner.slice(open + 1, end);
        out += sel.charAt(0) === '@' ? sel + '{' + markBlock(body, mark) + '}' : markSelectors(sel, mark) + '{' + body + '}';
        i = end + 1;
      }
      return out;
    }

    function parseQuery(prelude) {
      var conds = [], rest = prelude.replace(/^[\w-]+(?=\s*\()/, ''); // a container name is ignored: there are none
      rest = rest.replace(/\(\s*(min|max)-(width|height)\s*:\s*([\d.]+)px\s*\)/g, function (_, m, axis, px) {
        conds.push({ min: m === 'min', axis: axis, px: parseFloat(px) });
        return '';
      });
      return conds.length && !rest.replace(/\band\b/g, '').trim() ? conds : null;
    }

    function rewrite(text) {
      text = text.replace(/\/\*[\s\S]*?\*\//g, '');
      text.replace(/([^{}]+)\{([^{}]*)\}/g, function (_, sel, body) {
        var m = /container-type\s*:\s*(inline-size|size)/.exec(body);
        if (m) containers.push({ sel: sel.trim(), inline: m[1] === 'inline-size' });
        return '';
      });
      var out = '', i = 0;
      for (;;) {
        var at = text.indexOf('@container', i);
        if (at < 0) break;
        var open = text.indexOf('{', at), end = closing(text, open);
        var conds = parseQuery(text.slice(at + 10, open).trim());
        out += text.slice(i, at);
        if (conds) {
          queries.push(conds);
          out += markBlock(text.slice(open + 1, end), '[data-cq~="' + (queries.length - 1) + '"]');
        }
        i = end + 1;
      }
      out += text.slice(i);
      out = out.replace(/(-?(?:\d+\.?\d*|\.\d+))(cqmin|cqmax|cqw|cqh|cqi|cqb)\b/g, function (_, n, unit) {
        return 'calc(' + n + ' * var(--' + ({ cqi: 'cqw', cqb: 'cqh' }[unit] || unit) + '))';
      });
      return out.replace(/container-type\s*:\s*[^;}]+;?/g, '');
    }

    var links = Array.prototype.slice.call(document.querySelectorAll('link[rel="stylesheet"]'));
    var swapped = 0;
    links.forEach(function (link) {
      var href = link.getAttribute('href') || '';
      if (!SHEET.test(href)) return;
      var text;
      try { text = readText(href); } catch (_) { text = null; }
      if (text == null) return; // keep the original: unscaled beats unstyled
      var style = document.createElement('style');
      style.setAttribute('data-href', href);
      style.textContent = (swapped ? '' : ':root{--cqw:1vw;--cqh:1vh;--cqmin:1vmin;--cqmax:1vmax}\n') + rewrite(text);
      link.parentNode.replaceChild(style, link);
      swapped++;
    });
    if (!swapped || !containers.length) return;
    window.__pbCqCompat = true;

    var selector = containers.map(function (c) { return c.sel; }).join(',');
    var vars = document.createElement('style');
    vars.id = 'pb-cq-vars';
    document.head.appendChild(vars);

    var tracked = {}; // id -> { el, inline, rule }
    var nextId = 1, pending = [], frame = 0;
    function later() {
      // applied right after the callback, not inside it: a change that resizes a container again would otherwise
      // trip "ResizeObserver loop limit exceeded", which the panel reports as an error. A timer, not a frame: a
      // WebView that isn't drawing (in the background, behind another app) never runs animation frames.
      if (!frame) frame = setTimeout(apply, 0);
    }
    var ro = new ResizeObserver(function (entries) {
      entries.forEach(function (e) { pending.push({ el: e.target, w: e.contentRect.width, h: e.contentRect.height }); });
      later();
    });

    // A ResizeObserver reports on the next rendering step only, which a WebView that isn't drawing never takes: a
    // container found in the DOM is measured straight away, so it doesn't sit on the viewport fallback until then.
    function measure(el) {
      var cs = getComputedStyle(el);
      return {
        w: Math.max(0, el.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight)),
        h: Math.max(0, el.clientHeight - parseFloat(cs.paddingTop) - parseFloat(cs.paddingBottom)),
      };
    }

    function isInline(el) {
      for (var i = 0; i < containers.length; i++) {
        try { if (!containers[i].inline && el.matches(containers[i].sel)) return false; } catch (_) {}
      }
      return true;
    }

    function track(el) {
      var known = tracked[el.getAttribute('data-cqid')];
      if (known && known.el === el) return;
      // a clone (cloneNode copies the attribute) or an element that was pruned gets an entry of its own
      var id = String(nextId++);
      el.setAttribute('data-cqid', id);
      tracked[id] = { el: el, inline: isInline(el), rule: '' };
      ro.observe(el);
      pending.push({ el: el, w: null, h: null });
      later();
    }

    function scan(root) {
      if (root.nodeType !== 1) return;
      try { if (root.matches(selector)) track(root); } catch (_) { return; }
      var found = root.querySelectorAll(selector);
      for (var i = 0; i < found.length; i++) track(found[i]);
    }

    function px(n) { return (Math.round(n * 100) / 10000) + 'px'; } // a hundredth of n, so 1cqw is 1% of the width

    function apply() {
      frame = 0;
      var batch = pending; pending = [];
      var changed = false;
      // read every size first, then write: a write between two reads would force a layout per container
      batch.forEach(function (e) {
        if (e.w === null && e.el.isConnected) { var m = measure(e.el); e.w = m.w; e.h = m.h; }
      });
      batch.forEach(function (e) {
        var id = e.el.getAttribute('data-cqid'), t = id && tracked[id];
        if (!t || t.el !== e.el || e.w === null) return;
        var w = e.w, h = e.h;
        var sel = '[data-cqid="' + id + '"]';
        var decl = t.inline
          ? '--cqw:' + px(w) + ';--cqmin:min(' + px(w) + ',var(--cqh));--cqmax:max(' + px(w) + ',var(--cqh))'
          : '--cqw:' + px(w) + ';--cqh:' + px(h) + ';--cqmin:' + px(Math.min(w, h)) + ';--cqmax:' + px(Math.max(w, h));
        var rule = sel + '>*,' + sel + '::before,' + sel + '::after{' + decl + '}';
        if (rule !== t.rule) { t.rule = rule; changed = true; }
        var hits = [];
        for (var q = 0; q < queries.length; q++) {
          var ok = queries[q].every(function (c) {
            if (c.axis === 'height' && t.inline) return false; // an inline-size container answers width only
            var v = c.axis === 'width' ? w : h;
            return c.min ? v >= c.px : v <= c.px;
          });
          if (ok) hits.push(q);
        }
        var list = hits.join(' ');
        if (t.el.getAttribute('data-cq') !== list) { t.el.setAttribute('data-cq', list); changed = true; }
      });
      if (!changed) { settle = 0; return; }
      write();
      // what was just written can resize a container inside another one: measure again until nothing moves
      if (++settle < 3) {
        for (var id in tracked) pending.push({ el: tracked[id].el, w: null, h: null });
        later();
      } else settle = 0;
    }
    var settle = 0;

    function write() {
      var rules = [];
      for (var id in tracked) if (tracked[id].rule) rules.push(tracked[id].rule);
      vars.textContent = rules.join('\n');
    }

    var sweep = 0;
    function prune() {
      sweep = 0;
      var gone = false;
      for (var id in tracked) {
        if (tracked[id].el.isConnected) continue;
        ro.unobserve(tracked[id].el);
        delete tracked[id];
        gone = true;
      }
      if (gone) write();
    }

    new MutationObserver(function (records) {
      var removed = false;
      for (var r = 0; r < records.length; r++) {
        var added = records[r].addedNodes;
        for (var i = 0; i < added.length; i++) scan(added[i]);
        if (records[r].removedNodes.length) removed = true;
      }
      if (removed && !sweep) sweep = setTimeout(prune, 1000); // later, so a moved widget keeps its entry
    }).observe(document.documentElement, { childList: true, subtree: true });
    if (document.body) scan(document.body);
  } catch (e) {
    try { console.warn('cq-compat', e); } catch (_) {}
  }
})();
