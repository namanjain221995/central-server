using System.Reflection;
using System.Threading.RateLimiting;
using EndpointPlatform.Api.Endpoints;
using EndpointPlatform.Api.Security;
using EndpointPlatform.Contracts.Common;
using EndpointPlatform.Infrastructure.DependencyInjection;
using EndpointPlatform.Infrastructure.BitLocker;
using EndpointPlatform.Infrastructure.Hosting;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Serilog;

namespace EndpointPlatform.Api;

/// <summary>
/// Host for the ADMIN API: the trust boundary for authenticated human administrators.
/// </summary>
/// <remarks>
/// This host never exposes agent endpoints. Agents talk to
/// <c>EndpointPlatform.AgentApi</c>, which runs as a separate process on a separate
/// port with a separate authentication scheme. Keeping them apart means a flaw in
/// agent request handling cannot reach an administrator endpoint, and a stolen
/// device credential is useless here. See docs/adr/0001-separate-admin-and-agent-apis.md
/// </remarks>
public sealed class Program
{
    // Never instantiated. The class is non-static only because
    // WebApplicationFactory<TEntryPoint> requires a reference type argument to
    // locate the entry-point assembly for in-process integration tests.
    private Program()
    {
    }

    public static async Task<int> Main(string[] args)
    {
        // Bootstrap logger: captures failures that happen before configuration has
        // been read, which would otherwise be lost entirely.
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(args);

            // Form parsing must not cap below what the upload endpoints allow:
            // Kestrel's per-request limit (raised only on those endpoints) is the
            // outer gate, and every other endpoint keeps the 30 MB default, so
            // this global multipart ceiling opens nothing on its own.
            builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
                options.MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024);

            // ENDPOINTPLATFORM_Database__ConnectionString etc. Secrets come from the
            // environment or user-secrets, never from a committed settings file.
            builder.Configuration.AddEnvironmentVariables(prefix: "ENDPOINTPLATFORM_");

            builder.Host.UsePlatformSerilog();

            builder.Services.AddPlatformHosting();
            builder.Services.AddEndpointPlatformInfrastructure(builder.Configuration, builder.Environment);

            // Management-plane background jobs run in the Admin host only.
            builder.Services.AddHostedService<EndpointPlatform.Infrastructure.Tasks.TaskExpirySweeper>();
            builder.Services
                .AddHostedService<EndpointPlatform.Infrastructure.Peripherals.UsbGrantExpirySweeper>()
                .AddHostedService<EndpointPlatform.Infrastructure.Identity.LocalAdminElevationExpirySweeper>();

            // --- Authentication and authorization (Phase 3) -------------------
            builder.Services.AddOptions<AdminAuthOptions>()
                .Bind(builder.Configuration.GetSection(AdminAuthOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            builder.Services.AddScoped<AdminAuthService>();
            builder.Services.AddScoped<EndpointPlatform.Infrastructure.Identity.PlatformUserService>();

            // Singleton, like RevealRateLimiter: it holds only the Redis
            // multiplexer and a clock, and the counters live in Redis so nothing
            // per-request belongs here. Admin API only - the Agent API has no
            // sign-in form to protect.
            builder.Services.AddSingleton<SignInAddressThrottle>();

            // Multi-factor. Registered here rather than in shared infrastructure
            // for the same reason as the escrow key: the Agent API must never hold
            // the key that unseals a TOTP secret, because that would let the
            // endpoint-facing process derive a valid second factor for any
            // administrator. AgentApiKeyBoundaryGuard enforces it at startup.
            builder.Services.AddOptions<MfaOptions>()
                .Bind(builder.Configuration.GetSection(MfaOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            builder.Services.AddSingleton<ITotpSecretProtector, AesGcmTotpSecretProtector>();
            builder.Services.AddScoped<MfaService>();

            // Registered here rather than in the shared infrastructure, so the
            // Agent API never holds the key that decrypts recovery passwords.
            // That service is reachable by every managed endpoint; the escrow key
            // has no business being in its process. Validated on start: escrow
            // seals data at rest, so a missing or malformed key must stop this
            // API rather than surface later as passwords that cannot be read back.
            builder.Services.AddOptions<RecoveryEscrowOptions>()
                .Bind(builder.Configuration.GetSection(RecoveryEscrowOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            builder.Services.AddSingleton<IRecoveryKeyProtector, AesGcmRecoveryKeyProtector>();

            // The private half of the endpoint sealing keypair. This is the only
            // process in the platform that holds it, and the only place an
            // automatically escrowed password can be read.
            //
            // Not validated on start, unlike the master key above, and the
            // difference is deliberate. A missing master key means manual escrow
            // silently cannot work; a missing sealing key means only that hybrid
            // reveal is unavailable, which is a perfectly ordinary state for an
            // estate with no automatic escrows yet. Refusing to boot over it would
            // take the console down for a capability nothing is using.
            // Phase 3 left a gap: automatic escrow could succeed while reveal was
            // impossible, and nothing said so until a key was needed. Checked at
            // startup now -- a configured public key must have its matching private
            // half here, or this host does not start.
            AdminApiSealingKeyGuard.AssertRevealRemainsPossible(builder.Configuration);

            builder.Services.AddSingleton<IHybridEnvelopeUnsealer>(
                _ => new RsaHybridEnvelopeUnsealer(
                    builder.Configuration["RecoveryEscrow:SealingPrivateKey"]));

            builder.Services.AddSingleton<IEscrowSealingKeyProvider>(
                _ => new EscrowSealingKeyProvider(
                    builder.Configuration["RecoveryEscrow:SealingPublicKey"]));
            builder.Services.AddSingleton<RevealRateLimiter>();
            builder.Services.AddScoped<RecoveryEscrowService>();
            builder.Services.AddScoped<Infrastructure.BitLocker.EscrowAttemptAdminService>();

            builder.Services
                .AddAuthentication(AdminAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, AdminAuthenticationHandler>(
                    AdminAuthenticationHandler.SchemeName,
                    displayName: "Admin session",
                    configureOptions: null);

            builder.Services.AddAuthorization();
            builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
            builder.Services.AddSingleton<IAuthorizationHandler, PermissionRequirementHandler>();
            builder.Services.AddScoped<IAuthorizationMiddlewareResultHandler, AuditingAuthorizationResultHandler>();

            // Brute-force protection on sign-in: a small fixed window per client
            // address. In-memory - adequate for a single instance; Redis-backed
            // limiting is Phase 15 hardening.
            builder.Services.AddRateLimiter(limiter =>
            {
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, httpContext =>
                {
                    var authOptions = httpContext.RequestServices
                        .GetRequiredService<Microsoft.Extensions.Options.IOptions<AdminAuthOptions>>().Value;

                    return RateLimitPartition.GetFixedWindowLimiter(
                        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = authOptions.LoginAttemptsPerMinutePerAddress,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        });
                });
            });

            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(options =>
            {
                options.SwaggerDoc("v1", new()
                {
                    Title = "Endpoint Platform Admin API",
                    Version = "v1",
                    Description = "Administrative API for the Endpoint Management Platform. "
                                  + "Requires an authenticated administrator; not for agent use.",
                });
            });

            builder.Services.AddCors(options => options.AddPolicy(
                DashboardCorsPolicy,
                policy => policy
                    .WithOrigins(ReadDashboardOrigins(builder.Configuration))
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials()
                    .WithExposedHeaders(CorrelationId.HeaderName)));

            var app = builder.Build();

            app.UsePlatformRequestPipeline();

            // Returns RFC 7807 problem details instead of a stack trace. Malformed
            // request bodies (BadHttpRequestException) are the caller's fault and
            // surface as their own status code, not a 500.
            app.UseExceptionHandler(new ExceptionHandlerOptions
            {
                StatusCodeSelector = static ex =>
                    ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError,
            });
            app.UseStatusCodePages();

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }
            else
            {
                // Only outside Development: HSTS on localhost poisons the browser
                // cache for every other local project.
                app.UseHsts();
                app.UseHttpsRedirection();
            }

            app.UseCors(DashboardCorsPolicy);

            app.UseRateLimiter();
            app.UseMiddleware<CsrfProtectionMiddleware>();
            app.UseAuthentication();

            // Between authentication and authorization: it needs the identity, and
            // anything it blocks must be blocked before a policy can allow it.
            app.UseMiddleware<PasswordChangeRequiredMiddleware>();

            // AFTER the password gate, deliberately. A new administrator is
            // normally flagged for both, and a temporary password is the weaker
            // credential of the two - it was displayed on somebody's screen - so
            // it should be replaced before it is used to enrol a second factor.
            // The password gate's allowlist does not include the MFA routes, so
            // this ordering also means the two requirements cannot deadlock.
            app.UseMiddleware<MfaEnrolmentRequiredMiddleware>();

            app.UseAuthorization();

            app.MapPlatformHealthChecks();

            app.MapAuthEndpoints();
            app.MapPlatformUserEndpoints();
            app.MapAccessLevelEndpoints();
            app.MapEnrollmentTokenEndpoints();
            app.MapEnrollmentApprovalEndpoints();
            app.MapDeviceEndpoints();
            app.MapLocalAccountEndpoints();
            app.MapLocalAdminElevationEndpoints();
            app.MapDriverEndpoints();
            app.MapBitLockerEndpoints();
            app.MapDriverPackageEndpoints();
            app.MapBitLockerEscrowEndpoints();
            app.MapSoftwareEndpoints();
            app.MapPackageEndpoints();
            app.MapDeploymentEndpoints();
            app.MapSecurityEndpoints();
            app.MapUpdateEndpoints();
            app.MapReportEndpoints();
            app.MapPolicyEndpoints();
            app.MapGroupEndpoints();
            app.MapTaskEndpoints();
            app.MapAgentReleaseEndpoints();
            app.MapUsbEndpoints();

            app.MapGet("/", () => new ServiceInfoResponse(
                    Service: "admin-api",
                    Version: BuildVersion,
                    Environment: app.Environment.EnvironmentName))
                .WithName("GetAdminApiServiceInfo")
                .AllowAnonymous();

            Log.Information(
                "Admin API starting. Environment: {Environment}, version: {Version}.",
                app.Environment.EnvironmentName,
                BuildVersion);

            await app.RunAsync();
            return 0;
        }
        // HostAbortedException (and its pre-.NET-7 internal equivalent) is control
        // flow, not failure: WebApplicationFactory and the EF design-time tooling
        // abort the entry point this way after capturing the configured host.
        // Swallowing it would break in-process integration testing.
        catch (Exception ex) when (ex is not HostAbortedException
                                   && ex.GetType().Name != "StopTheHostException")
        {
            Log.Fatal(ex, "Admin API terminated unexpectedly during startup.");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    internal const string DashboardCorsPolicy = "dashboard";

    internal static string BuildVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>
    /// Reads the dashboard origins allowed to call this API.
    /// </summary>
    /// <remarks>
    /// An explicit allow-list, never <c>AllowAnyOrigin</c>. The Admin API is
    /// credentialed, and a wildcard origin combined with credentials is exactly the
    /// configuration that lets any website drive an administrator's session.
    /// </remarks>
    private static string[] ReadDashboardOrigins(IConfiguration configuration)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();

        if (origins is null or { Length: 0 })
        {
            throw new InvalidOperationException(
                "Cors:AllowedOrigins is not configured. The Admin API refuses to start without an "
                + "explicit dashboard origin allow-list. See docs/development.md.");
        }

        return origins;
    }
}
