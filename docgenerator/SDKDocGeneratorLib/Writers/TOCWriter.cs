using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SDKDocGenerator.Writers
{
    public class TOCWriter : BaseTemplateWriter
    {
        // internal (not private): the serialization contract these shapes feed is
        // consumed by three independent clients (app.js sidebar sync, search-worker.js,
        // index.html's legacy-bookmark resolver) and is pinned by unit tests.
        internal class TocNode
        {
            public string Name;
            public string Id;
            public string Href;
        }

        internal class NamespaceToc
        {
            public string Id;
            public string Href;
            public List<TocNode> Nodes;
        }

        private readonly Dictionary<string, NamespaceToc> _namespaceTocs = new Dictionary<string, NamespaceToc>();

        // Local-search member index. Types and namespaces are already searchable from
        // toc.json (the sidebar loads it), so this index covers only members. The
        // client (search-worker.js) fetches its manifest + chunks progressively in
        // the background, answering queries from the first chunk onward.
        //   kind: 1=method 2=property 3=field 4=event 5=enum-member (constructors excluded).
        // Built from the same per-namespace pass that builds toc.json (generation is
        // sequential, so a plain accumulator is safe). Only members DECLARED on a type
        // are indexed — inherited members appear on many pages and would bloat the
        // index with duplicates; users still find them on their declaring type.
        // Note: members are enumerated on the primary platform (same source as toc.json),
        // so platform-exclusive method pages may not be represented in search.
        // CONTRACT: these codes are mirrored by KIND_TABLE in resources/app.js
        // (which maps each to its display group, icon, and sort rank) AND by
        // ANCHOR_PREFIX in resources/search-worker.js (which derives the #anchor
        // for the file-less property/field/enum-member rows from the kind code).
        // Add or renumber a kind in all places together — a drift here makes the
        // client derive wrong anchors while every build stays green, which is why
        // the values are pinned by KindCodes_MatchClientAnchorPrefixes.
        // internal (not private) so that test can reference the real constants.
        internal const int KindMethod = 1;
        internal const int KindProperty = 2;
        internal const int KindField = 3;
        internal const int KindEvent = 4;
        internal const int KindEnumMember = 5;

        internal struct MemberEntry
        {
            public string Folder;   // items/<Folder>/ — service output sub-folder
            public string Name;     // short searchable identifier (e.g. "PutObject")
            public int Kind;
            public string File;     // own page file (methods/events) — null for anchor kinds,
                                    // whose href the client derives from TypeFile + #anchor
            public string Type;     // declaring type display name, shown as dimmed context
            public string TypeFile; // declaring type's page file (anchor base + type interning key)
            public string Sig;      // "(paramTypes)" — ONLY for overloaded methods, else null
        }

        private readonly List<MemberEntry> _searchEntries = new List<MemberEntry>();

        public TOCWriter(GeneratorOptions options)
            : base(options)
        {
        }

        /// <summary>
        /// Builds the TOC entry (namespace node plus one child node per type) for a
        /// namespace during generation of docs for types in that namespace. These are
        /// added to the _namespaceTocs collection to be collated into one single master
        /// toc at the end of processing of all namespaces.
        /// </summary>
        public void BuildNamespaceToc(string nameSpace, AssemblyWrapper sdkAssemblyWrapper)
        {
            var nsToc = BuildNamespaceModel(nameSpace, sdkAssemblyWrapper);
            if (_namespaceTocs.ContainsKey(nameSpace))
            {
                // Namespace already processed: replace its TOC (last write wins) and do NOT
                // re-collect search entries. _searchEntries is a plain append-only list, so
                // re-collecting would duplicate every member of this namespace in the index
                // (whereas the TOC is keyed and de-duplicates). Guard both together.
                _namespaceTocs[nameSpace] = nsToc;
            }
            else
            {
                _namespaceTocs.Add(nameSpace, nsToc);
                CollectSearchEntries(nameSpace, sdkAssemblyWrapper);
            }
        }

        /// <summary>
        /// Collects the members of every type in the namespace into the local-search
        /// index. Files are built with the same OutputSubFolderFromNamespace +
        /// FilenameGenerator calls ClassWriter uses, so search links match the pages
        /// that are actually emitted: methods/events carry their own page file;
        /// properties, fields and enum members are rows on the type page, whose
        /// "#anchor" link the client derives from TypeFile + the member name.
        /// </summary>
        void CollectSearchEntries(string nameSpace, AssemblyWrapper sdkAssemblyWrapper)
        {
            foreach (var type in sdkAssemblyWrapper.GetTypesForNamespace(nameSpace))
            {
                var folder = GenerationManifest.OutputSubFolderFromNamespace(type.Namespace);
                var typeName = type.GetDisplayName(false);
                var typePageFile = FilenameGenerator.GenerateFilename(type);

                // Anchor-kind entries (properties, fields, enum members) carry NO file:
                // the client derives "<TypeFile>#<prefix><Name>" itself (the anchor
                // suffix is exactly the member name — see FilenameGenerator.*Anchor),
                // so ~90% of index rows don't repeat the type page filename.
                if (type.IsEnum)
                {
                    foreach (var enumName in type.GetEnumNames())
                    {
                        AddSearchEntry(folder, enumName, KindEnumMember, typeName, typePageFile);
                    }
                    continue;
                }

                // Methods and events have their own pages (M_*.html / E_*.html).
                // Overloads all share a name — without their parameter lists the search
                // rows would be identical — so those (and only those) carry a signature.
                var declaredMethods = type.GetMethodsToDocument()
                    .Where(m => IsDeclaredOn(m.DeclaringType, type))
                    .ToList();
                var overloadedNames = new HashSet<string>(declaredMethods
                    .GroupBy(m => m.Name)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key));
                foreach (var info in declaredMethods)
                {
                    var sig = overloadedNames.Contains(info.Name) ? FormatSignature(info) : null;
                    AddSearchEntry(folder, info.Name, KindMethod, typeName, typePageFile,
                        FilenameGenerator.GenerateFilename(info), sig);
                }
                foreach (var info in type.GetEvents())
                {
                    if (!IsDeclaredOn(info.DeclaringType, type))
                        continue;
                    AddSearchEntry(folder, info.Name, KindEvent, typeName, typePageFile,
                        FilenameGenerator.GenerateFilename(info));
                }

                // Properties and fields are rows on the type page, reached via #anchor —
                // derived client-side, so no File here.
                foreach (var info in type.GetProperties())
                {
                    if (!IsDeclaredOn(info.DeclaringType, type))
                        continue;
                    AddSearchEntry(folder, info.Name, KindProperty, typeName, typePageFile);
                }
                foreach (var info in type.GetFields())
                {
                    if (!IsDeclaredOn(info.DeclaringType, type))
                        continue;
                    AddSearchEntry(folder, info.Name, KindField, typeName, typePageFile);
                }
            }
        }

        // A member is "declared" on the page's type when its declaring type matches;
        // inherited members render on the page but are indexed under their own type.
        static bool IsDeclaredOn(TypeWrapper declaringType, TypeWrapper pageType)
        {
            return declaringType != null && string.Equals(declaringType.FullName, pageType.FullName);
        }

        void AddSearchEntry(string folder, string name, int kind, string type, string typeFile,
            string ownFile = null, string sig = null)
        {
            _searchEntries.Add(new MemberEntry
            {
                Folder = folder,
                Name = name,
                Kind = kind,
                File = ownFile,
                Type = type,
                TypeFile = typeFile,
                Sig = sig
            });
        }

        // Compact "(TypeA, TypeB)" parameter list used to tell overloads apart in
        // search results. Display names come HTML-encoded (like everything else in
        // the index); the client decodes before rendering.
        static string FormatSignature(MethodInfoWrapper info)
        {
            var parameters = info.GetParameters();
            if (parameters == null || parameters.Count == 0)
                return "()";
            return "(" + string.Join(", ",
                parameters.Select(p => p.ParameterType != null ? p.ParameterType.GetDisplayName(false) : "?")) + ")";
        }

        protected override string GetTemplateName()
        {
            return "TOC.html";
        }

        /// <summary>
        /// Writes the legacy TOC.html (kept as a no-script / SEO fallback) and, in
        /// addition, emits toc.json at the doc-set root. app.js fetches toc.json once
        /// and hydrates the persistent sidebar from it, so the full navigation tree no
        /// longer has to be inlined into every generated page.
        /// </summary>
        public override void Write()
        {
            base.Write();
            WriteTocJson();
            WriteSearchIndexJson();
        }

        /// <summary>
        /// Serializes the collected namespace TOCs into the shape app.js expects:
        /// { "namespaces": [ { name, id, href, nodes: [ { name, id, href } ] } ] }.
        /// Hrefs are made root-relative (leading "./" stripped) so the client can
        /// resolve them against the page's data-root at any folder depth.
        /// </summary>
        void WriteTocJson()
        {
            var json = SerializeTocJson(_namespaceTocs);

            var outputPath = Path.Combine(Options.OutputFolder, "toc.json");
            File.WriteAllText(outputPath, json);
        }

        // Pure serialization seam, split from the file write so unit tests can pin
        // the toc.json contract without running a full generation.
        internal static string SerializeTocJson(IDictionary<string, NamespaceToc> namespaceTocs)
        {
            var namespaces = new List<object>();

            foreach (var ns in namespaceTocs.Keys.OrderBy(x => x))
            {
                var nsToc = namespaceTocs[ns];

                namespaces.Add(new
                {
                    name = ns,
                    // Service-folder grouping key (e.g. "S3", "Runtime", "Util"),
                    // so app.js can group related namespaces under one service.
                    service = GenerationManifest.OutputSubFolderFromNamespace(ns),
                    id = nsToc.Id,
                    href = NormalizeHref(nsToc.Href),
                    nodes = nsToc.Nodes.Select(n => new
                    {
                        name = n.Name,
                        id = n.Id,
                        href = NormalizeHref(n.Href)
                    }).ToList()
                });
            }

            var payload = new { namespaces };
            return System.Text.Json.JsonSerializer.Serialize(payload);
        }

        internal static string NormalizeHref(string href)
        {
            if (string.IsNullOrEmpty(href))
                return href;
            if (href.StartsWith("./"))
                return href.Substring(2);
            return href;
        }

        // Rows per chunk file. ~50k rows keeps each chunk around 1.5-2 MB raw
        // (~400 KB gzipped). The worker answers queries once the manifest AND the
        // first chunk are in, and it fetches those two in parallel — so first-answer
        // latency is bounded by the LARGER of the two, which at full-SDK scale is
        // the manifest's type table (a few MB), not the chunk. Shrinking this knob
        // therefore trims total transfer granularity but cannot cut first-answer
        // latency below the manifest download; it mainly bounds how much a single
        // failed/stalled chunk costs.
        internal const int SearchIndexChunkRowCount = 50000;

        // Index format version, emitted as "v" in the manifest and in every chunk.
        // search-worker.js rejects anything else (CONTRACT: bump it there too).
        // SdkDocGenerator folds this into DataVersion so a shape change always
        // moves the data URLs off their previously cached ones.
        internal const int SearchIndexFormatVersion = 2;

        internal sealed class SearchIndexArtifact
        {
            public string FileName;
            public string Json;
        }

        /// <summary>
        /// Serializes the collected member entries into the doc-set root as a small
        /// manifest (search-index.json) plus one or more row chunks
        /// (search-index-&lt;n&gt;.json) that the client loads progressively:
        ///   manifest: { "v":2, "g":"&lt;generation&gt;", "base":"items", "f":[ "S3", … ],
        ///               "t":[ [folderIdx, typeName, typeFile], … ],
        ///               "chunks":[ "search-index-0.json", … ] }
        ///   chunk:    { "v":2, "g":"&lt;generation&gt;",
        ///               "m":[ [typeIdx, kind, name],                           ← anchor kinds
        ///                     [typeIdx, kind, name, file],                     ← own-page kinds
        ///                     [typeIdx, kind, name, file, sig], … ] }          ← overloaded methods
        /// "g" is the generation token: chunk rows reference the manifest's type
        /// table BY INDEX, so the worker refuses any chunk whose g differs from its
        /// manifest's — otherwise a mixed set (mid-publish origin, or a CDN holding
        /// files from two releases) would resolve indexes against the wrong table
        /// and emit wrong or undefined hrefs.
        /// It is a CONTENT hash of everything an index resolves against (the folder
        /// and type tables plus every member row), not a per-run nonce. That
        /// distinction is what keeps the check from being a footgun: two runs that
        /// produce byte-identical indexes agree on g, so a cache serving one
        /// release's manifest beside another's chunks is only rejected when the
        /// halves genuinely disagree. With a random token, ANY cache mixing two
        /// runs — including republishing an unchanged doc set — would fail the
        /// check, and because these URLs are meant to be served immutable, that
        /// failure would persist until the data version moved. search-worker.js
        /// also uses g as a last-resort cache buster on mismatch, which only works
        /// because it is stable for a given index (see its retry note).
        /// Declaring types are interned into the manifest's "t" table so member rows
        /// carry an index, not a repeated type-name/file pair. Rows for anchor kinds
        /// (properties, fields, enum members — the overwhelming majority) omit the
        /// file entirely: search-worker.js derives "&lt;typeFile&gt;#&lt;prefix&gt;&lt;name&gt;" with
        /// the same prop_/field_/member_ prefixes FilenameGenerator.*Anchor emits
        /// (CONTRACT: change them in both places together). Full hrefs resolve as
        /// &lt;base&gt;/&lt;f[t[typeIdx][0]]&gt;/&lt;file&gt;. "base" is the configurable content
        /// sub-folder (<see cref="GeneratorOptions.ContentSubFolderName"/>, usually
        /// "items"), emitted here so the client doesn't hard-code it.
        /// "v" is the index format version; search-worker.js rejects anything else, so
        /// a stale/mismatched copy degrades to the visible "member search unavailable"
        /// state instead of being misread. Bump it in both places on incompatible change.
        /// Hosting note: every fetch of these files carries the release-specific
        /// ?v= data-version query (DATA_VQ in app.js), making each URL immutable per
        /// release — serve search-index*.json with long max-age/immutable cache
        /// headers so the download cost is paid once per release, not per session.
        /// </summary>
        void WriteSearchIndexJson()
        {
            foreach (var artifact in SerializeSearchIndexFiles(Options.ContentSubFolderName, _searchEntries, SearchIndexChunkRowCount))
            {
                File.WriteAllText(Path.Combine(Options.OutputFolder, artifact.FileName), artifact.Json);
            }
        }

        // Pure serialization seam, split from the file write so unit tests can pin
        // the search-index contract without running a full generation. The manifest
        // is always first in the returned list, followed by at least one chunk (a
        // single empty one when there are no entries, so the client contract is
        // uniform).
        internal static List<SearchIndexArtifact> SerializeSearchIndexFiles(
            string contentSubFolderName, IReadOnlyList<MemberEntry> searchEntries, int chunkRowCount)
        {
            if (chunkRowCount < 1)
                throw new ArgumentOutOfRangeException(nameof(chunkRowCount));

            // Folder and type lookup tables (stable, de-duplicated, ordered by first
            // use). Types are keyed per folder + page file: distinct types never share
            // a page file within a folder, and the same-named type in another service
            // folder must stay a separate row.
            var folderIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var folders = new List<string>();
            var typeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var types = new List<object[]>();

            var rows = new List<object[]>(searchEntries.Count);
            foreach (var e in searchEntries)
            {
                int fi;
                if (!folderIndex.TryGetValue(e.Folder, out fi))
                {
                    fi = folders.Count;
                    folderIndex[e.Folder] = fi;
                    folders.Add(e.Folder);
                }

                int ti;
                var typeKey = e.Folder + "\n" + e.TypeFile;
                if (!typeIndex.TryGetValue(typeKey, out ti))
                {
                    ti = types.Count;
                    typeIndex[typeKey] = ti;
                    types.Add(new object[] { fi, e.Type, e.TypeFile });
                }

                // Variable-length rows: anchor kinds stop after the name (their file
                // is derived client-side), and the signature slot exists only when
                // set, so the common cases pay for nothing they don't use.
                rows.Add(e.File == null
                    ? new object[] { ti, e.Kind, e.Name }
                    : e.Sig == null
                        ? new object[] { ti, e.Kind, e.Name, e.File }
                        : new object[] { ti, e.Kind, e.Name, e.File, e.Sig });
            }

            var chunkNames = new List<string>();
            var chunks = new List<List<object[]>>();
            for (var start = 0; start == 0 || start < rows.Count; start += chunkRowCount)
            {
                var count = Math.Min(chunkRowCount, rows.Count - start);
                chunkNames.Add("search-index-" + chunks.Count + ".json");
                chunks.Add(rows.GetRange(start, count));
            }

            var generationId = ComputeGenerationId(contentSubFolderName, folders, types, rows, chunkRowCount);

            var artifacts = new List<SearchIndexArtifact>
            {
                new SearchIndexArtifact
                {
                    FileName = "search-index.json",
                    Json = System.Text.Json.JsonSerializer.Serialize(
                        new { v = SearchIndexFormatVersion, g = generationId, @base = contentSubFolderName, f = folders, t = types, chunks = chunkNames })
                }
            };
            for (var c = 0; c < chunks.Count; c++)
            {
                artifacts.Add(new SearchIndexArtifact
                {
                    FileName = chunkNames[c],
                    Json = System.Text.Json.JsonSerializer.Serialize(new { v = SearchIndexFormatVersion, g = generationId, m = chunks[c] })
                });
            }
            return artifacts;
        }

        /// <summary>
        /// Content fingerprint of one search index: identical inputs always produce
        /// the same token, and any change that would make a chunk incoherent with a
        /// manifest changes it. Covers the folder and type tables (what row indexes
        /// resolve against), every row, the content sub-folder that hrefs are joined
        /// with, and the chunk size (it decides which rows land in which chunk file,
        /// so two runs that differ only there must not be mixed either).
        /// Hashes a streamed text projection rather than the JSON: at full-SDK scale
        /// the serialized index runs to tens of megabytes, and the artifacts are all
        /// held in memory already — this way the fingerprint costs a fixed buffer
        /// instead of a second copy of the whole index. Fields are newline/tab
        /// delimited, which is unambiguous here because names are identifiers or
        /// HTML-encoded type names and indexes are non-negative integers; nothing in
        /// the index can contain a delimiter. Formatted invariantly so the token does
        /// not depend on the build machine's culture.
        /// </summary>
        private static string ComputeGenerationId(
            string contentSubFolderName, List<string> folders, List<object[]> types, List<object[]> rows, int chunkRowCount)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var crypto = new System.Security.Cryptography.CryptoStream(
                       Stream.Null, sha, System.Security.Cryptography.CryptoStreamMode.Write))
            using (var writer = new StreamWriter(crypto, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(SearchIndexFormatVersion.ToString(CultureInfo.InvariantCulture));
                writer.Write('\n');
                writer.Write(chunkRowCount.ToString(CultureInfo.InvariantCulture));
                writer.Write('\n');
                writer.Write(contentSubFolderName ?? string.Empty);
                writer.Write('\n');
                WriteFingerprintRows(writer, folders.Select(f => new object[] { f }));
                WriteFingerprintRows(writer, types);
                WriteFingerprintRows(writer, rows);
                writer.Flush();
                crypto.FlushFinalBlock();
                return BitConverter.ToString(sha.Hash, 0, 4).Replace("-", "").ToLowerInvariant();
            }
        }

        // Row projection for ComputeGenerationId. The cell count is part of the text
        // (rows are variable length — an omitted file means "derive the anchor
        // client-side"), so a row cannot be confused with a longer one whose extra
        // cells are empty.
        private static void WriteFingerprintRows(TextWriter writer, IEnumerable<object[]> rows)
        {
            foreach (var row in rows)
            {
                writer.Write(row.Length.ToString(CultureInfo.InvariantCulture));
                foreach (var cell in row)
                {
                    writer.Write('\t');
                    writer.Write(Convert.ToString(cell, CultureInfo.InvariantCulture));
                }
                writer.Write('\n');
            }
        }

        protected override string ReplaceTokens(string templateBody)
        {
            var tocContent = TransformNamespaceTocsToHtml();

            var finalBody = templateBody.Replace("{TOC}", tocContent);
            // Same cache-busting query DocShell puts on every generated page's assets.
            finalBody = finalBody.Replace("{cssVersionQuery}",
                string.IsNullOrEmpty(Options.AssetVersion) ? "" : "?v=" + Options.AssetVersion);
            // Same pre-paint theme bootstrap DocShell emits on every generated page.
            finalBody = finalBody.Replace("{themeBootstrap}", DocShell.ThemeBootstrapScript);
            return finalBody;
        }

        NamespaceToc BuildNamespaceModel(string ns, AssemblyWrapper sdkAssemblyWrapper)
        {
            var nsFilePath = Path.Combine("./" + Options.ContentSubFolderName,
                                          GenerationManifest.OutputSubFolderFromNamespace(ns),
                                          FilenameGenerator.GenerateNamespaceFilename(ns)).Replace('\\', '/');

            var nsToc = new NamespaceToc
            {
                Id = ns.Replace(".", "_"),
                Href = nsFilePath,
                Nodes = new List<TocNode>()
            };

            foreach (var type in sdkAssemblyWrapper.GetTypesForNamespace(ns).OrderBy(x => x.Name))
            {
                var filePath = Path.Combine("./" + Options.ContentSubFolderName,
                                            GenerationManifest.OutputSubFolderFromNamespace(type.Namespace),
                                            FilenameGenerator.GenerateFilename(type)).Replace('\\', '/');

                nsToc.Nodes.Add(new TocNode
                {
                    Name = type.GetDisplayName(false),
                    // The id MUST equal the type page's runtime data-tocid so app.js can
                    // sync/highlight the active node. The page sets data-tocid from
                    // ClassWriter.GetTOCID() = FullName.Replace('.','_'), then
                    // FilenameGenerator.Escape (backtick -> "&#96;"). The browser HTML-decodes
                    // the attribute, so at runtime it reads back as FullName.Replace('.','_')
                    // with a literal backtick. toc.json is JSON (no HTML decoding), so we write
                    // the unescaped form here — escaping it would leave a literal "&#96;" that
                    // never matches the decoded attribute. (GetDisplayName(true) diverges for
                    // generic types — no namespace prefix, encoded "<>" — which broke sync.)
                    Id = type.FullName.Replace('.', '_'),
                    Href = filePath
                });
            }

            return nsToc;
        }

        /// <summary>
        /// Emit the set of namespace files encapsulated in json to a TOC based around
        /// unordered lists, returning the html for inclusion on the page.
        /// </summary>
        /// <returns></returns>
        string TransformNamespaceTocsToHtml()
        {
            var writer = new StringWriter();
            writer.Write("<ul>");
            foreach (var ns in _namespaceTocs.Keys.OrderBy(x => x))
            {
                var nsToc = _namespaceTocs[ns];

                // No expander button: TOC.html loads no JavaScript (it is the no-JS /
                // SEO fallback), so the tree renders fully expanded and an empty,
                // permanently aria-expanded="false" button would be inert and misleading.
                // The anchor id exists solely for the aria-labelledby reference below;
                // nothing styles or scripts this markup, so no classes or li ids.
                writer.Write(@"<li>
                                <a href=""{1}"" id=""{0}-parentnode"">{2}</a>",
                             nsToc.Id,
                             nsToc.Href,
                             ns);
                // No role: region is a LANDMARK, so one per namespace floods a screen
                // reader's landmark list with 1,300+ entries (the scripted sidebar in
                // app.js avoids the same trap by using group inside its tree), and ANY
                // role here — region or group — overrides the native list semantics that
                // are exactly what this markup wants. TOC.html is the no-JS/SEO
                // fallback: a plain nested <ul> announces as "list, N items", which a
                // namespace's type list should, and the nesting inside the parent <li>
                // already conveys the grouping a group role would add.
                // aria-labelledby stays (it is valid on the implicit list role) and must
                // reference the anchor's actual id (nsId-parentnode); using nsName here
                // previously dangled (no element has that id), leaving the list unnamed.
                // <ul> is not void, so no self-closing slash.
                writer.Write("<ul aria-labelledby=\"{0}-parentnode\">", nsToc.Id);

                foreach (var node in nsToc.Nodes)
                {
                    writer.Write("<li><a href=\"{0}\">{1}</a></li>",
                                 node.Href,
                                 node.Name);
                }

                writer.Write("</ul></li>");
            }
            writer.Write("</ul>");

            return writer.ToString();
        }
    }
}
