using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocFxGenerator.Configuration;

public class DocFxConfigBuilder
{
    private readonly GeneratorOptions _options;

    public DocFxConfigBuilder(GeneratorOptions options)
    {
        _options = options;
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
                        Files = new[] { "api/**/*.yml", "api/**/toc.yml", "api/index.md", "toc.yml" },
                        Src = "."
                    }
                },
                Overwrite = new[]
                {
                    new ContentItem
                    {
                        Files = new[] { "overwrite/**/*.md" },
                        Src = "."
                    }
                },
                GlobalMetadata = new Dictionary<string, object>
                {
                    ["memberLayout"] = "SeparatePages",
                    ["_appTitle"] = "AWS SDK for .NET API Reference",
                    ["_enableSearch"] = true
                },
                Template = new[] { "default", "modern", GetTemplatePath() },
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
              - exclude:
                  uidRegex: ^Amazon\..+\.Internal$
                  type: Namespace
              - exclude:
                  uidRegex: ^Amazon\..+\.Internal\.
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

    public string BuildBatchConfig(string intermediateFolder, List<ServiceInfo> batch, string xrefmapPath, int batchIndex, string outputDir)
    {
        var contentFiles = batch
            .SelectMany(s => new[] { $"api/{s.Name}/**/*.yml", $"api/{s.Name}/toc.yml" })
            .Concat(new[] { "toc.yml", "api/toc.yml", "api/index.md" })
            .ToArray();

        var overwriteFiles = batch
            .Select(s => $"overwrite/{s.Name}/**/*.md")
            .ToArray();

        var config = new
        {
            build = new
            {
                content = new[]
                {
                    new { files = contentFiles, src = "." }
                },
                overwrite = new[]
                {
                    new { files = overwriteFiles, src = "." }
                },
                resource = new[]
                {
                    new { files = new[] { "logo.png" }, src = "." }
                },
                globalMetadata = new Dictionary<string, object>
                {
                    ["memberLayout"] = "SeparatePages",
                    ["_appTitle"] = "AWS SDK for .NET API Reference",
                    ["_appName"] = "AWS SDK for .NET",
                    ["_appLogoPath"] = "logo.png",
                    ["_enableSearch"] = true,
                    ["_disableContribution"] = true
                },
                xref = new[] { xrefmapPath.Replace('\\', '/') },
                template = new[] { "default", "modern", GetTemplatePath() },
                dest = outputDir,
                disableGitFeatures = true
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    public string BuildRootConfig(string intermediateFolder, string xrefmapPath)
    {
        var config = new
        {
            build = new
            {
                content = new[]
                {
                    new { files = new[] { "toc.yml", "api/toc.yml", "api/index.md", "api/**/toc.yml" }, src = "." }
                },
                globalMetadata = new Dictionary<string, object>
                {
                    ["_appTitle"] = "AWS SDK for .NET API Reference",
                    ["_enableSearch"] = true
                },
                xref = new[] { xrefmapPath.Replace('\\', '/') },
                template = new[] { "default", "modern", GetTemplatePath() },
                dest = _options.OutputFolder,
                disableGitFeatures = true
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private static string GetFilterConfigPath(string outputBasePath)
    {
        return Path.Combine(outputBasePath, "filterConfig.yml");
    }

    private static string GetTemplatePath()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(DocFxConfigBuilder).Assembly.Location)!;
        var templatePath = Path.Combine(assemblyDir, "Templates", "aws-sdk");
        return templatePath.Replace('\\', '/');
    }
}

#region Config models

public class DocfxFullConfig
{
    public BuildConfig? Build { get; set; }
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
