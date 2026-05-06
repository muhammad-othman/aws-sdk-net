using System.Xml.Linq;
using DocFxGenerator.Configuration;
using YamlDotNet.RepresentationModel;

namespace DocFxGenerator.PostProcessing;

public class PlatformAvailabilityInjector
{
    private readonly GeneratorOptions _options;

    public PlatformAvailabilityInjector(GeneratorOptions options)
    {
        _options = options;
    }

    public void ProcessService(ServiceInfo service)
    {
        var apiFolder = Path.Combine(
            Path.GetFullPath(_options.IntermediateFolder), "api", service.Name);

        if (!Directory.Exists(apiFolder))
        {
            Console.WriteLine($"  Warning: API folder not found for {service.Name}, skipping post-processing");
            return;
        }

        var platformMembers = LoadPlatformMembers(service);
        var yamlFiles = Directory.GetFiles(apiFolder, "*.yml");

        foreach (var yamlFile in yamlFiles)
        {
            if (Path.GetFileName(yamlFile) == "toc.yml")
                continue;

            ProcessYamlFile(yamlFile, platformMembers);
        }
    }

    private void ProcessYamlFile(string yamlFile, Dictionary<string, HashSet<string>> platformMembers)
    {
        var content = File.ReadAllText(yamlFile);
        if (!content.StartsWith("### YamlMime:ManagedReference"))
            return;

        var yamlContent = content["### YamlMime:ManagedReference".Length..].TrimStart('\r', '\n');

        var yaml = new YamlStream();
        using (var reader = new StringReader(yamlContent))
        {
            yaml.Load(reader);
        }

        if (yaml.Documents.Count == 0)
            return;

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var itemsKey = new YamlScalarNode("items");
        if (!root.Children.TryGetValue(itemsKey, out var itemsNode))
            return;

        var items = (YamlSequenceNode)itemsNode;
        var allUids = CollectAllUids(items);
        var modified = false;

        // First pass: inject into concrete items
        var uidToVersionHtml = new Dictionary<string, string>();

        foreach (var item in items.Children.Cast<YamlMappingNode>())
        {
            var uidNode = TryGetScalar(item, "uid");
            var commentIdNode = TryGetScalar(item, "commentId");
            var typeNode = TryGetScalar(item, "type");

            if (uidNode == null)
                continue;

            var uid = uidNode.Value!;
            if (uid.EndsWith("*"))
                continue;

            var commentId = commentIdNode?.Value;
            var memberType = typeNode?.Value;

            // Platform availability
            if (commentId != null)
            {
                var platforms = GetPlatformAvailability(commentId, platformMembers);
                if (platforms.Count > 0)
                {
                    var versionHtml = FormatVersionInformation(platforms);
                    item.Children[new YamlScalarNode("versionInformation")] = new YamlScalarNode(versionHtml);
                    uidToVersionHtml[uid] = versionHtml;
                    modified = true;
                }
            }

            // Async method notes
            if (memberType == "Method")
            {
                var methodName = GetMethodNameFromUid(uid);
                if (methodName != null)
                {
                    if (methodName.EndsWith("Async"))
                    {
                        item.Children[new YamlScalarNode("isAsyncMethod")] = new YamlScalarNode("true");
                        modified = true;
                    }
                    else
                    {
                        var asyncUidBase = GetUidWithoutParams(uid).Replace($".{methodName}", $".{methodName}Async");
                        if (allUids.Any(u => u.StartsWith(asyncUidBase) && !u.EndsWith("*")))
                        {
                            item.Children[new YamlScalarNode("hasAsyncCounterpart")] = new YamlScalarNode("true");
                            item.Children[new YamlScalarNode("asyncCounterpartUid")] = new YamlScalarNode(asyncUidBase);
                            item.Children[new YamlScalarNode("asyncCounterpartHref")] = new YamlScalarNode($"{asyncUidBase}.html");
                            item.Children[new YamlScalarNode("asyncCounterpartName")] = new YamlScalarNode($"{methodName}Async");
                            modified = true;
                        }
                    }
                }
            }
        }

        // Second pass: inject into overload group items (uid ending with *)
        foreach (var item in items.Children.Cast<YamlMappingNode>())
        {
            var uidNode = TryGetScalar(item, "uid");
            if (uidNode == null) continue;

            var uid = uidNode.Value!;
            if (!uid.EndsWith("*")) continue;

            // Find the first concrete member matching this overload group
            var baseUid = uid[..^1];
            var matchingVersion = uidToVersionHtml
                .Where(kvp => kvp.Key.StartsWith(baseUid))
                .Select(kvp => kvp.Value)
                .FirstOrDefault();

            if (matchingVersion != null)
            {
                item.Children[new YamlScalarNode("versionInformation")] = new YamlScalarNode(matchingVersion);
                modified = true;
            }

            // Async notes for overload groups
            var methodName = baseUid.Contains('.') ? baseUid[(baseUid.LastIndexOf('.') + 1)..] : null;
            if (methodName != null)
            {
                if (methodName.EndsWith("Async"))
                {
                    item.Children[new YamlScalarNode("isAsyncMethod")] = new YamlScalarNode("true");
                    modified = true;
                }
                else
                {
                    var asyncOverloadUid = baseUid.Replace($".{methodName}", $".{methodName}Async") + "*";
                    if (allUids.Contains(asyncOverloadUid))
                    {
                        item.Children[new YamlScalarNode("hasAsyncCounterpart")] = new YamlScalarNode("true");
                        item.Children[new YamlScalarNode("asyncCounterpartUid")] = new YamlScalarNode(baseUid.Replace($".{methodName}", $".{methodName}Async"));
                        modified = true;
                    }
                }
            }
        }

        // Third pass: inject into references (overload groups live here)
        var referencesKey = new YamlScalarNode("references");
        var allRefUids = new HashSet<string>(StringComparer.Ordinal);
        if (root.Children.TryGetValue(referencesKey, out var refsNode) && refsNode is YamlSequenceNode refs)
        {
            // Collect reference UIDs first
            foreach (var refItem in refs.Children.Cast<YamlMappingNode>())
            {
                var u = TryGetScalar(refItem, "uid");
                if (u?.Value != null) allRefUids.Add(u.Value);
            }

            foreach (var refItem in refs.Children.Cast<YamlMappingNode>())
            {
                var refUidNode = TryGetScalar(refItem, "uid");
                if (refUidNode?.Value == null || !refUidNode.Value.EndsWith("*"))
                    continue;

                var baseUid = refUidNode.Value[..^1];
                var matchingVersion = uidToVersionHtml
                    .Where(kvp => kvp.Key.StartsWith(baseUid))
                    .Select(kvp => kvp.Value)
                    .FirstOrDefault();

                if (matchingVersion != null)
                {
                    refItem.Children[new YamlScalarNode("versionInformation")] = new YamlScalarNode(matchingVersion);
                    modified = true;
                }

                var methodName = baseUid.Contains('.') ? baseUid[(baseUid.LastIndexOf('.') + 1)..] : null;
                if (methodName != null)
                {
                    if (methodName.EndsWith("Async"))
                    {
                        refItem.Children[new YamlScalarNode("isAsyncMethod")] = new YamlScalarNode("true");
                        modified = true;
                    }
                    else
                    {
                        var asyncBase = baseUid.Replace($".{methodName}", $".{methodName}Async");
                        var asyncGroupUid = asyncBase + "*";
                        if (allRefUids.Contains(asyncGroupUid) || allUids.Any(u => u.StartsWith(asyncBase)))
                        {
                            refItem.Children[new YamlScalarNode("hasAsyncCounterpart")] = new YamlScalarNode("true");
                            refItem.Children[new YamlScalarNode("asyncCounterpartUid")] = new YamlScalarNode(asyncBase);
                            refItem.Children[new YamlScalarNode("asyncCounterpartHref")] = new YamlScalarNode($"{asyncBase}.html");
                            refItem.Children[new YamlScalarNode("asyncCounterpartName")] = new YamlScalarNode($"{methodName}Async");
                            modified = true;
                        }
                    }
                }
            }
        }

        if (modified)
        {
            using var writer = new StreamWriter(yamlFile);
            writer.WriteLine("### YamlMime:ManagedReference");
            yaml.Save(writer, assignAnchors: false);
        }
    }

    private static HashSet<string> CollectAllUids(YamlSequenceNode items)
    {
        var uids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.Children.Cast<YamlMappingNode>())
        {
            var uidNode = TryGetScalar(item, "uid");
            if (uidNode?.Value != null)
                uids.Add(uidNode.Value);
        }
        return uids;
    }

    private Dictionary<string, HashSet<string>> LoadPlatformMembers(ServiceInfo service)
    {
        var platformMembers = new Dictionary<string, HashSet<string>>();
        foreach (var framework in _options.TargetFrameworks)
        {
            var xmlPath = Path.Combine(_options.AssembliesRoot, framework, $"AWSSDK.{service.Name}.xml");
            if (!File.Exists(xmlPath))
                continue;
            platformMembers[framework] = ParseXmlDocMembers(xmlPath);
        }
        return platformMembers;
    }

    private static HashSet<string> ParseXmlDocMembers(string xmlPath)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var doc = XDocument.Load(xmlPath);
            foreach (var member in doc.Descendants("member"))
            {
                var name = member.Attribute("name")?.Value;
                if (name != null)
                    members.Add(name);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: Failed to parse XML doc {xmlPath}: {ex.Message}");
        }
        return members;
    }

    private static string FormatVersionInformation(List<string> platforms)
    {
        var platformSet = new HashSet<string>(platforms);
        var parts = new List<string>();

        var netVersions = new List<string>();
        if (platformSet.Contains("net8.0"))
            netVersions.Add("8.0 and newer");
        if (platformSet.Contains("netcoreapp3.1"))
            netVersions.Add("Core 3.1");
        if (netVersions.Count > 0)
            parts.Add($"<strong>.NET:</strong> Supported in: {string.Join(", ", netVersions)}");

        if (platformSet.Contains("netstandard2.0"))
            parts.Add("<strong>.NET Standard:</strong> Supported in: 2.0");

        if (platformSet.Contains("net472"))
            parts.Add("<strong>.NET Framework:</strong> Supported in: 4.7.2 and newer");

        return string.Join("<br/>", parts);
    }

    private List<string> GetPlatformAvailability(string commentId, Dictionary<string, HashSet<string>> platformMembers)
    {
        if (platformMembers.Count == 0)
            return _options.TargetFrameworks.ToList();

        var platforms = new List<string>();
        foreach (var framework in _options.TargetFrameworks)
        {
            if (platformMembers.TryGetValue(framework, out var members) && members.Contains(commentId))
                platforms.Add(framework);
        }

        // If no platform's XML contains this member, assume available everywhere
        if (platforms.Count == 0)
            return _options.TargetFrameworks.ToList();

        return platforms;
    }

    private static string? GetMethodNameFromUid(string uid)
    {
        if (uid.Contains(".#ctor")) return null;
        var parenIdx = uid.IndexOf('(');
        var namepart = parenIdx > 0 ? uid[..parenIdx] : uid;
        var lastDot = namepart.LastIndexOf('.');
        if (lastDot < 0) return null;
        return namepart[(lastDot + 1)..];
    }

    private static string GetUidWithoutParams(string uid)
    {
        var parenIdx = uid.IndexOf('(');
        return parenIdx > 0 ? uid[..parenIdx] : uid;
    }

    private static YamlScalarNode? TryGetScalar(YamlMappingNode node, string key)
    {
        if (node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar)
            return scalar;
        return null;
    }
}
