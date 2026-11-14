using ClaimsIntake.Legacy;
using ClaimsIntake.Pipeline.Policy;

namespace ClaimsIntake.Pipeline.Tests;

public class PolicyWordingParserTests
{
    private const string Small = """
        ---
        id: TM-PW-TEST-2026
        title: A small wording
        ---

        # Title

        > Notice that is not a clause.

        ## 1. Insuring clause

        ### 1.1 Cover
        We will pay for damage.

        ## 4. Exclusions

        ### 4.1 Flood
        We will not pay for flood.
        This continues on a second line.

        ### 4.10 Other
        Anything else.
        """;

    [Fact]
    public void A_wording_is_chunked_by_clause_with_its_section_reference_heading_and_text()
    {
        var wording = PolicyWordingParser.Parse(Small);

        Assert.Equal("TM-PW-TEST-2026", wording.Id);
        Assert.Equal("A small wording", wording.Title);
        Assert.Equal(["1.1", "4.1", "4.10"], wording.Clauses.Select(c => c.ClauseRef));
        var flood = wording.Clauses[1];
        Assert.Equal(("Exclusions", "Flood"), (flood.Section, flood.Heading));
        Assert.Equal("We will not pay for flood.\nThis continues on a second line.", flood.Text.ReplaceLineEndings("\n"));
        Assert.Equal("TM-PW-TEST-2026/4.1", flood.Key);
    }

    [Fact]
    public void Text_outside_a_clause_is_not_indexed()
    {
        var wording = PolicyWordingParser.Parse(Small);

        Assert.DoesNotContain(wording.Clauses, c => c.Text.Contains("Notice", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("# No front matter\n### 1.1 A\ntext")]
    [InlineData("---\nid: X\n# never closed")]
    [InlineData("---\ntitle: no id\n---\n### 1.1 A\ntext")]
    [InlineData("---\nid: bad id with spaces\n---\n### 1.1 A\ntext")]
    [InlineData("---\nid: X\n---\n# only a title")]
    [InlineData("---\nid: X\n---\n### 1.1 A\n### 1.2 B\ntext")]
    [InlineData("---\nid: X\n---\n### 1.1 A\ntext\n### 1.1 B\nmore")]
    public void A_malformed_wording_is_refused_with_a_message(string markdown)
    {
        Assert.Throws<InvalidDataException>(() => PolicyWordingParser.Parse(markdown));
    }

    [Fact]
    public void The_shipped_corpus_has_four_wordings_with_the_clauses_the_assessment_relies_on()
    {
        var wordings = Corpus.Wordings;

        Assert.Equal(["TM-PW-CC-2026", "TM-PW-CCF-2026", "TM-PW-EN-2026", "TM-PW-OF-2026"], wordings.Select(w => w.Id));
        foreach (var wording in wordings)
        {
            var sections = wording.Clauses.Select(c => c.Section).Distinct().ToList();
            Assert.Equal(["Insuring clause", "Definitions", "Insured perils", "Exclusions", "Conditions", "Excess and limits"], sections);
            Assert.Contains(wording.Clauses, c => c.Section == "Exclusions" && c.Heading.Contains("Wear", StringComparison.Ordinal));
            Assert.Contains(wording.Clauses, c => c.Section == "Exclusions" && c.Heading.Contains("Unoccupied", StringComparison.Ordinal));
            Assert.Contains(wording.Clauses, c => c.Section == "Conditions" && c.Heading.Contains("Notification", StringComparison.Ordinal));
            Assert.Contains(wording.Clauses, c => c.Section == "Excess and limits" && c.Heading.Contains("Excess", StringComparison.Ordinal));
            Assert.Equal(wording.Clauses.Count, wording.Clauses.Select(c => c.ClauseRef).Distinct().Count());
        }
    }

    [Fact]
    public void Flood_is_excluded_in_three_wordings_and_covered_in_the_extension()
    {
        var excluded = Corpus.Wordings.Where(w => w.Clauses.Any(c => c.Section == "Exclusions" && c.Heading == "Flood")).Select(w => w.Id);

        Assert.Equal(["TM-PW-CC-2026", "TM-PW-EN-2026", "TM-PW-OF-2026"], excluded);
        Assert.Contains(Corpus.Wordings.Single(w => w.Id == "TM-PW-CCF-2026").Clauses, c => c.Section == "Insured perils" && c.Heading == "Flood extension");
    }

    [Fact]
    public void Every_seeded_legacy_policy_is_mapped_to_a_wording_in_the_corpus()
    {
        var ids = Corpus.Wordings.Select(w => w.Id).ToHashSet();
        var store = new InMemoryPolicyStore();

        for (var n = 1; n <= 10; n++)
        {
            var policy = store.Find($"TM-CP-1000{n:D2}");
            Assert.NotNull(policy);
            Assert.Contains(policy.WordingId, ids);
        }
    }

    [Fact]
    public void A_missing_or_empty_folder_is_an_error_and_the_readme_is_not_a_wording()
    {
        Assert.Throws<DirectoryNotFoundException>(() => PolicyWordingParser.LoadFolder(Path.Combine(Corpus.Folder, "nope")));
        var empty = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(empty.FullName, "README.md"), "# not a wording");
            Assert.Throws<InvalidDataException>(() => PolicyWordingParser.LoadFolder(empty.FullName));
        }
        finally
        {
            empty.Delete(true);
        }
    }
}

public class Bm25PolicyWordingSearchTests
{
    private static readonly Bm25PolicyWordingSearch Search = Corpus.Search();

    private static async Task<IReadOnlyList<ClauseHit>> Find(string query, string? wording = null, int top = 5) =>
        await Search.SearchAsync(query, wording, top, CancellationToken.None);

    [Theory]
    [InlineData("flooded", "flood")]
    [InlineData("flooding", "flood")]
    [InlineData("floods", "flood")]
    [InlineData("damaged", "damag")]
    [InlineData("damage", "damag")]
    [InlineData("buildings", "build")]
    [InlineData("building", "build")]
    [InlineData("supplies", "supply")]
    [InlineData("pipes", "pip")]
    [InlineData("loss", "loss")]
    [InlineData("2026", "2026")]
    public void The_light_stemmer_joins_the_forms_of_a_word(string word, string stem)
    {
        Assert.Equal(stem, Bm25Tokeniser.Stem(word));
    }

    [Fact]
    public void Tokenising_lower_cases_drops_stop_words_and_punctuation()
    {
        Assert.Equal(["escap", "water", "burst", "pip"], Bm25Tokeniser.Tokenise("The Escape of WATER: a burst pipe!"));
        Assert.Empty(Bm25Tokeniser.Tokenise("   "));
        Assert.Empty(Bm25Tokeniser.Tokenise(null));
    }

    [Fact]
    public async Task A_flood_query_ranks_the_flood_exclusion_first_in_the_wording_that_has_one()
    {
        var hits = await Find("flood surface water", "TM-PW-OF-2026");

        Assert.Equal("4.1", hits[0].ClauseRef);
        Assert.Equal(("Exclusions", "Flood"), (hits[0].Section, hits[0].Heading));
        Assert.Equal("TM-PW-OF-2026", hits[0].WordingId);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public async Task The_heading_counts_for_more_than_a_passing_mention()
    {
        // Clause 3.2 (Escape of water) mentions flood in its text, but 4.1 is called Flood.
        var hits = await Find("flood", "TM-PW-CC-2026", top: 3);

        Assert.Equal("4.1", hits[0].ClauseRef);
    }

    [Theory]
    [InlineData("wear and tear gradual deterioration", "Exclusions", "Wear and tear")]
    [InlineData("notify us within days of the loss", "Conditions", "Notification of claims")]
    [InlineData("excess deducted from every claim", "Excess and limits", "Excess")]
    [InlineData("burst leaking pipe", "Insured perils", "Escape of water")]
    [InlineData("forcible violent entry theft", "Insured perils", "Theft")]
    public async Task Ordinary_questions_find_the_clause_a_handler_would_open(string query, string section, string heading)
    {
        var hits = await Find(query, "TM-PW-CC-2026");

        Assert.Equal((section, heading), (hits[0].Section, hits[0].Heading));
    }

    [Fact]
    public async Task The_wording_that_covers_flood_returns_its_extension_and_no_flood_exclusion()
    {
        var hits = await Find("flood inundation surface water", "TM-PW-CCF-2026", top: 3);

        Assert.Contains(hits, h => (h.Section, h.Heading) == ("Insured perils", "Flood extension"));
        Assert.DoesNotContain(hits, h => h.Section == "Exclusions");
    }

    [Fact]
    public async Task The_same_query_always_gives_the_same_ranking_and_scores()
    {
        var first = await Find("unoccupied buildings consecutive days");
        var second = await Find("unoccupied buildings consecutive days");

        Assert.Equal(first, second);
        Assert.Equal(first.OrderByDescending(h => h.Score).Select(h => h.Key), first.Select(h => h.Key));
    }

    [Fact]
    public async Task Limiting_to_one_wording_returns_only_its_clauses_and_does_not_change_a_score()
    {
        var all = await Find("flood", top: 50);
        var one = await Find("flood", "TM-PW-EN-2026", top: 50);

        Assert.All(one, h => Assert.Equal("TM-PW-EN-2026", h.WordingId));
        var inAll = all.Single(h => h.Key == one[0].Key);
        Assert.Equal(inAll.Score, one[0].Score);
        Assert.Contains(all, h => h.WordingId != "TM-PW-EN-2026");
    }

    [Fact]
    public async Task Top_limits_the_results_and_a_query_with_no_matching_term_returns_nothing()
    {
        Assert.Equal(2, (await Find("water", top: 2)).Count);
        Assert.Empty(await Find("zebra xylophone"));
        Assert.Empty(await Find("the of and"));
        Assert.Empty(await Find("flood", "TM-PW-DOES-NOT-EXIST"));
    }

    [Fact]
    public async Task Equal_scores_are_ordered_by_wording_then_clause_number()
    {
        // Every wording has the same fraud-free "Deliberate acts" text except numbering, so ties are common.
        var hits = await Find("deliberately partners directors employees consent", top: 10);

        for (var i = 1; i < hits.Count; i++)
        {
            if (hits[i - 1].Score == hits[i].Score)
            {
                Assert.True(string.CompareOrdinal(hits[i - 1].WordingId, hits[i].WordingId) <= 0);
            }
        }
    }

    [Fact]
    public async Task A_top_below_one_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Search.SearchAsync("flood", null, 0, CancellationToken.None));
    }

    [Fact]
    public void Clause_numbers_sort_as_people_read_them()
    {
        var sorted = new[] { "4.10", "4.2", "10.1", "4.1", "4" }.OrderBy(r => r, ClauseRefComparer.Instance);

        Assert.Equal(["4", "4.1", "4.2", "4.10", "10.1"], sorted);
    }

    [Fact]
    public void The_index_covers_every_clause_of_every_wording()
    {
        Assert.Equal(Corpus.Wordings.Sum(w => w.Clauses.Count), Search.ClauseCount);
    }
}
