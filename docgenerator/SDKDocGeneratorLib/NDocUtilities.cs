using System;
using System.CodeDom;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.XPath;
using System.Xml.Linq;

using SDKDocGenerator.Writers;
using System.Xml;
using System.Diagnostics;
using System.Text.RegularExpressions;

using System.Web;

namespace SDKDocGenerator
{
    public static class NDocUtilities
    {
        public const string MSDN_TYPE_URL_PATTERN = "https://msdn.microsoft.com/en-us/library/{0}.aspx";
        public const string DOC_SAMPLES_SUBFOLDER = "AWSSDKDocSamples";

        public const string crossReferenceOpeningTagText = "<see"; // <see> and <seealso> tags
        public const string crossReferenceClosingTagText = "/>";

        public const string crefAttributeName = "cref";
        public const string hrefAttributeName = "href";
        public const string nameAttributeName = "name";
        public const string targetAttributeName = "target";
        public const string pathAttributeName = "path";

        // inner attribute of a cross reference tag we're interested in
        public static readonly string innerCrefAttributeText = crefAttributeName + "=\"";
        public static readonly string innerHrefAttributeText = hrefAttributeName + "=\"";

        private static readonly Dictionary<string, string> NdocToHtmlElementMapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // summary/para are block containers (rendered as <div>, not <p>) because
            // their content may include block-level <note>/<important> noteblocks —
            // a <div> inside a <p> is invalid HTML and browsers auto-close the <p>,
            // leaving stray empty paragraphs and spurious vertical gaps. The
            // "doc-para" class restores paragraph-like spacing (see aws-docs.css).
            { "summary", "div" },
            { "para", "div" },
            { "see", "a" },
            { "paramref", "code" },
            { "important", "div" },
            { "note", "div" },
            { "item", "li" },
            { "term", "span" },
            { "description", "span" }
        };

        private static readonly Dictionary<string, string> NdocToHtmlClassMapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "summary", "doc-para" },
            { "para", "doc-para" },
            { "important", "noteblock noteblock-warning" },
            { "note", "noteblock" }
        };

        // HTML void elements — the only elements legally emitted without a separate
        // end tag. Everything else must get a full end tag: an HTML parser reads the
        // XML self-closing form (<div/>) as an unclosed start tag that swallows the
        // following siblings.
        private static readonly HashSet<string> VoidHtmlElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "area", "base", "br", "col", "embed", "hr", "img", "input",
            "link", "meta", "param", "source", "track", "wbr"
        };

        // Elements that would execute or reshape the page if they flowed from a doc
        // comment into the generated HTML (the doc XML permits arbitrary element
        // names). They are demoted to <span>, so their text stays visible but inert.
        private static readonly HashSet<string> DisallowedDocElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "script", "style", "iframe", "object", "embed", "form",
            "base", "link", "meta", "template", "svg", "math"
        };

        // The only attributes a doc comment may carry onto the page (names arrive
        // folded to lowercase); anything else — event handlers, hx-*/data-* hooks,
        // id (DOM clobbering), style/tabindex (page reshaping), target/rel/ping
        // (the generator writes its own on absolute links; an author copy could
        // undo the noopener hardening, and a duplicate write aborts generation) —
        // is dropped. href/src values are additionally scheme-checked, and class
        // is restricted to GeneratorDocClasses in the copy loop.
        private static readonly HashSet<string> AllowedDocAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "href", "cref", "name", "src", "alt", "title", "type", "width", "height", "class"
        };

        // The only class values that may survive: classes the generator's own
        // pre-pass stamps into the doc XML (highlight.js keys on language-csharp).
        // Author classes would reach the shipped stylesheet's behavioral selectors
        // (class="search-modal" is a full-viewport overlay), so they are dropped.
        private static readonly HashSet<string> GeneratorDocClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "language-csharp",
            "csharp-code-sample-title"
        };

        // Reduces a doc-comment URL to the form a browser's URL parser will act
        // on: whitespace/control characters are ignored for scheme detection
        // ("jav\tascript:" executes) and backslashes are normalized to slashes
        // ("https:\\host" resolves like "https://host").
        private static string NormalizeDocUrlProbe(string url)
        {
            return new string(url.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray())
                .Replace('\\', '/');
        }

        // Extracts the scheme from a normalized probe, or null when the URL is
        // relative (no colon, or the ':' sits inside the path/query/fragment).
        private static string GetDocUrlScheme(string probe)
        {
            var colon = probe.IndexOf(':');
            if (colon < 0)
                return null;
            var delimiter = probe.IndexOfAny(new[] { '/', '?', '#' });
            if (delimiter >= 0 && delimiter < colon)
                return null; // the ':' sits inside the path/query — still relative
            return probe.Substring(0, colon);
        }

        // URL attribute values from doc comments may only be relative, fragment,
        // http(s) or mailto — never javascript:/data:/vbscript: and friends. A
        // scheme-relative "//host" (which resolves to an arbitrary absolute
        // origin) is rejected in all its spellings ("\\host", "/\host").
        internal static bool IsSafeDocUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return false;
            var probe = NormalizeDocUrlProbe(url);
            if (probe.StartsWith("//", StringComparison.Ordinal))
                return false;
            var scheme = GetDocUrlScheme(probe);
            if (scheme == null)
                return true;
            return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                || scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase);
        }

        // True when a browser will resolve the URL to an absolute http(s) origin.
        // This must judge the SAME normalized form IsSafeDocUrl accepts on —
        // "https:\\host", "http:host" and a leading-space URL all resolve
        // cross-origin, so testing the raw text for a literal "http://" prefix
        // would let them through the filter yet skip the target/rel hardening.
        internal static bool IsAbsoluteHttpDocUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return false;
            var scheme = GetDocUrlScheme(NormalizeDocUrlProbe(url));
            return scheme != null
                && (scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                    || scheme.Equals("https", StringComparison.OrdinalIgnoreCase));
        }

        #region manage ndoc instances
        // The reason we cache the doc data on the side instead of directly referencing doc instances from
        // the type information is becasue we are loading the assemblies for reflection in a separate app domain.

        private static IDictionary<string, IDictionary<string, XElement>> _ndocCache = new Dictionary<string, IDictionary<string, XElement>>();

        public static string GenerateDocId(string serviceName, string platform)
        {
            // platform can be null; in which case we just use an empty string to construct the id.
            return string.Format("{0}:{1}", serviceName, platform == null ? "" : platform);
        }

        public static void LoadDocumentation(string assemblyName, string serviceName, string platform, GeneratorOptions options)
        {
            var ndocFilename = assemblyName + ".xml";
            var platformSpecificNdocFile = Path.Combine(options.SDKAssembliesRoot, platform, ndocFilename);
            if (File.Exists(platformSpecificNdocFile))
            {
                var docId = GenerateDocId(serviceName, platform);
                // De-duplication guard: LoadDocumentation may be called multiple times for
                // the same (service, platform) pair (e.g., during Generate() and again during
                // GenerateExclusivePagesFromMap()). Each docId is unique per service+platform,
                // so this only prevents redundant re-parsing of the same XML file.
                if (!_ndocCache.ContainsKey(docId))
                {
                    _ndocCache.Add(docId, CreateNDocTable(platformSpecificNdocFile, serviceName, options));
                }
            }
        }

        public static void UnloadDocumentation(string serviceName, string platform)
        {
            var docId = GenerateDocId(serviceName, platform);
            _ndocCache.Remove(docId);
        }

        public static IDictionary<string, XElement> GetDocumentationInstance(string serviceName, string platform)
        {
            return GetDocumentationInstance(GenerateDocId(serviceName, platform));
        }

        public static IDictionary<string, XElement> GetDocumentationInstance(string docId)
        {
            IDictionary<string, XElement> doc = null;
            if (_ndocCache.TryGetValue(docId, out doc))
            {
                return doc;
            }
            return null;
        }
        
        private static IDictionary<string, XElement> CreateNDocTable(string filePath, string serviceName, GeneratorOptions options)
        {
            var dict = new Dictionary<string, XElement>();
            var document = LoadAssemblyDocumentationWithSamples(filePath, options.CodeSamplesRootFolder, serviceName);
            PreprocessCodeBlocksToPreTags(options, document);

            foreach (var element in document.XPathSelectElements("//members/member"))
            {
                var xattribute = element.Attributes().FirstOrDefault(x => x.Name.LocalName == "name");
                if (xattribute == null)
                    continue;

                dict[xattribute.Value] = element;
            }

            return dict;
        }
        #endregion


        public static XElement FindDocumentation(AbstractWrapper wrapper, AbstractTypeProvider typeProvider)
        {
            var ndoc = GetDocumentationInstance(wrapper.DocId);
            return FindDocumentation(ndoc, wrapper, typeProvider);
        }

        public static XElement FindDocumentation(IDictionary<string, XElement> ndoc, AbstractWrapper wrapper, AbstractTypeProvider typeProvider)
        {
            if (ndoc == null)
                return null;

            if (wrapper is TypeWrapper)
                return FindDocumentation(ndoc, (TypeWrapper)wrapper, typeProvider);
            if (wrapper is PropertyInfoWrapper)
                return FindDocumentation(ndoc, (PropertyInfoWrapper)wrapper, typeProvider);
            if (wrapper is MethodInfoWrapper)
                return FindDocumentation(ndoc, (MethodInfoWrapper)wrapper, typeProvider);
            if (wrapper is ConstructorInfoWrapper)
                return FindDocumentation(ndoc, (ConstructorInfoWrapper)wrapper);
            if (wrapper is FieldInfoWrapper)
                return FindDocumentation(ndoc, (FieldInfoWrapper)wrapper);

            return null;
        }

        public static XElement FindDocumentation(IDictionary<string, XElement> ndoc, FieldInfoWrapper info)
        {
            var signature = string.Format("F:{0}.{1}", info.DeclaringType.FullName, info.Name);
            XElement element;
            if (!ndoc.TryGetValue(signature, out element))
                return null;

            return element;
        }

        public static XElement FindFieldDocumentation(TypeWrapper type, string fieldName)
        {
            var ndoc = GetDocumentationInstance(type.DocId);
            return FindFieldDocumentation(ndoc, type, fieldName);
        }

        public static XElement FindFieldDocumentation(IDictionary<string, XElement> ndoc, TypeWrapper type, string fieldName)
        {
            var signature = string.Format("F:{0}.{1}", type.FullName, fieldName);
            XElement element;
            if (!ndoc.TryGetValue(signature, out element))
                return null;

            return element;
        }

        public static XElement FindDocumentation(IDictionary<string, XElement> ndoc, TypeWrapper type, AbstractTypeProvider typeProvider)
        {
            var signature = "T:" + type.FullName;
            XElement element;
            if (!ndoc.TryGetValue(signature, out element))
                return null;

            // Follow a plain <inheritdoc/> by searching the base type and interfaces.
            var inheritdocElement = element.XPathSelectElement("inheritdoc");
            if (inheritdocElement == null)
                return element;

            if (inheritdocElement.Attribute(crefAttributeName) != null)
            {
                return FindCrefDocumentation(ndoc, typeProvider, inheritdocElement);
            }

            if (type.BaseType.FullName != "System.Object") // we never expect to inherit class-level docs from here
            {
                var baseTypeDocs = FindDocumentation(ndoc, type.BaseType, typeProvider);

                if (baseTypeDocs != null)
                {
                    return ExtractPathDocumentation(inheritdocElement, baseTypeDocs);
                }
            }

            foreach (var baseInterface in type.GetInterfaces())
            {
                var interfaceDocs = FindDocumentation(ndoc, baseInterface, typeProvider);

                if (interfaceDocs != null)
                {
                    return ExtractPathDocumentation(inheritdocElement, interfaceDocs);
                }
            }

            return element;
        }

        public static XElement FindDocumentation(IDictionary<string, XElement> ndoc, PropertyInfoWrapper info, AbstractTypeProvider typeProvider)
        {
            var type = info.DeclaringType;
            var signature = string.Format("P:{0}.{1}", type.FullName, info.Name);
            XElement element;
            if (!ndoc.TryGetValue(signature, out element))
                return null;

            // Follow a plain <inheritdoc/> by searching the base type and interfaces.
            var inheritdocElement = element.XPathSelectElement("inheritdoc");
            if (inheritdocElement == null)
                return element;

            if (inheritdocElement.Attribute(crefAttributeName) != null)
            {
                return FindCrefDocumentation(ndoc, typeProvider, inheritdocElement);
            }

            var baseTypeMatchingProperties = info.DeclaringType.BaseType.GetProperties().Where(property => property.Name.Equals(info.Name, StringComparison.OrdinalIgnoreCase));

            if (baseTypeMatchingProperties.Count() == 1)
                return ExtractPathDocumentation(inheritdocElement, FindDocumentation(ndoc, baseTypeMatchingProperties.First(), typeProvider));

            foreach (var baseInterface in info.DeclaringType.GetInterfaces())
            {
                var interfaceMatchingProperties = baseInterface.GetProperties().Where(property => property.Name.Equals(info.Name, StringComparison.OrdinalIgnoreCase));

                if (interfaceMatchingProperties.Count() == 1)
                    return ExtractPathDocumentation(inheritdocElement, FindDocumentation(ndoc, interfaceMatchingProperties.First(), typeProvider));
            }

            return element;
        }

        public static XElement FindDocumentation(IDictionary<string, XElement> ndoc, EventInfoWrapper info)
        {
            var type = info.DeclaringType;
            var signature = string.Format("E:{0}.{1}", type.FullName, info.Name);
            XElement element;
            if (!ndoc.TryGetValue(signature, out element))
                return null;

            return element;
        }

        public static string DetermineNDocNameLookupSignature(MethodInfo info, string docId)
        {
            return DetermineNDocNameLookupSignature(new MethodInfoWrapper(info, docId));
        }

        public static string DetermineNDocNameLookupSignature(MethodInfoWrapper info)
        {
            var type = info.DeclaringType;
            var fullName = type.FullName ?? type.Namespace + "." + type.Name;
            var typeGenericParameters = type.GetGenericArguments();
            var parameters = new StringBuilder();
            foreach (var param in info.GetParameters())
            {
                if (parameters.Length > 0)
                    parameters.Append(",");
                DetermineParameterName(param.ParameterType, parameters, typeGenericParameters);
                if (param.IsOut)
                {
                    parameters.Append("@");
                }
            }

            var genericTag = "";
            if (info.IsGenericMethod)
            {
                genericTag = "``" + info.GetGenericArguments().Length;
            }

            var signature = parameters.Length > 0
                ? string.Format("M:{0}.{1}{2}({3})", fullName, info.Name, genericTag, parameters)
                : string.Format("M:{0}.{1}{2}", fullName, info.Name, genericTag);

            return signature;
        }

        #region NDoc Signature Generation Helpers

        // These methods generate NDoc-style member signatures used for:
        // 1. Documentation lookup (original use case)
        // 2. Platform availability mapping (added 2026 for unified platform map feature)
        //
        // The signature format is standardized across the .NET ecosystem and must not change
        // without considering backwards compatibility with existing documentation XML files.

        /// <summary>
        /// Generates NDoc signature for a type.
        /// Format: T:Amazon.S3.Model.GetObjectRequest
        /// </summary>
        public static string DetermineTypeSignature(TypeWrapper type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            return "T:" + type.FullName;
        }

        /// <summary>
        /// Generates NDoc signature for a property.
        /// Format: P:Amazon.Runtime.ClientConfig.ReadWriteTimeout
        /// Note: Uses DeclaringType for inherited properties.
        /// </summary>
        public static string DeterminePropertySignature(PropertyInfoWrapper property)
        {
            if (property == null)
                throw new ArgumentNullException(nameof(property));
            return string.Format("P:{0}.{1}", property.DeclaringType.FullName, property.Name);
        }

        /// <summary>
        /// Generates NDoc signature for a field.
        /// Format: F:Amazon.S3.Model.Region.USEast1
        /// </summary>
        public static string DetermineFieldSignature(FieldInfoWrapper field)
        {
            if (field == null)
                throw new ArgumentNullException(nameof(field));
            return string.Format("F:{0}.{1}", field.DeclaringType.FullName, field.Name);
        }

        /// <summary>
        /// Generates NDoc signature for an event.
        /// Format: E:Amazon.S3.Transfer.TransferUtility.UploadProgressEvent
        /// </summary>
        public static string DetermineEventSignature(EventInfoWrapper eventInfo)
        {
            if (eventInfo == null)
                throw new ArgumentNullException(nameof(eventInfo));
            return string.Format("E:{0}.{1}", eventInfo.DeclaringType.FullName, eventInfo.Name);
        }

        /// <summary>
        /// Generates NDoc signature for a constructor.
        /// Format: M:Amazon.S3.AmazonS3Client.#ctor(System.String,System.String)
        /// </summary>
        public static string DetermineConstructorSignature(ConstructorInfoWrapper constructor)
        {
            if (constructor == null)
                throw new ArgumentNullException(nameof(constructor));

            var type = constructor.DeclaringType;
            var parameters = new StringBuilder();
            var typeGenericParameters = type.GetGenericArguments();
            foreach (var param in constructor.GetParameters())
            {
                if (parameters.Length > 0)
                    parameters.Append(",");
                DetermineParameterName(param.ParameterType, parameters, typeGenericParameters);
                if (param.IsOut)
                {
                    parameters.Append("@");
                }
            }

            var formattedParameters = parameters.Length > 0
                ? string.Format("({0})", parameters)
                : parameters.ToString();

            return string.Format("M:{0}.#ctor{1}", type.FullName, formattedParameters);
        }

        /// <summary>
        /// Helper to determine parameter type name for NDoc signatures.
        /// Handles generic parameters, generic types, arrays, and regular types.
        /// </summary>
        public static void DetermineParameterName(TypeWrapper parameterTypeInfo, StringBuilder parameters, IList<TypeWrapper> typeGenericParameters)
        {
            if (parameterTypeInfo.IsGenericParameter)
            {
                var typeGenericParameterIndex = typeGenericParameters.IndexOf(parameterTypeInfo);
                var isClassGenericParameter = typeGenericParameterIndex >= 0;

                if (isClassGenericParameter)
                    parameters.AppendFormat("`{0}", typeGenericParameterIndex);
                else
                    parameters.AppendFormat("``{0}", 0);
            }
            else if (parameterTypeInfo.IsGenericType)
            {
                parameters
                    .Append(parameterTypeInfo.GenericTypeName)
                    .Append("{");
                IList<TypeWrapper> args = parameterTypeInfo.GenericTypeArguments();

                for (var i = 0; i < args.Count; i++)
                {
                    if (i != 0)
                    {
                        parameters.Append(",");
                    }
                    DetermineParameterName(args[i], parameters, typeGenericParameters);
                }
                parameters.Append("}");
            }
            else if (parameterTypeInfo.IsArray)
            {
                // Handle array parameters
                var elementType = parameterTypeInfo.GetElementType();
                DetermineParameterName(elementType, parameters, typeGenericParameters);
                parameters.Append("[]");
            }
            else
            {
                parameters.Append(parameterTypeInfo.FullName);
            }
        }

        #endregion

        public static XElement FindDocumentation(IDictionary<string, XElement> ndoc, MethodInfoWrapper info, AbstractTypeProvider typeProvider)
        {
            var signature = DetermineNDocNameLookupSignature(info);

            XElement element;
            if (!ndoc.TryGetValue(signature, out element))
                return null;

            // Follow a plain < inheritdoc /> by searching the base type and interfaces.
            var inheritdocElement = element.XPathSelectElement("inheritdoc");
            if (inheritdocElement == null)
                return element;

            if (inheritdocElement.Attribute(crefAttributeName) != null)
            {
                return FindCrefDocumentation(ndoc, typeProvider, inheritdocElement);
            }

            var baseTypeMatchingMethods = info.DeclaringType.BaseType.GetMethodsToDocument().Where(method => method.FullName.Equals(info.FullName, StringComparison.OrdinalIgnoreCase));
                
            if (baseTypeMatchingMethods.Count() == 1)
                return ExtractPathDocumentation(inheritdocElement, FindDocumentation(ndoc, baseTypeMatchingMethods.First(), typeProvider));

            foreach (var baseInterface in info.DeclaringType.GetInterfaces())
            {
                var interfaceMatchingMethods = baseInterface.GetMethodsToDocument().Where(method => method.FullName.Equals(info.FullName, StringComparison.OrdinalIgnoreCase));

                if (interfaceMatchingMethods.Count() == 1)
                    return ExtractPathDocumentation(inheritdocElement, FindDocumentation(ndoc, interfaceMatchingMethods.First(), typeProvider));
            }

            return element;
        }                

        public static XElement FindDocumentation(IDictionary<string, XElement> ndoc, ConstructorInfoWrapper info)
        {
            var signature = DetermineConstructorSignature(info);

            XElement element;
            if (!ndoc.TryGetValue(signature, out element))
                return null;

            return element;
        }

        public static string FindParameterDocumentation(XElement ndoc, string name)
        {
            if (ndoc == null)
                return string.Empty;

            var node = ndoc.XPathSelectElement(string.Format("./param[@name = '{0}']", name));
            if (node == null)
                return string.Empty;

            return node.Value;
        }

        public static string FindReturnDocumentation(XElement ndoc)
        {
            if (ndoc == null)
                return string.Empty;

            var node = ndoc.XPathSelectElement("returns");
            if (node == null)
                return string.Empty;

            return node.Value;
        }

        /// <summary>
        /// Finds the Async version of the specified non async method info.
        /// </summary>
        /// <param name="ndoc"></param>
        /// <param name="info"></param>
        /// <returns></returns>
        public static XElement FindDocumentationAsync(IDictionary<string, XElement> ndoc, MethodInfoWrapper info, AbstractTypeProvider typeProvider)
        {
            if (ndoc == null)
                return null;
            var type = info.DeclaringType;
            if (type.FullName == null)
                return null;
            var parameters = new StringBuilder();

            foreach (var param in info.GetParameters())
            {
                if (parameters.Length > 0)
                    parameters.Append(",");

                if (param.ParameterType.IsGenericType)
                {
                    parameters
                        .Append(param.ParameterType.GenericTypeName)
                        .Append("{")
                        .Append(string.Join(",", param.ParameterType.GenericTypeArguments().Select(a => a.FullName)))
                        .Append("}");
                }
                else
                {
                    parameters.Append(param.ParameterType.FullName);
                    if (param.IsOut)
                        parameters.Append("@");
                }
            }

            if (parameters.Length > 0)
                parameters.Append(",");

            // Async methods have this additional parameter
            parameters.Append("System.Threading.CancellationToken");

            var signature = parameters.Length > 0
                ? string.Format("M:{0}.{1}({2})", type.FullName, info.Name + "Async", parameters)
                : string.Format("M:{0}.{1}", type.FullName, info.Name + "Async");

            XElement element;
            if (!ndoc.TryGetValue(signature, out element))
                return null;

            // Follow a plain < inheritdoc /> by searching the base type and interfaces.
            var inheritdocElement = element.XPathSelectElement("inheritdoc");
            if (inheritdocElement == null)
                return element;

            if (inheritdocElement.Attribute(crefAttributeName) != null)
            {
                return FindCrefDocumentation(ndoc, typeProvider, inheritdocElement);
            }

            var baseTypeMatchingMethods = info.DeclaringType.BaseType.GetMethodsToDocument().Where(method => method.FullName.Equals(info.FullName, StringComparison.OrdinalIgnoreCase));

            if (baseTypeMatchingMethods.Count() == 1)
                return ExtractPathDocumentation(inheritdocElement, FindDocumentation(ndoc, baseTypeMatchingMethods.First(), typeProvider));

            foreach (var baseInterface in info.DeclaringType.GetInterfaces())
            {
                var interfaceMatchingMethods = baseInterface.GetMethodsToDocument().Where(method => method.FullName.Equals(info.FullName, StringComparison.OrdinalIgnoreCase));

                if (interfaceMatchingMethods.Count() == 1)
                    return ExtractPathDocumentation(inheritdocElement, FindDocumentation(ndoc, interfaceMatchingMethods.First(), typeProvider));
            }

            return element;
        }

        public static string TransformDocumentationToHTML(XElement element, string rootNodeName, AbstractTypeProvider typeProvider, FrameworkVersion version)
        {
            if (element == null)
                return string.Empty;

            var rootNode = element.XPathSelectElement(rootNodeName);
            if (rootNode == null)       return string.Empty;
            
            if (rootNodeName.Equals("seealso", StringComparison.OrdinalIgnoreCase))
                return SeeAlsoElementToHTML(rootNode, typeProvider, version);
            else
                return DocBlobToHTML(rootNode, typeProvider, version);
        }

        private static XElement FindCrefDocumentation(IDictionary<string, XElement> ndoc, AbstractTypeProvider typeProvider, XElement inheritdocElement)
        {
            var crefValue = inheritdocElement.Attribute(crefAttributeName)?.Value;
            if (crefValue == null)
                return inheritdocElement;

            var attributParts = crefValue.Split(':');
            if (attributParts.Length != 2)
                return inheritdocElement;

            var attributePart = attributParts[1];
            var targetType = typeProvider.GetType(attributePart);

            XElement targetDocs = null;

            if (targetType == null) // the cref attribute is pointing to a method or a property
            {
                if (attributePart.LastIndexOf('.') < 0)
                    return inheritdocElement;

                var typeName = attributePart.Substring(0, attributePart.LastIndexOf('.'));
                targetType = typeProvider.GetType(typeName);

                if (targetType == null)
                    return inheritdocElement;

                var typeMemberName = attributePart.Substring(attributePart.LastIndexOf('.') + 1);

                var matchingMethods = targetType.GetMethodsToDocument().Where(method => method.FullName.Equals(attributePart, StringComparison.OrdinalIgnoreCase));
                var matchingProperties = targetType.GetProperties().Where(property => property.Name.Equals(typeMemberName, StringComparison.OrdinalIgnoreCase));

                if (matchingMethods.Count() == 1)
                    targetDocs = FindDocumentation(ndoc, matchingMethods.First(), typeProvider);
                else if (matchingProperties.Count() == 1)
                    targetDocs = FindDocumentation(ndoc, matchingProperties.First(), typeProvider);
                else 
                    return inheritdocElement;
            }
            else
            {
                targetDocs = FindDocumentation(ndoc, targetType, typeProvider);
            }

            if (targetDocs != null)
            {
                return ExtractPathDocumentation(inheritdocElement, targetDocs);
            }

            return inheritdocElement;
        }

        private static XElement ExtractPathDocumentation(XElement inheritdocElement, XElement docElement)
        {
            if (inheritdocElement.Attribute(pathAttributeName) == null)
                return docElement;

            var pathValue = inheritdocElement.Attribute(pathAttributeName).Value;

            var targetPathDocs = docElement.XPathSelectElement(pathValue);

            if (targetPathDocs != null) 
            {
                var docElementCopy = new XElement(docElement);
                docElementCopy.RemoveNodes();

                // Wrap the targetPathDocs in summary tag to be picked up by the html transformer.
                docElementCopy.Add(new XElement("summary", targetPathDocs));

                return docElementCopy;
            }

            return docElement;
        }

        private static string SeeAlsoElementToHTML(XElement rootNode, AbstractTypeProvider typeProvider, FrameworkVersion version)
        {
            // The link label is doc-comment text: encode it (raw inner XML here would
            // carry element/attribute gadgets straight past the DocBlobToHTML
            // sanitization, which this path bypasses).
            var label = HttpUtility.HtmlEncode(rootNode.Value);
            string content = "";

            var href = rootNode.Attribute("href");
            if (href != null && IsSafeDocUrl(href.Value))
            {
                // Attribute-encode the doc-supplied href so a quote can't break out of
                // the attribute value; disallowed schemes drop the link entirely.
                content += string.Format(@"<div><a href=""{0}"" target=""_parent"" rel=""noopener noreferrer"">{1}</a></div>",
                    HttpUtility.HtmlAttributeEncode(href.Value), label);
            }
            else if (href != null)
            {
                content += string.Format("<div>{0}</div>", label);
            }

            var cref = rootNode.Attribute(crefAttributeName);
            if (cref != null)
            {
                content += BaseWriter.CreateCrossReferenceTagReplacement(typeProvider, cref.Value, version);
            }

            return content;
        }

        private static string DocBlobToHTML(XElement rootNode, AbstractTypeProvider typeProvider, FrameworkVersion version)
        {
            using (var textWriter = new StringWriter())
            {
                var writerSettings = new XmlWriterSettings { OmitXmlDeclaration = true };
                using (var writer = XmlWriter.Create(textWriter, writerSettings))
                {
                    var reader = rootNode.CreateReader();
                    while (reader.Read())
                    {
                        switch (reader.NodeType)
                        {
                            case XmlNodeType.Element:
                                // handle self-closing element, like <a />
                                // this must be read before any other reading is done
                                var selfClosingElement = reader.IsEmptyElement;
                                // Judge (and emit) names the way an HTML parser will read
                                // them: XML treats <A HREF> and <a href> as distinct names,
                                // but HTML folds both to lowercase — routing on the raw
                                // spelling would let a case variant (HREF="javascript:…")
                                // slip past every check below while the browser still
                                // honors it.
                                var foldedLocalName = reader.LocalName.ToLowerInvariant();

                                // Read the attributes once, names folded to lowercase. On a
                                // fold collision (href + HREF) the first occurrence wins,
                                // matching HTML parsing — and writing both would abort
                                // generation (XmlWriter throws on duplicate names).
                                var elementAttributes = ReadFoldedDocAttributes(reader);

                                // element name substitution, if necessary
                                string elementName;
                                var isList = foldedLocalName == "list";
                                if (isList)
                                {
                                    // <list type="bullet"> → <ul>, <list type="number"> → <ol>
                                    var listType = GetDocAttribute(elementAttributes, "type");
                                    elementName = (listType == "number") ? "ol" : "ul";
                                }
                                else if (!NdocToHtmlElementMapping.TryGetValue(foldedLocalName, out elementName))
                                    elementName = foldedLocalName;

                                // A <script>/<iframe>/… arriving in a doc comment must not
                                // reach the page as an executable element.
                                if (DisallowedDocElements.Contains(elementName))
                                    elementName = "span";

                                // Resolve the cref (if any) exactly once; the <a>-vs-<span>
                                // decision and the attribute loop both consume this result.
                                // Skipped for <list>, whose attributes aren't copied.
                                var crefAttr = isList ? null : GetDocAttribute(elementAttributes, crefAttributeName);
                                TypeWrapper crefTargetType = null;
                                string crefTypeName = null;
                                string crefHref = null;
                                bool crefIsUnresolved = false;
                                if (crefAttr != null)
                                {
                                    if (crefAttr.StartsWith("!:", StringComparison.Ordinal))
                                    {
                                        // "!:…" is the compiler's could-not-resolve marker (exists
                                        // in shipping SDK sources). The payload is the author's
                                        // original text verbatim and may contain colons, so take it
                                        // whole; it degrades like any unresolved cref (encoded
                                        // text, nothing clickable) rather than abort the run.
                                        crefTypeName = crefAttr.Substring(2);
                                        crefTargetType = null;
                                        crefIsUnresolved = true;
                                    }
                                    else
                                    {
                                        // The prefix must be a single-letter doc-id kind (T/M/P/F/E/…).
                                        // Anything longer would also ride into the page verbatim when a
                                        // resolved cref is renamed to href — cref="javascript:Some.Type"
                                        // must be a build error, not a link scheme.
                                        var crefParts = crefAttr.Split(':');
                                        if (crefParts.Length != 2 || crefParts[0].Length != 1 || !char.IsLetter(crefParts[0][0]))
                                            throw new InvalidOperationException(string.Format(
                                                "Malformed cref \"{0}\" on <{1}> in a documentation comment: expected the compiler's \"X:Name\" form (e.g. \"T:Amazon.S3.AmazonS3Client\").",
                                                crefAttr, foldedLocalName));
                                        crefTypeName = crefParts[1];
                                        crefTargetType = typeProvider.GetType(crefTypeName);
                                        crefIsUnresolved = crefTargetType == null;
                                        if (!crefIsUnresolved)
                                            crefHref = crefTargetType.GetHelpPageUrl();
                                    }
                                }

                                // Anchors that would have no href look clickable but go
                                // nowhere. Render them as a plain <span> instead. This covers
                                // both <see cref="..."> whose target isn't in the generated doc
                                // set, and author-written <a> tags in the SDK XML that simply
                                // omit href.
                                if (elementName == "a")
                                {
                                    if (crefAttr != null)
                                    {
                                        if (crefIsUnresolved)
                                            elementName = "span";
                                        else if (selfClosingElement)
                                            // A self-closing resolved cref's content becomes a
                                            // generator-built reference anchor (CreateReferenceHtml)
                                            // — wrap it in a <span> rather than nesting an anchor
                                            // inside an anchor (invalid HTML that browsers
                                            // re-parent unpredictably).
                                            elementName = "span";
                                        else if (crefHref == null)
                                            // Resolved, but to a type with no page URL: keep the
                                            // label visible, render nothing clickable (an <a>
                                            // without href still looks like a link).
                                            elementName = "span";
                                    }
                                    else
                                    {
                                        // An href with a disallowed scheme (javascript: etc.) is
                                        // treated as absent: the attribute loop below drops it,
                                        // and the anchor demotes to <span> here.
                                        var hrefValue = GetDocAttribute(elementAttributes, hrefAttributeName);
                                        if ((string.IsNullOrEmpty(hrefValue) || !IsSafeDocUrl(hrefValue))
                                            && string.IsNullOrEmpty(GetDocAttribute(elementAttributes, nameAttributeName)))
                                        {
                                            // <a> with no (usable) href and no name — not a real
                                            // link and not a bookmark target, so render as <span>.
                                            // A bare <a name="foo"> in-page bookmark anchor is kept
                                            // as <a> (name only creates a fragment target on <a>).
                                            elementName = "span";
                                        }
                                    }
                                }

                                // some elements can't be empty, use this variable for that
                                string emptyElementContents = null;
                                // true only when the contents are generator-built HTML (a
                                // resolved cross-reference); everything else is doc-comment
                                // text and gets entity-encoded at the write below.
                                var emptyElementContentsAreHtml = false;

                                // start element
                                writer.WriteStartElement(elementName);

                                // Add CSS class if the original element has a class mapping
                                string cssClass;
                                if (NdocToHtmlClassMapping.TryGetValue(foldedLocalName, out cssClass))
                                {
                                    writer.WriteAttributeString("class", cssClass);
                                }

                                // copy over attributes (skip for list elements — type attribute already consumed)
                                if (elementAttributes != null && !isList)
                                {
                                    var isAbsoluteLink = false;
                                    // Names actually written on this element, so a transformed
                                    // name can't collide with a literal one (cref renames to
                                    // href — an author href beside it would otherwise be a
                                    // duplicate write, which aborts generation). First wins,
                                    // matching HTML parsing. Seeded with "class" when the
                                    // generator wrote its mapping class above.
                                    var writtenAttributeNames = new HashSet<string>(StringComparer.Ordinal);
                                    if (cssClass != null)
                                        writtenAttributeNames.Add("class");

                                    foreach (var docAttribute in elementAttributes)
                                    {
                                        var attributeName = docAttribute.Key;
                                        var attributeValue = docAttribute.Value;

                                        // Allowlist (see AllowedDocAttributes); src must
                                        // additionally carry a safe URL.
                                        if (!AllowedDocAttributes.Contains(attributeName))
                                            continue;
                                        if (string.Equals(attributeName, "src", StringComparison.OrdinalIgnoreCase)
                                            && !IsSafeDocUrl(attributeValue))
                                            continue;
                                        // class survives only when it is one the generator's own
                                        // pre-pass stamped into the doc XML (highlight.js keys on
                                        // language-csharp); author classes reach the stylesheet's
                                        // behavioral selectors and are dropped.
                                        if (string.Equals(attributeName, "class", StringComparison.Ordinal)
                                            && !GeneratorDocClasses.Contains(attributeValue))
                                            continue;

                                        // Attribute names were folded to lowercase up front, so
                                        // Ordinal compares here also catch HREF/Cref/… spellings.
                                        var isCref = string.Equals(attributeName, crefAttributeName, StringComparison.Ordinal);
                                        var isHref = string.Equals(attributeName, hrefAttributeName, StringComparison.Ordinal);
                                        var isName = string.Equals(attributeName, nameAttributeName, StringComparison.Ordinal);

                                        var writeAttribute = true;

                                        if (isCref)
                                        {
                                            // Reuse the single resolution done at element-open.
                                            if (crefIsUnresolved)
                                            {
                                                // Unresolved: the element was switched to <span> above.
                                                // Emit the bare type name as text and drop the cref
                                                // attribute entirely so nothing looks clickable.
                                                emptyElementContents = crefTypeName;
                                                writeAttribute = false;
                                            }
                                            else
                                            {
                                                emptyElementContents = crefTargetType.CreateReferenceHtml(fullTypeName: true);
                                                emptyElementContentsAreHtml = true;
                                                if (selfClosingElement || crefHref == null)
                                                {
                                                    // Self-closing: the <span> wrapper (see element-
                                                    // open) carries the generator-built anchor as
                                                    // content. No page URL: the element was demoted
                                                    // to <span>. Either way, a cref echoed as href
                                                    // would be a dead "T:…" link.
                                                    writeAttribute = false;
                                                }
                                                else
                                                {
                                                    // Labeled resolved cref: rewrite to the target's
                                                    // real page URL. The raw doc-id value
                                                    // ("T:Amazon.S3.AmazonS3Client") parses as a URI
                                                    // scheme, so echoing it made every such link dead.
                                                    attributeName = hrefAttributeName;
                                                    attributeValue = crefHref;
                                                    if (IsAbsoluteHttpDocUrl(crefHref))
                                                        isAbsoluteLink = true;
                                                }
                                            }
                                        }
                                        else if (isHref)
                                        {
                                            // Disallowed scheme: drop the attribute entirely (the
                                            // element was already demoted to <span> above).
                                            if (!IsSafeDocUrl(attributeValue))
                                                continue;

                                            // extract href value for emptyElementContents
                                            emptyElementContents = attributeValue;
                                            emptyElementContentsAreHtml = false; // doc-supplied text, even after a resolved cref

                                            if (IsAbsoluteHttpDocUrl(attributeValue))
                                            {
                                                isAbsoluteLink = true;
                                            }
                                        }
                                        else if (isName)
                                        {
                                            // <img name="…"> becomes an own property of `document`
                                            // that shadows prototype members (DOM clobbering —
                                            // name="getElementById" would break app.js). Drop it
                                            // there; on anchors name is just a fragment target.
                                            if (foldedLocalName == "img")
                                                continue;

                                            emptyElementContents = attributeValue;
                                            emptyElementContentsAreHtml = false; // doc-supplied text, even after a resolved cref

                                            if (elementName != "a")
                                                writeAttribute = false;
                                        }

                                        if (writeAttribute && writtenAttributeNames.Add(attributeName))
                                        {
                                            writer.WriteAttributeString(attributeName, attributeValue);
                                        }
                                    }

                                    if (elementName == "a" && isAbsoluteLink)
                                    {
                                        // rel=noopener severs the opener reference so the external
                                        // (doc-comment-supplied) page can't script this window.
                                        // Written unconditionally — author target/rel never pass
                                        // the allowlist, so these are the only writes.
                                        writer.WriteAttributeString(targetAttributeName, "_blank");
                                        writer.WriteAttributeString("rel", "noopener noreferrer");
                                    }
                                }

                                // if this is a self-closing element, close it
                                if (selfClosingElement)
                                {
                                    // Raw only for generator-built cross-reference HTML;
                                    // doc-comment-supplied values are text.
                                    if (!string.IsNullOrEmpty(emptyElementContents))
                                    {
                                        if (emptyElementContentsAreHtml)
                                            writer.WriteRaw(emptyElementContents);
                                        else
                                            writer.WriteString(emptyElementContents);
                                    }

                                    WriteElementEnd(writer, foldedLocalName);
                                }

                                break;
                            case XmlNodeType.EndElement:
                                WriteElementEnd(writer, reader.LocalName);
                                break;
                            case XmlNodeType.Text:
                            // CDATA is authorable in /// comments; its content is doc text.
                            case XmlNodeType.CDATA:
                                // Entity-encode: the XmlReader already decoded entities, so a
                                // raw write would turn "&lt;script&gt;" from a doc comment
                                // (authored outside this repo) into live markup. Encoded
                                // exactly once, here.
                                writer.WriteString(reader.Value);
                                break;
                            case XmlNodeType.Whitespace:
                            case XmlNodeType.SignificantWhitespace:
                                writer.WriteWhitespace(reader.Value);
                                break;
                            case XmlNodeType.Comment:
                            case XmlNodeType.ProcessingInstruction:
                                // Legal in doc XML, meaningless on the page — and letting
                                // them throw would abort the whole generation run.
                                break;
                            default:
                                throw new InvalidOperationException(string.Format(
                                    "Unexpected XML node type \"{0}\" in documentation comment.",
                                    reader.NodeType));
                        }
                    }
                }

                return textWriter.ToString();
            }
        }

        // Reads an element's attributes into document order with names folded to
        // lowercase (the way an HTML parser reads them); on a fold collision
        // (href + HREF) the first occurrence wins. Returns null when the element
        // has no attributes. Leaves the reader positioned back on the element.
        private static List<KeyValuePair<string, string>> ReadFoldedDocAttributes(XmlReader reader)
        {
            if (!reader.HasAttributes)
                return null;

            var attributes = new List<KeyValuePair<string, string>>(reader.AttributeCount);
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < reader.AttributeCount; i++)
            {
                reader.MoveToAttribute(i);
                var foldedName = reader.Name.ToLowerInvariant();
                // Namespace machinery and prefixed names (xmlns, xmlns:p, xml:lang,
                // p:href) never flow to the page: they are XML plumbing, not HTML
                // attributes — and passing one to WriteAttributeString would abort
                // the whole generation run (':' is invalid in a local name, and a
                // bare xmlns write throws a prefix-redefinition error).
                if (foldedName == "xmlns" || foldedName.IndexOf(':') >= 0)
                    continue;
                if (seenNames.Add(foldedName))
                    attributes.Add(new KeyValuePair<string, string>(foldedName, reader.Value));
            }
            reader.MoveToElement();

            return attributes;
        }

        // Case-insensitive-by-construction lookup over ReadFoldedDocAttributes
        // output (names are already folded; `name` must be passed lowercase).
        private static string GetDocAttribute(List<KeyValuePair<string, string>> attributes, string name)
        {
            if (attributes == null)
                return null;

            foreach (var attribute in attributes)
            {
                if (string.Equals(attribute.Key, name, StringComparison.Ordinal))
                    return attribute.Value;
            }

            return null;
        }

        // Closes the current element, choosing between a full end tag and the XML
        // self-closing form based on whether the (original) element maps to a void HTML
        // element. Non-void elements MUST get a full end tag so browsers don't mis-parse
        // an empty <div/>/<span/>/<li/> as an unclosed start tag. `originalLocalName` is
        // the NDoc source name; none of our remap targets are void, so its void-ness
        // equals the emitted element's void-ness.
        private static void WriteElementEnd(XmlWriter writer, string originalLocalName)
        {
            // Disallowed elements were demoted to <span> at the start tag, so even a
            // void original name (embed/link/meta/base) needs a full </span> end tag.
            if (VoidHtmlElements.Contains(originalLocalName) && !DisallowedDocElements.Contains(originalLocalName))
                writer.WriteEndElement();       // e.g. <br/>, <wbr/> — valid self-closing
            else
                writer.WriteFullEndElement();   // e.g. <div></div>, <span></span>
        }

        public static void PreprocessCodeBlocksToPreTags(GeneratorOptions options, XDocument doc)
        {
            var nodesToRemove = new List<XElement>();
            // Materialize the matches up front. We insert new <code class="language-csharp">
            // elements below, which also match "//code"; iterating the lazy XPath result
            // directly would re-find those and wrap them endlessly (OOM).
            var codeNodes = doc.XPathSelectElements("//code").ToList();
            foreach (var codeNode in codeNodes)
            {
                string processedCodeSample = null;
                var xattribute = codeNode.Attributes().FirstOrDefault(x => x.Name.LocalName == "source");
                if (xattribute != null)
                {
                    var sourceRelativePath = xattribute.Value;

                    xattribute = codeNode.Attributes().FirstOrDefault(x => x.Name.LocalName == "region");
                    if (xattribute == null)
                        continue;
                    var regionName = xattribute.Value;

                    var samplePath = FindSampleCodePath(options.CodeSamplesRootFolder, sourceRelativePath);
                    if (samplePath == null)
                    {
                        Console.Error.WriteLine("Error finding sample path for {0}", sourceRelativePath);
                        continue;
                    }

                    var content = File.ReadAllText(samplePath);

                    var startPos = content.IndexOf("#region " + regionName);
                    if (startPos == -1)
                    {
                        Console.Error.WriteLine("Error finding region for {0}", regionName);
                        continue;
                    }
                    startPos = content.IndexOf('\n', startPos);
                    var endPos = content.IndexOf("#endregion", startPos);

                    // Stored as plain text: DocBlobToHTML entity-encodes text nodes at
                    // emit, so pre-encoding here would double-encode the sample.
                    processedCodeSample = content.Substring(startPos, endPos - startPos);
                }
                else
                {
                    processedCodeSample = codeNode.Value;
                }

                if (processedCodeSample != null && processedCodeSample.IndexOf('\n') > -1)
                {

                    processedCodeSample = LeftJustifyCodeBlocks(processedCodeSample);
                    // Emit <pre><code class="language-csharp"> for highlight.js. The
                    // sample is stored as a plain text node; DocBlobToHTML encodes it
                    // exactly once when the page is written.
                    var codeElement = new XElement("code", processedCodeSample);
                    codeElement.SetAttributeValue("class", "language-csharp");
                    var preElement = new XElement("pre", codeElement);

                    codeNode.AddAfterSelf(preElement);
                    nodesToRemove.Add(codeNode);

                    string title = null;
                    xattribute = codeNode.Attributes().FirstOrDefault(x => x.Name.LocalName == "title");
                    if (xattribute != null)
                        title = xattribute.Value;

                    if (title != null)
                    {
                        var titleElement = new XElement("h4", title);
                        titleElement.SetAttributeValue("class", "csharp-code-sample-title");
                        preElement.AddBeforeSelf(titleElement);
                    }
                }
            }

            nodesToRemove.ForEach(x => x.Remove());
        }

        private static string FindSampleCodePath(string codeSampleRootDirectory, string relativePath)
        {
            if (string.IsNullOrEmpty(codeSampleRootDirectory))
                return null;

            var fullPath = Path.Combine(codeSampleRootDirectory, relativePath);
            return !File.Exists(fullPath) ? null : fullPath;
        }

        private static string LeftJustifyCodeBlocks(string codeBlock)
        {
            // Switch tabs to 4 spaces
            var block = new StringBuilder(codeBlock).Replace("\t", new string(' ', 4)).ToString();

            // Find the nearest indent location
            var nearestIndent = int.MaxValue;
            using (var reader = new StringReader(block))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int indent = FindFirstNoWhitePosition(line);
                    if (indent != -1 && indent < nearestIndent)
                        nearestIndent = indent;
                }
            }

            // Substring all lines with content to the indent location;
            var reformattedBuilder = new StringBuilder();
            using (var reader = new StringReader(block))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        reformattedBuilder.AppendLine(line);
                    else
                    {
                        var trimedLine = line.Substring(nearestIndent);
                        reformattedBuilder.AppendLine(trimedLine);
                    }
                }
            }

            return reformattedBuilder.ToString();
        }

        private static int FindFirstNoWhitePosition(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return -1;
            for (int space = 0; space < line.Length; space++)
            {
                if (!Char.IsWhiteSpace(line[space]))
                    return space;
            }
            return -1;
        }

        public static XDocument LoadAssemblyDocumentationWithSamples(string filePath, string samplesDir, string serviceName)
        {
            if (!string.IsNullOrEmpty(samplesDir))
            {
                var extraDocNodes = new List<XmlNode>();
                foreach (var pattern in new[] { ".extra.xml", ".GeneratedSamples.extra.xml" })
                {
                    var extraFile = Path.Combine(samplesDir, DOC_SAMPLES_SUBFOLDER, serviceName + pattern);
                    if (File.Exists(extraFile))
                    {
                        var extraDoc = new XmlDocument();
                        extraDoc.Load(extraFile);
                        foreach (XmlNode node in extraDoc.SelectNodes("docs/doc"))
                        {
                            extraDocNodes.Add(node);
                        }
                    }
                }

                if (extraDocNodes.Any())
                {
                    Trace.WriteLine(String.Format("Merging {0} code samples into {1}", serviceName, filePath));

                    var sdkDoc = new XmlDocument();
                    sdkDoc.Load(filePath);

                    var examplesMap = BuildExamplesMap(extraDocNodes);
                    ProcessExtraDoc(sdkDoc, examplesMap);

                    return XDocument.Load(new XmlNodeReader(sdkDoc), LoadOptions.PreserveWhitespace);
                }
            }

            return XDocument.Load(filePath, LoadOptions.PreserveWhitespace);
        }

        private static IDictionary<string, string> BuildExamplesMap(List<XmlNode> docNodes)
        {
            Trace.WriteLine(String.Format("Found {0} extra doc nodes", docNodes.Count), "verbose");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var docNode in docNodes)
            {
                var members = docNode.SelectNodes("members/member");
                foreach (XmlNode memberNode in members)
                {
                    var nameAttribute = memberNode.Attributes["name"];
                    if (null == nameAttribute)
                        throw new InvalidDataException("unable to retrieve 'name' attribute for member node.");

                    var memberSpec = nameAttribute.Value;
                    var exampleNode = docNode.SelectSingleNode("value/example");
                    var content = exampleNode.InnerXml;

                    if (map.ContainsKey(memberSpec))
                        map[memberSpec] += content;
                    else
                        map[memberSpec] = content;
                }
            }

            return map;
        }

        private static void ProcessExtraDoc(XmlDocument sdkDocument, IDictionary<string, string> examplesMap)
        {
            foreach (var memberSpec in examplesMap.Keys)
            {
                var docNode = sdkDocument.SelectSingleNode(string.Format("doc/members/member[@name='{0}']", memberSpec));
                if (null == docNode)
                {
                    Trace.WriteLine(String.Format("** member name not found, skipping: {0}", memberSpec), "verbose");
                    continue;
                }

                XmlNode sdkExampleNode = docNode.SelectSingleNode("example");
                if (null != sdkExampleNode)
                {
                    sdkExampleNode.InnerXml = examplesMap[memberSpec];
                }
                else
                {
                    string sdkXml = docNode.InnerXml;
                    sdkXml += String.Format("<example>{0}</example>", examplesMap[memberSpec]);
                    docNode.InnerXml = sdkXml;
                }

                Trace.WriteLine(string.Format("Successfully updated SDK XML for member {0}", memberSpec), "verbose");
            }
        }
    }
}
