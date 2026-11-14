# Probe: language and JSON facts for state types (stage 1)

Console app, SDK 11.0.100, `LangVersion=preview`, reflection-based JSON for the two `J.Run` lines and the
source-generated `Ctx` for the rest. Rename `facts.csproj.txt` to `facts.csproj` (and `editorconfig.txt` to
`.editorconfig` for the second build), then `dotnet build` and `dotnet run`. Output: [`output.txt`](output.txt).

What it shows (used in [state-transitions.md](../../../../architecture/state-transitions.md)):

* An enum `switch` that misses a named member is CS8509; one that covers every named member is still CS8524 (an
  unnamed value such as `(AuctionStatus)3`). With CS8524 configured to `none`, CS8509 still reports a missing member.
* `closed enum` does not exist in C# 15 (CS0106). `closed record` hierarchies and `union` types give a warning-free
  exhaustive `switch`.
* An enum member's attribute can reference sibling members unqualified (`[To(Closed)] Open`).
* JSON: an undefined enum number (`7`) deserializes; an unknown enum name (`"Reopened"`) throws `JsonException`; a name
  pinned with `[JsonStringEnumMemberName("settled")]` no longer reads the CLR name `"Settled"`; reading is
  case-insensitive. A `closed` hierarchy without `[JsonPolymorphic]` serializes as `{}` (data lost); a `union` writes the
  case's own payload with no discriminator.
