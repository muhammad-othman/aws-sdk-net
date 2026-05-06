using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocFxGenerator.Configuration;

namespace DocFxGenerator.Preprocessing;

public class SamplesMerger
{
    private readonly GeneratorOptions _options;

    public SamplesMerger(GeneratorOptions options)
    {
        _options = options;
    }

    public void GenerateOverwriteFiles(IReadOnlyList<ServiceInfo> services)
    {
        var extraXmlFiles = FindExtraXmlFiles();
        if (extraXmlFiles.Count == 0)
        {
            if (_options.Verbose)
                Console.WriteLine("  No extra XML sample files found.");
            return;
        }

        var serviceNames = new HashSet<string>(services.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var processed = 0;

        foreach (var xmlFile in extraXmlFiles)
        {
            var entries = ParseExtraXml(xmlFile);
            if (entries.Count == 0) continue;

            var grouped = GroupByUid(entries);

            foreach (var (uid, examples) in grouped)
            {
                var serviceName = ExtractServiceFromUid(uid);
                if (serviceName == null || !serviceNames.Contains(serviceName))
                    continue;

                var overwriteFolder = Path.Combine(
                    Path.GetFullPath(_options.IntermediateFolder), "overwrite", serviceName);
                Directory.CreateDirectory(overwriteFolder);

                var safeFileName = SanitizeFileName(uid);
                var overwriteFile = Path.Combine(overwriteFolder, $"{safeFileName}.examples.md");

                WriteOverwriteFile(overwriteFile, uid, examples);
                processed++;
            }
        }

        if (_options.Verbose)
            Console.WriteLine($"  Generated {processed} example overwrite files.");
    }

    private List<string> FindExtraXmlFiles()
    {
        if (!Directory.Exists(_options.SamplesFolder))
            return new List<string>();

        return Directory.GetFiles(_options.SamplesFolder, "*.extra.xml", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(_options.SamplesFolder, "*.GeneratedSamples.extra.xml", SearchOption.TopDirectoryOnly))
            .Distinct()
            .ToList();
    }

    private List<ExampleEntry> ParseExtraXml(string xmlFile)
    {
        var entries = new List<ExampleEntry>();

        try
        {
            var doc = XDocument.Load(xmlFile);
            var docElements = doc.Descendants("doc");

            foreach (var docElement in docElements)
            {
                var members = docElement.Element("members")?.Elements("member")
                    .Select(m => m.Attribute("name")?.Value)
                    .Where(n => n != null)
                    .Cast<string>()
                    .ToList() ?? new List<string>();

                var valueElement = docElement.Element("value");
                var examples = valueElement?.Elements("example").ToList() ?? new List<XElement>();

                foreach (var example in examples)
                {
                    // Collect interleaved <para> and <code> elements in order
                    var children = example.Elements().ToList();
                    string currentDescription = "";

                    foreach (var child in children)
                    {
                        if (child.Name == "para")
                        {
                            currentDescription = child.Value?.Trim() ?? "";
                        }
                        else if (child.Name == "code")
                        {
                            var title = child.Attribute("title")?.Value ?? "";
                            var source = child.Attribute("source")?.Value;
                            var region = child.Attribute("region")?.Value;

                            if (source == null || region == null) continue;

                            var code = ExtractRegionCode(source, region, xmlFile);
                            if (code == null) continue;

                            entries.Add(new ExampleEntry
                            {
                                MemberNames = members,
                                Title = title,
                                Description = currentDescription,
                                Code = code
                            });
                            currentDescription = "";
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: Failed to parse {xmlFile}: {ex.Message}");
        }

        return entries;
    }

    private string? ExtractRegionCode(string relativePath, string regionName, string xmlFilePath)
    {
        // Resolve path relative to the samples folder
        var normalizedPath = relativePath.Replace('\\', '/');
        if (normalizedPath.StartsWith("./"))
            normalizedPath = normalizedPath[2..];

        // The source paths are relative to the AWSSDKDocSamples folder
        var fullPath = Path.Combine(_options.SamplesFolder, normalizedPath.Replace("AWSSDKDocSamples/", ""));
        if (!File.Exists(fullPath))
        {
            // Try the path as-is relative to samples folder
            fullPath = Path.Combine(Path.GetDirectoryName(xmlFilePath)!, normalizedPath);
            if (!File.Exists(fullPath))
                return null;
        }

        try
        {
            var lines = File.ReadAllLines(fullPath);
            var regionStart = -1;
            var regionEnd = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed == $"#region {regionName}")
                    regionStart = i + 1;
                else if (regionStart >= 0 && trimmed == "#endregion")
                {
                    regionEnd = i;
                    break;
                }
            }

            if (regionStart < 0 || regionEnd < 0)
                return null;

            var codeLines = lines[regionStart..regionEnd];

            // Remove leading/trailing empty lines
            while (codeLines.Length > 0 && string.IsNullOrWhiteSpace(codeLines[0]))
                codeLines = codeLines[1..];
            while (codeLines.Length > 0 && string.IsNullOrWhiteSpace(codeLines[^1]))
                codeLines = codeLines[..^1];

            if (codeLines.Length == 0)
                return null;

            // Left-justify (remove common leading whitespace)
            var minIndent = codeLines
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Length - l.TrimStart().Length)
                .DefaultIfEmpty(0)
                .Min();

            var result = string.Join("\n", codeLines.Select(l =>
                l.Length > minIndent ? l[minIndent..] : l.TrimStart()));

            return result;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, List<ExampleEntry>> GroupByUid(List<ExampleEntry> entries)
    {
        var grouped = new Dictionary<string, List<ExampleEntry>>();

        foreach (var entry in entries)
        {
            foreach (var memberName in entry.MemberNames)
            {
                var uid = ConvertMemberNameToUid(memberName);
                if (uid == null) continue;

                if (!grouped.TryGetValue(uid, out var list))
                {
                    list = new List<ExampleEntry>();
                    grouped[uid] = list;
                }
                list.Add(entry);
            }
        }

        return grouped;
    }

    private static string? ConvertMemberNameToUid(string memberName)
    {
        // Format: "M:Amazon.S3.AmazonS3Client.PutObject(Amazon.S3.Model.PutObjectRequest)"
        // or "T:Amazon.S3.Model.PutObjectRequest"
        if (memberName.Length < 3 || memberName[1] != ':')
            return null;

        return memberName[2..];
    }

    private static string? ExtractServiceFromUid(string uid)
    {
        // uid format: Amazon.S3.AmazonS3Client.PutObject(...)
        // or: Amazon.S3.Model.PutObjectRequest
        if (!uid.StartsWith("Amazon."))
            return null;

        var parts = uid.Split('.');
        if (parts.Length < 2)
            return null;

        return parts[1];
    }

    private static void WriteOverwriteFile(string filePath, string uid, List<ExampleEntry> examples)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"uid: {uid}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Examples");
        sb.AppendLine();

        foreach (var example in examples)
        {
            if (!string.IsNullOrEmpty(example.Description))
            {
                sb.AppendLine(example.Description);
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(example.Title))
            {
                sb.AppendLine($"### {example.Title}");
                sb.AppendLine();
            }

            sb.AppendLine("```csharp");
            sb.AppendLine(example.Code);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        File.WriteAllText(filePath, sb.ToString());
    }

    private static string SanitizeFileName(string uid)
    {
        var name = uid;
        var parenIdx = name.IndexOf('(');
        if (parenIdx > 0)
            name = name[..parenIdx];

        if (name.Length > 100)
            name = name[..100];

        return name.Replace('<', '_').Replace('>', '_').Replace('`', '_');
    }

    private class ExampleEntry
    {
        public required List<string> MemberNames { get; init; }
        public required string Title { get; init; }
        public required string Description { get; init; }
        public required string Code { get; init; }
    }
}
