#!/usr/bin/env python3
"""Generates the synthetic evaluation dataset in evals/dataset/ (and a few test fixtures).

Everything here is made up: Thames Mutual Insurance is a fictional insurer, the insured businesses are the ones seeded in
src/ClaimsIntake.Legacy/Policies.cs, and the claims are invented. Nothing real is used.

How the ground truth is made
----------------------------
Each case is first a *specification*: which policy, what happened, when, what amount, how the document is written. The
document is rendered from the specification, and `<id>.expected.json` is computed from the same specification by the
tables below (which peril clause covers which cause under which wording, which exclusion clause applies, how many days
each wording allows for notification). The pipeline is never run to make the ground truth, so a wrong answer by the
pipeline shows up as a wrong answer and not as an agreement with itself.

It is still ONE author's idea of messy: the same person wrote the extractor, the wordings and this set. See
evals/README.md ("Limitations") before reading the numbers.

Determinism
-----------
The generator uses a fixed seed and a separate random stream per case, so adding or removing a case does not change any
other case. PDFs are written with reportlab's invariant mode. Running it twice produces identical files.

Usage
-----
    pip install reportlab        # BSD licence
    python evals/generate_dataset.py
"""

from __future__ import annotations

import json
import random
import textwrap
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

from reportlab.lib import pdfencrypt
from reportlab.lib.pagesizes import A4
from reportlab.pdfgen import canvas

ROOT = Path(__file__).resolve().parent
OUT = ROOT / "dataset"
FIXTURES = ROOT.parent / "tests" / "ClaimsIntake.Pipeline.Tests" / "fixtures"
SEED = 20261114
PAGE_WIDTH, PAGE_HEIGHT = A4

# --------------------------------------------------------------------------------------------------------------------
# Ground-truth tables, written from the policy wordings in samples/policy-wordings and the seed policies.
# --------------------------------------------------------------------------------------------------------------------

POLICIES = {
    # number: (insured, wording, period start, period end)
    "TM-CP-100001": ("Brindle & Marsh Joinery Ltd", "CC", date(2026, 1, 1), date(2026, 12, 31)),
    "TM-CP-100002": ("Kestrel Print & Packaging Ltd", "CC", date(2026, 3, 1), date(2027, 2, 28)),
    "TM-CP-100003": ("Harcourt Dental Practice", "CC", date(2026, 6, 15), date(2027, 6, 14)),
    "TM-CP-100004": ("Lowther Fabrication Works Ltd", "EN", date(2025, 11, 1), date(2026, 10, 31)),
    "TM-CP-100005": ("Pemberton Road Haulage Ltd", "CC", date(2026, 4, 1), date(2027, 3, 31)),
    "TM-CP-100006": ("Wren & Co Estate Agents", "OF", date(2026, 2, 1), date(2027, 1, 31)),
    "TM-CP-100007": ("Tideway Catering Supplies Ltd", "CCF", date(2026, 9, 1), date(2027, 8, 31)),
    "TM-CP-100008": ("Ashdown Veterinary Group", "CC", date(2026, 1, 15), date(2027, 1, 14)),
    "TM-CP-100009": ("Greyfriars Bookbinders", "OF", date(2025, 12, 1), date(2026, 11, 30)),
    "TM-CP-100010": ("Millbrook Garden Centres Ltd", "CCF", date(2026, 5, 1), date(2027, 4, 30)),
}

# Insured peril clause for each cause, per wording. A cause that a wording does not insure is absent.
PERIL = {
    "CC": {"fire": "3.1", "water": "3.2", "theft": "3.3", "storm": "3.4", "impact": "3.5"},
    "CCF": {"fire": "3.1", "water": "3.2", "theft": "3.3", "storm": "3.4", "flood": "3.5", "refrigeration": "3.6"},
    "OF": {"fire": "3.1", "water": "3.2", "theft": "3.3", "storm": "3.4"},
    "EN": {"breakdown": "3.1", "fire": "3.2", "water": "3.3", "theft": "3.4"},
}
EXCL_FLOOD = {"CC": "4.1", "OF": "4.1", "EN": "4.2"}  # CCF covers flood instead
EXCL_WEAR = {"CC": "4.2", "CCF": "4.1", "OF": "4.2", "EN": "4.1"}
EXCL_UNOCC = {"CC": "4.3", "CCF": "4.2", "OF": "4.3", "EN": "4.3"}
NOTIFY_DAYS = {"CC": 30, "CCF": 30, "OF": 14, "EN": 21}
NOTIFY_CLAUSE = "5.1"
UNOCC_LIMIT = 30

# --------------------------------------------------------------------------------------------------------------------
# Text pools.
# --------------------------------------------------------------------------------------------------------------------

AREAS = {
    "CC": ["workshop store room", "warehouse loading bay", "stockroom", "treatment room", "packing hall"],
    "CCF": ["cold store", "kitchen prep area", "garden centre warehouse", "goods-in bay", "yard"],
    "OF": ["back office", "ground-floor suite", "print room", "reception area", "archive room"],
    "EN": ["press shop", "fabrication bay", "plant room", "machine shop", "paint line"],
}
DAMAGED = ["Two desks and a lot of paperwork", "Timber stock and two bench saws", "A quantity of finished stock", "The flooring and fittings",
           "Several pallets of packaging", "Computer equipment and a printer", "Shelving and boxed goods", "Two display units and the carpet"]
STOLEN = ["Two pallets of coated board", "A laptop, a monitor and a till", "Power tools and a forklift battery", "Copper cable and a generator",
          "Four computers and a projector"]
PEOPLE = ["Sam Brindle", "Priya Nair", "Dan Whitlock", "Hannah Okafor", "Marcus Hale", "Joanna Pryce", "Tariq Aziz", "Ellen Marsh"]
TITLES = ["Director", "Office manager", "Operations manager", "Practice manager", "Finance director"]
BROKERS = ["Hale Brokers", "Marlowe & Finch Insurance Brokers", "Stanton Risk Partners"]

CAUSE_TEXT = {
    "fire": [
        "A fire started in the {area} overnight. The fire brigade attended and put it out. {dmg} were ruined by fire and smoke.",
        "There was a fire in the {area} when the building was empty. The alarm company called the fire brigade. {dmg} were destroyed.",
        "An electrical fire broke out behind the {area}. Smoke damage spread through the unit and {dmg2} were lost.",
    ],
    "water": [
        "A water pipe burst in the {area} overnight. {dmg} were damaged by the water.",
        "A leaking pipe above the {area} gave way during the night and soaked the floor. {dmg} were damaged.",
        "The water tank in the roof space overflowed into the {area}. {dmg} were badly damaged.",
    ],
    "theft": [
        "Thieves forced entry through the rear door of the {area}. {stolen} were stolen.",
        "We were burgled overnight. Forced entry through a window by the {area}; {stolen} were taken.",
        "A break-in over the weekend: the lock on the {area} door was forced and {stolen} were stolen.",
    ],
    "storm": [
        "Storm damage. High winds tore tiles from the roof above the {area} and rain came in. {dmg} were damaged.",
        "During the storm a gale lifted part of the roof over the {area} and {dmg2} were soaked.",
    ],
    "impact": [
        "A delivery lorry collided with the {area} door and frame while reversing. The door and the frame were wrecked.",
        "A vehicle crashed into the side of the {area}, damaging the wall and the racking inside.",
    ],
    "refrigeration": [
        "The walk-in freezer compressor failed overnight and the stock in the {area} spoiled.",
        "Our cold room lost power to the refrigeration unit at the weekend; all the chilled stock was lost.",
    ],
    "breakdown": [
        "The hydraulic press seized during a shift and the ram was damaged beyond economic repair.",
        "A mechanical failure in the main drive of the CNC machine in the {area} wrecked the spindle and the control board.",
    ],
    "flood": [
        "Heavy rain overnight overwhelmed the drains and surface water flooded the {area}. {dmg} were damaged.",
        "The river burst its banks and flooded the {area} to a depth of about a foot. {dmg2} were ruined.",
    ],
}

# --------------------------------------------------------------------------------------------------------------------
# Small helpers.
# --------------------------------------------------------------------------------------------------------------------

MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"]
ONES = ["", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen",
        "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"]
TENS = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"]


def words_under_1000(n: int) -> str:
    parts = []
    if n >= 100:
        parts.append(f"{ONES[n // 100]} hundred")
        n %= 100
        if n:
            parts.append("and")
    if n >= 20:
        parts.append(TENS[n // 10] + (f"-{ONES[n % 10]}" if n % 10 else ""))
    elif n:
        parts.append(ONES[n])
    return " ".join(parts)


def amount_in_words(amount: int) -> str:
    thousands, rest = divmod(amount, 1000)
    parts = []
    if thousands:
        parts.append(f"{words_under_1000(thousands)} thousand")
    if rest:
        parts.append(("and " if thousands and rest < 100 else "") + words_under_1000(rest))
    return " ".join(parts) + " pounds"


def ordinal(n: int) -> str:
    suffix = "th" if 10 <= n % 100 <= 20 else {1: "st", 2: "nd", 3: "rd"}.get(n % 10, "th")
    return f"{n}{suffix}"


def fmt_date(d: date, style: str) -> str:
    return {
        "iso": d.isoformat(),
        "long": f"{d.day} {MONTHS[d.month - 1]} {d.year}",
        "ordinal_short": f"{ordinal(d.day)} {MONTHS[d.month - 1][:3]} {d.year}",
        "ordinal_long": f"{ordinal(d.day)} {MONTHS[d.month - 1]} {d.year}",
        "slash4": f"{d.day:02d}/{d.month:02d}/{d.year}",
        "slash2": f"{d.day:02d}/{d.month:02d}/{d.year % 100:02d}",
        "weekday": f"{d.strftime('%A')} {d.day} {MONTHS[d.month - 1]} {d.year}",
    }[style]


def fmt_amount(value: int, style: str) -> str:
    return {
        "pound": f"£{value:,}",
        "pence": f"£{value:,}.00",
        "plain": f"£{value}",
        "words": amount_in_words(value),
        "gbp": f"GBP {value:,}",
        "k": f"£{value / 1000:g}k",
    }[style]


def ocr_noise(rng: random.Random, text: str, rate: float = 0.04) -> str:
    """Substitutes look-alike characters, as a poor scan does. Policy numbers and dates are never passed through here."""
    table = {"l": "1", "O": "0", "o": "0", "e": "c", "m": "rn", "i": "!", "S": "5"}
    return "".join(table[c] if c in table and rng.random() < rate else c for c in text)


def received_at(d: date, rng: random.Random) -> str:
    """A time of day that is the same calendar day in London and UTC (08:00 to 16:59), as an ISO 8601 UTC string."""
    t = datetime(d.year, d.month, d.day, rng.randint(8, 16), rng.randint(0, 59), tzinfo=timezone.utc)
    return t.isoformat().replace("+00:00", "Z")


# --------------------------------------------------------------------------------------------------------------------
# The specification of a case.
# --------------------------------------------------------------------------------------------------------------------

@dataclass
class Spec:
    id: str
    family: str
    split: str
    doc: str = "form"  # form | scattered | email | eml | blank_pdf | corrupt_pdf | protected_pdf | text
    tags: list[str] = field(default_factory=list)
    policy: str | None = "TM-CP-100001"  # the policy the claim is really on
    policy_written: str | None = "same"  # what the document says: "same", None (missing), or a literal (a typo)
    claimant_style: str = "canonical"  # canonical | and_limited | upper
    cause: str | None = "water"
    description: str | None = None  # overrides the generated description
    loss: date | None = None
    loss_style: str = "long"
    amount: int | None = 12500
    amount_style: str = "pound"
    notified: date | None = None
    notified_stated: bool = True  # form documents only; emails always carry their sent date
    received: date | None = None  # defaults to the notified date
    unoccupied_days: int | None = None
    wear: bool = False
    noise: bool = False
    extra_dates: bool = False
    existing: list[dict] = field(default_factory=list)
    duplicate_of: str | None = None
    max_bytes: int | None = None
    quality: str | None = None  # the issue code the input-quality gate should raise, for documents that are not claims
    body: str | None = None  # literal text for non-claim documents


def wording(spec: Spec) -> str | None:
    return POLICIES[spec.policy][1] if spec.policy else None


def claimant_name(spec: Spec) -> str | None:
    if not spec.policy:
        return None
    name = POLICIES[spec.policy][0]
    return name


def written_claimant(spec: Spec) -> str | None:
    name = claimant_name(spec)
    if name is None:
        return None
    if spec.claimant_style == "and_limited":
        return name.replace(" & ", " and ").replace(" Ltd", " Limited")
    if spec.claimant_style == "upper":
        return name.upper()
    return name


def policy_text(spec: Spec) -> str | None:
    if spec.policy_written is None:
        return None
    return spec.policy if spec.policy_written == "same" else spec.policy_written


# --------------------------------------------------------------------------------------------------------------------
# Expected output, computed from the specification.
# --------------------------------------------------------------------------------------------------------------------

def known_policy(spec: Spec) -> bool:
    """The document names a policy that exists, spelled correctly enough to be found."""
    return spec.policy is not None and spec.policy_written == "same"


def expected(spec: Spec) -> dict:
    if spec.quality:
        return {
            "fields": {k: None for k in ("policy_number", "claimant_name", "loss_date", "cause", "estimated_amount_gbp", "notification_date")},
            "issueCodes": [spec.quality],
            "findings": [],
            "route": "SeniorReview",
        }

    w = wording(spec)
    issues: list[str] = []
    findings: list[dict] = []

    written = policy_text(spec)
    if written is None:
        issues.append("MissingPolicyNumber")
    elif not known_policy(spec):
        issues.append("UnknownPolicy")
    if spec.loss is None:
        issues.append("MissingLossDate")
    elif known_policy(spec):
        _, _, start, end = POLICIES[spec.policy]
        if not (start <= spec.loss <= end):
            issues.append("LossDateOutsidePolicyPeriod")
    if spec.loss is not None and spec.notified is not None and spec.loss > spec.notified:
        issues.append("LossDateInFuture")

    # Findings follow the wording of the policy that is really named in the document. When the policy cannot be identified
    # nothing is assessed (the data has to be fixed first), exactly as the design in ADR 0006 says.
    if known_policy(spec) and w:
        cause = spec.cause
        if cause and cause in PERIL[w]:
            findings.append({"kind": "CoverageClause", "clauseRef": PERIL[w][cause]})
        if cause == "flood" and w in EXCL_FLOOD:
            findings.append({"kind": "PossibleExclusion", "clauseRef": EXCL_FLOOD[w]})
        if spec.wear:
            findings.append({"kind": "PossibleExclusion", "clauseRef": EXCL_WEAR[w]})
        if spec.unoccupied_days and spec.unoccupied_days > UNOCC_LIMIT and cause in ("water", "theft"):
            findings.append({"kind": "PossibleExclusion", "clauseRef": EXCL_UNOCC[w]})
        if spec.loss and spec.notified and (spec.notified - spec.loss).days > NOTIFY_DAYS[w]:
            findings.append({"kind": "NotificationBreach", "clauseRef": NOTIFY_CLAUSE})
        for e in spec.existing:
            same_date = e["lossDate"] == spec.loss.isoformat() if spec.loss else False
            close = spec.amount is not None and abs(e["amountGbp"] - spec.amount) <= 0.10 * max(e["amountGbp"], spec.amount)
            if same_date and close:
                findings.append({"kind": "PossibleDuplicate", "clauseRef": ""})

    senior = any(f["kind"] in ("PossibleExclusion", "NotificationBreach", "PossibleDuplicate") for f in findings)
    if known_policy(spec) and not senior and not any(f["kind"] == "CoverageClause" for f in findings):
        senior = True  # nothing in the wording clearly covers it: a doubt about the assessment itself
    route = "SeniorReview" if senior else "StraightToHandler"

    return {
        "fields": {
            "policy_number": written,
            "claimant_name": claimant_name(spec),
            "loss_date": spec.loss.isoformat() if spec.loss else None,
            "cause": spec.cause,
            "estimated_amount_gbp": float(spec.amount) if spec.amount is not None else None,
            "notification_date": (spec.notified.isoformat() if spec.notified else None),
        },
        "issueCodes": sorted(issues),
        "findings": findings,
        "route": route,
    }


# --------------------------------------------------------------------------------------------------------------------
# Rendering.
# --------------------------------------------------------------------------------------------------------------------

def description_for(spec: Spec, rng: random.Random) -> str:
    if spec.description:
        return spec.description
    w = wording(spec) or "CC"
    template = rng.choice(CAUSE_TEXT[spec.cause])
    text = template.format(area=rng.choice(AREAS[w]), dmg=rng.choice(DAMAGED), dmg2=rng.choice(DAMAGED).lower(), stolen=rng.choice(STOLEN))
    if spec.unoccupied_days:
        text += f" The premises had been unoccupied for {spec.unoccupied_days} days while we refitted them."
    if spec.wear:
        text += " The pipe had corroded over time."
    return text


def form_pdf(path: Path, title: str, lines: list[str], footer: str = "", encrypt=None) -> None:
    pdf = canvas.Canvas(str(path), pagesize=A4, invariant=1, encrypt=encrypt)
    pdf.setTitle(title)
    pdf.setFont("Helvetica-Bold", 14)
    pdf.drawString(56, PAGE_HEIGHT - 64, title)
    pdf.setFont("Helvetica", 11)
    y = PAGE_HEIGHT - 100
    for line in lines:
        pdf.drawString(56, y, line)
        y -= 20 if line else 12
    if footer:
        pdf.setFont("Helvetica-Oblique", 9)
        pdf.drawString(56, 48, footer)
    pdf.save()


def wrap(label: str, text: str, width: int = 88) -> list[str]:
    return textwrap.wrap(f"{label}{text}", width=width, subsequent_indent="")


def render_form(spec: Spec, rng: random.Random, path: Path) -> None:
    labels = rng.choice([
        ("Policy number: ", "Insured: ", "Date of loss: ", "Description of loss: ", "Estimated amount: "),
        ("Policy no: ", "Policyholder: ", "Date of incident: ", "What happened: ", "Amount claimed: "),
        ("Policy ref: ", "Name of insured: ", "Loss date: ", "Description of loss: ", "Estimated cost: "),
    ])
    pol, ins, dol, desc, amt = labels
    lines: list[str] = []
    p = policy_text(spec)
    lines.append(f"{pol}{p}" if p else f"{pol}to follow, please look us up")
    lines.append(f"{ins}{written_claimant(spec) or 'Harcourt Dental Practice'}")
    if spec.loss:
        lines.append(f"{dol}{fmt_date(spec.loss, spec.loss_style)}")
    if spec.notified_stated and spec.notified:
        lines.append(f"Date notified: {fmt_date(spec.notified, 'long')}")
    if spec.extra_dates and spec.policy:
        _, _, start, _ = POLICIES[spec.policy]
        lines.append(f"Policy start date: {fmt_date(start, 'long')}")
        lines.append(f"Previous claim: {fmt_date(date(2025, 3, 17), 'long')} (settled)")
    lines.append("")
    text = description_for(spec, rng)
    if spec.noise:
        text = ocr_noise(rng, text)
    lines.extend(wrap(desc, text))
    lines.append("")
    if spec.amount is not None:
        lines.append(f"{amt}{fmt_amount(spec.amount, spec.amount_style)}")
        lines.append("")
    lines.append(f"Reported by: {rng.choice(PEOPLE)}, {rng.choice(TITLES)}")
    form_pdf(path, "Thames Mutual Insurance - Commercial claim notification", lines,
             footer="Synthetic evaluation document. Fictional insurer and customer.")


def render_scattered(spec: Spec, rng: random.Random, path: Path) -> None:
    """Fragments of the form at odd positions, a word split across two pieces, labels far from values, and OCR noise."""
    pdf = canvas.Canvas(str(path), pagesize=A4, invariant=1)
    pdf.setTitle("Claim notification")
    name = written_claimant(spec) or "Harcourt Dental Practice"
    cut = max(4, len(name) // 2)
    desc = description_for(spec, rng)
    d1 = desc[: len(desc) // 2]
    d2 = desc[len(desc) // 2:]
    pieces = [
        (60, 790, "CLAIM NOTIF", 14), (300, 760, f"ICATION  ref {rng.randint(10, 99)}-{rng.choice('ABCD')}", 14),
        (400, 700, "Loss", 10), (60, 690, name[:cut], 11), (230, 640, name[cut:], 11),
        (60, 600, "Pol", 10), (340, 596, policy_text(spec) or "", 11),
        (120, 540, fmt_date(spec.loss, spec.loss_style) if spec.loss else "", 11), (420, 520, "damaged", 11),
        (60, 470, ocr_noise(rng, d1, 0.05) if spec.noise else d1, 9), (60, 440, ocr_noise(rng, d2, 0.05) if spec.noise else d2, 9),
    ]
    if spec.amount is not None:
        amount = fmt_amount(spec.amount, spec.amount_style)
        pieces.append((450, 340, amount.replace("0", "O", 1) if spec.noise else amount, 11))
    pieces.append((60, 120, "Page 1 of 1", 8))
    for x, y, text, size in pieces:
        if text:
            pdf.setFont("Helvetica", size)
            pdf.drawString(x, y, text)
    pdf.save()


def email_headers(spec: Spec, rng: random.Random, sender: str, domain: str) -> list[str]:
    sent = spec.notified
    weekday = sent.strftime("%a")
    return [
        f"From: {sender} <{sender.split()[0].lower()}@{domain}>",
        "To: claims@thamesmutual.example",
        f"Date: {weekday}, {sent.day} {MONTHS[sent.month - 1][:3]} {sent.year} {rng.randint(7, 17):02d}:{rng.randint(0, 59):02d}:02 +0000",
        f"Subject: {rng.choice(['Claim notification', 'Please can you open a claim', 'Loss at our premises', 'Claim - ' + (written_claimant(spec) or 'our business')])}",
    ]


def render_email(spec: Spec, rng: random.Random, path: Path) -> None:
    sender = rng.choice(PEOPLE)
    domain = (written_claimant(spec) or "business").split()[0].lower().replace("&", "").strip() + ".example"
    org = written_claimant(spec) or "our business"
    desc = description_for(spec, rng)
    if spec.noise:
        desc = ocr_noise(rng, desc)
    lines = email_headers(spec, rng, sender, domain) + ["", "Hi,", ""]
    style = rng.choice(["chatty", "terse", "narrative"])
    p = policy_text(spec)
    pol_sentence = f"We have policy {p}." if p else "I do not have the policy number to hand."
    if style == "chatty":
        lines += [f"I am the office manager at {org}. Sorry to be sending this by email but I do not have the claim form. {pol_sentence}", ""]
        if spec.loss:
            lines += [f"It happened on {fmt_date(spec.loss, spec.loss_style)}. {desc}"]
        else:
            lines += [desc]
        if spec.extra_dates:
            lines += [f"(We also had a minor leak on {fmt_date(date(2026, 8, 4), 'long')} which was fixed by our plumber and is not part of this claim.)"]
        if spec.amount is not None:
            lines += [f"We think it is roughly {fmt_amount(spec.amount, spec.amount_style)} of damage."]
        lines += ["I know there is an excess of £250.", "", "Can someone call me?"]
    elif style == "terse":
        lines += [f"Claim for {org}. {pol_sentence}"]
        if spec.loss:
            lines += [f"Loss on {fmt_date(spec.loss, spec.loss_style)}."]
        lines += [desc]
        if spec.amount is not None:
            lines += [f"Estimated cost {fmt_amount(spec.amount, spec.amount_style)}."]
    else:
        lines += [f"On behalf of {org} I am writing to report a loss. {pol_sentence}", ""]
        if spec.loss and spec.extra_dates:
            lines += [f"The {spec.cause} problem was discovered on {fmt_date(spec.loss + timedelta(days=1), 'long')} but it happened on the night of {fmt_date(spec.loss, spec.loss_style)}."]
        elif spec.loss:
            lines += [f"It happened on the night of {fmt_date(spec.loss, spec.loss_style)}."]
        lines += [desc]
        if spec.amount is not None:
            lines += ["", f"Our estimate of the cost is {fmt_amount(spec.amount, spec.amount_style)}."]
    lines += ["", "Thanks,", sender.split()[0]]
    path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")


def render_eml(spec: Spec, rng: random.Random, path: Path) -> None:
    broker = rng.choice(BROKERS)
    sent = spec.notified
    org = written_claimant(spec) or "our client"
    p = policy_text(spec)
    desc = description_for(spec, rng)
    body = ["Hello claims team,", "", "Please register the following on behalf of our client.", ""]
    body.append(f"Policy number: {p}" if p else "Policy number: awaiting from client")
    body.append(f"Insured: {org}")
    if spec.loss:
        body.append(f"Date of loss: {fmt_date(spec.loss, spec.loss_style)}")
    body.append(f"Description of loss: {desc}")
    if spec.amount is not None:
        body.append(f"Estimated amount: {fmt_amount(spec.amount, spec.amount_style)}")
    body += ["", "Kind regards,", rng.choice(PEOPLE)]
    headers = [
        f"From: \"{rng.choice(PEOPLE)}\" <claims@{broker.split()[0].lower()}.example>", "To: claims@thamesmutual.example",
        f"Subject: New claim - {org}" + (f" ({p})" if p else ""),
        f"Date: {sent.strftime('%a')}, {sent.day} {MONTHS[sent.month - 1][:3]} {sent.year} {rng.randint(8, 16):02d}:{rng.randint(0, 59):02d}:11 +0000",
        f"Message-ID: <{rng.randint(10**9, 10**10)}@{broker.split()[0].lower()}.example>", "MIME-Version: 1.0",
        "Content-Type: text/plain; charset=utf-8",
    ]
    path.write_bytes(("\r\n".join(headers) + "\r\n\r\n" + "\r\n".join(body) + "\r\n").encode("utf-8"))


NON_CLAIMS = {
    "marketing": (
        "From: Offers <offers@coverdeals.example>\nTo: claims@thamesmutual.example\nDate: Fri, 6 Nov 2026 07:30:00 +0000\n"
        "Subject: 20% off business insurance this month only!\n\nLimited time offer for small firms. Get a free quote in two minutes and "
        "save on your renewal. Click here to book now, or view this email in your browser. You are receiving this because you signed up "
        "to our newsletter. To unsubscribe, reply STOP.\n"
    ),
    "french": (
        "From: Claire Dubois <claire@atelier-dubois.example>\nTo: claims@thamesmutual.example\nDate: Mon, 9 Nov 2026 09:12:00 +0000\n"
        "Subject: Sinistre\n\nBonjour, nous avons eu un sinistre dans notre entrepôt la semaine dernière et nous voudrions savoir comment "
        "procéder avec votre assurance. Les dégâts causés par l'eau sont importants et nous avons besoin de votre aide pour la suite. "
        "Merci de nous répondre dès que possible.\n"
    ),
    "german": (
        "From: Jonas Keller <jonas@keller-werkstatt.example>\nTo: claims@thamesmutual.example\nDate: Tue, 10 Nov 2026 14:05:00 +0000\n"
        "Subject: Schaden\n\nGuten Tag, in unserem Lager ist letzte Woche ein Wasserschaden entstanden und wir möchten wissen, wie wir bei "
        "Ihrer Versicherung vorgehen sollen. Bitte melden Sie sich bei uns zurück, wir brauchen Ihre Hilfe bei der Abwicklung.\n"
    ),
    "internal": (
        "From: Office <office@thamesmutual.example>\nTo: all@thamesmutual.example\nDate: Wed, 11 Nov 2026 10:00:00 +0000\n"
        "Subject: Team lunch\n\nHi all, the team lunch on Friday is moved to one o'clock because the meeting room is booked for training "
        "in the morning. Please let me know what you would like to eat and whether you have any dietary needs.\n"
    ),
}


def build_pdf_fixtures(directory: Path) -> None:
    """A scan-like PDF (pages and shapes but no text), a password-protected PDF and a truncated PDF."""
    scan = canvas.Canvas(str(directory / "scan.pdf"), pagesize=A4, invariant=1)
    scan.setTitle("scan")
    for i in range(14):
        scan.line(56, PAGE_HEIGHT - 120 - i * 28, PAGE_WIDTH - 56, PAGE_HEIGHT - 118 - i * 28)
    scan.rect(56, 120, 200, 60)
    scan.save()

    form_pdf(directory / "protected.pdf", "Thames Mutual Insurance - Commercial claim notification",
             ["Policy number: TM-CP-100001", "Insured: Brindle & Marsh Joinery Ltd", "Date of loss: 3 November 2026"],
             encrypt=pdfencrypt.StandardEncryption("userpw", ownerPassword="ownerpw", canPrint=1))

    good = directory / "good.pdf"
    form_pdf(good, "Thames Mutual Insurance - Commercial claim notification",
             ["Policy number: TM-CP-100001", "Insured: Brindle & Marsh Joinery Ltd", "Date of loss: 3 November 2026"] * 3)
    data = good.read_bytes()
    (directory / "corrupt.pdf").write_bytes(data[: len(data) // 3])
    good.unlink()


# --------------------------------------------------------------------------------------------------------------------
# The cases.
# --------------------------------------------------------------------------------------------------------------------

D = date

def cases() -> list[Spec]:
    c: list[Spec] = []

    def add(**kw) -> Spec:
        s = Spec(**kw)
        c.append(s)
        return s

    # clean_pdf: five tidy forms across wordings and causes. Notified a few days after the loss.
    add(id="c01-clean-water-pdf", family="clean_pdf", split="dev", tags=["clean"], policy="TM-CP-100001", cause="water", loss=D(2026, 11, 3), notified=D(2026, 11, 6), amount=12500)
    add(id="c02-clean-theft-pdf", family="clean_pdf", split="test", tags=["clean"], policy="TM-CP-100002", cause="theft", loss=D(2026, 10, 21), notified=D(2026, 10, 26), amount=8750, amount_style="pence", loss_style="iso")
    add(id="c03-clean-fire-pdf", family="clean_pdf", split="dev", tags=["clean"], policy="TM-CP-100006", cause="fire", loss=D(2026, 11, 9), notified=D(2026, 11, 11), amount=6000)
    add(id="c04-clean-breakdown-pdf", family="clean_pdf", split="test", tags=["clean"], policy="TM-CP-100004", cause="breakdown", loss=D(2026, 10, 12), notified=D(2026, 10, 15), amount=21000, claimant_style="and_limited")
    add(id="c05-clean-storm-pdf", family="clean_pdf", split="test", tags=["clean"], policy="TM-CP-100003", cause="storm", loss=D(2026, 10, 28), notified=D(2026, 11, 2), amount=4200, loss_style="ordinal_long")

    # email_txt: informal emails with the fields in prose.
    add(id="c06-email-fire", family="email_txt", split="dev", tags=["email"], doc="email", policy="TM-CP-100006", cause="fire", loss=D(2026, 11, 9), notified=D(2026, 11, 10), amount=6000, loss_style="ordinal_long")
    add(id="c07-email-theft", family="email_txt", split="dev", tags=["email"], doc="email", policy="TM-CP-100002", cause="theft", loss=D(2026, 11, 2), notified=D(2026, 11, 4), amount=3400, amount_style="plain")
    add(id="c08-email-water", family="email_txt", split="test", tags=["email"], doc="email", policy="TM-CP-100008", cause="water", loss=D(2026, 10, 30), notified=D(2026, 11, 3), amount=5200, claimant_style="and_limited")
    add(id="c09-email-storm", family="email_txt", split="test", tags=["email"], doc="email", policy="TM-CP-100010", cause="storm", loss=D(2026, 11, 7), notified=D(2026, 11, 9), amount=9800)

    # email_eml: a broker's email with labelled lines.
    add(id="c10-eml-impact", family="email_eml", split="dev", tags=["eml"], doc="eml", policy="TM-CP-100005", cause="impact", loss=D(2026, 11, 10), notified=D(2026, 11, 11), amount=5400, loss_style="weekday")
    add(id="c11-eml-fire", family="email_eml", split="test", tags=["eml"], doc="eml", policy="TM-CP-100010", cause="fire", loss=D(2026, 10, 19), notified=D(2026, 10, 22), amount=14250, amount_style="pence")
    add(id="c12-eml-water", family="email_eml", split="test", tags=["eml"], doc="eml", policy="TM-CP-100001", cause="water", loss=D(2026, 11, 1), notified=D(2026, 11, 3), amount=7600)

    # missing_policy / missing_loss_date / missing_amount
    add(id="c13-missing-policy-form", family="missing_policy", split="dev", tags=["missing_field"], policy="TM-CP-100003", policy_written=None, cause="flood", loss=D(2026, 10, 28), loss_style="slash4", notified=D(2026, 11, 2), amount=4200)
    add(id="c14-missing-policy-email", family="missing_policy", split="test", tags=["missing_field"], doc="email", policy="TM-CP-100002", policy_written=None, cause="theft", loss=D(2026, 11, 5), notified=D(2026, 11, 6), amount=2900)
    add(id="c15-missing-loss-date-form", family="missing_loss_date", split="test", tags=["missing_field"], policy="TM-CP-100003", cause="water", loss=None, notified=D(2026, 11, 8), amount=3100)
    add(id="c16-missing-amount-form", family="missing_amount", split="test", tags=["missing_field"], policy="TM-CP-100007", cause="refrigeration", loss=D(2026, 11, 6), notified=D(2026, 11, 9), amount=None)
    add(id="c17-missing-amount-email", family="missing_amount", split="test", tags=["missing_field"], doc="email", policy="TM-CP-100009", cause="water", loss=D(2026, 11, 4), notified=D(2026, 11, 5), amount=None)

    # amount_words: "twelve thousand pounds", and other amounts not written as £n,nnn.
    add(id="c18-amount-words-form", family="amount_words", split="dev", tags=["amount_words"], policy="TM-CP-100001", cause="fire", loss=D(2026, 10, 25), notified=D(2026, 10, 29), amount=12000, amount_style="words")
    add(id="c19-amount-words-email", family="amount_words", split="test", tags=["amount_words"], doc="email", policy="TM-CP-100002", cause="water", loss=D(2026, 11, 3), notified=D(2026, 11, 5), amount=8500, amount_style="words")
    add(id="c20-amount-gbp-form", family="amount_words", split="test", tags=["amount_words"], policy="TM-CP-100008", cause="theft", loss=D(2026, 10, 15), notified=D(2026, 10, 19), amount=15200, amount_style="gbp")

    # uk_dates: 3rd Nov 2026, 03/11/26, 03/11/2026.
    add(id="c21-uk-date-ordinal-short", family="uk_dates", split="dev", tags=["uk_dates"], policy="TM-CP-100001", cause="water", loss=D(2026, 11, 3), loss_style="ordinal_short", notified=D(2026, 11, 5), amount=6400)
    add(id="c22-uk-date-slash-2-digit-year", family="uk_dates", split="test", tags=["uk_dates"], policy="TM-CP-100008", cause="fire", loss=D(2026, 11, 3), loss_style="slash2", notified=D(2026, 11, 6), amount=7200)
    add(id="c23-uk-date-slash-4-digit-year", family="uk_dates", split="test", tags=["uk_dates"], doc="email", policy="TM-CP-100005", cause="impact", loss=D(2026, 11, 3), loss_style="slash4", notified=D(2026, 11, 4), amount=3300)

    # policy_typo: the number as written does not exist, or does not look like one.
    add(id="c24-policy-typo-short", family="policy_typo", split="dev", tags=["policy_typo"], policy="TM-CP-100002", policy_written="TM-CP-10002", cause="theft", loss=D(2026, 11, 2), notified=D(2026, 11, 4), amount=2600)
    add(id="c25-policy-typo-letter-o", family="policy_typo", split="test", tags=["policy_typo"], doc="email", policy="TM-CP-100003", policy_written="TM-CP-1OO003", cause="water", loss=D(2026, 11, 5), notified=D(2026, 11, 6), amount=4800)

    # garbled layout and OCR noise.
    add(id="c26-garbled-freezer", family="garbled_layout", split="dev", tags=["garbled", "ocr_noise"], doc="scattered", policy="TM-CP-100007", cause="refrigeration", loss=D(2026, 11, 11), loss_style="slash4", notified=D(2026, 11, 12), notified_stated=False, amount=3100, noise=True)
    add(id="c27-garbled-water", family="garbled_layout", split="test", tags=["garbled"], doc="scattered", policy="TM-CP-100001", cause="water", loss=D(2026, 11, 2), notified=D(2026, 11, 5), notified_stated=False, amount=5800)
    add(id="c28-ocr-noise-form", family="garbled_layout", split="test", tags=["ocr_noise"], policy="TM-CP-100010", cause="theft", loss=D(2026, 10, 24), notified=D(2026, 10, 27), amount=6900, noise=True)

    # multiple_dates: the loss date is not the only date in the document.
    add(id="c29-multiple-dates-form", family="multiple_dates", split="test", tags=["multiple_dates"], policy="TM-CP-100006", cause="water", loss=D(2026, 10, 30), notified=D(2026, 11, 4), amount=5300, extra_dates=True)
    add(id="c30-multiple-dates-email", family="multiple_dates", split="test", tags=["multiple_dates"], doc="email", policy="TM-CP-100001", cause="water", loss=D(2026, 11, 3), notified=D(2026, 11, 5), amount=4100, extra_dates=True)

    # exclusion triggers.
    add(id="c31-exclusion-flood-office", family="exclusion_flood", split="dev", tags=["exclusion"], policy="TM-CP-100009", cause="flood", loss=D(2026, 11, 10), notified=D(2026, 11, 12), amount=7800)
    add(id="c32-exclusion-flood-engineering", family="exclusion_flood", split="test", tags=["exclusion"], policy="TM-CP-100004", cause="flood", loss=D(2026, 10, 20), notified=D(2026, 10, 23), amount=18500)
    add(id="c33-exclusion-wear-office", family="exclusion_wear", split="test", tags=["exclusion"], policy="TM-CP-100006", cause="water", wear=True, loss=D(2026, 10, 26), notified=D(2026, 10, 29), amount=3900)
    add(id="c34-exclusion-unoccupied-water", family="exclusion_unoccupied", split="test", tags=["exclusion"], policy="TM-CP-100008", cause="water", unoccupied_days=45, loss=D(2026, 11, 5), notified=D(2026, 11, 8), amount=15200)
    add(id="c35-exclusion-unoccupied-theft", family="exclusion_unoccupied", split="test", tags=["exclusion"], doc="email", policy="TM-CP-100002", cause="theft", unoccupied_days=60, loss=D(2026, 11, 1), notified=D(2026, 11, 3), amount=11000)

    # late notification: the loss and the notification are more days apart than the wording allows.
    add(id="c36-late-notification-form", family="late_notification", split="dev", tags=["late_notification"], policy="TM-CP-100008", cause="fire", loss=D(2026, 9, 28), notified=D(2026, 11, 9), amount=22000)
    add(id="c37-late-notification-office", family="late_notification", split="test", tags=["late_notification"], policy="TM-CP-100006", cause="water", loss=D(2026, 10, 20), notified=D(2026, 11, 9), amount=4700)
    # A document that states an earlier notification date than the one it was received on. The stated date (10 days) is in time;
    # measured to the received date (16 days) it would be late on a 14-day wording.
    add(id="c38-stated-before-received", family="stated_vs_received", split="test", tags=["stated_vs_received"], doc="email", policy="TM-CP-100009", cause="theft", loss=D(2026, 10, 20), notified=D(2026, 10, 30), received=D(2026, 11, 5), amount=2800)

    # duplicates of a claim that already exists (in the review store or the legacy system).
    add(id="c39-duplicate-claim-chasing-email", family="duplicate_claim", split="test", tags=["duplicate_claim"], doc="email", policy="TM-CP-100001", cause="water", loss=D(2026, 11, 3), notified=D(2026, 11, 13), amount=12400,
        existing=[{"reference": "LEG-0042", "source": "legacy", "lossDate": "2026-11-03", "amountGbp": 12500.0}])
    add(id="c40-duplicate-claim-resubmitted-form", family="duplicate_claim", split="test", tags=["duplicate_claim"], policy="TM-CP-100002", cause="theft", loss=D(2026, 10, 21), notified=D(2026, 10, 30), amount=8750,
        existing=[{"reference": "9f1c2a64-5b73-4e0d-8a21-7c3b4d5e6f70", "source": "review", "lossDate": "2026-10-21", "amountGbp": 8700.0}])

    # a loss outside the policy period, and one in the future.
    add(id="c41-loss-outside-period", family="outside_period", split="test", tags=["outside_period"], policy="TM-CP-100004", cause="breakdown", loss=D(2026, 11, 8), notified=D(2026, 11, 12), amount=21000)
    add(id="c42-loss-date-in-future", family="future_date", split="test", tags=["future_date"], policy="TM-CP-100003", cause="fire", loss=D(2027, 2, 15), notified=D(2026, 11, 14), amount=9800)

    # non-claims.
    add(id="c43-non-claim-marketing", family="non_claim", split="test", doc="text", tags=["non_claim"], policy=None, cause=None, amount=None, quality="NotAClaim", body="marketing")
    add(id="c44-non-claim-french", family="non_claim", split="dev", doc="text", tags=["non_claim"], policy=None, cause=None, amount=None, quality="NotAClaim", body="french")
    add(id="c45-non-claim-internal-email", family="non_claim", split="test", doc="text", tags=["non_claim"], policy=None, cause=None, amount=None, quality="NotAClaim", body="internal")

    # input-quality gate: documents a person has to look at.
    add(id="c46-quality-blank-scan", family="quality", split="dev", doc="blank_pdf", tags=["quality_gate"], policy=None, cause=None, amount=None, quality="UnreadableDocument")
    add(id="c47-quality-near-empty-note", family="quality", split="test", doc="text", tags=["quality_gate"], policy=None, cause=None, amount=None, quality="UnreadableDocument", body="See attached scan.")
    add(id="c48-quality-corrupt-pdf", family="quality", split="dev", doc="corrupt_pdf", tags=["quality_gate"], policy=None, cause=None, amount=None, quality="ProtectedOrCorruptDocument")
    add(id="c49-quality-password-pdf", family="quality", split="test", doc="protected_pdf", tags=["quality_gate"], policy=None, cause=None, amount=None, quality="ProtectedOrCorruptDocument")
    add(id="c50-quality-oversized", family="quality", split="test", doc="text", tags=["quality_gate"], policy=None, cause=None, amount=None, quality="DocumentTooLarge", body="marketing", max_bytes=300)
    add(id="c51-quality-duplicate-file", family="quality", split="test", doc="text", tags=["quality_gate", "duplicate_file"], policy=None, cause=None, amount=None, quality="DuplicateDocument", body="marketing", duplicate_of="c43-non-claim-marketing")

    # Derived tags: what the document is, and how it is written.
    for s in c:
        if s.doc in ("form", "scattered") and "pdf" not in s.tags:
            s.tags.append("pdf")
        if s.doc == "email" and "email" not in s.tags:
            s.tags.append("email")
        if s.amount_style in ("words", "gbp") and s.amount is not None and "amount_words" not in s.tags:
            s.tags.append("amount_words")
        if s.loss_style in ("ordinal_short", "slash2", "slash4", "weekday", "ordinal_long") and s.loss and "uk_dates" not in s.tags:
            s.tags.append("uk_dates")
        if s.claimant_style != "canonical" and "name_variant" not in s.tags:
            s.tags.append("name_variant")
        if s.wear or s.unoccupied_days or s.cause == "flood":
            if "exclusion" not in s.tags:
                s.tags.append("exclusion")
        s.tags = sorted(set(s.tags))
    return c


# --------------------------------------------------------------------------------------------------------------------

def render(spec: Spec, rng: random.Random) -> str:
    """Writes the document and returns its file name."""
    ext = {"form": "pdf", "scattered": "pdf", "blank_pdf": "pdf", "corrupt_pdf": "pdf", "protected_pdf": "pdf", "email": "txt", "eml": "eml", "text": "txt"}[spec.doc]
    name = f"{spec.id}.{ext}"
    path = OUT / name
    if spec.doc == "form":
        render_form(spec, rng, path)
    elif spec.doc == "scattered":
        render_scattered(spec, rng, path)
    elif spec.doc == "email":
        render_email(spec, rng, path)
    elif spec.doc == "eml":
        render_eml(spec, rng, path)
    elif spec.doc == "text":
        body = NON_CLAIMS.get(spec.body or "", spec.body or "")
        path.write_text(body, encoding="utf-8", newline="\n")
    else:
        build_pdf_fixtures(OUT / ".tmp")
        source = {"blank_pdf": "scan.pdf", "corrupt_pdf": "corrupt.pdf", "protected_pdf": "protected.pdf"}[spec.doc]
        path.write_bytes((OUT / ".tmp" / source).read_bytes())
    return name


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / ".tmp").mkdir(exist_ok=True)
    for old in OUT.iterdir():
        if old.is_file():
            old.unlink()

    specs = cases()
    ids = [s.id for s in specs]
    assert len(ids) == len(set(ids)), "duplicate case ids"

    for spec in specs:
        rng = random.Random(f"{SEED}-{spec.id}")
        name = render(spec, rng)
        notified_for_received = spec.received or spec.notified
        meta = {
            "id": spec.id,
            "file": name,
            "split": spec.split,
            "family": spec.family,
            "tags": spec.tags,
            "context": {
                "receivedAt": received_at(notified_for_received, random.Random(f"{SEED}-{spec.id}-received")) if notified_for_received else "2026-11-14T09:00:00Z",
                "existingClaims": [
                    {"reference": e["reference"], "source": e["source"], "policyNumber": spec.policy, "lossDate": e["lossDate"], "amountGbp": e["amountGbp"], "status": "Submitted"}
                    for e in spec.existing
                ],
                "duplicateOf": spec.duplicate_of,
                "maxDocumentBytes": spec.max_bytes,
            },
            "expected": expected(spec),
        }
        exp = meta["expected"]["fields"]
        # A document that states no notification date is dated by when it was received.
        # Emails and .eml files always carry a sent date; forms only when the spec says so.
        if not spec.quality:
            stated = spec.doc in ("email", "eml") or (spec.doc in ("form",) and spec.notified_stated and spec.notified is not None)
            if not stated and spec.notified is not None:
                exp["notification_date"] = (spec.received or spec.notified).isoformat()
            meta["expected"]["notificationSource"] = "document" if stated else "received"
        (OUT / f"{spec.id}.expected.json").write_text(json.dumps(meta, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")

    # Remove the scratch folder and write the small fixtures the unit tests use.
    for f in (OUT / ".tmp").iterdir():
        f.unlink()
    (OUT / ".tmp").rmdir()
    FIXTURES.mkdir(parents=True, exist_ok=True)
    build_pdf_fixtures(FIXTURES)
    (FIXTURES / "good.pdf").unlink(missing_ok=True)

    dev = sum(1 for s in specs if s.split == "dev")
    print(f"Wrote {len(specs)} cases to {OUT} (dev {dev}, test {len(specs) - dev}) and fixtures to {FIXTURES}")


if __name__ == "__main__":
    main()
