#!/usr/bin/env python3
"""Generates the synthetic claim documents in this folder.

Everything here is made up: Thames Mutual Insurance is a fictional insurer, the insured businesses and people
are invented, and the policy numbers match the seed data in src/ClaimsIntake.Legacy/Policies.cs.

Every form states a "Date notified", and every email carries its sent date, so the notification-period check does not
depend on the day the sample is processed (Milestone 4 date fix). Only 08-garbled-layout.pdf states none: it falls back to
the time the file was received.

The documents are written to be awkward in useful ways. Each has a file name that says what is wrong with it,
and tests/ClaimsIntake.Pipeline.Tests/SampleDocumentTests.cs checks that the pipeline finds that problem.

Usage:
    pip install reportlab        # BSD licence
    python samples/generate_samples.py

The PDFs are written with reportlab's invariant mode, so running this again produces identical files.
"""

from pathlib import Path

from reportlab.lib.pagesizes import A4
from reportlab.pdfgen import canvas

OUT = Path(__file__).resolve().parent
PAGE_WIDTH, PAGE_HEIGHT = A4


def form_pdf(name: str, title: str, lines: list[str], footer: str = "") -> None:
    """A plain one-page notification form: a title, then one line of text per entry."""
    pdf = canvas.Canvas(str(OUT / name), pagesize=A4, invariant=1)
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


def scattered_pdf(name: str, title: str, pieces: list[tuple[float, float, str, int]]) -> None:
    """A page where fragments of text sit at odd positions, as a bad scan or a table export can leave them."""
    pdf = canvas.Canvas(str(OUT / name), pagesize=A4, invariant=1)
    pdf.setTitle(title)
    for x, y, text, size in pieces:
        pdf.setFont("Helvetica", size)
        pdf.drawString(x, y, text)
    pdf.save()


def main() -> None:
    form_pdf(
        "01-water-damage-clean.pdf",
        "Thames Mutual Insurance - Commercial claim notification",
        [
            "Policy number: TM-CP-100001",
            "Insured: Brindle & Marsh Joinery Ltd",
            "Date of loss: 3 November 2026",
            "Date notified: 6 November 2026",
            "",
            "Description of loss: A water pipe burst in the workshop store room overnight.",
            "Timber stock and two bench saws were damaged by the water.",
            "",
            "Estimated amount: £12,500",
            "",
            "Reported by: Sam Brindle, Director",
        ],
        footer="Synthetic sample document. Fictional insurer and customer.",
    )

    form_pdf(
        "02-theft-clean.pdf",
        "Thames Mutual Insurance - Commercial claim notification",
        [
            "Policy no: TM-CP-100002",
            "Insured: Kestrel Print & Packaging Ltd",
            "Date of loss: 2026-10-21",
            "Date notified: 14 November 2026",
            "",
            "Description of loss: Forced entry through the rear loading door. Two pallets of",
            "coated board and a forklift battery were stolen.",
            "",
            "Estimated cost: £8,750.00",
        ],
        footer="Synthetic sample document. Fictional insurer and customer.",
    )

    form_pdf(
        "03-missing-policy-number.pdf",
        "Claim notification",
        [
            "Policy number: not to hand, please look us up",
            "Insured: Harcourt Dental Practice",
            "Date of loss: 28/10/2026",
            "Date notified: 14 November 2026",
            "",
            "Description of loss: The basement surgery flooded after heavy rain and the",
            "compressor and two chairs were damaged.",
            "",
            "Estimated amount: £4,200",
        ],
        footer="Synthetic sample document. The policy number is missing on purpose.",
    )

    form_pdf(
        "04-future-loss-date.pdf",
        "Thames Mutual Insurance - Commercial claim notification",
        [
            "Policy number: TM-CP-100003",
            "Insured: Harcourt Dental Practice",
            "Date of loss: 15 February 2027",
            "Date notified: 14 November 2026",
            "",
            "Description of loss: Fire in the sterilisation room. Smoke damage to the",
            "autoclave and the reception area.",
            "",
            "Estimated amount: £9,800",
        ],
        footer="Synthetic sample document. The loss date is in the future on purpose.",
    )

    form_pdf(
        "05-loss-outside-policy-period.pdf",
        "Thames Mutual Insurance - Commercial claim notification",
        [
            "Policy number: TM-CP-100004",
            "Insured: Lowther Fabrication Works Ltd",
            "Date of loss: 8 November 2026",
            "Date notified: 14 November 2026",
            "",
            "Description of loss: A press brake hydraulic line failed and the machine was",
            "damaged beyond economic repair.",
            "",
            "Estimated amount: £21,000",
        ],
        footer="Synthetic sample document. Policy TM-CP-100004 ended on 31 October 2026.",
    )

    (OUT / "06-email-fire.txt").write_text(
        "From: Priya Nair <priya.nair@wren-estates.example>\n"
        "To: claims@thamesmutual.example\n"
        "Date: Tue, 10 Nov 2026 08:14:02 +0000\n"
        "Subject: Fire at our Guildford office - please can you open a claim\n"
        "\n"
        "Hi,\n"
        "\n"
        "I am the office manager at Wren & Co Estate Agents. Sorry to be sending this by email but\n"
        "I do not have the claim form. We have policy TM-CP-100006.\n"
        "\n"
        "There was a fire in the back office. It happened on the night of 9th November 2026 and\n"
        "the cleaners found it first thing. The fire brigade came and put it out. Two desks, the\n"
        "server cabinet and a lot of paperwork are ruined. We think it is roughly £6,000 of damage.\n"
        "I know there is an excess of £250.\n"
        "\n"
        "Can someone call me?\n"
        "\n"
        "Thanks,\n"
        "Priya\n",
        encoding="utf-8",
        newline="\n",
    )

    (OUT / "07-broker-email.eml").write_text(
        "From: \"Marcus Hale\" <marcus.hale@hale-brokers.example>\r\n"
        "To: claims@thamesmutual.example\r\n"
        "Subject: New claim - Pemberton Road Haulage Ltd (TM-CP-100005)\r\n"
        "Date: Wed, 11 Nov 2026 16:40:11 +0000\r\n"
        "Message-ID: <20261111164011.1234@hale-brokers.example>\r\n"
        "MIME-Version: 1.0\r\n"
        "Content-Type: text/plain; charset=utf-8\r\n"
        "\r\n"
        "Hello claims team,\r\n"
        "\r\n"
        "Please register the following on behalf of our client.\r\n"
        "\r\n"
        "Policy number: TM-CP-100005\r\n"
        "Insured: Pemberton Road Haulage Ltd\r\n"
        "Date of loss: Tuesday 10 November 2026\r\n"
        "Description of loss: A fleet lorry was struck from behind at the M25 Junction 10\r\n"
        "roundabout. The rear doors and tail lift were damaged. No injuries reported.\r\n"
        "Estimated amount: £5,400\r\n"
        "\r\n"
        "Kind regards,\r\n"
        "Marcus Hale\r\n",
        encoding="utf-8",
        newline="",
    )

    # A table export that lost its structure: fragments of the form are scattered over the page, a word is
    # split across two pieces, and the labels are far from their values.
    scattered_pdf(
        "08-garbled-layout.pdf",
        "Claim notification",
        [
            (60, 790, "CLAIM NOTIF", 14),
            (300, 760, "ICATION  ref 77-B", 14),
            (400, 700, "Loss", 10),
            (60, 690, "Tideway Cater", 11),
            (230, 640, "ing Supplies Ltd", 11),
            (60, 600, "Pol", 10),
            (340, 596, "TM-CP-100007", 11),
            (120, 540, "11/11/2026", 11),
            (420, 520, "damaged", 11),
            (60, 470, "Walk-in freezer comp-", 11),
            (330, 440, "ressor failed overnight, stock", 11),
            (90, 400, "spoiled", 11),
            (450, 340, "£3 1OO", 11),
            (60, 120, "Page 1 of 1", 8),
        ],
    )

    # --- Milestone 3: samples that the policy wording check has something to find in. ---

    form_pdf(
        "09-flood-office-exclusion.pdf",
        "Thames Mutual Insurance - Commercial claim notification",
        [
            "Policy number: TM-CP-100009",
            "Insured: Greyfriars Bookbinders",
            "Date of loss: 10 November 2026",
            "Date notified: 14 November 2026",
            "",
            "Description of loss: Heavy rain overnight overwhelmed the drains and surface water",
            "flooded the ground-floor workshop. Paper stock and two guillotines were damaged.",
            "",
            "Estimated amount: £7,800",
        ],
        footer="Synthetic sample document. Policy TM-CP-100009 is an Office wording, which excludes flood (clause 4.1).",
    )

    form_pdf(
        "10-unoccupied-late-notification.pdf",
        "Thames Mutual Insurance - Commercial claim notification",
        [
            "Policy number: TM-CP-100008",
            "Insured: Ashdown Veterinary Group",
            "Date of loss: 5 October 2026",
            "Date notified: 14 November 2026",
            "",
            "Description of loss: A pipe burst in our closed branch surgery, which had been",
            "unoccupied for 45 days while we refitted it. The floor and fittings were damaged.",
            "",
            "Estimated amount: £15,200",
        ],
        footer="Synthetic sample document. Unoccupied over 30 days (clause 4.3) and notified more than 30 days after the loss (clause 5.1).",
    )

    (OUT / "11-duplicate-water-damage-email.txt").write_text(
        "From: Sam Brindle <sam@brindle-marsh.example>\n"
        "To: claims@thamesmutual.example\n"
        "Date: Fri, 13 Nov 2026 09:02:40 +0000\n"
        "Subject: Chasing our water damage claim\n"
        "\n"
        "Hello,\n"
        "\n"
        "I have not heard back about the water damage at our workshop, so I am sending the details again\n"
        "in case the first form did not arrive.\n"
        "\n"
        "Policy number: TM-CP-100001\n"
        "Insured: Brindle & Marsh Joinery Ltd\n"
        "Date of loss: 3 November 2026\n"
        "Description of loss: A water pipe burst in the workshop store room and soaked the timber stock.\n"
        "Estimated amount: £12,400\n"
        "\n"
        "Thanks,\n"
        "Sam\n",
        encoding="utf-8",
        newline="\n",
    )

    print(f"Wrote samples to {OUT}")


if __name__ == "__main__":
    main()
