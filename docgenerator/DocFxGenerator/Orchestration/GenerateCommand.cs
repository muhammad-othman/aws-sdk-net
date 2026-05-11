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
            var manifest = new IncrementalManifest(_options, discovery);
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
            PostProcessMetadata(servicesToGenerate, discovery);
            Console.WriteLine($"  Completed in {FormatElapsed(stepTimer.Elapsed)}");

            // Phase 4: Build documentation (YAML → HTML) — only changed services
            var servicesToBuild = FilterServicesToBuild(services, servicesToGenerate);
            stepTimer.Restart();
            await BuildDocumentationAsync(configBuilder, intermediateFolder, servicesToBuild);
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

    private List<ServiceInfo> FilterServicesToBuild(List<ServiceInfo> allServices, List<ServiceInfo> changedServices)
    {
        if (_options.Clean)
            return allServices;

        // Only rebuild services that had metadata regenerated or don't have HTML output yet
        var toBuild = new List<ServiceInfo>();
        var skipped = 0;

        foreach (var service in allServices)
        {
            var outputDir = Path.Combine(_options.OutputFolder, "api", service.Name);
            var wasRegenerated = changedServices.Any(s => s.Name == service.Name);

            if (wasRegenerated || !Directory.Exists(outputDir))
                toBuild.Add(service);
            else
                skipped++;
        }

        if (skipped > 0)
            Console.WriteLine($"  Skipping build for {skipped} unchanged services (HTML already exists).");

        return toBuild;
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

    private void PostProcessMetadata(List<ServiceInfo> services, ServiceDiscovery discovery)
    {
        if (services.Count == 0) return;

        Console.WriteLine("Post-processing metadata (platform availability + async notes)...");
        var injector = new PlatformAvailabilityInjector(_options, discovery);

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

        // Copy logo
        var logoSource = Path.Combine(Path.GetDirectoryName(typeof(GenerateCommand).Assembly.Location)!, "logo.png");
        if (File.Exists(logoSource))
            File.Copy(logoSource, Path.Combine(intermediateFolder, "logo.png"), overwrite: true);

        // Root toc.yml → empty (no top navbar dropdown items, just logo + search)
        var rootTocPath = Path.Combine(intermediateFolder, "toc.yml");
        File.WriteAllText(rootTocPath, "[]");

        // api/toc.yml → minimal placeholder so DocFX associates api/index.md with a sidebar TOC.
        // The real toc.json is generated post-build by MergeTocJson from per-service toc files.
        var apiTocPath = Path.Combine(apiFolder, "toc.yml");
        File.WriteAllText(apiTocPath, "- name: Loading...\n  href: index.md\n");

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

    private async Task BuildDocumentationAsync(
        DocFxConfigBuilder configBuilder, string intermediateFolder, List<ServiceInfo> servicesToBuild)
    {
        Console.WriteLine("Building documentation (YAML → HTML)...");
        Directory.CreateDirectory(_options.OutputFolder);

        var servicesFolder = Path.Combine(_options.OutputFolder, "services");
        Directory.CreateDirectory(servicesFolder);

        // Build each service independently with throttled concurrency
        Console.WriteLine($"  Building {servicesToBuild.Count} services ({_options.MaxParallelism} concurrent)...");

        using var semaphore = new SemaphoreSlim(_options.MaxParallelism);
        var buildTasks = servicesToBuild.Select(async service =>
        {
            await semaphore.WaitAsync();
            try
            {
                var serviceOutputDir = Path.Combine(servicesFolder, service.Name);
                var configJson = configBuilder.BuildServiceConfig(intermediateFolder, service.Name, serviceOutputDir);
                var configPath = Path.Combine(intermediateFolder, $"docfx-build-{service.Name}.json");
                File.WriteAllText(configPath, configJson);
                await RunDocfxCommandAsync("build", configPath);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(buildTasks);

        // Generate per-service index.html redirects
        foreach (var service in servicesToBuild)
        {
            var serviceDir = Path.Combine(servicesFolder, service.Name);

            // Root service redirect → api/{Service}/namespace page
            var serviceIndex = Path.Combine(serviceDir, "index.html");
            if (!File.Exists(serviceIndex))
            {
                var namespacePage = $"api/{service.Name}/Amazon.{service.Name}.html";
                File.WriteAllText(serviceIndex, $"<!DOCTYPE html><html><head><meta http-equiv=\"refresh\" content=\"0;url={namespacePage}\"></head><body></body></html>");
            }

            // api/{Service}/index.html → first namespace page
            var apiServiceDir = Path.Combine(serviceDir, "api", service.Name);
            if (Directory.Exists(apiServiceDir))
            {
                var apiIndex = Path.Combine(apiServiceDir, "index.html");
                if (!File.Exists(apiIndex))
                {
                    var firstPage = Directory.GetFiles(apiServiceDir, $"Amazon.{service.Name}.html").FirstOrDefault()
                        ?? Directory.GetFiles(apiServiceDir, "Amazon.*.html").FirstOrDefault();
                    var target = firstPage != null ? Path.GetFileName(firstPage) : "toc.html";
                    File.WriteAllText(apiIndex, $"<!DOCTYPE html><html><head><meta http-equiv=\"refresh\" content=\"0;url={target}\"></head><body></body></html>");
                }
            }
        }

        // Generate homepage
        GenerateHomepage(servicesToBuild);

        // Copy logo to output root
        var logoSource = Path.Combine(Path.GetDirectoryName(typeof(GenerateCommand).Assembly.Location)!, "logo.png");
        if (File.Exists(logoSource))
            File.Copy(logoSource, Path.Combine(_options.OutputFolder, "logo.png"), overwrite: true);

        Console.WriteLine($"Documentation built successfully to: {_options.OutputFolder}");
    }

    private void GenerateHomepage(List<ServiceInfo> services)
    {
        var firstService = services.First().Name;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("  <title>AWS SDK for .NET API Reference</title>");
        sb.AppendLine($"  <link rel=\"stylesheet\" href=\"services/{firstService}/public/docfx.min.css\">");
        sb.AppendLine($"  <link rel=\"stylesheet\" href=\"services/{firstService}/public/main.css\">");
        sb.AppendLine("  <style>");
        sb.AppendLine("    .service-list { column-count: 4; column-gap: 2rem; list-style: none; padding: 0; margin: 0; }");
        sb.AppendLine("    .service-list li { padding: 4px 0; }");
        sb.AppendLine("    .service-list a { text-decoration: none; color: var(--bs-link-color); }");
        sb.AppendLine("    .service-list a:hover { text-decoration: underline; }");
        sb.AppendLine("    .content { display: flex; }");
        sb.AppendLine("    .sidebar { width: 300px; padding: 1rem; border-right: 1px solid var(--bs-border-color); height: calc(100vh - 70px); overflow-y: auto; }");
        sb.AppendLine("    .main-content { flex: 1; padding: 2rem; overflow-y: auto; height: calc(100vh - 70px); }");
        sb.AppendLine("    .sidebar-filter { width: 100%; padding: 6px 10px; margin-bottom: 1rem; border: 1px solid var(--bs-border-color); border-radius: 4px; background: var(--bs-body-bg); color: var(--bs-body-color); }");
        sb.AppendLine("    @media (max-width: 768px) { .service-list { column-count: 2; } .sidebar { display: none; } }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine($"<body data-bs-theme=\"dark\">");
        sb.AppendLine("  <header class=\"bg-body border-bottom\">");
        sb.AppendLine("  <nav id=\"autocollapse\" class=\"navbar navbar-expand-md\" role=\"navigation\">");
        sb.AppendLine("    <div class=\"container-xxl flex-nowrap\">");
        sb.AppendLine("      <a class=\"navbar-brand\" href=\"/index.html\">");
        sb.AppendLine("        <img id=\"logo\" class=\"svg\" src=\"logo.png\" alt=\"AWS SDK for .NET\">");
        sb.AppendLine("        AWS SDK for .NET");
        sb.AppendLine("      </a>");
        sb.AppendLine("     <div id=\"collapse navbar-collapse\">");
        sb.AppendLine("       <span style=\"font-size:1.1rem;font-weight:500\">AWS SDK for .NET API Reference</span>");
        sb.AppendLine("     </div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </nav>");
        sb.AppendLine("  </header>");
        sb.AppendLine("  <div class=\"content  container-xxl\">");
        sb.AppendLine("    <aside class=\"sidebar\">");
        sb.AppendLine("      <input type=\"text\" class=\"sidebar-filter\" id=\"filter\" placeholder=\"Filter services...\" oninput=\"filterServices()\">");
        sb.AppendLine("      <ul class=\"service-list\" id=\"service-list\" style=\"column-count:1\">");
        foreach (var service in services.OrderBy(s => s.Name))
        {
            sb.AppendLine($"        <li><a href=\"/services/{service.Name}/api/{service.Name}/Amazon.{service.Name}.html\">{service.Name}</a></li>");
        }
        sb.AppendLine("      </ul>");
        sb.AppendLine("    </aside>");
        sb.AppendLine("    <main class=\"main-content\">");
        sb.AppendLine("      <h1>AWS SDK for .NET API Reference</h1>");
        sb.AppendLine("      <p>Welcome to the AWS SDK for .NET API Reference. Select a service from the navigation to browse its API documentation.</p>");
        sb.AppendLine("    </main>");
        sb.AppendLine("  </div>");
        sb.AppendLine("  <script>");
        sb.AppendLine("    function filterServices() {");
        sb.AppendLine("      const q = document.getElementById('filter').value.toLowerCase();");
        sb.AppendLine("      document.querySelectorAll('#service-list li').forEach(li => {");
        sb.AppendLine("        li.style.display = li.textContent.toLowerCase().includes(q) ? '' : 'none';");
        sb.AppendLine("      });");
        sb.AppendLine("    }");
        sb.AppendLine("  </script>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        File.WriteAllText(Path.Combine(_options.OutputFolder, "index.html"), sb.ToString());
    }

    private void _Unused_GenerateCoreXrefmap(string intermediateFolder, string xrefmapPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("### YamlMime:XRefMap");
        sb.AppendLine("references:");

        var coreApiFolder = Path.Combine(intermediateFolder, "api", "Core");
        if (Directory.Exists(coreApiFolder))
        {
            foreach (var ymlFile in Directory.GetFiles(coreApiFolder, "*.yml"))
            {
                if (Path.GetFileName(ymlFile) == "toc.yml") continue;

                foreach (var line in File.ReadLines(ymlFile))
                {
                    if (line.StartsWith("- uid: "))
                    {
                        var uid = line["- uid: ".Length..].Trim();
                        sb.AppendLine($"- uid: {uid}");
                        sb.AppendLine($"  name: {uid}");
                        sb.AppendLine($"  href: api/Core/{uid}.html");
                    }
                }
            }
        }

        File.WriteAllText(xrefmapPath, sb.ToString());
        Console.WriteLine($"  Generated Core xrefmap for cross-service references");
    }

    private static void MergeDirectory(string sourceDir, string destDir)
    {
        // Move top-level subdirectories (api/ServiceName, public, etc.) — fast rename
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            var destSubDir = Path.Combine(destDir, dirName);

            if (!Directory.Exists(destSubDir))
            {
                Directory.Move(dir, destSubDir);
            }
            else
            {
                // Merge into existing directory
                foreach (var subDir in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
                {
                    Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, subDir)));
                }
                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var destFile = Path.Combine(destDir, Path.GetRelativePath(sourceDir, file));
                    File.Move(file, destFile, overwrite: true);
                }
            }
        }

        // Move top-level files (skip xrefmap.yml)
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            if (Path.GetFileName(file) == "xrefmap.yml")
                continue;
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Move(file, destFile, overwrite: true);
        }
    }

    private static void GenerateServiceIndexPages(string outputFolder)
    {
        var apiFolder = Path.Combine(outputFolder, "api");
        foreach (var serviceDir in Directory.GetDirectories(apiFolder))
        {
            var indexPath = Path.Combine(serviceDir, "index.html");
            if (File.Exists(indexPath)) continue;

            // Find the first namespace page (e.g. Amazon.S3.html) to redirect to
            var serviceName = Path.GetFileName(serviceDir);
            var namespacePage = Directory.GetFiles(serviceDir, $"Amazon.{serviceName}.html").FirstOrDefault()
                ?? Directory.GetFiles(serviceDir, "Amazon.*.html").FirstOrDefault();

            var target = namespacePage != null ? Path.GetFileName(namespacePage) : "toc.html";

            File.WriteAllText(indexPath, $"""
                <!DOCTYPE html>
                <html>
                <head><meta http-equiv="refresh" content="0;url={target}"></head>
                <body><a href="{target}">Redirecting...</a></body>
                </html>
                """);
        }
    }

    private static void GenerateServiceListToc(string outputFolder)
    {
        var apiFolder = Path.Combine(outputFolder, "api");
        var serviceDirs = Directory.GetDirectories(apiFolder)
            .Where(d => File.Exists(Path.Combine(d, "toc.json")))
            .OrderBy(d => Path.GetFileName(d))
            .ToList();

        using var stream = File.Create(Path.Combine(apiFolder, "toc.json"));
        using var writer = new System.Text.Json.Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteStartArray("items");

        foreach (var serviceDir in serviceDirs)
        {
            var serviceName = Path.GetFileName(serviceDir);
            writer.WriteStartObject();
            writer.WriteString("name", serviceName);
            writer.WriteString("href", $"{serviceName}/");
            writer.WriteString("topicHref", $"{serviceName}/");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void _Unused_MergeSearchIndexes(string outputFolder, List<string> searchIndexParts)
    {
        if (searchIndexParts.Count == 0) return;

        if (searchIndexParts.Count == 1)
        {
            File.WriteAllText(Path.Combine(outputFolder, "index.json"), searchIndexParts[0]);
            return;
        }

        // DocFX index.json is a JSON object with keys as relative paths and values as search data.
        // Merge by combining all objects.
        using var combinedDoc = System.Text.Json.JsonDocument.Parse("{}");
        var merged = new Dictionary<string, System.Text.Json.JsonElement>();

        foreach (var part in searchIndexParts)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(part);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    merged[prop.Name] = prop.Value.Clone();
                }
            }
            catch { }
        }

        var outputPath = Path.Combine(outputFolder, "index.json");
        using var stream = File.Create(outputPath);
        using var writer = new System.Text.Json.Utf8JsonWriter(stream);
        writer.WriteStartObject();
        foreach (var (key, value) in merged)
        {
            writer.WritePropertyName(key);
            value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static void MergeTocJson(string outputFolder)
    {
        // Build a flat api/toc.json that shows all services in the sidebar at all times.
        // Prefix hrefs with service folder name since they're relative to api/{Service}/
        // but we're writing them relative to api/.
        var apiFolder = Path.Combine(outputFolder, "api");
        var serviceDirs = Directory.GetDirectories(apiFolder)
            .Where(d => File.Exists(Path.Combine(d, "toc.json")))
            .OrderBy(d => Path.GetFileName(d))
            .ToList();

        using var stream = File.Create(Path.Combine(apiFolder, "toc.json"));
        using var writer = new System.Text.Json.Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteStartArray("items");

        foreach (var serviceDir in serviceDirs)
        {
            var serviceName = Path.GetFileName(serviceDir);
            var serviceTocPath = Path.Combine(serviceDir, "toc.json");

            try
            {
                var tocContent = File.ReadAllText(serviceTocPath);
                using var doc = System.Text.Json.JsonDocument.Parse(tocContent);

                writer.WriteStartObject();
                writer.WriteString("name", serviceName);

                if (doc.RootElement.TryGetProperty("items", out var items))
                {
                    writer.WritePropertyName("items");
                    // Rewrite items with prefixed hrefs
                    PrefixHrefs(writer, items, serviceName);
                }

                writer.WriteEndObject();
            }
            catch
            {
                writer.WriteStartObject();
                writer.WriteString("name", serviceName);
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }


    private static void PrefixHrefs(System.Text.Json.Utf8JsonWriter writer, System.Text.Json.JsonElement element, string prefix)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
            {
                PrefixHrefs(writer, item, prefix);
            }
            writer.WriteEndArray();
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var prop in element.EnumerateObject())
            {
                if ((prop.Name == "href" || prop.Name == "topicHref") &&
                    prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var val = prop.Value.GetString();
                    if (val != null && !val.StartsWith("http") && !val.StartsWith("/"))
                        writer.WriteString(prop.Name, $"{prefix}/{val}");
                    else
                        prop.Value.WriteTo(writer);
                }
                else if (prop.Name == "items")
                {
                    writer.WritePropertyName("items");
                    PrefixHrefs(writer, prop.Value, prefix);
                }
                else
                {
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        else
        {
            element.WriteTo(writer);
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

    private void WriteRootRedirect()
    {
        var rootIndex = Path.Combine(_options.OutputFolder, "index.html");
        if (!File.Exists(rootIndex))
        {
            File.WriteAllText(rootIndex, """
                <!DOCTYPE html>
                <html>
                <head><meta http-equiv="refresh" content="0;url=api/index.html"></head>
                <body><a href="api/index.html">Redirecting to API Reference...</a></body>
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
