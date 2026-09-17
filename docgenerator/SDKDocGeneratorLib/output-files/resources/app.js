/* =====================================================================
   AWS SDK for .NET — API Reference client runtime
   ---------------------------------------------------------------------
   The whole client runtime in one file, loaded with `defer` after
   search-core.js (the scorer shared with search-worker.js) — no build
   step, no module system. Sections, in order:
     core      — shared constants, theme, DOM/URL helpers.
     sidebar   — toc.json hydration, filter, active-sync, mobile drawer.
     search    — local search modal, member worker, external-scope search.
     page      — per-page init on load and on every htmx navigation.
   Only two globals are exposed for inline handlers on generated markup:
   window.AWSHelpObj (topbar form onsubmit) and window.toggleTOC.
   ===================================================================== */
(function () {
  "use strict";


  /* ----------------------------- Constants -------------------------- */
  var THEME_KEY = "awsdocs-theme";
  // The default search scope ("Documentation - This Guide") is handled by the
  // local search modal; every other scope escalates to the external AWS search.
  var LOCAL_SCOPE = "documentation-guide";
  // The AWS documentation search lives on docs.aws.amazon.com — or, for the
  // China partition, docs.amazonaws.cn — so the external-scope search targets
  // the partition's docs host (not the current origin — on a local/preview
  // server the /search endpoint does not exist).
  var DOCS_BASE = /\.cn$/i.test(window.location.host || "")
    ? "https://docs.amazonaws.cn"
    : "https://docs.aws.amazon.com";

  // Asset fingerprint, recovered from this script's own ?v= (DocShell stamps
  // every CSS/JS link with it). Used to version runtime-constructed asset URLs
  // (the search worker) so they can't go stale behind a CDN either.
  var ASSET_V = (function () {
    try {
      var src = document.currentScript && document.currentScript.src;
      var m = src && src.match(/[?&]v=([0-9a-f]+)/i);
      return m ? m[1] : "";
    } catch (e) { return ""; }
  })();

  // Data fingerprint for the runtime-fetched data files (toc.json,
  // search-index.json, _sdk-versions.json), emitted by the generator on
  // <body data-datav>. Separate from ASSET_V: the data changes every SDK
  // release while the static assets usually don't, so the asset hash could
  // never bust a stale toc.json. DATA_VQ is the ready-to-append "?v=…".
  var DATA_V = (function () {
    try {
      return (document.body && document.body.getAttribute("data-datav")) || "";
    } catch (e) { return ""; }
  })();
  var DATA_VQ = DATA_V ? "?v=" + DATA_V : "";

  /* ------------------------------- Theme ---------------------------- */
  function preferredTheme() {
    try {
      var saved = localStorage.getItem(THEME_KEY);
      if (saved === "light" || saved === "dark") return saved;
    } catch (e) { /* private mode */ }
    return (window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches)
      ? "dark" : "light";
  }

  function applyTheme(theme) {
    document.documentElement.setAttribute("data-theme", theme);
    // The toggle is a pressed-state button ("Dark theme"); without this a
    // screen reader hears the same announcement in both themes and can't tell
    // whether the toggle worked.
    var btn = document.getElementById("themeToggle");
    if (btn) btn.setAttribute("aria-pressed", theme === "dark" ? "true" : "false");
  }

  function toggleTheme() {
    var next = document.documentElement.getAttribute("data-theme") === "dark" ? "light" : "dark";
    applyTheme(next);
    try { localStorage.setItem(THEME_KEY, next); } catch (e) { /* ignore */ }
  }

  // The inline snippet in <head> already set data-theme pre-paint (no FOUC);
  // calling applyTheme again here initializes #themeToggle's aria-pressed,
  // which the snippet doesn't touch.
  applyTheme(preferredTheme());

  // Follow OS theme changes at runtime — but only while the user hasn't made an
  // explicit choice (a stored value always wins, matching preferredTheme()).
  try {
    var mq = window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)");
    if (mq && mq.addEventListener) {
      mq.addEventListener("change", function (ev) {
        try { if (localStorage.getItem(THEME_KEY)) return; } catch (e) { /* private mode */ }
        applyTheme(ev.matches ? "dark" : "light");
      });
    }
  } catch (e) { /* matchMedia unavailable */ }

  /* --------------------------- Doc-set root ------------------------- */
  // Resolved once and cached: every page's data-root resolves to the same
  // absolute doc-set root, so it is stable across htmx swaps.
  var rootAbs = null;

  // Parsed toc.json (namespaces + types), fetched once by the sidebar and
  // shared with the search modal. Survives htmx swaps in module state.
  var tocData = null;

  function docRoot() {
    // RootRelativePath emitted on <body data-root>; resolve to an absolute
    // URL so sidebar/search links stay correct after htmx swaps the content.
    var rel = (document.body && document.body.getAttribute("data-root")) || ".";
    return new URL(rel.replace(/\/?$/, "/"), window.location.href);
  }

  function ensureRoot() {
    if (!rootAbs) rootAbs = docRoot();
    return rootAbs;
  }

  function absHref(rootRelative) {
    return new URL(rootRelative, rootAbs || docRoot()).href;
  }

  // Freeze the persistent chrome's depth-relative links to absolute URLs NOW,
  // at script-execute time. The chrome lives outside #main and is never
  // swapped, but hx-boost captures each anchor's RAW href attribute when it
  // processes the page (htmx's DOMContentLoaded init — which runs after this,
  // because htmx.min.js registered its listener first but deferred scripts
  // execute before DOMContentLoaded fires) and resolves it at click time
  // against the CURRENT document URL. After any cross-depth swap (landing
  // page items/… is depth 1, type pages items/<svc>/… are depth 2) the
  // relative form resolves to items/items/… and 404s. On this first document
  // the relative href still resolves correctly, so a.href gives the right
  // absolute URL. Fragment-only hrefs (skip link) are left alone — htmx
  // ignores them, and absolutizing would make them boostable.
  (function freezeChromeHrefs() {
    var links = document.querySelectorAll("#topbar a[href], #sidebar a[href]");
    for (var i = 0; i < links.length; i++) {
      var raw = links[i].getAttribute("href");
      if (raw && raw.charAt(0) !== "#") links[i].setAttribute("href", links[i].href);
    }
  })();

  /* --------------------------- DOM helpers -------------------------- */
  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = text;
    return n;
  }

  // toc.json stores HTML-encoded display names (e.g. generics as "Foo&lt;&gt;")
  // because the static TOC.html fallback injects them as raw HTML. We render the
  // sidebar/search via textContent, so decode entities first to show "Foo<>".
  // Decoding goes through DOMParser: its documents have no browsing context, so
  // nothing in the parsed string can load or execute — unlike the classic
  // detached-textarea innerHTML trick, which becomes a live XSS sink the moment
  // a payload breaks out of the RCDATA context ("</textarea><img onerror=…>").
  // Hot paths (filter / modal matching) precompute decoded names once instead of
  // calling this per keystroke — see loadSidebar.
  var _decoderParser = null;
  // Shared because a DOMParser instance is stateless; also used by readHeadMeta
  // to read the <head> of a boosted response (same no-browsing-context argument
  // applies there: nothing in a parsed document loads or executes).
  function sharedParser() {
    if (!_decoderParser) _decoderParser = new DOMParser();
    return _decoderParser;
  }
  function decodeEntities(s) {
    if (s == null) return s;
    if (s.indexOf("&") === -1) return s;
    return sharedParser().parseFromString(s, "text/html").documentElement.textContent;
  }

  function cssEscape(s) {
    if (window.CSS && CSS.escape) return CSS.escape(s);
    return String(s).replace(/["\\\]]/g, "\\$&");
  }

  // Navigate via htmx when available (in-place #main swap + history push), else a
  // normal load. `push` records the URL in history so it behaves like navigation.
  function navigateTo(href) {
    if (window.htmx) {
      // No scroll modifier: #main is not a scroll container (the window
      // scrolls), so htmx's scroll handling is a no-op here — the
      // htmx:afterSettle handler below owns scrolling instead.
      window.htmx.ajax("GET", href, {
        target: "#main", select: "#main", swap: "outerHTML", push: href
      });
    } else {
      window.location.assign(href);
    }
  }

  // Idempotent wiring guard: returns true and runs fn() the first time a given
  // (node, key) is seen, false on every later call, so event listeners are
  // bound exactly once even though onPageLoad runs on every htmx:afterSwap.
  //
  // The guard MUST live in JS state (WeakMap keyed on the live node), never in
  // a DOM attribute: htmx's history support snapshots the history element's
  // innerHTML (#main via hx-history-elt) and restores it on Back/Forward, which
  // serializes attributes but not listeners. An attribute sentinel would come
  // back "already wired" on freshly-restored nodes that have no listeners,
  // permanently dead. A restored node is a new object, so the WeakMap correctly
  // reports it as unwired and fn() re-binds.
  var _wired = new WeakMap();
  function once(node, key, fn) {
    if (!node) return false;
    var keys = _wired.get(node);
    if (keys && keys[key]) return false;
    if (!keys) { keys = {}; _wired.set(node, keys); }
    keys[key] = true;
    if (fn) fn();
    return true;
  }

  // Debounce factory shared by the sidebar filter and the search modal input.
  // fn reads its inputs at fire time (not capture time), so flush() — used by
  // the modal's Enter handler to act on exactly what the input shows — always
  // sees the latest value; cancel() drops a pending run (modal close).
  function makeDebounce(fn, ms) {
    var t = null;
    function run() {
      clearTimeout(t);
      t = setTimeout(function () { t = null; fn(); }, ms);
    }
    run.flush = function () {
      if (t === null) return;
      clearTimeout(t);
      t = null;
      fn();
    };
    run.cancel = function () { clearTimeout(t); t = null; };
    return run;
  }


  /* ===================================================================
     Sidebar + chrome
     -------------------------------------------------------------------
     Hydrates the persistent sidebar once from toc.json (loaded for the
     whole SDK, not inlined into every page), keeps the active node in
     sync on every htmx navigation, filters the tree, and wires the
     top-bar chrome (mobile drawer, theme button).
     The parsed toc.json is kept in tocData so the search section can
     match types/namespaces synchronously (zero-latency, no extra fetch).
     =================================================================== */



  // Sidebar and search-result links are built in JS *after* page load, so htmx's
  // hx-boost (which only processes anchors present when it initializes / in
  // swapped content) does not boost them — a plain click would trigger a full
  // page load. Route left-clicks through navigateTo() for an in-place swap, but
  // leave modified clicks (Ctrl/Cmd/middle/shift = open in new tab/window) and
  // the real href alone so the no-JS fallback and "open in new tab" keep
  // working. `beforeNav` runs only when we do navigate (the search modal
  // closes itself before the swap).
  function boostClick(ev, href, beforeNav) {
    if (ev.defaultPrevented) return;
    if (ev.button !== 0 || ev.metaKey || ev.ctrlKey || ev.shiftKey || ev.altKey) return;
    ev.preventDefault();
    if (beforeNav) beforeNav();
    navigateTo(href);
  }

  /* ----------------------------- Sidebar ----------------------------
     Service-grouped tree. Related namespaces fold under a header named by their
     common prefix (e.g. "Amazon.S3" gathers Amazon.S3, Amazon.S3.Model, …) so the
     top level stays short across 225 services. When that prefix is itself a
     namespace, its header IS that namespace (a clickable link that also expands
     to the sub-namespaces + its own types). Each level expands lazily. */
  var topNodes = [];         // ordered top-level node descriptors {kind, data}
  var idIndex = {};          // page tocid -> { highlightId, chainIds:[ancestor ids] }
  // The exact #sidebarNav element the tree was last rendered into. Compared by
  // identity, NOT a boolean: an htmx history restore (Back/Forward) can replace
  // the whole sidebar DOM with a listener-less copy (cache hit) or an empty,
  // unhydrated shell (cache miss) while module state survives — a boolean flag
  // would report "built" forever and the sidebar would stay dead/empty until a
  // full reload. When the element differs, re-render from the cached toc data.
  var builtNav = null;

  function serviceId(service) {
    return "svc__" + String(service).replace(/[^A-Za-z0-9_]/g, "_");
  }

  // Build the top-level list. Namespaces are grouped by service key, then each
  // group is collapsed to the most intuitive shape:
  //   * 1 namespace            → that namespace is shown directly (no wrapper).
  //   * the group's common prefix IS one of its namespaces (e.g. "Amazon.S3"
  //     with Amazon.S3 + Amazon.S3.Model + …) → that root namespace BECOMES the
  //     header: a clickable link to its page that expands to reveal the
  //     sub-namespaces and its own types (no redundant duplicate child).
  //   * otherwise (prefix isn't itself a namespace) → a plain expandable group.
  // idIndex maps any page's tocid to {highlightId, chainIds} so the active page
  // can be located and its ancestors expanded.
  function buildGroups(tocData) {
    topNodes = [];
    idIndex = {};
    var byService = {};
    var order = [];
    tocData.namespaces.forEach(function (ns) {
      var key = ns.service || ns.name;
      var g = byService[key];
      if (!g) { g = { namespaces: [] }; byService[key] = g; order.push(g); }
      g.namespaces.push(ns);
    });

    order.forEach(function (g) {
      if (g.namespaces.length === 1) {
        // Single namespace: show it directly at the top level.
        var only = g.namespaces[0];
        topNodes.push({ kind: "namespace", data: only });
        indexNamespace(only, []);
        return;
      }

      var label = commonNamespacePrefix(g.namespaces);
      var rootNs = null;
      var subs = [];
      g.namespaces.forEach(function (ns) {
        if (rootNs === null && ns.name === label) rootNs = ns;
        else subs.push(ns);
      });

      var group = {
        kind: "group",
        id: serviceId(label),
        label: label,
        href: rootNs ? rootNs.href : null,   // header links to the root namespace page
        subNamespaces: subs,                  // shown first when expanded (like folders)
        rootTypes: rootNs ? (rootNs.nodes || []) : [] // then the root namespace's own types
      };
      topNodes.push({ kind: "group", data: group });

      // The root namespace has no row of its own — its page highlights the header.
      if (rootNs) idIndex[rootNs.id] = { highlightId: group.id, chainIds: [] };
      // Root-namespace types are leaves directly under the header.
      (rootNs ? rootNs.nodes || [] : []).forEach(function (t) {
        idIndex[t.id] = { highlightId: t.id, chainIds: [group.id] };
      });
      // Sub-namespaces (and their types) nest one level deeper under the header.
      subs.forEach(function (ns) {
        idIndex[ns.id] = { highlightId: ns.id, chainIds: [group.id] };
        (ns.nodes || []).forEach(function (t) {
          idIndex[t.id] = { highlightId: t.id, chainIds: [group.id, ns.id] };
        });
      });
    });

    topNodes.sort(function (a, b) {
      return topLabel(a).localeCompare(topLabel(b));
    });
  }

  // Index a top-level namespace and its types (chain rooted at that namespace).
  function indexNamespace(ns, parentChain) {
    var chain = parentChain.concat([ns.id]);
    idIndex[ns.id] = { highlightId: ns.id, chainIds: parentChain };
    (ns.nodes || []).forEach(function (t) {
      idIndex[t.id] = { highlightId: t.id, chainIds: chain };
    });
  }

  // Longest shared dotted-segment prefix across namespace names, e.g.
  // [Amazon.S3, Amazon.S3.Model, Amazon.S3.Util] → "Amazon.S3".
  function commonNamespacePrefix(namespaces) {
    if (!namespaces.length) return "";
    var parts = namespaces[0].name.split(".");
    for (var i = 1; i < namespaces.length; i++) {
      var p = namespaces[i].name.split(".");
      var n = Math.min(parts.length, p.length);
      var k = 0;
      while (k < n && parts[k] === p[k]) k++;
      parts = parts.slice(0, k);
      if (!parts.length) break;
    }
    return parts.join(".");
  }

  // Decoded display label (uses the precomputed dname, see loadSidebar).
  function topLabel(t) {
    return t.kind === "group" ? t.data.label : t.data.dname;
  }

  /* The sidebar is a real ARIA tree (WAI-ARIA APG "tree view" pattern):
       ul.toc-root      role="tree"
       li.toc-node      role="none"        (structural only)
       a / span label   role="treeitem"    aria-expanded on parents
       ul.toc-children  role="group"
     Treeitems use a roving tabindex — the whole tree is ONE Tab stop and
     ↑/↓/→/←/Home/End move within it. Without this, every visible link was its
     own Tab stop (hundreds once a service is expanded) and link-less group
     headers were only reachable through their chevrons. The chevron is now a
     mouse-only affordance (aria-hidden); keyboards expand/collapse with →/←. */

  // Renders a node. kind: "group" | "namespace" | "leaf".
  function makeNode(kind, data) {
    var isLeaf = kind === "leaf";
    var li = el("li", "toc-node toc-" + kind + (isLeaf ? " toc-leaf" : ""));
    li.setAttribute("data-id", data.id);
    li.setAttribute("role", "none");
    li.__kind = kind;
    li.__data = data;

    var row = el("div", "toc-row");
    row.setAttribute("data-id", data.id);

    var chevron = el("span", "toc-chevron");
    chevron.textContent = "›";
    chevron.setAttribute("aria-hidden", "true");
    row.appendChild(chevron);

    var labelText = kind === "group" ? data.label : data.dname;
    var href = data.href; // group may be null (no root namespace page)

    var item; // the role="treeitem" element (link or plain label)
    if (href) {
      item = el("a", null, labelText);
      // The 300px sidebar ellipsizes at ~30 chars while the SDK's dominant
      // naming pattern differs only in the suffix (…ConfigurationRequest /
      // …ConfigurationResponse) — a hover tooltip is the only way to tell
      // truncated siblings apart. Screen readers keep using the text content.
      item.title = labelText;
      var abs = absHref(href);
      item.href = abs;
      item.addEventListener("click", function (ev) {
        // Clicking a link that has hidden children also expands it (so the user
        // sees the children without having to hunt for the chevron).
        if (!isLeaf) setExpanded(li, true);
        boostClick(ev, abs); // in-place swap (see boostClick note above)
      });
    } else {
      item = el("span", "toc-label", labelText);
      item.title = labelText; // same truncation, same tooltip (linkless headers)
    }
    item.setAttribute("role", "treeitem");
    item.setAttribute("tabindex", "-1"); // roving; wireTree makes one item tabbable
    li.__item = item;
    row.appendChild(item);

    li.appendChild(row);

    if (!isLeaf) {
      item.setAttribute("aria-expanded", "false");
      li.__childrenUl = el("ul", "toc-children");
      li.__childrenUl.setAttribute("role", "group");
      li.appendChild(li.__childrenUl);
      var toggle = function (ev) {
        ev.preventDefault();
        ev.stopPropagation();
        setExpanded(li, !li.classList.contains("is-open"));
      };
      chevron.addEventListener("click", toggle);
      // A header with no link (no root namespace page) toggles on row click too;
      // keyboards get Enter/Space via the tree keydown handler.
      if (!href) row.addEventListener("click", toggle);
    }
    return li;
  }

  // Expand/collapse a node, keeping the treeitem's aria-expanded in sync and
  // building children on first open.
  function setExpanded(li, open) {
    if (!li || li.classList.contains("toc-leaf")) return;
    li.classList.toggle("is-open", open);
    if (li.__item) li.__item.setAttribute("aria-expanded", open ? "true" : "false");
    if (open) buildChildren(li);
  }

  function buildChildren(li) {
    if (li.getAttribute("data-built") === "1") return;
    var kind = li.__kind, data = li.__data;
    var frag = document.createDocumentFragment();
    if (kind === "group") {
      data.subNamespaces.forEach(function (ns) { frag.appendChild(makeNode("namespace", ns)); });
      data.rootTypes.forEach(function (t) { frag.appendChild(makeNode("leaf", t)); });
    } else if (kind === "namespace") {
      (data.nodes || []).forEach(function (t) { frag.appendChild(makeNode("leaf", t)); });
    }
    li.__childrenUl.appendChild(frag);
    li.setAttribute("data-built", "1");
  }

  /* ------------------- Tree keyboard support ------------------------ */
  // All treeitems currently visible (inside collapsed nodes the children are
  // either unbuilt or display:none — offsetParent filters both).
  function visibleItems(nav) {
    var all = nav.querySelectorAll('[role="treeitem"]');
    var out = [];
    for (var i = 0; i < all.length; i++) {
      if (all[i].offsetParent !== null) out.push(all[i]);
    }
    return out;
  }

  // Make `item` the tree's single Tab stop (roving tabindex).
  function setTabStop(nav, item) {
    var all = nav.querySelectorAll('[role="treeitem"]');
    for (var i = 0; i < all.length; i++) all[i].setAttribute("tabindex", "-1");
    item.setAttribute("tabindex", "0");
  }

  function focusItem(nav, item) {
    if (!item) return;
    setTabStop(nav, item);
    item.focus();
  }

  // One Tab stop for the whole tree: make the first (or active) item tabbable.
  function initRovingFocus(nav) {
    var items = visibleItems(nav);
    if (!items.length) return;
    var active = nav.querySelector('.toc-row.is-active [role="treeitem"]');
    (active || items[0]).setAttribute("tabindex", "0");
  }

  function wireTree(nav) {
    once(nav, "tree-keys", function () {
      // Keep the roving tabindex honest when focus arrives by mouse click too.
      nav.addEventListener("focusin", function (ev) {
        var t = ev.target;
        if (t && t.getAttribute && t.getAttribute("role") === "treeitem") {
          setTabStop(nav, t);
        }
      });

      nav.addEventListener("keydown", function (ev) {
        var item = ev.target;
        if (!item || !item.getAttribute || item.getAttribute("role") !== "treeitem") return;
        var li = item.closest("li.toc-node");
        var items, idx;

        switch (ev.key) {
          case "ArrowDown":
          case "ArrowUp":
            ev.preventDefault();
            items = visibleItems(nav);
            idx = items.indexOf(item) + (ev.key === "ArrowDown" ? 1 : -1);
            if (idx >= 0 && idx < items.length) focusItem(nav, items[idx]);
            break;
          case "ArrowRight":
            ev.preventDefault();
            if (li && !li.classList.contains("toc-leaf")) {
              if (!li.classList.contains("is-open")) {
                setExpanded(li, true);
              } else {
                var child = li.__childrenUl && li.__childrenUl.querySelector('[role="treeitem"]');
                if (child) focusItem(nav, child);
              }
            }
            break;
          case "ArrowLeft":
            ev.preventDefault();
            if (li && li.classList.contains("is-open")) {
              setExpanded(li, false);
            } else if (li) {
              var parentLi = li.parentElement && li.parentElement.closest("li.toc-node");
              if (parentLi && parentLi.__item) focusItem(nav, parentLi.__item);
            }
            break;
          case "Home":
          case "End":
            ev.preventDefault();
            items = visibleItems(nav);
            focusItem(nav, ev.key === "Home" ? items[0] : items[items.length - 1]);
            break;
          case "Enter":
          case " ":
          case "Spacebar":
            // Links activate natively on Enter; only link-less headers need help.
            if (item.tagName !== "A") {
              ev.preventDefault();
              if (li) setExpanded(li, !li.classList.contains("is-open"));
            }
            break;
        }
      });
    });
  }

  function renderTree() {
    var nav = document.getElementById("sidebarNav");
    if (!nav) return;
    nav.textContent = "";
    var ul = el("ul", "toc-root");
    ul.setAttribute("role", "tree");
    ul.setAttribute("aria-label", "API navigation");
    var frag = document.createDocumentFragment();
    topNodes.forEach(function (t) { frag.appendChild(makeNode(t.kind, t.data)); });
    ul.appendChild(frag);
    nav.appendChild(ul);
    initRovingFocus(nav);
    wireTree(nav);
  }

  // Mirror filter outcomes into the visually-hidden status region the shell
  // emits beside the filter input (#sidebarFilterStatus, role="status"): the
  // tree DOM swap itself is silent for screen readers, so without this a user
  // typing into "Filter navigation" gets no feedback that results changed or
  // that nothing matched (WCAG 4.1.3 Status Messages). Pages generated before
  // the region existed simply skip the announcement.
  function setFilterStatus(text) {
    var status = document.getElementById("sidebarFilterStatus");
    if (status) status.textContent = text || "";
  }

  function renderSearch(query) {
    var nav = document.getElementById("sidebarNav");
    if (!nav) return;
    var q = query.toLowerCase();
    var matches = [];
    var LIMIT = 400;
    outer:
    for (var i = 0; i < tocData.namespaces.length; i++) {
      var ns = tocData.namespaces[i];
      // Match against the precomputed decoded+lowercased name (loadSidebar) so
      // typing "<" finds "Foo<>" and the scan allocates nothing per row.
      if (ns.lname.indexOf(q) !== -1) {
        matches.push(ns);
        if (matches.length >= LIMIT) break;
      }
      var nodes = ns.nodes || [];
      for (var j = 0; j < nodes.length; j++) {
        if (nodes[j].lname.indexOf(q) !== -1) {
          matches.push(nodes[j]);
          if (matches.length >= LIMIT) break outer;
        }
      }
    }

    nav.textContent = "";
    if (!matches.length) {
      nav.appendChild(el("div", "sidebar-empty", "No matches for “" + query + "”"));
      setFilterStatus("No matches for “" + query + "”");
      return;
    }
    var ul = el("ul", "toc-root");
    ul.setAttribute("role", "tree");
    ul.setAttribute("aria-label", "Filtered navigation");
    var frag = document.createDocumentFragment();
    // Search results are shown flat as leaf rows (each links to its page).
    matches.forEach(function (node) { frag.appendChild(makeNode("leaf", node)); });
    ul.appendChild(frag);
    nav.appendChild(ul);
    initRovingFocus(nav);
    wireTree(nav);
    // "400+" when the scan broke at the cap: the true total is unknown then.
    setFilterStatus((matches.length >= LIMIT ? LIMIT + "+" : String(matches.length))
      + (matches.length === 1 ? " result" : " results") + " for “" + query + "”");
  }

  function wireFilter() {
    var input = document.getElementById("sidebarFilter");
    if (!input) return;
    once(input, "data-wired", function () {
      var applyFilter = makeDebounce(function () {
        var v = input.value.trim();
        if (v.length) renderSearch(v);
        else { renderTree(); syncActive(); setFilterStatus(""); }
      }, 120);
      input.addEventListener("input", applyFilter);
    });
  }

  // Failure state (fetch failed, bad JSON, file:// where fetch is blocked):
  // point at the static TOC.html instead of a dead-end message.
  function navUnavailable(nav) {
    nav.textContent = "";
    var msg = el("div", "sidebar-empty", "Navigation unavailable — ");
    var link = el("a", null, "browse the table of contents");
    link.href = absHref("TOC.html");
    msg.appendChild(link);
    msg.appendChild(document.createTextNode("."));
    nav.appendChild(msg);
  }

  // The in-flight toc.json fetch, so concurrent loadSidebar calls (every htmx
  // swap re-runs it) share one request. Without this, navigating before the
  // first (large) fetch resolves starts duplicate fetches, and each late
  // resolution rebuilds the tree — collapsing the user's expansions and
  // clobbering an active filter view. Reset to null on failure so a later
  // navigation can retry.
  var tocLoading = null;

  function loadSidebar() {
    var nav = document.getElementById("sidebarNav");
    if (!nav || builtNav === nav) return Promise.resolve();
    if (tocData) {
      // The DOM was replaced (htmx history restore) but the parsed toc survives
      // in module state: re-render without re-fetching. topNodes/idIndex are
      // still valid; renderTree replaces whatever stale markup was restored.
      builtNav = nav;
      renderTree();
      wireFilter();
      syncActive();
      return Promise.resolve();
    }
    if (tocLoading) return tocLoading;
    var rootAbs = ensureRoot();
    // DATA_VQ cache-busts the data file: types added in a new SDK release must
    // not be missing from a CDN-cached toc.json paired with new pages.
    tocLoading = fetch(new URL("toc.json" + (DATA_VQ || ""), rootAbs).href)
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        if (!data || !data.namespaces) {
          tocLoading = null;
          navUnavailable(nav);
          return;
        }
        // Decode display names once up front so the filter / search modal do
        // pure string matching per keystroke instead of an entity round-trip.
        // Lowercase + acronym forms are precomputed alongside for the same
        // reason: the modal's toc scan visits every name per (debounced)
        // keystroke on the MAIN thread, and deriving these per row per query
        // allocated ~2 strings × ~100k rows per keystroke at full-SDK scale.
        // (acronymOf may be absent on a stale cached search-core.js; score()
        // then just falls back to deriving lazily.)
        var acrOf = self.AwsDocsSearch && self.AwsDocsSearch.acronymOf;
        data.namespaces.forEach(function (ns) {
          ns.dname = decodeEntities(ns.name);
          ns.lname = ns.dname.toLowerCase();
          ns.acr = acrOf ? acrOf(ns.dname) : undefined;
          (ns.nodes || []).forEach(function (t) {
            t.dname = decodeEntities(t.name);
            t.lname = t.dname.toLowerCase();
            t.acr = acrOf ? acrOf(t.dname) : undefined;
          });
        });
        tocData = data;
        buildGroups(data);
        builtNav = nav;
        renderTree();
        wireFilter();
        syncActive();
        // A modal query typed while toc.json was in flight rendered without
        // type/namespace hits (matchTocData returns [] until tocData exists),
        // and only a worker reply or another keystroke would re-render — the
        // worker's "ready" handler has a replay hook, the toc half didn't.
        // Refresh the open modal so those hits appear the moment data lands.
        if (modalOpen() && lastQuery) renderModalResults();
      })
      .catch(function () {
        tocLoading = null;
        navUnavailable(nav);
      });
    return tocLoading;
  }

  function clearActive() {
    var prev = document.querySelectorAll("#sidebarNav .toc-row.is-active");
    for (var i = 0; i < prev.length; i++) prev[i].classList.remove("is-active");
    // aria-current mirrors .is-active for assistive tech (set in syncActive).
    var cur = document.querySelectorAll("#sidebarNav [aria-current]");
    for (var j = 0; j < cur.length; j++) cur[j].removeAttribute("aria-current");
  }

  function syncActive() {
    if (!builtNav) return;
    var filter = document.getElementById("sidebarFilter");
    if (filter && filter.value.trim().length) return; // don't disturb search view

    clearActive();
    // The per-page identifier rides on .content-shell (inside #main, not on
    // it): an htmx history restore swaps only #main's innerHTML
    // (hx-history-elt), so an attribute on #main itself would go stale while
    // .content-shell is part of every swapped or restored payload.
    var main = document.getElementById("main");
    var shell = main && main.querySelector(".content-shell");
    var tocid = shell && shell.getAttribute("data-tocid");
    if (!tocid) return;

    var entry = idIndex[tocid];
    if (!entry) return;

    // Expand each ancestor (top → down), building children so the next level
    // exists in the DOM before we look for it.
    (entry.chainIds || []).forEach(function (id) {
      var li = document.querySelector('#sidebarNav .toc-node[data-id="' + cssEscape(id) + '"]');
      if (li) setExpanded(li, true);
    });

    var row = document.querySelector('#sidebarNav .toc-row[data-id="' + cssEscape(entry.highlightId) + '"]');
    if (row) {
      row.classList.add("is-active");
      // Bring it into view within the sidebar without yanking the page.
      if (row.scrollIntoView) row.scrollIntoView({ block: "nearest" });
      // Make the active node the tree's single Tab stop (roving tabindex),
      // without stealing focus from wherever the user is.
      var nav = document.getElementById("sidebarNav");
      var item = row.querySelector('[role="treeitem"]');
      // The visual highlight alone is silent for screen readers; aria-current
      // announces which tree entry is the page being read (cleared in clearActive).
      if (item) item.setAttribute("aria-current", "page");
      if (nav && item) setTabStop(nav, item);
    }
  }

  /* --------------------------- Mobile drawer ------------------------ */
  // Single entry point for drawer state so the toggle's aria-expanded and the
  // inert-ing of the page behind the scrim can't drift from the CSS class.
  // `inert` removes #main from the tab order / a11y tree while the drawer
  // covers it (older browsers ignore the attribute — graceful degradation);
  // the topbar stays interactive because it hosts the toggle itself.
  function setDrawerOpen(open) {
    document.body.classList.toggle("nav-open", open);
    var toggle = document.getElementById("navToggle");
    if (toggle) toggle.setAttribute("aria-expanded", open ? "true" : "false");
    var main = document.getElementById("main");
    if (main) {
      if (open) main.setAttribute("inert", "");
      else main.removeAttribute("inert");
    }
  }

  function closeDrawer() { setDrawerOpen(false); }

  // Reset drawer state when the viewport leaves the drawer breakpoint: past
  // 1024px the CSS scrim and hamburger stop applying, but body.nav-open and
  // #main's `inert` would otherwise persist — the whole content region dead,
  // with no visual cue and no toggle to clear it. Keep the media query in sync
  // with the @media (max-width: 1024px) block in aws-docs.css.
  try {
    var drawerMq = window.matchMedia("(max-width: 1024px)");
    var onDrawerMq = function (ev) { if (!ev.matches) closeDrawer(); };
    if (drawerMq.addEventListener) drawerMq.addEventListener("change", onDrawerMq);
    else if (drawerMq.addListener) drawerMq.addListener(onDrawerMq);
  } catch (e) { /* matchMedia unavailable */ }

  function wireChrome() {
    var navToggle = document.getElementById("navToggle");
    once(navToggle, "data-wired", function () {
      navToggle.addEventListener("click", function () {
        setDrawerOpen(!document.body.classList.contains("nav-open"));
      });
    });
    var scrim = document.getElementById("navScrim");
    once(scrim, "data-wired", function () { scrim.addEventListener("click", closeDrawer); });
    var themeBtn = document.getElementById("themeToggle");
    once(themeBtn, "data-wired", function () { themeBtn.addEventListener("click", toggleTheme); });

    // Escape closes the open mobile drawer (parity with the search modal). Bound
    // once at the document level so it survives htmx swaps. Only acts when the
    // drawer is open and the search modal isn't (onGlobalKeydown owns Escape
    // while the modal is open), then returns focus to the toggle that opened it.
    once(document.documentElement, "data-drawer-esc-bound", function () {
      document.addEventListener("keydown", function (ev) {
        if (ev.key !== "Escape") return;
        if (!document.body.classList.contains("nav-open")) return;
        var modal = document.getElementById("searchModal");
        if (modal && !modal.hidden) return; // modal is on top; let it handle Escape
        ev.preventDefault();
        closeDrawer();
        var toggle = document.getElementById("navToggle");
        if (toggle && toggle.focus) toggle.focus();
      });
    });
  }


  /* ===================================================================
     Local search
     -------------------------------------------------------------------
     The default scope ("Documentation - This Guide") searches THIS API
     reference locally in a command-palette modal; every other scope
     keeps escalating to the external AWS search (searchFormSubmit).
     The modal opens when the user starts typing in the topbar box (the
     keystroke is handed off to the modal's own input), or via ⌘K /
     Ctrl-K / "/". Results render live, grouped by kind.
     Two result sources, ranked on one scale by the shared scorer in
     search-core.js (self.AwsDocsSearch.score):
       • types + namespaces — matched synchronously from the in-memory
         tocData (loaded by the sidebar section), zero latency.
       • members (methods/properties/fields/events/enum) — matched in
         search-worker.js off the main thread; they merge in a beat later.
     =================================================================== */



  var SEARCH_LIMIT = 50;         // total rows shown; overflow noted, never silent
  var SEARCH_DEBOUNCE_MS = 90;   // coalesce keystrokes before scanning toc data
  var score = (self.AwsDocsSearch && self.AwsDocsSearch.score) || null;

  /* ---------------------- Result-kind taxonomy ----------------------
     Single source of truth for the result kinds. The numeric codes match
     TOCWriter (1 method, 2 property, 3 field, 4 event, 5 enum-member); "ns"
     and "type" are local pseudo-kinds for the tocData matches. GROUPS (display
     order), the icon mapping, and the tie-break rank are all derived from this
     one table so they cannot drift. If you add a kind, add it HERE and in
     TOCWriter's Kind* constants.
     Icons reuse the member-table icon classes (aws-docs.css) so search results
     and member rows share one icon system; only "ico-namespace" is
     search-specific (namespaces have no member-table row). */
  var KIND_TABLE = [
    { code: "ns",   label: "Namespaces",  icon: "ico-namespace",  rank: 0 },
    { code: "type", label: "Types",       icon: "class",          rank: 1 },
    { code: 1,      label: "Methods",     icon: "publicMethod",   rank: 2 },
    { code: 2,      label: "Properties",  icon: "publicProperty", rank: 2 },
    { code: 3,      label: "Fields",      icon: "field",          rank: 2 },
    { code: 4,      label: "Events",      icon: "event",          rank: 2 },
    { code: 5,      label: "Enum values", icon: "enum",           rank: 2 }
  ];
  var KIND_BY_CODE = {};
  KIND_TABLE.forEach(function (k) { KIND_BY_CODE[k.code] = k; });

  // Result groups in display order (one per kind that can appear).
  var GROUPS = KIND_TABLE.map(function (k) {
    return { code: k.code, label: k.label };
  });

  function kindIconClass(kind) {
    var k = KIND_BY_CODE[kind];
    return k ? k.icon : "class";
  }

  function kindRank(kind) {
    var k = KIND_BY_CODE[kind];
    return k ? k.rank : 2;
  }

  /* ------------------------------ State ----------------------------- */
  var searchWorker = null;       // lazy; null if Workers unavailable
  var workerReady = false;
  var workerFailed = false;      // worker/index load failed; retried once per modal open
  var workerUnsupported = false; // no Worker constructor / file:// — permanent, never retried
  var searchSeq = 0;             // monotonic; ignore stale worker replies
  var lastQuery = "";
  var memberResults = [];        // most recent member matches from the worker
  var memberTotal = 0;           // TOTAL member matches (beyond the returned cap)
  var memberPending = false;     // worker reply for the current searchSeq still outstanding
  var memberCoverage = 0;        // % of the chunked member index loaded so far (0-100)
  var memberIndexIncomplete = false; // a later chunk failed; corpus stays partial
  var activeIndex = -1;          // highlighted row (flattened across groups)
  var currentRows = [];          // descriptors backing the rendered rows, in order
  var lastFocus = null;          // element focused before the modal opened (restored on close)

  // Debounced modal search: matchTocData scans every namespace + type, so
  // coalesce bursts of keystrokes (the worker query is debounced with it).
  // Reads the input at fire time, so Enter can flush it synchronously (see
  // onModalKeydown); closeSearchModal cancels a pending run.
  var debouncedModalSearch = makeDebounce(function () {
    var input = document.getElementById("searchModalInput");
    runSearch(input ? input.value : "");
  }, SEARCH_DEBOUNCE_MS);

  function isLocalScope() {
    var sel = document.getElementById("sel");
    return !sel || sel.value === LOCAL_SCOPE;
  }

  /* ------------------------------ Worker ---------------------------- */
  function ensureWorker() {
    if (searchWorker || workerReady || workerFailed || workerUnsupported) return;
    if (typeof Worker === "undefined" || window.location.protocol === "file:") {
      // No worker support — or file://, where the Worker constructor throws a
      // SecurityError and fetch of the index is blocked anyway. Permanent for
      // this environment, so workerUnsupported is never reset (unlike the
      // retriable workerFailed latch — see openSearchModal). Fail visibly:
      // renderModalResults shows the members-unavailable note off workerFailed.
      workerUnsupported = true;
      workerFailed = true;
      renderModalResults();
      return;
    }
    var rootAbs = ensureRoot();
    // Same cache-busting fingerprint the page's own script tags carry; the
    // worker passes it on to its importScripts (see search-worker.js).
    var vq = ASSET_V ? "?v=" + ASSET_V : "";
    try {
      searchWorker = new Worker(new URL("resources/search-worker.js" + vq, rootAbs).href);
      searchWorker.onmessage = function (ev) {
        var msg = ev.data || {};
        if (msg.type === "ready") {
          workerReady = true;
          memberCoverage = typeof msg.pct === "number" ? msg.pct : 100;
          if (lastQuery) postWorkerQuery(lastQuery);
        } else if (msg.type === "progress") {
          // Another index chunk landed (or failed — incomplete). Re-run the
          // live query so its results reflect the grown corpus, and re-render
          // so the coverage note stays current. searchSeq is untouched: the
          // re-query answers the SAME query, so its reply must not be
          // treated as stale. incomplete is a level, not an edge: each
          // progress message re-states whether chunks are still missing, so a
          // successful retry pass clears the latch here.
          memberCoverage = typeof msg.pct === "number" ? msg.pct : memberCoverage;
          memberIndexIncomplete = !!msg.incomplete;
          if (lastQuery && workerReady) {
            memberPending = true;
            postWorkerQuery(lastQuery);
          }
          renderModalResults();
        } else if (msg.type === "results") {
          if (msg.seq !== searchSeq) return; // stale
          memberPending = false;
          memberResults = msg.items || [];
          memberTotal = typeof msg.total === "number" ? msg.total : memberResults.length;
          renderModalResults();
        } else if (msg.type === "error") {
          // Index fetch failed / malformed: member search is off until the next
          // modal open clears the latch and retries (under hx-boost this module
          // lives for the whole browsing session, so latching forever would let
          // one network blip kill member search for hours). Say so instead of
          // rendering a confident-looking "No matches".
          workerFail();
        }
      };
      searchWorker.onerror = function () { workerFail(); };
      // DATA_VQ cache-busts the index: members added in a new SDK release must
      // not be missing from a CDN-cached search-index.json paired with new
      // pages. The worker propagates the same ?v= to the sibling chunk files
      // the manifest lists, so the whole set is version-coherent.
      memberCoverage = 0;
      memberIndexIncomplete = false;
      searchWorker.postMessage({ type: "init", indexUrl: new URL("search-index.json" + (DATA_VQ || ""), rootAbs).href });
    } catch (e) { workerFail(); }
  }

  function workerFail() {
    if (searchWorker && searchWorker.terminate) {
      try { searchWorker.terminate(); } catch (e) { /* ignore */ }
    }
    searchWorker = null;
    workerReady = false;
    workerFailed = true;
    memberPending = false; // no reply is coming; stop saying "searching"
    renderModalResults();
  }

  function postWorkerQuery(q) {
    if (!searchWorker || !workerReady) return;
    searchWorker.postMessage({ type: "query", q: q, seq: searchSeq, limit: SEARCH_LIMIT });
  }

  /* --------------------------- Matching ----------------------------- */
  var _tocMatchTotal = 0;   // total toc hits for the last query (incl. beyond the cap)
  var _tocMatchCacheQ = null;   // query the cached scan answered
  var _tocMatchCacheRows = null;
  var _tocMatchCacheTotal = 0;

  // Match types + namespaces from the in-memory toc data (instant, no fetch).
  // Names, lowercase forms, and acronyms are precomputed by loadSidebar
  // (ns.dname/lname/acr), so the scan allocates nothing per row.
  // Keeps only the best SEARCH_LIMIT rows via the shared top-N collector
  // (search-core.js — the worker ranks members with the same one) instead of
  // collecting-then-sorting every hit — a 1-2 character query matches tens of
  // thousands of names at full-SDK scale, and this runs per (debounced)
  // keystroke on the main thread. _tocMatchTotal still counts everything so
  // the overflow note can say how much was cut.
  // The last query's result is cached: every render calls this (the immediate
  // keystroke render AND the worker-reply merge render), and without the cache
  // each keystroke paid the full scan twice. Returns a copy — mergedMatches
  // pushes member rows into the returned array.
  function matchTocData(q) {
    if (q === _tocMatchCacheQ && _tocMatchCacheRows) {
      _tocMatchTotal = _tocMatchCacheTotal;
      return _tocMatchCacheRows.slice();
    }
    _tocMatchTotal = 0;
    // makeTopN guarded like score: with a stale search-core.js (CDN cache from
    // an older deploy) the export set may predate either helper, and a throw
    // here would kill the whole modal render, not just type/namespace rows.
    if (!tocData || !tocData.namespaces || !score || !self.AwsDocsSearch.makeTopN) return [];
    var top = self.AwsDocsSearch.makeTopN(SEARCH_LIMIT);
    // Dotted queries ("S3.PutObjectRequest") miss the plain scorer for TYPE
    // nodes (their names are unqualified); retry those namespace-qualified.
    // Namespace names contain dots themselves, so they already match dotted
    // queries directly. Hoisted: the dot check is per-query, not per-row.
    var scoreQualified = self.AwsDocsSearch.scoreQualified;
    var qualified = q.indexOf(".") !== -1 && scoreQualified;

    for (var i = 0; i < tocData.namespaces.length; i++) {
      var ns = tocData.namespaces[i];
      var s = score(ns.dname, q, ns.lname, ns.acr);
      if (s >= 0) top.consider({ kind: "ns", name: ns.dname, href: ns.href, type: "", score: s });
      var nodes = ns.nodes || [];
      for (var k = 0; k < nodes.length; k++) {
        var ts = score(nodes[k].dname, q, nodes[k].lname, nodes[k].acr);
        if (ts < 0 && qualified) ts = scoreQualified(nodes[k].dname, ns.dname, q, nodes[k].lname, nodes[k].acr, ns.lname);
        if (ts >= 0) top.consider({ kind: "type", name: nodes[k].dname, href: nodes[k].href, type: ns.dname, score: ts });
      }
    }
    _tocMatchTotal = top.total();
    _tocMatchCacheQ = q;
    _tocMatchCacheRows = top.result();
    _tocMatchCacheTotal = _tocMatchTotal;
    return _tocMatchCacheRows.slice();
  }

  // Combine the (instant) toc matches with the (async) member matches into one
  // score-sorted list, capped at SEARCH_LIMIT.
  function mergedMatches() {
    var merged = matchTocData(lastQuery.toLowerCase());
    for (var i = 0; i < memberResults.length; i++) {
      var m = memberResults[i];
      merged.push({
        kind: m.kind,
        name: m.name,
        // The worker joins base/folder/file into a root-relative href itself.
        href: m.href,
        // The declaring-type context comes from search-index.json HTML-encoded
        // (e.g. generic "Constant&lt;T&gt;"); decode so it renders like the
        // toc-sourced type results (which use decoded dname). The same applies
        // to the overload signature (present only for overloaded methods).
        type: decodeEntities(m.type || ""),
        sig: m.sig ? decodeEntities(m.sig) : null,
        score: m.score
      });
    }
    merged.sort(function (a, b) {
      if (b.score !== a.score) return b.score - a.score;
      var kr = kindRank(a.kind) - kindRank(b.kind);
      if (kr) return kr;
      return a.name.localeCompare(b.name);
    });
    return merged;
  }

  function isTocKind(kind) { return kind === "ns" || kind === "type"; }

  // Cap the merged list at SEARCH_LIMIT, but never let members crowd out every
  // type/namespace row. Score alone does that constantly: member names repeat
  // across the SDK (hundreds of types declare "BucketName", "Marker",
  // "NextToken", every request type has "Validate"), so a query that hits one
  // exactly fills all 50 slots with same-scored EXACT-tier members and evicts
  // the PREFIX/SUBSTR-tier TYPE the user was far more likely reaching for —
  // "S3Bucket" would list dozens of BucketName properties and not the
  // S3BucketResource type. Reserve up to TOC_RESERVED_SLOTS of the cap for toc
  // rows when there are that many to show, and spend the rest on members.
  // This only changes which rows are DROPPED: the sort above still decides
  // order, the groups still render in kind order, and the "+N more" note still
  // counts everything cut. Never yields fewer rows than the plain cap did —
  // the reserve is bounded by the number of toc rows actually available.
  var TOC_RESERVED_SLOTS = 10;
  function capResults(all) {
    if (all.length <= SEARCH_LIMIT) return all;
    var tocAvailable = 0;
    for (var i = 0; i < all.length; i++) if (isTocKind(all[i].kind)) tocAvailable++;
    var memberBudget = SEARCH_LIMIT - Math.min(TOC_RESERVED_SLOTS, tocAvailable);
    var out = [];
    var members = 0;
    for (var j = 0; j < all.length && out.length < SEARCH_LIMIT; j++) {
      if (!isTocKind(all[j].kind)) {
        if (members >= memberBudget) continue;
        members++;
      }
      out.push(all[j]);
    }
    return out;
  }

  /* --------------------------- Rendering ---------------------------- */
  // Out-of-listbox messaging (empty state, overflow, index trouble) renders in
  // the #searchModalStatus live region under the results — never inside the
  // role="listbox", whose children must be options or groups.
  function setStatus(text) {
    var status = document.getElementById("searchModalStatus");
    if (status) status.textContent = text || "";
  }

  // "Searching members…", qualified with how much of the chunked index has
  // loaded when that's mid-way — a first-visit search can be answered against
  // a partial corpus, and results that grow afterwards need explaining.
  function searchingMembersLabel() {
    return memberCoverage > 0 && memberCoverage < 100
      ? "Searching members (" + memberCoverage + "% of index loaded)…"
      : "Searching members…";
  }

  // Render the modal body: results partitioned into kind groups (fixed order),
  // each group keeping its members in score order. currentRows is a flat array
  // in the same visual order so ↑/↓/Enter traverse across groups.
  /* ------------------- External docs-search URL ----------------------
     Shared by the modal's full-text escape hatch (#searchModalExternal, the
     guide scope) and searchFormSubmit's documentation scopes. Built explicitly
     (path?query#fragment) and opened by us rather than via native form-GET:
     native submission with a #fragment in the action is fragile — depending on
     the host it can drop the ".html" extension and/or the query string. */

  // application/x-www-form-urlencoded component: spaces become "+".
  function enc(s) { return encodeURIComponent(s).replace(/%20/g, "+"); }

  // Value of a hidden topbar-form input, with a fallback for pages without it.
  function fieldValue(id, fallback) {
    var n = document.getElementById(id);
    return n && n.value !== "" ? n.value : (fallback || "");
  }

  function docsSearchUrl(scope, query) {
    var product = fieldValue("this_doc_product", "AWS SDK for .NET Version 4");
    var guide = fieldValue("this_doc_guide", "API Reference");
    var locale = fieldValue("doc_locale", "en_us");
    var qs = "searchPath=" + enc(scope)
      + "&searchQuery=" + enc(query)
      + "&this_doc_product=" + enc(product)
      + "&this_doc_guide=" + enc(guide)
      + "&doc_locale=" + enc(locale);
    // Facet fragment (spaces stay "%20"): the guide scope narrows to
    // product+guide, "documentation-product" to product only, the broader
    // documentation scopes to nothing.
    var facet = "";
    if (scope === LOCAL_SCOPE) {
      facet = "#facet_doc_product=" + encodeURIComponent(product)
        + "&facet_doc_guide=" + encodeURIComponent(guide);
    } else if (scope === "documentation-product") {
      facet = "#facet_doc_product=" + encodeURIComponent(product);
    }
    return DOCS_BASE + "/search/doc-search.html?" + qs + facet;
  }

  /* ---------------- Full-text escape hatch (external) ----------------
     The local index matches names only, so a prose query ("multipart upload")
     that used to hit the external guide-scoped full-text search would dead-end
     in the modal. #searchModalExternal (emitted by DocShell under the results)
     always offers that search for the current query. */
  function externalGuideSearchUrl(q) {
    return docsSearchUrl(LOCAL_SCOPE, q);
  }

  function updateExternalLink() {
    var link = document.getElementById("searchModalExternal");
    if (!link) return;
    if (!lastQuery) { link.hidden = true; return; }
    link.href = externalGuideSearchUrl(lastQuery);
    link.textContent = "Search the AWS documentation for “" + lastQuery + "” (full text)";
    link.hidden = false;
  }

  function renderModalResults() {
    var box = document.getElementById("searchModalResults");
    if (!box) return;
    updateExternalLink();

    // A late worker reply for the SAME query re-renders; keep the user's
    // arrow-key position (re-found by href) so Enter doesn't open row 0 when
    // they had already moved. Index 0 (the default) intentionally re-ranks.
    var prevActiveHref = (activeIndex > 0 && currentRows[activeIndex])
      ? currentRows[activeIndex].href : null;

    box.textContent = "";
    currentRows = [];
    activeIndex = -1;

    var input = document.getElementById("searchModalInput");
    if (input) {
      input.removeAttribute("aria-activedescendant");
      input.setAttribute("aria-expanded", "false");
    }

    if (!lastQuery) {
      setStatus("Type to search types, methods, properties…");
      return;
    }

    var all = capResults(mergedMatches());

    if (!all.length) {
      // Never print a confident "No matches" while the worker still owes a
      // reply for this query — on a fresh load that reply can be a chunk
      // download away, and a member-only query has zero toc rows. A "no
      // matches" over a partial corpus is qualified, not confident: the name
      // could live in a chunk that hasn't arrived (or failed to).
      setStatus(workerFailed
        ? "No type or namespace matches. Member search is unavailable — the search index could not be loaded."
        : memberPending
          ? searchingMembersLabel()
          : memberIndexIncomplete
            ? "No matches for “" + lastQuery + "” — but part of the member index failed to load, so members may be missing"
            : memberCoverage < 100
              ? "No matches yet — " + memberCoverage + "% of the member index searched so far…"
              : "No matches for “" + lastQuery + "”");
      return;
    }

    var frag = document.createDocumentFragment();
    // Partition rows into the fixed-order kind groups, then sweep EVERYTHING
    // no group claimed (an index emitted by a newer generator that added a
    // kind code before this file learned it) into a trailing "Other" group —
    // dropping them would render nothing while they still occupy `all` slots,
    // silently skewing the "+N more" arithmetic below. The sweep is by
    // claimed-index, not a second kind lookup: the groups match with ===, so
    // a lookup with different coercion (e.g. `in`, which stringifies) would
    // re-open the exact crack this exists to close (a string "1" kind matches
    // no group AND reads as known). kindIconClass/kindRank already fall back
    // for unknown codes.
    var claimed = [];
    var sections = GROUPS.map(function (g) {
      return { label: g.label, rows: all.filter(function (r, i) {
        if (r.kind !== g.code) return false;
        claimed[i] = true;
        return true;
      }) };
    });
    sections.push({ label: "Other", rows: all.filter(function (r, i) { return !claimed[i]; }) });
    sections.forEach(function (g) {
      var rows = g.rows;
      if (!rows.length) return;
      // role="group" keeps the listbox structure valid (its children must be
      // options or groups); the visual header is decorative for AT since the
      // group's aria-label already carries the name.
      var group = el("div", "search-group");
      group.setAttribute("role", "group");
      group.setAttribute("aria-label", g.label);
      var head = el("div", "search-group-head", g.label);
      head.setAttribute("aria-hidden", "true");
      group.appendChild(head);
      rows.forEach(function (r) {
        var idx = currentRows.length;
        currentRows.push(r);
        var row = el("a", "search-result");
        row.href = absHref(r.href);
        row.id = "search-opt-" + idx;      // referenced by aria-activedescendant
        row.setAttribute("role", "option");
        row.setAttribute("aria-selected", "false");
        row.setAttribute("data-idx", String(idx));
        // Options must NOT be tab stops. This is an aria-activedescendant listbox:
        // DOM focus stays on the combobox input, which owns every arrow/Enter key
        // (onModalKeydown is bound to mInput alone). Left focusable, an <a href>
        // option would be picked up both by Tab and by onModalTabTrap's
        // 'a[href]' selector, moving real focus onto a row that reports
        // aria-selected="false" while the highlight and aria-activedescendant stay
        // on the arrow-selected one — and stranding the arrow keys. Same roving
        // treatment as the sidebar treeitems. Modified/middle clicks are
        // unaffected: href and the click handler stay as they were.
        row.tabIndex = -1;
        row.appendChild(el("span", "search-ico " + kindIconClass(r.kind)));
        var nameSpan = el("span", "search-name", r.name);
        // Overloads share a name; the parameter list is what tells them apart.
        if (r.sig) nameSpan.appendChild(el("span", "search-sig", r.sig));
        row.appendChild(nameSpan);
        if (r.type) row.appendChild(el("span", "search-context", r.type));
        // Hover follows the mouse (without scrolling the list — see
        // setActive); click navigates (htmx handles the load).
        row.addEventListener("mousemove", function () { setActive(idx, false); });
        row.addEventListener("click", function (ev) {
          // boostClick lets modified/middle clicks fall through to the native
          // href (Ctrl/Cmd/Shift-click, open-in-new-tab) and closes the modal
          // only when we actually navigate in place.
          boostClick(ev, absHref(r.href), closeSearchModal);
        });
        group.appendChild(row);
      });
      frag.appendChild(group);
    });
    box.appendChild(frag);

    // Everything known beyond what is shown: toc hits past the cap plus the
    // worker's full match count past what it returned (memberTotal), so the
    // note doesn't under-count when member matches were truncated at source.
    var overflow = (_tocMatchTotal + memberTotal) - all.length;
    var statusText = overflow > 0
      ? "+" + overflow + " more — refine your query to narrow results" : "";
    // Member results may still be on their way (index downloading / worker
    // ranking): say so, or their later pop-in looks like a glitch.
    if (memberPending) {
      statusText += (statusText ? " · " : "") + searchingMembersLabel();
    } else if (memberCoverage < 100 && !memberIndexIncomplete && !workerFailed) {
      // Not waiting on a reply, but the corpus is still growing — the
      // progress handler will re-query and re-render as chunks land.
      statusText += (statusText ? " · " : "")
        + memberCoverage + "% of the member index searched — more loading…";
    }
    if (memberIndexIncomplete && !workerFailed) {
      statusText += (statusText ? " · " : "")
        + "part of the member index failed to load; some members may be missing";
    }
    if (workerFailed) {
      statusText += (statusText ? " · " : "")
        + "Member results are unavailable — the search index could not be loaded.";
    }
    setStatus(statusText);

    if (currentRows.length) {
      if (input) input.setAttribute("aria-expanded", "true"); // listbox now has options
      var startIdx = 0;
      if (prevActiveHref) {
        for (var p = 0; p < currentRows.length; p++) {
          if (currentRows[p].href === prevActiveHref) { startIdx = p; break; }
        }
      }
      setActive(startIdx);
    }
  }

  // `scroll` defaults on: keyboard/programmatic selection brings the row into
  // view. Hover selection passes false — scrollIntoView on a row half-clipped
  // at the list edge scrolls a DIFFERENT row under the stationary cursor,
  // whose mousemove re-selects and re-scrolls (Chromium re-dispatches
  // mousemove after scrolling): a runaway selection cascade.
  function setActive(idx, scroll) {
    var box = document.getElementById("searchModalResults");
    if (!box) return;
    var rows = box.querySelectorAll(".search-result");
    var input = document.getElementById("searchModalInput");
    if (!rows.length) {
      activeIndex = -1;
      if (input) input.removeAttribute("aria-activedescendant");
      return;
    }
    if (idx < 0) idx = rows.length - 1;
    if (idx >= rows.length) idx = 0;
    for (var i = 0; i < rows.length; i++) {
      rows[i].classList.remove("is-active");
      rows[i].setAttribute("aria-selected", "false");
    }
    rows[idx].classList.add("is-active");
    rows[idx].setAttribute("aria-selected", "true");
    // Point the combobox at the active option so screen readers announce it
    // while focus stays in the input (ARIA activedescendant pattern).
    if (input && rows[idx].id) input.setAttribute("aria-activedescendant", rows[idx].id);
    if (scroll !== false && rows[idx].scrollIntoView) rows[idx].scrollIntoView({ block: "nearest" });
    activeIndex = idx;
  }

  /* ----------------------- Open / close / run ----------------------- */
  function modalOpen() {
    var modal = document.getElementById("searchModal");
    return modal && !modal.hidden;
  }

  // Open the modal, seeding its input with seedText (caret at end) and running
  // a query. Idempotent: a second call just re-seeds and re-queries.
  // focusInput === false leaves focus where it is: the mid-IME topbar handoff
  // shows live results while the composition keeps running in #sq — stealing
  // focus there would abort it (see onTopbarInput).
  function openSearchModal(seedText, focusInput) {
    var modal = document.getElementById("searchModal");
    var input = document.getElementById("searchModalInput");
    if (!modal || !input) return;
    // A transient index-load failure (network blip, CDN 5xx) must not disable
    // member search for the rest of the session — under hx-boost this module
    // lives across every in-place navigation, so a permanent latch could last
    // hours. Clear it so ensureWorker retries: at most once per modal open,
    // and never when the cause is permanent (workerUnsupported). The
    // !modalOpen() gate is what makes it "once per open": openSearchModal is
    // idempotent and re-fires per gesture while already open (IME-composing
    // topbar keystrokes, launcher re-focus) — clearing on those too would
    // spawn a fresh worker + index fetch per keystroke against a failing CDN.
    if (!modalOpen() && workerFailed && !workerUnsupported) workerFailed = false;
    // Same once-per-open rhythm for PARTIAL index loads: if some chunks failed
    // earlier (transient CDN error / timeout), ask the live worker to re-fetch
    // just those. The worker ignores this while a pass is already in flight,
    // and a progress message clears memberIndexIncomplete on success.
    if (!modalOpen() && memberIndexIncomplete && searchWorker && workerReady) {
      searchWorker.postMessage({ type: "retry" });
    }
    // Remember what had focus so we can restore it on close (a11y).
    if (!modalOpen()) lastFocus = document.activeElement;
    modal.hidden = false;
    document.body.classList.add("search-modal-open");
    input.value = seedText || "";
    if (focusInput !== false) {
      input.focus();
      // Caret to end (focus on some browsers selects all).
      try { var n = input.value.length; input.setSelectionRange(n, n); } catch (e) { /* ignore */ }
    }
    runSearch(input.value);
  }

  function closeSearchModal() {
    var modal = document.getElementById("searchModal");
    if (modal) modal.hidden = true;
    document.body.classList.remove("search-modal-open");
    var mInput = document.getElementById("searchModalInput");
    if (mInput) {
      mInput.removeAttribute("aria-activedescendant");
      mInput.setAttribute("aria-expanded", "false");
    }
    // Clear the topbar launcher box so it never shows a stale half-typed query.
    var sq = document.getElementById("sq");
    if (sq) sq.value = "";
    lastQuery = "";
    memberResults = [];
    memberTotal = 0;
    memberPending = false;
    currentRows = [];
    activeIndex = -1;
    searchSeq++;
    debouncedModalSearch.cancel();
    // Restore focus to whatever opened the modal (keyboard users land back where
    // they were, not at the top of the document).
    if (lastFocus && lastFocus.focus && document.contains(lastFocus)) {
      try { lastFocus.focus(); } catch (e) { /* ignore */ }
    }
    lastFocus = null;
  }

  // Run a search for q: instant toc matches now, member matches when the worker
  // replies. Empty q clears results but leaves the modal open.
  function runSearch(q) {
    q = (q || "").trim();
    lastQuery = q;
    searchSeq++;
    memberResults = [];
    memberTotal = 0;
    memberPending = false;
    if (!q) { renderModalResults(); return; }
    ensureWorker();
    // The worker owes a reply for this seq unless it is unavailable; until it
    // answers, the renderer says "searching members" instead of a premature
    // "No matches". Not-yet-ready still counts as pending — the "ready"
    // handler replays lastQuery. Cleared on the matching reply / workerFail.
    memberPending = !workerFailed && !workerUnsupported;
    postWorkerQuery(q);
    renderModalResults(); // show instant toc matches immediately
  }

  /* --------------------------- Input wiring ------------------------- */
  // Keystroke handoff: when the user types in the default-scope topbar box, open
  // the modal seeded with the typed text and clear the box (so the character is
  // not duplicated and the modal owns input). Using the "input" event guarantees
  // the value already includes the keystroke (also covers paste). IME composition
  // is the one exception: clearing the box or moving focus mid-composition
  // aborts the composition and commits its raw keystrokes as ASCII. But merely
  // WAITING for the commit is no good either — composing-by-default keyboards
  // (Android/Gboard underline ordinary Latin too) would show nothing until the
  // word commits. So while composing, open the modal for live results WITHOUT
  // the disruptive half (box keeps its text and focus, so the composition
  // continues); the real handoff (clear + focus) completes on compositionend
  // (wireSearch).
  function onTopbarInput(ev) {
    var input = document.getElementById("sq");
    if (!input) return;
    if (!isLocalScope()) return;       // external scope: leave the native form alone
    var v = input.value;
    if (!v) return;
    if (ev && ev.isComposing) {
      openSearchModal(v, false);       // results only; composition continues in #sq
      return;
    }
    input.value = "";
    openSearchModal(v);
  }

  function onModalKeydown(ev) {
    // Never hijack keys mid-IME-composition (Enter/arrows there commit or move
    // within the composition, not the result list). 229 covers older engines.
    if (ev.isComposing || ev.keyCode === 229) return;
    switch (ev.key) {
      case "ArrowDown": ev.preventDefault(); setActive(activeIndex + 1); break;
      case "ArrowUp":   ev.preventDefault(); setActive(activeIndex - 1); break;
      case "Enter":
        // Flush a pending debounce first, so Enter acts on what the input
        // shows — not on the previous render's results. Typing + a fast Enter
        // (palette muscle memory, or right after the topbar handoff) otherwise
        // navigates to the top hit of the query minus its last keystrokes.
        // Member (worker) matches for the flushed query can't exist yet, so a
        // member-only query leaves zero rows and this Enter is a no-op — the
        // deliberate trade-off: never navigate anywhere the user hasn't seen.
        debouncedModalSearch.flush();
        if (activeIndex >= 0 && currentRows[activeIndex]) {
          ev.preventDefault();
          var href = absHref(currentRows[activeIndex].href);
          closeSearchModal();
          navigateTo(href);
        }
        break;
      // No Escape case: the input's keydown bubbles to the document-level
      // onGlobalKeydown, whose modalOpen() branch closes the modal.
    }
  }

  // ⌘K / Ctrl-K and "/" open the modal from anywhere; Esc closes it. "/" is
  // ignored while typing in a field so it doesn't hijack normal input.
  function onGlobalKeydown(ev) {
    if (ev.isComposing || ev.keyCode === 229) return; // ignore IME composition
    var k = ev.key;
    if ((k === "k" || k === "K") && (ev.metaKey || ev.ctrlKey)) {
      ev.preventDefault();
      if (modalOpen()) closeSearchModal(); else openSearchModal("");
      return;
    }
    if (k === "/" && !modalOpen() && !isTypingTarget(ev.target)) {
      ev.preventDefault();
      openSearchModal("");
      return;
    }
    if (k === "Escape" && modalOpen()) { ev.preventDefault(); closeSearchModal(); }
  }

  function isTypingTarget(t) {
    if (!t) return false;
    var tag = t.tagName;
    return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || t.isContentEditable;
  }

  // Trap Tab focus within the open modal so keyboard users can't tab out into the
  // (inert) page behind it; Shift-Tab wraps backwards. Bound once on the panel.
  function onModalTabTrap(ev) {
    if (ev.key !== "Tab" || !modalOpen()) return;
    var modal = document.getElementById("searchModal");
    var focusables = modal.querySelectorAll(
      'input, button, a[href], [tabindex]:not([tabindex="-1"])');
    var visible = [];
    for (var i = 0; i < focusables.length; i++) {
      if (focusables[i].offsetParent !== null) visible.push(focusables[i]);
    }
    if (!visible.length) return;
    var first = visible[0], last = visible[visible.length - 1];
    if (ev.shiftKey && document.activeElement === first) {
      ev.preventDefault(); last.focus();
    } else if (!ev.shiftKey && document.activeElement === last) {
      ev.preventDefault(); first.focus();
    }
  }

  function wireSearch() {
    var input = document.getElementById("sq");
    if (input) {
      once(input, "data-search-wired", function () {
        input.addEventListener("input", onTopbarInput);
        // IME: composing input only surfaces results (see onTopbarInput); the
        // full handoff — clear the box, focus the modal — runs once the
        // composition commits. Deferred a tick: a composition also commits
        // when the user LEAVES the field (tap elsewhere dismissing the
        // keyboard, desktop IME blur), and stealing focus back then would
        // yank them into the modal they were abandoning. After the timeout,
        // that blur has landed and the focus check skips the handoff.
        input.addEventListener("compositionend", function () {
          setTimeout(function () {
            if (document.activeElement === input) onTopbarInput();
          }, 0);
        });
        // If the user re-focuses the launcher and it already has text, hand off.
        input.addEventListener("focus", function () { if (isLocalScope() && input.value) onTopbarInput(); });
      });
    }

    var mInput = document.getElementById("searchModalInput");
    if (mInput) {
      once(mInput, "data-search-wired", function () {
        mInput.addEventListener("input", debouncedModalSearch);
        mInput.addEventListener("keydown", onModalKeydown);
      });
    }

    // data-search-close elements (scrim + close button) dismiss the modal; the
    // panel also hosts the Tab focus-trap.
    var modal = document.getElementById("searchModal");
    if (modal) {
      once(modal, "data-search-wired", function () {
        var closers = modal.querySelectorAll("[data-search-close]");
        for (var i = 0; i < closers.length; i++) {
          closers[i].addEventListener("click", closeSearchModal);
        }
        modal.addEventListener("keydown", onModalTabTrap);
      });
    }

    // Global shortcuts: bind once at the document level (survives htmx swaps).
    once(document.documentElement, "data-search-global-bound", function () {
      document.addEventListener("keydown", onGlobalKeydown);
    });
  }

  /* ---------------------- External AWS search -----------------------
     Non-local scopes target the external AWS documentation search. The scope
     <select> (#sel) determines where it searches: "documentation*" scopes hit
     the docs search endpoint via docsSearchUrl above; everything else falls
     back to the site-wide AWS search. */
  function searchFormSubmit(formElement) {
    var scope = fieldValue("sel", "documentation-guide");
    var query = fieldValue("sq", "");

    // Default scope is handled locally (the search modal); never submit it to
    // the external search. Submitting the topbar form opens the modal seeded
    // with whatever was typed there.
    if (scope === LOCAL_SCOPE) {
      openSearchModal(query);
      return false;
    }

    var url;
    if (scope.indexOf("documentation") === 0) {
      url = docsSearchUrl(scope, query);
    } else {
      // The scope must ride along as searchPath (the old native form GET
      // submitted it); without it every non-documentation scope collapses to
      // the same unfiltered site search and the dropdown choice does nothing.
      url = "https://aws.amazon.com/search?searchQuery=" + enc(query)
          + "&searchPath=" + enc(scope);
    }

    window.open(url, "_blank", "noopener");
    return false; // we navigated ourselves; cancel the native submit
  }


  /* ===================================================================
     Page orchestration
     -------------------------------------------------------------------
     Per-page behavior that runs on first load and on every htmx
     navigation: highlight.js, copy buttons, the "In this article"
     dropdown, scroll handling, the nav progress bar, and the
     back-compat globals generated pages still call inline.
     =================================================================== */


  /* ---------------------------- highlight --------------------------- */
  function highlight() {
    if (window.hljs) {
      var blocks = document.querySelectorAll("#main pre code:not(.hljs)");
      for (var i = 0; i < blocks.length; i++) {
        try { window.hljs.highlightElement(blocks[i]); } catch (e) { /* ignore */ }
      }
    }
    addCopyButtons();
  }

  // A "Copy" affordance on every code block. Added here (not in the generator)
  // so it never appears for no-JS readers, for whom it could not work.
  function addCopyButtons() {
    if (!navigator.clipboard) return; // http:// or very old browser
    var pres = document.querySelectorAll("#main pre");
    for (var i = 0; i < pres.length; i++) {
      wireCopyButton(pres[i]);
    }
  }

  function wireCopyButton(pre) {
    once(pre, "copy-btn", function () {
      var code = pre.querySelector("code");
      if (!code) return;
      // An htmx history restore can bring this <pre> back with a serialized,
      // listener-less copy of a previously added button (the snapshot captures
      // innerHTML). Remove the dead copies before wiring a live one, or each
      // Back/Forward stacks one more inert button on the block.
      var stale = pre.querySelectorAll(".code-copy");
      for (var s = 0; s < stale.length; s++) {
        if (stale[s].parentNode) stale[s].parentNode.removeChild(stale[s]);
      }
      var btn = el("button", "code-copy", "Copy");
      btn.type = "button";
      btn.setAttribute("aria-label", "Copy code to clipboard");
      btn.addEventListener("click", function () {
        navigator.clipboard.writeText(code.textContent).then(function () {
          btn.textContent = "Copied";
          btn.classList.add("is-copied");
          setTimeout(function () {
            btn.textContent = "Copy";
            btn.classList.remove("is-copied");
          }, 1500);
        }).catch(function () { /* clipboard denied; leave the button as-is */ });
      });
      pre.appendChild(btn);
    });
  }

  // WCAG 2.1.1 (keyboard). The horizontal scroll container for a wide sample is
  // the inner `pre > code` — that is where overflow-x: auto lives — and a scroll
  // container that cannot take focus can only be panned with a pointer, so a
  // keyboard-only reader has no way to reach the right-hand end of a long
  // signature. tabindex="0" makes it a tab stop whose arrow keys scroll it
  // natively.
  //
  // role="group" and a name, not role="region": region is a LANDMARK, and a
  // class page with a dozen samples would put a dozen of them in the landmark
  // list. The role is not optional though — aria-label on an element with no
  // role is not reliably exposed, and an unnamed stop announces by reading out
  // the whole sample.
  //
  // Only blocks that actually overflow are marked; a tab stop on every one-line
  // snippet is noise. That depends on layout, hence the re-run on resize — which
  // is also what text zoom triggers, and zoom is exactly when a sample that used
  // to fit starts to scroll.
  function markScrollableCodeBlocks() {
    var blocks = document.querySelectorAll("#main pre > code");
    for (var i = 0; i < blocks.length; i++) {
      var code = blocks[i];
      // 1px of slack: sub-pixel layout reports a hairline overflow on blocks
      // that visibly fit.
      if (code.scrollWidth - code.clientWidth > 1) {
        code.setAttribute("tabindex", "0");
        code.setAttribute("role", "group");
        code.setAttribute("aria-label", "Code sample");
      } else if (code.getAttribute("tabindex") === "0") {
        // Widened (or zoomed back out) past the point of scrolling — a stop that
        // scrolls nothing is just an extra Tab press.
        code.removeAttribute("tabindex");
        code.removeAttribute("role");
        code.removeAttribute("aria-label");
      }
    }
  }
  window.addEventListener("resize", makeDebounce(markScrollableCodeBlocks, 200));

  /* --------------------- Per-page (htmx) helpers -------------------- */
  // Region disclaimer: show only on China (.cn) hosts.
  function applyRegionDisclaimer() {
    var d = document.getElementById("regionDisclaimer");
    if (!d) return;
    var host = window.location.host || "";
    if (/\.cn$/i.test(host)) d.style.display = "block";
    else if (d.parentNode) d.parentNode.removeChild(d);
  }

  function applyAssemblyVersion() {
    var holder = document.getElementById("assemblyVersion");
    if (!holder) return;
    var vfile = holder.getAttribute("data-version-file");
    var svc = holder.getAttribute("data-service");
    if (!vfile) return;

    function hideVersion() {
      var vd = document.getElementById("versionData");
      if (vd) vd.style.display = "none";
    }

    // DATA_VQ: same cache-busting the sidebar/search data fetches carry, so a
    // CDN can't serve a stale version manifest with new pages.
    fetch(new URL(vfile + (DATA_VQ || ""), window.location.href).href)
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        if (!data) { hideVersion(); return; }
        var v = (svc && data.ServiceVersions && data.ServiceVersions[svc])
          ? data.ServiceVersions[svc].Version
          : data.CoreVersion;
        if (v) holder.textContent = v;
        else hideVersion();
      })
      .catch(hideVersion);
  }

  function applyCopyright() {
    var n = document.getElementById("awsdocs-legal-zone-copyright");
    if (n && !n.innerHTML.trim()) {
      n.innerHTML = "&copy; Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.";
    }
  }

  /* ----- In-article TOC (the #pageTOC widget on class pages) --------
     The markup carries an inline onclick="toggleTOC()" on the heading, so the
     global toggleTOC (exported at the bottom of this file) is the single source
     of truth. We do NOT add a second listener here (that caused a double-toggle
     that cancelled itself out). We just normalize the initial state and close
     it on an outside click. */
  function wirePageToc() {
    var toc = document.getElementById("pageTOC");
    if (!toc) return;
    // The generator renders #pageTOC inside #pageHeader (right of the title).
    // Collapsed by default: the dropdown panel (tocList) is hidden until the
    // user clicks the "In this article" button.
    var list = document.getElementById("tocList");
    var toggle = document.getElementById("tocToggle");
    if (list) list.style.display = "none";
    if (toggle) toggle.textContent = "▾"; // down-pointing affordance (closed)
    var btn = toc.querySelector("h2 button");
    if (btn) btn.setAttribute("aria-expanded", "false");
    // Close the dropdown when clicking outside of it (bound once at document level).
    once(document.documentElement, "data-pagetoc-bound", function () {
      document.addEventListener("click", function (ev) {
        var t = document.getElementById("pageTOC");
        var l = document.getElementById("tocList");
        if (!t || !l) return;
        if (getComputedStyle(l).display === "none") return;
        // A click on one of the dropdown's own links is a selection, not
        // "outside" — close it too, or the open panel sits over the content
        // the user just jumped to (fragment links are unboosted, so no htmx
        // swap re-runs wirePageToc to reset the state).
        var link = ev.target && ev.target.closest ? ev.target.closest("a") : null;
        if (link && l.contains(link)) { setPageTocOpen(false); return; }
        if (!t.contains(ev.target)) setPageTocOpen(false);
      });
    });
  }

  function setPageTocOpen(open) {
    var list = document.getElementById("tocList");
    var toggle = document.getElementById("tocToggle");
    if (!list || !toggle) return;
    list.style.display = open ? "block" : "none";
    toggle.textContent = open ? "▴" : "▾"; // open / closed affordance
    var btn = document.querySelector("#pageTOC h2 button");
    if (btn) btn.setAttribute("aria-expanded", open ? "true" : "false");
  }

  function togglePageToc() {
    var list = document.getElementById("tocList");
    if (!list) return;
    setPageTocOpen(getComputedStyle(list).display === "none");
  }

  /* --------------------------- Page init ---------------------------- */
  // `swapped` is true when called from htmx:afterSwap (an in-place navigation),
  // false on the initial DOMContentLoaded. Focus/announcement only happen on a
  // swap — on first paint the native document load already handles both.
  function onPageLoad(swapped) {
    wireChrome();
    closeDrawer();
    ensureRoot();
    loadSidebar();
    wireSearch();
    closeSearchModal(); // dismiss the modal after an in-place navigation
    syncActive();
    highlight();
    markScrollableCodeBlocks(); // after highlight(): hljs rewrites the block first
    applyRegionDisclaimer();
    applyAssemblyVersion();
    applyCopyright();
    wirePageToc();
    if (swapped === true) focusAndAnnounceAfterSwap();
  }

  // After an in-place (htmx) swap the whole #main is replaced, so focus falls back
  // to <body> and screen readers get no signal that the page changed. Move focus to
  // the new #main (tabindex="-1", non-Tab-stop) and announce the new title in the
  // persistent live region so keyboard/SR users land in — and are told about — the
  // new content. §7.4 of the modernization design requires this explicitly.
  function focusAndAnnounceAfterSwap() {
    var main = document.getElementById("main");
    if (main) {
      try { main.focus({ preventScroll: true }); } catch (e) { main.focus(); }
    }
    var live = document.getElementById("navAnnounce");
    if (live) {
      var h1 = main && main.querySelector("h1");
      var label = (h1 && h1.textContent.trim()) || document.title;
      // Reassigning textContent (even to the same string is rare here) triggers the
      // aria-live announcement.
      live.textContent = label;
    }
  }

  /* ------------------ <head> metadata across swaps ------------------ */
  // hx-select="#main" means a boosted response's <head> is discarded entirely:
  // htmx updates document.title and the URL, and nothing else. So the tags that
  // identify the page — the canonical URL, the description, and the platform's
  // aws-tocid — keep advertising the FIRST page of the session for every page
  // after it. That is not cosmetic here: trackVirtualPageView below deliberately
  // lets the platform build its beacon from the live document, so an un-synced
  // head reports every later page view under the landing page's identity (land
  // on TAmazonS3Client, click through to TAmazonEC2Client, and the EC2 view is
  // still attributed to S3 for the rest of the session). Anything else reading
  // the document — a feedback widget, a share action, a crawler that executes
  // scripts — sees the same stale facts.
  //
  // These values exist only in the response body, which only htmx:beforeSwap
  // sees, so they are captured there and applied on the matching afterSwap.
  // Back/Forward is covered too: a history restore re-swaps content htmx cached
  // (or re-fetches the page) and likewise never touches the head, and once one
  // in-place navigation has happened, leaving the head alone is no longer the
  // right answer for a restore either.
  //
  // The three per-page tags, as [tag, keyAttr, keyValue, valueAttr]. Mirrors what
  // DocShell.WriteHeadAndChrome emits; a null value means "the page has no such tag",
  // which is a real case (canonical is conditional on Options.CanonicalUrl).
  var HEAD_META_TAGS = [
    ["link", "rel", "canonical", "href"],
    ["meta", "name", "description", "content"],
    ["meta", "name", "aws-tocid", "content"]
  ];
  // Per-DOCUMENT table so Back/Forward can restore a head htmx never sends again.
  // Bounded: a session of in-place navigation can cover thousands of pages, and
  // this is only ever read for an entry still reachable in history. It does not
  // outlive a reload (htmx's own snapshot cache does, in sessionStorage), so a
  // Back across one lands on a cache hit with no head to restore — which is the
  // pre-existing behavior of leaving the head alone, not a new wrong value.
  var HEAD_META_MAX = 50;
  var _headMetaByPath = {};
  var _headMetaOrder = [];
  var _pendingHeadMeta = null;

  // Values in HEAD_META_TAGS order, or null if there is nothing to read.
  function collectHeadMeta(scope) {
    if (!scope) return null;
    var out = [];
    for (var i = 0; i < HEAD_META_TAGS.length; i++) {
      var t = HEAD_META_TAGS[i];
      var n = scope.querySelector(t[0] + "[" + t[1] + "=\"" + t[2] + "\"]");
      out.push(n ? n.getAttribute(t[3]) : null);
    }
    return out;
  }

  // Parses only the head of a full response: it is a couple of KB, and the body
  // (an API table of any size) is irrelevant here — htmx has already parsed that
  // for hx-select. No "</head>" at all means this is not a whole page, so take
  // nothing rather than guess from a prefix cut mid-tag.
  function readHeadMeta(responseText) {
    if (typeof responseText !== "string") return null;
    var end = responseText.indexOf("</head>");
    if (end === -1) return null;
    return collectHeadMeta(sharedParser().parseFromString(responseText.slice(0, end), "text/html"));
  }

  function applyHeadMeta(values) {
    if (!values || !document.head) return;
    for (var i = 0; i < HEAD_META_TAGS.length; i++) {
      var t = HEAD_META_TAGS[i];
      var node = document.head.querySelector(t[0] + "[" + t[1] + "=\"" + t[2] + "\"]");
      var value = values[i];
      if (value == null) {
        // The new page carries no such tag — removing ours beats leaving the
        // previous page's value pointing at the wrong document.
        if (node && node.parentNode) node.parentNode.removeChild(node);
        continue;
      }
      if (!node) {
        node = document.createElement(t[0]);
        node.setAttribute(t[1], t[2]);
        document.head.appendChild(node);
      }
      node.setAttribute(t[3], value);
    }
  }

  // Keyed on pathname+search so a forward navigation (which records
  // location.href) and a restore (which reports htmx's own
  // "pathname+search" path) agree. The hash is dropped on purpose: one document,
  // one head, whichever member row the reader deep-linked to.
  function headMetaKey(path) {
    try {
      var u = new URL(path, window.location.href);
      return u.pathname + u.search;
    } catch (e) { return null; }
  }

  function rememberHeadMeta(path, values) {
    var key = values && headMetaKey(path);
    if (!key) return;
    var at = _headMetaOrder.indexOf(key);
    if (at !== -1) _headMetaOrder.splice(at, 1);
    _headMetaOrder.push(key);            // most-recently-visited last
    _headMetaByPath[key] = values;
    while (_headMetaOrder.length > HEAD_META_MAX) {
      delete _headMetaByPath[_headMetaOrder.shift()];
    }
  }

  function recallHeadMeta(path) {
    var key = headMetaKey(path);
    if (!key || !Object.prototype.hasOwnProperty.call(_headMetaByPath, key)) return null;
    return _headMetaByPath[key];
  }

  // Back/Forward. A cache MISS re-fetched the page, so its head is in the event;
  // a HIT only carries the cached history-element content, so the head comes from
  // the table above. Registered separately from the scroll handler on the same
  // event — that one returns early per branch.
  document.addEventListener("htmx:historyRestore", function (ev) {
    var d = ev.detail || {};
    var path = d.path || window.location.href;
    var restored = d.cacheMiss ? readHeadMeta(d.serverResponse) : recallHeadMeta(path);
    if (!restored) return;
    applyHeadMeta(restored);
    rememberHeadMeta(path, restored);
  });

  /* ---------------------- Analytics page views ---------------------- */
  // In-place navigation has to report its own page views. The docs platform
  // bootstrap (awsdocs-boot.js, loaded once per page by DocShell) sends its
  // Adobe beacon on document load and instruments nothing else — it predates
  // this doc set being navigated in place. The CloudWatch RUM plugin alongside
  // it patches pushState, so RUM already counts boosted navigations; the Adobe
  // stream does not, and left alone every page after the first in a session goes
  // unreported. That shows up as a traffic cliff on release day and reads as a
  // real drop in doc usage rather than as an instrumentation gap.
  //
  // Dispatched through AWSMA's own trigger event so the platform builds the
  // beacon from the live document instead of this file assembling one — nothing
  // here needs to know the report suite, the page-name scheme, or the consent
  // state. No detail payload is attached for the same reason: the trigger is the
  // signal, and inventing a shape risks the platform reading a field we guessed
  // at. Guarded on AWSMA existing, so a local preview or a standalone copy of
  // the doc set (no platform script) is a silent no-op.
  var _lastTrackedHref = null;
  function trackVirtualPageView() {
    var ma = window.AWSMA;
    if (!ma) return;
    // A swap that leaves the URL alone is not a new page view, and afterSwap can
    // fire more than once for one navigation.
    if (window.location.href === _lastTrackedHref) return;
    _lastTrackedHref = window.location.href;
    var trigger = typeof ma.TRIGGER_EVENT === "string" ? ma.TRIGGER_EVENT : "custom_awsma_trigger";
    try {
      document.dispatchEvent(new CustomEvent(trigger));
    } catch (e) { /* no CustomEvent constructor — nothing to report, not fatal */ }
  }

  // htmx:load fires only for htmx-swapped content, NOT the initial document, so
  // drive init from both: DOMContentLoaded for first paint, htmx:afterSwap for
  // every in-place navigation. onPageLoad is idempotent (once() guards) so a
  // double-invocation is harmless.
  document.addEventListener("DOMContentLoaded", function () {
    onPageLoad(false);
    // The platform counted this load itself; record it so the first swap is
    // measured against it.
    _lastTrackedHref = window.location.href;
    // This document's own head, banked before any swap overwrites it, so Back to
    // the page the session started on restores the right identity.
    rememberHeadMeta(window.location.href, collectHeadMeta(document.head));
  });
  document.addEventListener("htmx:afterSwap", function () {
    // Before any per-page setup runs: make the head describe the page that just
    // landed. The swap never carries it (hx-select="#main"), so it comes from
    // what beforeSwap read off the response. Null on a history restore — that
    // path applies its own, above.
    applyHeadMeta(_pendingHeadMeta);
    onPageLoad(true);
    // Deferred a task: htmx sets the URL and <title> as part of handling the
    // navigation, and the platform reads both off the live document. The URL is
    // also what keys the head-metadata table, so bank this page's head here too,
    // once location is authoritative.
    setTimeout(function () {
      rememberHeadMeta(window.location.href, _pendingHeadMeta);
      _pendingHeadMeta = null;
      trackVirtualPageView();
    }, 0);
  });

  // Scroll handling for in-place navigation. htmx's `scroll:top` targets the swapped
  // element (#main), which is not a scroll container in this layout (the window
  // scrolls), so it is a no-op — a boosted/search/sidebar nav would otherwise keep
  // the previous scroll offset. Scroll the window to top on each swap, UNLESS the
  // target URL carries a #fragment, so member deep-links (e.g. #prop_Foo) still land
  // on their row. Runs after settle so the swapped-in content/anchor exists.
  //
  // Two things fight this and must be neutralized: (1) the browser's automatic
  // scroll restoration on history navigation (htmx pushes history), and (2) the
  // global `scroll-behavior: smooth` on <html>, which would animate — and let htmx's
  // own scrollIntoViewOnBoost interrupt — the reset. So take over scroll restoration
  // and force an instant jump.
  if ("scrollRestoration" in history) history.scrollRestoration = "manual";

  // Manual scrollRestoration also disables the browser's restore on plain
  // reloads (F5) and full-load back/forward. Compensate: remember the offset
  // on pagehide and re-apply it when the SAME URL comes back via one of those
  // navigation types (never on fresh link navigations, and a #fragment wins).
  var SCROLL_KEY = "awsdocs-scroll";
  window.addEventListener("pagehide", function () {
    try {
      sessionStorage.setItem(SCROLL_KEY,
        JSON.stringify({ href: window.location.href, y: window.scrollY || 0 }));
    } catch (e) { /* private mode */ }
  });
  document.addEventListener("DOMContentLoaded", function () {
    var entries = (window.performance && performance.getEntriesByType)
      ? performance.getEntriesByType("navigation") : [];
    var navType = entries.length ? entries[0].type : "";
    if (navType !== "reload" && navType !== "back_forward") return;
    if (window.location.hash) return;
    try {
      var saved = JSON.parse(sessionStorage.getItem(SCROLL_KEY) || "null");
      if (saved && saved.href === window.location.href && saved.y > 0) {
        window.scrollTo(0, saved.y);
      }
    } catch (e) { /* ignore */ }
  });

  // Instant (not smooth): behavior "auto" follows the CSS scroll-behavior of
  // the scroller, and <html> has scroll-behavior: smooth — an animated reset
  // can be interrupted mid-flight by a wheel/keypress, stranding the reader
  // mid-page on the new document. "instant" bypasses the CSS; the fallback
  // forces it via the style for engines without the enum.
  function scrollTopInstant() {
    if (!window.scrollTo) return;
    try { window.scrollTo({ top: 0, left: 0, behavior: "instant" }); }
    catch (e) {
      var prev = document.documentElement.style.scrollBehavior;
      document.documentElement.style.scrollBehavior = "auto";
      window.scrollTo(0, 0);
      document.documentElement.style.scrollBehavior = prev;
    }
  }

  // If the current URL carries a #fragment whose target exists in the (settled)
  // content, land on it — member deep-links like "…#prop_Foo" — else go to top.
  function scrollToHashTargetOrTop() {
    var hash = window.location.hash;
    if (hash && hash.length > 1) {
      // decodeURIComponent throws URIError on malformed escapes ("#%"); fall
      // back to the raw fragment so a bad link degrades to a top scroll (or a
      // literal-id match) instead of an uncaught error in the settle handler.
      var id = hash.slice(1);
      try { id = decodeURIComponent(id); } catch (e) { /* keep raw */ }
      var target = document.getElementById(id);
      if (target && target.scrollIntoView) {
        markTargetRow(target);
        try { target.scrollIntoView({ behavior: "instant", block: "start" }); }
        catch (e) { target.scrollIntoView(); }
        return;
      }
    }
    scrollTopInstant();
  }

  document.addEventListener("htmx:afterSettle", function (ev) {
    // History restores (Back/Forward) also settle, but with no requestConfig in
    // the event detail. On a cache HIT htmx re-applies the scroll position it
    // saved with the snapshot — resetting to top would throw the reader's place
    // away. (Cache misses are handled off htmx:historyRestore below.)
    if (!ev.detail || !ev.detail.requestConfig) return;
    scrollToHashTargetOrTop();
  });

  // History restore that MISSED the snapshot cache: htmx re-fetches the page
  // but applies no scroll (only cache hits carry a saved offset), and native
  // restoration is off (scrollRestoration = manual) — without this the viewport
  // keeps the previous page's arbitrary offset. Misses are routine here: the
  // snapshots of large API pages shed quickly from sessionStorage. The event
  // fires after the synchronous swap, so a restored deep-link's #fragment
  // target is already in the DOM and wins over the top-of-page reset.
  var _restoreScrollBehaviorTimer = null;
  document.addEventListener("htmx:historyRestore", function (ev) {
    if (ev.detail && ev.detail.cacheMiss) {
      // A same-document return that the veto below didn't intercept (htmx
      // re-fetched the page we were already on): land on the pre-jump offset
      // rather than the top of the page.
      var origin = fragmentReturnOffset();
      if (origin !== null) {
        scrollToOffsetInstant(origin);
        return;
      }
      scrollToHashTargetOrTop();
      return;
    }
    // Cache HIT: htmx re-applies the snapshot's saved offset via a bare
    // window.scrollTo in a setTimeout(0) — which follows the scroller's CSS
    // scroll-behavior, and <html> is `smooth`, so the restore animates and a
    // wheel/keypress can interrupt it mid-flight (the exact failure
    // scrollTopInstant exists to avoid). Suppress smooth around that restore.
    // Timing: this lift runs in a 60ms timeout registered after htmx's 0ms
    // one, so the override is still in force when htmx's scroll fires. The
    // lift clears the inline style (nothing else sets one persistently)
    // rather than restoring a captured value: two restores inside the window
    // would capture "auto" as the "previous" value and latch it forever.
    clearTimeout(_restoreScrollBehaviorTimer);
    document.documentElement.style.scrollBehavior = "auto";
    _restoreScrollBehaviorTimer = setTimeout(function () {
      document.documentElement.style.scrollBehavior = "";
    }, 60);
  });

  // Instant scroll to a remembered offset — see scrollTopInstant on why a smooth
  // restore is the wrong thing (it animates, and an animation can be interrupted).
  function scrollToOffsetInstant(y) {
    try { window.scrollTo({ top: y, left: 0, behavior: "instant" }); }
    catch (e) { window.scrollTo(0, y); }
  }

  // Native same-document fragment traversal gets no scroll handling at all
  // with scrollRestoration = "manual": an "In this article" #anchor click
  // pushes a normal null-state entry (fragment-only hrefs are never boosted),
  // and on Back/Forward the browser suppresses both its scroll restore and
  // its fragment re-scroll — the URL changes, the viewport doesn't. Handle
  // those traversals here. The state.htmx guard is load-bearing: htmx
  // replaceStates {htmx:true} onto every entry it manages and runs its own
  // restore for them on popstate — scrolling here too would fight it. Those
  // entries are handled instead by the historyCacheHit/Miss pair below.
  //
  // Backward direction: Back from "page#a" to "page" has no fragment to land
  // on, and scrolling to top would strand the reader far from where they were
  // before the jump. So the moment a fragment-only anchor is clicked, the
  // pre-jump offset is remembered two ways: stamped on the CURRENT history entry
  // (merged into whatever state htmx put there — the htmx flag must survive) for
  // entries htmx does not manage, and in the table below for the ones it does.
  //
  // The table is not redundant. htmx replaceStates a bare {htmx:true} over the
  // current entry at the END of saveCurrentPageToHistory, which it runs at the
  // top of every restore AND before every boosted request — so on any page
  // reached by in-place navigation, which is the common case, the stamp is wiped
  // before it can ever be read back. Re-stamping from htmx:beforeHistorySave
  // does not work either: that event fires just BEFORE the same replaceState,
  // so the wipe still lands last.
  //
  // Keyed by path+search, the shape htmx keys its own history cache by: the
  // fragment is exactly what differs between the two entries, so it cannot be
  // part of the key. In-memory and bounded — a real document load has the
  // browser's restoration and SCROLL_KEY above to fall back on, and starting
  // empty is what stops an offset from outliving the session that recorded it.
  var ANCHOR_ORIGIN_LIMIT = 32;
  var anchorOrigins = []; // [{ key, y }], first-seen order; oldest key evicted first

  function docHistoryKey() {
    return window.location.pathname + window.location.search;
  }

  function rememberAnchorOrigin(key, y) {
    for (var i = 0; i < anchorOrigins.length; i++) {
      if (anchorOrigins[i].key === key) { anchorOrigins[i].y = y; return; }
    }
    anchorOrigins.push({ key: key, y: y });
    if (anchorOrigins.length > ANCHOR_ORIGIN_LIMIT) anchorOrigins.shift();
  }

  function anchorOriginFor(key) {
    for (var i = 0; i < anchorOrigins.length; i++) {
      if (anchorOrigins[i].key === key) return anchorOrigins[i].y;
    }
    return null;
  }

  document.addEventListener("click", function (ev) {
    if (ev.defaultPrevented) return;
    var a = ev.target && ev.target.closest ? ev.target.closest('a[href^="#"]') : null;
    if (!a) return;
    var y = window.scrollY || 0;
    rememberAnchorOrigin(docHistoryKey(), y);
    var st = history.state || {};
    st.awsScrollY = y;
    try { history.replaceState(st, "", window.location.href); } catch (e) { /* quota/sandbox */ }
  });
  window.addEventListener("popstate", function (ev) {
    if (ev.state && ev.state.htmx) return;
    if (!window.location.hash && ev.state && typeof ev.state.awsScrollY === "number") {
      // Returning to a stamped pre-jump entry: restore its offset.
      scrollToOffsetInstant(ev.state.awsScrollY);
      return;
    }
    scrollToHashTargetOrTop();
  });

  // The htmx-managed half of the same story. When Back lands on an entry htmx
  // owns it runs its own restore, and for a return from "page#a" to "page" that
  // restore is pure waste: the document is already mounted, so htmx re-swaps
  // identical content (discarding in-page state, re-announcing the page to
  // screen readers, and on a cache MISS re-fetching it over the network) and
  // then applies the scroll offset it saved microseconds earlier at the top of
  // the same restore — which is the anchor the reader is trying to leave. Both
  // restore paths are cancelable, so veto them and do the only thing that has to
  // happen: put the viewport back where the reader was before the jump.
  //
  // The same-document test is deliberately narrow, because the opposite error —
  // vetoing a restore that DID need to swap — would leave the reader on the
  // wrong page under the right URL. All three conditions must hold:
  //   * the target URL carries no fragment, so there is nothing to scroll TO;
  //   * htmx's own idea of the mounted path matches the path being restored;
  //   * a fragment jump was recorded FROM that exact path, which only a click in
  //     the live document can do.
  // The last one carries the weight: htmx falls back to location.pathname for
  // its saved path whenever it has not navigated in-place yet, and during a
  // popstate location already reads as the restore target — so the second
  // condition alone can be trivially true on a cross-document restore.
  var _htmxSavedPath = null;
  document.addEventListener("htmx:beforeHistorySave", function (ev) {
    _htmxSavedPath = (ev.detail && ev.detail.path) || null;
  });

  function fragmentReturnOffset() {
    if (window.location.hash) return null;
    var key = docHistoryKey();
    if (_htmxSavedPath !== key) return null;
    return anchorOriginFor(key);
  }

  function vetoRedundantRestore(ev) {
    var y = fragmentReturnOffset();
    if (y === null) return;
    ev.preventDefault();
    scrollToOffsetInstant(y);
  }
  // Vetoing the swap also skips htmx's own "current path" bookkeeping, which is
  // harmless here and only here: the path it would record is the one it already
  // has — that equality is the gate.
  document.addEventListener("htmx:historyCacheHit", vetoRedundantRestore);
  document.addEventListener("htmx:historyCacheMiss", vetoRedundantRestore);

  /* ------------------- Failed history restores ---------------------- */
  // The history cache holds 10 entries, so going back far enough misses it and
  // htmx re-fetches the page (refreshOnHistoryMiss stays false — the cheap
  // #main swap is the whole point). That fetch can fail, and htmx does not
  // recover: on a non-2xx it fires htmx:historyCacheMissLoadError and swaps
  // nothing, and on a transport failure (offline, DNS, reset connection) its
  // xhr has no onerror at all, so nothing happens whatsoever. Either way
  // popstate has ALREADY moved the address bar, so silence leaves the reader on
  // the previous page's content under the restored page's URL — the same
  // URL-lies-about-content state the boosted-navigation fallbacks above exist
  // to prevent, which is why this one gets the same treatment.
  //
  // reload(), not assign(path): popstate has put the target URL in the address
  // bar already, so a reload re-fetches exactly the document the URL now claims
  // and cannot be turned into a same-URL no-op the way assign() can. A browser
  // error page is a truthful outcome here; stale content is not. This runs at
  // most once per failed restore — a document load cannot re-enter it.
  function reloadAfterFailedRestore() { window.location.reload(); }
  document.addEventListener("htmx:historyCacheMissLoadError", reloadAfterFailedRestore);
  // Transport failures never reach that event, so watch the xhr htmx hands over
  // with the miss event itself. Skipped when the miss was vetoed just above:
  // htmx gates .send() on this event, so a vetoed request is never sent and its
  // xhr can only ever stay silent. Deliberately only "error": there is no
  // "timeout" to hear (htmx never sets xhr.timeout on this request), and
  // "abort" here is the browser tearing down in-flight requests as the document
  // unloads — reloading then would fight the navigation the reader just asked
  // for.
  document.addEventListener("htmx:historyCacheMiss", function (ev) {
    if (ev.defaultPrevented) return;
    var xhr = ev.detail && ev.detail.xhr;
    if (xhr) xhr.addEventListener("error", reloadAfterFailedRestore);
  });

  // CSS :target only updates on native fragment navigation, not pushState, so
  // htmx deep-links (e.g. a search result to "…#prop_Foo") get an explicit
  // class carrying the same highlight (see tr:target / .is-target-row in CSS).
  function markTargetRow(target) {
    var prev = document.querySelectorAll(".is-target-row");
    for (var i = 0; i < prev.length; i++) prev[i].classList.remove("is-target-row");
    target.classList.add("is-target-row");
  }

  /* ------------------------- Nav progress bar ----------------------- */
  // htmx has no built-in progress bar; drive a slim top bar off request events.
  // The element is looked up per event (never captured in a closure): an htmx
  // history restore can replace it with a fresh node, which a captured
  // reference would silently stop controlling.
  function setNavProgress(active) {
    var bar = document.getElementById("navProgress");
    if (!bar) return;
    bar.hidden = !active;
    bar.classList.toggle("is-active", active);
  }
  document.addEventListener("htmx:beforeRequest", function () { setNavProgress(true); });
  document.addEventListener("htmx:afterRequest", function () { setNavProgress(false); });
  // Safety net: clear the bar once content has settled.
  document.addEventListener("htmx:afterSettle", function () { setNavProgress(false); });

  /* ----------------------- Failed navigations ----------------------- */
  // htmx fails closed: a 4xx/5xx response or a network error swaps nothing, so
  // a boosted click would silently do nothing. Fall back to a full navigation —
  // the browser then shows its real error page (or the server's 404 page).
  function fullNavigationFallback(ev) {
    var d = ev.detail || {};
    var path = (d.pathInfo && (d.pathInfo.finalRequestPath || d.pathInfo.requestPath))
            || (d.requestConfig && d.requestConfig.path);
    if (path) window.location.assign(path);
  }
  document.addEventListener("htmx:responseError", fullNavigationFallback);
  document.addEventListener("htmx:sendError", fullNavigationFallback);

  // A 200 whose body contains no id="main" is worse than an error status:
  // hx-select="#main" + hx-swap="outerHTML" with an empty selection REMOVES
  // the current #main, and with the swap target gone every later boosted
  // click dies in htmx's targetError — navigation is bricked until a hard
  // reload. Veto the swap and navigate for real instead. (A string probe, not
  // a parse: every generated page carries a literal id="main".)
  document.addEventListener("htmx:beforeSwap", function (ev) {
    var d = ev.detail || {};
    if (typeof d.serverResponse === "string" && d.serverResponse.indexOf('id="main"') === -1) {
      d.shouldSwap = false;
      fullNavigationFallback(ev);
      return;
    }
    // The only point in the swap where the response's <head> still exists — see
    // the "<head> metadata across swaps" section. Applied on afterSwap.
    _pendingHeadMeta = readHeadMeta(d.serverResponse);
  });
  // Belt and braces: if #main is somehow already gone, recover with a full
  // load. targetError's detail carries no request path (htmx 2.0.10 triggers
  // it as {error, target} only — fullNavigationFallback would silently no-op
  // here), but the event fires ON the element htmx was processing, so the
  // destination is recoverable from the anchor itself.
  document.addEventListener("htmx:targetError", function (ev) {
    var a = ev.target && ev.target.closest ? ev.target.closest("a[href]") : null;
    if (a && a.href) window.location.assign(a.href);
  });

  /* ----------------------- Inline-handler API ----------------------- */
  // The only inline calls the generator emits: the topbar form's onsubmit
  // (DocShell.WriteSearchForm) and the in-article TOC heading's onclick.
  window.AWSHelpObj = { searchFormSubmit: searchFormSubmit };
  window.toggleTOC = togglePageToc;
})();
