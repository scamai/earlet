"""Per-meeting-type note templates — the Granola/BlueDot "templates / recipes"
feature. A template steers HOW the notes are written for a given kind of
meeting (a customer discovery call wants pain points, budget and next steps; a
1:1 wants feedback, blockers and growth; an interview wants per-competency
signals and a recommendation) without changing the underlying note schema.

Design: a template carries a plain-text `guidance` string. `notes_gen`
injects it as a **system message** in front of every generation pass, so it
steers the overview, the structured extraction AND the topic narratives at
once, and — crucially — does so WITHOUT polluting the user prompt or
weakening the "return strict JSON" instruction each structured pass ends with
(system vs user is the right seam for standing instructions). The neutral
`default` template contributes no system message, so it reproduces today's
behavior exactly.

Custom templates match BlueDot's "write your own template as a plain-text
prompt": a JSON file of `{id: {name, description, guidance}}` the user owns,
loaded beside the other user config. A malformed/missing file yields no custom
templates rather than an error — same fail-soft contract as the vocab/alias
loaders.
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Template:
    id: str
    name: str
    description: str
    guidance: str  # "" = neutral (no system steer, i.e. today's default)

    def system_message(self) -> str | None:
        """The system-role steer injected before each generation pass, or None
        for a neutral template."""
        g = self.guidance.strip()
        if not g:
            return None
        return (f"You are writing the notes for a {self.name.lower()}. {g} "
                "Still follow the user's formatting instructions in each request exactly "
                "(including any 'return strict JSON' instruction).")


# Built-in templates. `guidance` is deliberately about EMPHASIS and WHAT TO
# SURFACE, never about output format (format is owned by notes_gen's prompts),
# so a template can't accidentally break the JSON contract.
BUILTIN: dict[str, Template] = {
    t.id: t for t in [
        Template("default", "Meeting", "General-purpose meeting notes.", ""),
        Template(
            "customer_discovery", "Customer discovery call",
            "Discovery / sales-qualification call with a prospect.",
            "Emphasize the customer's pain points and the cost of the status quo, their current "
            "tools and workflow, budget and timeline signals, who owns the decision and how it "
            "gets made, objections raised, and the concrete next steps and owners. Capture exact "
            "figures, product names and dates the customer gave."),
        Template(
            "sales_meddic", "MEDDIC sales call",
            "Sales call tracked against the MEDDIC framework.",
            "Organize what was said around MEDDIC: Metrics (quantified impact), Economic buyer, "
            "Decision criteria, Decision process, Identified pain, and Champion. Note explicitly "
            "which of these are still unknown after the call."),
        Template(
            "one_on_one", "1:1",
            "One-on-one between a manager and a report.",
            "Emphasize feedback given in both directions, blockers and how they'll be unblocked, "
            "career/growth topics, and clear action items with an owner for each side. Keep "
            "personal/sensitive detail factual and brief."),
        Template(
            "standup", "Standup",
            "Daily standup / status sync.",
            "For each person, capture what they completed, what they're doing next, and any "
            "blockers. Keep it terse and per-person; surface cross-cutting blockers and "
            "dependencies as decisions or action items."),
        Template(
            "interview", "Interview",
            "Candidate interview.",
            "Emphasize signals for and against the candidate organized by competency, concrete "
            "examples they gave, concerns to follow up, and a clear hire / no-hire leaning with "
            "the reasons. Attribute claims to what the candidate actually said."),
        Template(
            "project_sync", "Project sync",
            "Cross-functional project status meeting.",
            "Emphasize status against the plan, risks and mitigations, decisions made, and "
            "action items with owners and due dates. Flag anything that slipped or is blocked."),
    ]
}


def builtin_ids() -> list[str]:
    return list(BUILTIN.keys())


def default_custom_path() -> Path:
    """Under the user-owned Dictation folder in the home directory."""
    return Path.home() / "Dictation" / "notes" / "templates.json"


def load_custom(path: Path | None = None) -> dict[str, Template]:
    """User's own templates as {id: Template}, or empty. Fail-soft: a missing
    or malformed file yields no custom templates rather than an error."""
    path = path or default_custom_path()
    try:
        raw = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    if not isinstance(raw, dict):
        return {}
    out: dict[str, Template] = {}
    for tid, spec in raw.items():
        if not isinstance(spec, dict):
            continue
        tid = str(tid).strip()
        if not tid:
            continue
        out[tid] = Template(
            id=tid,
            name=str(spec.get("name") or tid).strip(),
            description=str(spec.get("description") or "").strip(),
            guidance=str(spec.get("guidance") or "").strip(),
        )
    return out


def get(template_id: str | None, custom_path: Path | None = None) -> Template:
    """Resolve a template id to a Template. Custom templates shadow built-ins
    of the same id. An unknown id (or None) falls back to `default` — an
    unrecognized template must never break note generation, only forgo the
    steer."""
    if not template_id:
        return BUILTIN["default"]
    custom = load_custom(custom_path)
    return custom.get(template_id) or BUILTIN.get(template_id) or BUILTIN["default"]
