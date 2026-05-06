using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocFxGenerator.Configuration;

namespace DocFxGenerator.Orchestration;

public class ParallelMetadataRunner
{
    private readonly GeneratorOptions _options;
    private readonly ServiceDiscovery _discovery;

    public ParallelMetadataRunner(GeneratorOptions options, ServiceDiscovery discovery)
    {
        _options = options;
        _discovery = discovery;
    }

    public async Task RunAsync(List<ServiceInfo> services, string intermediateFolder)
    {
        var batchCount = Math.Min(_options.MaxParallelism, services.Count);
        var batches = Partition(services, batchCount);

        Console.WriteLine($"  Splitting {services.Count} services into {batches.Count} parallel batches");

        var tasks = new List<Task>();
        for (int i = 0; i < batches.Count; i++)
        {
            var batchConfig = GenerateBatchConfig(batches[i], intermediateFolder, i);
            tasks.Add(RunDocfxMetadataAsync(batchConfig, i));
        }

        await Task.WhenAll(tasks);
    }

    private string GenerateBatchConfig(List<ServiceInfo> batch, string intermediateFolder, int batchIndex)
    {
        var references = _discovery.GetReferenceAssemblies(_options.PrimaryFramework);
        var frameworkPath = Path.Combine(_options.AssembliesRoot, _options.PrimaryFramework);
        var filterPath = Path.Combine(intermediateFolder, "filterConfig.yml");

        var metadata = batch.Select(service => new
        {
            src = new[]
            {
                new
                {
                    files = new[] { Path.GetFileName(service.DllPath) },
                    src = frameworkPath
                }
            },
            dest = Path.Combine(intermediateFolder, "api", service.Name),
            references = references.ToArray(),
            filter = filterPath,
            memberLayout = "SeparatePages"
        }).ToArray();

        var config = new { metadata };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var configPath = Path.Combine(intermediateFolder, $"docfx-batch-{batchIndex}.json");
        File.WriteAllText(configPath, json);
        return configPath;
    }

    private async Task RunDocfxMetadataAsync(string configPath, int batchIndex)
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
            throw new InvalidOperationException($"Failed to start docfx metadata process for batch {batchIndex}");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (_options.Verbose && !string.IsNullOrEmpty(stdout))
            Console.WriteLine($"  [Batch {batchIndex}] {stdout}");

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine($"  [Batch {batchIndex}] docfx metadata failed with exit code {process.ExitCode}");
            if (!string.IsNullOrEmpty(stderr))
                Console.Error.WriteLine(stderr);
            throw new InvalidOperationException($"DocFX metadata generation failed for batch {batchIndex}");
        }

        if (_options.Verbose)
            Console.WriteLine($"  [Batch {batchIndex}] completed");
    }

    private static string FindDocfxPath()
    {
        var toolsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools", "docfx.exe");

        if (File.Exists(toolsPath))
            return toolsPath;

        var toolsPathUnix = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools", "docfx");

        if (File.Exists(toolsPathUnix))
            return toolsPathUnix;

        return "docfx";
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
}
