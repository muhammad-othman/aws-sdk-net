/* =====================================================================
   AWS SDK for .NET — API Reference local search: shared scorer + top-N
   ---------------------------------------------------------------------
   Loaded BOTH by app.js (window, via <script>) and by search-worker.js
   (worker, via importScripts). `self` is the global in either context,
   so the same scoring function and top-N collector rank types/namespaces
   (matched on the main thread from toc.json) and members (matched in the
   worker) on one consistent scale — otherwise the two result groups
   would interleave incorrectly. Names-only matching; no descriptions are
   indexed.
   ===================================================================== */
(function () {
  "use strict";

  // Match tiers, highest first. The numeric gaps leave room for small
  // per-match bonuses (shorter names and earlier matches rank higher)
  // without a lower tier ever overtaking a higher one.
  var EXACT = 100000;
  var PREFIX = 80000;
  var ACRONYM = 60000;   // camelCase initials, e.g. "gob" -> GetObject
  var WORD = 40000;      // substring starting at a word boundary
  var SUBSTR = 20000;    // substring mid-word

  // camelCase / Pascal initials of a name: first char plus every
  // uppercase letter or digit, lowercased. "GetObjectV2" -> "gov2".
  function acronymOf(name) {
    var out = "";
    for (var i = 0; i < name.length; i++) {
      var c = name.charAt(i);
      if (i === 0 || (c >= "A" && c <= "Z") || (c >= "0" && c <= "9")) {
        out += c.toLowerCase();
      }
    }
    return out;
  }

  // Shorter names rank above longer ones at the same tier (capped so it
  // never crosses a tier boundary).
  function lengthBonus(name) {
    var b = 60 - name.length;
    return b < 0 ? 0 : b;
  }

  function isBoundary(name, idx) {
    if (idx === 0) return true;
    var prev = name.charAt(idx - 1);
    var here = name.charAt(idx);
    if (prev < "A" || (prev > "Z" && prev < "a") || prev > "z") {
      if (prev < "0" || prev > "9") return true; // non-alphanumeric before
    }
    return here >= "A" && here <= "Z"; // uppercase start = camelCase boundary
  }

  // Returns a score (higher = better) or -1 for no match.
  // `q` MUST already be lower-cased and non-empty.
  // `lname`/`acr` are optional precomputed name.toLowerCase()/acronymOf(name):
  // hot callers scanning a large corpus per keystroke (the worker) pass them
  // so the scan allocates nothing per row; casual callers omit them.
  function score(name, q, lname, acr) {
    if (!name) return -1;
    if (lname === undefined) lname = name.toLowerCase();

    if (lname === q) return EXACT + lengthBonus(name);
    if (lname.lastIndexOf(q, 0) === 0) return PREFIX + lengthBonus(name);

    if (acr === undefined) acr = acronymOf(name);
    if (acr.lastIndexOf(q, 0) === 0) return ACRONYM + lengthBonus(name);

    var idx = lname.indexOf(q);
    if (idx !== -1) {
      var tier = isBoundary(name, idx) ? WORD : SUBSTR;
      return tier + lengthBonus(name) - idx;
    }
    return -1;
  }

  // Qualified "Context.Name" queries ("S3Client.PutObject",
  // "Amazon.S3.Model.PutObjectRequest"): the plain scorer sees one short name
  // at a time, so any dotted query would miss every tier. Split on the LAST
  // dot — the part after it scores against the name as usual; the part before
  // it is a gate that must appear (case-insensitive substring) in the entry's
  // context (declaring type or namespace). Returns the name's score, so a
  // qualified hit ranks exactly like its unqualified equivalent.
  // `q` MUST already be lower-cased; callers check for a dot before looping.
  // `lname`/`acr`/`lcontext` are optional precomputed lowercase/acronym forms
  // (see score) so hot callers avoid per-row allocation.
  function scoreQualified(name, context, q, lname, acr, lcontext) {
    var di = q.lastIndexOf(".");
    if (di < 1 || di === q.length - 1) return -1; // need text on both sides
    if (!context) return -1;
    if (lcontext === undefined) lcontext = context.toLowerCase();
    if (lcontext.indexOf(q.slice(0, di)) === -1) return -1;
    return score(name, q.slice(di + 1), lname, acr);
  }

  // Top-N collector shared by the main-thread toc scan (app.js matchTocData)
  // and the worker's member scan: scan once, keep the best `limit` rows via
  // insertion into a short sorted buffer instead of collecting-then-sorting
  // every hit — matches number in the tens of thousands at full-SDK scale.
  // Rows must carry a numeric `score`. total() counts every considered row
  // (kept or not) so "+N more" overflow notes reflect reality.
  function makeTopN(limit) {
    var best = [];
    var total = 0;
    function byScoreDesc(a, b) { return b.score - a.score; }
    return {
      consider: function (row) {
        total++;
        if (best.length < limit) {
          best.push(row);
          if (best.length === limit) best.sort(byScoreDesc);
        } else if (row.score > best[best.length - 1].score) {
          best[best.length - 1] = row;
          // bubble the new entry up to keep `best` sorted descending
          for (var j = best.length - 1; j > 0 && best[j].score > best[j - 1].score; j--) {
            var tmp = best[j]; best[j] = best[j - 1]; best[j - 1] = tmp;
          }
        }
      },
      result: function () {
        if (best.length < limit) best.sort(byScoreDesc);
        return best;
      },
      total: function () { return total; }
    };
  }

  self.AwsDocsSearch = self.AwsDocsSearch || {};
  self.AwsDocsSearch.score = score;
  self.AwsDocsSearch.scoreQualified = scoreQualified;
  self.AwsDocsSearch.makeTopN = makeTopN;
  // Exported so the worker can precompute acronyms once at index-append time
  // with the SAME derivation the scorer falls back to.
  self.AwsDocsSearch.acronymOf = acronymOf;
})();
