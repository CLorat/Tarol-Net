using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Probe.Changes.Gen;

var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Concat(Directory.GetFiles(AppContext.BaseDirectory, "Tarol.*.dll")).Concat([typeof(Tarol.Changes.ChangePipeline).Assembly.Location])
    .Distinct().Select(p => MetadataReference.CreateFromFile(p)).ToArray();
var parse = new CSharpParseOptions(LanguageVersion.Preview);
SyntaxTree Tree(string path, string text) => CSharpSyntaxTree.ParseText(text, parse, path);
var changesText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "input/Changes.cs"));
var domain = Tree("Domain.cs", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "input/Domain.cs")));
var compilation = CSharpCompilation.Create("Gen", [domain, Tree("Changes.cs", changesText)], refs, new(OutputKind.DynamicallyLinkedLibrary));

GeneratorDriver driver = CSharpGeneratorDriver.Create([new ChangesGenerator().AsSourceGenerator()], parseOptions: parse,
    driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
var fails = 0;
void Check(bool ok, string what, object? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}{(detail is null ? "" : $": {detail}")}");
    if (!ok) fails++;
}
string Reasons(GeneratorRunResult r, string step) => r.TrackedSteps.TryGetValue(step, out var s)
    ? string.Join(",", s.SelectMany(x => x.Outputs).Select(o => o.Reason).GroupBy(x => x).Select(g => $"{g.Key}x{g.Count()}")) : "-";
string Out(GeneratorRunResult r) => string.Join(",", r.TrackedOutputSteps.SelectMany(kv => kv.Value).SelectMany(x => x.Outputs).Select(o => o.Reason).GroupBy(x => x).Select(g => $"{g.Key}x{g.Count()}"));

driver = driver.RunGenerators(compilation);
var r1 = driver.GetRunResult().Results[0];
Console.WriteLine($"     run 1: {r1.GeneratedSources.Length} files: {string.Join(", ", r1.GeneratedSources.Select(s => s.HintName))}");
Check(r1.Diagnostics.IsEmpty, "no diagnostics", string.Join("; ", r1.Diagnostics));
Check(r1.GeneratedSources.Any(s => s.HintName == "CloseAuction.Change.g.cs"), "Target generated per change");

var c2 = compilation.AddSyntaxTrees(Tree("Other.cs", "namespace ArtFlow; public sealed class Unrelated { public int X => 1; }"));
driver = driver.RunGenerators(c2);
var r2 = driver.GetRunResult().Results[0];
Console.WriteLine($"     run 2 (unrelated file): models {Reasons(r2, "ChangeModels")}; outputs {Out(r2)}");
Check(!Out(r2).Contains("Modified") && !Out(r2).Contains("New"), "an unrelated file changes no output");

var c3 = c2.ReplaceSyntaxTree(c2.SyntaxTrees.Single(t => t.FilePath == "Changes.cs"),
    Tree("Changes.cs", changesText.Replace("=> auction.Close(context.Now);", "=> auction.Close(context.Now.AddSeconds(0));")));
driver = driver.RunGenerators(c3);
var r3 = driver.GetRunResult().Results[0];
Console.WriteLine($"     run 3 (edit a method body in Changes.cs): models {Reasons(r3, "ChangeModels")}; outputs {Out(r3)}");
Check(!Out(r3).Contains("Modified"), "editing Execute's body changes no output (the model holds no bodies)");

var c4 = c3.ReplaceSyntaxTree(c3.SyntaxTrees.Single(t => t.FilePath == "Changes.cs"),
    Tree("Changes.cs", changesText.Replace("public sealed partial record SettleAuction(Id<Auction> Auction)", "public sealed partial record SettleAuction(Id<Auction> Auction, Id<Auction> Other)")));
driver = driver.RunGenerators(c4);
var r4 = driver.GetRunResult().Results[0];
Console.WriteLine($"     run 4 (two Id<Auction> members): {string.Join("; ", r4.Diagnostics.Select(d => d.GetMessage()))}");
Check(r4.Diagnostics.Any(d => d.Id == "TCHG001"), "TCHG001 when the target is ambiguous");

var dispatch = r1.GeneratedSources.Single(s => s.HintName == "Changes.Dispatch.g.cs").SourceText.ToString();
Console.WriteLine("\n----- Changes.Dispatch.g.cs (run 1) -----\n" + dispatch);
Console.WriteLine("----- CloseAuction.Change.g.cs -----\n" + r1.GeneratedSources.Single(s => s.HintName == "CloseAuction.Change.g.cs").SourceText);
Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;
