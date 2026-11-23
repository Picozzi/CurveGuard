# Invalid EFFR fixing rate

[Source fixture](../examples/invalid-effr-rate.json) · [Report after replay](../reports/replays/invalid-effr-rate.json)

## Expected diagnosis

- The EFFR fixing fails the requirement for a finite rate representable as a decimal. The check leaves the observed value unspecified.
- CurveGuard returned `QUARANTINE` and marked bootstrap `SKIPPED`. Pricing impact is unknown.
- The citation should point to `input-1` (`INVALID_EFFR_RATE`).
- One appropriate next action is to verify the EFFR fixing against the published rate and check how it was imported.

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
