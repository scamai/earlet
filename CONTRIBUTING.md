# Contributing to Earlet

Thank you for helping. Earlet is two native desktop apps, a SwiftUI app for
macOS and a WPF app for Windows, around one local Python speech worker. This
page says what help is most useful, how to build and test on each platform,
the rules a change has to follow, and how a pull request lands.

- [What helps most](#what-helps-most)
- [Your first contribution](#your-first-contribution)
- [Build and test](#build-and-test)
- [Rules for a change](#rules-for-a-change)
- [Commits and sign-off](#commits-and-sign-off)
- [Using AI tools](#using-ai-tools)
- [How a pull request lands](#how-a-pull-request-lands)
- [Licensing](#licensing)

By taking part you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).
Report security problems privately, as [SECURITY.md](SECURITY.md) explains,
never in a public issue.

## What helps most

You do not need to write code to help. A clear report of a confusing setup
step, a reproducible failure, or an accessibility obstacle is useful.

- **Mac source-preview feedback:** reproducible failures in setup, capture,
  insertion and recovery, with the OS and build identified.
- **Recognition and learning regressions:** a minimal reproducible case with
  synthetic or consented data, without posting private recordings or vocabulary.
- **Bug fixes, tests and docs**, especially for failure paths.
- **Windows source contributions** are welcome, with real-device evidence.
  Windows binary releases and mobile apps are outside the current preview.

Start a new feature as an idea in Discussions before writing code, so we can
agree on whether and how it fits; the [roadmap](ROADMAP.md) lists what is
planned. Questions go to Discussions too ([SUPPORT.md](SUPPORT.md)).

## Your first contribution

| You can help with | A useful first contribution |
|---|---|
| Using the Mac preview | Try one short, non-sensitive dictation in an app you use. Report the build, OS, microphone, hold key, expected result and actual result using the [reporting guide](SUPPORT.md#what-to-include). |
| Documentation | Identify the exact step that was confusing and propose clearer wording. Include the command or link you checked, without private paths or data. |
| Accessibility | Describe the task, assistive technology, and step that prevented completion. A screenshot is optional; remove personal information first. |
| Code or tests | Choose one confirmed bug or a missing failure-path test. Keep the fix and its verification in one pull request. |

1. Search [existing issues](https://github.com/scamai/earlet/issues) and
   [Discussions](https://github.com/scamai/earlet/discussions) first.
   [Good first issues](https://github.com/scamai/earlet/labels/good%20first%20issue)
   are a starting point when available; the list may be empty.
2. For an existing issue, leave a short comment describing your proposed
   change so others can coordinate. Discuss a new feature before coding.
3. Fork the repository, create a branch, and make one focused change.
   Follow the [change rules](#rules-for-a-change) and run the affected
   [checks](#build-and-test). For docs, verify the links and the instructions
   you changed; state any platform you could not check.
4. [Sign off your commits](#commits-and-sign-off), open a pull request with
   the provided template, and describe the problem, change and verification.
   Disclose [AI assistance](#using-ai-tools) when used.

Maintainers review contributions here. Accepted changes arrive through a
source publication; GitHub may show your pull request as **closed rather
than merged**. [How a pull request lands](#how-a-pull-request-lands) explains
the credit and sign-off trail before you start.

## Build and test

The toolchains each platform needs are listed in
[docs/development.md → Prerequisites](docs/development.md#prerequisites).
Every command runs from the repository root. One runner owns every test
suite; install its few dependencies first, with Python 3.11 or later (in a
virtual environment if your system manages its Python):

```bash
python3 -m pip install -r tests/requirements.txt
python3 tools/run_tests.py --list   # which suites run here, and why the rest do not
python3 tools/run_tests.py          # run them; exit 0 only if every gating suite passed
```

| Platform | What you can build, test and run there |
|---|---|
| macOS, Apple Silicon | The Mac app and its Swift tests, the Python tests, the .NET tests when the .NET SDK is installed, and an app package. Set `TMPDIR=/tmp` before running the tests: the default macOS temporary folder makes some Unix socket paths too long. |
| Windows x64 | The Windows app and its .NET tests, the Python tests, and an app package. The app runs only on Windows. |
| Linux | The Python tests, the .NET tests, a cross-build of the Windows app and the platform-free Swift learning target. Neither app runs on Linux. |

Building an app on its own:

```bash
dotnet build windows/Dictation.App/Dictation.App.csproj -c Release
python3 tools/package.py --platform macos --app-only --output dist/macos-app
```

An app-only package has no speech runtime, so it cannot recognise speech.
To dictate with your build, make a complete package with a relocatable
Python and a local Whisper model:
[docs/development.md → Packaging](docs/development.md#packaging). How the
pieces fit together is in [docs/architecture.md](docs/architecture.md).

## Rules for a change

- **One change per pull request,** with its tests and docs in the same pull
  request.
- **Test the failure paths too.** A check counts only when breaking it turns
  a test red; if you add a guard, show that its test fails without it.
- **Say what ran where.** A Linux test, a cross-build, a fake, synthetic
  audio or reading the code is not evidence of how an app behaves on a
  device. The pull request template asks which device and OS ran the change.
- **Learning rules change in every implementation at once.** A rule in
  [LEARNING.md](LEARNING.md) changes together with
  `tests/learning_reference.py`, the C# port
  (`windows/Dictation.Core/Learning/`), the Swift port
  (`macos/Sources/DictationLearning/`) and the shared cases in
  `tests/fixtures/learning-golden.json`, in one pull request, or not at all.
  Keep LEARNING.md's section numbers and titles: code and tests cite them.
- **The apps use the same words.** UI copy lives in
  `windows/Dictation.Core/DictationCopy.cs` and
  `macos/Sources/DictationAppCore/DictationCopy.swift`. A string changes on
  both platforms unless the platforms differ (Right Ctrl and Right Command,
  Enter and Return, the notification area and the menu bar).
- **No dictated words in logs or failure messages,** and no new line that
  names an app or a window. The logs already name the app a dictation went
  into (the Mac by bundle ID when it will not paste, Windows by program name
  at the end of every dictation), and Windows the title of a window whose app
  it cannot identify
  ([docs/design-windows.md](docs/design-windows.md#diagnostics-and-the-log-on-windows)).
- **English** for docs, code comments and UI copy. Other languages appear
  only as data: test cases, recognition tables, voice commands and the
  language picker's native names.
- **Never commit** model weights, Python distributions, recordings, user
  data, credentials, signed packages or test output.
- **Update the contract you changed:** [DESIGN.md](DESIGN.md) for behaviour
  both apps share, [docs/design-macos.md](docs/design-macos.md) and
  [docs/design-windows.md](docs/design-windows.md) for each app,
  [LEARNING.md](LEARNING.md) for learning. Add a line under Unreleased in
  [CHANGELOG.md](CHANGELOG.md) for anything a user would notice.

## Commits and sign-off

Write commit messages as [Conventional Commits](https://www.conventionalcommits.org/):
`fix(windows): keep the clipboard when the paste is refused`. Useful scopes
are `macos`, `windows`, `worker`, `learning`, `tools` and `docs`.

Sign off every commit under the
[Developer Certificate of Origin](https://developercertificate.org/) (DCO).
The sign-off is a line at the end of the commit message that certifies you
wrote the change or otherwise have the right to submit it under this
project's license:

```text
Signed-off-by: Your Name <you@example.com>
```

`git commit -s` adds it from your Git name and email. To sign off commits you
already made, run `git rebase --signoff HEAD~3` (for the last three commits;
or `git rebase --signoff upstream/main` against a fresh fetch of this
repository) and force-push your branch. Commits that are not signed off are
not applied. There is no contributor license agreement.

## Using AI tools

You may use AI tools to help write a contribution, on these terms:

- **Say so.** The pull request names the tool and what it did: wrote code,
  wrote tests, reviewed, drafted docs.
- **Write the pull request description yourself,** in your own words, as you
  understand the change.
- **You are the author.** Understand every line you submit, be able to
  explain it in review, and run the tests yourself. "The tool wrote it" does
  not answer a review question.
- **Your sign-off covers generated code too.** Do not submit output that
  reproduces someone else's code unless its license allows it here and you
  keep its notice.
- **Keep other people's data out of the tools:** no recordings, private
  documents or unpublished code that is not yours to share.
- Large, unfocused changes with no sign that they were run or reviewed are
  closed without a detailed review.

Coding agents working in this repository read [AGENTS.md](AGENTS.md)
([CLAUDE.md](CLAUDE.md) loads it).

## How a pull request lands

The maintainers develop in a private repository, and this repository is
published from it; only that publication writes to `main` here.

1. You open a pull request here, and it is reviewed here, in public.
2. Once it is approved, a maintainer applies your commits to the development
   repository with your sign-off, and credits you by name in each of them.
3. The next publication brings the change to `main` here. A publication is a
   commit of its own, so your change appears inside it rather than as your
   original commits. The pull request is then closed with a link to that
   commit; GitHub shows it as closed, not merged.

## Licensing

The project is licensed under the [Apache License 2.0](LICENSE); see also
[NOTICE](NOTICE). Under section 5 of that license, what you contribute is
licensed under the same terms. Files carry no per-file license headers:
[REUSE.toml](REUSE.toml) records the licensing of every file. If a change
brings in code from a third party, keep its notice and add an annotation for
it to REUSE.toml in the same pull request.
