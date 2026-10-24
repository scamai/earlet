## Summary

<!-- In your own words: what this changes and why. Link the issue or discussion. -->

## Platforms touched

- [ ] macOS app
- [ ] Windows app
- [ ] Speech worker (`runtime/`)
- [ ] Learning rules (`LEARNING.md` and its four implementations)
- [ ] Tools, tests or docs only

## How it was tested

<!-- Paste the summary lines of `python3 tools/run_tests.py` and name the machine it ran on. -->

- [ ] Ran on real hardware. Device and OS:
- [ ] Not run on a device. A Linux test, a cross-build, a fake or synthetic audio is not evidence of how an app behaves on a device; say which of them this relies on.

## Checklist

- [ ] One change, with its tests; a new check fails when the thing it checks is broken.
- [ ] UI changes: light and dark screenshots from the render modes are attached.
- [ ] Learning rule changes: `LEARNING.md`, `tests/learning_reference.py`, the C# and Swift ports and `tests/fixtures/learning-golden.json` change together.
- [ ] The contract docs and `CHANGELOG.md` (Unreleased) are updated where users would notice.
- [ ] Every commit is signed off under the Developer Certificate of Origin (`Signed-off-by:`, added by `git commit -s`).
- [ ] AI assistance: none, or named here with what it did:
