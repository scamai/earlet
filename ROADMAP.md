# Roadmap

Where Earlet is going, in three horizons. Nothing here has a date. Ideas
and questions belong in Discussions; the [changelog](CHANGELOG.md) records
what has landed.

The immediate target is a free Mac-first source preview. A standalone private
repository is the preparation point; source publication follows the
public content, rights and export gates. Windows source remains in the tree,
but Windows device work and public binary distribution follow later.

- [Now: toward a first release](#now-toward-a-first-release)
- [Next](#next)
- [Later](#later)
- [Open questions](#open-questions)
- [What will not change](#what-will-not-change)

## Now: toward a first release

- **A source tree people can build.** Finish removing unused inherited code
  and private identifiers, confirm rights and notices for the retained code,
  and verify the exact public snapshot and its build instructions.
- **Mac reliability on real hardware.** Verify quiet takes after the short-noise
  insertion fix. Keep intermittent repeated text and
  resource use during long speech under observation, then verify correction
  learning, human-voice recognition, microphone recovery and physical hold-key
  conflicts as devices are available.
- **Verify long-dictation recovery (stage 1).** The Mac now implements a
  ten-minute ceiling, hands-free silence stopping and recovery of failed
  takes. Automated checks pass; real microphone and failure-recovery checks
  remain. The Windows implementation has a five-minute ceiling and has only
  been built and tested on Linux.
- **Public contribution checks.** Shared checks run on pushes and pull requests;
  native Mac checks can be started manually. Add the DCO sign-off check before
  public contributions, keeping unsupported or unrun platform checks explicit.

## Next

- **A public Mac binary.** Distribution signing, hardening, notarization,
  bundled license notices and clean-machine installer acceptance follow the
  source preview.
- **Long dictations without the wait (stage 2).** Recognise finished stretches
  of speech while the key is still held, so releasing waits only for the last
  few seconds; then raise the ceiling to 20 minutes, with a "still listening"
  note at 10.
- **A microphone picker** in both apps.
- **Windows, when a Windows machine is available:** keep the source maintained;
  then run hardware validation, dependency packaging and native CI before any
  public binary. Further Windows behavior work waits for that environment.
- **Bluetooth headsets** checked on real devices.
- **Accessibility:** VoiceOver and Narrator walk-throughs, and measured
  contrast, for every window and indicator state.
- **Updates** through a signed update channel.

## Later

- **Windows on ARM**, once a speech engine runs there (CTranslate2 has no
  Windows ARM64 build).
- **Android and iOS**, through a keyboard or input-method adapter; the
  constraints are in [mobile/README.md](mobile/README.md).

## Open questions

- Should the indicator show your words while you speak by default? It also
  appears on shared screens. Today the Mac shows them and Windows does not.

## What will not change

Capturing, recognising and typing your speech will never need an account or
a sign-in.
