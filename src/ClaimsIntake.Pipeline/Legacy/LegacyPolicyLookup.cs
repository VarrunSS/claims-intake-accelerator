using System.Net;
using System.Net.Http.Json;
using ClaimsIntake.Domain;

namespace ClaimsIntake.Pipeline.Legacy;

/// <summary>The lookup_policy tool: reads a policy from the legacy claims system. Read-only, never writes.</summary>
public interface IPolicyLookup
{
    /// <returns>The policy, or null when the legacy system has no policy with that number.</returns>
    Task<PolicyReference?> FindAsync(string policyNumber, CancellationToken cancellationToken);
}

/// <summary>The legacy system could not be reached or answered with an error. Retrying may help.</summary>
public sealed class LegacySystemUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reads policies over a typed <see cref="HttpClient"/>. The base address comes from configuration.</summary>
public sealed class LegacyPolicyLookup(HttpClient client) : IPolicyLookup
{
    private const int MaxNumberLength = 40;

    private sealed record PolicyDto(string Number, string InsuredName, DateOnly PeriodStart, DateOnly PeriodEnd);

    public async Task<PolicyReference?> FindAsync(string policyNumber, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyNumber);
        var number = policyNumber.Trim();
        if (number.Length > MaxNumberLength)
        {
            return null; // Not a policy number. No point asking.
        }

        try
        {
            using var response = await client.GetAsync($"policies/{Uri.EscapeDataString(number)}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            var dto = await response.Content.ReadFromJsonAsync<PolicyDto>(cancellationToken)
                ?? throw new LegacySystemUnavailableException("The legacy system returned an empty policy.");
            return new PolicyReference(dto.Number, dto.InsuredName, dto.PeriodStart, dto.PeriodEnd);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or ArgumentException)
        {
            throw new LegacySystemUnavailableException($"The legacy system policy lookup failed ({ex.GetType().Name}).", ex);
        }
    }
}
