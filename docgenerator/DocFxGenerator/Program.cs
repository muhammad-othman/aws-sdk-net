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

var generator = new GenerateCommand(options);
return await generator.ExecuteAsync();
