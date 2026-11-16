# Caching: evaluation (step 13 design) — draft

Owner answers so far (round 1, 2026-11-16):

| # | Answer |
|---|---|
| CA-1 | List pages only; views stay uncached; the aggregate cache is asked in round 2 |
| CA-2 | In process, the projection's checkpoint pushed by NOTIFY with a poll fallback |
| CA-3 | Cache neither lookups nor codec metadata |
