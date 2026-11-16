# Caching: evaluation (step 13 design) — draft

Owner answers so far (round 1, 2026-11-16):

| # | Answer |
|---|---|
| CA-1 | List pages only; views stay uncached; the aggregate cache is asked in round 2 |
| CA-2 | In process, the projection's checkpoint pushed by NOTIFY with a poll fallback |
| CA-3 | Cache neither lookups nor codec metadata |

Round 2 (2026-11-16):

| # | Answer |
|---|---|
| CA-4 | No aggregate cache; C-7 unchanged |
| CA-5 | Invalidation by the projection checkpoint (pushed by NOTIFY, poll fallback) |
| CA-6 | ETag/304 + `Cache-Control: private, no-cache`, `Vary: Authorization`; no output caching |
| CA-7 | A new `Tarol.Caching` package (against the recommendation "no new package") |
