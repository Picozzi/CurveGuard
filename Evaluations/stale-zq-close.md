# Stale ZQ close

[Source fixture](../examples/stale-zq-close.json) · [Report after replay](../reports/replays/stale-zq-close.json)

## Expected diagnosis

- The close for `ZQZ26.CBT` is dated 2026-10-30. The expected close date is 2026-11-02.
- CurveGuard returned `QUARANTINE` and marked bootstrap `SKIPPED`. Pricing impact is unknown.
- The citation should point to `input-1` (`STALE_ZQ_CLOSE`).
- One appropriate next action is to verify or refresh the 2026-11-02 close for this contract.

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
