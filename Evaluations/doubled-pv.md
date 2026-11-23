# 5Y OIS pricing discrepancy

[Source fixture](../examples/5y-doubled-pv.json) · [Report after replay](../Evaluations/replays-eval1/doubled-pv.json)

## Expected diagnosis

- CurveGuard returned `QUARANTINE` after bootstrap `PASS`. Both 5Y dollar PV changes are about twice their expected size.
- At +1 bp, the reported change was $890.51 against $445.25 expected. Cite `pricing-3` (`pv_change_usd`).
- At +10 bp, the reported change was $8,897.24 against $4,448.62 expected. Cite `pricing-5` (`pv_change_usd`).
- The 10 bp to 1 bp ratio passed at about 9.99 against 10 expected. The cause of the dollar discrepancy is unknown.
- One appropriate next action is to reconcile the reported 5Y scenario PVs with the repriced trade and check how the dollar changes were calculated.

## Manual review

| Run date | Model | Prompt revision or commit |
| 11/22/2026 | gpt-6-luna | b0d017e |
| --- | --- | --- |

Read the report’s `diagnostic` alongside its `evidence`. Mark each criterion **Pass** or **Fail**. Accept equivalent wording and reasonable rounding.

| Criterion | Grade | Reviewer notes |
| --- | --- | --- |
| Factual accuracy — identifies the failure and reports the correct stage and case details |PASS|verbatim|
| Evidence — cites the relevant failed checks and keeps claims within what the evidence shows |PASS|correct guards|
| Next action — gives the analyst a concrete step that addresses the finding |PASS|different than what we expected, but also a valid next step|

**Overall result** (Pass if all three pass, Fail, or No diagnostic) PASS

**Reason or diagnostic error** N/A
