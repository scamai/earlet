<!-- English only -->

[← Earlet README](../README.md)

# Attended acceptance guide

How to check an installed Mac build: run the automatic check first, then
confirm correction learning with your own voice. Two commands do the machine
part. `auto` checks the installed recognition engine and the learning rules.
`watch` shows whether the words you name get recorded while you edit a
dictation. The attended steps are the rest of the test.

**An automatic PASS still needs the attended steps.** Synthetic speech checks
the program path. The microphone, the hold key, edits in the Witty input field
and whether the next recognition comes out right can only be checked in real
use. For every automatic run, the generated `result.json` is the record. The
tool's checks are implemented in [tools/acceptance.py](../tools/acceptance.py);
this page documents the procedure and exit statuses.

## Contents

- [Preparation and commands](#preparation-and-commands)
- [Attended script](#attended-script)
- [Reading the watch output](#reading-the-watch-output)
- [When something fails](#when-something-fails)
- [What this test does not cover](#what-this-test-does-not-cover)
- [Related files](#related-files)

## Preparation and commands

You need, on a Mac with Apple Silicon:

- the complete offline package installed at `/Applications/Earlet.app`,
  which carries its own Python runtime and MLX speech model;
- Python 3 (the tool uses only the standard library);
- the Xcode command line tools, for the Swift compiler (`xcrun swiftc`);
- an installed English macOS voice. The default is Samantha.

The tool never downloads a model. Run it from the `apps/dictation` directory of
this repository.

```bash
python3 tools/acceptance.py auto
```

Options:

- `--app PATH` checks a bundle other than `/Applications/Earlet.app`.
- `--voice NAME` uses another installed English voice instead of Samantha.
  `say -v '?'` lists the installed voices.

`auto` checks the installed bundle and its signature, speaks "We have a
server called Miles." with the chosen voice, runs the installed Python/MLX
engine through seven learning and scene stages and a confirmed-alias check,
and talks to the installed worker over its real framed protocol. The learning
stages are driven by a probe compiled from the Swift learning code in this
checkout; it writes only a scratch vocabulary, never your personal one. Every
stage that selects Miles must also recognize Miles in the synthetic speech.
The run records the build number, version and file fingerprints but does not
compare the build number with anything, so a different build cannot fail it.
Check the build number in `result.json` yourself.

Read the terminal first. It prints `PASS installed-bundle` (with the build
number), `PASS learning-pipeline` and `PASS worker-ipc` as those checks pass,
with progress lines in between. The final line gives the automatic status,
the human status and the path of the report:

```text
evidence/acceptance/dictation-acceptance-<date>-<time>-<random>/result.json
```

Each finished step prints `Ran <step> (exit 0)`. The final line is
`Automatic acceptance <STATUS>; human acceptance NOT_RUN. Report: <path>`.

When the run did not pass, the reason follows that line. Open the report when
something went wrong. The same folder keeps each step's `.stdout` and
`.stderr`, including the per-stage results of the learning pipeline.

| Status | Meaning |
|---|---|
| PASS | Every automatic step passed. |
| FAIL | A check ran and failed, for example the signature, a step that exited with an error, a learning stage or the worker IPC. |
| INCOMPLETE | The check could not finish, for example: not macOS; a missing or different bundle (files, model weights, bundle ID or provider); no Swift compiler or voice; a timeout; or your own learning file changed during the run or could not be compared. |

The process exits 0 for PASS, 1 for FAIL and 2 for INCOMPLETE. The report
records the observed checks and failure reason. Human status is always
reported separately as `NOT_RUN`.

`auto` fingerprints your personal learning file before and after the run and
never writes or restores it. If it changed during the run, for example
because you dictated and corrected something meanwhile, a PASS becomes
INCOMPLETE and the report notes it. Do not dictate while `auto` runs.

Then start the watch in a separate terminal window, leave it open and switch
back to Witty:

```bash
python3 tools/acceptance.py watch --word Miles --word repo --seconds 3600
```

`--word` is required and can be repeated; each word is 1-128 printable
characters and matches regardless of case. `--seconds` defaults to 600 (10
minutes) and accepts up to 3600. Use the maximum for the attended script. If
the watch ends before you finish, start it again; its new `baseline` line
shows the state carried over. Press Control+C to stop early.

The watch only reads the learning record the app has saved, checking it every
half second. It prints only the words you named, their counts, the scene and
anonymized record IDs; it never prints what you typed or any other word. It
writes its log to the `watch.jsonl` path it prints, and a summary to
`watch-result.json` beside it. Both commands keep their output in the ignored
`evidence/` folder on this Mac; nothing goes into Git.

The watch opens with `Read-only watch: ...` (a reminder to edit in the
original field within 90 seconds without sending) and `Watch log: <path>` (the
`watch.jsonl` path). It ends with `Watch ended: N target record changes, M read
errors. ...` (N changes to watched words, M read errors).

## Attended script

Keep each draft; you never need to send anything.

Read all the steps first. In Earlet's settings, confirm that "Learn from my
corrections" is on and that the right dictation hold key is chosen. Every time,
let Earlet insert the text itself, then edit **in the same field, within
the inserted span, within 90 seconds**. After an edit, wait about 6 seconds
before looking at the watch window: the Mac publishes an edit only after the
field has been quiet for 4 seconds
([LEARNING.md §7](../LEARNING.md#7-observers-and-privacy)), Witty is read
every 0.6 seconds and refreshes the text it reports only every 0.5 seconds,
and the watch checks every 0.5 seconds. Finish the edit and the retraction of one
dictation before you start the next. Typing a whole sentence by hand is not
the same as correcting a dictation.

In Witty, only the Codex composer (the `› ` input box) is observed; other
terminal programs are not
([LEARNING.md §7](../LEARNING.md#7-observers-and-privacy)).

1. **Baseline.** In Witty, say "We have a server called Miles." Note whether
   it inserted Miles, Mies or another spelling, and go straight on to steps 2
   and 3. After step 4, repeat the same sequence with "Please commit the
   changes to the GitHub repo." and record the result for repo separately. Do
   not edit the vocabulary first, and do not count one correct result as
   learned.
2. **Change one letter.** If the first sentence came out as Mies, add only an
   l before the e to make Miles. Check that Miles gains a new anonymized record
   and that its scene is `terminal` (Witty's scene tag; you do not need to
   switch to Terminal). If the sentence was already correct, record "not
   triggered this time". You can then use a new proper noun such as Nerovia and
   open another watch with `--word Nerovia`. Start from a real misrecognition;
   do not clear the existing vocabulary.
3. **Same draft and retraction.** Still in this one dictation, change the edit
   back to the original wrong spelling. The contribution of the correction you
   just made should be withdrawn; older evidence for the word, if any, may
   remain. Then fix it again. It must stay the same anonymized record, not
   become two independent confirmations. Record what the watch showed.
4. **Independent retests.** Start a new dictation and say the same sentence;
   do this five times. Each time, note "correct as inserted" or "needed an
   edit". If it is wrong again, correct it once more: that is a new
   dictation with a different anonymized record. A recognition that is already
   correct does not vote for itself. Record the first attempt and the retests
   separately.
5. **Other apps.** Say the same technical sentence into an unsent WeChat
   draft; if needed, correct the misspelling and check that the scene is
   `wechat`. Repeat once in a scratch TextEdit document, where the scene should
   be `other`. Words can appear across apps. The scene changes the ranking; it
   does not promise different output as soon as you switch apps.
6. **Counterexample and speed.** Say "Mies is an ordinary fixture word." and check
   that a correct Mies was not changed to Miles. Then say two everyday
   sentences without these names. Each time, roughly note the time from
   releasing the key to the text appearing, and any missing words, stalls or
   double insertions. A wrong replacement fails this step.

A word may enter the candidates after one correction or need several
independent ones. Being saved in the vocabulary, becoming an alias that is
applied automatically, and finally being recognized correctly are three
different outcomes. There is no guarantee that a fixed number of corrections
makes recognition right.

### Result template

Copy this and fill it in:

```text
Date / app build number:
auto: PASS / FAIL / INCOMPLETE; result.json path:
App: Witty / WeChat / TextEdit
Test word:
First output:
One-letter edit: recorded / not recorded / not triggered this time
Repeated edits of one draft: same anonymized record / unexpected new record
Retraction: this contribution withdrawn / not withdrawn
Independent retests: correct as inserted __ / 5
Scenes observed:
Wrong replacement in the counterexample: none / yes (write the sentence)
Key release to insertion: about __ s; missing words or stalls:
watch.jsonl path:
```

## Reading the watch output

The first line, `baseline`, is the state when the watch started; each later
`change` line is a change in a watched word. It does not write a line per
keystroke. Each line carries the record's state: `readable`, `missing` (no
learning file yet) or `unreadable` (the file could not be read or parsed;
only the error type is logged, and it counts as a read error).

| Field | Meaning | How to judge it |
|---|---|---|
| `learned` | Whether the word is saved in the learned vocabulary | `true` proves it was saved, not that the next recognition will choose it. |
| `support` / `opposition` | The current supporting and opposing evidence counts | Compare with the baseline; a word that already existed need not start from 0. |
| `observations` / `id_hash` | The independent dictations that involve this word, with an anonymized ID for each | Edits of one draft keep the same ID; only a new dictation adds a record. A retraction can remove this dictation's contribution. |
| `scene` | The coarse scene where the correction happened | Witty is `terminal`, WeChat is `wechat`, ordinary editors are `other`. `unknown` means the record carries no recognized scene, as with records made before scenes existed or without app information. |
| `pending_aliases` / `confirmed_aliases` | How many pending and confirmed rewrite rules target this word | The ordinary word Mies must not get a forced rewrite just because it was changed to Miles. |

The watch can only prove that the saved state changed. Whether the next
recognition improved is what the five attended retests show. The watch exits
0 when a requested word's saved state changes with no read error, or 2
otherwise. Neither result means the attended acceptance passed as a whole.

## When something fails

- **`auto` is INCOMPLETE:** read the `reason` in the report. If the app, the
  Swift tools or the voice is missing, install it and run the same command
  again. For a missing voice, either install Samantha or pick an installed
  English voice with `--voice`. If the report says your learning file changed
  during the run, run `auto` again without dictating. A missing item is never
  counted as a pass.
- **`auto` is FAIL:** keep `result.json` and the matching `.stderr` file in the
  same folder and pass on their paths. When the bundle check got that far,
  the report includes the build number and file fingerprints, which show
  whether the installed version is the one you meant to test. A signature
  failure stops before they are recorded.
- **No change after an edit:** check that you are watching the correct
  spelling, that learning is on, that Earlet inserted the text, and that
  you edited the original span within 90 seconds. If the field is not one
  Earlet is verified to observe, if the watch shows `unreadable`, or if all
  of these hold and still nothing changes, record the step as failed and keep
  the logs. Do not keep clearing the vocabulary.
- **Saved but still misrecognized:** saving what you taught and choosing it
  during recognition are separate stages. Hand in the filled template and the
  watch log so the cause can be traced; no other chat content is needed.

## What this test does not cover

`auto` uses synthetic speech made on this Mac, the Swift learning module from
the current source and the installed Python/MLX engine. It is not an
end-to-end test of the installed native app, a real microphone or the edit
observer, and a valid signature does not prove that the system permissions
work. It records prompt construction, beam candidates and confirmed-alias
postprocessing, but it does not prove that the beam beats other algorithms.

`watch` is a read-only observer of the vocabulary file. It does not listen to
the keyboard for the app, send messages or fix permissions. The attended steps
create learning records the normal way; do not clear the existing vocabulary
for the test. Native Windows devices, Bluetooth microphones, every other app
and long-term accuracy are outside this Mac acceptance.

## Related files

- [tools/acceptance.py](../tools/acceptance.py), the tool
- [LEARNING.md](../LEARNING.md), the learning rules
- [Development and tests](development.md#running-the-tests), the automated suites
- [Known limitations](../README.md#known-limitations), the source preview's device limits

This guide needs no web service or account.
