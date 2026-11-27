---
name: tarol-application-layout
description: >-
  Use when creating, finding or moving code in a Tarol application: one project per bounded context, one folder per
  aggregate, file names Name.Role.cs (.Aggregate, .Events, .Entity, .Change, .Reaction, .Job, .Effect, .View, .Projection,
  .Values, .Value, .Support, .Rules, .Security, .Json, .Port, .Adapter, .DomainService, .Contract) and which moves keep stored
  event names, stream types, reaction names and OpenAPI schemas. Do not rename types to fit file names, do not move code
  between projects, and do not decompile or web-search for the layout.
---
# Tarol application layout

## Rules

1. **One project = one bounded context = one Tarol module** (`[assembly: GenerateTarolModule("…")]` in `<Context>.Module.cs`).
   Generators ignore folders and file names, so the layout is free inside a project. Never move code to another project as
   part of a layout change: that is another module.
2. **One folder per aggregate**, named as its plural (`MediaItems/`). `Shared/` holds types no single aggregate owns;
   `Contracts/` holds what exists for other contexts (published topics, ports they call, helpers no aggregate owns);
   `Projections/` rows built only from other contexts' events. At the root: `<Context>.Module.cs`, `<Context>.Json.cs`,
   `<Context>.Security.cs`.
3. **Folders are not namespaces.** Every file keeps the context's one namespace (`namespace Billing;`); existing
   sub-namespaces that others import stay, each file keeping the namespace of its declarations.
4. **File name = `Name.Role.cs`**, PascalCase. Single roles are named after their type, group roles after the aggregate:

   | Single (one type per file) | Group (one file per aggregate) |
   |---|---|
   | `.Aggregate` (root: state, factory, behaviour, invariants), `.Entity`, `.Lifecycle`, `.Change`, `.Reaction` (`[OnEvent<T>]`), `.Job` (`[Scheduled]`, `IJob`), `.Effect` (`IEffect<T>`), `.View` (`IView<T, TView>` + records only it uses), `.Projection` (`[Projection]` row + sort enum + queries), `.Value` (value object with rules), `.DomainService`, `.Port`, `.Adapter`, `.Contract` | `.Events` (partial root: nested events + `On`), `.Values` (plain enums/records/constants of state and events), `.Support` (marker interfaces, load helpers, inputs, notice texts), `.Rules` (row rules), `.Security` (role map), `.Json` (partial part of the context's JSON class), `.Endpoints` (hand-written endpoints) |

5. **One change per file, always**, reactions and jobs included.
6. **A change lives in the folder of the aggregate it changes** (`T` of `IChange<T>`/`ILoads<T>`), even if it reacts to another
   aggregate's event. If `T` is in another context (a notification, an audit trail), it lives with the aggregate whose
   event starts it.
7. **Events stay nested** in their root or entity; put the root's events in `X.Events.cs` (`public partial class X`).
8. **File names follow types, never the reverse.** Renaming a type changes stored or published names (table below).
9. **A large root splits by topic, topic after the role:** `X.Aggregate.<Topic>.cs`, `X.Values.<Topic>.cs`. The generated
   state writer follows the file order of the parts; keep it (check `obj/**/generated` after naming a part).

## Example: an aggregate folder with one entity and one change

```
Gallery/
  Gallery.Module.cs        [assembly: Tarol.Changes.GenerateTarolModule("Gallery")]
  Gallery.Json.cs          [JsonSourceGenerationOptions(...)] public sealed partial class GalleryJson : JsonSerializerContext;
  MediaItems/
    MediaItem.Aggregate.cs
    MediaItem.Events.cs
    MediaComment.Entity.cs
    AddComment.Change.cs
    MediaItem.Json.cs
```

```csharp
// MediaItems/MediaItem.Aggregate.cs
namespace Gallery;
public partial class MediaItem : AggregateRoot<MediaItem>
{
    public string Caption { get; private set; } = "";
    public EntitySet<MediaComment> Comments { get; } = [];
    public static Result<MediaItem> Upload(string caption, DateTimeOffset now)
    {
        var item = new MediaItem(Id<MediaItem>.New(now));
        item.Raise(new Uploaded(caption));
        return item;
    }
    public Result<MediaComment> Comment(string text) => Raise(new MediaComment.Added(text));
}

// MediaItems/MediaItem.Events.cs
namespace Gallery;
public partial class MediaItem
{
    public sealed record Uploaded(string Caption) : Event;      // stored as "media_item.uploaded"
    private void On(Uploaded e) => Caption = e.Caption;
}

// MediaItems/MediaComment.Entity.cs
namespace Gallery;
public partial class MediaComment : Entity<MediaItem, MediaComment>
{
    public string Text { get; private set; } = "";
    public Success Remove() => Raise(new Removed());
    public sealed record Added(string Text) : Created;          // "media_item.media_comment.added"
    public sealed record Removed : Deleted;
    private void On(Added e) => Text = e.Text;
}

// MediaItems/AddComment.Change.cs
namespace Gallery;
[Endpoint("POST /media/{mediaItem}/comments")]
public sealed partial record AddComment(Id<MediaItem> MediaItem, string Text) : IChange<MediaItem>, IValidatable
{
    public Result Execute(MediaItem aggregate, ChangeContext context) => aggregate.Comment(Text).ToResult();
}

// MediaItems/MediaItem.Json.cs: this aggregate's entries of the one context
[JsonSerializable(typeof(MediaItem.Uploaded)), JsonSerializable(typeof(MediaComment.Added)), JsonSerializable(typeof(MediaComment.Removed))]
[JsonSerializable(typeof(AddComment))]
public sealed partial class GalleryJson;
```

Usings (`Tarol.Primitives`, `Tarol.Results`, `Tarol.StronglyTypedId`, `Tarol.Changes`, `Tarol.Changes.AspNetCore`,
`Tarol.Validations`, `System.Text.Json.Serialization`) are left out above.

## What a move or rename breaks

| You do | Result |
|---|---|
| move a file or a declaration inside the project, or an event to another `partial` part of the same type | nothing changes (safe) |
| move an event out of its entity, or into another nested type | alias changes (`media_item.media_comment.added` → `media_item.added`): stored events no longer load. Pin with `[EventType("media_item.media_comment.added")]` |
| rename an event | same as above |
| rename the aggregate | stream type and all aliases change; pin `[StreamType("media_item")]` and each `[EventType]` |
| rename a change with `[OnEvent<T>]` or `[Scheduled]` | the reaction/job name is the type name: waiting outbox rows of the old name dead-letter, a job starts a new row. Pin `Name = "Old"` |
| rename a view, row, input or enum | OpenAPI schema and the generated client type change; two types of one name throw when the document is built. Pin `[OpenApiSchemaName("Old")]` |
| move a type to another project | another module; TPRM035 if its JSON entry stays behind, the reaction leaves `GeneratedReactions.Set` |
| leave a `[JsonSerializable]` entry behind in the wrong part | nothing: all parts are one context. TPRM035 reports a missing entry, TPRM036 one generated anyway |

Every rename needs a pinned name and the owner's OK.

## Moving an existing context

1. Build with `-p:EmitCompilerGeneratedFiles=true` and keep `obj/**/generated`; keep the OpenAPI document and the client.
2. Commit 1: `git mv` each file unchanged to its aggregate's folder (history follows). Commit 2: move declarations into role
   files, copying text; add only `partial class X { }` around events and the needed usings.
3. Prove: 0 warnings, all tests, `diff -r` of the generated trees empty, OpenAPI and client without diff.

## AOT

The layout changes no generated code, so it changes no AOT behaviour. Keep every `[JsonSerializable]` in the project whose
types it lists; source-generated JSON has no reflection fallback.
