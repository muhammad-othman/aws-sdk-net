using System.Collections.Generic;
using System.Text.Json;

using Xunit;

using SDKDocGenerator;
using SDKDocGenerator.Writers;

namespace SDKDocGenerator.UnitTests
{
    // Pins the toc.json / search-index*.json data contracts. Three independent
    // clients consume these files and their expectations live only in comments:
    // app.js hydrates the sidebar from toc.json and syncs the active node by
    // matching node id against the page's HTML-decoded data-tocid;
    // search-worker.js requires the v:2 manifest + chunk shape, resolves
    // hrefs as <base>/<f[t[typeIdx][0]]>/<file> and derives anchor-kind files
    // as <typeFile>#<prefix><name>; index.html's legacy-bookmark resolver
    // walks namespaces[].nodes[]{id, href} with "./"-stripped hrefs.
    // A green build after an incompatible change here used to be invisible
    // until production (sidebar never highlighting, search links 404ing).
    public class TOCWriterJsonTests
    {
        [Fact]
        public void TocJson_EmitsExpectedShape_SortedByNamespace()
        {
            var tocs = new Dictionary<string, TOCWriter.NamespaceToc>
            {
                // Deliberately inserted out of alphabetical order.
                ["Amazon.S3"] = new TOCWriter.NamespaceToc
                {
                    Id = "Amazon_S3",
                    Href = "./items/S3/NS3.html",
                    Nodes = new List<TOCWriter.TocNode>
                    {
                        new TOCWriter.TocNode { Name = "AmazonS3Client", Id = "Amazon_S3_AmazonS3Client", Href = "./items/S3/TS3Client.html" }
                    }
                },
                ["Amazon.EC2"] = new TOCWriter.NamespaceToc
                {
                    Id = "Amazon_EC2",
                    Href = "./items/EC2/NEC2.html",
                    Nodes = new List<TOCWriter.TocNode>()
                },
            };

            using (var doc = JsonDocument.Parse(TOCWriter.SerializeTocJson(tocs)))
            {
                var namespaces = doc.RootElement.GetProperty("namespaces");
                Assert.Equal(2, namespaces.GetArrayLength());

                // Sorted by namespace name, not insertion order.
                Assert.Equal("Amazon.EC2", namespaces[0].GetProperty("name").GetString());
                Assert.Equal("Amazon.S3", namespaces[1].GetProperty("name").GetString());

                var s3 = namespaces[1];
                Assert.Equal(GenerationManifest.OutputSubFolderFromNamespace("Amazon.S3"),
                    s3.GetProperty("service").GetString());
                Assert.Equal("Amazon_S3", s3.GetProperty("id").GetString());
                // Leading "./" stripped so clients resolve against data-root.
                Assert.Equal("items/S3/NS3.html", s3.GetProperty("href").GetString());

                var node = s3.GetProperty("nodes")[0];
                Assert.Equal("AmazonS3Client", node.GetProperty("name").GetString());
                Assert.Equal("Amazon_S3_AmazonS3Client", node.GetProperty("id").GetString());
                Assert.Equal("items/S3/TS3Client.html", node.GetProperty("href").GetString());
            }
        }

        [Fact]
        public void TocJson_GenericTypeIds_KeepLiteralBacktick()
        {
            // The page's data-tocid attribute is HTML-decoded by the browser back to a
            // literal backtick; toc.json is JSON (no HTML decoding), so the id must be
            // written UNescaped — a "&#96;" here would never match at runtime.
            var tocs = new Dictionary<string, TOCWriter.NamespaceToc>
            {
                ["Amazon.X"] = new TOCWriter.NamespaceToc
                {
                    Id = "Amazon_X",
                    Href = "./items/X/NX.html",
                    Nodes = new List<TOCWriter.TocNode>
                    {
                        new TOCWriter.TocNode { Name = "Pool<T>", Id = "Amazon_X_Pool`1", Href = "./items/X/TPool&#96;1.html" }
                    }
                },
            };

            using (var doc = JsonDocument.Parse(TOCWriter.SerializeTocJson(tocs)))
            {
                var node = doc.RootElement.GetProperty("namespaces")[0].GetProperty("nodes")[0];
                Assert.Equal("Amazon_X_Pool`1", node.GetProperty("id").GetString());
                // Hrefs, by contrast, mirror the emitted FILENAME, which does carry the
                // FilenameGenerator.Escape form.
                Assert.Equal("items/X/TPool&#96;1.html", node.GetProperty("href").GetString());
            }
        }

        [Theory]
        [InlineData("./items/S3/T.html", "items/S3/T.html")]
        [InlineData("items/S3/T.html", "items/S3/T.html")]
        [InlineData("", "")]
        [InlineData(null, null)]
        public void NormalizeHref_StripsExactlyOneLeadingDotSlash(string input, string expected)
        {
            Assert.Equal(expected, TOCWriter.NormalizeHref(input));
        }

        [Fact]
        public void SearchIndex_EmitsV2ManifestAndChunk_WithInternedFolderAndTypeTables()
        {
            var entries = new List<TOCWriter.MemberEntry>
            {
                new TOCWriter.MemberEntry { Folder = "S3", Name = "PutObject", Kind = 1, File = "MS3PutObject.html", Type = "AmazonS3Client", TypeFile = "TS3Client.html" },
                // Anchor kind (property): no File — the client derives
                // TypeFile + "#prop_" + Name.
                new TOCWriter.MemberEntry { Folder = "S3", Name = "BucketName", Kind = 2, Type = "PutObjectRequest", TypeFile = "TPutObjectRequest.html" },
                // Same declaring type again: must reuse the interned type row.
                new TOCWriter.MemberEntry { Folder = "S3", Name = "Key", Kind = 2, Type = "PutObjectRequest", TypeFile = "TPutObjectRequest.html" },
                new TOCWriter.MemberEntry { Folder = "EC2", Name = "RunInstances", Kind = 1, File = "MEC2Run.html", Type = "AmazonEC2Client", TypeFile = "TEC2Client.html", Sig = "(RunInstancesRequest)" },
            };

            var files = TOCWriter.SerializeSearchIndexFiles("items", entries, chunkRowCount: 100);
            Assert.Equal(2, files.Count);
            Assert.Equal("search-index.json", files[0].FileName);
            Assert.Equal("search-index-0.json", files[1].FileName);

            using (var doc = JsonDocument.Parse(files[0].Json))
            {
                var root = doc.RootElement;
                // search-worker.js rejects any other version — bump BOTH sides together.
                Assert.Equal(2, root.GetProperty("v").GetInt32());
                Assert.Equal("items", root.GetProperty("base").GetString());

                // Folder table: de-duplicated, ordered by first use.
                var f = root.GetProperty("f");
                Assert.Equal(2, f.GetArrayLength());
                Assert.Equal("S3", f[0].GetString());
                Assert.Equal("EC2", f[1].GetString());

                // Type table: [folderIdx, typeName, typeFile], de-duplicated,
                // ordered by first use.
                var t = root.GetProperty("t");
                Assert.Equal(3, t.GetArrayLength());
                Assert.Equal(0, t[0][0].GetInt32());
                Assert.Equal("AmazonS3Client", t[0][1].GetString());
                Assert.Equal("TS3Client.html", t[0][2].GetString());
                Assert.Equal("PutObjectRequest", t[1][1].GetString());
                Assert.Equal(1, t[2][0].GetInt32()); // EC2 folder slot
                Assert.Equal("AmazonEC2Client", t[2][1].GetString());

                var chunks = root.GetProperty("chunks");
                Assert.Equal(1, chunks.GetArrayLength());
                Assert.Equal("search-index-0.json", chunks[0].GetString());
            }

            using (var doc = JsonDocument.Parse(files[1].Json))
            {
                var root = doc.RootElement;
                Assert.Equal(2, root.GetProperty("v").GetInt32());
                var rows = root.GetProperty("m");
                Assert.Equal(4, rows.GetArrayLength());

                // Own-page row: [typeIdx, kind, name, file].
                Assert.Equal(4, rows[0].GetArrayLength());
                Assert.Equal(0, rows[0][0].GetInt32());
                Assert.Equal(1, rows[0][1].GetInt32());
                Assert.Equal("PutObject", rows[0][2].GetString());
                Assert.Equal("MS3PutObject.html", rows[0][3].GetString());

                // Anchor-kind row stops after the name: [typeIdx, kind, name].
                Assert.Equal(3, rows[1].GetArrayLength());
                Assert.Equal(1, rows[1][0].GetInt32()); // interned PutObjectRequest slot
                Assert.Equal(2, rows[1][1].GetInt32());
                Assert.Equal("BucketName", rows[1][2].GetString());
                Assert.Equal(1, rows[2][0].GetInt32()); // same type row reused

                // Overloaded method: [typeIdx, kind, name, file, sig].
                Assert.Equal(5, rows[3].GetArrayLength());
                Assert.Equal(2, rows[3][0].GetInt32());
                Assert.Equal("(RunInstancesRequest)", rows[3][4].GetString());
            }
        }

        [Fact]
        public void SearchIndex_SplitsRowsAcrossChunks_InOrder()
        {
            var entries = new List<TOCWriter.MemberEntry>
            {
                new TOCWriter.MemberEntry { Folder = "S3", Name = "A", Kind = 2, Type = "T1", TypeFile = "T1.html" },
                new TOCWriter.MemberEntry { Folder = "S3", Name = "B", Kind = 2, Type = "T1", TypeFile = "T1.html" },
                new TOCWriter.MemberEntry { Folder = "S3", Name = "C", Kind = 2, Type = "T1", TypeFile = "T1.html" },
            };

            var files = TOCWriter.SerializeSearchIndexFiles("items", entries, chunkRowCount: 2);
            Assert.Equal(3, files.Count); // manifest + 2 chunks

            using (var doc = JsonDocument.Parse(files[0].Json))
            {
                var chunks = doc.RootElement.GetProperty("chunks");
                Assert.Equal(2, chunks.GetArrayLength());
                Assert.Equal("search-index-0.json", chunks[0].GetString());
                Assert.Equal("search-index-1.json", chunks[1].GetString());
            }
            using (var doc = JsonDocument.Parse(files[1].Json))
            {
                var rows = doc.RootElement.GetProperty("m");
                Assert.Equal(2, rows.GetArrayLength());
                Assert.Equal("A", rows[0][2].GetString());
                Assert.Equal("B", rows[1][2].GetString());
            }
            using (var doc = JsonDocument.Parse(files[2].Json))
            {
                var rows = doc.RootElement.GetProperty("m");
                Assert.Equal(1, rows.GetArrayLength());
                Assert.Equal("C", rows[0][2].GetString());
            }
        }

        [Fact]
        public void SearchIndex_NoEntries_StillEmitsManifestAndOneEmptyChunk()
        {
            // Uniform client contract: the worker always has a chunks list with at
            // least one file to walk, so the empty doc set needs no special casing.
            var files = TOCWriter.SerializeSearchIndexFiles("items", new List<TOCWriter.MemberEntry>(), chunkRowCount: 2);
            Assert.Equal(2, files.Count);

            using (var doc = JsonDocument.Parse(files[0].Json))
            {
                Assert.Equal(0, doc.RootElement.GetProperty("t").GetArrayLength());
                Assert.Equal(1, doc.RootElement.GetProperty("chunks").GetArrayLength());
            }
            using (var doc = JsonDocument.Parse(files[1].Json))
            {
                Assert.Equal(0, doc.RootElement.GetProperty("m").GetArrayLength());
            }
        }

        [Fact]
        public void SearchIndex_HtmlEncodedDisplayNames_SurviveJsonRoundTrip()
        {
            // Type display names arrive HTML-encoded (generics as "List&lt;T&gt;");
            // the client decodes them before rendering. JSON serialization must not
            // mangle or double-escape them.
            var entries = new List<TOCWriter.MemberEntry>
            {
                new TOCWriter.MemberEntry { Folder = "Core", Name = "Items", Kind = 2, Type = "List&lt;PutObjectRequest&gt;", TypeFile = "T.html" },
            };

            var files = TOCWriter.SerializeSearchIndexFiles("items", entries, chunkRowCount: 100);
            using (var doc = JsonDocument.Parse(files[0].Json))
            {
                Assert.Equal("List&lt;PutObjectRequest&gt;", doc.RootElement.GetProperty("t")[0][1].GetString());
            }
        }

        [Fact]
        public void KindCodes_MatchClientAnchorPrefixes()
        {
            // The v2 index tags anchor-kind rows with these numeric codes, and
            // search-worker.js hard-codes ANCHOR_PREFIX = {2:"prop_", 3:"field_",
            // 5:"member_"} to derive their hrefs (app.js's GROUPS/KIND_ICONS mirror
            // the full set for icons/grouping). Renumbering a constant without
            // updating the client maps would make every affected deep-link derive
            // the wrong anchor while the build stays green — this pins the pairing.
            Assert.Equal(1, TOCWriter.KindMethod);
            Assert.Equal(2, TOCWriter.KindProperty);   // -> "prop_"
            Assert.Equal(3, TOCWriter.KindField);      // -> "field_"
            Assert.Equal(4, TOCWriter.KindEvent);
            Assert.Equal(5, TOCWriter.KindEnumMember); // -> "member_"
        }

        [Fact]
        public void MemberAnchors_MatchTypePageRowIds()
        {
            // ClassWriter stamps these ids on the <tr> rows of the type page, and
            // search-worker.js DERIVES the same strings client-side (ANCHOR_PREFIX:
            // prop_/field_/member_ + member name) for the file-less anchor-kind rows
            // of the v:2 index. If any side ever drifts, search deep-links land at
            // the top of the page instead of the row.
            Assert.Equal("prop_BucketName", FilenameGenerator.PropertyAnchor("BucketName"));
            Assert.Equal("field_MaxErrorRetry", FilenameGenerator.FieldAnchor("MaxErrorRetry"));
            Assert.Equal("member_Standard", FilenameGenerator.EnumMemberAnchor("Standard"));
        }
    }
}
