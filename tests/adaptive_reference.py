"""Bounded, reversible correction evidence. Executable contract for both hosts.

Only admitted correction spans are retained, never a dictated sentence. The
caller owns extraction, file IO, tombstones and the existing vocabulary caps.
"""
from __future__ import annotations

import copy

MAX_OBSERVATIONS = 512
MAX_CORRECTIONS = 32
MAX_LEGACY_WEIGHT = 4


def contribution(candidate, policy, lexicon):
    return {
        "heard": policy.heard_key(candidate.heard), "meant": candidate.meant,
        "word": policy.learns_word(candidate, lexicon),
        "alias": policy.learns_alias(candidate, lexicon),
        "secret": policy.is_secret_shaped(candidate.meant),
    }


def seed(state, policy, lexicon):
    """Conservatively retain old counts as bounded prior evidence, once."""
    records = []
    for kind, flag, key in (("words", "word", "text"), ("aliases", "alias", "meant")):
        for row in state[kind]:
            if row["origin"] != "correction":
                continue
            identity = row["text"].lower() if flag == "word" else row["heard"]
            records.append({
                "id": "legacy:" + flag + ":" + identity,
                "at": row["last_seen"], "first_seen": row["first_seen"],
                "weight": max(1, min(MAX_LEGACY_WEIGHT, row["count"])),
                "corrections": [{"heard": row.get("heard", ""), "meant": row[key],
                                 "word": flag == "word", "alias": flag == "alias",
                                 "secret": flag != "word" and policy.is_secret_shaped(row[key])}],
            })
    records = list({r["id"]: r for r in records}.values())
    for row in state["pending"]:
        candidate = policy.Candidate(row["heard"], row["meant"], "spelling")
        c = contribution(candidate, policy, lexicon)
        c["word"] = c["word"] and not any(w["text"].lower() == row["meant"].lower() for w in state["words"])
        for identity in row["dictations"]:
            record = next((r for r in records if r["id"] == identity), None)
            if record is None:
                record = {"id": identity, "at": row["last_seen"], "weight": 1, "corrections": []}
                records.append(record)
            if c not in record["corrections"]:
                record["corrections"].append(c)
    return {"version": 1, "observations": records}


def valid_journal(journal):
    def integer(value):
        return type(value) is int and -(2**63) <= value < 2**63

    if not isinstance(journal, dict) or type(journal.get("version")) is not int or journal["version"] != 1:
        return False
    records = journal.get("observations")
    if not isinstance(records, list) or len(records) > 4096:
        return False
    ids = set()
    for r in records:
        if not isinstance(r, dict) or not isinstance(r.get("id"), str) or not 0 < len(r["id"]) <= 320:
            return False
        if r["id"] in ids or not integer(r.get("at")) or not integer(r.get("weight")) or not 1 <= r["weight"] <= 4:
            return False
        if r.get("first_seen") is not None and not integer(r["first_seen"]):
            return False
        ids.add(r["id"])
        corrections = r.get("corrections")
        if not isinstance(corrections, list) or len(corrections) > MAX_CORRECTIONS:
            return False
        for c in corrections:
            if not isinstance(c, dict) or not isinstance(c.get("heard"), str) or len(c["heard"]) > 256:
                return False
            if not isinstance(c.get("meant"), str) or not 0 < len(c["meant"]) <= 256:
                return False
            if any(type(c.get(k)) is not bool for k in ("word", "alias", "secret")):
                return False
    return True


def revise(state, identity, candidates, at, policy, lexicon, scene=None):
    """Replace this insertion's evidence, then rebuild only automatic entries.

    Unknown journal versions fail closed. Empty/reverted edits retract the
    previous contribution; identical observations do not refresh their clock.
    """
    previous_words = {w["text"].lower() for w in state["words"]}
    previous_aliases = {(a["heard"], a["meant"]) for a in state["aliases"]}
    if not identity or len(identity) > 128 or identity.startswith("legacy:") or not lexicon.available:
        return {"words": [], "aliases": []}
    # A correction whose meant text has no letter (a PIN) is never journaled (LEARNING.md §4.6).
    candidates = [c for c in candidates if policy.has_letter(c.meant)]
    journal = state.get("evidence")
    if "evidence" not in state:
        if not candidates:
            return {"words": [], "aliases": []}
        journal = seed(state, policy, lexicon)
    if not valid_journal(journal):
        return {"words": [], "aliases": []}
    records = journal["observations"]
    # Unconfirmed pairs expire; a previously promoted alias is durable until
    # contradicted/removed. Do this before adding the new observation, so an
    # expired first sighting cannot supply the second confirmation months later.
    active = {(a["heard"], a["meant"].lower()) for a in state["aliases"]}
    last_pair = {}
    for r in records:
        for c in r["corrections"]:
            if c["alias"] or (c["word"] and c["secret"]):
                key = c["heard"], c["meant"].lower()
                last_pair[key] = max(last_pair.get(key, r["at"]), r["at"])
    expired = {k for k, seen in last_pair.items() if seen < at - policy.PENDING_TTL_MS and k not in active}
    for r in records:
        for c in r["corrections"]:
            if (c["heard"], c["meant"].lower()) in expired:
                c["alias"] = False
                if c["secret"]:
                    c["word"] = False
    corrections = [contribution(c, policy, lexicon) for c in candidates[:MAX_CORRECTIONS]
                   if len(c.heard) <= 256 and len(c.meant) <= 256]
    old = next((r for r in records if r["id"] == identity), None)
    fields = ("heard", "meant", "word", "alias", "secret")
    previous = [{k: c[k] for k in fields} for c in old["corrections"]] if old else None
    if old is None or previous != corrections:
        records[:] = [r for r in records if r["id"] != identity]
        if corrections:
            new = copy.deepcopy(old) if old else {}
            new.update(id=identity, at=at, weight=1, corrections=corrections)
            if old is None and scene in ("terminal", "wechat", "other"):
                new["scene"] = scene
            records.append(new)
    records.sort(key=lambda r: (r["at"], r["id"]))
    del records[:-MAX_OBSERVATIONS]
    state["evidence"] = journal
    rebuild(state, records, at, policy)
    return {
        "words": [w["text"] for w in state["words"] if w["text"].lower() not in previous_words],
        "aliases": [a["heard"] for a in state["aliases"] if (a["heard"], a["meant"]) not in previous_aliases],
    }


def rebuild(state, records, at, policy):
    old_words = {w["text"].lower(): w for w in state["words"]}
    old_aliases = {a["heard"]: a for a in state["aliases"]}
    protected_words = {k: w for k, w in old_words.items() if w["origin"] != "correction"}
    protected_aliases = {k: a for k, a in old_aliases.items() if a["origin"] != "correction"}
    removed_words = {w["key"] for w in state["removed"]["words"]}
    removed_aliases = {(a["heard"], a["meant"]) for a in state["removed"]["aliases"]}
    words, pairs, replacements = {}, {}, {}
    for record in records:
        for c in record["corrections"]:
            key, heard = c["meant"].lower(), c["heard"]
            # A valid spelling correction away from a previously learned word
            # is negative evidence, even when its target is an ordinary word.
            if heard and heard != key:
                replacements.setdefault(heard, {})[record["id"]] = record
            if key in removed_words:
                continue
            if c["word"] and key not in protected_words and (not c["secret"] or heard not in protected_aliases):
                words.setdefault(key, {})[record["id"]] = (record, c)
            waits = c["word"] and c["secret"] and key not in protected_words
            if (c["alias"] or waits) and heard not in protected_aliases and heard not in protected_words:
                if (heard, key) not in removed_aliases:
                    pairs.setdefault((heard, key), {})[record["id"]] = (record, c)

    def totals(entries, negative):
        support = sum(r["weight"] for r, _ in entries.values())
        opposition = sum(r["weight"] for i, r in negative.items() if i not in entries)
        return support, opposition

    def entry(entries, old=None):
        newest, c = max(entries.values(), key=lambda rc: (rc[0]["at"], rc[0]["id"]))
        row = copy.deepcopy(old) if old else {}
        row.update(origin="correction", first_seen=min(r.get("first_seen", r["at"]) for r, _ in entries.values()),
                   last_seen=newest["at"], adaptive=True)
        return row, c

    next_words = list(protected_words.values())
    for key, entries in words.items():
        support, opposition = totals(entries, replacements.get(key, {}))
        _, latest = max(entries.values(), key=lambda rc: (rc[0]["at"], rc[0]["id"]))
        threshold = 2 if latest["secret"] else 1
        if support - opposition < threshold:
            continue
        row, c = entry(entries, old_words.get(key))
        row.update(text=c["meant"], count=support, opposition=opposition)
        next_words.append(row)
    next_aliases, pending = list(protected_aliases.values()), []
    for (heard, key), entries in pairs.items():
        negative = dict(replacements.get(key, {}))
        for (other_heard, other_key), rival in pairs.items():
            if other_heard == heard and other_key != key:
                negative.update({i: r for i, (r, _) in rival.items()})
        support, opposition = totals(entries, negative)
        row, c = entry(entries, old_aliases.get(heard))
        if c["alias"] and support - opposition >= policy.ALIAS_PROMOTION_COUNT:
            row.update(heard=heard, meant=c["meant"], count=support, opposition=opposition)
            next_aliases.append(row)
        elif row["last_seen"] >= at - policy.PENDING_TTL_MS and (not c["secret"] or support - opposition < 2):
            pending.append({"heard": heard, "meant": c["meant"], "count": support,
                            "dictations": sorted(entries, key=lambda i: (entries[i][0]["at"], i))[-policy.PENDING_DICTATION_IDS:],
                            "first_seen": row["first_seen"], "last_seen": row["last_seen"]})
    state.update(words=next_words, aliases=next_aliases, pending=pending)
