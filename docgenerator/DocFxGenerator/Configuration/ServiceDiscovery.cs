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

            var dlls = Directory.GetFiles(frameworkPath, "AWSSDK.*.dll")
                .Where(f => !IsExcludedAssembly(Path.GetFileName(f)))
                .OrderBy(f => f);

            foreach (var dllPath in dlls)
            {
                var fileName = Path.GetFileNameWithoutExtension(dllPath);
                var serviceName = ExtractServiceName(fileName);
                if (serviceName == null) continue;

                var xmlPath = Path.ChangeExtension(dllPath, ".xml");
                if (!File.Exists(xmlPath)) continue;

                if (serviceMap.ContainsKey(serviceName))
                    continue;

                serviceMap[serviceName] = new ServiceInfo
                {
                    Name = serviceName,
                    DllPath = dllPath,
                    XmlPath = xmlPath,
                    FrameworkAvailability = GetFrameworkAvailability(serviceName)
                };
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

    public string? GetDllPathForFramework(ServiceInfo service, string framework)
    {
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

        var cborRef = Path.Combine(frameworkPath, "AWSSDK.Extensions.CborProtocol.dll");
        if (File.Exists(cborRef))
            references.Add(cborRef);

        var systemCborRef = Path.Combine(frameworkPath, "System.Formats.Cbor.dll");
        if (File.Exists(systemCborRef))
            references.Add(systemCborRef);

        return references;
    }

    private Dictionary<string, bool> GetFrameworkAvailability(string serviceName)
    {
        var availability = new Dictionary<string, bool>();
        foreach (var framework in _options.TargetFrameworks)
        {
            var dllPath = Path.Combine(_options.AssembliesRoot, framework, $"AWSSDK.{serviceName}.dll");
            availability[framework] = File.Exists(dllPath);
        }
        return availability;
    }

    private static string? ExtractServiceName(string fileName)
    {
        const string prefix = "AWSSDK.";
        if (!fileName.StartsWith(prefix))
            return null;

        var serviceName = fileName[prefix.Length..];

        if (serviceName == "Core")
            return serviceName;

        if (serviceName.StartsWith("Extensions."))
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
    public required Dictionary<string, bool> FrameworkAvailability { get; init; }
}
