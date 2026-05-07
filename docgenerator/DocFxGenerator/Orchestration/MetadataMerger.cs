using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DocFxGenerator.Configuration;
using YamlDotNet.RepresentationModel;

namespace DocFxGenerator.Orchestration;

/// <summary>
/// For each target framework, generates DocFx metadata and merges any
/// members/types not already present in the primary output.
/// </summary>
public class MetadataMerger
{
    private readonly GeneratorOptions _options;
    private readonly ServiceDiscovery _discovery;

    public MetadataMerger(GeneratorOptions options, ServiceDiscovery discovery)
    {
        _options = options;
        _discovery = discovery;
    }

    public async Task GenerateAndMergeAllFrameworksAsync(List<ServiceInfo> services, string intermediateFolder)
    {
        if (services.Count == 0)
            return;

        var intermediateFullPath = Path.GetFullPath(intermediateFolder);
        var isFirst = true;

        foreach (var framework in _options.TargetFrameworks)
        {
            var frameworkServices = services
                .Where(s => s.FrameworkAvailability.TryGetValue(framework, out var avail) && avail)
                .ToList();

            if (frameworkServices.Count == 0)
                continue;

            Console.WriteLine($"  Processing {framework} ({frameworkServices.Count} services)...");

            if (isFirst)
            {
                await GenerateMetadataForFrameworkAsync(frameworkServices, framework, intermediateFullPath, intermediateFullPath);
                isFirst = false;
            }
            else
            {
                var tempOutput = Path.Combine(intermediateFullPath, $"_temp_{framework}");
                Directory.CreateDirectory(tempOutput);

                try
                {
                    await GenerateMetadataForFrameworkAsync(frameworkServices, framework, tempOutput, intermediateFullPath);
                    MergeFrameworkOutput(frameworkServices, framework, intermediateFullPath, tempOutput);
                }
                finally
                {
                    if (Directory.Exists(tempOutput))
                        Directory.Delete(tempOutput, recursive: true);
                }
            }
        }
    }

    private async Task GenerateMetadataForFrameworkAsync(
        List<ServiceInfo> services, string framework, string tempOutput, string intermediateFolder)
    {
        if (services.Count > _options.BatchThreshold && _options.MaxParallelism > 1)
        {
            var batchCount = Math.Min(_options.MaxParallelism, services.Count);
            var batches = Partition(services, batchCount);

            // if (_options.Verbose)
            Console.WriteLine($"    [{framework}] Splitting into {batches.Count} parallel batches");

            var tasks = new List<Task>();
            for (int i = 0; i < batches.Count; i++)
            {
                var configPath = WriteMetadataConfigForBatch(batches[i], framework, tempOutput, intermediateFolder, i);
                tasks.Add(RunDocfxMetadataAsync(configPath, $"{framework}/batch-{i}"));
            }

            await Task.WhenAll(tasks);
        }
        else
        {
            var configPath = WriteMetadataConfigForBatch(services, framework, tempOutput, intermediateFolder, null);
            await RunDocfxMetadataAsync(configPath, framework);
        }
    }

    private string WriteMetadataConfigForBatch(
        List<ServiceInfo> services, string framework, string tempOutput, string intermediateFolder, int? batchIndex)
    {
        var frameworkPath = Path.Combine(_options.AssembliesRoot, framework).Replace('\\', '/');
        var filterPath = Path.Combine(intermediateFolder, "filterConfig.yml").Replace('\\', '/');
        var baseReferences = _discovery.GetReferenceAssemblies(framework);

        var metadata = services.Select(service =>
        {
            string srcPath;
            List<string> references;

            if (service.IsExtension)
            {
                var dllPath = _discovery.FindExtensionDllPath(service.Name, framework);
                srcPath = Path.GetDirectoryName(dllPath)!.Replace('\\', '/');
                references = Directory.GetFiles(Path.GetDirectoryName(dllPath)!, "*.dll")
                    .Where(f => !Path.GetFileName(f).Equals($"AWSSDK.{service.Name}.dll", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Replace('\\', '/'))
                    .ToList();
                references.AddRange(baseReferences.Select(r => r.Replace('\\', '/')));
            }
            else
            {
                srcPath = frameworkPath;
                references = baseReferences.Select(r => r.Replace('\\', '/')).ToList();
            }

            return new
            {
                src = new[]
                {
                    new
                    {
                        files = new[] { $"AWSSDK.{service.Name}.dll" },
                        src = srcPath
                    }
                },
                dest = Path.Combine(tempOutput, "api", service.Name).Replace('\\', '/'),
                references = references.ToArray(),
                filter = filterPath,
                memberLayout = "SeparatePages"
            };
        }).ToArray();

        var config = new { metadata };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var suffix = batchIndex.HasValue ? $"-batch{batchIndex}" : "";
        var configPath = Path.Combine(tempOutput, $"docfx-{framework}{suffix}.json");
        File.WriteAllText(configPath, json);
        return configPath;
    }

    private async Task RunDocfxMetadataAsync(string configPath, string label)
    {
        var docfxPath = FindDocfxPath();
        var psi = new ProcessStartInfo
        {
            FileName = docfxPath,
            Arguments = $"metadata \"{configPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException($"Failed to start docfx metadata for {label}");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (_options.Verbose && !string.IsNullOrEmpty(stdout))
            Console.WriteLine($"    [{label}] {stdout}");

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine($"    [{label}] docfx metadata failed (exit code {process.ExitCode})");
            if (!string.IsNullOrEmpty(stderr))
                Console.Error.WriteLine(stderr);
            throw new InvalidOperationException($"DocFX metadata failed for {label}");
        }
    }

    private static List<List<ServiceInfo>> Partition(List<ServiceInfo> services, int batchCount)
    {
        var batches = new List<List<ServiceInfo>>();
        for (int i = 0; i < batchCount; i++)
            batches.Add(new List<ServiceInfo>());
        for (int i = 0; i < services.Count; i++)
            batches[i % batchCount].Add(services[i]);
        return batches;
    }

    private void MergeFrameworkOutput(
        List<ServiceInfo> services, string framework, string intermediateFolder, string tempOutput)
    {
        foreach (var service in services)
        {
            var primaryApiFolder = Path.Combine(intermediateFolder, "api", service.Name);
            var secondaryApiFolder = Path.Combine(tempOutput, "api", service.Name);

            if (!Directory.Exists(secondaryApiFolder))
                continue;

            if (!Directory.Exists(primaryApiFolder))
            {
                // Service only exists in this framework — copy everything
                Directory.CreateDirectory(primaryApiFolder);
                foreach (var file in Directory.GetFiles(secondaryApiFolder))
                    File.Copy(file, Path.Combine(primaryApiFolder, Path.GetFileName(file)));

                if (_options.Verbose)
                    Console.WriteLine($"    Added service {service.Name} from {framework}");
                continue;
            }

            var secondaryFiles = Directory.GetFiles(secondaryApiFolder, "*.yml");

            foreach (var secondaryFile in secondaryFiles)
            {
                var fileName = Path.GetFileName(secondaryFile);

                if (fileName == "toc.yml")
                {
                    MergeToc(Path.Combine(primaryApiFolder, "toc.yml"), secondaryFile);
                    continue;
                }

                var primaryFile = Path.Combine(primaryApiFolder, fileName);

                if (File.Exists(primaryFile))
                {
                    MergeTypeYaml(primaryFile, secondaryFile);
                }
                else
                {
                    File.Copy(secondaryFile, primaryFile);
                    if (_options.Verbose)
                        Console.WriteLine($"    Added {fileName} from {framework}");
                }
            }
        }
    }

    private void MergeTypeYaml(string primaryPath, string secondaryPath)
    {
        var primaryContent = File.ReadAllText(primaryPath);
        var secondaryContent = File.ReadAllText(secondaryPath);

        const string header = "### YamlMime:ManagedReference";
        if (!primaryContent.StartsWith(header) || !secondaryContent.StartsWith(header))
            return;

        var primaryYaml = ParseYaml(primaryContent[header.Length..]);
        var secondaryYaml = ParseYaml(secondaryContent[header.Length..]);
        if (primaryYaml == null || secondaryYaml == null)
            return;

        var primaryRoot = (YamlMappingNode)primaryYaml.Documents[0].RootNode;
        var secondaryRoot = (YamlMappingNode)secondaryYaml.Documents[0].RootNode;

        var modified = false;
        modified |= MergeItems(primaryRoot, secondaryRoot);
        modified |= MergeReferences(primaryRoot, secondaryRoot);

        if (modified)
            WriteYamlFile(primaryPath, header, primaryYaml);
    }

    private bool MergeItems(YamlMappingNode primaryRoot, YamlMappingNode secondaryRoot)
    {
        var itemsKey = new YamlScalarNode("items");

        if (!primaryRoot.Children.TryGetValue(itemsKey, out var primaryItemsNode) ||
            !secondaryRoot.Children.TryGetValue(itemsKey, out var secondaryItemsNode))
            return false;

        var primaryItems = (YamlSequenceNode)primaryItemsNode;
        var secondaryItems = (YamlSequenceNode)secondaryItemsNode;

        var primaryUids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in primaryItems.Children.Cast<YamlMappingNode>())
        {
            var uid = GetScalar(item, "uid");
            if (uid != null)
                primaryUids.Add(uid);
        }

        var modified = false;

        foreach (var item in secondaryItems.Children.Cast<YamlMappingNode>())
        {
            var uid = GetScalar(item, "uid");
            if (uid == null || primaryUids.Contains(uid))
                continue;

            primaryItems.Add(item);
            primaryUids.Add(uid);
            modified = true;

            var parentUid = GetScalar(item, "parent");
            if (parentUid != null)
                UpdateParentChildren(primaryItems, parentUid, uid);
        }

        return modified;
    }

    private static void UpdateParentChildren(YamlSequenceNode items, string parentUid, string childUid)
    {
        foreach (var item in items.Children.Cast<YamlMappingNode>())
        {
            if (GetScalar(item, "uid") != parentUid)
                continue;

            var childrenKey = new YamlScalarNode("children");
            if (item.Children.TryGetValue(childrenKey, out var childrenNode) &&
                childrenNode is YamlSequenceNode childrenSeq)
            {
                var existing = childrenSeq.Children.OfType<YamlScalarNode>()
                    .Any(c => c.Value == childUid);
                if (!existing)
                    childrenSeq.Add(new YamlScalarNode(childUid));
            }
            break;
        }
    }

    private static bool MergeReferences(YamlMappingNode primaryRoot, YamlMappingNode secondaryRoot)
    {
        var refsKey = new YamlScalarNode("references");

        if (!secondaryRoot.Children.TryGetValue(refsKey, out var secondaryRefsNode))
            return false;

        var secondaryRefs = (YamlSequenceNode)secondaryRefsNode;

        YamlSequenceNode primaryRefs;
        if (primaryRoot.Children.TryGetValue(refsKey, out var primaryRefsNode))
        {
            primaryRefs = (YamlSequenceNode)primaryRefsNode;
        }
        else
        {
            primaryRefs = new YamlSequenceNode();
            primaryRoot.Children[refsKey] = primaryRefs;
        }

        var primaryRefUids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var refItem in primaryRefs.Children.Cast<YamlMappingNode>())
        {
            var uid = GetScalar(refItem, "uid");
            if (uid != null)
                primaryRefUids.Add(uid);
        }

        var modified = false;
        foreach (var refItem in secondaryRefs.Children.Cast<YamlMappingNode>())
        {
            var uid = GetScalar(refItem, "uid");
            if (uid == null || primaryRefUids.Contains(uid))
                continue;

            primaryRefs.Add(refItem);
            primaryRefUids.Add(uid);
            modified = true;
        }

        return modified;
    }

    private void MergeToc(string primaryTocPath, string secondaryTocPath)
    {
        if (!File.Exists(primaryTocPath))
        {
            File.Copy(secondaryTocPath, primaryTocPath);
            return;
        }

        var primaryContent = File.ReadAllText(primaryTocPath);
        var secondaryContent = File.ReadAllText(secondaryTocPath);

        const string tocHeader = "### YamlMime:TableOfContent";
        var primaryYamlText = primaryContent.StartsWith(tocHeader)
            ? primaryContent[tocHeader.Length..] : primaryContent;
        var secondaryYamlText = secondaryContent.StartsWith(tocHeader)
            ? secondaryContent[tocHeader.Length..] : secondaryContent;

        var primaryYaml = ParseYaml(primaryYamlText);
        var secondaryYaml = ParseYaml(secondaryYamlText);
        if (primaryYaml == null || secondaryYaml == null)
            return;

        var primaryItems = GetSequence(primaryYaml.Documents[0].RootNode);
        var secondaryItems = GetSequence(secondaryYaml.Documents[0].RootNode);
        if (primaryItems == null || secondaryItems == null)
            return;

        if (MergeTocItems(primaryItems, secondaryItems))
        {
            var headerToWrite = primaryContent.StartsWith(tocHeader) ? tocHeader : null;
            WriteYamlFile(primaryTocPath, headerToWrite, primaryYaml);
        }
    }

    private bool MergeTocItems(YamlSequenceNode primary, YamlSequenceNode secondary)
    {
        var primaryUids = new HashSet<string>(StringComparer.Ordinal);
        CollectUids(primary, primaryUids);

        var modified = false;

        foreach (var item in secondary.Children.Cast<YamlMappingNode>())
        {
            var uid = GetScalar(item, "uid");
            if (uid == null) continue;

            if (primaryUids.Contains(uid))
            {
                var primaryItem = FindByUid(primary, uid);
                var secondaryChildren = GetNestedItems(item);
                var primaryChildren = primaryItem != null ? GetNestedItems(primaryItem) : null;

                if (secondaryChildren != null && primaryChildren != null)
                    modified |= MergeTocItems(primaryChildren, secondaryChildren);
                else if (secondaryChildren != null && primaryItem != null)
                {
                    primaryItem.Children[new YamlScalarNode("items")] = secondaryChildren;
                    modified = true;
                }
            }
            else
            {
                InsertAlphabetically(primary, item);
                primaryUids.Add(uid);
                modified = true;
            }
        }

        return modified;
    }

    private static void WriteYamlFile(string path, string? header, YamlStream yaml)
    {
        var sb = new StringBuilder();
        if (header != null)
            sb.AppendLine(header);

        using (var writer = new StringWriter(sb))
        {
            yaml.Save(writer, assignAnchors: false);
        }

        // Remove the trailing document end marker that YamlDotNet adds
        var result = sb.ToString();
        result = result.TrimEnd();
        if (result.EndsWith("\n...") || result.EndsWith("\r\n..."))
            result = result[..result.LastIndexOf("...")].TrimEnd();

        File.WriteAllText(path, result + Environment.NewLine);
    }

    private static void InsertAlphabetically(YamlSequenceNode items, YamlMappingNode newItem)
    {
        var newName = GetScalar(newItem, "name") ?? GetScalar(newItem, "uid") ?? "";
        for (int i = 0; i < items.Children.Count; i++)
        {
            if (items.Children[i] is YamlMappingNode existing)
            {
                var name = GetScalar(existing, "name") ?? GetScalar(existing, "uid") ?? "";
                if (string.Compare(newName, name, StringComparison.Ordinal) < 0)
                {
                    items.Children.Insert(i, newItem);
                    return;
                }
            }
        }
        items.Add(newItem);
    }

    private static void CollectUids(YamlSequenceNode items, HashSet<string> uids)
    {
        foreach (var item in items.Children.Cast<YamlMappingNode>())
        {
            var uid = GetScalar(item, "uid");
            if (uid != null) uids.Add(uid);
            var nested = GetNestedItems(item);
            if (nested != null) CollectUids(nested, uids);
        }
    }

    private static YamlMappingNode? FindByUid(YamlSequenceNode items, string uid)
    {
        return items.Children.Cast<YamlMappingNode>()
            .FirstOrDefault(item => GetScalar(item, "uid") == uid);
    }

    private static YamlSequenceNode? GetNestedItems(YamlMappingNode node)
    {
        if (node.Children.TryGetValue(new YamlScalarNode("items"), out var val) && val is YamlSequenceNode seq)
            return seq;
        return null;
    }

    private static YamlSequenceNode? GetSequence(YamlNode node)
    {
        if (node is YamlSequenceNode seq) return seq;
        if (node is YamlMappingNode map &&
            map.Children.TryGetValue(new YamlScalarNode("items"), out var val) &&
            val is YamlSequenceNode items)
            return items;
        return null;
    }

    private static YamlStream? ParseYaml(string content)
    {
        var yaml = new YamlStream();
        try
        {
            using var reader = new StringReader(content.TrimStart('\r', '\n'));
            yaml.Load(reader);
            return yaml.Documents.Count > 0 ? yaml : null;
        }
        catch { return null; }
    }

    private static string? GetScalar(YamlMappingNode node, string key)
    {
        if (node.Children.TryGetValue(new YamlScalarNode(key), out var val) && val is YamlScalarNode scalar)
            return scalar.Value;
        return null;
    }

    private static string FindDocfxPath()
    {
        var toolsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools", "docfx.exe");
        if (File.Exists(toolsPath)) return toolsPath;

        var toolsPathUnix = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools", "docfx");
        if (File.Exists(toolsPathUnix)) return toolsPathUnix;

        return "docfx";
    }
}
