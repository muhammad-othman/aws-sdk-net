using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SDKDocGenerator.Writers
{
    public class TOCWriter : BaseTemplateWriter
    {
        // internal (not private): the serialization contract these shapes feed is
        // consumed by three independent clients and pinned by unit tests.
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

        // Local-search member index kind codes (toc.json already covers types and
        // namespaces, so the index holds only members; constructors excluded).
        // Only members DECLARED on a type are indexed — inherited members would
        // duplicate across pages. CONTRACT: mirrored by GROUPS/KIND_ICONS in
        // resources/app.js and ANCHOR_PREFIX in resources/search-worker.js (which
        // derives #anchors from the kind code) — renumbering here with stale
        // clients derives wrong anchors while every build stays green, so the
        // values are pinned by KindCodes_MatchClientAnchorPrefixes.
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

        // Keyed by namespace, like _namespaceTocs, so a namespace processed twice
        // replaces its entries in BOTH artifacts — toc.json and the search index
        // must always describe the same type set.
        private readonly Dictionary<string, List<MemberEntry>> _searchEntriesByNamespace =
            new Dictionary<string, List<MemberEntry>>();

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
            // Both artifacts replace-on-rewrite (last write wins): a namespace rebuilt
            // from a later pass must never leave toc.json and the search index
            // describing different type sets.
            _namespaceTocs[nameSpace] = BuildNamespaceModel(nameSpace, sdkAssemblyWrapper);
            _searchEntriesByNamespace[nameSpace] = CollectSearchEntries(nameSpace, sdkAssemblyWrapper);
        }

        /// <summary>
        /// Collects the members of every type in the namespace into the local-search
        /// index. Files are built with the same OutputSubFolderFromNamespace +
        /// FilenameGenerator calls ClassWriter uses, so search links match the pages
        /// that are actually emitted: methods/events carry their own page file;
        /// properties, fields and enum members are rows on the type page, whose
        /// "#anchor" link the client derives from TypeFile + the member name.
        /// </summary>
        List<MemberEntry> CollectSearchEntries(string nameSpace, AssemblyWrapper sdkAssemblyWrapper)
        {
            var entries = new List<MemberEntry>();
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
                        AddSearchEntry(entries, folder, enumName, KindEnumMember, typeName, typePageFile);
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
                    AddSearchEntry(entries, folder, info.Name, KindMethod, typeName, typePageFile,
                        FilenameGenerator.GenerateFilename(info), sig);
                }
                foreach (var info in type.GetEvents())
                {
                    if (!IsDeclaredOn(info.DeclaringType, type))
                        continue;
                    AddSearchEntry(entries, folder, info.Name, KindEvent, typeName, typePageFile,
                        FilenameGenerator.GenerateFilename(info));
                }

                // Properties and fields are rows on the type page, reached via #anchor —
                // derived client-side, so no File here.
                foreach (var info in type.GetProperties())
                {
                    if (!IsDeclaredOn(info.DeclaringType, type))
                        continue;
                    AddSearchEntry(entries, folder, info.Name, KindProperty, typeName, typePageFile);
                }
                foreach (var info in type.GetFields())
                {
                    if (!IsDeclaredOn(info.DeclaringType, type))
                        continue;
                    AddSearchEntry(entries, folder, info.Name, KindField, typeName, typePageFile);
                }
            }
            return entries;
        }

        // A member is "declared" on the page's type when its declaring type matches;
        // inherited members render on the page but are indexed under their own type.
        static bool IsDeclaredOn(TypeWrapper declaringType, TypeWrapper pageType)
        {
            return declaringType != null && string.Equals(declaringType.FullName, pageType.FullName);
        }

        static void AddSearchEntry(List<MemberEntry> entries, string folder, string name, int kind, string type, string typeFile,
            string ownFile = null, string sig = null)
        {
            entries.Add(new MemberEntry
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

        // Rows per chunk file: ~50k keeps each chunk around 1.5-2 MB raw
        // (~400 KB gzipped). The worker answers queries once the manifest and the
        // first chunk are in; this knob mainly bounds what one failed chunk costs.
        internal const int SearchIndexChunkRowCount = 50000;

        // Index format version, emitted as "v" in the manifest and every chunk.
        // search-worker.js rejects anything else (CONTRACT: bump it there too).
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
        ///   manifest: { "v":2, "base":"items", "f":[ "S3", … ],
        ///               "t":[ [folderIdx, typeName, typeFile], … ],
        ///               "chunks":[ "search-index-0.json", … ] }
        ///   chunk:    { "v":2,
        ///               "m":[ [typeIdx, kind, name],                           ← anchor kinds
        ///                     [typeIdx, kind, name, file],                     ← own-page kinds
        ///                     [typeIdx, kind, name, file, sig], … ] }          ← overloaded methods
        /// Declaring types are interned into the manifest's "t" table so member rows
        /// carry an index, not a repeated type-name/file pair. Rows for anchor kinds
        /// (properties, fields, enum members — the overwhelming majority) omit the
        /// file: search-worker.js derives "&lt;typeFile&gt;#&lt;prefix&gt;&lt;name&gt;" with the same
        /// prop_/field_/member_ prefixes FilenameGenerator.*Anchor emits (CONTRACT:
        /// change them together). Full hrefs resolve as &lt;base&gt;/&lt;f[t[typeIdx][0]]&gt;/&lt;file&gt;.
        /// Every fetch carries the ?v= data-version query, so manifest and chunks
        /// stay version-coherent and can be served with immutable cache headers.
        /// </summary>
        void WriteSearchIndexJson()
        {
            // Flattened in namespace order (matching toc.json) so the emitted chunks
            // are deterministic regardless of generation order.
            var searchEntries = _searchEntriesByNamespace
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .SelectMany(kvp => kvp.Value)
                .ToList();
            foreach (var artifact in SerializeSearchIndexFiles(Options.ContentSubFolderName, searchEntries, SearchIndexChunkRowCount))
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

            var artifacts = new List<SearchIndexArtifact>
            {
                new SearchIndexArtifact
                {
                    FileName = "search-index.json",
                    Json = System.Text.Json.JsonSerializer.Serialize(
                        new { v = SearchIndexFormatVersion, @base = contentSubFolderName, f = folders, t = types, chunks = chunkNames })
                }
            };
            for (var c = 0; c < chunks.Count; c++)
            {
                artifacts.Add(new SearchIndexArtifact
                {
                    FileName = chunkNames[c],
                    Json = System.Text.Json.JsonSerializer.Serialize(new { v = SearchIndexFormatVersion, m = chunks[c] })
                });
            }
            return artifacts;
        }

        protected override string ReplaceTokens(string templateBody)
        {
            var tocContent = TransformNamespaceTocsToHtml();

            var finalBody = templateBody.Replace("{TOC}", tocContent);
            // Same cache-busting query DocShell puts on every generated page's assets.
            finalBody = finalBody.Replace("{cssVersionQuery}",
                string.IsNullOrEmpty(Options.DataVersion) ? "" : "?v=" + Options.DataVersion);
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
                    // The id MUST equal the page's runtime data-tocid so app.js can
                    // highlight the active node. The attribute is HTML-decoded by the
                    // browser (backtick escape undone), toc.json is not — so write the
                    // UNescaped form or generic-type ids never match.
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

                // TOC.html is the no-JS / SEO fallback: fully expanded, so no expander
                // button, and nothing styles or scripts this markup.
                writer.Write(@"<li>
                                <a href=""{1}"" id=""{0}-parentnode"">{2}</a>",
                             nsToc.Id,
                             nsToc.Href,
                             ns);
                // No role: region/group would flood a screen reader's landmark list or
                // override the native list semantics this markup wants; aria-labelledby
                // must reference the anchor's actual id or the list goes unnamed.
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
