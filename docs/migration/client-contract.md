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
| BC-14 | An unknown list query key answers 400 `List.UnknownKey`. |

## 2. Decisions asked of the owner

| # | Question | Answer (2026-11-19) |
|---|---|---|
| CC-1 | Search snippets: (a) plain text + `highlights: [{start, length}]` (UTF-16 offsets, the client never needs `{@html}`), or (b) HTML-escaped text that keeps only `<b>` | **(a) text + offsets** (recommended) |
| CC-2 | Where the query `access_token` is read: (a) an `AddZitadel` option `HubPath` (default `/tarol/messages`, `null` = off) plus BC-15 in the same PR, or (b) also a generic helper in `Tarol.Security` | **(a) the `AddZitadel` option** (recommended) |
| CC-3 | What the list ETag hashes besides the rows: (a) `next` and `total`, or (b) the projection position | **(a) `next` and `total`** (recommended) |
| CC-4 | Which Part C items to build in this session | **all**: BC-5, BC-14, BC-11 + BC-12 + BC-13, BC-4 |

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

| PR | Items |
|---|---|
| CC-0 | this note |
| A1 | BC-6 |
| A2 | BC-7 |
| A3 | BC-8 |
| A4 | BC-10 |
| A5 | BC-16, BC-17, the AppHost tenant, the release lesson |
| B1 | BC-1 |
| B2 | BC-2, BC-15 |
| B3 | BC-3 |
| B4 | BC-9 |
| C… | BC-5, BC-14, BC-11/12/13, BC-4 |

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
