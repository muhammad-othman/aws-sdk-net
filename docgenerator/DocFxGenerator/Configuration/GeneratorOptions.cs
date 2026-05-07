namespace DocFxGenerator.Configuration;

public class GeneratorOptions
{
    public required string AssembliesRoot { get; init; }
    public required string OutputFolder { get; init; }
    public required string[] Services { get; init; }
    public required string SamplesFolder { get; init; }
    public string? ExampleMetaJsonPath { get; init; }
    public bool Clean { get; init; }
    public bool Verbose { get; init; }
    public int MaxParallelism { get; init; } = Math.Min(Environment.ProcessorCount, 8);
    public int BatchThreshold { get; init; } = 20;

    public string IntermediateFolder => Path.Combine(
        Path.GetDirectoryName(OutputFolder)!,
        "_intermediate");

    public bool GenerateAllServices => Services.Length == 1 && Services[0] == "*";

    public string[] TargetFrameworks => ["net472", "net8.0", "netstandard2.0", "netcoreapp3.1"];

    public static GeneratorOptions? ParseArgs(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--"))
            {
                var key = arg[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    dict[key] = args[++i];
                }
                else
                {
                    flags.Add(key);
                }
            }
            else if (arg == "-h" || arg == "--help")
            {
                return null;
            }
        }

        if (flags.Contains("help"))
            return null;

        var baseDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));

        var assembliesRoot = dict.GetValueOrDefault("assemblies-root")
            ?? Path.Combine(repoRoot, "Deployment", "assemblies");

        var outputFolder = dict.GetValueOrDefault("output")
            ?? Path.Combine(repoRoot, "docgenerator", "_site");

        var services = dict.GetValueOrDefault("services")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? new[] { "*" };

        var samplesFolder = dict.GetValueOrDefault("samples-folder")
            ?? Path.Combine(repoRoot, "docgenerator", "AWSSDKDocSamples");

        return new GeneratorOptions
        {
            AssembliesRoot = Path.GetFullPath(assembliesRoot),
            OutputFolder = Path.GetFullPath(outputFolder),
            Services = services,
            SamplesFolder = Path.GetFullPath(samplesFolder),
            ExampleMetaJsonPath = dict.GetValueOrDefault("example-meta-json"),
            Clean = flags.Contains("clean"),
            Verbose = flags.Contains("verbose"),
            MaxParallelism = int.TryParse(dict.GetValueOrDefault("max-parallelism"), out var mp) ? mp : Math.Min(Environment.ProcessorCount, 8),
            BatchThreshold = int.TryParse(dict.GetValueOrDefault("batch-threshold"), out var bt) ? bt : 20
        };
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            AWS SDK for .NET DocFX Documentation Generator

            Usage: DocFxGenerator [options]

            Options:
              --assemblies-root <path>    Root folder containing platform subfolders (default: Deployment/assemblies)
              --output <path>             Output directory for generated documentation (default: docgenerator/_site)
              --services <svc1,svc2>      Comma-delimited service list, or '*' for all (default: *)
              --samples-folder <path>     Root folder for generated code samples
              --example-meta-json <path>  Path to example_meta.json for CodeLibrary examples
              --clean                     Delete output and intermediate folders before generation
              --verbose                   Enable verbose diagnostic output
              --max-parallelism <N>       Max parallel metadata batches (default: CPU count, max 8)
              --batch-threshold <N>       Parallel kicks in above this count (default: 20)
              --help                      Show this help message
            """);
    }
}
