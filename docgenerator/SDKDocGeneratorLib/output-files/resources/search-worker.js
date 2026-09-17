/* =====================================================================
   AWS SDK for .NET — API Reference local search: member worker
   ---------------------------------------------------------------------
   Owns the member half of local search off the main thread so typing
   never janks. app.js handles types/namespaces from the already-loaded
   toc.json (instant); this worker loads the larger member index and
   matches against it. The index is chunked: the worker answers queries
   as soon as the FIRST chunk is in and keeps appending the rest in the
   background, reporting coverage so the client can qualify results. A
   failed chunk is skipped (its siblings still load — rows only reference
   the manifest's type table, never each other) and can be retried later.

   Protocol (postMessage):
     <- { type:"init", indexUrl }       load + decode the index manifest
     <- { type:"query", q, seq, limit } rank members for q
     <- { type:"retry" }                re-fetch chunks that failed earlier
     -> { type:"ready", pct }           first chunk loaded; queries answerable
     -> { type:"progress", pct, incomplete } more chunks landed (pct 0-100);
        incomplete:true = at least one chunk is still missing after this pass
     -> { type:"error", message }       manifest failed or NO chunk loaded;
                                        nothing usable
     -> { type:"results", seq, items:[{name,kind,href,type,sig,score}], total, pct }
        ("total" counts every match, including those beyond the limit, so the
        client's overflow note can report what was cut; "pct" is coverage at
        answer time so partial answers can be qualified)

   The index is the chunked shape emitted by TOCWriter.WriteSearchIndexJson:
     manifest search-index.json:
       { v:2, g:"<generation>", base:"items", f:[folder…],
         t:[[folderIdx, typeName, typeFile]…],
         chunks:["search-index-0.json", …] }
     chunk search-index-<n>.json:
       { v:2, g:"<generation>",
         m:[ [typeIdx, kind, name],             ← anchor kinds (derived file)
             [typeIdx, kind, name, file],       ← own-page kinds
             [typeIdx, kind, name, file, sig] ] } ← overloaded methods
   "v" is the index format version: anything else is rejected (-> "error"), so
   a stale/mismatched copy degrades to the modal's visible "member search
   unavailable" note instead of misreading the data. Bump it in both places
   when the shape changes incompatibly.
   "g" is the generation token: chunk rows reference the manifest's type table
   BY INDEX, so a chunk whose g differs from the manifest's (mixed releases on
   a CDN, a mid-publish origin) must not be resolved against the wrong table.
   Row typeIdx values are bounds-checked as a second line of defense.
   The token is a CONTENT hash of the index (TOCWriter.ComputeGenerationId), not
   a per-run nonce, and this worker leans on that twice: identical republished
   indexes agree, so an unchanged doc set is never rejected; and because the
   same index always yields the same token, it doubles as the cache buster for
   recovering from a mismatch. A mismatch is a CACHE fault, not a data fault —
   these URLs are meant to be served immutable, so left alone one stale half
   would keep failing the check for as long as the cache holds it. So the
   recovery is: re-fetch the offending chunk once at ?…&g=<manifest g> (a URL
   no cache can already hold a mismatched body for), and if that still leaves
   nothing usable, the manifest was the stale half — re-fetch it once past the
   cache too and start over. Both are one-shot, so a genuinely broken index
   costs one extra round trip, not a retry storm.
   Rows with no file are anchor kinds — properties (2), fields (3) and enum
   members (5) — whose href is derived here as typeFile + "#" + prefix + name.
   CONTRACT: the prefixes mirror FilenameGenerator.PropertyAnchor/FieldAnchor/
   EnumMemberAnchor and the kind codes mirror TOCWriter's Kind* constants;
   change them in all places together.
   "base" is the content sub-folder; result hrefs are joined here as
   <base>/<folder>/<file> (root-relative) so nothing client-side hard-codes
   "items". Chunk files are fetched as siblings of the manifest and inherit
   its ?v= cache-bust.
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
// "searching members…" forever: a stall costs one chunk (skipped, retriable),
// not the session.
var FETCH_TIMEOUT_MS = 45000;

// Declaring-type table from the manifest (parallel arrays, one slot per type).
var TYPE_FOLDERS = null; // resolved folder string
var TYPE_NAMES = null;   // display name (HTML-encoded, e.g. "Foo&lt;T&gt;")
var TYPE_LNAMES = null;  // lower-cased display name (qualified-query gate)
var TYPE_FILES = null;   // type page file (anchor derivation base)

// Member rows, appended chunk by chunk (parallel arrays for tight memory /
// fast scan). Lowercase names and acronyms are precomputed ONCE here, at
// append time, so the per-keystroke scan never re-derives them per row.
var NAMES = [];
var LNAMES = [];
var ACRS = [];
var KINDS = [];
var FILES = [];   // own page file, or undefined for anchor kinds (derived)
var TYPEIDX = [];
var SIGS = [];    // "(paramTypes)" for overloaded methods, else undefined

var BASE = "items";  // content sub-folder from the manifest; fallback if absent
var GEN = null;      // manifest generation token; chunks must match
var ready = false;   // first chunk in — queries are answerable
var loading = false; // a sequential load/retry pass is in flight
var chunkDir = "";   // manifest's directory — chunks are its siblings
var chunkVq = "";    // manifest's ?v= cache-bust, inherited by chunk fetches
var totalChunks = 0;
var loadedChunks = 0;
var failedChunkNames = []; // skipped this pass; re-fetched on {type:"retry"}
var manifestUrl = null;    // the URL init was first called with, for the one-shot refetch
var manifestRefetched = false;   // that refetch has been spent
var sawGenerationMismatch = false; // a chunk from another generation reached us

function coveragePct() {
  return totalChunks ? Math.round((loadedChunks / totalChunks) * 100) : 100;
}

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

// Throws on any shape/coherence problem BEFORE touching that row, so the
// parallel arrays always stay aligned and every appended row is valid.
function appendChunk(chunk) {
  if (!chunk || chunk.v !== 2 || !chunk.m) throw new Error("bad index chunk");
  // Kept separate from the shape check above: this chunk is well formed, it just
  // belongs to a different generation of the index, so a cache gave us one half
  // of a mixed pair. That is recoverable (see loadChunk) where malformed is not.
  // The check runs before any row is pushed, so a rejected chunk leaves the
  // parallel arrays untouched and the retry can append it cleanly.
  if (chunk.g !== GEN) {
    var mismatch = new Error("index chunk generation mismatch");
    mismatch.generationMismatch = true;
    throw mismatch;
  }
  var rows = chunk.m;
  if (!Array.isArray(rows)) throw new Error("bad index chunk");
  for (var k = 0; k < rows.length; k++) {
    var row = rows[k];          // [typeIdx, kind, name, file?, sig?]
    // Validate the WHOLE row before pushing anything. The seven arrays are
    // parallel and nothing here truncates them, so a throw part way through a
    // row would leave them permanently skewed — every later row would then
    // pair one member's name with another member's type, file and signature,
    // and a retry (which re-appends this chunk's valid prefix) would skew them
    // twice. Cheap per row: five typeof checks against an array we just parsed.
    if (!Array.isArray(row) || row.length < 3 || row.length > 5) {
      throw new Error("bad index chunk");
    }
    var ti = row[0];
    // Bounds-guard the type-table reference: an out-of-range index means a
    // mixed/corrupt set that slipped past the generation check.
    if (!(typeof ti === "number" && ti >= 0 && ti < TYPE_FILES.length)) {
      throw new Error("bad index chunk");
    }
    if (typeof row[1] !== "number") throw new Error("bad index chunk");
    var name = row[2];
    if (typeof name !== "string") throw new Error("bad index chunk");
    // Optional tail cells: absent is normal (3-element anchor-kind rows), but a
    // present-and-wrong-typed one would silently corrupt an href or a label.
    if (row.length > 3 && typeof row[3] !== "string") throw new Error("bad index chunk");
    if (row.length > 4 && typeof row[4] !== "string") throw new Error("bad index chunk");
    TYPEIDX.push(ti);
    KINDS.push(row[1]);
    NAMES.push(name);
    LNAMES.push(name.toLowerCase());
    ACRS.push(acronymOf(name));
    FILES.push(row[3]);         // undefined for the (common) 3-element rows
    SIGS.push(row[4]);
  }
}

function onChunkLoaded() {
  loadedChunks++;
  if (!ready) {
    ready = true;
    postMessage({ type: "ready", pct: coveragePct() });
  } else {
    postMessage({ type: "progress", pct: coveragePct(), incomplete: failedChunkNames.length > 0 });
  }
}

function finishPass() {
  loading = false;
  if (!ready) {
    // Nothing usable. If what we got back was a different generation than this
    // manifest — and re-fetching those chunks past the cache didn't settle it —
    // then the manifest is the stale half of the pair. Re-fetch it once at a URL
    // no cache can own and start the whole load over; the token can't tell us
    // WHICH half is stale, so this covers the other direction. One shot only.
    if (sawGenerationMismatch && !manifestRefetched && manifestUrl) {
      manifestRefetched = true;
      resetIndex();
      init(manifestUrl + (manifestUrl.indexOf("?") === -1 ? "?" : "&") + "r=" + Date.now());
      return;
    }
    // Manifest arrived but not one chunk did — nothing usable. The client
    // latches this visibly and retries once per modal open.
    postMessage({ type: "error", message: "no index chunks could be loaded" });
    return;
  }
  // Complete passes already announced pct via the last onChunkLoaded; only an
  // incomplete ending needs a terminal signal (it also tells a retry pass's
  // caller that some chunks are STILL missing).
  if (failedChunkNames.length) {
    postMessage({ type: "progress", pct: coveragePct(), incomplete: true });
  }
}

// Everything decoded so far, dropped so a re-init starts clean. A chunk can fail
// PART WAY through appending (typeIdx is bounds-checked per row), so the arrays
// can hold rows from an index we're about to abandon.
function resetIndex() {
  TYPE_FOLDERS = null;
  TYPE_NAMES = null;
  TYPE_LNAMES = null;
  TYPE_FILES = null;
  NAMES = [];
  LNAMES = [];
  ACRS = [];
  KINDS = [];
  FILES = [];
  TYPEIDX = [];
  SIGS = [];
  BASE = "items";
  GEN = null;
  totalChunks = 0;
  loadedChunks = 0;
  failedChunkNames = [];
  sawGenerationMismatch = false;
}

// One fetch attempt for a chunk. `bustGeneration` appends the manifest's own
// generation token: a URL that no cache can already hold a mismatched body for.
// Sound only because the token is a content hash — the same index always asks
// for the same URL, so the retry stays cacheable instead of degenerating into a
// guaranteed miss for every visitor.
function loadChunk(name, bustGeneration) {
  var url = chunkDir + name + chunkVq;
  if (bustGeneration) {
    url += (url.indexOf("?") === -1 ? "?" : "&") + "g=" + encodeURIComponent(GEN);
  }
  return fetchJson(url).then(appendChunk);
}

// Sequential, not parallel: one in-flight request at a time keeps a cold page
// load from competing with itself on constrained connections. A failure is
// contained to its own chunk — the pass continues, because chunks are
// independent (rows reference only the manifest's type table).
function loadSequential(names, i) {
  if (i >= names.length) { finishPass(); return; }
  var name = names[i];
  loadChunk(name, false)
    .catch(function (e) {
      // Only generation mismatches get the second attempt: those are a stale
      // cache entry, which retrying past can actually fix. A 404, a timeout or a
      // malformed body would just fail again at a different URL.
      if (!e || !e.generationMismatch || !GEN) throw e;
      sawGenerationMismatch = true;
      return loadChunk(name, true);
    })
    .then(
      function () { onChunkLoaded(); },
      function () { failedChunkNames.push(name); }
    )
    .then(function () { loadSequential(names, i + 1); });
}

function init(indexUrl) {
  // Remember the URL the client asked for, never the one a retry rewrote, so the
  // one-shot manifest refetch always busts from the original.
  if (!manifestUrl) manifestUrl = indexUrl;

  // Chunks are fetched as siblings of the manifest, carrying the same ?v=
  // (DATA_VQ) cache-bust; the generation token (g) is what actually enforces
  // that manifest and chunks came from one generator run.
  var qi = indexUrl.indexOf("?");
  chunkVq = qi === -1 ? "" : indexUrl.slice(qi);
  var path = qi === -1 ? indexUrl : indexUrl.slice(0, qi);
  chunkDir = path.slice(0, path.lastIndexOf("/") + 1);

  // The first chunk's name is deterministic, so fetch it IN PARALLEL with the
  // manifest: the manifest's type table is the largest single file at full-SDK
  // scale, and overlapping the two round trips is what bounds time-to-first-
  // results by max(manifest, chunk0) instead of their sum. The speculative
  // result is only trusted after the manifest validates it (g + chunks[0]);
  // any mismatch just falls back to the normal sequential fetch.
  var speculative0 = fetchJson(chunkDir + "search-index-0.json" + chunkVq)
    .catch(function () { return null; });

  fetchJson(indexUrl)
    .then(function (data) {
      if (!data || data.v !== 2 || typeof data.g !== "string" || !data.f || !data.t || !data.chunks) {
        throw new Error("bad index");
      }
      if (data.base) BASE = data.base;
      GEN = data.g;
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
        TYPE_LNAMES[k] = row[1].toLowerCase();
        TYPE_FILES[k] = row[2];
      }
      totalChunks = data.chunks.length;
      if (!totalChunks) {
        // Degenerate but legal: an empty doc set. Ready with a complete
        // (empty) corpus.
        ready = true;
        postMessage({ type: "ready", pct: 100 });
        return;
      }
      loading = true;
      return speculative0.then(function (c0) {
        var used = false;
        if (c0 && data.chunks[0] === "search-index-0.json") {
          try {
            appendChunk(c0);
            used = true;
            onChunkLoaded();
          } catch (e) { /* stale/mixed speculation — refetch normally */ }
        }
        loadSequential(data.chunks, used ? 1 : 0);
      });
    })
    .catch(function (e) {
      postMessage({ type: "error", message: String(e && e.message || e) });
    });
}

// Re-fetch the chunks that failed earlier (transient CDN error, timeout).
// The client sends this once per modal open while its status says the index
// is incomplete — so a network blip costs until the next search, not the
// whole hx-boost-long session.
function retryFailed() {
  if (loading || !ready || !failedChunkNames.length) return;
  var names = failedChunkNames;
  failedChunkNames = [];
  loading = true;
  loadSequential(names, 0);
}

function hrefOf(idx) {
  var ti = TYPEIDX[idx];
  var file = FILES[idx];
  if (file == null) file = TYPE_FILES[ti] + "#" + ANCHOR_PREFIX[KINDS[idx]] + NAMES[idx];
  return BASE + "/" + TYPE_FOLDERS[ti] + "/" + file;
}

function query(q, seq, limit) {
  if (!ready || !q) {
    postMessage({ type: "results", seq: seq, items: [], total: 0, pct: coveragePct() });
    return;
  }
  limit = limit || 20;
  var lq = q.toLowerCase();

  // Keep the best `limit` by score via the shared top-N collector (the index
  // is names-only and member counts are large, so we avoid sorting every
  // match). Its total() counts every match (kept or not) so the client's
  // "+N more" note reflects reality.
  var top = makeTopN(limit);
  // Dotted queries ("S3Client.PutObject") miss the plain scorer (member names
  // are unqualified); retry those context-qualified against the declaring
  // type. Type names are HTML-encoded (generics as "Foo&lt;T&gt;"), which is
  // fine for a substring gate: the type-name half of a real query matches
  // before any generic bracket. Hoisted: the dot check is per-query, not
  // per-row. Names/acronyms/type names pass through pre-lowered — nothing in
  // this loop allocates per row.
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
  postMessage({ type: "results", seq: seq, items: items, total: top.total(), pct: coveragePct() });
}

onmessage = function (ev) {
  var msg = ev.data || {};
  if (msg.type === "init") init(msg.indexUrl);
  else if (msg.type === "query") query(msg.q, msg.seq, msg.limit);
  else if (msg.type === "retry") retryFailed();
};
