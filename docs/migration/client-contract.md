# Client contract: evaluation (the browser client's backend findings)

The design session of `CLorat/Tarol-Svelte` (the Svelte client) read Tarol-Net `main` at `73b35ff` and ran the
`samples/Sagas` AppHost. Its list of backend bugs and gaps is Tarol-Svelte `docs/backend/changes.md` (BC-1 … BC-17). This
note checks each finding against the code, proposes the fix and records the owner's decisions (**CC-**). Each fix is its
own pull request; §4 lists them, and §5 onward records what each PR changed and how it was verified.

Labels as in the rest of the migration notes: **[verified]** reproduced (a failing test or the running sample),
**[verified by reading]** checked in the code at `73b35ff`, **[unverified]**.

## 1. Findings and proposed fixes

### Part A: bugs

| # | Finding | Check | Proposed fix |
|---|---|---|---|
| BC-6 | The list ETag is a hash over the page's (id, version) pairs (`ListQuery.cs:286`, `Cursor.ETag`). It ignores `next` and `total`: a row appended behind a full last page turns `next` from `null` into a cursor and the client still gets a 304. | **[verified by reading]**; a failing test in PR A1 | hash `next` and `total` too (CC-3) |
| BC-7 | The view's `If-None-Match` compares verbatim (`ChangeEndpoints.cs:394`), search too (`SearchEndpoints.cs:133`); lists use a weak, list-aware match (`QueryEndpoints.cs:286-303`). `W/"5"` or `"4", "5"` on a view answer 200. | **[verified by reading]** | one RFC 9110 §13.1.2 matcher (weak comparison, comma lists, `*`) for views, lists and search |
| BC-8 | Search snippets are raw text with `<b>` markers in every provider: in-memory (`InMemorySearchIndex.cs:216`), Npgsql (`ts_headline … StartSel=<b>`), OpenSearch (`pre_tags`), Typesense (`highlight_start_tag`). A `<script>` in the text reaches the client unescaped. | **[verified by reading]** | plain text plus highlight offsets (CC-1) |
| BC-10 | `ErasureIncompleteException` escapes `POST {admin}/subjects/{id}/erase` as a 500 (`DataSubjects.cs:90-92`, `AdminEndpoints.cs:201-211`); every admin error is `{"error":"<text>"}` (`OperationsJson.cs:35`, `AdminEndpoints.cs:72`). | **[verified by reading]** | 409 `DataProtection.ErasureIncomplete` with the failed participants (the re-run stays idempotent: the key is a tombstone); every admin error `application/problem+json` with `code` |
| — | `samples/Sagas/apphost.sh:69-71` calls export and erase without `?tenant=`; since the tenant fix (`aca4182`) they answer 400 (`AdminEndpoints.cs:180-185`). data-protection.md §18.3 still shows the run before that fix. | **[verified by reading]** | pass `?tenant=00000000-0000-0000-0000-000000000000` (the bidder profile is tenant-less); note §18.3's run predates the fix |
| BC-16 | OpenAPI lists 415 for changes, but `ChangeEndpoints.ReadBytes` (155-171) and the generated binders parse any body as JSON. | **[verified by reading]** | 415 for a non-empty body whose `Content-Type` is not `application/json` or `*/*+json` |
| BC-17 | 422 `Idempotency.KeyReused` carries the detail `http:User:<sub>:POST /auctions: <key>`. | **[verified]** by the client session against the sample | the detail names the key only; the scope stays in the log |
| — | The step 17 lesson "403 on publish: the package name is taken by another repository" is not on `main`. | **[verified by reading]** (`release.md`) | add it to `docs/migration/release.md` |

### Part B: what blocks a browser client

| # | Finding | Check | Proposed fix |
|---|---|---|---|
| BC-1 | No response carries `Access-Control-*`. | **[verified]** by the client session (`curl -H 'Origin: …' :5080/auctions`) | `TarolHeaders.Exposed` / `TarolHeaders.Allowed` in `Tarol.Changes.AspNetCore`, an `AddCors` recipe in its README, used in the sample; a preflight test |
| BC-2 | `AddZitadel` never reads `access_token` from the query; browsers cannot set headers on a WebSocket. | **[verified by reading]** (`ZitadelTarolBuilderExtensions.cs:36-43`) | `ZitadelOptions.HubPath` (CC-2): `OnMessageReceived` reads the query token only for that path; `CloseOnAuthenticationExpiration` on the hub (BC-15); a real-JWT WebSocket test |
| BC-3 | The OpenAPI document has empty problem responses, no operation kind, no id types, no Tarol headers, numbers as `["number","string"]` + pattern in responses, `Option<T>` not nullable, and neither search nor admin. | **[verified by reading]** (`OpenApiEmitter.cs`), numbers **[verified]** by the client session | `TarolProblem` component, `x-tarol-kind`, `x-tarol-id`, `x-tarol-errors`, the headers, `number` in responses, nullable options, search and admin in the document; emitter snapshot tests |
| BC-9 | The sample cannot drive a browser e2e: no user-agent app, no human users, no hub, no validation rule on a change, no CORS, no short token, no "stay up" mode, not built in CI. | **[verified by reading]** | as listed in BC-9; the sample built in CI (conventions §4) |

### Part C: improvements (owner chose to build all of them, CC-4)

| # | Proposal |
|---|---|
| BC-4 | `x-tarol-rules: [{rule, code, min?, max?, pattern?, flags?}]` for class and multi-field value objects, `IValidatable` parameters, `Count`, `IgnoreCase`; patterns checked to be ECMAScript-compatible by a TVAL diagnostic. |
| BC-5 | `errorDetails: {path: [{code, detail?}]}` next to `errors` on a 400 (`FieldError.Detail` is dropped today, `ChangeEndpoints.cs:339-341`). |
| BC-11 | `Tarol-Request-Id` in every answer and `requestId` in every problem (the trace id when there is an activity, else `HttpContext.TraceIdentifier`). |
| BC-12 | Hub errors as `<code>: <detail>` with documented codes (`Hub.Forbidden`, `Hub.Topic`, `Auth.Tenant`) and a constant class. |
| BC-13 | `GET /me`: subject, tenant, roles of the caller. |
| BC-14 | An unknown list query key answers 400 (field code `Unknown`). |

## 2. Decisions asked of the owner

| # | Question | Answer (2026-11-19) |
|---|---|---|
| CC-1 | Search snippets: (a) plain text + `highlights: [{start, length}]` (UTF-16 offsets, the client never needs `{@html}`), or (b) HTML-escaped text that keeps only `<b>` | **(a) text + offsets** (recommended) |
| CC-2 | Where the query `access_token` is read: (a) an `AddZitadel` option `HubPath` (default `/tarol/messages`, `null` = off) plus BC-15 in the same PR, or (b) also a generic helper in `Tarol.Security` | **(a) the `AddZitadel` option** (recommended) |
| CC-3 | What the list ETag hashes besides the rows: (a) `next` and `total`, or (b) the projection position | **(a) `next` and `total`** (recommended) |
| CC-4 | Which Part C items to build in this session | **all**: BC-5, BC-14, BC-11 + BC-12 + BC-13, BC-4 |
| CC-5 | (found in B3) An `Option<T>` member with a parameter default breaks ASP.NET's OpenAPI schema export: (a) a TWEB diagnostic on such a member of an entry, (b) work around it in the schema transformer, (c) leave it | **(a) a TWEB diagnostic** (recommended) |
| CC-6 | (BC-11) The request id: (a) `Tarol-Request-Id` (the trace id, else `TraceIdentifier`) on every answer and `traceId` in problems, or (b) only the draft `traceresponse` header | **(a) `Tarol-Request-Id` + `traceId`** (recommended) |
| CC-7 | (BC-12) Hub errors: (a) `Hub.Forbidden`, `Hub.Topic`, `Auth.Tenant` as a documented `<code>: <detail>` contract with constants (breaking for `Forbidden:` matchers), or (b) keep the prefixes and document them | **(a) `Hub.*` codes** (recommended) |
| CC-8 | (BC-13) `GET /me`: (a) opt-in `MapTarolMe("/me")`, or (b) always mapped by `MapChanges` | **(a) opt-in `MapTarolMe`** (recommended) |

Defaults taken without a question (recommendations; the owner may revise them in review):
* BC-10's status for an incomplete erasure is 409 (`DataProtection.ErasureIncomplete`), as the session prompt suggests: the
  request conflicts with a participant's state and can be repeated. Other admin codes: `Admin.Query` (400), `Admin.Kind`
  (404 unknown dead-letter kind), `Admin.NotFound` (404), `Admin.Tenant` (400), `Projection.Drop` (409).
* BC-16 treats a missing `Content-Type` on a non-empty body as not JSON (415). An empty body (a change without input) needs none.

## 3. Behaviour changes for clients (summary)

Every PR lists its own; together, for a client written against `73b35ff`:
* search hits: `snippet` is plain text, highlights are in `highlights` (BC-8);
* admin errors are `application/problem+json` with `code`; an incomplete erasure is 409, not 500 (BC-10);
* a change with a non-JSON body is 415 (BC-16); `Idempotency.KeyReused`'s detail is the key only (BC-17);
* views and search answer 304 for a weak or listed `If-None-Match` (BC-7); a list answers 200 when `next` or `total` changed
  (BC-6).

## 4. Pull requests

Stacked: each PR's base is the one before it; retarget each to `main` before its base is merged.

| PR | Items | § |
|---|---|---|
| #91 | this note; the Changes integration tests compile again | 1–4 |
| #92 | BC-6 list ETag | 5 |
| #93 | BC-7 `If-None-Match` | 6 |
| #94 | BC-8 search snippets | 7 |
| #95 | BC-10 admin problems | 8 |
| #96 | BC-16, BC-17, the AppHost tenant, the release lesson | 9 |
| #97 | the erasure's search participant in the API process (found) | 10 |
| #98 | BC-1 CORS | 11 |
| #99 | BC-2 hub token, BC-15 | 12 |
| #100 | BC-3 OpenAPI | 13 |
| #101 | BC-9 sample for a browser e2e, `ZitadelOptions.ClockSkew` | 14 |
| #102 | BC-5 `errorDetails`; CC-5 … CC-8 recorded | 15 |
| #103 | BC-14 unknown list keys | 16 |
| #104 | BC-11 request id | 17 |
| #105 | BC-12 hub error codes | 18 |
| #106 | BC-13 `GET /me` | 19 |
| #107 | TWEB007 (CC-5) | 20 |
| #108 | BC-4 `x-tarol-rules`, TVAL010 | 21 |

The reply for the client is Tarol-Svelte `docs/backend/tarol-net-reply.md`.

## 5. PR A1: the list ETag covers `next` and `total` (BC-6)

* **Change**: `ListQuery<TRow>.ToPage` (shared by the in-memory store and `Tarol.Queries.Npgsql`) builds the page's ETag with the
  new `Cursor.ETag(rows, next, total)`: the (id, version) pairs as before, then `next` (or a marker for none) and the total (or
  a marker for "not counted"). `Cursor.ETag(rows)` stays as it was.
* **Behaviour change**: a list answers 200 instead of 304 when only `next` or `total` changed. Every page's ETag value changes
  once with the upgrade (cached pages revalidate once). A counted and an uncounted request of the same page have different tags.
* **Tests** (red at `2dda57f`, green after the fix **[verified]**): `QueryTests.A_row_behind_a_full_last_page_changes_the_page_ETag`,
  `QueryTests.A_changed_total_changes_the_page_ETag`, and over HTTP
  `QueryEndpointTests.A_row_appended_behind_a_full_last_page_answers_200_with_a_next_cursor` (`If-None-Match` with the old tag
  answers 200 with a non-null `next`).
* **Public API**: `+ static Cursor.ETag(IEnumerable<(Guid Id, long Version)>, string?, long?)` (generated by `eng/public-api.py update`).
* **Suppressions**: none.

## 6. PR A2: one `If-None-Match` matcher (BC-7)

* **Change**: `EntityTags.Matches` (internal, `Tarol.Changes.AspNetCore`) implements RFC 9110 §13.1.2: `*`, a comma-separated
  list, the weak comparison (`W/` ignored on both sides), commas inside an opaque tag kept. The view (`ChangeEndpoints.Load`),
  lists (`QueryEndpoints`, whose private matcher it replaces) and search (`SearchEndpoints`) use it.
* **Behaviour change**: a view or search answers 304 for `W/"<tag>"` and for a list that holds the tag in any position
  (before: only for the exact header value or `*`). A view's own tag stays strong (`"<version>"`).
* **Tests**: `EndpointTests.A_view_matches_if_none_match_weakly_and_in_lists` (6 cases) and the search 304 test with a weak
  tag, a list, `*` and a mismatch. Red before the fix **[verified]**: the view with `W/"2"` and with `"1",W/"2"` answered 200,
  and search with the tag without `W/` answered 200. (`"1", "2"` passed already: the test host splits header values at
  `", "`.)
* **Public API**: none. **Suppressions**: none.

## 7. PR A3: search snippets as text and offsets (BC-8, CC-1)

* **Change**: every provider marks matched words with two private-use characters (`SearchSnippets.Open` U+E000,
  `SearchSnippets.Close` U+E001) instead of `<b>`/`</b>`: the in-memory index, Npgsql (`ts_headline … StartSel/StopSel`),
  OpenSearch (`pre_tags`/`post_tags`) and Typesense (`highlight_start_tag`/`highlight_end_tag`). `SearchSnippets.Parse` turns
  the engine's answer into plain text and `SearchHighlight(Start, Length)` ranges (UTF-16 code units, a JavaScript string's
  indexes); unbalanced or empty markers are dropped, adjacent ranges merge. `SearchHit.Highlights` (an `init` property, so the
  record's constructor and deconstruction stay as they were) carries them.
* **Wire (breaking)**: a hit of `GET /search?…&snippets=true` is `{…, "snippet": "<plain text>", "highlights": [{"start": n,
  "length": n}]}`. The client renders the text escaped and marks the ranges; it never needs `{@html}`.
* **Why private-use characters**: an engine highlights inside the text it stores, so the markers must be characters no
  document contains. A document that does contain U+E000/U+E001 gets a spurious (harmless, text-only) highlight; nothing
  becomes markup.
* **Tests**: the shared `SearchContract.A_snippet_is_plain_text_and_markup_in_the_document_stays_text` (a title and body with
  `<script>`, `<b>` and `<img onerror>`) for **in-memory, Npgsql, OpenSearch 2.19.1 and Typesense 28.0** (25 / 21 / 21
  contract tests, 0 failed **[verified]**); `A_snippet_marks_the_matched_word` now checks the highlight ranges;
  `SnippetTests` (parser: offsets, UTF-16, unbalanced markers); over HTTP `SearchEndpointTests.A_snippet_is_plain_text_with_highlight_offsets`.
  Red before the fix: the contract asserted the `<b>` markup (`hit.Snippet.ShouldContain("<b>")`), i.e. the raw markup was the
  specified behaviour; the new tests do not compile against `73b35ff` (no `Highlights`).
* **AOT smoke**: the four search smoke apps checked `<b>` in the snippet (found by the first full `eng/aot-smoke.sh` run of this
  branch: `Tarol.Search.AotSmoke` and `Tarol.Search.Npgsql.AotSmoke` failed their checks). They check the plain text and the
  highlight ranges now; all four publish with 0 IL warnings and pass natively, OpenSearch 2.19.1 and Typesense 28.0 included
  (`TAROL_OPENSEARCH`, `TAROL_TYPESENSE`) **[verified]**.
* **Public API** (`Tarol.Search`, generated): `SearchHit.Highlights`, `SearchHighlight`, `SearchSnippets` (`Open`, `Close`,
  `Parse`, `Hit`). `eng/public-api.py update` had to run twice: the first run, while `SearchHighlight` was still undeclared,
  dropped `SearchHit`'s record members; the second restored them (worth knowing for the script, not fixed here).
* **Suppressions**: none.

## 8. PR A4: admin problems and the incomplete erasure (BC-10)

* **Change**: every error of `MapTarolAdmin` is an RFC 9457 `application/problem+json` (`type`, `status`, `title` = `code`,
  `code`, `detail`), written directly like the change endpoints' fallback. Codes in `AdminProblems`: `Admin.Query` (400 a
  malformed `after`/`limit`/`tenant`/`max`), `Admin.Tenant` (400 a data-subject call without a valid `tenant`), `Admin.Kind`
  (404 an unknown dead-letter kind), `Admin.NotFound` (404 no dead letter with that id: had no body), `Projection.Drop` (409),
  `DataProtection.ErasureIncomplete` (409 with `tenant`, `subject` and `failed`).
* **The erasure**: `Tarol.Operations` does not reference `Tarol.DataProtection`, so it cannot catch
  `ErasureIncompleteException` by type. `Tarol.Persistence` (where `IDataSubjects` lives) gets `IIncompleteErasure` (`Tenant`,
  `Subject`, `Failed`), and the exception implements it; nothing else changes on it. The re-run is idempotent as before (the key
  is a tombstone, the forgetting runs again): the test's second call answers 200 with `alreadyErased: true`.
* **New**: `GET {prefix}/dead` lists the registered kinds (from BC-10's list; the admin UI needs it to build its menu).
* **Behaviour change (breaking on the wire)**: `{"error":"<text>"}` becomes a problem. `AdminError` and
  `OperationsJson.AdminError` stay public (shipped in 0.1.0) but are no longer written; they can go in the next major.
* **Tests**: `AdminTests.Every_admin_error_is_a_problem_with_a_code` (8 errors), `An_incomplete_erasure_is_409_with_the_failed_participants_and_a_rerun_completes_it`,
  `The_dead_letter_kinds_are_listed`, the projection-drop test reads the problem's `detail`; `DataSubjectsTests` checks the
  interface. Red with the old endpoints **[verified]**: 4 of 19 failed, the erasure's exception escaped the endpoint (an
  unhandled 500 in a real host), the kinds path did not exist, the errors were `{"error"}`.
* **Public API** (generated): `+ AdminProblems` (6 constants), `+ OperationsJson.IReadOnlyListString`, `+ IIncompleteErasure`.
* **Suppressions**: none.

## 9. PR A5: 415, the key detail, the AppHost tenant, the release lesson

* **BC-16, 415**: the finding is partly right. Routing (`Accepts` metadata) already answers 415 for another media type
  (`text/plain`, the existing `A_body_that_is_not_json_is_415_from_routing_…` test), but a body **without** `Content-Type`
  passed routing and was parsed as JSON (201). `ChangeEndpoints.ReadJson` answers 415 for a non-empty body whose
  `Content-Type` is missing, not `application/json`/`application/*+json`, or not UTF-8 (`IsJson`); an empty body needs none
  (a change without input, `POST /auctions/{id}/close`, still answers 204). The generated entries call `ReadJson`; webhooks
  keep `ReadBytes` (the signature covers the bytes whatever their type). Snapshots `endpoints` and `openapi` accepted (only
  `ReadBytes` → `ReadJson` and the comment).
* **BC-17, the key detail** **[verified]** by a test: the 422 detail was `http:User:alice:POST /auctions: k-1`. `StoreErrors.KeyReused`
  and `StoreErrors.DuplicateKey` (the same pattern, found here) name the key only; the pipeline logs the scope (7004 when a key
  is reused for another request, 7005 when a concurrent request holds it).
* **AppHost**: `apphost.sh` passes `?tenant=00000000-0000-0000-0000-000000000000` to export and erase; data-protection.md
  §18.3 notes that its run predates the tenant fix.
* **Release lesson**: release.md §15 (403 on push when the package id is linked to another repository).
* **Behaviour changes**: 415 for a JSON body without `Content-Type` (a client that omitted the header must send it); the two
  key errors' `detail` text.
* **Tests** (red before the fix **[verified]**: 2 of 82 failed): `A_json_body_without_a_content_type_is_415_and_a_change_without_a_body_needs_none`,
  `Json_content_types` (10 cases), the key test checks the detail is `k-1` and names neither the scope nor the user.
* **Public API** (generated): `+ ChangeEndpoints.ReadJson(HttpContext)`, `+ static ChangeEndpoints.IsJson(string?)`.
* **Suppressions**: none.
* **AppHost run** (`apphost.sh … 5`, JIT, NATS, 2026-11-19) **[verified]**: health 200 after 27 s; bid 204, settle by bob
  403 / alice 204; 5 sagas completed; list 304 by ETag; admin 200/403/401; `export by alice: email bob@artflow.example; by
  bob 403`. The erasure line now shows what the missing `?tenant=` hid: `409 DataProtection.ErasureIncomplete … "failed":
  ["search"]`. The search participant fails in the API process; that is its own bug, fixed in the next PR (§10). The export
  afterwards reads `email None` (the key is erased).

## 10. PR A6: the search participant of an erasure in the API process (found by the A5 AppHost run)

* **Finding** **[verified]**: with the tenant passed (A5), the sample's erasure answered 409 with `failed: ["search"]`. The API
  process registers `AddSearch` for `GET /search`, but its `SearchHost` stays gated (the worker is the runner), so `Prepare`
  never ran there. `NpgsqlSearchIndex` resolves its version's table in `Prepare`; `Replace` then ran `DELETE FROM  d …` with an
  empty table name: `42P01: relation "d" does not exist`. Before the tenant fix the same failure was an unhandled 500, and the
  step 18G run never reached it (the call answered 400).
* **Fix**: `SearchHost.Reindex` and `SearchHost.Remove` (the erasure and retention paths, outside the feed) prepare the index
  once (`Prepare` is idempotent: `CREATE … IF NOT EXISTS` and the fingerprint check); a failed preparation is retried by the next
  call. The OpenSearch and Typesense indexes resolve their index/collection the same way, so the fix in the host covers them.
* **Test**: `NpgsqlSearchTests.An_erasure_in_a_process_whose_host_never_ran_reaches_the_index` (a worker host feeds the index; a
  second host on its own index object, never started, re-indexes after the key is erased and removes the documents). Red before
  the fix with `42P01` **[verified]**.
* **Behaviour change**: none intended; an erasure in an API process completes instead of failing its search participant.
* **Public API**: none. **Suppressions**: none.
* **AppHost run** (`apphost.sh … 5`, JIT, NATS, 2026-11-19) **[verified]**: `erase by alice: {"tenant":"00000000-…","subject":"…",
  "alreadyErased":false,"streams":1}; export afterwards: email None`; audit `export subject, erase subject, export subject`;
  tombstone true; the rest as in §9.

## 11. PR B1: CORS with Tarol's headers (BC-1)

* **Change**: `TarolHeaders` (`Tarol.Changes.AspNetCore`): `Exposed` (`ETag`, `Location`, `Tarol-Position`, `Tarol-Stale`,
  `Idempotent-Replayed`, `Retry-After`, `WWW-Authenticate`), `Allowed` (`Authorization`, `Content-Type`, `Idempotency-Key`,
  `If-Match`, `If-None-Match`, `Tarol-Read-After`, `traceparent`), the constants `IdempotentReplayed` and `IdempotencyKey`
  (the endpoints use them), and `WithTarolHeaders(this CorsPolicyBuilder)`. CORS stays the application's: Tarol does not add a
  policy or choose origins. The README has the recipe; `UseCors` goes before `UseAuthentication` (a preflight has no token).
* **Sample**: the API allows `ArtFlow:BrowserOrigins` (default `http://localhost:5173`, the Vite dev server) with the recipe.
* **Tests**: `CorsTests` against the real entries: a preflight for `POST /auctions` with all seven request headers answers
  204 with them allowed; a creation from the origin exposes exactly `TarolHeaders.Exposed`; another origin gets no CORS
  headers. Red before **[verified]** by the client session (`curl -H 'Origin: http://localhost:5173' :5080/auctions`: no
  `Access-Control-*`); the sample after the change: §11 run below.
* **Public API** (generated): `TarolHeaders` (`Exposed`, `Allowed`, `IdempotentReplayed`, `IdempotencyKey`, `WithTarolHeaders`).
* **Behaviour**: none without a policy. **Suppressions**: none.
* **Sample run** (`apphost.sh … 1`, 2026-11-19) **[verified]**: the preflight `OPTIONS /auctions` from `http://localhost:5173`
  answers 204 with `Access-Control-Allow-Headers: Authorization,Content-Type,Idempotency-Key,If-Match,If-None-Match,Tarol-Read-After,traceparent`;
  `GET /auctions` from that origin (401 without a token) carries `Access-Control-Expose-Headers: ETag,Location,Tarol-Position,Tarol-Stale,Idempotent-Replayed,Retry-After,WWW-Authenticate`.
  The script's run is unchanged.

## 12. PR B2: the hub token from the query string (BC-2, CC-2) and closing on expiry (BC-15)

* **Change**: `ZitadelOptions.HubPath` (default `/tarol/messages`, `UiPushOptions.HubPath`'s default; `null` = off). `AddZitadel`
  sets `JwtBearerEvents.OnMessageReceived`: under that path, and only without an `Authorization` header, the query value
  `access_token` is the token. Everywhere else a token in the URL is ignored (URLs reach logs and proxies). The path must start
  with `/` (checked by `Validate`). An application with its own `JwtBearerEvents` would replace them: `AddZitadel` owns the
  scheme's options, so that is no new constraint.
* **BC-15** (asked to be considered in this PR): `UiPushOptions.CloseOnAuthenticationExpiration`, default `true`, maps to
  `HttpConnectionDispatcherOptions.CloseOnAuthenticationExpiration`. **Behaviour change**: a hub connection closes when its
  bearer token expires; `MessagePushClient` and the browser client reconnect with a fresh token from their token provider.
* **Tests**: `ZitadelHubTests` against a real Zitadel v4.20.3 and a real Kestrel port: a raw `ClientWebSocket` to
  `/tarol/messages?access_token=<JWT>` does the SignalR JSON handshake and `Subscribe("artflow.bids")` succeeds (the tenant from
  the organisation), `Subscribe("billing.secret")` is refused `Forbidden`; without a token and with a tampered one the upgrade
  is 401; `/whoami?access_token=…` stays anonymous while the header authenticates. Red with `HubPath = null` (the old
  behaviour) **[verified]**: `The server returned status code '401' when status code '101' was expected`. `HubMappingTests`:
  the hub endpoint's dispatcher options close on expiry by default and not when opted out. An expiry test needs a token that
  expires within the test: B4 adds the short-lived token option to the sample; here the mapping is what is tested.
* **Public API** (generated): `+ ZitadelOptions.HubPath`, `+ UiPushOptions.CloseOnAuthenticationExpiration`.
* **Suppressions**: none.

## 13. PR B3: OpenAPI complete enough for a typed client (BC-3)

What the generated `AddTarolEndpoints` transformers add (`OpenApiEmitter.cs`; the package still does not reference the OpenAPI
packages):

| BC-3 | Change |
|---|---|
| (a) problems | a document transformer adds the component `TarolProblem` (`type`, `status`, `title` required; `code`, `detail`, `errors` as codes per JSON path); every 4xx/5xx except 401 names it (401 has no body, it gets `WWW-Authenticate`) |
| (b) kind | `x-tarol-kind`: `creation`, `change`, `view`, `list`, `search`; `admin` for `MapTarolAdmin` |
| (c) codes | `x-tarol-errors` from `TarolEndpointMetadata.Errors` (new). The generator lists Tarol's codes for the kind (`<Aggregate>.NotFound`, `Auth.Forbidden`, `Stream.VersionConflict`, `Idempotency.KeyReused`, `Idempotency.DuplicateKey`, `<Aggregate>.AlreadyExists`, `Projection.Building`, `Tenant.NotFound` with a `{tenant}` route) and the constant `code` of every `Tarol.Results.Error` factory call in the change's methods and in the methods of the change or the aggregate those call (two levels, source only; a parameter default such as `Error.Forbidden()`'s counts). A code built at run time (`"Order." + id`) is not listed. Search lists `Search.Building`, `Auth.Forbidden`, `Tenant.NotFound`; the admin operations their `AdminProblems` |
| (d) ids | `x-tarol-id: "<Aggregate>"` on `Id<T>` schemas and on `id` path parameters (`RouteValue.Aggregate`, new) |
| (e) headers | writes (204, 201): `ETag`, `Tarol-Position`, `Idempotent-Replayed` (+ `Location` on 201); lists and search: `ETag`, `Tarol-Position`, `Tarol-Stale`; 503: `Retry-After` |
| (g) numbers | a schema with `number`/`integer` **and** `string` plus a pattern (the web defaults' `AllowReadingFromString`) becomes the number type alone, for requests and answers (a client sends numbers) **[verified]**: `highestBid` was `["number","string"]` + pattern |
| (h) `Option<T>` | its schema is `T`'s schema plus `null` (was `{}`): `OptionOfstring` = `["null","string"]` |
| (f) search, admin | `MapTarolSearch` carries the handler's `MethodInfo` and `TarolEndpointMetadata("search")` with its query parameters; the transformer writes the page schema (hits with `snippet` and `highlights`, `counts`, `countsCapped`, `next`), 304, 503. `MapTarolAdmin` gives each endpoint its `MethodInfo`, an operation id (`TarolAdminDeadLetters`, …), the 200 answer type and the tag `Tarol.Admin` (`TarolAdminEndpoints.OpenApiTag`); the transformer adds the route and query values, statuses, `TarolProblem` and codes by operation id. `AddOperations` adds `OperationsJson` to the HTTP JSON options so the answer schemas resolve under Native AOT too |

* **Why tables for admin**: `Tarol.Operations` does not reference `Tarol.Changes.AspNetCore` (so it cannot attach
  `TarolEndpointMetadata`), ApiExplorer takes no parameters from a `RequestDelegate`'s `MethodInfo`, and it drops response
  metadata without a type. The generated code therefore knows the admin operations by id; the test pins both sides.
* **Found** **[verified]**: an input or view member `Option<T> X = default` (a parameter default) makes the document throw
  `JsonException: The JSON value could not be converted to Tarol.Options.Option`1[System.String]` inside ASP.NET's schema
  exporter (it reads the default value); without the default the member works. The owner chose a diagnostic (CC-5): TWEB007, §20.
* **Tests**: `EdgeTests.The_openapi_document_is_complete_enough_for_a_typed_client` (a–h against the test app: problem refs on
  every 4xx/5xx of five operations, kinds, codes including `Bid.TooLow`/`Bid.OwnAuction`/`Auction.NotOpen` found in the aggregate,
  `x-tarol-id`, headers, the search parameters and highlights, `number`, `Option<T>`), `OpenApiAdminTests` (operation id, kind,
  codes, parameters, the answer `$ref`, `TarolProblem`, statuses), the generator test
  `The_static_problem_codes_of_a_change_are_its_metadata` (a constant, a named argument, a parameter default; a computed code
  is not listed), the `endpoints`/`lists`/`openapi` snapshots (accepted after review: `Errors`, `Aggregate`, the transformers),
  and the AOT smoke app checks `x-tarol-kind`, `x-tarol-errors`, `x-tarol-id` and `TarolProblem` natively.
* **AOT** **[verified]**: the first publish of `Tarol.Changes.AspNetCore.AotSmoke` failed with IL2026/IL3050 on the generated
  `JsonArray.Add<T>(T)` (the generic overload); the codes are added as `JsonNode` now, and the app publishes with 0 IL warnings
  and passes its new check natively.
* **Public API** (generated): `+ TarolEndpointMetadata.Errors`, `+ RouteValue.Aggregate`, `+ TarolAdminEndpoints.OpenApiTag`.
* **Behaviour**: only the OpenAPI document; `AddOperations` appends `OperationsJson` to the HTTP JSON resolver chain.
* **Suppressions**: none.

## 14. PR B4: a sample a browser e2e can drive (BC-9)

* **Zitadel** (`ZitadelSetup`): the user-agent app `artflow-web` (`OIDC_APP_TYPE_USER_AGENT`, auth method none = PKCE,
  authorization code + refresh token, JWT access tokens with role assertion, dev mode so `http://localhost:5173/*` is a valid
  redirect); its client id goes to `web.txt` next to `clients.txt`. Human users ada (seller, tarol-admin), ben and cleo
  (bidder) with grants and a password generated per run (`users.txt`; nothing in the repository). An optional third argument
  sets the instance's access and id token lifetime (protobuf durations in seconds: `720h` is refused, `2592000s` works
  **[verified]**).
* **API**: `AddMessaging("artflow", m => m.UseBroker(…).PushToUi(…))` forwards `artflow.bid-placed` (a new publication of
  `Bid.Placed` in the worker: auction, amount, time; no bidder identity) to the hub's tenant group, `MapMessageHub()`; CORS
  (B1); `CreateAuction([Range(1, 1_000_000)] decimal Reserve) : IValidatable`; `Zitadel:ClockSkew`. The AppHost gives the API
  the broker and, with `ARTFLOW_TOKEN_LIFETIME`, a clock skew of 0.
* **Found** **[verified]**: with 30-second tokens the API still accepted an expired token for 5 minutes (JwtBearer's default
  `ClockSkew`), so an "expired token" test could not be quick. `ZitadelOptions.ClockSkew` (new; `null` keeps the default)
  lets the sample set 0: after expiry the API answers `401` with `WWW-Authenticate: Bearer error="invalid_token",
  error_description="The token expired at …"`, and the hub closes the connection (35 s for a 30 s token: BC-15 holds).
* **Script**: `ARTFLOW_STAY_UP=1` starts the AppHost without the 420 s limit, waits for readiness, prints the endpoints, the web
  client id and where the users are, and stays up until Ctrl-C. Every run checks the browser path: the PKCE authorize request
  (302 to Zitadel's login), the 400 on `reserve`, and a push over a real WebSocket with the token in the query
  (`samples/Sagas/hubcheck.py`, the standard library only), and prints the token lifetime.
* **CI**: the `build` job builds `samples/Sagas/AppHost` (conventions §4.7 says the sample is built by CI); it is not run there.
  `README.md` of the sample said "not built by CI" and named step 18 for the full sample (it is step 19).
* **AppHost run** (`apphost.sh … 5`, JIT, NATS, 2026-11-19) **[verified]**:

  ```
  access token lifetime: 43200 s
  browser app: authorize with PKCE 302 -> http://localhost:8080/ui/v2/login/login; human users: ada (seller,tarol-admin) ben (bidder) cleo (bidder)
  validation: reserve 0 answers 400 {'reserve': ['Range']}
  hub push (WebSocket, ?access_token=): {"topic": "artflow.bid-placed", "id": "…", "version": 1, "subject": "…", "time": "…", "sender": "artflow", …}
  auction 1: bid 204 …; settle by bob 403, settle by alice 204
  settlement sagas completed: 5 … erase by alice: {…"alreadyErased":false,"streams":1}; export afterwards: email None
  ```

  and with `ARTFLOW_STAY_UP=1 ARTFLOW_TOKEN_LIFETIME=30s`: `access token lifetime: 30 s`, the `up:` lines, a fresh token 200,
  the hub closed by the server after 35 s, the expired token 401 `invalid_token`.
* **Tests**: `ZitadelClaimsTests.The_clock_skew_and_the_hub_path_are_options` (the default skew, 0, the checks of both options).
* **Public API** (generated): `+ ZitadelOptions.ClockSkew`.
* **Suppressions**: none. The sample's AppHost has Aspire's `ASPIRE010` warning from the Aspire SDK's own targets (as before).

## 15. PR C1: field error details on the wire (BC-5)

* **Change**: `ChangeEndpoints.Problem` writes `errorDetails: {"<path>": [{"code": "…", "detail": "…"}]}` next to `errors` for an
  `Invalid` (the `FieldError.Detail` that was dropped). The generated value rules give their bounds as the detail (`Length`:
  `3..60`, `Range`: `1..1000000`); a rule without one has no `detail`. With an `IProblemDetailsService` the member is an
  extension holding a `JsonElement` (covered by ASP.NET's problem-details JSON metadata, also natively). `errors` is unchanged.
  The `TarolProblem` schema (B3) has `errorDetails`.
* **Behaviour**: one more member on 400 answers.
* **Tests**: the direct-write test pins the exact body (red before: no `errorDetails` **[verified]**), the service path test reads
  the codes and details, the document test lists the member; the AOT smoke app checks `errorDetails` natively through
  `AddProblemDetails()` (0 IL warnings, passed **[verified]**). Snapshots accepted (the schema).
* **Public API**: none. **Suppressions**: none.

## 16. PR C2: 400 for unknown list keys (BC-14)

* **Change**: `ChangeEndpoints.Paging` (called by every generated list entry) checks the query string's keys against the entry's
  own `TarolEndpointMetadata.Query` (the filters the generator found, `sort`, `after`, `limit`, `offset`, `count`; the same list
  the OpenAPI document shows), ignoring case like ASP.NET's query collection. Another key is a field error with the code
  `Unknown` (`ChangeEndpoints.UnknownKeyCode`; a field code like `Range`, so it sits in `errors` and `errorDetails`). Nothing is
  generated anew.
* **Behaviour change**: a list request with an unknown key answers 400 instead of the unfiltered page. A claim-bound member in
  the query (`/my/auctions?seller=bob`) is refused instead of ignored; the existing test pinned "ignored" and now pins the 400.
  The sample's queries use known keys only (checked).
* **Tests**: `An_unknown_query_key_is_400_instead_of_an_unfiltered_list` (`?selller=` 400 on `selller`; `?Seller=…&SORT=…` 200),
  the claim-bound test. Red before: the typo answered 200 **[verified by reading]** (the generated binder reads named keys only).
* **Public API** (generated): `+ ChangeEndpoints.UnknownKeyCode`. **Suppressions**: none.

## 17. PR C3: a request id in every answer (BC-11, CC-6)

* **Change**: `ChangeEndpoints.RequestId(http)` is the W3C trace id of `Activity.Current` (the id the traces of the request are
  found by), else `HttpContext.TraceIdentifier`. `ChangeEndpoints.Of` (the first call of every generated entry: changes, views,
  lists, webhooks), `MapTarolSearch` and `Problem` set `Tarol-Request-Id`; so do the admin endpoints (`Tarol.Operations` repeats
  the two lines, it does not reference `Tarol.Changes.AspNetCore`). Every problem has `traceId` with the same value: directly
  written, and with an `IProblemDetailsService` as the `traceId` extension set before ASP.NET would add its own (the
  `traceparent`-shaped `00-…-…-01`, which differed from the header).
* **CORS and OpenAPI**: `TarolHeaders.Exposed` lists `Tarol-Request-Id`; every documented response has the header, the
  `TarolProblem` schema `traceId`.
* **Behaviour**: one more header and one more problem member.
* **Tests**: `EdgeTests.Every_answer_has_a_request_id_and_a_problem_names_it` (a view, a 404 problem, a list, a 413 that the
  generated code answers with a status only), the exact direct-write bodies now end in `"traceId":"<the header>"`, the admin
  problems' `traceId` equals the header, the document test lists the header and the member.
* **Public API** (generated): `+ TarolHeaders.RequestId`, `+ ChangeEndpoints.RequestId`, `+ ChangeEndpoints.StampRequestId`
  (`EditorBrowsable(Never)`). **Suppressions**: none.

## 18. PR C4: structured hub errors (BC-12, CC-7)

* **Change**: `HubErrors` (`Tarol.Messaging.SignalR`): `Forbidden = "Hub.Forbidden"`, `Topic = "Hub.Topic"`,
  `Tenant = "Auth.Tenant"` (the HTTP entries' code for the same refusal), `Exception(code, detail)` and `CodeOf(message)`, which
  also finds the code inside the text SignalR clients wrap around a server error. `MessageHub` throws through it.
* **Behaviour change (breaking)**: `Forbidden: Subscribe billing.secret` → `Hub.Forbidden: Subscribe billing.secret`, `Topic: …` →
  `Hub.Topic: …`; `Auth.Tenant: …` unchanged.
* **Tests**: the push tests check each code with its `": "`; `HubMappingTests.The_code_of_a_hub_error` (5 cases, a client-wrapped
  message included); the Zitadel WebSocket test reads `Hub.Forbidden`.
* **Public API** (generated): `+ HubErrors`. **Suppressions**: none.

## 19. PR C5: `GET /me` (BC-13, CC-8)

* **Change**: `MeEndpoint.MapTarolMe(pattern = "/me")`, opt-in. It runs `ChangeEndpoints.Edge` (so the actor rule, e.g.
  `AddZitadel`'s, and the tenant claim with its mapping decide, exactly as for a change) and answers `{"subject", "actor":
  {"kind", "id"}, "tenant", "roles"}`; `tenant` is `null` without one; roles are the principal's role claims (after the identity
  provider's claims transformation), sorted; 401 anonymously (the default authorization policy), 401 `Auth.Tenant` for an
  unmappable tenant claim. Private, no-cache. `TarolEndpointMetadata("me")` puts it in the document with its schema.
* **Tests**: `EdgeTests.Me_answers_who_the_caller_is_by_the_entries_rules` (a user, a service by its `client_id`, anonymous 401,
  the document); the AOT smoke app calls `/me` natively (0 IL warnings, passed **[verified]**).
* **Public API** (generated): `+ MeEndpoint.MapTarolMe`. **Behaviour**: none unless mapped. **Suppressions**: none.

## 20. PR C6: TWEB007, an `Option<T>` member with a default (CC-5)

* **Finding** (B3, **[verified]**): `Option<T> X = default` on a view or input member makes ASP.NET's OpenAPI schema export throw
  `JsonException: The JSON value could not be converted to Tarol.Options.Option`1[…]` when the document is requested.
* **Change**: the endpoint generator warns TWEB007 for such a member of a change, a creation (constructor parameters) or a view
  (its widest public constructor). The default adds nothing: an omitted `Option<T>` member is `None`.
* **Tests**: `DiagnosticTests.TWEB007_an_option_member_with_a_default_warns` (a change, a view, and no warning without the
  default). `AnalyzerReleases.Unshipped.md` lists TWEB007.
* **Public API**: none. **Suppressions**: none.

## 21. PR C7: complete validation metadata for clients (BC-4)

* **Where the rules come from**: the endpoint generator reads the same `Tarol.Validations` attributes the validation generator
  reads (`Length`, `Range`, `Pattern` with `IgnoreCase`, `Email`, `NotEmpty`, `Count`), on every type an input's body members
  reach (through `Option<T>`, `T?`, lists and arrays, four levels deep):
  * a single-field value object (`Title([Length(3, 60)] string Value)`): its own schema, as before, plus `x-tarol-rules`;
  * the members of a multi-field or class value object (`Price([Range(Min = 0)] decimal Value, [Pattern(…, IgnoreCase = true)]
    string Currency)`) and of an `IValidatable` input (`CreateAuction([Range(1, 1_000_000)] decimal Reserve)`): the property
    schemas, matched by declaring type and JSON name, with `x-tarol-rules` and the JSON Schema keywords (`minLength`,
    `maxLength`, `minimum`, `maximum`, `pattern`, `format: email`, `minItems`, `maxItems`).
* **Shape**: `x-tarol-rules: [{"rule": "Length", "code": "Length", "min": 3, "max": 60}]`; `code` is what a 400 reports
  (`NotEmpty` reports `Required`); `min`/`max` are numbers; `pattern` and `flags` (`i` for `IgnoreCase`, `u` when the pattern
  uses `\p{…}`, which `RegExp` reads only with `u`). `[Email]` adds `x-tarol-email` with `Rules.Email`'s exact structural
  check, so a client can implement the same test instead of a regex.
* **TVAL010** (warning, `Tarol.Validations`): a `[Pattern]` that does not parse with `RegexOptions.ECMAScript`, or that uses
  `\A`, `\Z`, `\z`, `\G`, atomic groups, comments or inline options (which that option still accepts). `\d` and `\w` match
  Unicode digits and letters in .NET but ASCII in JavaScript: the description says so; not reported (they are too common to
  warn on). No pattern in the repository or the sample is reported.
* **Found and fixed** **[verified]**: a list of value objects (`IReadOnlyList<Contact>`) had an array schema without `items` (the
  value-object converter gives an empty schema); the transformer fills `items` from the value object's own schema.
* **Tests**: `EdgeTests.Every_rule_carrying_schema_has_its_rules_for_the_client` (a value object, an e-mail value object with
  `x-tarol-email`, an input member with keywords, `Count` with `minItems`/`maxItems`, a multi-field value object with `IgnoreCase`)
  on a new test entry `MakeOffer`; `DiagnosticTests.TVAL010_A_Pattern_JavaScript_Reads_Otherwise_Warns` (three warnings, a
  correct pattern and an escaped `\\A` without); the AOT smoke app checks the rules natively (0 IL warnings, passed); the
  `openapi`/`endpoints` snapshots accepted (the rule tables).
* **Public API**: none (generated code and a diagnostic). **Behaviour**: the document only. **Suppressions**: none.
