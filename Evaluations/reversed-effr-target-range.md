# Invalid EFFR target range

[Source fixture](../examples/reversed-effr-target-range.json) · [Report after replay](../reports/replays/reversed-effr-target-range.json)

## Expected diagnosis

- The EFFR target range failed its finite, ordered-bounds check. The model sees the combined check, so the precise values and which condition failed are unknown from its evidence.
- CurveGuard returned `QUARANTINE` and marked bootstrap `SKIPPED`. Pricing impact is unknown.
- The citation should point to `input-1` (`INVALID_EFFR_TARGET_RANGE`).
- One appropriate next action is to verify the lower and upper bounds against the published target range and confirm their ordering.

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
