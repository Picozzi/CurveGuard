# Future-dated ZQ close

[Source fixture](../examples/future-zq-close.json) · [Report after replay](../Evaluations/replays-eval1/future-zq-close.json)

## Expected diagnosis

- The close for `ZQZ26.CBT` is dated 2026-11-03. The expected close date is 2026-11-02.
- CurveGuard returned `QUARANTINE` and marked bootstrap `SKIPPED`. Pricing impact is unknown.
- The citation should point to `input-1` (`FUTURE_ZQ_CLOSE`).
- One appropriate next action is to check the historical close for this contract as of 2026-11-02.

## Manual review

| Run date | Model | Prompt revision or commit |
| 11/22/2026 | gpt-6-luna | b0d017e |
| | | |

Read the report’s `diagnostic` alongside its `evidence`. Mark each criterion **Pass** or **Fail**. Accept equivalent wording and reasonable rounding.

| Criterion | Grade | Reviewer notes |
| --- | --- | --- |
| Factual accuracy — identifies the failure and reports the correct stage and case details |PASS|verbatim|
| Evidence — cites the relevant failed check and keeps claims within what the evidence shows |PASS|correct guard cited|
| Next action — gives the analyst a concrete step that addresses the finding |PASS|verbatim next step|

**Overall result** (Pass if all three pass, Fail, or No diagnostic) PASS

**Reason or diagnostic error** ________________
