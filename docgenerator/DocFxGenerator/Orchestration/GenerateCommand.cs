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

            // Phase 1: Generate metadata with incremental + parallel support
            var manifest = new IncrementalManifest(_options);
            var servicesToGenerate = FilterChangedServices(services, manifest);
            await GenerateMetadataAsync(servicesToGenerate, discovery, intermediateFolder);
            foreach (var svc in servicesToGenerate)
                manifest.RecordService(svc);
            manifest.Save();

            // Phase 2: Pre-process examples
            PreProcessExamples(services);

            // Phase 3: Post-process YAML (platform availability + async notes)
            PostProcessMetadata(servicesToGenerate);

            // Phase 4: Build documentation (YAML → HTML)
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

    private List<ServiceInfo> FilterChangedServices(List<ServiceInfo> services, IncrementalManifest manifest)
    {
        if (_options.Clean)
            return services;

        var changed = new List<ServiceInfo>();
        var skipped = 0;

        foreach (var service in services)
        {
            if (manifest.HasServiceChanged(service))
                changed.Add(service);
            else
                skipped++;
        }

        if (skipped > 0)
            Console.WriteLine($"  Skipping {skipped} unchanged services (incremental build).");

        if (changed.Count > 0)
            Console.WriteLine($"  {changed.Count} services need metadata regeneration.");

        return changed;
    }

    private async Task GenerateMetadataAsync(
        List<ServiceInfo> services,
        ServiceDiscovery discovery,
        string intermediateFolder)
    {
        if (services.Count == 0)
        {
            Console.WriteLine("Metadata up-to-date, skipping generation.");
            return;
        }

        Console.WriteLine($"Generating metadata for {services.Count} services...");

        if (services.Count > _options.BatchThreshold && _options.MaxParallelism > 1)
        {
            var runner = new ParallelMetadataRunner(_options, discovery);
            await runner.RunAsync(services, intermediateFolder);
        }
        else
        {
            await GenerateMetadataSequentialAsync(services, discovery, intermediateFolder);
        }
    }

    private async Task GenerateMetadataSequentialAsync(
        List<ServiceInfo> services,
        ServiceDiscovery discovery,
        string intermediateFolder)
    {
        var configBuilder = new DocFxConfigBuilder(_options, discovery);
        var configJson = configBuilder.BuildMetadataConfig(services, intermediateFolder);
        var configPath = Path.Combine(intermediateFolder, "docfx-metadata.json");
        await File.WriteAllTextAsync(configPath, configJson);

        if (_options.Verbose)
            Console.WriteLine($"  Metadata config written to: {configPath}");

        await RunDocfxCommandAsync("metadata", configPath);
    }

    private void PreProcessExamples(List<ServiceInfo> services)
    {
        Console.WriteLine("Pre-processing code examples...");
        var merger = new SamplesMerger(_options);
        merger.GenerateOverwriteFiles(services);
    }

    private void PostProcessMetadata(List<ServiceInfo> services)
    {
        if (services.Count == 0) return;

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

    private async Task BuildDocumentationAsync(DocFxConfigBuilder configBuilder, string intermediateFolder)
    {
        Console.WriteLine("Building documentation (YAML → HTML)...");

        var buildConfigJson = configBuilder.BuildFullConfig(intermediateFolder);
        var buildConfigPath = Path.Combine(intermediateFolder, "docfx-build.json");
        await File.WriteAllTextAsync(buildConfigPath, buildConfigJson);

        if (_options.Verbose)
            Console.WriteLine($"  Build config written to: {buildConfigPath}");

        await RunDocfxCommandAsync("build", buildConfigPath);

        Console.WriteLine($"Documentation built successfully to: {_options.OutputFolder}");
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
}
