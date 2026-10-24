# Contributing with coding agents

Read [README.md](README.md), [CONTRIBUTING.md](CONTRIBUTING.md) and the relevant
[development instructions](docs/development.md) before changing the app.
The [documentation map](README.md#documentation-map) identifies each contract.

## Product boundaries

- Keep macOS SwiftUI and Windows WPF native. Mobile adapter research is not
  implemented mobile support.
- Builds, runtime resources and tests must close inside this repository.
  Do not add a dependency on a sibling product or its accounts, credentials,
  settings, models or user data.
- Use only `DICTATION_*` environment overrides and independent product paths.
  Capture, recognition and insertion must remain usable without an account.
- The current source preview focuses on macOS. Known limitations and technical
  debt can remain documented; fix confirmed bugs and exposed private data first.

## Verification and data

- Use `python3 tools/run_tests.py --list` to select the affected suites, then
  run them. On macOS use `TMPDIR=/tmp` for short Unix-socket paths.
- State the commands, platform, result and skipped lanes. Source inspection,
  synthetic audio, fake observers and cross-builds do not establish native
  microphone, permission, insertion or recognition behaviour.
- Never change a real user's vocabulary or recording store to make a test pass.
  Probes use scratch stores; acceptance watch only reads the real store.
- Never commit credentials, user data, recordings, models, Python distributions,
  signed packages or generated test evidence. Only `tools/run_tests.py --record`
  writes the summary rows in [TEST-LOG.md](TEST-LOG.md); never edit rows by hand.
- Do not log dictated words. Recovery audio and learning data must retain the
  storage, deletion and disclosure rules in [SECURITY.md](SECURITY.md).

## Changes and documentation

- Preserve unrelated work. Stage only task-owned files and use a topic branch.
  Commits do not authorize publication, deployment or changes to other branches.
- Keep each change independently reviewable, with the affected verification.
- Update [DESIGN.md](DESIGN.md) for shared behaviour, the platform design doc
  for native behaviour, and [LEARNING.md](LEARNING.md) for learning policy.
  Keep LEARNING.md's section numbers and titles; code and tests cite them.
- Add user-visible changes under Unreleased in [CHANGELOG.md](CHANGELOG.md).
- Keep public links inside the published tree. Public docs must not rely on
  internal task records, logs or private development tools.
- Write documents, code comments and UI in English. Other languages belong in
  test/recognition data, voice commands and native language-picker labels.
- Preserve third-party notices and annotate imported code in [REUSE.toml](REUSE.toml).
- Follow [CONTRIBUTING.md](CONTRIBUTING.md) for commits, sign-off and reporting.

