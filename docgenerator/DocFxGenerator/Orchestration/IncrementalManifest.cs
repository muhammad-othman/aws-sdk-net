using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocFxGenerator.Configuration;

namespace DocFxGenerator.Orchestration;

public class IncrementalManifest
{
    private const string ManifestFileName = ".generation-manifest.json";

    private readonly GeneratorOptions _options;
    private readonly string _manifestPath;
    private ManifestData _data;

    public IncrementalManifest(GeneratorOptions options)
    {
        _options = options;
        _manifestPath = Path.Combine(Path.GetFullPath(options.IntermediateFolder), ManifestFileName);
        _data = Load();
    }

    public bool HasServiceChanged(ServiceInfo service)
    {
        var currentHash = ComputeServiceHash(service);

        if (_data.Services.TryGetValue(service.Name, out var entry) && entry.Hash == currentHash)
            return false;

        return true;
    }

    public void RecordService(ServiceInfo service)
    {
        _data.Services[service.Name] = new ServiceEntry
        {
            Hash = ComputeServiceHash(service),
            GeneratedAt = DateTime.UtcNow
        };
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);
        var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_manifestPath, json);
    }

    private ManifestData Load()
    {
        if (!File.Exists(_manifestPath))
            return new ManifestData();

        try
        {
            var json = File.ReadAllText(_manifestPath);
            return JsonSerializer.Deserialize<ManifestData>(json) ?? new ManifestData();
        }
        catch
        {
            return new ManifestData();
        }
    }

    private string ComputeServiceHash(ServiceInfo service)
    {
        using var sha = SHA256.Create();
        using var stream = new MemoryStream();

        // Hash the DLL
        if (File.Exists(service.DllPath))
        {
            var dllBytes = File.ReadAllBytes(service.DllPath);
            stream.Write(dllBytes);
        }

        // Hash the XML doc
        if (File.Exists(service.XmlPath))
        {
            var xmlBytes = File.ReadAllBytes(service.XmlPath);
            stream.Write(xmlBytes);
        }

        stream.Position = 0;
        var hashBytes = sha.ComputeHash(stream);
        return Convert.ToHexString(hashBytes);
    }
}

public class ManifestData
{
    [JsonPropertyName("services")]
    public Dictionary<string, ServiceEntry> Services { get; set; } = new();
}

public class ServiceEntry
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; }
}
