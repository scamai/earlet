# Dictation test log

One row per recorded run of `tools/run_tests.py`, newest first. Rows are
summaries written only by `tools/run_tests.py --record` on the machine that ran
the suites; do not type or edit them by hand. The raw evidence (each suite's
log, its JUnit, TRX or xUnit result files, `summary.json` and `summary.md`)
stays in `evidence/test-runs/<run>/`, which is gitignored. A row carries counts
and short reasons only: no test output, no local paths.

- **Commit** is marked `+dirty` when the tree under `apps/dictation` had
  uncommitted changes, and `(unverified copy)` when the run came from a copy
  with no git history; both need `--allow-dirty` to be recorded, and in a copy
  `--commit` supplies the id.
- **Suites** lists passed/failed/skipped for each suite that ran, read from the
  suite's result file. The exception is an XCTest skip: SwiftPM's parallel
  xUnit file writes a skipped XCTest as a pass, so the skip is read from
  XCTest's own `Test Case '…' skipped` line in a serial rerun of the same
  build. Suites without a result file (boundary, the Windows build and the
  packages) show pass or FAIL from their exit status. `snapshot` never gates.
- **Learning tests** counts the tests whose names contain `Learning`,
  `learning_golden`, `LearnedVocabulary` or `learned_vocabulary`, across the
  run (passed/failed/skipped).
- **Verdict** is OK (every gating suite that ran passed; skips are named),
  FAILED, or INCOMPLETE (nothing ran). A run narrowed with `--suite` is OK when
  its selected suites pass, so an OK row on one suite says nothing about the
  others.
- **Not covered** names the suites this platform has that the run skipped, and
  why.

## Runs

| Date (UTC) | Commit | Platform/arch | Host | Suites (passed/failed/skipped) | Learning tests | Verdict | Not covered |
|---|---|---|---|---|---|---|---|
