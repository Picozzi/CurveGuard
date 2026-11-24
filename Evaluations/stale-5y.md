# Stale 5Y OIS quote

[Source fixture](../examples/stale-5y.json) · [Report after replay](../Evaluations/replays-eval1/stale-5y.json)

## Expected diagnosis

- The 5Y OIS quote is dated 2026-10-30. The valuation requires a quote dated 2026-11-02.
- CurveGuard returned `QUARANTINE` and marked bootstrap `SKIPPED`. Pricing impact is unknown.
- The citation should point to `input-1` (`STALE_OIS_QUOTE`).
- One appropriate next action is to verify or refresh the 5Y OIS quote for 2026-11-02.

## Manual review

| Run date | Model | Prompt revision or commit |
| 11/22/2026 | gpt-6-luna | b0d017e |
| | | |

Read the report’s `diagnostic` alongside its `evidence`. Mark each criterion **Pass** or **Fail**. Accept equivalent wording and reasonable rounding.

| Criterion | Grade | Reviewer notes |
| --- | --- | --- |
| Factual accuracy — identifies the failure and reports the correct stage and case details |PASS|verbatim|
| Evidence — cites the relevant failed check and keeps claims within what the evidence shows |PASS|correct guard|
| Next action — gives the analyst a concrete step that addresses the finding |PASS|verbatim|

**Overall result** (Pass if all three pass, Fail, or No diagnostic) PASS

**Reason or diagnostic error** ________________
