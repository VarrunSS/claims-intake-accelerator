using ClaimsIntake.Domain;
using ClaimsIntake.Pipeline.Fields;
using ClaimsIntake.Pipeline.Text;

namespace ClaimsIntake.Pipeline.Tests;

public class HeuristicClaimExtractorTests
{
    private static async Task<Dictionary<string, ExtractedField>> Extract(string text)
    {
        var fields = await new HeuristicClaimExtractor().ExtractAsync("doc-1", new DocumentText([text]), CancellationToken.None);
        return fields.ToDictionary(f => f.Name);
    }

    [Fact]
    public async Task It_always_returns_the_five_fields_and_finds_nothing_in_unrelated_text()
    {
        var fields = await Extract("Hello, just checking you got my last message about the weekend.");

        Assert.Equal(
            [ClaimFields.ClaimantName, ClaimFields.EstimatedAmount, ClaimFields.LossDate, ClaimFields.LossDescription, ClaimFields.PolicyNumber],
            fields.Keys.Order());
        Assert.All(fields.Values, f =>
        {
            Assert.Null(f.Value);
            Assert.Equal(0.0, f.Confidence);
            Assert.Null(f.Source);
        });
    }

    [Fact]
    public async Task It_is_deterministic()
    {
        const string text = "Policy number: TM-CP-100001\nInsured: Brindle & Marsh Joinery Ltd\nDate of loss: 3 November 2026\n";

        Assert.Equal(await Extract(text), await Extract(text));
    }

    [Fact]
    public async Task A_labelled_policy_number_scores_higher_than_a_bare_one()
    {
        var labelled = (await Extract("Policy number: TM-CP-100001"))[ClaimFields.PolicyNumber];
        var bare = (await Extract("Please open a claim on TM-CP-100001 today."))[ClaimFields.PolicyNumber];

        Assert.Equal("TM-CP-100001", labelled.Value);
        Assert.Equal(0.97, labelled.Confidence);
        Assert.Equal("TM-CP-100001", bare.Value);
        Assert.Equal(0.88, bare.Confidence);
    }

    [Fact]
    public async Task A_bare_policy_number_span_covers_the_whole_number()
    {
        const string text = "Ref 12\nOur policy TM CP 100002 covers it";
        var field = (await Extract(text))[ClaimFields.PolicyNumber];

        Assert.Equal("TM-CP-100002", field.Value); // Tidied to the standard form.
        Assert.Equal("TM CP 100002", text.Substring(field.Source!.Offset, field.Source.Length));
    }

    [Fact]
    public async Task Two_different_policy_numbers_make_the_choice_doubtful()
    {
        var field = (await Extract("Policy number: TM-CP-100001\nThis follows our earlier claim on TM-CP-100002."))[ClaimFields.PolicyNumber];

        Assert.Equal("TM-CP-100001", field.Value);
        Assert.True(field.Confidence <= 0.65);
    }

    [Theory]
    [InlineData("Policy number: not to hand")]
    [InlineData("Policy number: unknown")]
    [InlineData("Policy no: TBC")]
    public async Task A_policy_label_without_a_number_gives_no_policy_number(string text)
    {
        Assert.Null((await Extract(text))[ClaimFields.PolicyNumber].Value);
    }

    [Fact]
    public async Task An_unfamiliar_policy_format_is_taken_with_low_confidence()
    {
        var field = (await Extract("Policy number: ABC/2026/7781"))[ClaimFields.PolicyNumber];

        Assert.Equal("ABC/2026/7781", field.Value);
        Assert.Equal(0.55, field.Confidence);
    }

    [Theory]
    [InlineData("Date of loss: 2026-11-03", "2026-11-03")]
    [InlineData("Date of loss: 3 November 2026", "2026-11-03")]
    [InlineData("Date of loss: 3rd November 2026", "2026-11-03")]
    [InlineData("Date of loss: Tuesday 3 November 2026", "2026-11-03")]
    [InlineData("Date of incident: 1 Sept 2026", "2026-09-01")]
    [InlineData("Loss date: 25/10/2026", "2026-10-25")]
    public async Task Dates_are_read_in_the_formats_seen_in_the_samples_and_tidied_to_iso(string text, string expected)
    {
        var field = (await Extract(text))[ClaimFields.LossDate];

        Assert.Equal(expected, field.Value);
        Assert.Equal(0.93, field.Confidence);
    }

    [Fact]
    public async Task A_day_month_pair_that_can_be_read_either_way_lowers_confidence()
    {
        var field = (await Extract("Date of loss: 03/11/2026"))[ClaimFields.LossDate];

        Assert.Equal("2026-11-03", field.Value); // UK order.
        Assert.Equal(0.85, field.Confidence);
    }

    [Fact]
    public async Task An_impossible_date_is_kept_as_written_for_the_validator_to_report()
    {
        var field = (await Extract("Date of loss: 31/02/2026"))[ClaimFields.LossDate];

        Assert.Equal("31/02/2026", field.Value);
    }

    [Fact]
    public async Task An_unlabelled_date_scores_lower_and_the_email_sent_date_is_ignored()
    {
        const string email = "From: A <a@x.example>\nDate: Tue, 10 Nov 2026 08:14:02 +0000\n\nIt was last seen working on 5 November 2026 and then it stopped.";
        var field = (await Extract(email))[ClaimFields.LossDate];

        Assert.Equal("2026-11-05", field.Value);
        Assert.Equal(0.40, field.Confidence);
        Assert.Null((await Extract("Date: 10 Nov 2026\nNo other date here."))[ClaimFields.LossDate].Value);
    }

    [Fact]
    public async Task A_labelled_name_beats_an_organisation_pattern_which_beats_the_email_sender()
    {
        var labelled = (await Extract("Insured: Brindle & Marsh Joinery Ltd"))[ClaimFields.ClaimantName];
        var pattern = (await Extract("We are Kestrel Print & Packaging Ltd and we have a loss."))[ClaimFields.ClaimantName];
        var sender = (await Extract("From: \"Marcus Hale\" <m@x.example>\n\nHello"))[ClaimFields.ClaimantName];

        Assert.Equal(("Brindle & Marsh Joinery Ltd", 0.93), (labelled.Value, labelled.Confidence));
        Assert.Equal(("Kestrel Print & Packaging Ltd", 0.62), (pattern.Value, pattern.Confidence));
        Assert.Equal(("Marcus Hale", 0.45), (sender.Value, sender.Confidence));
    }

    [Fact]
    public async Task A_labelled_description_runs_to_the_next_label()
    {
        var field = (await Extract("Description of loss: A pipe burst.\nStock was soaked.\nEstimated amount: £500"))[ClaimFields.LossDescription];

        Assert.Equal("A pipe burst. Stock was soaked.", field.Value);
        Assert.Equal(0.88, field.Confidence);
    }

    [Fact]
    public async Task Without_a_label_a_sentence_with_a_loss_word_is_a_weak_description()
    {
        var field = (await Extract("Subject: Fire\n\nHello. There was a fire in the stockroom last night. Please call."))[ClaimFields.LossDescription];

        Assert.Equal("There was a fire in the stockroom last night.", field.Value);
        Assert.Equal(0.55, field.Confidence);
    }

    [Fact]
    public async Task A_labelled_amount_is_trusted_and_an_excess_is_not_taken_for_the_loss()
    {
        var labelled = (await Extract("Estimated amount: £12,500"))[ClaimFields.EstimatedAmount];
        var excessFirst = (await Extract("There is an excess of £250. We think about £6,000 is lost."))[ClaimFields.EstimatedAmount];

        Assert.Equal(("£12,500", 0.90), (labelled.Value, labelled.Confidence));
        Assert.Equal(("£6,000", 0.55), (excessFirst.Value, excessFirst.Confidence));
    }

    [Fact]
    public async Task Source_spans_are_page_numbered_from_one_across_pages()
    {
        var fields = await new HeuristicClaimExtractor().ExtractAsync(
            "doc-1", new DocumentText(["Cover page.", "Policy number: TM-CP-100001"]), CancellationToken.None);

        var policy = fields.Single(f => f.Name == ClaimFields.PolicyNumber);
        Assert.Equal(2, policy.Source!.Page);
        Assert.Equal("doc-1", policy.Source.DocumentId);
    }

    [Fact]
    public async Task Hostile_text_cannot_hang_the_extractor()
    {
        var text = string.Concat(Enumerable.Repeat("Policy ", 20_000)) + new string('£', 5_000) + string.Concat(Enumerable.Repeat("1,", 20_000));

        var started = DateTime.UtcNow;
        await Extract(text);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("twelve thousand pounds", "£12,000")]
    [InlineData("twelve thousand five hundred pounds", "£12,500")]
    [InlineData("eight thousand seven hundred and fifty pounds", "£8,750")]
    [InlineData("four thousand two hundred pounds", "£4,200")]
    [InlineData("twenty-five pounds", "£25")]
    public async Task An_amount_written_in_words_is_read(string words, string expected)
    {
        var fields = await Extract($"Policy number: TM-CP-100001\nWe estimate the damage at {words}.");

        Assert.Equal(expected, fields[ClaimFields.EstimatedAmount].Value);
        Assert.Equal(0.80, fields[ClaimFields.EstimatedAmount].Confidence);
    }

    [Fact]
    public async Task An_excess_written_in_words_is_not_the_loss_amount()
    {
        var fields = await Extract("The excess is two hundred and fifty pounds.");

        Assert.Null(fields[ClaimFields.EstimatedAmount].Value);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("hundred", false)]
    [InlineData("banana thousand", false)]
    [InlineData("one hundred", true)]
    public void Number_words_that_make_no_number_are_refused(string text, bool ok) =>
        Assert.Equal(ok, NumberWords.TryParse(text, out _));

    [Fact]
    public async Task The_organisation_named_in_an_email_sentence_is_the_claimant_with_moderate_confidence()
    {
        var fields = await Extract("From: Dan <dan@x.example>\n\nI am the office manager at Wren & Co Estate Agents. Sorry to write by email.");

        Assert.Equal("Wren & Co Estate Agents", fields[ClaimFields.ClaimantName].Value);
        Assert.Equal(0.70, fields[ClaimFields.ClaimantName].Confidence);
    }

    [Fact]
    public async Task Lowercase_text_after_at_is_not_a_claimant()
    {
        var fields = await Extract("There was a fire at the premises last night.");

        Assert.Null(fields[ClaimFields.ClaimantName].Value);
    }
}
