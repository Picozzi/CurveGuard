# Duplicate 5Y OIS tenor

[Source fixture](../examples/duplicate-5y.json) · [Report after replay](../reports/replays/duplicate-5y.json)

## Expected diagnosis

- More than one 5Y OIS quote was supplied. The evidence does not establish which quote should be used.
- CurveGuard returned `QUARANTINE` and marked bootstrap `SKIPPED`. Pricing impact is unknown.
- The citation should point to `input-1` (`DUPLICATE_OIS_TENOR`).
- One appropriate next action is to reconcile the 5Y entries against the source snapshot and confirm the authoritative quote.

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
