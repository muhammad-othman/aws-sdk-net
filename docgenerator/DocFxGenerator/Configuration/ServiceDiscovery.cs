namespace DocFxGenerator.Configuration;

public class ServiceDiscovery
{
    private readonly GeneratorOptions _options;

    public ServiceDiscovery(GeneratorOptions options)
    {
        _options = options;
    }

    public List<ServiceInfo> DiscoverServices()
    {
        var serviceMap = new Dictionary<string, ServiceInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var framework in _options.TargetFrameworks)
        {
            var frameworkPath = Path.Combine(_options.AssembliesRoot, framework);
            if (!Directory.Exists(frameworkPath))
                continue;

            // Scan top-level DLLs
            var dlls = Directory.GetFiles(frameworkPath, "AWSSDK.*.dll")
                .Where(f => !IsExcludedAssembly(Path.GetFileName(f)))
                .OrderBy(f => f);

            foreach (var dllPath in dlls)
                TryAddService(dllPath, serviceMap, isExtension: false);

            // Scan extensions subfolders
            var extensionsPath = Path.Combine(frameworkPath, "extensions");
            if (Directory.Exists(extensionsPath))
            {
                foreach (var extDir in Directory.GetDirectories(extensionsPath))
                {
                    var extDlls = Directory.GetFiles(extDir, "AWSSDK.*.dll").OrderBy(f => f);
                    foreach (var dllPath in extDlls)
                        TryAddService(dllPath, serviceMap, isExtension: true);
                }
            }
        }

        if (serviceMap.Count == 0)
            throw new InvalidOperationException($"No assemblies found under: {_options.AssembliesRoot}");

        var services = serviceMap.Values.OrderBy(s => s.Name).ToList();

        if (!_options.GenerateAllServices)
        {
            var requestedServices = new HashSet<string>(_options.Services, StringComparer.OrdinalIgnoreCase);
            services = services.Where(s => requestedServices.Contains(s.Name)).ToList();

            var found = new HashSet<string>(services.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            var missing = requestedServices.Except(found, StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
            {
                Console.WriteLine($"Warning: Services not found: {string.Join(", ", missing)}");
            }
        }

        return services;
    }

    private void TryAddService(string dllPath, Dictionary<string, ServiceInfo> serviceMap, bool isExtension)
    {
        var fileName = Path.GetFileNameWithoutExtension(dllPath);
        var serviceName = ExtractServiceName(fileName, isExtension);
        if (serviceName == null) return;

        var xmlPath = Path.ChangeExtension(dllPath, ".xml");
        if (!File.Exists(xmlPath)) return;

        if (serviceMap.ContainsKey(serviceName))
            return;

        serviceMap[serviceName] = new ServiceInfo
        {
            Name = serviceName,
            DllPath = dllPath,
            XmlPath = xmlPath,
            IsExtension = isExtension,
            FrameworkAvailability = GetFrameworkAvailability(serviceName, isExtension)
        };
    }

    public string? GetDllPathForFramework(ServiceInfo service, string framework)
    {
        if (service.IsExtension)
            return FindExtensionDllPath(service.Name, framework);

        var dllPath = Path.Combine(_options.AssembliesRoot, framework, $"AWSSDK.{service.Name}.dll");
        return File.Exists(dllPath) ? dllPath : null;
    }

    public List<string> GetReferenceAssemblies(string framework)
    {
        var frameworkPath = Path.Combine(_options.AssembliesRoot, framework);
        var references = new List<string>();

        var coreRef = Path.Combine(frameworkPath, "AWSSDK.Core.dll");
        if (File.Exists(coreRef))
            references.Add(coreRef);

        // CborProtocol can be at root or in extensions subfolder
        var cborRef = Path.Combine(frameworkPath, "AWSSDK.Extensions.CborProtocol.dll");
        if (File.Exists(cborRef))
        {
            references.Add(cborRef);
        }
        else
        {
            var extCborPath = FindExtensionDllPath("Extensions.CborProtocol", framework);
            if (extCborPath != null)
                references.Add(extCborPath);
        }

        // System.Formats.Cbor can be at root or co-located with CborProtocol extension
        var systemCborRef = Path.Combine(frameworkPath, "System.Formats.Cbor.dll");
        if (File.Exists(systemCborRef))
        {
            references.Add(systemCborRef);
        }
        else
        {
            var extCborDir = FindExtensionDllPath("Extensions.CborProtocol", framework);
            if (extCborDir != null)
            {
                var colocated = Path.Combine(Path.GetDirectoryName(extCborDir)!, "System.Formats.Cbor.dll");
                if (File.Exists(colocated))
                    references.Add(colocated);
            }
        }

        return references;
    }

    private Dictionary<string, bool> GetFrameworkAvailability(string serviceName, bool isExtension)
    {
        var availability = new Dictionary<string, bool>();
        foreach (var framework in _options.TargetFrameworks)
        {
            if (isExtension)
            {
                var extensionsPath = Path.Combine(_options.AssembliesRoot, framework, "extensions");
                var found = Directory.Exists(extensionsPath) &&
                    Directory.GetDirectories(extensionsPath)
                        .Any(d => File.Exists(Path.Combine(d, $"AWSSDK.{serviceName}.dll")));
                availability[framework] = found;
            }
            else
            {
                var dllPath = Path.Combine(_options.AssembliesRoot, framework, $"AWSSDK.{serviceName}.dll");
                availability[framework] = File.Exists(dllPath);
            }
        }
        return availability;
    }

    /// <summary>
    /// Finds the DLL path for an extension service in a given framework's extensions subfolders.
    /// </summary>
    public string? FindExtensionDllPath(string serviceName, string framework)
    {
        var extensionsPath = Path.Combine(_options.AssembliesRoot, framework, "extensions");
        if (!Directory.Exists(extensionsPath))
            return null;

        foreach (var dir in Directory.GetDirectories(extensionsPath))
        {
            var dllPath = Path.Combine(dir, $"AWSSDK.{serviceName}.dll");
            if (File.Exists(dllPath))
                return dllPath;
        }
        return null;
    }

    private static string? ExtractServiceName(string fileName, bool isExtension = false)
    {
        const string prefix = "AWSSDK.";
        if (!fileName.StartsWith(prefix))
            return null;

        var serviceName = fileName[prefix.Length..];

        if (serviceName == "Core")
            return isExtension ? null : serviceName;

        if (serviceName.StartsWith("Extensions.") && !isExtension)
            return null;

        return serviceName;
    }

    private static bool IsExcludedAssembly(string fileName)
    {
        var excludePatterns = new[]
        {
            "AWSSDK.Extensions.",
            "System.",
            "Microsoft.",
            "Newtonsoft."
        };

        return excludePatterns.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}

public class ServiceInfo
{
    public required string Name { get; init; }
    public required string DllPath { get; init; }
    public required string XmlPath { get; init; }
    public required bool IsExtension { get; init; }
    public required Dictionary<string, bool> FrameworkAvailability { get; init; }
}
