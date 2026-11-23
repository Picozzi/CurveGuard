# Missing 1Y OIS anchor

[Source fixture](../examples/missing-1y.json) · [Report after replay](../reports/replays/missing-1y.json)

## Expected diagnosis

- The snapshot is missing the 1Y OIS quote required to calibrate the FOMC path.
- CurveGuard returned `QUARANTINE` and marked bootstrap `SKIPPED`. Pricing impact is unknown.
- The citation should point to `input-1` (`MISSING_1Y_OIS_QUOTE`).
- One appropriate next action is to obtain the 1Y OIS quote for the valuation date and rerun calibration.

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
