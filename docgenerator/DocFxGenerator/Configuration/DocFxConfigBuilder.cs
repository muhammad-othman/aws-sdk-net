using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocFxGenerator.Configuration;

public class DocFxConfigBuilder
{
    private readonly GeneratorOptions _options;
    private readonly ServiceDiscovery _discovery;

    public DocFxConfigBuilder(GeneratorOptions options, ServiceDiscovery discovery)
    {
        _options = options;
        _discovery = discovery;
    }

    public string BuildMetadataConfig(IReadOnlyList<ServiceInfo> services, string outputBasePath)
    {
        var references = _discovery.GetReferenceAssemblies(_options.PrimaryFramework);
        var frameworkPath = Path.Combine(_options.AssembliesRoot, _options.PrimaryFramework);

        var metadata = services.Select(service => new MetadataItem
        {
            Src = new[]
            {
                new SrcItem
                {
                    Files = new[] { Path.GetFileName(service.DllPath) },
                    Src = frameworkPath
                }
            },
            Dest = Path.Combine(outputBasePath, "api", service.Name),
            References = references.ToArray(),
            Filter = GetFilterConfigPath(outputBasePath),
            MemberLayout = "SeparatePages"
        }).ToArray();

        var config = new DocfxConfig
        {
            Metadata = metadata
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    public string BuildFullConfig(string intermediateFolder)
    {
        var config = new DocfxFullConfig
        {
            Build = new BuildConfig
            {
                Content = new[]
                {
                    new ContentItem
                    {
                        Files = new[] { "api/**/*.yml", "api/**/toc.yml" },
                        Src = intermediateFolder
                    }
                },
                Overwrite = new[]
                {
                    new ContentItem
                    {
                        Files = new[] { "overwrite/**/*.md" },
                        Src = intermediateFolder
                    }
                },
                GlobalMetadata = new Dictionary<string, object>
                {
                    ["memberLayout"] = "SeparatePages",
                    ["_appTitle"] = "AWS SDK for .NET API Reference",
                    ["_enableSearch"] = true
                },
                Template = new[] { "default", "modern" },
                Dest = _options.OutputFolder
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    public void WriteFilterConfig(string outputBasePath)
    {
        var filterPath = GetFilterConfigPath(outputBasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(filterPath)!);

        var filterContent = """
            apiRules:
              - include:
                  uidRegex: ^Amazon\.
                  type: Namespace
              - include:
                  uidRegex: ^Amazon\.
                  type: Type
              - include:
                  uidRegex: ^Amazon\.
                  type: Member
              - exclude:
                  uidRegex: .*
            """;

        File.WriteAllText(filterPath, filterContent);
    }

    private static string GetFilterConfigPath(string outputBasePath)
    {
        return Path.Combine(outputBasePath, "filterConfig.yml");
    }
}

#region Config models

public class DocfxConfig
{
    public MetadataItem[]? Metadata { get; set; }
}

public class DocfxFullConfig
{
    public BuildConfig? Build { get; set; }
}

public class MetadataItem
{
    public SrcItem[]? Src { get; set; }
    public string? Dest { get; set; }
    public string[]? References { get; set; }
    public string? Filter { get; set; }
    public string? MemberLayout { get; set; }
}

public class SrcItem
{
    public string[]? Files { get; set; }
    public string? Src { get; set; }
}

public class BuildConfig
{
    public ContentItem[]? Content { get; set; }
    public ContentItem[]? Overwrite { get; set; }
    public Dictionary<string, object>? GlobalMetadata { get; set; }
    public string[]? Template { get; set; }
    public string? Dest { get; set; }
}

public class ContentItem
{
    public string[]? Files { get; set; }
    public string? Src { get; set; }
}

#endregion
