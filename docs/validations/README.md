# Validations

Package guide: [Tarol.Validations](../../src/Validations/Tarol.Validations/README.md).
Agent skill: [tarol-validations](../../src/Validations/Tarol.Validations/skills/tarol-validations/SKILL.md).
Concept: [docs/architecture/validation.md](../architecture/validation.md). Review, measurements and migration notes:
[docs/migration/validations.md](../migration/validations.md).

## Design in five points

1. **Four kinds of rules, four places.** Value rules (the shape of an input) run when an input is parsed, before the
   aggregate is loaded, and report every field at once as `Invalid`. Aggregate invariants run after a change on the live
   path (step 6). Preconditions of a change are guard clauses in the behaviour, with `now` as a parameter, and stop at
   the first failure (`Error.Conflict`). Permissions are not validation (`Tarol.Security`).
2. **Each rule once, on a value object.** `public readonly partial record struct Title([Length(3, 80)] string Value);`
   Commands, events and aggregates use `Title`; nothing repeats the rule. A different limit is a different concept,
   named by its namespace (`Bidders.Name`, `Experts.Name`). A limit of one request (page size) sits on that input's
   parameter.
3. **Constructors trust, factories validate.** `Create` and `Validated()` are the live path; constructors and JSON
   reading never validate, so events recorded under older rules always load, and replay, snapshots and projections
   never run a rule.
4. **The type says what is required; the path says where.** `Option<T>` is optional, every other member is required.
   Errors carry the input path (`Lines[1].Quantity`), a rule code (`Range`) and the limits (`1..100`).
5. **Generated, allocation-free, AOT-ready.** The bundled generator writes the factories, checks, conversions,
   comparisons and JSON converters; the valid path allocates nothing; nine diagnostics (TVAL001–TVAL009) catch misuse
   at compile time, including the one JSON trap of source generators (a same-assembly context needs
   `ValueObjectJson.Options`).
