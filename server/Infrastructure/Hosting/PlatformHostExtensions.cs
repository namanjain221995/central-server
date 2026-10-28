using EndpointPlatform.Infrastructure.DependencyInjection;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace EndpointPlatform.Infrastructure.Hosting;

/// <summary>
/// Host wiring shared by the Admin API and the Agent API.
/// </summary>
/// <remarks>
/// Shared here purely to avoid two copies of identical plumbing. Nothing in this
/// file grants access to anything: authentication schemes, authorisation policies
/// and endpoint routing are configured separately by each host, which is what keeps
/// the two trust boundaries distinct.
/// </remarks>
public static class PlatformHostExtensions
{
    /// <summary>Registers logging, correlation-id tracking and problem-details responses.</summary>
    public static IServiceCollection AddPlatformHosting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<CorrelationIdAccessor>();
        services.AddScoped<ICorrelationIdAccessor>(sp => sp.GetRequiredService<CorrelationIdAccessor>());

        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                var accessor = context.HttpContext.RequestServices
                    .GetRequiredService<ICorrelationIdAccessor>();

                // Give the caller the id they need to quote in a support request,
                // without exposing any server internals.
                context.ProblemDetails.Extensions["correlationId"] = accessor.CorrelationId;
                context.ProblemDetails.Extensions.Remove("traceId");
            };
        });

        return services;
    }

    /// <summary>
    /// Configures Serilog from application configuration.
    /// </summary>
    /// <remarks>
    /// Request logging is set to Information for successful requests and Error for
    /// failed ones, and the health endpoints are dropped to Verbose so that a
    /// one-second orchestrator probe does not bury real traffic in the log.
    /// </remarks>
    public static IHostBuilder UsePlatformSerilog(this IHostBuilder hostBuilder)
    {
        ArgumentNullException.ThrowIfNull(hostBuilder);

        return hostBuilder.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithProperty("Application", context.HostingEnvironment.ApplicationName)
            .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName));
    }

    /// <summary>Inserts correlation-id tracking, security headers and request logging.</summary>
    public static IApplicationBuilder UsePlatformRequestPipeline(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();

        app.UseSerilogRequestLogging(options =>
        {
            options.GetLevel = static (httpContext, _, exception) =>
            {
                if (exception is not null || httpContext.Response.StatusCode >= 500)
                {
                    return LogEventLevel.Error;
                }

                if (httpContext.Response.StatusCode is 401 or 403)
                {
                    // Authentication and authorisation failures are security signals.
                    return LogEventLevel.Warning;
                }

                return IsHealthEndpoint(httpContext.Request.Path)
                    ? LogEventLevel.Verbose
                    : LogEventLevel.Information;
            };

            options.EnrichDiagnosticContext = static (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("ClientIp", httpContext.Connection.RemoteIpAddress?.ToString());
                // NOTE: query strings and request bodies are never enriched here.
                // They can carry enrollment tokens and credentials.
            };
        });

        return app;
    }

    /// <summary>
    /// Maps <c>/health/live</c> and <c>/health/ready</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are anonymous by design so an orchestrator can probe them, and both
    /// therefore return only check names and statuses - never a connection string,
    /// an exception message or a stack trace, which is what the default health
    /// response writer would leak.
    /// </para>
    /// <para>
    /// Marked <c>AllowAnonymous</c> explicitly, not merely left unannotated. The
    /// Admin API runs a deny-by-default fallback authorization policy, under which an
    /// unannotated endpoint requires a signed-in administrator -- and the container
    /// healthcheck and the deploy script's health wait call these with no
    /// credentials, so an unannotated probe would fail every deploy. The Agent API
    /// has no authorization middleware at all; the metadata is inert there and its
    /// behaviour is unchanged.
    /// </para>
    /// </remarks>
    public static IEndpointRouteBuilder MapPlatformHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = static registration =>
                registration.Tags.Contains(InfrastructureServiceCollectionExtensions.LivenessTag),
            ResponseWriter = WriteMinimalHealthResponse,
            AllowCachingResponses = false,
        })
            .AllowAnonymous();

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = static registration =>
                registration.Tags.Contains(InfrastructureServiceCollectionExtensions.ReadinessTag),
            ResponseWriter = WriteMinimalHealthResponse,
            AllowCachingResponses = false,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                // Degraded means "serving, but a non-critical dependency is down".
                // It must stay 200 so a load balancer does not remove the instance.
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        })
            .AllowAnonymous();

        return endpoints;
    }

    private static Task WriteMinimalHealthResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                // entry.Value.Description and .Exception are deliberately omitted:
                // a failing Npgsql check puts the connection string in its message.
            }),
        };

        return context.Response.WriteAsJsonAsync(payload);
    }

    private static bool IsHealthEndpoint(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
}
