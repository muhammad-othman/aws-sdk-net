using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocFxGenerator.Configuration;

namespace DocFxGenerator.PostProcessing;

public class RedirectRuleGenerator
{
    private const string ToolId = "DotNetSDKV4";
    private const string DocPathPrefix = "/sdkfornet/v4/apidocs/";
    private static readonly Regex UrlPattern = new(@".*/WebAPI/(.*)/(.*)");

    private readonly GeneratorOptions _options;
    private readonly SortedDictionary<string, SortedSet<string>> _rules = new();

    public RedirectRuleGenerator(GeneratorOptions options)
    {
        _options = options;
    }

    public void GenerateRedirects(IReadOnlyList<ServiceInfo> services)
    {
        foreach (var service in services)
        {
            var xmlPath = Path.Combine(_options.AssembliesRoot, _options.TargetFrameworks[0], $"AWSSDK.{service.Name}.xml");
            if (!File.Exists(xmlPath))
                continue;

            ProcessXmlDoc(xmlPath, service.Name);
        }

        WriteRedirectFile();
    }

    private void ProcessXmlDoc(string xmlPath, string serviceName)
    {
        try
        {
            var doc = XDocument.Load(xmlPath);
            var seeAlsoElements = doc.Descendants("seealso");

            foreach (var element in seeAlsoElements)
            {
                var href = element.Attribute("href")?.Value;
                if (string.IsNullOrEmpty(href))
                    continue;

                var match = UrlPattern.Match(href);
                if (!match.Success || match.Groups.Count != 3)
                    continue;

                var serviceId = match.Groups[1].Value;
                var shape = match.Groups[2].Value;

                var memberName = element.Parent?.Attribute("name")?.Value;
                if (memberName == null)
                    continue;

                var docPage = ConvertMemberToDocPage(memberName, serviceName);
                if (docPage == null)
                    continue;

                AddRule(serviceId, shape, docPage);
            }
        }
        catch (Exception ex)
        {
            if (_options.Verbose)
                Console.WriteLine($"  Warning: Failed to process redirects for {xmlPath}: {ex.Message}");
        }
    }

    private static string? ConvertMemberToDocPage(string memberName, string serviceName)
    {
        if (memberName.Length < 3 || memberName[1] != ':')
            return null;

        var uid = memberName[2..];
        var parenIdx = uid.IndexOf('(');
        var uidBase = parenIdx > 0 ? uid[..parenIdx] : uid;

        return $"{DocPathPrefix}api/{serviceName}/{uidBase}.html";
    }

    private void AddRule(string serviceId, string shape, string docPath)
    {
        if (!_rules.TryGetValue(serviceId, out var set))
        {
            set = new SortedSet<string>(StringComparer.Ordinal);
            _rules[serviceId] = set;
        }

        var rule = $@"RewriteRule ^/goto/{ToolId}/{serviceId}/{shape}$ ""{docPath}"" [L,R,NE]";
        set.Add(rule);
    }

    private void WriteRedirectFile()
    {
        var outputPath = Path.Combine(_options.OutputFolder, "package.redirects.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var totalRules = _rules.Values.Sum(s => s.Count);

        using var writer = new StreamWriter(outputPath);
        writer.WriteLine($@"RewriteCond ""%{{REQUEST_URI}}"" ""!^/goto/{ToolId}/""");
        writer.WriteLine($@"RewriteRule "".?"" ""-"" [S={totalRules}]");

        foreach (var (_, rules) in _rules)
        {
            foreach (var rule in rules)
            {
                writer.WriteLine(rule);
            }
        }

        Console.WriteLine($"  Generated {totalRules} redirect rules to package.redirects.conf");
    }
}
