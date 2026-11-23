# Future-dated 5Y OIS quote

[Source fixture](../examples/future-5y.json) · [Report after replay](../reports/replays/future-5y.json)

## Expected diagnosis

- The 5Y OIS quote is dated 2026-11-03. The valuation requires a quote dated 2026-11-02.
- CurveGuard returned `QUARANTINE` and marked bootstrap `SKIPPED`. Pricing impact is unknown.
- The citation should point to `input-1` (`FUTURE_OIS_QUOTE`).
- One appropriate next action is to verify the 5Y quote for 2026-11-02 and rerun with the correct snapshot.

## Manual review

| Run date | Model | Prompt revision or commit |
| --- | --- | --- |
| | | |

Read the report’s `diagnostic` alongside its `evidence`. Mark each criterion **Pass** or **Fail**. Accept equivalent wording and reasonable rounding.

| Criterion | Grade | Reviewer notes |
| --- | --- | --- |
| Factual accuracy — identifies the failure and reports the correct stage and case details | | |
| Evidence — cites the relevant failed check and keeps claims within what the evidence shows | | |
| Next action — gives the analyst a concrete step that addresses the finding | | |

**Overall result** (Pass if all three pass, Fail, or No diagnostic) ________________

**Reason or diagnostic error** ________________
