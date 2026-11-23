# Stale overnight SOFR fixing

[Source fixture](../examples/stale-sofr-fixing.json) · [Report after replay](../reports/replays/stale-sofr-fixing.json)

## Expected diagnosis

- The SOFR fixing is dated 2026-10-29. The required previous-business-day fixing is 2026-10-30.
- CurveGuard returned `QUARANTINE` and marked bootstrap `SKIPPED`. Pricing impact is unknown.
- The citation should point to `input-1` (`SOFR_FIXING_DATE_MISMATCH`).
- One appropriate next action is to verify and load the overnight SOFR fixing dated 2026-10-30.

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
