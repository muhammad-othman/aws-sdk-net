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
