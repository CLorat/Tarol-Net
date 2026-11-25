# Files: uploads, blobs, downloads

Design note (Tatalu round 2, not built) for a package family `Tarol.Files`: binary content (photos, scanned proofs, generated exports and
PDFs) next to the event store, with size and type rules, a data subject, erasure and export, authorized downloads and signed links.
Decision ids `FI-*` (proposed). Labels as in the [conventions](conventions.md).

## 1. The need (Tatalu)

* **FB-F02.1-9**: no file storage. Profile photos (F02.1-R4) and proofs (power of attorney, medical certificate, vaccination record) need
  binary data with size/type limits, erasure and inclusion in the Art. 15 export. Tatalu **postponed the photo** (US-02.1.2, US-02.3.3);
  its draft: an own table `child_photos` (tenant, child, ≤ 512 KB, only `image/jpeg`/`image/webp`, resized in the browser), an own
  `ErasureParticipant`, a reaction on the withdrawal of the consent that deletes.
* **FB-F17.5-3**: no building block for a file download. Views answer JSON and the generated client has no download, so the tenant export
  is a string: `DataExport.Content` is a `[PersonalData] string` in the event `DataExport.Completed`
  (`backend/src/Tatalu.Plattform/Konto/Account.cs:44,74`), shown by `DataExportView.Content`
  (`Tatalu.Plattform/Konto/Changes.cs:92-97`) and turned into a Blob in the browser (`frontend/apps/web/src/screens/konto/Account.svelte`).
  The whole export JSON of a tenant thus lives in an event.

## 2. What Tarol has today

* Nothing for binaries. Change bodies are JSON read by the generated binder; `ChangeEndpoints.ReadBytes`/`ReadJson` read the body
  with a size limit (`src/Changes/Tarol.Changes.AspNetCore/ChangeEndpoints.cs:184-220`); views are written as JSON (`WriteView`, `:527`).
* Data protection: subject keys and the `tdp1.` envelope (`src/Persistence/Tarol.Persistence/DataProtection/Envelope.cs`), `IKeyStore`
  (`IKeyStore.cs`), `ErasureParticipant.Forget/ForgetTenant/Deleted` (`DataProtection/DataSubjects.cs`), `IDataSubjects.Export` returns
  one JSON document (`SubjectExport(Tenant, Subject, Utf8Json)`).
* Webhooks already verify HMAC-SHA256 signatures with `FixedTimeEquals` (rest.md §8), the pattern for signed links.

## 3. Options

| | Option | For | Against |
|---|---|---|---|
| A | Bytes in events (`[PersonalData] string` base64), today's export | nothing new | events grow by megabytes; every load and replay reads them; retention only per stream |
| B | **A blob port; events carry a `FileRef`** (id, type, size, hash); content encrypted with a file key sealed under the subject's key | events stay small; crypto-shredding covers blobs and their backups; one authorization model | a second store to back up and to clean |
| C | Leave it to each application (Tatalu's `child_photos`) | no framework work | every app re-implements limits, sniffing, erasure, export, authorization of downloads |

**Where the file enters.** (i) A separate upload (`POST /files` → id) referenced by a later change: two requests, orphans to expire, a file
whose subject is unknown at upload. (ii) **The change itself carries the file** (multipart or a raw body): the generated binder streams it
into the store, then runs the change; a failed change deletes the blob. Recommend (ii); (i) only for large files later.

## 4. Recommendation (FI-1 … FI-5)

**FI-1: option B with entry (ii). Packages `Tarol.Files` (port, `FileRef`, rules, endpoints, generator support in the AspNetCore
generator, in-memory store), `Tarol.Files.Npgsql` (a `files` table, content in `bytea` chunks), `Tarol.Files.S3` later (FI question 2).**

```csharp
[Endpoint("PUT /kinder/{child}/foto")]
public sealed partial record SetPhoto(Id<Child> Child,
    [File(MaxBytes = 512 * 1024, Types = ["image/jpeg", "image/webp"])] FileRef Photo) : IChange<Child>
{
    public Result Execute(Child child, ChangeContext context) => child.SetPhoto(Photo, context.Now);
}
public sealed record PhotoSet([property: PersonalData] FileRef? Photo) : Event;            // subject = the child (the stream)

[Endpoint("GET /kinder/{child}/foto", Restricted = true)]                                   // view-access.md VA-2
public sealed record ChildPhoto : IFileView<Child, ChildPhoto>
{
    public static Option<FileRef> From(Child child, ViewContext context) => child.Photo;
}
```

* `FileRef(Id<IFile> Id, string ContentType, long Length, string Sha256)`: a value object; stored in events like any other member.
* **FI-2: rules at the edge.** `[File]` sets the maximum size (413 beyond it, checked while streaming) and the allowed types; the type is
  **sniffed** from the first bytes (JPEG, PNG, WebP, PDF signatures), the client's `Content-Type` is only a hint (415 when they disagree).
  The OpenAPI document describes `multipart/form-data` (JSON part + file part) and, for a single file member, the raw body.
* **FI-3: subject and keys.** The file's subject is the subject of the `FileRef` member's `[PersonalData]` (the stream by default,
  `Subject = nameof(…)` otherwise), known from the change's target before the upload is stored. Content is AES-256-GCM in 64 KiB chunks
  under a random file key; the file key is sealed with the subject's key (`tdp1.`) in the `files` row. `EraseSubject` tombstones the key, so
  blob and backups are unreadable at once; a participant deletes the rows. A `FileRef` member without `[PersonalData]` is encrypted under
  the tenant's key (no subject).
* **FI-4: downloads.** `IFileView<T, TSelf>` (static `From` returning `Option<FileRef>`) gets a generated GET: load, authorize as a view
  (`Read<T>` plus the view, VA-2), stream with `Content-Type`, `Content-Length`, `ETag` = hash, `Content-Disposition` (`attachment` unless
  `Inline = true`), `X-Content-Type-Options: nosniff`, `Cache-Control: private`. **Signed links** for `<img src>` and downloads that cannot
  send a bearer token: a view may return `FileLink(Url, ExpiresAt)` from `IFileLinks.Link(fileRef, ttl)`, served by
  `GET /tarol/files/{id}?exp=…&sig=…` (HMAC-SHA256 over tenant, id, expiry; default 5 minutes; key from configuration with rotation like
  webhooks). The link is tenant-bound and does not need the principal.
* **FI-5: export, retention, generated files.** `IDataSubjects.Export` lists the subject's files (`FileRef` and where they occur);
  `ExportTenant` ([data-protection-round-2.md](data-protection-round-2.md)) writes a zip with the JSON and the files. Retention deletes the
  files of deleted streams (`ErasureParticipant.Deleted`). Generated documents (the tenant export, invoice PDFs) are written by effects
  through `IFileStore.Put(…, expires)` and served by an `IFileView`; FB-F17.5-3 then has no content in events. The browser side
  (`client.download(request)`, `x-tarol-download` in the OpenAPI document) is a Tarol-Svelte item.

```csharp
public interface IFileStore                               // the port
{
    ValueTask<Result<FileRef>> Put(OperationContext context, Guid? subject, Stream content, FileRules rules, DateTimeOffset? expires, CancellationToken ct);
    ValueTask<Option<Stream>> Open(OperationContext context, Id<IFile> id, CancellationToken ct);   // tenant-scoped, decrypted
    ValueTask Delete(OperationContext context, Id<IFile> id, CancellationToken ct);
}
```

## 5. Not part of this

* Image processing (resizing, thumbnails, EXIF stripping): the browser resizes; stripping EXIF is a later option (question 4).
* Virus scanning (a hook on `Put` could call one; no scanner ships).
* Resumable or chunked uploads, files above the size the edge allows (default 10 MB).
* Public, unauthenticated files.

## 6. Open questions for the owner

1. FI-1: entry through the change (recommended) only, or also a separate upload endpoint in the first version?
2. S3: build `Tarol.Files.S3` now (which client: AWS SDK under AOT **[unverified]**, or a minimal SigV4 client of our own) or Npgsql only?
3. FI-3: encrypt every file (recommended), or only files under `[PersonalData]`?
4. Strip EXIF/metadata of images on upload (privacy: GPS in photos), or leave it to the client?
5. FI-4: default lifetime of signed links, and are they allowed for `Sensitive` views (an access log entry per link creation or per fetch)?

## 7. Steps (one PR each)

1. **FI-A** `Tarol.Files`: `FileRef`, `IFileStore`, in-memory store, encryption and sniffing, unit tests (limits, wrong type, erased key).
2. **FI-B** AspNetCore generator: `[File]` members (multipart and raw body), `IFileView`, OpenAPI; endpoint tests (413, 415, 201/204,
   download headers, 304 by hash).
3. **FI-C** `Tarol.Files.Npgsql` (table, chunks, participant, retention), integration tests.
4. **FI-D** signed links, export of files, skill `tarol-files`, AOT smoke app.
5. **FI-E** (if decided) `Tarol.Files.S3`.
