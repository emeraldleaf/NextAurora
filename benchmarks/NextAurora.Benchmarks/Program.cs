using BenchmarkDotNet.Running;

// Entry point — runs all benchmarks in the assembly.
// Usage from the repo root:
//   dotnet run -c Release --project benchmarks/NextAurora.Benchmarks
//
// To filter to a single benchmark class:
//   dotnet run -c Release --project benchmarks/NextAurora.Benchmarks -- --filter '*OrderFactory*'
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

internal sealed partial class Program;
