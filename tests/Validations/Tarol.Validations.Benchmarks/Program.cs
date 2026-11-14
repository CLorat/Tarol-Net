using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Tarol.Validations.Benchmarks.ValidationBenchmarks).Assembly).Run(args);
