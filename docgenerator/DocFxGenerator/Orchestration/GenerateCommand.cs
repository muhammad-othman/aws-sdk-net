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
        var totalTimer = Stopwatch.StartNew();

        try
        {
            var discovery = new ServiceDiscovery(_options);
            var configBuilder = new DocFxConfigBuilder(_options);
            var intermediateFolder = Path.GetFullPath(_options.IntermediateFolder);

            if (_options.Clean)
            {
                if (Directory.Exists(intermediateFolder))
                {
                    Console.WriteLine("Cleaning intermediate folder...");
                    Directory.Delete(intermediateFolder, recursive: true);
                }
                if (Directory.Exists(_options.OutputFolder))
                {
                    Console.WriteLine("Cleaning output folder...");
                    Directory.Delete(_options.OutputFolder, recursive: true);
                }
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
            GenerateRootContent(services, intermediateFolder);

            // Phase 1: Generate metadata for all frameworks and merge unique members
            var manifest = new IncrementalManifest(_options);
            var servicesToGenerate = FilterChangedServices(services, manifest);
            var stepTimer = Stopwatch.StartNew();
            await GenerateMetadataAsync(servicesToGenerate, discovery, intermediateFolder);
            Console.WriteLine($"  Completed in {FormatElapsed(stepTimer.Elapsed)}");

            foreach (var svc in servicesToGenerate)
                manifest.RecordService(svc);
            manifest.Save();

            // Phase 2: Pre-process examples
            stepTimer.Restart();
            PreProcessExamples(services);
            Console.WriteLine($"  Completed in {FormatElapsed(stepTimer.Elapsed)}");

            // Phase 3: Post-process YAML (platform availability + async notes)
            stepTimer.Restart();
            PostProcessMetadata(servicesToGenerate);
            Console.WriteLine($"  Completed in {FormatElapsed(stepTimer.Elapsed)}");

            // Phase 4: Build documentation (YAML → HTML)
            stepTimer.Restart();
            await BuildDocumentationAsync(configBuilder, intermediateFolder);
            Console.WriteLine($"  Completed in {FormatElapsed(stepTimer.Elapsed)}");

            // Phase 5: Generate redirect rules
            stepTimer.Restart();
            GenerateRedirects(services);
            Console.WriteLine($"  Completed in {FormatElapsed(stepTimer.Elapsed)}");

            Console.WriteLine($"\nTotal time: {FormatElapsed(totalTimer.Elapsed)}");
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

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes >= 1)
            return $"{(int)elapsed.TotalMinutes} minute{((int)elapsed.TotalMinutes != 1 ? "s" : "")} {elapsed.Seconds} seconds";
        return $"{elapsed.TotalSeconds:F1} seconds";
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

        Console.WriteLine($"Generating metadata for {services.Count} services across all frameworks...");
        var merger = new MetadataMerger(_options, discovery);
        await merger.GenerateAndMergeAllFrameworksAsync(services, intermediateFolder);
        Console.WriteLine("Metadata generation complete.");
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

    private void GenerateRedirects(List<ServiceInfo> services)
    {
        Console.WriteLine("Generating redirect rules...");
        var generator = new RedirectRuleGenerator(_options);
        generator.GenerateRedirects(services);
    }

    private void GenerateRootContent(List<ServiceInfo> services, string intermediateFolder)
    {
        var apiFolder = Path.Combine(intermediateFolder, "api");
        Directory.CreateDirectory(apiFolder);

        // Root toc.yml → top navbar with single "API Reference" entry
        var rootTocPath = Path.Combine(intermediateFolder, "toc.yml");
        File.WriteAllText(rootTocPath, "- name: API Reference\n  href: api/\n");

        // api/toc.yml → left sidebar listing all services
        var apiTocPath = Path.Combine(apiFolder, "toc.yml");
        using (var writer = new StreamWriter(apiTocPath))
        {
            foreach (var service in services.OrderBy(s => s.Name))
            {
                writer.WriteLine($"- name: {service.Name}");
                writer.WriteLine($"  href: {service.Name}/toc.yml");
            }
        }

        // api/index.md → landing page (inside api/ so it gets the left sidebar)
        var indexPath = Path.Combine(apiFolder, "index.md");
        File.WriteAllText(indexPath, """
            ---
            title: AWS SDK for .NET API Reference
            ---

            # AWS SDK for .NET API Reference

            Welcome to the AWS SDK for .NET API Reference. Select a service from the navigation to browse its API documentation.
            """.Replace("            ", ""));
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

        WriteRootRedirect();

        Console.WriteLine($"Documentation built successfully to: {_options.OutputFolder}");
    }

    private void WriteRootRedirect()
    {
        var rootIndex = Path.Combine(_options.OutputFolder, "index.html");
        if (!File.Exists(rootIndex))
        {
            File.WriteAllText(rootIndex, """
                <!DOCTYPE html>
                <html>
                <head><meta http-equiv="refresh" content="0;url=api/"></head>
                <body><a href="api/">Redirecting to API Reference...</a></body>
                </html>
                """.Replace("                ", ""));
        }
    }

    private async Task RunDocfxCommandAsync(string command, string configPath)
    {
        var docfxPath = FindDocfxPath();

        var psi = new ProcessStartInfo
        {
            FileName = docfxPath,
            Arguments = $"{command} \"{configPath}\" --disableGitFeatures",
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
