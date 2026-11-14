using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using ClaimsIntake.Api.Telemetry;
using Microsoft.AspNetCore.RateLimiting;

namespace ClaimsIntake.Api.Http;

/// <summary>
/// Rate limiting (API-01). Approve and reject get a strict policy, intake a looser one, and everything else a general limit so
/// no single caller can starve the API. Limits are per signed-in caller (the token's <c>oid</c>), and per remote address for an
/// anonymous caller such as a health probe. A refused request gets 429, a Retry-After header and problem details.
/// Fixed windows, in memory: one API instance (REL-01), so one limiter. Behind several instances the limit would be per instance.
/// </summary>
public static class RateLimiting
{
    public const string Decisions = "decisions";
    public const string Intake = "intake";

    /// <summary>Settings under <c>RateLimiting</c>: <c>{Policy}:PermitLimit</c> and <c>{Policy}:WindowSeconds</c>.</summary>
    public const string SectionName = "RateLimiting";

    public static IServiceCollection AddClaimsIntakeRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var decisions = Options(configuration, Decisions, permitLimit: 20, windowSeconds: 60);
        var intake = Options(configuration, Intake, permitLimit: 120, windowSeconds: 60);
        var general = Options(configuration, "general", permitLimit: 300, windowSeconds: 60);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Decisions, context => Partition(context, Decisions, decisions));
            options.AddPolicy(Intake, context => Partition(context, Intake, intake));
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => Partition(context, "general", general));
            options.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                var policy = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "general";
                ApiMetrics.RateLimited.Add(1, new KeyValuePair<string, object?>("policy", policy));
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many requests",
                        Detail = "Slow down and try again after the time in the Retry-After header.",
                    },
                });
            };
        });
        return services;
    }

    /// <summary>The same caller always lands in the same window: the object id when signed in, otherwise the remote address.</summary>
    private static RateLimitPartition<string> Partition(HttpContext context, string policy, LimiterSettings settings)
    {
        var caller = context.User.FindFirstValue("oid") is { Length: > 0 } oid
            ? $"oid:{oid}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        return RateLimitPartition.GetFixedWindowLimiter($"{policy}|{caller}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = settings.PermitLimit,
            Window = TimeSpan.FromSeconds(settings.WindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    private static LimiterSettings Options(IConfiguration configuration, string policy, int permitLimit, int windowSeconds)
    {
        var section = configuration.GetSection($"{SectionName}:{policy}");
        return new LimiterSettings(
            section.GetValue<int?>("PermitLimit") is > 0 and var limit ? limit : permitLimit,
            section.GetValue<int?>("WindowSeconds") is > 0 and var window ? window : windowSeconds);
    }

    private sealed record LimiterSettings(int PermitLimit, int WindowSeconds);
}
