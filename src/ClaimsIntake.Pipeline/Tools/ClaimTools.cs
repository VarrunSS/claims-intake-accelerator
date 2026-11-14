using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Pipeline.Legacy;
using ClaimsIntake.Pipeline.Policy;
using Microsoft.Extensions.AI;

namespace ClaimsIntake.Pipeline.Tools;

/// <summary>The tool names the model sees. Stable, because audit entries and evals refer to them.</summary>
public static class ToolNames
{
    public const string LookupPolicy = "lookup_policy";
    public const string SearchPolicyWording = "search_policy_wording";
    public const string CheckDuplicate = "check_duplicate";
    public const string ProposeClaim = "propose_claim";
}

/// <summary>What <c>lookup_policy</c> tells the model about a policy.</summary>
public sealed record PolicyInfo(string PolicyNumber, string InsuredName, string PeriodStart, string PeriodEnd, string? WordingId, decimal? ExcessGbp);

/// <summary>An existing claim that looks like the one being assessed.</summary>
public sealed record DuplicateMatch(string Reference, string Source, string? LossDate, decimal? AmountGbp, string Status);

/// <summary>What <c>check_duplicate</c> found.</summary>
public sealed record DuplicateResult(bool IsPossibleDuplicate, IReadOnlyList<DuplicateMatch> Matches);

/// <summary>What <c>propose_claim</c> recorded. It is a proposal only.</summary>
public sealed record ClaimProposal(string PolicyNumber, string LossDate, decimal? AmountGbp, string Route, int RationaleLength);

public sealed record ProposalAck(bool Recorded, string Status, string Note);

/// <summary>
/// The tools the assessment can use, as plain C# methods with no framework in them: unit-tested directly and
/// offered to the model as <see cref="AIFunction"/>s by <see cref="AsAIFunctions"/>. One instance per assessment,
/// so each call is attributed to its claim and the clauses it returned are remembered for the citation guard.
///
/// Every tool is read-only except <c>propose_claim</c>, which records a proposal in memory and nothing else. This class
/// holds no writer for the legacy system or the review store, so it cannot write to either (ADR 0002).
/// Each call gets an Activity span (<c>tool.&lt;name&gt;</c>) and an <see cref="AuditEntry"/>. Neither holds personal data.
/// </summary>
public sealed partial class ClaimTools(
    Guid claimId,
    IPolicyLookup policies,
    IPolicyWordingSearch wording,
    IExistingClaims existingClaims,
    TimeProvider time)
{
    /// <summary>Two amounts within this fraction of the larger one count as similar.</summary>
    public const decimal AmountTolerance = 0.10m;

    public const int MaxQueryLength = 500;
    public const int MaxResults = 10;

    private readonly object _gate = new();
    private readonly List<AuditEntry> _audit = [];
    private readonly Dictionary<string, ClauseHit> _returnedClauses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DuplicateMatch> _returnedMatches = new(StringComparer.Ordinal);
    private readonly List<ClaimProposal> _proposals = [];

    public Guid ClaimId => claimId;

    /// <summary>Every call made so far, in order.</summary>
    public IReadOnlyList<AuditEntry> AuditTrail
    {
        get
        {
            lock (_gate)
            {
                return [.. _audit];
            }
        }
    }

    /// <summary>The clauses <c>search_policy_wording</c> has returned in this assessment. The only clauses a finding may cite.</summary>
    public IReadOnlyDictionary<string, ClauseHit> ReturnedClauses
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, ClauseHit>(_returnedClauses, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>The existing claims <c>check_duplicate</c> has returned. The only claims a finding may name as a duplicate.</summary>
    public IReadOnlyDictionary<string, DuplicateMatch> ReturnedMatches
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, DuplicateMatch>(_returnedMatches, StringComparer.Ordinal);
            }
        }
    }

    public IReadOnlyList<ClaimProposal> Proposals
    {
        get
        {
            lock (_gate)
            {
                return [.. _proposals];
            }
        }
    }

    [Description("Look up a policy in the legacy claims system. Returns the policy period, the excess and the id of the policy wording it is written on, or null if there is no such policy.")]
    public Task<PolicyInfo?> LookupPolicyAsync(
        [Description("The policy number, for example TM-CP-100001.")] string policyNumber,
        CancellationToken cancellationToken = default)
    {
        var valid = PolicyNumberFormat().IsMatch(policyNumber ?? "");
        return Run(ToolNames.LookupPolicy,
            new() { ["policy_number"] = valid ? policyNumber!.Trim() : Redact(policyNumber) },
            async () => valid ? await FindPolicy(policyNumber!.Trim(), cancellationToken) : null,
            p => p is null ? "not found" : $"found; wording {p.WordingId ?? "unknown"}");
    }

    [Description("Search the policy wordings for clauses that match some words, for example a cause of loss or the name of an exclusion. Returns the best matching clauses with their wording id, clause reference, heading and text. Cite clauses only as returned here.")]
    public Task<IReadOnlyList<ClauseHit>> SearchPolicyWordingAsync(
        [Description("Words to look for, for example: flood inundation surface water.")] string query,
        [Description("Limit the search to this wording id, taken from lookup_policy. Leave empty to search all wordings.")] string? wordingId = null,
        [Description("How many clauses to return, 1 to 10.")] int top = 5,
        CancellationToken cancellationToken = default)
    {
        var id = string.IsNullOrWhiteSpace(wordingId) ? null : wordingId.Trim();
        var limit = Math.Clamp(top, 1, MaxResults);
        var text = (query ?? "").Trim();
        if (text.Length > MaxQueryLength)
        {
            text = text[..MaxQueryLength];
        }

        return Run(ToolNames.SearchPolicyWording,
            new() { ["query"] = $"[text:{text.Length} chars]", ["wording_id"] = id is null ? "(all)" : WordingIds.IsValid(id) ? id : Redact(id), ["top"] = limit.ToString(CultureInfo.InvariantCulture) },
            async () =>
            {
                if (text.Length == 0)
                {
                    throw new ArgumentException("A query is required.");
                }

                var hits = await wording.SearchAsync(text, id, limit, cancellationToken);
                lock (_gate)
                {
                    foreach (var hit in hits)
                    {
                        _returnedClauses[hit.Key] = hit;
                    }
                }

                return hits;
            },
            hits => hits.Count == 0 ? "0 clauses" : $"{hits.Count} clauses: {string.Join(", ", hits.Select(h => h.Key))}");
    }

    [Description("Check whether a claim like this one already exists: the same policy and loss date and a similar amount, in the review queue or the legacy claims system. Returns the matches.")]
    public Task<DuplicateResult> CheckDuplicateAsync(
        [Description("The policy number.")] string policyNumber,
        [Description("The date of loss, as yyyy-MM-dd.")] string lossDate,
        [Description("The estimated amount in pounds, if known.")] decimal? amountGbp = null,
        CancellationToken cancellationToken = default)
    {
        var valid = PolicyNumberFormat().IsMatch(policyNumber ?? "");
        var dateOk = ClaimValidator.TryParseDate(lossDate, out var date);
        return Run(ToolNames.CheckDuplicate,
            new()
            {
                ["policy_number"] = valid ? policyNumber!.Trim() : Redact(policyNumber),
                ["loss_date"] = dateOk ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Redact(lossDate),
                ["amount_gbp"] = amountGbp?.ToString("0.##", CultureInfo.InvariantCulture) ?? "(unknown)",
            },
            async () =>
            {
                if (!valid || !dateOk)
                {
                    throw new ArgumentException("A policy number and a loss date (yyyy-MM-dd) are required.");
                }

                var existing = await existingClaims.FindByPolicyAsync(policyNumber!.Trim(), cancellationToken);
                var matches = existing
                    .Where(c => c.LossDate == date && c.Status != nameof(ClaimStatus.Rejected) && !IsThisClaim(c) && SimilarAmount(c.AmountGbp, amountGbp))
                    .Select(c => new DuplicateMatch(c.Reference, c.Source, c.LossDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), c.AmountGbp, c.Status))
                    .ToList();
                lock (_gate)
                {
                    foreach (var match in matches)
                    {
                        _returnedMatches[match.Reference] = match;
                    }
                }

                return new DuplicateResult(matches.Count > 0, matches);
            },
            r => r.IsPossibleDuplicate ? $"{r.Matches.Count} possible: {string.Join(", ", r.Matches.Select(m => m.Reference))}" : "none");
    }

    [Description("Record the proposed route for this claim. This only records a proposal for a human handler: it does not create, change or submit any claim in any system.")]
    public Task<ProposalAck> ProposeClaimAsync(
        [Description("The policy number.")] string policyNumber,
        [Description("The date of loss, as yyyy-MM-dd.")] string lossDate,
        [Description("The estimated amount in pounds, if known.")] decimal? amountGbp,
        [Description("The proposed route: straight_to_handler or senior_review.")] string route,
        [Description("One or two sentences saying why.")] string rationale,
        CancellationToken cancellationToken = default)
    {
        var valid = PolicyNumberFormat().IsMatch(policyNumber ?? "");
        var routeOk = route is "straight_to_handler" or "senior_review";
        return Run(ToolNames.ProposeClaim,
            new()
            {
                ["policy_number"] = valid ? policyNumber!.Trim() : Redact(policyNumber),
                ["loss_date"] = ClaimValidator.TryParseDate(lossDate, out var d) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Redact(lossDate),
                ["amount_gbp"] = amountGbp?.ToString("0.##", CultureInfo.InvariantCulture) ?? "(unknown)",
                ["route"] = routeOk ? route : Redact(route),
                ["rationale"] = $"[text:{(rationale ?? "").Length} chars]",
            },
            () =>
            {
                if (!valid || !routeOk)
                {
                    throw new ArgumentException("A valid policy number and route are required.");
                }

                lock (_gate)
                {
                    _proposals.Add(new ClaimProposal(policyNumber!.Trim(), lossDate, amountGbp, route, (rationale ?? "").Length));
                }

                return Task.FromResult(new ProposalAck(true, "proposed_for_human_review",
                    "Recorded. Nothing has been written to the claims system. A handler must approve the claim first."));
            },
            ack => ack.Status);
    }

    /// <summary>The tools as <see cref="AIFunction"/>s, for an agent. The names and descriptions are what the model sees.</summary>
    public IReadOnlyList<AIFunction> AsAIFunctions() =>
    [
        AIFunctionFactory.Create(LookupPolicyAsync, ToolNames.LookupPolicy),
        AIFunctionFactory.Create(SearchPolicyWordingAsync, ToolNames.SearchPolicyWording),
        AIFunctionFactory.Create(CheckDuplicateAsync, ToolNames.CheckDuplicate),
        AIFunctionFactory.Create(ProposeClaimAsync, ToolNames.ProposeClaim),
    ];

    private async Task<PolicyInfo?> FindPolicy(string number, CancellationToken cancellationToken)
    {
        var policy = await policies.FindAsync(number, cancellationToken);
        return policy is null
            ? null
            : new PolicyInfo(
                policy.Number, policy.InsuredName,
                policy.PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                policy.PeriodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                policy.WordingId, policy.ExcessGbp);
    }

    private bool IsThisClaim(ExistingClaim c) => c.Source == "review" && c.Reference == claimId.ToString();

    private static bool SimilarAmount(decimal? existing, decimal? proposed)
    {
        if (existing is null || proposed is null)
        {
            return true; // Nothing to compare: the policy and date still match, and a person will look.
        }

        var larger = Math.Max(existing.Value, proposed.Value);
        return larger == 0 || Math.Abs(existing.Value - proposed.Value) <= larger * AmountTolerance;
    }

    /// <summary>A value that is not shaped like what the tool expects is recorded by length only: a model can pass any text as an argument.</summary>
    private static string Redact(string? value) => $"[invalid:{(value ?? "").Length} chars]";

    private async Task<T> Run<T>(string tool, Dictionary<string, string> arguments, Func<Task<T>> work, Func<T, string> summarise)
    {
        var started = time.GetUtcNow();
        var clock = Stopwatch.GetTimestamp();
        using var activity = PipelineTelemetry.Source.StartActivity($"tool.{tool}");
        activity?.SetTag("tool", tool);
        activity?.SetTag("claimId", claimId.ToString());

        string? summary = null;
        var succeeded = true;
        try
        {
            var result = await work();
            summary = summarise(result);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            succeeded = false;
            summary = ex.GetType().Name; // The type only: messages can quote claim text.
            activity?.SetStatus(ActivityStatusCode.Error, summary);
            activity?.SetTag("error.type", summary);
            throw;
        }
        finally
        {
            var duration = Math.Round(Stopwatch.GetElapsedTime(clock).TotalMilliseconds, 1);
            activity?.SetTag(PipelineTelemetry.DurationMs, duration);
            activity?.SetTag("succeeded", succeeded);
            if (summary is not null)
            {
                lock (_gate)
                {
                    _audit.Add(new AuditEntry(claimId, tool, arguments, summary, duration, started, succeeded));
                }
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,40}$")]
    private static partial Regex PolicyNumberFormat();
}
