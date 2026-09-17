using System;
using System.Xml.Linq;

using Xunit;

using SDKDocGenerator;

namespace SDKDocGenerator.UnitTests
{
    // <summary>/<para> map to <div class="doc-para"> (block) rather than <p>, because their
    // content may include block-level <note>/<important> noteblocks — a <div> inside a <p> is
    // invalid HTML and browsers auto-close the <p>, leaving stray empty paragraphs. The
    // "doc-para" class restores paragraph spacing. These expectations track that mapping
    // (see NdocToHtmlElementMapping / NdocToHtmlClassMapping in NDocUtilities).
    public class NDocUtilitiesHtmlTests
    {
        private static string TransformSummary(string innerXml)
        {
            var xml = $"<doc><summary>{innerXml}</summary></doc>";
            var element = XElement.Parse(xml);
            return NDocUtilities.TransformDocumentationToHTML(element, "summary", null, FrameworkVersion.DotNet472);
        }

        // Resolves every cref to System.String so tests can exercise the
        // resolved-cross-reference branches without loading SDK assemblies.
        private sealed class ResolveEverythingProvider : AbstractTypeProvider
        {
            public ResolveEverythingProvider() : base("test:") { }
            public override TypeWrapper GetType(string name)
            {
                return new TypeWrapper(typeof(string), DocId);
            }
        }

        [Fact]
        public void ImportantTag_RendersAsWarningNoteblock()
        {
            var result = TransformSummary("<important><para>text</para></important>");

            Assert.Equal(
                "<div class=\"doc-para\"><div class=\"noteblock noteblock-warning\"><div class=\"doc-para\">text</div></div></div>",
                result);
        }

        [Fact]
        public void NoteTag_RendersAsNoteblock()
        {
            var result = TransformSummary("<note><para>text</para></note>");

            Assert.Equal(
                "<div class=\"doc-para\"><div class=\"noteblock\"><div class=\"doc-para\">text</div></div></div>",
                result);
        }

        [Fact]
        public void ParaTag_RendersAsDocParaBlock()
        {
            var result = TransformSummary("<para>text</para>");

            Assert.Equal("<div class=\"doc-para\"><div class=\"doc-para\">text</div></div>", result);
        }

        [Fact]
        public void SelfClosingImportantTag_ProducesEmptyWarningNoteblock()
        {
            var result = TransformSummary("<important />");

            // Empty elements must emit a full end tag (<div></div>), not the XML
            // self-closing form (<div/>) — an HTML parser treats <div/> as an unclosed
            // start tag that swallows following siblings. See NDocUtilities self-closing
            // branch (WriteFullEndElement).
            Assert.Equal(
                "<div class=\"doc-para\"><div class=\"noteblock noteblock-warning\"></div></div>",
                result);
        }

        [Fact]
        public void EmptyPara_EmitsFullEndTagNotSelfClosingDiv()
        {
            // An empty <para/> is extremely common in the SDK doc XML. It must render as
            // <div class="doc-para"></div> (full end tag), never the XML self-closing
            // <div class="doc-para" /> — an HTML parser treats <div/> as an unclosed tag
            // that swallows following siblings, mis-nesting the rest of the block.
            var result = TransformSummary("<para/>");

            Assert.Equal(
                "<div class=\"doc-para\"><div class=\"doc-para\"></div></div>",
                result);
            Assert.DoesNotContain("/>", result);
        }

        [Fact]
        public void EmptyParaWithEndTag_EmitsFullEndTagNotSelfClosingDiv()
        {
            // <para></para> (empty but NOT self-closing) also collapses to <div/> via
            // XmlWriter.WriteEndElement unless forced to a full end tag. This is the form
            // that actually appears in the SDK XML (e.g. an empty <para></para> after a list).
            var result = TransformSummary("<para></para>");

            Assert.Equal(
                "<div class=\"doc-para\"><div class=\"doc-para\"></div></div>",
                result);
            Assert.DoesNotContain("/>", result);
        }

        [Fact]
        public void AnchorWithNameOnly_IsPreservedAsAnchorBookmark()
        {
            // <a name="foo"> is an in-page bookmark target; keep it as <a> (a `name`
            // attribute only creates a fragment target on an anchor, not on a <span>).
            var result = TransformSummary("<a name=\"foo\">bookmark</a>");

            Assert.Equal(
                "<div class=\"doc-para\"><a name=\"foo\">bookmark</a></div>",
                result);
        }

        [Fact]
        public void AnchorWithNeitherHrefNorName_BecomesSpan()
        {
            // A bare <a> with no cref, href, or name is not a real link — render as <span>
            // so nothing looks clickable but dead.
            var result = TransformSummary("<a>text</a>");

            Assert.Equal(
                "<div class=\"doc-para\"><span>text</span></div>",
                result);
        }

        [Fact]
        public void ImportantTagWithInnerMarkup_PreservesNestedElements()
        {
            var result = TransformSummary("<important><para>Use <c>X</c> instead.</para></important>");

            Assert.Equal(
                "<div class=\"doc-para\"><div class=\"noteblock noteblock-warning\"><div class=\"doc-para\">Use <c>X</c> instead.</div></div></div>",
                result);
        }

        [Fact]
        public void NoteTagWithTypeAttribute_PreservesAttribute()
        {
            var result = TransformSummary("<note type=\"caution\"><para>Be careful.</para></note>");

            Assert.Equal(
                "<div class=\"doc-para\"><div class=\"noteblock\" type=\"caution\"><div class=\"doc-para\">Be careful.</div></div></div>",
                result);
        }

        [Fact]
        public void BulletList_RendersAsUnorderedList()
        {
            var result = TransformSummary("<list type=\"bullet\"><item><description>text</description></item></list>");

            Assert.Equal(
                "<div class=\"doc-para\"><ul><li><span>text</span></li></ul></div>",
                result);
        }

        [Fact]
        public void NumberedList_RendersAsOrderedList()
        {
            var result = TransformSummary("<list type=\"number\"><item><description>first</description></item></list>");

            Assert.Equal(
                "<div class=\"doc-para\"><ol><li><span>first</span></li></ol></div>",
                result);
        }

        [Fact]
        public void ListWithTermAndDescription_RendersBoth()
        {
            var result = TransformSummary("<list type=\"bullet\"><item><term>T</term><description>D</description></item></list>");

            Assert.Equal(
                "<div class=\"doc-para\"><ul><li><span>T</span><span>D</span></li></ul></div>",
                result);
        }

        [Fact]
        public void ListDefaultsToUnorderedList()
        {
            var result = TransformSummary("<list><item><description>text</description></item></list>");

            Assert.Equal(
                "<div class=\"doc-para\"><ul><li><span>text</span></li></ul></div>",
                result);
        }

        [Fact]
        public void EntityEncodedMarkupInText_StaysText()
        {
            // Service-model doc strings are authored outside this repo; entity-encoded
            // markup in them is literal text and must never come back to life as
            // elements on the generated page (stored XSS).
            var result = TransformSummary("Returns &lt;img src=x onerror=alert(1)&gt; markers.");

            Assert.Equal(
                "<div class=\"doc-para\">Returns &lt;img src=x onerror=alert(1)&gt; markers.</div>",
                result);
        }

        [Fact]
        public void AmpersandInText_IsEncoded()
        {
            var result = TransformSummary("S3 &amp; EC2");

            Assert.Equal("<div class=\"doc-para\">S3 &amp; EC2</div>", result);
        }

        [Fact]
        public void EventHandlerAndHtmxAttributes_AreDropped()
        {
            var result = TransformSummary(
                "<span onclick=\"alert(1)\" onmouseover=\"x()\" hx-get=\"/pwn\" data-hx-trigger=\"load\">text</span>");

            Assert.Equal("<div class=\"doc-para\"><span>text</span></div>", result);
        }

        [Fact]
        public void JavascriptSchemeHref_DemotesAnchorToSpanAndDropsHref()
        {
            var result = TransformSummary("<a href=\"javascript:alert(1)\">click</a>");

            Assert.Equal("<div class=\"doc-para\"><span>click</span></div>", result);
        }

        [Fact]
        public void ObfuscatedJavascriptSchemeHref_IsAlsoDropped()
        {
            // Browsers strip whitespace/control characters when sniffing the scheme.
            var result = TransformSummary("<a href=\"jav\tascript:alert(1)\">click</a>");

            Assert.Equal("<div class=\"doc-para\"><span>click</span></div>", result);
        }

        [Fact]
        public void HttpsHref_KeepsAnchorWithNoopenerBlankTarget()
        {
            var result = TransformSummary("<a href=\"https://example.com/x\">docs</a>");

            Assert.Equal(
                "<div class=\"doc-para\"><a href=\"https://example.com/x\" target=\"_blank\" rel=\"noopener noreferrer\">docs</a></div>",
                result);
        }

        [Fact]
        public void RelativeHref_KeepsAnchorWithoutBlankTarget()
        {
            var result = TransformSummary("<a href=\"../S3/TS3Client.html\">S3</a>");

            Assert.Equal(
                "<div class=\"doc-para\"><a href=\"../S3/TS3Client.html\">S3</a></div>",
                result);
        }

        [Fact]
        public void ScriptElement_IsDemotedToInertSpan()
        {
            var result = TransformSummary("<script>alert(1)</script>");

            Assert.Equal("<div class=\"doc-para\"><span>alert(1)</span></div>", result);
        }

        [Fact]
        public void SelfClosingDisallowedVoidElement_EmitsBalancedSpan()
        {
            // <link/> is a void element, but its demoted <span> replacement is not —
            // it must get a full end tag, never <span/>. (The href echo as content is
            // the standard self-closing-href treatment, e.g. <see href="…"/>; on a
            // span it is inert.)
            var result = TransformSummary("before<link href=\"x.css\"/>after");

            Assert.Equal("<div class=\"doc-para\">before<span href=\"x.css\">x.css</span>after</div>", result);
        }

        [Fact]
        public void NameAttributeAfterResolvedCref_IsWrittenAsTextNotRawHtml()
        {
            // Regression: a name (or href) attribute AFTER a resolved cref overwrites
            // emptyElementContents with doc-supplied text; the "raw HTML" flag from the
            // cref branch must not survive that overwrite, or the value reaches the
            // page unencoded (stored XSS).
            var xml = "<doc><summary><see cref=\"T:System.String\" name=\"&lt;img src=x onerror=alert(1)&gt;\"/></summary></doc>";
            var element = XElement.Parse(xml);
            var result = NDocUtilities.TransformDocumentationToHTML(
                element, "summary", new ResolveEverythingProvider(), FrameworkVersion.DotNet472);

            Assert.DoesNotContain("<img", result);
            Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", result);
        }

        [Fact]
        public void SeeAlso_EncodesLabelAndHref()
        {
            var xml = "<doc><seealso href=\"https://example.com/?a=1&amp;b=2\"><b onclick=\"alert(1)\">docs</b></seealso></doc>";
            var element = XElement.Parse(xml);
            var result = NDocUtilities.TransformDocumentationToHTML(
                element, "seealso", null, FrameworkVersion.DotNet472);

            // The label is doc-comment text: markup in it must not survive as elements.
            Assert.DoesNotContain("onclick", result);
            Assert.Contains("docs", result);
            Assert.Contains("href=\"https://example.com/?a=1&amp;b=2\"", result);
        }

        [Fact]
        public void SeeAlso_DropsJavascriptSchemeLink()
        {
            var xml = "<doc><seealso href=\"javascript:alert(1)\">docs</seealso></doc>";
            var element = XElement.Parse(xml);
            var result = NDocUtilities.TransformDocumentationToHTML(
                element, "seealso", null, FrameworkVersion.DotNet472);

            Assert.DoesNotContain("javascript:", result);
            Assert.DoesNotContain("<a", result);
            Assert.Contains("docs", result);
        }

        [Fact]
        public void CodeElementText_IsEncodedExactlyOnce()
        {
            // Generic type names in inline code must render as text, not be parsed
            // as markup (and must not be double-encoded either).
            var result = TransformSummary("<code>List&lt;string&gt;</code>");

            Assert.Equal("<div class=\"doc-para\"><code>List&lt;string&gt;</code></div>", result);
        }

        [Fact]
        public void UppercaseAnchorAndHref_CannotBypassSchemeFilter()
        {
            // XML treats <A HREF> as names distinct from <a href>, but HTML folds
            // both to lowercase — the sanitizer must judge the element the way a
            // browser will read it, or a case-variant spelling revives the link.
            var result = TransformSummary("<A HREF=\"javascript:alert(1)\">click</A>");

            Assert.Equal("<div class=\"doc-para\"><span>click</span></div>", result);
        }

        [Fact]
        public void UppercaseHrefBesideNameAttribute_IsStillFiltered()
        {
            // The name attribute keeps the element an <a> (bookmark target), so the
            // demotion path doesn't apply — the attribute loop itself must still
            // recognize HREF as href and drop the unsafe scheme.
            var result = TransformSummary("<a name=\"x\" HREF=\"javascript:alert(1)\">click</a>");

            Assert.Equal("<div class=\"doc-para\"><a name=\"x\">click</a></div>", result);
        }

        [Fact]
        public void UppercaseHttpsHref_KeepsAnchorWithFoldedAttributeName()
        {
            // A legitimate link spelled HREF must keep working (folded to href),
            // including the target=_blank/noopener treatment for absolute links.
            var result = TransformSummary("<a HREF=\"https://example.com/x\">docs</a>");

            Assert.Equal(
                "<div class=\"doc-para\"><a href=\"https://example.com/x\" target=\"_blank\" rel=\"noopener noreferrer\">docs</a></div>",
                result);
        }

        [Fact]
        public void UppercaseScriptElement_IsDemotedToInertSpan()
        {
            var result = TransformSummary("<SCRIPT>alert(1)</SCRIPT>");

            Assert.Equal("<div class=\"doc-para\"><span>alert(1)</span></div>", result);
        }

        [Fact]
        public void SchemeRelativeHref_IsTreatedAsUnsafe()
        {
            // "//evil.example" has no scheme colon but resolves absolute — it must
            // not pass the allowlist as a relative link. Browsers also normalize
            // backslashes here, so both flavors are rejected; a single leading
            // slash (root-relative) stays fine.
            Assert.False(NDocUtilities.IsSafeDocUrl("//evil.example/x"));
            Assert.False(NDocUtilities.IsSafeDocUrl("/\\evil.example/x"));
            Assert.False(NDocUtilities.IsSafeDocUrl("\\\\evil.example/x"));
            Assert.True(NDocUtilities.IsSafeDocUrl("/rooted/path.html"));

            var result = TransformSummary("<a href=\"//evil.example/x\">click</a>");

            Assert.Equal("<div class=\"doc-para\"><span>click</span></div>", result);
        }

        [Fact]
        public void IdentityStyleAndDataAttributes_AreDropped()
        {
            // id would DOM-clobber the getElementById wiring in app.js (a planted
            // #assemblyVersion with data-version-file triggers an attacker-chosen
            // fetch), data-* are the client runtime's behavior hooks, and
            // style/tabindex/autofocus reshape the page. None may flow from a doc
            // comment; benign presentational attributes (title here) still pass.
            var result = TransformSummary(
                "<span id=\"assemblyVersion\" data-version-file=\"https://evil.example/v.json\""
                + " style=\"position:fixed\" tabindex=\"1\" autofocus=\"autofocus\""
                + " formaction=\"/pwn\" title=\"ok\">text</span>");

            Assert.Equal("<div class=\"doc-para\"><span title=\"ok\">text</span></div>", result);
        }

        [Fact]
        public void BehaviorHookAttributes_AreDropped()
        {
            // contenteditable turns doc text into an editing surface, is= rebinds the
            // element to a custom-element definition, accesskey/draggable/slot reshape
            // interaction, and the form* family overrides form submission targets.
            var result = TransformSummary(
                "<span contenteditable=\"true\" is=\"evil-el\" accesskey=\"x\" draggable=\"true\""
                + " slot=\"s\" form=\"f\" formmethod=\"post\" formtarget=\"_top\" title=\"ok\">text</span>");

            Assert.Equal("<div class=\"doc-para\"><span title=\"ok\">text</span></div>", result);
        }

        [Fact]
        public void CaseVariantDuplicateAttributes_EmitOnlyOne()
        {
            // href and HREF are distinct XML names that fold to ONE HTML attribute;
            // the first occurrence wins and the duplicate must not abort generation
            // (XmlWriter throws on writing the same attribute twice).
            var result = TransformSummary("<a href=\"a.html\" HREF=\"b.html\">docs</a>");

            Assert.Equal("<div class=\"doc-para\"><a href=\"a.html\">docs</a></div>", result);
        }

        [Fact]
        public void AuthorRelAndTargetOnAbsoluteLink_AreReplacedNotDuplicated()
        {
            // rel/target/ping never flow from a doc comment: an author target used
            // to suppress — and an author rel could invert (rel="opener") — the
            // generator's noopener hardening, and the duplicate rel write aborted
            // the whole generation run (XmlWriter throws on a second rel). The
            // generator's own target/rel are now the only ones, unconditionally.
            var result = TransformSummary(
                "<a href=\"https://example.com/x\" rel=\"opener\" target=\"_self\""
                + " ping=\"https://evil.example/beacon\">docs</a>");

            Assert.Equal(
                "<div class=\"doc-para\"><a href=\"https://example.com/x\""
                + " target=\"_blank\" rel=\"noopener noreferrer\">docs</a></div>",
                result);
        }

        [Fact]
        public void CaseVariantRelOnAbsoluteLink_DoesNotAbortGeneration()
        {
            // Regression guard for the duplicate-attribute crash: REL folds to rel,
            // which the generator writes itself after the copy loop — the author
            // copy must be dropped, not written-then-collided-with.
            var result = TransformSummary("<a href=\"https://example.com/x\" REL=\"nofollow\">docs</a>");

            Assert.Equal(
                "<div class=\"doc-para\"><a href=\"https://example.com/x\""
                + " target=\"_blank\" rel=\"noopener noreferrer\">docs</a></div>",
                result);
        }

        [Fact]
        public void AuthorClass_IsDropped()
        {
            // class reaches the shipped stylesheet's behavioral selectors
            // (class="search-modal" is a full-viewport fixed overlay) — the same
            // page-reshaping power the style ban removes. Generator-emitted classes
            // (doc-para here) are unaffected.
            var result = TransformSummary("<span class=\"search-modal\">text</span>");

            Assert.Equal("<div class=\"doc-para\"><span>text</span></div>", result);
        }

        [Fact]
        public void GeneratorCodeSampleClasses_ArePreserved()
        {
            // PreprocessCodeBlocksToPreTags stamps these classes onto elements the
            // generator itself inserts into the doc XML; the client runtime needs
            // them (highlight.js keys on language-csharp). The author-class ban
            // must not strip them.
            var result = TransformSummary(
                "<h4 class=\"csharp-code-sample-title\">Sample</h4>"
                + "<pre><code class=\"language-csharp\">var x = 1;</code></pre>");

            Assert.Contains("class=\"language-csharp\"", result);
            Assert.Contains("class=\"csharp-code-sample-title\"", result);
        }

        [Fact]
        public void ResolvedCrefBesideAuthorHref_DoesNotAbortGeneration()
        {
            // A resolved cref is renamed to href; a literal author href beside it
            // used to be a second write of the same attribute name, which aborts
            // the whole generation run (XmlWriter throws on duplicates). First
            // write wins.
            var xml = "<doc><summary><see cref=\"T:System.String\" href=\"other.html\">docs</see></summary></doc>";
            var element = XElement.Parse(xml);
            var result = NDocUtilities.TransformDocumentationToHTML(
                element, "summary", new ResolveEverythingProvider(), FrameworkVersion.DotNet472);

            Assert.Contains("docs", result);
            Assert.Equal(1, CountOccurrences(result, "href=\""));
        }

        [Fact]
        public void SelfClosingResolvedCref_WrapsReferenceInSpanNotNestedAnchor()
        {
            // A self-closing resolved cref's content is a generator-built anchor
            // (CreateReferenceHtml); the wrapper must be a <span>, not an outer
            // <a href="T:..."> — nested anchors are invalid HTML that browsers
            // re-parent unpredictably, and the T: echo is a dead link.
            var xml = "<doc><summary><see cref=\"T:System.String\"/></summary></doc>";
            var element = XElement.Parse(xml);
            var result = NDocUtilities.TransformDocumentationToHTML(
                element, "summary", new ResolveEverythingProvider(), FrameworkVersion.DotNet472);

            Assert.DoesNotContain("href=\"T:", result);
            Assert.Equal(1, CountOccurrences(result, "<a "));
            Assert.StartsWith("<div class=\"doc-para\"><span>", result);
        }

        [Fact]
        public void BackslashAbsoluteHref_GetsTargetAndRelHardening()
        {
            // Browsers normalize "https:\\host" to "https://host" — a cross-origin
            // absolute link. The target/rel hardening must judge the normalized
            // form, not require a literal "https://" prefix.
            var result = TransformSummary("<a href=\"https:\\\\example.com\\x\">docs</a>");

            Assert.Contains("target=\"_blank\"", result);
            Assert.Contains("rel=\"noopener noreferrer\"", result);
        }

        [Fact]
        public void ImgNameAttribute_IsDropped()
        {
            // <img name="..."> is a document named object: the name becomes an own
            // property of `document` that shadows prototype members, so a doc
            // comment could clobber document.getElementById for the whole page.
            // <a name> bookmarks stay (anchors are not document named objects).
            var result = TransformSummary("<img src=\"images/x.png\" name=\"getElementById\"/>");

            Assert.DoesNotContain("name=", result);
            Assert.Contains("src=\"images/x.png\"", result);
        }

        [Fact]
        public void CdataInDocText_IsEncodedTextNotLiveMarkup()
        {
            // CDATA is writable in /// comments; its content is doc text and must
            // neither abort generation nor come back to life as elements.
            var result = TransformSummary("<![CDATA[<script>alert(1)</script>]]>");

            Assert.Equal("<div class=\"doc-para\">&lt;script&gt;alert(1)&lt;/script&gt;</div>", result);
        }

        [Fact]
        public void XmlCommentAndProcessingInstruction_AreDroppedWithoutAborting()
        {
            var result = TransformSummary("a<!-- note -->b<?pi data?>c");

            Assert.Equal("<div class=\"doc-para\">abc</div>", result);
        }

        [Fact]
        public void NamespacedAttributes_AreDroppedWithoutAborting()
        {
            // xml:lang / xmlns / prefixed names are XML plumbing, not HTML
            // attributes — writing them would abort the whole generation run
            // (':' is invalid in an XmlWriter local name).
            var result = TransformSummary(
                "<span xml:lang=\"en\" xmlns:p=\"urn:x\" p:href=\"javascript:alert(1)\">text</span>");

            Assert.Equal("<div class=\"doc-para\"><span>text</span></div>", result);
        }

        [Fact]
        public void NonDocIdCrefPrefix_ThrowsInsteadOfBecomingLinkScheme()
        {
            // A resolved cref is renamed to href, so a multi-letter "prefix" would
            // ride into the page as a URL scheme — cref="javascript:Some.Type" must
            // be a build error, not a link.
            var xml = "<doc><summary><see cref=\"javascript:Amazon.S3.Foo\">x</see></summary></doc>";
            var element = XElement.Parse(xml);

            var ex = Assert.Throws<InvalidOperationException>(
                () => NDocUtilities.TransformDocumentationToHTML(
                    element, "summary", new ResolveEverythingProvider(), FrameworkVersion.DotNet472));

            Assert.Contains("javascript:Amazon.S3.Foo", ex.Message);
        }

        private static int CountOccurrences(string text, string token)
        {
            int count = 0, index = 0;
            while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += token.Length;
            }
            return count;
        }

        // The C# compiler emits cref="!:<original text>" for a cref it cannot
        // resolve (warnings CS1574/CS1584 — but the doc XML still ships), and such
        // crefs exist in SDK sources today (e.g. DynamoDBv2's Search.Async
        // summaries). The payload is the author's original text VERBATIM, so it
        // can itself contain colons ("Overload:" doc-ids, pasted URLs). One author
        // typo must render as plain text like any unresolved cref, not abort the
        // entire generation run — and never become a link, even for a
        // scheme-shaped payload.
        [Theory]
        [InlineData("!:Search.GetNextSet", "Search.GetNextSet")]
        [InlineData("!:Overload:Foo.Bar", "Overload:Foo.Bar")]
        [InlineData("!:https://example.com/x", "https://example.com/x")]
        public void CompilerUnresolvedCref_DegradesToPlainTextInsteadOfAborting(string cref, string expectedText)
        {
            var xml = "<doc><summary><seealso cref=\"" + cref + "\"/></summary></doc>";
            var element = XElement.Parse(xml);
            var result = NDocUtilities.TransformDocumentationToHTML(
                element, "summary", new ResolveEverythingProvider(), FrameworkVersion.DotNet472);

            // Payload as text, nothing clickable, no cref/href residue — even
            // though the provider would resolve it, '!' means the COMPILER
            // already failed to, so it must not be looked up (the payload may
            // not even be a type name).
            Assert.Contains(expectedText, result);
            Assert.DoesNotContain("<a", result);
            Assert.DoesNotContain("cref", result);
            Assert.DoesNotContain("href", result);
        }

        [Fact]
        public void MalformedCref_ThrowsWithActionableMessage()
        {
            // Malformed crefs abort generation by design (a doc bug worth surfacing
            // at build time) — but the message must name the offending value, and
            // the check must fire for case-variant spellings too (CREF is cref to
            // the HTML consumer).
            var ex = Assert.Throws<InvalidOperationException>(
                () => TransformSummary("<span CREF=\"nocolon\">x</span>"));

            Assert.Contains("nocolon", ex.Message);
        }
    }
}
