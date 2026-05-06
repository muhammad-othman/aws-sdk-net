using System.Diagnostics;
using DocFxGenerator.Configuration;
using DocFxGenerator.PostProcessing;
using DocFxGenerator.Preprocessing;

namespace DocFxGenerator.Orchestration;

public class GenerateCommand
{
    private readonly GeneratorOptions _options;

    public GenerateCommand(GeneratorOptions options)
    {
        _options = options;
    }

    public async Task<int> ExecuteAsync()
    {
        try
        {
            var discovery = new ServiceDiscovery(_options);
            var configBuilder = new DocFxConfigBuilder(_options, discovery);

            var intermediateFolder = Path.GetFullPath(_options.IntermediateFolder);

            if (_options.Clean && Directory.Exists(intermediateFolder))
            {
                Console.WriteLine("Cleaning intermediate folder...");
                Directory.Delete(intermediateFolder, recursive: true);
            }

            Directory.CreateDirectory(intermediateFolder);

            var services = discovery.DiscoverServices();
            Console.WriteLine($"Discovered {services.Count} services to generate.");

            if (services.Count == 0)
            {
                Console.WriteLine("No services found. Nothing to generate.");
                return 0;
            }

            configBuilder.WriteFilterConfig(intermediateFolder);

            // Phase 1: Generate metadata (DLL → YAML)
            await GenerateMetadataAsync(services, configBuilder, intermediateFolder);

            // Phase 2: Pre-process examples (extract code samples → overwrite .md files)
            PreProcessExamples(services);

            // Phase 3: Post-process YAML (inject platform availability + async notes)
            PostProcessMetadata(services);

            // Phase 3: Build documentation (YAML → HTML)
            await BuildDocumentationAsync(configBuilder, intermediateFolder);

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (_options.Verbose)
                Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private void PreProcessExamples(List<ServiceInfo> services)
    {
        Console.WriteLine("Pre-processing code examples...");
        var merger = new SamplesMerger(_options);
        merger.GenerateOverwriteFiles(services);
    }

    private void PostProcessMetadata(List<ServiceInfo> services)
    {
        Console.WriteLine("Post-processing metadata (platform availability + async notes)...");
        var injector = new PlatformAvailabilityInjector(_options);

        foreach (var service in services)
        {
            if (_options.Verbose)
                Console.WriteLine($"  Processing {service.Name}...");
            injector.ProcessService(service);
        }

        Console.WriteLine("Post-processing complete.");
    }

    private async Task GenerateMetadataAsync(
        List<ServiceInfo> services,
        DocFxConfigBuilder configBuilder,
        string intermediateFolder)
    {
        Console.WriteLine($"Generating metadata for {services.Count} services...");

        if (services.Count > _options.BatchThreshold && _options.MaxParallelism > 1)
        {
            await GenerateMetadataParallelAsync(services, configBuilder, intermediateFolder);
        }
        else
        {
            await GenerateMetadataSequentialAsync(services, configBuilder, intermediateFolder);
        }
    }

    private async Task GenerateMetadataSequentialAsync(
        List<ServiceInfo> services,
        DocFxConfigBuilder configBuilder,
        string intermediateFolder)
    {
        var configJson = configBuilder.BuildMetadataConfig(services, intermediateFolder);
        var configPath = Path.Combine(intermediateFolder, "docfx-metadata.json");
        await File.WriteAllTextAsync(configPath, configJson);

        if (_options.Verbose)
            Console.WriteLine($"Metadata config written to: {configPath}");

        await RunDocfxMetadataAsync(configPath);
    }

    private async Task GenerateMetadataParallelAsync(
        List<ServiceInfo> services,
        DocFxConfigBuilder configBuilder,
        string intermediateFolder)
    {
        var batchCount = Math.Min(_options.MaxParallelism, services.Count);
        var batches = PartitionServices(services, batchCount);

        Console.WriteLine($"Splitting into {batches.Count} parallel batches ({services.Count} services total)");

        var tasks = new List<Task>();
        for (int i = 0; i < batches.Count; i++)
        {
            var batch = batches[i];
            var batchConfigJson = configBuilder.BuildMetadataConfig(batch, intermediateFolder);
            var batchConfigPath = Path.Combine(intermediateFolder, $"docfx-batch-{i}.json");
            await File.WriteAllTextAsync(batchConfigPath, batchConfigJson);

            if (_options.Verbose)
                Console.WriteLine($"  Batch {i}: {batch.Count} services");

            tasks.Add(RunDocfxMetadataAsync(batchConfigPath));
        }

        await Task.WhenAll(tasks);
        Console.WriteLine("All metadata batches completed.");
    }

    private async Task BuildDocumentationAsync(DocFxConfigBuilder configBuilder, string intermediateFolder)
    {
        Console.WriteLine("Building documentation (YAML → HTML)...");

        var buildConfigJson = configBuilder.BuildFullConfig(intermediateFolder);
        var buildConfigPath = Path.Combine(intermediateFolder, "docfx-build.json");
        await File.WriteAllTextAsync(buildConfigPath, buildConfigJson);

        if (_options.Verbose)
            Console.WriteLine($"Build config written to: {buildConfigPath}");

        await RunDocfxCommandAsync("build", buildConfigPath);

        Console.WriteLine($"Documentation built successfully to: {_options.OutputFolder}");
    }

    private async Task RunDocfxMetadataAsync(string configPath)
    {
        await RunDocfxCommandAsync("metadata", configPath);
    }

    private async Task RunDocfxCommandAsync(string command, string configPath)
    {
        var docfxPath = FindDocfxPath();

        var psi = new ProcessStartInfo
        {
            FileName = docfxPath,
            Arguments = $"{command} \"{configPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException($"Failed to start docfx {command} process");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (_options.Verbose && !string.IsNullOrEmpty(stdout))
            Console.WriteLine(stdout);

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine($"docfx {command} failed with exit code {process.ExitCode}");
            if (!string.IsNullOrEmpty(stderr))
                Console.Error.WriteLine(stderr);
            if (!string.IsNullOrEmpty(stdout))
                Console.Error.WriteLine(stdout);
            throw new InvalidOperationException($"DocFX {command} failed for config: {configPath}");
        }
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

    private static List<List<ServiceInfo>> PartitionServices(List<ServiceInfo> services, int batchCount)
    {
        var batches = new List<List<ServiceInfo>>();
        for (int i = 0; i < batchCount; i++)
            batches.Add(new List<ServiceInfo>());

        for (int i = 0; i < services.Count; i++)
            batches[i % batchCount].Add(services[i]);

        return batches;
    }
}
