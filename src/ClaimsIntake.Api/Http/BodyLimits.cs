using Microsoft.AspNetCore.Http.Features;

namespace ClaimsIntake.Api.Http;

/// <summary>Endpoint metadata: the largest request body this route accepts, in bytes. Routes without it get <see cref="BodyLimits.DefaultBytes"/>.</summary>
public sealed record BodyLimitMetadata(long MaxBytes);

public static class BodyLimits
{
    /// <summary>The default for every route (INP-03): approve and reject bodies are a few hundred bytes. 64 KB is generous.</summary>
    public const long DefaultBytes = 64 * 1024;

    /// <summary>Sets the body limit of one route, replacing the 64 KB default.</summary>
    public static TBuilder WithBodyLimit<TBuilder>(this TBuilder builder, long maxBytes) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new BodyLimitMetadata(maxBytes));

    /// <summary>
    /// Enforces the per-route limit (INP-03), after routing so the endpoint is known. A request that declares a longer body is
    /// refused with 413 and problem details before anything is read; one that does not declare a length is cut off by the server
    /// limit, which is set to the route's value here, when the server supports it (Kestrel does).
    /// </summary>
    public static IApplicationBuilder UseBodyLimits(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var limit = context.GetEndpoint()?.Metadata.GetMetadata<BodyLimitMetadata>()?.MaxBytes ?? DefaultBytes;

            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            {
                feature.MaxRequestBodySize = limit;
            }

            if (context.Request.ContentLength > limit)
            {
                var problems = context.RequestServices.GetRequiredService<IProblemDetailsService>();
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await problems.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status413PayloadTooLarge,
                        Title = "The request body is too large",
                        Detail = $"This route accepts at most {limit:N0} bytes.",
                    },
                });
                return;
            }

            await next(context);
        });
}
