# Automatic vocabulary learning

Earlet learns the words it gets wrong from the corrections the user makes
right after a dictation, and uses them in later dictations on both desktop
apps. A distinctive word the user fixes is learned on the first fix, unless it
looks like a password or a key, which waits for the same fix on a second
dictation. A sound-alike rewrite ("trovio" → "Trovioo") is learned only after
the same fix on two separate dictations. Everything learned is listed in the app
window, can be removed one entry at a time or all at once, and a removed entry
is never learned again by itself. The user can also teach a word directly.

The rules below are the product's contract. They are executable in
`tests/learning_reference.py`, which takes the reversible evidence of §4.6 from
`tests/adaptive_reference.py`. The shared cases in
`tests/fixtures/learning-golden.json` are checked against that reference
(`tests/test_learning_golden.py`), against the C# implementation
(`windows/Dictation.Core/Learning/`) and against the Swift implementation
(`macos/Sources/DictationLearning/`); `tests/test_adaptive_learning.py` adds
the §4.6 cases for the reference. Change a rule in all four places in one
commit, or not at all.

Two parts run only in the worker (`runtime/transcription_worker/`): the scene
ranking of §4.7 (`context_vocabulary.py`; the golden file has no scene cases,
and the hosts only tag observations and send the scene) and the prompt order
and per-tier search adjustments of §6 (`learned_vocabulary.py`). Change those
together with this file and their tests (`test_context_vocabulary.py`,
`test_learned_vocabulary.py`). The tier names the Vocabulary list shows are
computed again by each host with the same thresholds (`learningConfidence` in
`macos/Sources/DictationLearning/LearningEvidence.swift`, `ConfidenceOf` in
`windows/Dictation.Core/Learning/DictationLearning.cs`), so a threshold change
also changes both hosts and their tests (`LearningTests.swift`,
`VocabularyModelTests.cs`).

**Contents**

- [1. Data flow](#1-data-flow)
- [2. Ordinary words](#2-ordinary-words)
- [3. From a correction to candidates](#3-from-a-correction-to-candidates)
  - [3.1 Tokens](#31-tokens)
  - [3.2 Diff](#32-diff)
  - [3.3 Classifying a correction](#33-classifying-a-correction-first-match-wins)
- [4. What is learned](#4-what-is-learned)
  - [4.1 Words (the vocabulary)](#41-words-the-vocabulary)
  - [4.2 Aliases (rewrites)](#42-aliases-rewrites)
  - [4.3 Teaching](#43-teaching)
  - [4.4 Removing](#44-removing)
  - [4.5 Caps](#45-caps)
  - [4.6 Reversible evidence and confidence](#46-reversible-evidence-and-confidence)
  - [4.7 Scene relevance](#47-scene-relevance)
- [5. The file (`dictation.learning/1`)](#5-the-file-dictationlearning1)
- [6. What the worker does with it](#6-what-the-worker-does-with-it)
- [7. Observers and privacy](#7-observers-and-privacy)
- [8. Evidence and limits](#8-evidence-and-limits)

## 1. Data flow

```text
hold → worker transcript → paste into the focused field
     → observer watches THAT span for up to 90 s (host, per platform)
     → (dictated text, edited span) → learning policy (host) → learning.json
worker, on every transcribe: reads learning.json
     → learned words join the vocabulary (recognition prompt + declared casing)
     → learned aliases join the spoken-alias table (exact rewrite after decoding)
```

- **One writer.** Only the host app writes `learning.json`, through an atomic
  replace. The worker only reads it (re-read when its size or mtime changes).
- **Path.** `<data>/dictation/learning.json`, beside the user's own
  `bias_aliases.json`: `%LOCALAPPDATA%\Dictation\dictation\` on Windows,
  `~/Library/Application Support/Dictation/dictation/` on macOS. The worker
  resolves the same directory in `spoken_aliases.py`.
- **Precedence of spoken aliases:** the user's own `bias_aliases.json` wins over
  learned aliases, which win over the shipped (empty) defaults. The user file's
  `removed` list suppresses learned and shipped entries alike.
- **Switch.** "Learn from my corrections" turns the observer off. It does not
  unlearn anything: learned entries stay in use until removed. It is on by
  default; this keeps the normal correction workflow useful without requiring
  a separate setup step.

## 2. Ordinary words

A token is *ordinary* when its lowercase form, cut at the first apostrophe and
split on `-`, `.` and `_`, has every part in the word list, directly, after one
of three plural rules (`-ies`→`-y`, drop `-es`, drop `-s`), or once a verb
ending is taken off. The verb endings are `-ed` and `-ing`, dropped (`played`,
`playing`), dropped with an `e` put back (`changed`, `making`), or dropped with
one of a doubled final consonant (`stopped`, `running`; any ASCII letter but a
vowel); and `-ied` turned back into `-y` (`tried`). The base an ending leaves
is looked up with the plural rules and needs at least 3 code points, so `Ying`
is not `y` + `-ing`. Like the plural rules these are crude on purpose, and they
make a few names ordinary: `Ewing` (`ewe`) and `Harding` (`hard`), neither of
which is in web2 in any case. Such a name is no longer learned as a word on its
first correction; it can still become an alias after two (§4.2). A part that is
itself an excluded name (below) is never made ordinary by a verb ending
(`Downing` stays distinctive although `down` is a word). A part with no
letter at all (`10`, `4417`, `1-800`) is ordinary, so a number on its own is
never vocabulary: a fix whose corrected text has no letter (a PIN, a phone
number) is never stored, prompted or rewritten, and never enters the evidence
journal (§4.6). A number that comes with a
distinctive word is different: the number in `Trovioo 44171234` makes that
correction secret-shaped, so it is written to `pending` in plain text and
learned, number included, when a second dictation repeats it (§4.1, §5). The
word list is Webster's Second (`web2`): Windows embeds a curated 235,642-line
subset (`windows/Dictation.Services/Resources/web2-words.txt.gz`) with 334 lines
containing the product name removed; macOS reads `/usr/share/dict/web2` from the
system. The two copies are intentionally not byte-identical. **If the list cannot be read, every token
is ordinary** (fail closed): nothing is distinctive and no alias is learned;
taught entries still work.

The learning loaders use only entries present in lowercase in that file;
capitalized-only names such as `Miles` and `Jarvis` must remain eligible
vocabulary. Lowercase counterparts keep `Apple`, `A` and `I` ordinary. This
filter is specific to the shipped Webster source: explicitly supplied test or
caller word lists remain case-insensitive declarations of ordinary words, and
the separate import/scan dictionary keeps the complete file. Excluded names
are checked before plural folding (`Miles` must not match `mile`). Other plural
rules still apply: `Mies` is treated as ordinary by the `-ies` → `-y` rule,
while `Miles` is excluded, so the synthetic `Mies` → `Miles` correction can
learn the written word immediately but cannot create a forced alias.

A text is *distinctive* when at least one of its non-CJK tokens has a lowercase
letter and also an uppercase letter after its first code point (`SkyDock`,
`iPhone`), mixes letters and digits (`GPT4`), or is not ordinary. CJK-only text
is never distinctive (there is no lexicon for it).

## 3. From a correction to candidates

### 3.1 Tokens
Code points are classified by Unicode general category. A CJK code point
(Hiragana/Katakana U+3040–30FF, U+3400–4DBF, U+4E00–9FFF, Hangul U+AC00–D7AF,
U+F900–FAFF, U+20000–2FFFF) is a token by itself. Otherwise a token is a maximal
run of letters, marks and decimal digits (`L*`, `M*`, `Nd`), which may continue
across one of `' ’ - . _` when a word character follows it (`don't`, `Node.js`).
Everything else separates tokens. Indices are code points.

### 3.2 Diff
Token texts are aligned by longest common subsequence, compared exactly (case
matters). Ties break deterministically: on a mismatch, drop the dictated token
when that keeps the LCS at least as long as dropping the edited token. Maximal
runs of non-matching tokens between matches form hunks. A hunk with tokens on
both sides is a correction; pure insertions and deletions are ignored. The
heard/meant texts are the original substrings from the first to the last token
of each side, so spacing inside the span is kept.

Nothing is extracted when the dictation is empty, when either side has more than
200 tokens, or when a dictation of 4 or more tokens keeps fewer than half of its
tokens (the user rewrote it rather than corrected it).

A hunk that reaches the last token of both texts and has more tokens on the
edited side is rejected as `boundary`, and so is one that reaches the first token
of both and grows there. Text typed straight after the span ("Call trovio" →
"Call Trovioo now") joins the last hunk, and the observers cannot tell such a
continuation from a fix, so it is refused rather than learned as "Trovioo now".

### 3.3 Classifying a correction (first match wins)

| Test | Result |
|---|---|
| more than 4 non-CJK tokens, or more than 8 CJK characters, on either side | rejected `too_long` |
| the two texts are equal ignoring case | `casing` |
| equal after lowercasing and removing `' ’ - . _`, token by token | rejected `punctuation` |
| equal after also removing the spaces between tokens | `respacing` |
| one is the other plus `s` or `es`, and the shorter side is all ordinary words | rejected `inflection` |
| the heard side has CJK and the meant side is only lowercase ASCII letters, ASCII apostrophes (`'`) and ASCII spaces | rejected `composition` (pinyin or romaji an input method has not committed yet) |
| one side has CJK and the other does not | `transliteration` |
| each side is one non-CJK token, the heard token is ordinary, and the meant token is distinctive | `spelling` (word only; the ordinary heard token cannot become an alias) |
| Levenshtein distance of the squashed forms over the longer length > 0.5 | rejected `rewording` |
| otherwise | `spelling` |

The single-word exception remembers unfamiliar terms such as `repo` when the
recognizer wrote an ordinary word such as `wrapper`. It adds the corrected word
to the recognition vocabulary; it does not replace future legitimate uses of
`wrapper`. Whole-sentence rewrite, boundary, secret-shaped text, removed-word,
and unreadable-lexicon safeguards still apply. Ordinary-to-ordinary rewording
and multi-token distant rewrites do not qualify.

## 4. What is learned

### 4.1 Words (the vocabulary)
Any candidate whose meant text is distinctive teaches that text as a word,
immediately, unless the word was removed before or the text is secret-shaped. A
learned word gains support from independent insertions; a revision of the same
insertion replaces its previous support (§4.6). Learned spelling follows the
latest supporting correction. Taught and unknown-origin entries are unchanged.

A text is *secret-shaped* when one of its tokens has at least 8 code points and
at least 2 digits (`Sunshine42`, `Hunter42`, the number in `Trovioo 44171234`),
or when two of its non-CJK tokens are glued together, with no white space
between them, by a character other than `' ’ - . _` (`p@ss`, `AT&T`; not
`Trovioo，好`, since CJK text has no spaces). That is what a password, licence
key, account number or one-time code typed over a dictated word looks like, and
the observer cannot tell it from a fix. A secret-shaped word that is not already
known waits in `pending` as the pair heard → meant, under the rules of §4.2: it
needs 2 distinct dictations, the same tombstones block it, and so does a taught
alias for the same sound. When the pair is promoted the word is learned, and the
pair becomes an alias too only if §4.2 admits it. Short tokens (`GPT4`, `M4`,
`Hunter2`) are learned at once, and teaching (§4.3) is never delayed. The
thresholds are a judgment, not a measurement: `iPhone15` and `Llama3.1` wait for
a second correction too.

### 4.2 Aliases (rewrites)
A candidate may become an alias `heard → meant` when all of these hold:
- its kind is `respacing`, `spelling` or `transliteration` (never `casing`, which
  declared casing already handles through the word);
- the meant text has a letter in it (a number is never a rewrite target);
- the two texts, compared without case, do not differ only by `s`, `es`, `ed`
  or `ing` added to the end of one of them (`ies` or `ied` for a final `y`:
  `app` → `apps`, `deploy` → `deployed`, `try` → `tried`, `city` → `cities`);
  an inflection is never an alias, whatever the word list says, though its
  meant side can still be taught as a word (§4.1);
- the heard key (lowercase, whitespace collapsed) has at least 2 characters;
- the meant text does not contain the heard text as a run of whole tokens
  (a rewrite that contains its own trigger would rewrite its output again);
- for `respacing`: the meant text is distinctive; if the heard text has CJK: always;
  otherwise a single heard token must not be ordinary, and a heard phrase needs at
  least one single letter or non-ordinary token.

The pair waits in `pending` (with the secret-shaped words of §4.1) until it has
been seen on **2 distinct dictations** (dictation ids are random per insertion;
the same id counts once), with at least two more supporting than opposing
insertions (§4.6). A competing spelling suspends a weak or tied alias; it does
not automatically win just because it is newer. A *taught* alias is never
replaced, and a learned alias cannot rewrite a taught word. A removed pair is
never learned again, and neither is any alias that writes a removed word.
Pending pairs expire 30 days after they were last seen.

### 4.3 Teaching
The user types a written form and, optionally, how it sounds. The word is taught
at once (origin `taught`, never evicted by learned words, never re-cased); a
sounds-like form sets a taught alias at once. Teaching clears the tombstone of
what it teaches. It is refused with `empty` (blank or without a single word
character, or a sounds-like shorter than 2 characters), `too_long` (more than 60
characters, 6 non-CJK tokens or 12 CJK characters), `self_containing` (the
written form contains the sounds-like form) or `full` (500 taught words, or 200
aliases none of which is learned).

### 4.4 Removing
Removing a word removes it, every alias that writes it, and every pending pair
that would, and records tombstones for the word and those aliases; while the
word's tombstone stands, no alias that writes it is learned. Removing an alias
keeps its word. "Forget all" empties everything: words, aliases, pending pairs,
tombstones and the evidence journal of §4.6.

### 4.5 Caps
At most 100 learned words (the least recently seen go first), 500 taught words,
200 aliases, 500 pending pairs and 1,000 tombstones of each kind. Only learned
aliases are evicted to make room, least recently seen first: a taught alias is
never evicted, and teaching refuses with `full` instead (§4.3). An entry whose
origin this version does not know is never evicted (§5). A pending pair lists
at most 4 dictation ids (`PENDING_DICTATION_IDS`), the most recent ones; its
count is still the support of every dictation behind it (§4.6).

### 4.6 Reversible evidence and confidence

Each insertion has a random stable ID. After an admitted edit settles, its
latest correction spans replace that ID's entire previous contribution. A
return to the original text removes the contribution. Identical repeats change
neither counts nor timestamps. This identity survives saving and reloading.
A correction whose meant text has no letter (a PIN, a phone number) is never
journaled: it is dropped before the record is built, and an observation left
with no correction adds or keeps no record, as one with no corrections does.
Unchanged dictated words do **not** reinforce themselves: no edit is not proof
that the recognizer was right.

The optional `evidence` journal stores, for each observation, only its ID, its
time, a bounded weight, the admitted corrected spans with their
word/alias/secret admission flags, the coarse scene tag of §4.7 when one was
recorded and, for a record migrated from old counts, the original first
sighting (`first_seen`). It keeps the latest **512 observations**, ordered by
timestamp then code-point ID; each has at most 32 corrections of at most 256
code points on each side. Older votes fall out of this bounded history. This is
local memory, not acoustic-model training. It contains no sentence, app name,
field value or audio.

Rebuilding automatic entries counts each insertion at most once per word or
pair (target keys ignore casing). A valid spelling correction **away from a
learned target** opposes that target. Competing targets under the same heard
key also oppose one another's aliases. One insertion can never both support and
oppose the same entry. `count` is support; `opposition` is opposition. Net
support of 1 keeps a normal word tentative, 2–3 confirms it, and 4 or more
establishes it. Net zero suppresses the automatic word; its evidence remains
reversible. Secret-shaped words and aliases require net support of at least 2.
All existing ordinary-word, composition, rewrite, secure-field and removal
guards still apply.

These are engineering defaults, not measured accuracy thresholds. Manual
teaching always wins. Unknown-origin entries keep their original fields.
Unconfirmed pair evidence expires after 30 days before a new vote is added;
confirmed aliases remain until contradicted, removed or evicted. There is no
silent time-based increase in confidence.

Old schema/1 vocabulary is migrated lazily on the first eligible observation.
Automatic word/alias counts become prior evidence capped at 4, keeping their
first/last timestamps; pending IDs are merged without duplicating word support.
The file schema remains `/1`. `evidence.version = 1`, `adaptive = true`, and
`opposition` are optional extensions; unknown keys survive round trips. An
unknown/malformed journal stops automatic updates without replacing the user's
vocabulary. Teach, remove, Forget all and unreadable-file replay remain
available.

### 4.7 Scene relevance

New correction observations may also carry a `scene`: `terminal`, `wechat` or
`other`. The host derives it from the verified insertion application's identity,
then discards that identity. On macOS the scene comes from the application's
bundle ID, and Witty belongs to Terminal. On Windows it comes from the process
name: WeChat or Weixin is `wechat`, and a name on the formatter's terminal list
(`DictationTextFormatter.IsTerminal`) is `terminal`; the Windows observer
refuses most of those terminals outright (§7). Missing app information leaves
the tag absent; old observations are never assigned a guessed scene. Revising
one insertion retains its original tag and replaces its contribution;
retracting it removes the scene contribution too. Restart preserves both.

Scene relevance orders words that already passed §4.6. It does not admit a
suppressed word, increase a beam delta, or create an alias. Among admitted words,
manually taught entries retain the first tier. Automatic entries use a small
interpolated prior, computed from the existing bounded journal:

```text
g(w) = max(1, stored support(w) - stored opposition(w))
Pglobal(w) = (g(w) + 20 / vocabulary_size) / (sum(g) + 20)
n(w, scene) = max(0, local support(w) - local opposition(w))
Pscene(w) = (n(w, scene) + 20 * Pglobal(w)) / (sum(n) + 20)
score(w) = log(Pscene(w)) + 0.05 * exp(-age_days(w) / 30)
```

One insertion votes at most once for each word; positive evidence wins over a
negative vote within that insertion. More independent local corrections give
the scene more influence. Global vocabulary remains available across apps, so
Terminal words can still be used in WeChat. The finite prompt/search budgets can
still exclude a low-ranked term; retention is not guaranteed selection. Recency
is bounded and changes rank only. This is a fixed, cheap scoring rule with
dynamic evidence, not a trained scene classifier or next-word model.

The ranking runs only in the worker
(`runtime/transcription_worker/context_vocabulary.py`). Missing/unknown scene,
invalid journal, or no positive net evidence for this scene uses the exact
existing global order. Scene counts cover the bounded 512-observation journal,
while the global prior uses stored entry counts. The constants are conservative
engineering defaults, not accuracy-optimal claims. No window titles, repository
paths, conversation text or app identifiers are stored as context.
Repo-specific vocabulary and semantic topic inference are not implemented.

## 5. The file (`dictation.learning/1`)

```json
{
  "schema": "dictation.learning/1",
  "words":   [{"text": "Trovioo", "origin": "correction", "count": 2, "first_seen": 1790000000000, "last_seen": 1790000001000}],
  "aliases": [{"heard": "trovio", "meant": "Trovioo", "origin": "correction", "count": 2, "first_seen": 1790000000000, "last_seen": 1790000001000}],
  "pending": [{"heard": "javis", "meant": "Jarvis", "count": 1, "dictations": ["<random id>"], "first_seen": 1790000000000, "last_seen": 1790000000000}],
  "removed": {"words": [{"key": "sol", "at": 1790000000000}], "aliases": [{"heard": "so", "meant": "sol", "at": 1790000000000}]}
}
```

The example shows rows in the shape schema/1 started with, which every reader
still accepts; the worker reads such a row as a legacy row (§6). The hosts now
write three more things (§4.6): `"adaptive": true` and `"opposition"` (the
opposing weight) on every automatic word and alias they rebuild, and a
top-level `evidence` journal, for example:

```json
"evidence": {
  "version": 1,
  "observations": [
    {"id": "<random id>", "at": 1790000000000, "weight": 1,
     "corrections": [{"heard": "trovio", "meant": "Trovioo",
                      "word": true, "alias": true, "secret": false}],
     "scene": "terminal"}
  ]
}
```

Times are Unix milliseconds (UTC). `words` are stored most recently seen first;
the worker combines that order with confidence and optional scene relevance.
The file holds no dictated sentence or application identifiers; it may hold the
coarse scene tags in §4.7. It is not free of field contents: every entry is
text the user typed over a dictated span, kept in plain text with its counts and
times. Secret-shaped corrections (§4.1) wait in pending evidence until a second
dictation confirms them. The worker does not use unconfirmed evidence as
vocabulary, and the app does not list it. Unconfirmed pair eligibility expires
after 30 days; the bounded journal retains corrected spans until evicted or
"Forget all". The journal is private local data, not an audit log.

Reading is tolerant within the schema: a missing top-level list reads as empty,
an entry with an `origin` this version does not know is kept as it is (never
evicted, never re-cased), and unknown keys are written back unchanged. Two
failures are handled differently:

- **Undecodable** (not JSON, or another `schema`): the worker treats it as
  empty; the host moves it aside to `learning.json.bad` (replacing an older one)
  and starts empty. "Forget all" does not delete `learning.json.bad`.
- **Unreadable** (an I/O or permission error): the file is left exactly where it
  is and never overwritten. Learning goes on in memory; every later change tries
  the read again, and once it succeeds the changes made in the meantime are
  replayed onto the file's contents and saved. While anything is unsaved, the
  Vocabulary section says "Your vocabulary isn't saved yet. Earlet keeps using
  it and will try again." and offers Save again, which tries at once
  ([docs/design-macos.md](docs/design-macos.md#correction-notices-save-receipts-and-save-again)).
  Each app tries a last save when it quits.

## 6. What the worker does with it

- **Words** start ordered by evidence strength, then recency: manually taught,
  established (net ≥4), confirmed (2–3), tentative (1). Legacy rows without
  adaptive metadata keep their previous strength. Net-zero automatic rows are
  excluded. When valid scene evidence exists, §4.7 reorders this eligible set
  before either recognition budget is applied, preserving each word's strength.
  The Vocabulary list shows Tentative, Confirmed or Established for
  adaptive words; older rows keep their origin label. Words are prepended
  (deduplicated case-insensitively) to the request's `bias_phrases`. On macOS
  (`mlx`) they become Whisper's `initial_prompt` through `_vocabulary_prompt`
  (223-token budget, most relevant kept). On Windows (`faster`) the same prompt
  is passed to faster-whisper, which applies it to the first 30-second window
  of a call only. A hold with automatic language is decoded in spans of at most
  24 s and every span call is prompted; a hold with a pinned language is one
  whole-utterance call. The prompt's accuracy effect on that CPU engine is
  **not measured** in this source preview. Both engines then restore the declared
  casing of those words. A word
  learned from a correction restores case only; respacing or folding its spaced
  form ("sky dock" → "SkyDock") waits for the two-dictation alias (§4.2). A
  taught word may also be respaced. The hosts' sentence capitalisation still
  runs after this: a word with an inner capital (iPhone) is left alone, but a
  word taught all in lower case (`npm`) is capitalised at the start of a
  sentence. For MLX chunks pinned or detected as English, the generated carrier
  sentence is English too, because the Chinese carrier made an English decode
  translate or loop (reproduced with exactly `repo` and `repository`, on both
  the prompt-only and beam paths). Explicit preview prompts and the explicit
  `DICTATION_MLX_SCRIPT_SEED` override remain authoritative.
- **Final macOS search** combines that prompt with a two-candidate beam on
  the same MLX/Metal model whenever Earlet has active vocabulary. The trie
  uses up to 32 of the prompt's most relevant terms, with bounded log-score
  adjustments per matching token: tentative 0.2, confirmed 0.35,
  established/manual/legacy 0.5. Confidence stays attached to its word through
  merging, prompt trimming and trie caching. An explicit request delta
  overrides automatic evidence; an explicitly requested word without a delta
  keeps the engine default. Unfinished matches lose their extra score;
  completing a term again earns no extra credit. The acoustic scores used by
  speech/fallback gates do not contain the vocabulary adjustment. Empty
  vocabulary, previews, other clients and word-timestamp requests keep their
  previous decode paths. `vocabulary_search: false` on a transcribe request
  provides a prompt-only comparison; normal native hosts need no new setting.
  The comparison baseline retains the same beam width with no vocabulary.
  Search failures fall back to prompt decoding for the rest of the hold and
  are reported in `stats.vocabulary_search` and counts-only worker logs.
  Temperature retries use the upstream sampler, not the beam. The beam is a
  local adapter, because mlx-whisper 0.4.3 has no beam decoder, and it avoids
  moving recognition to the older Torch/CPU path. How it keeps upstream
  windowing and timestamp decoding, shares audio-attention caches, reorders
  only text-attention caches and corrects two upstream timestamp-mask defects
  (with local regressions and the upstream tracking links) is documented in
  `runtime/transcription_worker/mlx_beam.py`.
- **Aliases** are applied after decoding by `spoken_aliases.apply`, once per
  dictation. A match that already lies inside an occurrence of its own written
  form is left alone, so "flow" → "Whispr Flow" does not grow to "Whispr Whispr
  Flow". Matching is case-insensitive. A spoken form's Latin or digit edge
  must not touch another ASCII letter, digit or underscore, so spaces, `-`,
  other punctuation and CJK characters all count as boundaries; a CJK edge
  needs no boundary at all. An alias therefore fires next to Chinese text, and
  also inside a longer token such as `GPT-5`, a documented limit.
- **Live Earlet preview** reads the same saved words at every new stream,
  before any requested phrases, using the final decode's case-insensitive
  deduplication. Pending pairs and alias-only entries are not prompt words.
  Removing a word takes effect on the next stream without restarting the worker.
  A preview already in progress keeps the vocabulary it started with.
  Its explicit prompt reaches MLX unchanged; alias replacement runs only at
  the preview commit gate, so overlapping aliases cannot expand twice.
- **Scene snapshot.** Both native hosts capture the recognition scene when a
  hold is admitted, and retain it across worker replacement. macOS uses cheap
  frontmost-app metadata at press, without adding an accessibility field read
  to microphone startup. Preview start and final transcription send the same
  optional `vocabulary_scene` tag on both platforms (Windows starts a preview
  only with `DICTATION_PREVIEW=1`).
  Switching apps during a hold does not change that tag; correction learning
  instead uses the verified application that actually received the text. The
  worker re-ranks its cached document on each request, so switching apps does
  not require a file rewrite or worker restart.
- Logs contain vocabulary counts, selected search width, completed search
  windows, matches, fallback class and elapsed time; never the vocabulary text.

## 7. Observers and privacy

The observer starts only after a paste the app confirmed, only on the field it
pasted into (same application, not a secure/password field), and only for the
span it pasted. It ends at the first of: 90 s, the next dictation, the field
losing focus or being destroyed, pause, quit. Pause and quit stop further
learning; earlier stable contributions remain reversible through the vocabulary
controls. The current span stays in memory only; the policy reduces each stable
revision to the corrected spans of §3.2. Logs record how an observation ended
and counts, never words.

- **macOS:** Accessibility (`DictationCorrectionObserver`), which the app already
  needs to paste.
- **Windows:** UI Automation (`SpanObserver` with `UiaSpanObserverTarget`). An
  edit counts only when keyboard input reached that field while it was observed.
  Terminals and consoles are refused outright, by a fixed list of process names
  and window classes (`SpanObserver.DeniedProcesses`, `DeniedWindowClasses`).
  So is Earlet's own process (`SpanObserver.ReasonSelf`), so a fix typed
  into the window's "Try dictation" practice field is never learned.

Both hosts publish a revision once the user has stopped typing for a few
seconds (the edit has *settled*: 3 s on Windows, 4 s on macOS), then keep
watching within the original 90-second deadline. Each revision uses the same
insertion ID: editing one letter later replaces the first contribution;
reverting the draft retracts it. Unchanged polls add no evidence. No Send
action is required. Sending or clearing the field before settlement may lose
the unfinished edit. A fix still being typed when the next dictation is
released is not committed (a press that ends as a shortcut, a refusal or a
cancel leaves the watch running).

On macOS an admitted field is snapshotted immediately before insertion. Its
before/after UTF-16 values must prove exactly one insertion of the pasted text,
or the exact replacement of the previous nonempty selection. This handles a
missing or unreliable caret without treating an old occurrence as the new
paste. The snapshot must belong to the same AX element, process and
application; it stays in memory for that attempt, never in logs, storage or
worker requests. If no readable snapshot is available, the original exact
caret-derived anchor remains available. An ambiguous change, a rewrite on
paste, or an edit outside the span during the watch yields no correction.
Switching learning off, pausing or quitting while a paste is being prepared or
inserted invalidates that attempt: it is never watched, even if learning is
switched on again before it ends.

**Witty running Codex:** current and legacy public release identities select
the same narrow adapter. Its AXValue is the whole terminal screen and it does
not emit text-change notifications. A dedicated adapter isolates the final
`› ` composer above a recognized Codex footer, strips the empty placeholder
and canonicalizes whitespace before the same unique-insertion proof. Both the
busy queue-hint footer and the idle draft's context-only footer (a 0–100
percent indicator with an optional warning count) are recognized. It polls only
that focused, admitted field every 600 ms, within the existing 90 s window.
Changing output above the composer is never an edit to the input. Submission,
unknown layouts, collapsed pastes/images, clipped drafts, and word-boundary
changes across display rows stop observation; no whole-screen fallback is
permitted. A correction at a wrap boundary may therefore be missed. Other
terminal programs and hosts have no adapter. These are local parsing rules,
not a dependency on Witty or Codex code or data.

Other terminals usually expose no editable text span, so corrections made there
are not seen. Real-device coverage depends on the focused application's
accessibility support and should be checked during attended acceptance.

What the Vocabulary section shows while a correction is checked, waiting,
rejected or saved is specified in
[docs/design-macos.md → Correction notices, save receipts and Save again](docs/design-macos.md#correction-notices-save-receipts-and-save-again)
(both apps; Windows maps its observer's endings in
[Windows: window and tray](docs/design-windows.md#windows-window-and-tray)) and
[Learning feedback, receipts and Undo](docs/design-macos.md#learning-feedback-receipts-and-undo)
(both apps; Windows has the receipt and Undo in its window and its
notification-area menu, and its indicator says the correction was remembered).
Anchor diagnostics record only a reason, attempt count, lengths and selection
offsets, never field or dictated text.

## 8. Evidence and limits

- A rewrite of an ordinary word needs repeated evidence, so repetition alone is
  not enough. The two-dictation count and the requirement that the heard form
  be non-ordinary are the executable safety contract (§4.2). Accuracy and
  coverage of this learning policy have not been measured on a representative
  user corpus; source-preview fixtures verify the boundary cases only.
- Prompt vocabulary on `mlx` is bounded by the 223-token prompt and 32-term
  trie budgets. The included ranking cases are small synthetic logic fixtures,
  not recognition or WER evidence. Learned words are capped at 100 (§4.5), and
  the prompt keeps terms ordered by strength and recency, or by scene relevance
  when there is scene evidence (§6, §4.7).
- Aliases cost only the frequency of their literal spoken form in ordinary
  speech, which is why an ordinary word may never be a learned spoken form.
