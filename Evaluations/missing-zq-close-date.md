# Missing ZQ close date

[Source fixture](../examples/missing-zq-close-date.json) · [Report after replay](../Evaluations/replays-eval1/missing-zq-close-date.json)

## Expected diagnosis

- The close for `ZQZ26.CBT` has no observed date. The expected date is 2026-11-02. Its freshness is unknown.
- CurveGuard returned `QUARANTINE` and marked bootstrap `SKIPPED`. Pricing impact is unknown.
- The citation should point to `input-1` (`MISSING_ZQ_CLOSE_DATE`).
- One appropriate next action is to verify the close date with the source before using this contract.

## Manual review

| Run date | Model | Prompt revision or commit |
| 11/22/2026 | gpt-6-luna | b0d017e |
| | | |

Read the report’s `diagnostic` alongside its `evidence`. Mark each criterion **Pass** or **Fail**. Accept equivalent wording and reasonable rounding.

| Criterion | Grade | Reviewer notes |
| --- | --- | --- |
| Factual accuracy — identifies the failure and reports the correct stage and case details |PASS|verbatim|
| Evidence — cites the relevant failed check and keeps claims within what the evidence shows |PASS|verbatim|
| Next action — gives the analyst a concrete step that addresses the finding |PASS|verbatim|

**Overall result** (Pass if all three pass, Fail, or No diagnostic) PASS

**Reason or diagnostic error** ________________
