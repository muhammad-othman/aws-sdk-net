/* =====================================================================
   AWS SDK for .NET — API Reference local search: member worker
   ---------------------------------------------------------------------
   Owns the member half of local search off the main thread so typing
   never janks. app.js handles types/namespaces from the already-loaded
   toc.json (instant); this worker loads the larger member index and
   matches against it. The index is chunked: the worker answers queries
   as soon as the FIRST chunk is in and keeps appending the rest in the
   background. A failed chunk is skipped — rows only reference the
   manifest's type table, never each other.

   Protocol (postMessage):
     <- { type:"init", indexUrl }       load + decode the index manifest
     <- { type:"query", q, seq, limit } rank members for q
     -> { type:"ready" }                first chunk loaded; queries answerable
     -> { type:"progress" }             another chunk landed (client re-queries)
     -> { type:"error", message }       manifest failed or NO chunk loaded
     -> { type:"results", seq, items:[{name,kind,href,type,sig,score}], total }
        ("total" counts every match, including beyond the limit, so the
        client's overflow note can report what was cut)

   The index is the chunked shape emitted by TOCWriter.WriteSearchIndexJson:
     manifest search-index.json:
       { v:2, base:"items", f:[folder…],
         t:[[folderIdx, typeName, typeFile]…],
         chunks:["search-index-0.json", …] }
     chunk search-index-<n>.json:
       { v:2, m:[ [typeIdx, kind, name],             ← anchor kinds (derived file)
                  [typeIdx, kind, name, file],       ← own-page kinds
                  [typeIdx, kind, name, file, sig] ] } ← overloaded methods
   "v" is the index format version: anything else is rejected (-> "error") so
   a mismatched copy degrades to the modal's visible "member search
   unavailable" note. Bump it in both places when the shape changes.
   Rows with no file are anchor kinds — properties (2), fields (3) and enum
   members (5) — whose href is derived here as typeFile + "#" + prefix + name.
   CONTRACT: the prefixes mirror FilenameGenerator.PropertyAnchor/FieldAnchor/
   EnumMemberAnchor and the kind codes mirror TOCWriter's Kind* constants;
   change them in all places together.
   Chunk files are fetched as siblings of the manifest and inherit its ?v=
   cache-bust, so manifest and chunks stay version-coherent.
   ===================================================================== */
"use strict";

// Propagate the cache-busting ?v= this worker itself was loaded with (see
// ensureWorker in app.js) so the shared scorer can't be a stale CDN copy.
importScripts("search-core.js" + (self.location.search || ""));
var score = self.AwsDocsSearch.score;
var scoreQualified = self.AwsDocsSearch.scoreQualified;
var makeTopN = self.AwsDocsSearch.makeTopN;
var acronymOf = self.AwsDocsSearch.acronymOf;

// Anchor prefixes by kind code — mirror of FilenameGenerator.*Anchor and
// TOCWriter's Kind* constants (see the CONTRACT note in the header).
var ANCHOR_PREFIX = { 2: "prop_", 3: "field_", 5: "member_" };

// Per-fetch bound so a stalled connection can't leave the client saying
// "searching members…" forever: a stall costs one chunk, not the session.
var FETCH_TIMEOUT_MS = 45000;

// Declaring-type table from the manifest (parallel arrays, one slot per type).
var TYPE_FOLDERS = null; // resolved folder string
var TYPE_NAMES = null;   // display name (HTML-encoded, e.g. "Foo&lt;T&gt;")
var TYPE_LNAMES = null;  // DECODED + lower-cased display name (qualified-query gate)
var TYPE_FILES = null;   // type page file (anchor derivation base)

// The index stores display names HTML-encoded (generics as "Foo&lt;T&gt;"), but
// queries are raw text and app.js scores types against DECODED names — the gate
// must match on the same form or "Foo<T>.Bar" finds the type and zero members.
// No DOMParser in a worker, so decode by string: only the entities the
// generator's GetDisplayName actually emits (&lt;/&gt;), plus &amp; LAST so a
// literal "&amp;lt;" can't double-decode.
function decodeEntities(s) {
  if (s.indexOf("&") === -1) return s;
  return s.replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&amp;/g, "&");
}

// Member rows, appended chunk by chunk (parallel arrays for tight memory /
// fast scan). Lowercase names and acronyms are precomputed once at append
// time so the per-keystroke scan never re-derives them per row.
var NAMES = [];
var LNAMES = [];
var ACRS = [];
var KINDS = [];
var FILES = [];   // own page file, or undefined for anchor kinds (derived)
var TYPEIDX = [];
var SIGS = [];    // "(paramTypes)" for overloaded methods, else undefined

var BASE = "items";  // content sub-folder from the manifest; fallback if absent
var ready = false;   // first chunk in — queries are answerable
var chunkDir = "";   // manifest's directory — chunks are its siblings
var chunkVq = "";    // manifest's ?v= cache-bust, inherited by chunk fetches

function fetchJson(url) {
  return new Promise(function (resolve, reject) {
    var ctrl = typeof AbortController !== "undefined" ? new AbortController() : null;
    var timer = setTimeout(function () {
      if (ctrl) ctrl.abort();
      reject(new Error("index fetch timed out"));
    }, FETCH_TIMEOUT_MS);
    fetch(url, ctrl ? { signal: ctrl.signal } : undefined)
      .then(function (r) {
        if (!r.ok) throw new Error("HTTP " + r.status);
        return r.json();
      })
      .then(
        function (data) { clearTimeout(timer); resolve(data); },
        function (e) { clearTimeout(timer); reject(e); }
      );
  });
}

function appendChunk(chunk) {
  if (!chunk || chunk.v !== 2 || !Array.isArray(chunk.m)) throw new Error("bad index chunk");
  var rows = chunk.m;
  for (var k = 0; k < rows.length; k++) {
    var row = rows[k];          // [typeIdx, kind, name, file?, sig?]
    var ti = row[0];
    // Bounds-guard the type-table reference BEFORE pushing anything: a row
    // from a corrupt/mixed set must not skew the parallel arrays.
    if (!(typeof ti === "number" && ti >= 0 && ti < TYPE_FILES.length)) {
      throw new Error("bad index chunk");
    }
    var name = row[2];
    // Derive BEFORE pushing: a non-string name throws here, while the arrays
    // are still untouched — a throw between pushes would skew the parallel
    // arrays for every row appended after it.
    var lname = name.toLowerCase();
    var acr = acronymOf(name);
    TYPEIDX.push(ti);
    KINDS.push(row[1]);
    NAMES.push(name);
    LNAMES.push(lname);
    ACRS.push(acr);
    FILES.push(row[3]);         // undefined for the (common) 3-element rows
    SIGS.push(row[4]);
  }
}

function onChunkLoaded() {
  if (!ready) {
    ready = true;
    postMessage({ type: "ready" });
  } else {
    postMessage({ type: "progress" });
  }
}

// Sequential, not parallel: one in-flight request at a time keeps a cold page
// load from competing with itself on constrained connections. A failure is
// contained to its own chunk — the pass continues.
function loadSequential(names, i) {
  if (i >= names.length) {
    // The client latches this visibly and retries once per modal open.
    if (!ready) postMessage({ type: "error", message: "no index chunks could be loaded" });
    return;
  }
  fetchJson(chunkDir + names[i] + chunkVq)
    .then(appendChunk)
    .then(onChunkLoaded, function () { /* skip failed chunk */ })
    .then(function () { loadSequential(names, i + 1); });
}

function init(indexUrl) {
  // Chunks are fetched as siblings of the manifest, carrying the same ?v=
  // (DATA_VQ) cache-bust.
  var qi = indexUrl.indexOf("?");
  chunkVq = qi === -1 ? "" : indexUrl.slice(qi);
  var path = qi === -1 ? indexUrl : indexUrl.slice(0, qi);
  chunkDir = path.slice(0, path.lastIndexOf("/") + 1);

  fetchJson(indexUrl)
    .then(function (data) {
      if (!data || data.v !== 2 || !data.f || !data.t || !data.chunks) {
        throw new Error("bad index");
      }
      if (data.base) BASE = data.base;
      var folderTable = data.f;
      var typeRows = data.t;
      var n = typeRows.length;
      TYPE_FOLDERS = new Array(n);
      TYPE_NAMES = new Array(n);
      TYPE_LNAMES = new Array(n);
      TYPE_FILES = new Array(n);
      for (var k = 0; k < n; k++) {
        var row = typeRows[k];  // [folderIdx, typeName, typeFile]
        TYPE_FOLDERS[k] = folderTable[row[0]];
        TYPE_NAMES[k] = row[1];
        TYPE_LNAMES[k] = decodeEntities(row[1]).toLowerCase();
        TYPE_FILES[k] = row[2];
      }
      if (!data.chunks.length) {
        // Degenerate but legal: an empty doc set. Ready with an empty corpus.
        ready = true;
        postMessage({ type: "ready" });
        return;
      }
      loadSequential(data.chunks, 0);
    })
    .catch(function (e) {
      postMessage({ type: "error", message: String(e && e.message || e) });
    });
}

function hrefOf(idx) {
  var ti = TYPEIDX[idx];
  var file = FILES[idx];
  if (file == null) file = TYPE_FILES[ti] + "#" + ANCHOR_PREFIX[KINDS[idx]] + NAMES[idx];
  return BASE + "/" + TYPE_FOLDERS[ti] + "/" + file;
}

function query(q, seq, limit) {
  if (!ready || !q) {
    postMessage({ type: "results", seq: seq, items: [], total: 0 });
    return;
  }
  limit = limit || 20;
  var lq = q.toLowerCase();

  // Keep the best `limit` by score via the shared top-N collector (member
  // counts are large, so avoid sorting every match). total() counts every
  // match so the client's "+N more" note reflects reality.
  var top = makeTopN(limit);
  // Dotted queries ("S3Client.PutObject") miss the plain scorer (member names
  // are unqualified); retry those context-qualified against the declaring
  // type. Pre-lowered forms pass through — nothing in this loop allocates.
  var qualified = lq.indexOf(".") !== -1;
  for (var k = 0; k < NAMES.length; k++) {
    var s = score(NAMES[k], lq, LNAMES[k], ACRS[k]);
    if (s < 0 && qualified) {
      var ti = TYPEIDX[k];
      s = scoreQualified(NAMES[k], TYPE_NAMES[ti], lq, LNAMES[k], ACRS[k], TYPE_LNAMES[ti]);
    }
    if (s >= 0) top.consider({ idx: k, score: s });
  }

  var items = top.result().map(function (b) {
    return {
      name: NAMES[b.idx],
      kind: KINDS[b.idx],
      href: hrefOf(b.idx),
      type: TYPE_NAMES[TYPEIDX[b.idx]],
      sig: SIGS[b.idx],
      score: b.score
    };
  });
  postMessage({ type: "results", seq: seq, items: items, total: top.total() });
}

onmessage = function (ev) {
  var msg = ev.data || {};
  if (msg.type === "init") init(msg.indexUrl);
  else if (msg.type === "query") query(msg.q, msg.seq, msg.limit);
};
