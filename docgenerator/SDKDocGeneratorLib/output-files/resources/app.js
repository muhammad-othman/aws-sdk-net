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
  // every CSS/JS link with it); versions the worker URL the same way.
  var ASSET_V = (function () {
    try {
      var src = document.currentScript && document.currentScript.src;
      var m = src && src.match(/[?&]v=([0-9a-f]+)/i);
      return m ? m[1] : "";
    } catch (e) { return ""; }
  })();

  // Fingerprint for the runtime-fetched data files (toc.json, search index,
  // _sdk-versions.json), from <body data-datav>; appended as ?v= so a CDN
  // can't pair new pages with stale data. DATA_VQ is the ready "?v=…".
  var DATA_V = (document.body && document.body.getAttribute("data-datav")) || "";
  var DATA_VQ = DATA_V ? "?v=" + DATA_V : "";

  /* ------------------------------- Theme ---------------------------- */
  function preferredTheme() {
    try {
      var saved = localStorage.getItem(THEME_KEY);
      if (saved === "light" || saved === "dark") return saved;
    } catch (e) { /* private mode */ }
    return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
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
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", function (ev) {
    try { if (localStorage.getItem(THEME_KEY)) return; } catch (e) { /* private mode */ }
    applyTheme(ev.matches ? "dark" : "light");
  });

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

  // Freeze the chrome's depth-relative links to absolute URLs now: hx-boost
  // resolves the RAW href attribute against the CURRENT document URL at click
  // time, so after a cross-depth swap (items/… vs items/<svc>/…) a relative
  // href would resolve to items/items/… and 404. Fragment-only hrefs (skip
  // link) stay — htmx ignores them, and absolutizing would make them boostable.
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

  // toc.json stores HTML-encoded display names (generics as "Foo&lt;&gt;"); we
  // render via textContent, so decode first. DOMParser (not the detached-
  // textarea innerHTML trick): its documents have no browsing context, so
  // nothing in the parsed string can load or execute. Hot paths precompute
  // decoded names once instead of calling this per keystroke — see loadSidebar.
  var _decoderParser = null;
  function sharedParser() {
    if (!_decoderParser) _decoderParser = new DOMParser();
    return _decoderParser;
  }
  function decodeEntities(s) {
    if (s == null) return s;
    if (s.indexOf("&") === -1) return s;
    return sharedParser().parseFromString(s, "text/html").documentElement.textContent;
  }

  // Navigate via htmx when available (in-place #main swap + history push), else a
  // normal load. `push` records the URL in history so it behaves like navigation.
  function navigateTo(href) {
    if (window.htmx) {
      window.htmx.ajax("GET", href, {
        target: "#main", select: "#main", swap: "outerHTML", push: href
      });
    } else {
      window.location.assign(href);
    }
  }

  // Idempotent wiring guard: runs fn() the first time a (node, key) is seen, so
  // listeners bind once even though onPageLoad runs on every htmx:afterSwap.
  // Must be JS state (WeakMap on the live node), never a DOM attribute: htmx's
  // history restore serializes attributes but not listeners, so an attribute
  // sentinel would come back "already wired" on listener-less restored nodes.
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



  // Sidebar and search-result links are built in JS after hx-boost initialized,
  // so htmx never boosts them. Route left-clicks through navigateTo() for an
  // in-place swap; leave modified clicks (Ctrl/Cmd/middle/shift) and the real
  // href alone so open-in-new-tab and the no-JS fallback keep working.
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
  // The exact #sidebarNav element the tree was last rendered into — identity,
  // NOT a boolean: an htmx history restore can replace the sidebar DOM with a
  // listener-less or empty copy while module state survives, and a boolean
  // would report "built" forever. A differing element triggers a re-render.
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

  // The in-flight toc.json fetch, shared by concurrent loadSidebar calls (every
  // htmx swap re-runs it) — duplicate fetches would each rebuild the tree,
  // collapsing the user's expansions. Reset to null on failure so a later
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
        // Precompute decoded/lowercase/acronym forms once: the filter and the
        // modal's toc scan visit every name per keystroke on the main thread,
        // and deriving these per row would allocate ~100k strings a keystroke.
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
        // A modal query typed while toc.json was in flight had no toc hits;
        // refresh the open modal so they appear the moment data lands.
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
      var li = document.querySelector('#sidebarNav .toc-node[data-id="' + CSS.escape(id) + '"]');
      if (li) setExpanded(li, true);
    });

    var row = document.querySelector('#sidebarNav .toc-row[data-id="' + CSS.escape(entry.highlightId) + '"]');
    if (row) {
      row.classList.add("is-active");
      // Bring it into view within the sidebar without yanking the page.
      row.scrollIntoView({ block: "nearest" });
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
  // 1024px the scrim/hamburger CSS stops applying but body.nav-open and #main's
  // `inert` would persist, leaving the content dead with no toggle to clear it.
  // Keep in sync with the @media (max-width: 1024px) block in aws-docs.css.
  window.matchMedia("(max-width: 1024px)").addEventListener("change", function (ev) {
    if (!ev.matches) closeDrawer();
  });

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
     reference locally in a command-palette modal (opened by typing in
     the topbar box, or ⌘K / Ctrl-K / "/"); every other scope escalates
     to the external AWS search. Two result sources, ranked on one scale
     by the shared scorer in search-core.js: types + namespaces matched
     synchronously from tocData, members matched in search-worker.js off
     the main thread (merging in a beat later).
     =================================================================== */



  var SEARCH_LIMIT = 50;         // total rows shown; overflow noted, never silent
  var SEARCH_DEBOUNCE_MS = 90;   // coalesce keystrokes before scanning toc data
  var score = (self.AwsDocsSearch && self.AwsDocsSearch.score) || null;

  // Result kinds. Numeric codes mirror TOCWriter's Kind* constants (change
  // both together); "ns"/"type" are local pseudo-kinds for the tocData matches.
  // Icons reuse the member-table icon classes so both share one icon system.
  var GROUPS = [
    { code: "ns",   label: "Namespaces" },
    { code: "type", label: "Types" },
    { code: 1,      label: "Methods" },
    { code: 2,      label: "Properties" },
    { code: 3,      label: "Fields" },
    { code: 4,      label: "Events" },
    { code: 5,      label: "Enum values" }
  ];
  var KIND_ICONS = { ns: "ico-namespace", type: "class", 1: "publicMethod", 2: "publicProperty", 3: "field", 4: "event", 5: "enum" };

  function kindIconClass(kind) {
    return KIND_ICONS[kind] || "class";
  }

  // Tie-break rank: namespaces, then types, then members.
  function kindRank(kind) {
    return kind === "ns" ? 0 : kind === "type" ? 1 : 2;
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
      // No worker support — or file://, where Worker throws and the index
      // fetch is blocked anyway. Permanent for this environment (never reset,
      // unlike the retriable workerFailed latch). Fail visibly.
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
          if (lastQuery) postWorkerQuery(lastQuery);
        } else if (msg.type === "progress") {
          // Another index chunk landed: re-run the live query so results
          // reflect the grown corpus. searchSeq is untouched — the re-query
          // answers the SAME query, so its reply must not read as stale.
          if (lastQuery && workerReady) {
            memberPending = true;
            postWorkerQuery(lastQuery);
          }
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
      // DATA_VQ cache-busts the index; the worker propagates the same ?v= to
      // the sibling chunk files so the whole set is version-coherent.
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
  // Hot path (per debounced keystroke, main thread): names/lowercase/acronyms
  // are precomputed by loadSidebar, the shared top-N collector avoids sorting
  // every hit, and the last query's result is cached because every render
  // calls this twice (keystroke render + worker-merge render). Returns a copy
  // — mergedMatches pushes member rows into it.
  function matchTocData(q) {
    if (q === _tocMatchCacheQ && _tocMatchCacheRows) {
      _tocMatchTotal = _tocMatchCacheTotal;
      return _tocMatchCacheRows.slice();
    }
    _tocMatchTotal = 0;
    if (!tocData || !tocData.namespaces || !score || !self.AwsDocsSearch.makeTopN) return [];
    var top = self.AwsDocsSearch.makeTopN(SEARCH_LIMIT);
    // Dotted queries ("S3.PutObjectRequest") miss the plain scorer for TYPE
    // nodes (unqualified names); retry those namespace-qualified.
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
        // Type context and overload sig arrive HTML-encoded from the index;
        // decode so they render like the toc-sourced results.
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

  // Cap at SEARCH_LIMIT, but reserve some slots for type/namespace rows:
  // member names repeat SDK-wide ("BucketName", "Validate"), so exact-tier
  // members would otherwise evict the TYPE the user was likely reaching for.
  // Only changes which rows are DROPPED — order and overflow count are
  // unaffected, and it never yields fewer rows than a plain cap.
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

  /* ------------------- External docs-search URL ----------------------
     Shared by the modal's full-text escape hatch and searchFormSubmit's
     documentation scopes. Built and opened by us, not native form-GET:
     native submission with a #fragment in the action is fragile. */

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
      // reply — a member-only query has zero toc rows until it answers.
      setStatus(workerFailed
        ? "No type or namespace matches. Member search is unavailable — the search index could not be loaded."
        : memberPending
          ? "Searching members…"
          : "No matches for “" + lastQuery + "”");
      return;
    }

    var frag = document.createDocumentFragment();
    // Partition rows into the fixed-order kind groups; each group keeps its
    // rows in score order, and currentRows mirrors the visual order so
    // ↑/↓/Enter traverse across groups.
    var sections = GROUPS.map(function (g) {
      return { label: g.label, rows: all.filter(function (r) { return r.kind === g.code; }) };
    });
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
        // Options must NOT be tab stops: this is an aria-activedescendant
        // listbox — DOM focus stays on the combobox input, which owns the
        // arrow/Enter keys. A focusable <a href> option would strand them.
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
      statusText += (statusText ? " · " : "") + "Searching members…";
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
    if (scroll !== false) rows[idx].scrollIntoView({ block: "nearest" });
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
    // A transient index-load failure must not disable member search for the
    // rest of the (hx-boost-long) session. Clear the latch so ensureWorker
    // retries — at most once per modal open (the !modalOpen() gate; the modal
    // re-fires openSearchModal per gesture while open), and never when the
    // cause is permanent (workerUnsupported).
    if (!modalOpen() && workerFailed && !workerUnsupported) workerFailed = false;
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
  // Keystroke handoff: typing in the default-scope topbar box opens the modal
  // seeded with the typed text and clears the box. IME composition is the
  // exception — clearing/refocusing mid-composition aborts it and commits raw
  // ASCII — so while composing, only surface live results (box keeps text and
  // focus); the real handoff completes on compositionend (wireSearch).
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
        // Flush a pending debounce so Enter acts on what the input shows, not
        // the previous render — never navigate anywhere the user hasn't seen.
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
        // Full IME handoff once the composition commits. Deferred a tick: a
        // composition also commits when the user LEAVES the field, and
        // stealing focus back then would yank them into the modal they were
        // abandoning — after the timeout the focus check skips that case.
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
     dropdown, scroll handling, and the back-compat globals generated
     pages still call inline.
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

  // WCAG 2.1.1: a scroll container that can't take focus can only be panned
  // with a pointer, so overflowing code blocks get tabindex="0" (arrow keys
  // then scroll natively). role="group" + name, not role="region" — region is
  // a landmark and a dozen samples would flood the landmark list, but some
  // role is required for aria-label to be reliably exposed. Only blocks that
  // actually overflow are marked (layout-dependent, hence the resize re-run —
  // text zoom is exactly when a sample that used to fit starts to scroll).
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

  // After an in-place swap, focus falls back to <body> and screen readers get
  // no signal the page changed: move focus to the new #main (tabindex="-1")
  // and announce the new title in the persistent live region.
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
  // hx-select="#main" discards a boosted response's <head>, so the tags that
  // identify the page — canonical, description, aws-tocid — would keep
  // advertising the first page of the session. That skews analytics:
  // trackVirtualPageView lets the platform build its beacon from the live
  // document, so an un-synced head attributes every later page view to the
  // first page. The values exist only in the response body, which only
  // htmx:beforeSwap sees — capture there, apply on the matching afterSwap.
  // (Back/Forward history restores are left alone: a stale canonical during a
  // restore is harmless, and tracking fires only on real navigations.)
  var HEAD_META_TAGS = [
    ["link", "rel", "canonical", "href"],
    ["meta", "name", "description", "content"],
    ["meta", "name", "aws-tocid", "content"]
  ];
  var _pendingHeadMeta = null;

  // Parses only the head of a full response (a couple of KB; htmx already
  // parsed the body for hx-select). No "</head>" means this is not a whole
  // page — take nothing rather than guess from a prefix cut mid-tag.
  function readHeadMeta(responseText) {
    if (typeof responseText !== "string") return null;
    var end = responseText.indexOf("</head>");
    if (end === -1) return null;
    var scope = sharedParser().parseFromString(responseText.slice(0, end), "text/html");
    var out = [];
    for (var i = 0; i < HEAD_META_TAGS.length; i++) {
      var t = HEAD_META_TAGS[i];
      var n = scope.querySelector(t[0] + "[" + t[1] + "=\"" + t[2] + "\"]");
      out.push(n ? n.getAttribute(t[3]) : null);
    }
    return out;
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

  /* ---------------------- Analytics page views ---------------------- */
  // In-place navigation has to report its own page views: the docs platform
  // bootstrap (awsdocs-boot.js) sends its Adobe beacon on document load only,
  // so left alone every page after the first in a session goes unreported.
  // Dispatched through AWSMA's own trigger event so the platform builds the
  // beacon from the live document (report suite, consent state, page-name
  // scheme all stay its business). No AWSMA (local preview) = silent no-op.
  var _lastTrackedHref = null;
  function trackVirtualPageView() {
    var ma = window.AWSMA;
    if (!ma) return;
    // A swap that leaves the URL alone is not a new page view, and afterSwap
    // can fire more than once for one navigation.
    if (window.location.href === _lastTrackedHref) return;
    _lastTrackedHref = window.location.href;
    var trigger = typeof ma.TRIGGER_EVENT === "string" ? ma.TRIGGER_EVENT : "custom_awsma_trigger";
    document.dispatchEvent(new CustomEvent(trigger));
  }

  // htmx:load fires only for htmx-swapped content, NOT the initial document, so
  // drive init from both; onPageLoad is idempotent (once() guards).
  document.addEventListener("DOMContentLoaded", function () {
    onPageLoad(false);
    // The platform counted this load itself; record it so the first swap is
    // measured against it.
    _lastTrackedHref = window.location.href;
  });
  document.addEventListener("htmx:afterSwap", function () {
    // Make the head describe the page that just landed (captured in beforeSwap;
    // the swap itself never carries it).
    applyHeadMeta(_pendingHeadMeta);
    _pendingHeadMeta = null;
    onPageLoad(true);
    // Deferred a task: htmx sets the URL and <title> as part of handling the
    // navigation, and the platform reads both off the live document.
    setTimeout(trackVirtualPageView, 0);
  });

  // Scroll handling for in-place navigation. htmx's own scroll handling targets
  // the swapped element (#main), which is not a scroll container in this layout
  // (the window scrolls), so it is a no-op — a boosted/search/sidebar nav would
  // otherwise keep the previous scroll offset. Scroll the window to top on each
  // forward navigation, UNLESS the URL carries a #fragment, so member
  // deep-links (e.g. #prop_Foo) land on their row. Runs after settle so the
  // swapped-in anchor target exists. History traversals are left to the
  // browser's native scroll restoration.
  function scrollToHashTargetOrTop() {
    var hash = window.location.hash;
    if (hash && hash.length > 1) {
      // decodeURIComponent throws on malformed escapes ("#%"); degrade to the
      // raw fragment rather than an uncaught error in the settle handler.
      var id = hash.slice(1);
      try { id = decodeURIComponent(id); } catch (e) { /* keep raw */ }
      var target = document.getElementById(id);
      if (target) {
        markTargetRow(target);
        target.scrollIntoView({ block: "start" });
        return;
      }
    }
    window.scrollTo(0, 0);
  }

  document.addEventListener("htmx:afterSettle", function (ev) {
    // History restores (Back/Forward) also settle, but with no requestConfig in
    // the event detail — their scroll is restoration's business, not ours.
    if (!ev.detail || !ev.detail.requestConfig) return;
    scrollToHashTargetOrTop();
  });

  /* ------------------- Failed history restores ---------------------- */
  // A history-cache miss makes htmx re-fetch the page, and that fetch can fail
  // with popstate having ALREADY moved the address bar — silence would leave
  // the previous page's content under the restored page's URL. reload(), not
  // assign(): the address bar already holds the target URL, so a reload
  // re-fetches exactly what the URL claims. A browser error page is a truthful
  // outcome here; stale content is not.
  function reloadAfterFailedRestore() { window.location.reload(); }
  document.addEventListener("htmx:historyCacheMissLoadError", reloadAfterFailedRestore);
  // Transport failures (offline, DNS) never reach that event — htmx's restore
  // xhr has no onerror — so watch the xhr handed over with the miss event.
  // Only "error": "abort" is the browser tearing down requests as the document
  // unloads, and reloading then would fight the navigation the reader asked for.
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
