# Step 5: Validations

Origin: `CLorat/Tarol@6a62f81`: `src/Validations/{Validations,Validations.Value,Validations.Invariant}`,
`gen/Validations/{Validations.Value.Generators,Validations.Invariant.Generators,Validations.Generators}`,
`Validations.Prerequisite/` (repository root), `tests/Validations/*`. Old review: `docs/review/validations-and-changes.md`
(findings 1–32, §2.3, §4, §7) and `docs/review/ROADMAP.md` Phase 3.

**Status: concept review (stage 1) done; evaluation (stage 2) waits for the owner's scope answers.**

The concept review is in [validation.md](../architecture/validation.md); this note will reference it and not repeat it.
Probes are in [`probes/validations/`](probes/validations/) (`*.csproj.txt`: copy a folder **outside** the repository,
rename to `.csproj`, pass `-p:TarolNetRepo=<clone>`). Environment: SDK 11.0.100, .NET 11.0.0, linux-x64.

| Probe | What it shows | Command |
|---|---|---|
| `mev/` | `Microsoft.Extensions.Validation` 11.0.0 without ASP.NET Core: paths, codes, `Option<T>`, `IValidatableObject`, ReDoS, allocations; JIT and Native AOT | `dotnet run -c Release`; `dotnet publish -c Release -r linux-x64` |

Stage 2 (inventory, experiments, proposed API) follows here after the scope questions in
[validation.md § Open questions](../architecture/validation.md#open-questions) are answered.
