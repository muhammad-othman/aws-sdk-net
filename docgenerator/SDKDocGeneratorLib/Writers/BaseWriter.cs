using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Reflection;

namespace SDKDocGenerator.Writers
{
    public abstract class BaseWriter
    {
        protected FrameworkVersion _version;        

        public static readonly List<TableColumnHeader> FieldTableColumnHeaders = new List<TableColumnHeader>
        {
            new TableColumnHeader { CssClass = "iconColumn" },
            new TableColumnHeader {Title = "Name", CssClass = "nameColumn"},
            new TableColumnHeader {Title = "Type", CssClass = "typeColumn"},
            new TableColumnHeader {Title = "Description", CssClass = "descriptionColumn"}
        };

        public static readonly List<TableColumnHeader> PropertiesTableColumnHeaders = new List<TableColumnHeader>
        {
            new TableColumnHeader { CssClass = "iconColumn" },
            new TableColumnHeader {Title = "Name", CssClass = "nameColumn"},
            new TableColumnHeader {Title = "Type", CssClass = "typeColumn"},
            new TableColumnHeader {Title = "Description", CssClass = "descriptionColumn"}
        };

        public static readonly List<TableColumnHeader> IconisedNameDescriptionTableColumnHeaders = new List<TableColumnHeader>
        {
            new TableColumnHeader {CssClass = "iconColumn"},
            new TableColumnHeader {Title = "Name", CssClass = "nameColumn"},
            new TableColumnHeader {Title = "Description", CssClass = "descriptionColumn"}
        };

        public static readonly List<TableColumnHeader> NameDescriptionTableColumnHeaders = new List<TableColumnHeader>
        {
            new TableColumnHeader {Title = "Name", CssClass = "nameColumn"},
            new TableColumnHeader {Title = "Description", CssClass = "descriptionColumn"}
        };

        private const string FeedbackSection =
            "<!-- BEGIN-FEEDBACK-SECTION --><span class=\"feedback\">{0}</span><!-- END-FEEDBACK-SECTION -->";

        public static string BJSDisclaimerTemplate
                    = "AWS services or capabilities described in AWS Documentation may vary by region/location. "
                    + "Click <a href=\"https://{0}/en_us/aws/latest/userguide/services.html\">Getting Started with Amazon AWS</a> to see specific differences applicable to the China (Beijing) Region.";
        
        public GenerationManifest Artifacts { get; private set; }
        public AbstractTypeProvider TypeProvider { get; private set; }

        protected BaseWriter(GenerationManifest artifacts, AbstractTypeProvider typeProvider, FrameworkVersion version)
        {
            Artifacts = artifacts;
            TypeProvider = typeProvider;
            _version = version;
        }

        protected BaseWriter(GenerationManifest artifacts, FrameworkVersion version)
            : this(artifacts, artifacts.ManifestAssemblyContext.SdkAssembly, version)
        {
        }

        public string BJSRegionDisclaimer
        {
            get
            {
                return string.Format(BJSDisclaimerTemplate, Artifacts.Options.BJSDocsDomain);
            }
        }

        protected abstract string GetTitle();
        protected abstract string GetMemberName();
        protected abstract string GetMemberType();
        protected abstract string GenerateFilename();
        protected abstract string GenerateFilepath();
        protected abstract XElement GetSummaryDocumentation();

        protected virtual void AddSummaryNotes(TextWriter writer)
        {
        }

        protected abstract void WriteContent(TextWriter writer);

        // the computed relative path(s) to the root of the doc set 
        // (ie to the folder containing ./items)
        protected string RootRelativePath { get; private set; }

        public void Write()
        {
            var filename = Path.Combine(Artifacts.OutputFolder, GenerateFilepath(), GenerateFilename());
            try
            {
                RootRelativePath = ComputeRelativePathToRoot(filename);
            }
            catch (PathTooLongException)
            {
                Console.WriteLine("Path is too long for file : {0}", filename);
                throw;
            }

            var directory = new FileInfo(filename).Directory.FullName;

            if (!Directory.Exists(directory))
            {
                Console.WriteLine("\t\tcreating directory: {0}", directory);
                Directory.CreateDirectory(directory);
            }

            using (var writer = new StringWriter())
            {
                // The page shell (head, top bar, sidebar, layout) is emitted by DocShell
                // so it stays identical to the landing page and lives in one place.
                var shell = new DocShell.Options
                {
                    RootRelativePath = RootRelativePath,
                    Title = GetTitle(),
                    TocId = FilenameGenerator.Escape(this.GetTOCID()),
                    ContentSubFolder = Artifacts.Options.ContentSubFolderName,
                    AssetVersion = Artifacts.Options.AssetVersion,
                    DataVersion = Artifacts.Options.DataVersion,
                    CanonicalUrl = string.Format(
                        "https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/{0}/{1}",
                        FilenameGenerator.Escape(this.GenerateFilepath()),
                        FilenameGenerator.Escape(this.GenerateFilename()))
                };

                DocShell.WriteHeadAndChrome(writer, shell);

                    WriteRegionDisclaimer(writer);

                    this.WriteHeader(writer);

                    writer.WriteLine("<div id=\"pageContent\">");
                        this.WriteContent(writer);
                    writer.WriteLine("</div>");

                    this.WriteFooter(writer);

                DocShell.WriteFootShell(writer);

                // normalize all line endings so any docs committed into Git present a consistent
                // set of line terminators for core.autocrlf to work with
                var content = new StringBuilder(writer.ToString());
                content.Replace("\r\n", "\n").Replace("\n", "\r\n");

                // The XML documentation will use the "<c>" tag, but the corresponding HTML tag is "<code>".
                // There's also a "<code>" tag in XML docs, but it has a different meaning (multiple lines of code); this can cause formatting issues such as
                // https://github.com/aws/aws-sdk-net/issues/1934 and https://github.com/aws/aws-sdk-net/issues/1954
                content
                    .Replace("<c>", "<code>")
                    .Replace("</c>", "</code>");

                using (var fileWriter = new StreamWriter(filename))
                {
                    fileWriter.Write(content);
                }
            }
        }

        protected virtual void WriteRegionDisclaimer(TextWriter writer)
        {
			// comment disclaimer is used by DCA pipeline only at present
            writer.WriteLine("<!--REGION_DISCLAIMER_DO_NOT_REMOVE-->");

            // the BJS disclaimer uses its own div with js/css control of
            // visibility instead of its own pipeline (currently) and that
            // div needs to be suppressed from the dca-deployed docs
            writer.WriteLine("<!-- BEGIN-SECTION -->");
			writer.WriteLine("<div id=\"regionDisclaimer\">");
            writer.WriteLine("<p>{0}</p>", BJSRegionDisclaimer);
            writer.WriteLine("</div>");
            writer.WriteLine("<!-- END-SECTION -->");
        }

        protected virtual void WriteHeader(TextWriter writer)
        {
            writer.WriteLine("<div id=\"pageHeader\">");
                writer.WriteLine("<div id=\"titles\">");
                    writer.WriteLine("<h1>{0}</h1>", this.GetMemberName());
                    if (this.GetMemberType() != null)
                        writer.WriteLine("<h2 class=\"subtitle\">{0}</h2>", this.GetMemberType());
                writer.WriteLine("</div>");
                this.WriteHeaderAside(writer);
            writer.WriteLine("</div>");
        }

        /// <summary>
        /// Optional content rendered on the right of the page header, on the same row
        /// as the title. Used by the class page for its "In this article" dropdown.
        /// Empty by default.
        /// </summary>
        protected virtual void WriteHeaderAside(TextWriter writer)
        {
        }

        protected virtual void WriteFooter(TextWriter writer)
        {
            writer.WriteLine("<div id=\"pageFooter\">");
                writer.WriteLine(FeedbackSection, GenerateFeedbackHTML());
                writer.WriteLine("<div id=\"awsdocs-legal-zone-copyright\"></div>");
            writer.WriteLine("</div>");
        }

        protected abstract string GetTOCID();

        private string ComputeRelativePathToRoot(string filePath)
        {
            return ComputeRelativePathToRoot(Artifacts.OutputFolder, filePath);
        }

        // Static seam so the one path-depth computation every page depends on is
        // pinned by unit tests: the returned prefix reaches the doc-set root from
        // the page's folder, and every CSS/JS/cross-page reference on every page
        // is built from it — a depth regression 404s all of them while the build
        // stays green (it happened once with a single-separator split here).
        // outputFolder is the ./items folder (the command-line docs root with
        // "items" appended — see SdkDocGenerator); its PARENT is the doc-set
        // root. filePath is a page inside it, built by Path.Combine(outputFolder,
        // …), so the two share their prefix verbatim. String-based (no Path.*) so
        // both separator styles behave identically on every OS: Path.* emits
        // '\' on Windows and '/' elsewhere, and splitting on '\' alone once
        // made every items/<Service>/ page one "../" short on Linux/macOS.
        internal static string ComputeRelativePathToRoot(string outputFolder, string filePath)
        {
            var separators = new[] { '\\', '/' };
            // Trailing separator would make the "parent" lookup below land on the
            // items folder itself and trim one path component too many.
            var itemsFolder = outputFolder.TrimEnd(separators);
            var rootLength = itemsFolder.LastIndexOfAny(separators); // docs root = items' parent
            var dirEnd = filePath.LastIndexOfAny(separators);
            if (dirEnd <= rootLength)
                return "."; // file sits at the doc-set root; "." keeps "{prefix}/x" hrefs relative ("" would make them root-absolute)
            var pathFromDocsRoot = filePath.Substring(rootLength + 1, dirEnd - rootLength - 1);
            var pathComponents = pathFromDocsRoot.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            var rel = new StringBuilder();
            for (var i = 0; i < pathComponents.Length; i++)
            {
                if (i > 0)
                    rel.Append("/");
                rel.Append("..");
            }
            return rel.ToString();
        }

        private string GenerateFeedbackHTML()
        {
            var filename = FilenameGenerator.Escape(Path.GetFileNameWithoutExtension(GenerateFilename()));
            const string baseUrl = "https://docs.aws.amazon.com/forms/aws-doc-feedback";
            var queryString = string.Format("?service_name={0}&amp;file_name={1}",
                                            "NET-Ref-V4",  // service_name
                                            filename   // guide_name
                                            );
            var fullUrl = baseUrl + queryString;

            const string feedbackContentFormat = "<span id=\"feedback\">" +
                                                "<!-- BEGIN-FEEDBACK-SECTION -->" +
                                                 "Did this page help you?&nbsp;&nbsp;" +
                                                 "<a href=\"https://docs.aws.amazon.com/sdkfornet/v4/apidocs/feedbackyes.html?topic_id={0}\" target=\"_blank\" rel=\"noopener noreferrer\">Yes</a>&nbsp;&nbsp;" +
                                                 "<a href=\"https://docs.aws.amazon.com/sdkfornet/v4/apidocs/feedbackno.html?topic_id={0}\" target=\"_blank\" rel=\"noopener noreferrer\">No</a>&nbsp;&nbsp;&nbsp;" +
                                                 "<a href=\"{1}\" target=\"_blank\" rel=\"noopener noreferrer\">Tell us about it...</a>" +
                                                 "</span>" +
                                                 "<!-- END-FEEDBACK-SECTION -->";
            string feedbackContent = string.Format(feedbackContentFormat, filename, fullUrl);
            return feedbackContent;
        }

        /// <summary>
        /// Builds the "Name(params)" HTML for a member link, inserting &lt;wbr&gt; word-break
        /// opportunities after the opening brace and each comma so long signatures wrap at
        /// natural boundaries first (breaking mid-word only as a last resort via CSS).
        /// </summary>
        protected static string FormatMemberSignatureHtml(string name, string parameters)
        {
            if (string.IsNullOrEmpty(parameters))
                return string.Format("{0}()", name);

            return string.Format("{0}(<wbr>{1})", name, parameters.Replace(", ", ",<wbr> "));
        }

        protected string FormatParameters(IList<ParameterInfoWrapper> infos)
        {
            var sb = new StringBuilder();
            foreach (var info in infos)
            {
                if (sb.Length > 0)
                    sb.Append(", ");

                string parameterTypeName;
                switch (info.ParameterType.FullName)
                {
                    case "System.String":
                        parameterTypeName = "string";
                        break;
                    case "System.Int32":
                        parameterTypeName = "int";
                        break;
                    case "System.Double":
                        parameterTypeName = "double";
                        break;
                    case "System.Float":
                        parameterTypeName = "float";
                        break;
                    case "System.Boolean":
                        parameterTypeName = "bool";
                        break;
                    case "System.Object":
                        parameterTypeName = "object";
                        break;
                    default:
                        parameterTypeName = info.ParameterType.GetDisplayName(false);
                        break;
                }

                sb.AppendFormat("{0}{1}", info.IsOut ? "out " : "", parameterTypeName);
            }
            return sb.ToString();
        }

        protected void AddMemberTableSectionHeader(TextWriter writer, string name, bool showIconColumn = true)
        {
            AddMemberTableSectionHeader(writer, 
                                        name, 
                                        showIconColumn 
                                            ? IconisedNameDescriptionTableColumnHeaders 
                                            : NameDescriptionTableColumnHeaders);
        }

        /// <summary>
        /// Adds a standard member-name/description table with optional type column
        /// </summary>
        /// <param name="writer"></param>
        /// <param name="title"></param>
        /// <param name="columnHeaders"></param>
        protected void AddMemberTableSectionHeader(TextWriter writer, 
                                                   string title, 
                                                   List<TableColumnHeader> columnHeaders)
        {
            writer.WriteLine("<div>");
                writer.WriteLine("<div>");
                    writer.WriteLine("<div class=\"collapsibleSection\">");
                    // ToLowerInvariant, not ToLower: the "In this article" links in
                    // WriteHeaderAside target these ids with hardcoded ASCII fragments
                    // (e.g. "#versioninformation"), so a build host in a Turkish/Azeri
                    // locale would otherwise lowercase 'I' to dotless 'ı' and break
                    // every one of them.
                    writer.WriteLine("<h2 id=\"{1}\" class=\"title\">{0}</h2>", title, title.Replace(" ", "").ToLowerInvariant());
                    writer.WriteLine("</div>");
                writer.WriteLine("</div>");

                writer.WriteLine("<div class=\"sectionbody\">");
                    writer.WriteLine("<table class=\"members\">");
                        writer.WriteLine("<tbody>");
                        writer.WriteLine("<tr>");

                        foreach (var ch in columnHeaders)
                        {
                            writer.Write("<th");
                            if (!string.IsNullOrEmpty(ch.Id))
                                writer.Write(" id=\"{0}\"", ch.Id);
                            if (!string.IsNullOrEmpty(ch.CssClass))
                                writer.Write(" class=\"{0}\"", ch.CssClass);
                            writer.Write(">");
                            if (!string.IsNullOrEmpty(ch.Title))
                                writer.Write(ch.Title);
                            writer.Write("</th>");
                        }
                        writer.WriteLine("</tr>");
        }

        protected void AddMemberTableSectionClosing(TextWriter writer)
        {
                        writer.WriteLine("</tbody>");
                    writer.WriteLine("</table>");
                writer.WriteLine("</div>");
            writer.WriteLine("</div>");
        }

        protected void AddSectionHeader(TextWriter writer, string name)
        {
            writer.WriteLine("<div>");
                writer.WriteLine("<div>");
                    writer.WriteLine("<div class=\"collapsibleSection\">");
                        // ToLowerInvariant: see AddMemberTableSectionHeader — these ids are
                        // the targets of hardcoded ASCII fragments in WriteHeaderAside.
                        writer.WriteLine("<h2 id=\"{1}\" class=\"title\">{0}</h2>", name, name.Replace(" ", "").ToLowerInvariant());
                    writer.WriteLine("</div>");
                writer.WriteLine("</div>");

                writer.WriteLine("<div class=\"sectionbody\">");
        }

        protected void AddSectionClosing(TextWriter writer)
        {
                writer.WriteLine("</div>");
            writer.WriteLine("</div>");
        }

        protected void AddSummaryDocumentation(TextWriter writer)
        {
            writer.WriteLine("<div id=\"summaryblock\">");

            var element = GetSummaryDocumentation();
            if (element != null)
            {
                var htmlDocs = NDocUtilities.TransformDocumentationToHTML(element, "summary", TypeProvider, this._version);
                writer.WriteLine(htmlDocs);

                AddSummaryNotes(writer);
            }

            writer.WriteLine("</div>");
        }

        protected void AddRemarksDocumentation(TextWriter writer)
        {
            var element = GetSummaryDocumentation();
            if (element != null)
            {
                var htmlDocs = NDocUtilities.TransformDocumentationToHTML(element, "remarks", TypeProvider, this._version);
                if (string.IsNullOrEmpty(htmlDocs))
                    return;

                AddSectionHeader(writer, "Remarks");
                writer.WriteLine(htmlDocs);
                AddSectionClosing(writer);
            }
        }

        protected void AddExamples(TextWriter writer)
        {
            var element = GetSummaryDocumentation();
            if (element != null)
            {
                var htmlDocs = NDocUtilities.TransformDocumentationToHTML(element, "example", TypeProvider, this._version);
                if (string.IsNullOrEmpty(htmlDocs))
                    return;

                AddSectionHeader(writer, "Examples");
                writer.WriteLine(htmlDocs);
                AddSectionClosing(writer);
            }
        }

        protected void AddSeeAlso(TextWriter writer)
        {
            var element = GetSummaryDocumentation();
            if (element != null)
            {
                var htmlDocs = NDocUtilities.TransformDocumentationToHTML(element, "seealso", TypeProvider, this._version);
                if (string.IsNullOrEmpty(htmlDocs))
                    return;

                AddSectionHeader(writer, "See Also");
                writer.WriteLine(htmlDocs);
                AddSectionClosing(writer);
            }
        }

        protected void AddNamespace(TextWriter writer, string ns, string moduleName)
        {
            // app.js reads the version file/service from data-* attributes and fills in
            // #assemblyVersion on page load / htmx:afterSwap (replacing the old inline jQuery ajax call).
            var isCore = Artifacts.ServiceName.Equals("Core", StringComparison.OrdinalIgnoreCase);
            var versionInfoFile = RootRelativePath + "/items/_sdk-versions.json";

            writer.WriteLine("<div id=\"namespaceblock\">");
                writer.Write("<p>");
                writer.Write("<strong>Namespace: </strong>{0}<br/>", ns);
                writer.Write("<strong>Assembly: </strong>{0}", moduleName);
                writer.Write("<span id=\"versionData\">");
                // "4.x.y.z" is only a pre-JS placeholder; app.js replaces it with the real
                // version from _sdk-versions.json on load. Keep the major matching this v4
                // reference so a JS-disabled reader never sees a misleading "3".
                if (isCore)
                    writer.Write("<br/><strong>Version: </strong><span id=\"assemblyVersion\" data-version-file=\"{0}\">4.x.y.z</span>",
                                 versionInfoFile);
                else
                    writer.Write("<br/><strong>Version: </strong><span id=\"assemblyVersion\" data-version-file=\"{0}\" data-service=\"{1}\">4.x.y.z</span>",
                                 versionInfoFile,
                                 Artifacts.ServiceName);
                writer.Write("</span>");
                writer.Write("</p>");
            writer.WriteLine("</div>");
        }

        protected void AddVersionInformation(TextWriter writer, AbstractWrapper wrapper)
        {
            AddSectionHeader(writer, "Version Information");
            
            var docs472 = NDocUtilities.FindDocumentation(Artifacts.NDocForPlatform("net472"), wrapper, TypeProvider);
            var docsCore20 = NDocUtilities.FindDocumentation(Artifacts.NDocForPlatform("netstandard2.0"), wrapper, TypeProvider);
            var docsNetCoreApp31 = NDocUtilities.FindDocumentation(Artifacts.NDocForPlatform("netcoreapp3.1"), wrapper, TypeProvider);
            var docsNet80 = NDocUtilities.FindDocumentation(Artifacts.NDocForPlatform("net8.0"), wrapper, TypeProvider);

            // If there is no documentation then assume it is available for all platforms.
            var boolNoDocs = docs472 == null && docsCore20 == null && docsNetCoreApp31 == null
                && docsNet80 == null;

            // .NET Core / .NET
            var netAppVersions = new List<string>();

            if (boolNoDocs || (wrapper != null && docsNet80 != null))
                netAppVersions.Add("8.0 and newer");

            if (boolNoDocs || (wrapper != null && docsNetCoreApp31 != null))
                netAppVersions.Add("Core 3.1");

            if (netAppVersions.Count > 0)
            {
                writer.WriteLine("<p><strong>.NET: </strong><br/>Supported in: {0}<br/>", string.Join(", ", netAppVersions));
            }

            // .NET Standard
            var netstandardVersions = new List<string>();            
            if (boolNoDocs || (wrapper != null && docsCore20 != null))
                netstandardVersions.Add("2.0");            

            if(netstandardVersions.Count > 0)
            {
                writer.WriteLine("<p><strong>.NET Standard: </strong><br/>Supported in: {0}<br/>", string.Join(", ", netstandardVersions));
            }

            // .NET Framework
            var netframeworkVersions = new List<string>();
            if (boolNoDocs || (wrapper != null && docs472 != null))
                netframeworkVersions.Add("4.7.2 and newer");

            if (netframeworkVersions.Count > 0)
            {
                writer.WriteLine("<p><strong>.NET Framework: </strong><br/>Supported in: {0}<br/>", string.Join(", ", netframeworkVersions));
            }                        

            AddSectionClosing(writer);
        }

        protected void AddSyntax(TextWriter writer, string csharpSyntax)
        {
            if (string.IsNullOrEmpty(csharpSyntax))
                return;

            AddSectionHeader(writer, "Syntax");

            writer.WriteLine("<div class=\"codeSnippetContainer\">");

                writer.WriteLine("<div class=\"codeSnippetContainerTabs\">");
                    writer.WriteLine("<div class=\"codeSnippetContainerTabActive\">");
                        // Not a link — there's only one language, so it's a static label.
                        writer.WriteLine("<span class=\"languageTabLabel\">C#</span>");
                    writer.WriteLine("</div>");
                writer.WriteLine("</div>");

                writer.WriteLine("<div class=\"codeSnippetContainerCodeContainer\">");
                    writer.WriteLine("<div>");
                        writer.WriteLine("<pre class=\"syntax\"><code class=\"language-csharp\">{0}</code></pre>", SyntaxMarkupToPlainText(csharpSyntax));
                    writer.WriteLine("</div>");
                writer.WriteLine("</div>");

            writer.WriteLine("</div>");

            AddSectionClosing(writer);
        }

        // Reduces SyntaxWriter's legacy inline markup (keyword spans with hard-coded
        // colors, <br/> line separators) to the plain text the highlight.js pipeline
        // expects: hljs reads textContent — where a <br/> contributes nothing and an
        // inline color style would fight the theme — and re-highlights from scratch.
        // Only the generator's own markup (span/br) is removed structurally. The
        // decode-then-re-encode pass leaves exactly ONE level of entity encoding on
        // everything else, because the result is written into <pre><code> with no
        // further encoding: generator entities (&lt;T&gt; in generic signatures)
        // round-trip unchanged, while tag-shaped free text riding in through an
        // [Obsolete("…")] message (interpolated verbatim by SyntaxWriter from
        // service-model values authored outside this repo) leaves as inert text
        // instead of live markup.
        public static string SyntaxMarkupToPlainText(string markup)
        {
            if (string.IsNullOrEmpty(markup))
                return string.Empty;

            var text = Regex.Replace(markup, @"<br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"</?span[^>]*>", string.Empty, RegexOptions.IgnoreCase);
            text = System.Net.WebUtility.HtmlDecode(text);
            // Trim: a trailing <br/> (attribute-only syntax) or stray whitespace would
            // otherwise render as a visible blank line inside <pre>.
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Trim();
        }

        public static string GetCrossReferenceTypeName(XElement element)
        {
            var node = element.Attribute("cref");
            if (node == null)
                return null;

            var typeName = node.Value;
            return typeName;
        }

        public void WriteCrossReferenceTagReplacement(TextWriter writer, string typeName)
        {
            var replacement = CreateCrossReferenceTagReplacement(TypeProvider, typeName, this._version);
            writer.Write(replacement);
        }

        public static string CreateCrossReferenceTagReplacement(AbstractTypeProvider typeProvider, string crefTypeName, FrameworkVersion version)
        {
            const string amazonNamespaceRoot = "Amazon.";

            var target = string.Empty;
            string url = null;

            string typeName;
            if (crefTypeName.Length > 2 && crefTypeName[1] == ':') // cref M:, T:, P:, F: indicators
                typeName = crefTypeName.Substring(2);
            else
                typeName = crefTypeName;

            var typeWrapper = typeProvider.GetType(typeName);

            if (typeWrapper != null)
                url = string.Format("./{0}", FilenameGenerator.GenerateFilename(typeWrapper));
            else if (typeName.StartsWith("system.", StringComparison.OrdinalIgnoreCase))
            {
                // ToLowerInvariant: this is a URL path segment, so it must not vary
                // with the build host's locale (tr/az lowercase 'I' to dotless 'ı').
                url = string.Format(NDocUtilities.MSDN_TYPE_URL_PATTERN, typeName.ToLowerInvariant());
                target = " target=\"_new\"";
            }

            // If we couldn't generate a url to use with an anchor tag, make the typename italic+bold so
            // that it at least stands out. Encode it: an unresolved cref is doc-comment
            // text and can carry arbitrary markup.
            if (url == null)
                return string.Format("<i><b>{0}</b></i>", System.Net.WebUtility.HtmlEncode(typeName));

            // If the type is one of ours, strip the namespace from the display text to condense things
            // a little
            if (typeName.StartsWith(amazonNamespaceRoot, StringComparison.Ordinal))
            {
                var lastPeriodIndex = typeName.LastIndexOf('.');
                typeName = typeName.Substring(lastPeriodIndex + 1);
            }

            // Both the URL and the display name derive from doc-comment cref text on
            // the MSDN branch (an unresolvable name that merely starts with "system.").
            // Encode them: a raw quote would close the href attribute and inject fresh
            // ones, and raw markup in the name would become live elements. The resolved
            // branch's generator-built filename is unaffected by the encoding.
            return string.Format("<a href=\"{0}\"{2} rel=\"noopener noreferrer\">{1}</a>",
                System.Net.WebUtility.HtmlEncode(url),
                System.Net.WebUtility.HtmlEncode(typeName),
                target);
        }
    }

    /// <summary>
    /// Used to build ordered collections of table headers used in the various
    /// sections of a page. Custom css (as id or class) can optionally be applied.
    /// </summary>
    public class TableColumnHeader
    {
        /// <summary>
        /// The colum header displayed to the user. If not specified the column
        /// will have a blank title.
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// if set, applied as an 'id' attribute on the resulting td element
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// If set, applied as a 'class' attribute on the resulting td element
        /// </summary>
        public string CssClass { get; set; }
    }
}
