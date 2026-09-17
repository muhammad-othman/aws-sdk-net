using Xunit;

using SDKDocGenerator;
using SDKDocGenerator.Writers;

namespace SDKDocGenerator.UnitTests
{
    // Guards the two BaseWriter helpers that turn generator data into page HTML:
    // the syntax-section normalization for the highlight.js pipeline, and the
    // cross-reference replacement that interpolates doc-comment cref text.
    public class BaseWriterTests
    {
        // Resolves nothing, so cref replacement exercises the unresolved and
        // system.* fallback branches without loading SDK assemblies.
        private sealed class ResolveNothingProvider : AbstractTypeProvider
        {
            public ResolveNothingProvider() : base("test:") { }
        }

        // Every CSS/JS/cross-page href on every generated page starts with this
        // prefix; a depth regression 404s all of them on the published site while
        // the build stays green. Cases pin: both separator styles (the '\'-only
        // split once broke every Linux/macOS-generated page), depth 1 (a page
        // directly in items/) vs depth 2 (items/<Service>/, the common case),
        // and a trailing separator on the command-line output folder (which used
        // to over-trim one component).
        [Theory]
        [InlineData(@"C:\dep\docs\items", @"C:\dep\docs\items\S3\TS3Client.html", "../..")]
        [InlineData("/dep/docs/items", "/dep/docs/items/S3/TS3Client.html", "../..")]
        [InlineData("/dep/docs/items", "/dep/docs/items/TOC.html", "..")]
        [InlineData("/dep/docs/items/", "/dep/docs/items/S3/TS3Client.html", "../..")]
        [InlineData(@"C:\dep\docs\items\", @"C:\dep\docs\items\S3\TS3Client.html", "../..")]
        [InlineData(@"..\..\Deployment\docs\items", @"..\..\Deployment\docs\items\EC2\TInstance.html", "../..")]
        // A file AT the doc-set root gets "." (not ""): the prefix is used as
        // "{prefix}/resources/…", and "" would make that root-absolute.
        [InlineData("/dep/docs/items", "/dep/docs/index.html", ".")]
        public void RelativePathToRoot_ClimbsExactlyToTheDocSetRoot(string outputFolder, string filePath, string expected)
        {
            Assert.Equal(expected, BaseWriter.ComputeRelativePathToRoot(outputFolder, filePath));
        }

        [Fact]
        public void SyntaxMarkup_ReducesToPlainTextWithRealLineBreaks()
        {
            // SyntaxWriter's shape: attribute lines end in <br/> (with NO following
            // space — WriteToken skips it after <br/>) and keywords ride in styled
            // spans. highlight.js reads textContent, so the <br/> must become a real
            // newline and the spans must go, while the pre-encoded generic entities
            // stay encoded.
            var markup = "[ObsoleteAttribute(\"Use V2\")]<br/>"
                + "<span style=\"color:Blue;\">public</span> <span style=\"color:Blue;\">class</span> Foo&lt;T&gt;";

            var text = BaseWriter.SyntaxMarkupToPlainText(markup);

            Assert.Equal("[ObsoleteAttribute(\"Use V2\")]\npublic class Foo&lt;T&gt;", text);
            Assert.DoesNotContain("<span", text);
            Assert.DoesNotContain("<br", text);
        }

        [Fact]
        public void SyntaxMarkup_HandlesBrSpacingVariants()
        {
            Assert.Equal("a\nb", BaseWriter.SyntaxMarkupToPlainText("a<br />b"));
            Assert.Equal("a\nb", BaseWriter.SyntaxMarkupToPlainText("a<BR/>b"));
        }

        [Fact]
        public void SyntaxMarkup_TagShapedFreeTextLeavesAsInertEntities()
        {
            // An [Obsolete("...")] message is interpolated verbatim by SyntaxWriter
            // and can carry tag-shaped text. The output is written into <pre><code>
            // raw, so everything that is not generator markup (span/br) must leave
            // entity-encoded — including nested-tag constructs that a naive
            // strip-all-tags pass would reassemble into a live <script>.
            var markup = "[Obsolete(\"see <<b>script</b> src=https://evil.example/x.js></script> docs\")]<br/>"
                + "<span style=\"color:Blue;\">class</span> C";

            var text = BaseWriter.SyntaxMarkupToPlainText(markup);

            Assert.DoesNotContain("<script", text);
            Assert.DoesNotContain("<b>", text);
            Assert.Contains("&lt;b&gt;script", text);
            Assert.EndsWith("\nclass C", text);
        }

        [Fact]
        public void SystemCref_WithQuote_CannotEscapeHrefAttribute()
        {
            // cref values are doc-comment text. An unresolvable name that still
            // starts with "system." flows into the MSDN URL — a raw double quote
            // there would close the href attribute and inject fresh ones.
            var result = BaseWriter.CreateCrossReferenceTagReplacement(
                new ResolveNothingProvider(),
                "T:System.String\" autofocus onfocus=\"alert(1)",
                FrameworkVersion.DotNet472);

            Assert.DoesNotContain("\" autofocus", result);
            Assert.DoesNotContain("onfocus=\"", result);
            Assert.Contains("&quot;", result);
        }

        [Fact]
        public void SystemCref_WithMarkup_IsEncodedInLinkBody()
        {
            var result = BaseWriter.CreateCrossReferenceTagReplacement(
                new ResolveNothingProvider(),
                "T:System.<img src=x onerror=alert(1)>",
                FrameworkVersion.DotNet472);

            Assert.DoesNotContain("<img", result);
            Assert.Contains("&lt;img", result);
        }

        [Fact]
        public void UnresolvedNonSystemCref_StaysEncodedText()
        {
            // The pre-existing encoded branch — locked in so the three return
            // paths stay uniformly encoded.
            var result = BaseWriter.CreateCrossReferenceTagReplacement(
                new ResolveNothingProvider(),
                "T:Custom.<b onclick=\"x()\">Type</b>",
                FrameworkVersion.DotNet472);

            Assert.DoesNotContain("<b onclick", result);
            Assert.Contains("&lt;b", result);
        }
    }
}
