using System.Diagnostics;
using DocFxGenerator.Configuration;
using DocFxGenerator.Orchestration;

var options = GeneratorOptions.ParseArgs(args);

if (options == null)
{
    GeneratorOptions.PrintUsage();
    return 1;
}

if (options.Verbose)
{
    Console.WriteLine($"Assemblies root: {options.AssembliesRoot}");
    Console.WriteLine($"Output folder: {options.OutputFolder}");
    Console.WriteLine($"Services: {string.Join(", ", options.Services)}");
    Console.WriteLine($"Max parallelism: {options.MaxParallelism}");
    Console.WriteLine($"Batch threshold: {options.BatchThreshold}");
}

var stopwatch = Stopwatch.StartNew();

var generator = new GenerateCommand(options);
var exitCode = await generator.ExecuteAsync();

stopwatch.Stop();
Console.WriteLine($"Documentation generation completed in {stopwatch.Elapsed.TotalSeconds:F1}s");

return exitCode;
