using System.Diagnostics;

namespace ClaimsIntake.Domain.Telemetry;

/// <summary>
/// Turns a request path into its route template for spans and logs (PII-02). A policy number in <c>/policies/TM-CP-100001</c>
/// or a claim id in <c>/claims/{guid}</c> would otherwise sit in <c>url.path</c> and <c>url.full</c> of every HTTP span. Only
/// the literal words of this system's routes are kept; every other segment becomes a placeholder, so nothing a caller or a
/// document supplied can reach telemetry through a URL, even on a route added later.
/// </summary>
public static class UrlScrubber
{
    private static readonly HashSet<string> Literals = new(StringComparer.OrdinalIgnoreCase)
    {
        "claims", "history", "audit", "approve", "reject", "policies", "intake", "health", "live", "ready", "me", "openapi",
    };

    /// <summary>For example <c>/policies/TM-CP-100001</c> becomes <c>/policies/{number}</c>, and <c>/claims/{guid}/approve</c> becomes <c>/claims/{id}/approve</c>. The query string is dropped.</summary>
    public static string RouteTemplate(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        var end = path.IndexOfAny(['?', '#']);
        var segments = (end >= 0 ? path[..end] : path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>(segments.Length);
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (Guid.TryParse(segment, out _))
            {
                result.Add("{id}");
            }
            else if (Literals.Contains(segment))
            {
                result.Add(segment.ToLowerInvariant());
            }
            else if (i > 0 && segments[i - 1].Equals("policies", StringComparison.OrdinalIgnoreCase))
            {
                result.Add("{number}");
            }
            else
            {
                result.Add("{value}");
            }
        }

        return "/" + string.Join('/', result);
    }

    /// <summary>Replaces the URL attributes of an HTTP span with the route template, and removes the query.</summary>
    public static void Scrub(Activity? activity, string? scheme, string? host, int? port, string? path)
    {
        if (activity is null)
        {
            return;
        }

        var template = RouteTemplate(path);
        var authority = string.IsNullOrEmpty(host) ? "" : port is > 0 and not 80 and not 443 ? $"{host}:{port}" : host;
        activity.SetTag("url.path", template);
        activity.SetTag("url.query", null);
        activity.SetTag("url.full", string.IsNullOrEmpty(authority) ? template : $"{scheme ?? "http"}://{authority}{template}");
        activity.SetTag("http.url", null);
        activity.SetTag("http.target", null);
    }
}
