using System.IO;
using System.Text.RegularExpressions;

using Xunit;

using SDKDocGenerator.Writers;

namespace SDKDocGenerator.UnitTests
{
    // Guards the assembled page shell emitted by DocShell (head + chrome + footer):
    // it must be well-formed, balanced, and contain the accessibility/navigation
    // hooks the client runtime relies on (focusable #main, aria-live region, etc.).
    public class DocShellTests
    {
        private static string RenderShell()
        {
            var o = new DocShell.Options
            {
                RootRelativePath = "../..",
                Title = "AmazonS3Client Class",
                TocId = "Amazon_S3_AmazonS3Client",
                CanonicalUrl = "https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TAmazonS3Client.html"
            };
            using (var sw = new StringWriter())
            {
                DocShell.WriteHeadAndChrome(sw, o);
                sw.Write("<div id=\"pageContent\"><h1>AmazonS3Client</h1></div>");
                DocShell.WriteFootShell(sw);
                return sw.ToString();
            }
        }

        [Fact]
        public void Shell_ContainerTagsAreBalanced()
        {
            var html = RenderShell();
            // The chrome opens layout containers in WriteHeadAndChrome that WriteFootShell
            // must close. Count start vs end tags for each container element (ignoring the
            // <div>s inside the inline <script>, which contains no markup tags). An imbalance
            // means the footer failed to close something the head opened.
            AssertBalanced(html, "div");
            AssertBalanced(html, "main");
            AssertBalanced(html, "body");
            AssertBalanced(html, "html");
            AssertBalanced(html, "header");
            AssertBalanced(html, "aside");
            AssertBalanced(html, "nav");
        }

        private static void AssertBalanced(string html, string tag)
        {
            var open = Regex.Matches(html, "<" + tag + "(?:\\s[^>]*)?>").Count;
            var selfClose = Regex.Matches(html, "<" + tag + "(?:\\s[^>]*)?/>").Count;
            var close = Regex.Matches(html, "</" + tag + ">").Count;
            Assert.Equal(open - selfClose, close);
        }

        [Fact]
        public void Shell_HasFaviconAtDocSetRoot()
        {
            var html = RenderShell();
            Assert.Contains("<link rel=\"icon\" href=\"../../favicon.ico\"/>", html);
        }

        [Fact]
        public void Shell_HasSidebarFilterStatusRegion()
        {
            // app.js announces filter outcomes into this visually-hidden live region;
            // without it the announcement getElementById silently no-ops.
            var html = RenderShell();
            Assert.Contains("<div id=\"sidebarFilterStatus\" class=\"visually-hidden\" role=\"status\"></div>", html);
        }

        [Fact]
        public void Shell_NoScriptKeepsSidebarReachableOnSmallViewports()
        {
            // The sidebar's noscript TOC link is the only no-JS navigation entry, and
            // the off-canvas drawer can only be opened by app.js. Without scripting the
            // shell must put the sidebar back into the document flow at ≤1024px.
            var html = RenderShell();
            Assert.Contains("<noscript><style>", html);
            Assert.Contains("#sidebar { position: static; transform: none; visibility: visible;", html);
        }

        [Fact]
        public void Shell_MainIsFocusableSwapTarget()
        {
            var html = RenderShell();
            // #main is the htmx swap target; app.js focuses it after a swap (tabindex=-1).
            Assert.Contains("<main id=\"main\" role=\"main\" tabindex=\"-1\"", html);
        }

        [Fact]
        public void Shell_HistorySnapshotIsConfinedToMain()
        {
            var html = RenderShell();
            // Without hx-history-elt, htmx snapshots/restores body.innerHTML on
            // Back/Forward, resurrecting the chrome as listener-less dead markup.
            Assert.Contains("hx-history-elt", html);
            var mainTag = html.Substring(html.IndexOf("<main "), html.IndexOf(">", html.IndexOf("<main ")) - html.IndexOf("<main "));
            Assert.Contains("hx-history-elt", mainTag);
        }

        [Fact]
        public void Shell_ContentShellCarriesTocIdForHistoryRestores()
        {
            var html = RenderShell();
            // The per-page identifier must ride on .content-shell (inside #main):
            // history restores swap only #main's innerHTML, so an attribute on
            // #main itself would go stale.
            Assert.Contains("<div class=\"content-shell\" data-tocid=\"Amazon_S3_AmazonS3Client\">", html);
        }

        [Fact]
        public void Shell_HasSkipLinkBeforeChrome()
        {
            var html = RenderShell();
            Assert.Contains("class=\"skip-link\" href=\"#main\"", html);
            Assert.True(html.IndexOf("skip-link") < html.IndexOf("<header"),
                "skip link must be the first Tab stop, ahead of the topbar");
        }

        [Fact]
        public void Shell_NavToggleExposesExpandedState()
        {
            var html = RenderShell();
            Assert.Contains("id=\"navToggle\"", html);
            var toggleTag = html.Substring(html.IndexOf("<button id=\"navToggle\""), 200);
            Assert.Contains("aria-expanded=\"false\"", toggleTag);
            Assert.Contains("aria-controls=\"sidebar\"", toggleTag);
        }

        [Fact]
        public void Shell_AssetLinksCarryCacheBustingVersion()
        {
            var o = new DocShell.Options
            {
                RootRelativePath = "../..",
                Title = "T",
                DataVersion = "abcd1234"
            };
            using (var sw = new StringWriter())
            {
                DocShell.WriteHeadAndChrome(sw, o);
                DocShell.WriteFootShell(sw);
                var html = sw.ToString();
                Assert.Contains("../../resources/aws-docs.css?v=abcd1234", html);
                Assert.Contains("../../resources/app.js?v=abcd1234", html);
                Assert.Contains("../../resources/htmx.min.js?v=abcd1234", html);
                // One DataVersion token drives both the asset links and data-datav.
                Assert.Contains("data-datav=\"abcd1234\"", html);
                // The docs-platform boot script is not ours to version.
                Assert.Contains("src=\"/assets/js/awsdocs-boot.js\"", html);
            }
        }

        [Fact]
        public void Shell_OmitsVersionQueryWhenUnset()
        {
            var html = RenderShell(); // RenderShell leaves DataVersion null
            Assert.DoesNotContain("?v=", html);
        }

        [Fact]
        public void Shell_EscapesQuotesInAttributeValues()
        {
            var o = new DocShell.Options
            {
                RootRelativePath = ".",
                Title = "Say \"hello\"",
                TocId = "Toc\"Id"
            };
            using (var sw = new StringWriter())
            {
                DocShell.WriteHeadAndChrome(sw, o);
                DocShell.WriteFootShell(sw);
                var html = sw.ToString();
                // Attribute contexts must not let a quote break out of the value.
                Assert.Contains("content=\"Say &quot;hello&quot;\"", html);
                Assert.Contains("data-tocid=\"Toc&quot;Id\"", html);
            }
        }

        [Fact]
        public void Shell_BrandLinkHonorsContentSubFolder()
        {
            var o = new DocShell.Options
            {
                RootRelativePath = "..",
                Title = "T",
                ContentSubFolder = "content"
            };
            using (var sw = new StringWriter())
            {
                DocShell.WriteHeadAndChrome(sw, o);
                DocShell.WriteFootShell(sw);
                Assert.Contains("href=\"../content/sdk-api-home.html\"", sw.ToString());
            }
        }

        [Fact]
        public void Shell_HasPersistentLiveRegionForAnnouncements()
        {
            var html = RenderShell();
            // Lives outside #main so it survives swaps; app.js writes the new title here.
            Assert.Contains("id=\"navAnnounce\"", html);
            Assert.Contains("aria-live=\"polite\"", html);
        }

        [Fact]
        public void Shell_HasSingleMainAndBody()
        {
            var html = RenderShell();
            Assert.Equal(1, CountOccurrences(html, "<main "));
            Assert.Equal(1, CountOccurrences(html, "<body "));
            // Scripts load in dependency order: the shared search scorer must run
            // before the runtime (app.js) that uses it.
            Assert.True(html.IndexOf("search-core.js") < html.IndexOf("app.js"));
        }

        [Fact]
        public void Shell_HardensHtmxConfigBeforeLoadingHtmx()
        {
            var html = RenderShell();
            // Without this meta, htmx ships with allowEval/allowScriptTags on — any
            // markup reaching page content would gain eval-capable gadgets.
            Assert.Contains("name=\"htmx-config\"", html);
            Assert.Contains("\"allowEval\":false", html);
            Assert.Contains("\"allowScriptTags\":false", html);
            Assert.Contains("\"selfRequestsOnly\":true", html);
            // Attribute settling REMOVES settleable attributes (class/style/width/
            // height by default) from an element whose same-id twin in the incoming
            // page lacks them — which is every runtime inline style inside #main,
            // #regionDisclaimer's display:block among them.
            Assert.Contains("\"attributesToSettle\":[]", html);
            Assert.True(html.IndexOf("htmx-config") < html.IndexOf("htmx.min.js"),
                "htmx-config must be emitted before the htmx script loads");
        }

        [Fact]
        public void Shell_BodyCarriesDataVersionWhenSet()
        {
            var o = new DocShell.Options
            {
                RootRelativePath = "..",
                Title = "T",
                DataVersion = "0123abcd"
            };
            using (var sw = new StringWriter())
            {
                DocShell.WriteHeadAndChrome(sw, o);
                DocShell.WriteFootShell(sw);
                // The client runtime appends this as ?v= to the toc.json /
                // search-index.json / _sdk-versions.json fetches.
                Assert.Contains("data-datav=\"0123abcd\"", sw.ToString());
            }
        }

        [Fact]
        public void Shell_OmitsDataVersionAttributeWhenUnset()
        {
            var html = RenderShell(); // RenderShell leaves DataVersion null
            Assert.DoesNotContain("data-datav", html);
        }

        [Fact]
        public void Shell_SearchModalCarriesExternalEscapeHatch()
        {
            var html = RenderShell();
            // app.js updateExternalLink resolves this by id and no-ops silently
            // when absent — without this guard the full-text escalation link could
            // vanish with no test signal. rel=noopener is the security-relevant
            // part of a target=_blank link.
            var idx = html.IndexOf("id=\"searchModalExternal\"");
            Assert.True(idx >= 0, "search modal must carry the external full-text search link");
            var tag = html.Substring(html.LastIndexOf("<a", idx), 200);
            Assert.Contains("target=\"_blank\"", tag);
            Assert.Contains("rel=\"noopener noreferrer\"", tag);
            Assert.Contains("hidden", tag);
        }

        [Fact]
        public void Shell_ThemeToggleExposesPressedState()
        {
            var html = RenderShell();
            var toggleTag = html.Substring(html.IndexOf("<button id=\"themeToggle\""), 200);
            // app.js applyTheme keeps this in sync; without it a screen reader
            // can't tell which theme is active or whether the toggle worked.
            Assert.Contains("aria-pressed=", toggleTag);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i)) != -1) { count++; i += needle.Length; }
            return count;
        }
    }
}
