using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.CompanyPortal;

/// <summary>
/// Answers a Company Portal request by asking the management server. It never
/// decides anything itself and never touches the machine.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of what a signed-in user can make the LocalSystem service
/// do: relay one of three questions, about this device only, to the server the
/// device is enrolled with. An install that the server agrees to arrives later
/// as an ordinary <c>InstallPackage</c> task and goes through every gate that
/// executor already has; nothing here installs, downloads or runs anything.
/// </para>
/// <para>
/// Per-caller limits are applied before the server is asked, so a looping or
/// hostile process in a user's session cannot turn the service into a way to
/// hammer the server. The caller key is whatever the transport can vouch for
/// (the session id, for the pipe); the user name is for the record only.
/// </para>
/// </remarks>
public sealed class CompanyPortalRequestHandler(
    IAgentApiClient apiClient,
    IDeviceCredentialStore credentialStore,
    ILogger<CompanyPortalRequestHandler> logger,
    TimeProvider? timeProvider = null) : IPortalRequestHandler
{
    /// <summary>List requests a caller may make in a minute. A portal refreshes a few times.</summary>
    internal const int MaxListsPerMinute = 120;

    /// <summary>
    /// Icon requests a caller may make in a minute. Separate from lists so a
    /// first load, which fetches every tile at once, does not spend the refresh
    /// allowance; the portal caches icons, so only that first load is heavy.
    /// </summary>
    internal const int MaxIconsPerMinute = 600;

    /// <summary>Install requests a caller may make in a minute. The server applies its own limit on top.</summary>
    internal const int MaxInstallsPerMinute = 10;

    private static readonly HashSet<string> KnownOutcomes =
        ["Queued", "AlreadyInstalled", "AlreadyInProgress", "Refused"];

    private readonly IAgentApiClient _apiClient = apiClient;
    private readonly IDeviceCredentialStore _credentialStore = credentialStore;
    private readonly ILogger<CompanyPortalRequestHandler> _logger = logger;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Window> _windows = [];

    /// <inheritdoc />
    public async Task<PortalResponse> HandleAsync(
        PortalRequest request, string callerKey, string? requestedBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callerKey);

        if (!Admit(callerKey, request.Operation))
        {
            return PortalResponse.Failed(PortalError.TooManyRequests);
        }

        var credential = await _credentialStore.LoadAsync(cancellationToken);
        if (credential is null)
        {
            return PortalResponse.Failed(PortalError.NotEnrolled);
        }

        switch (request.Operation)
        {
            case PortalOperation.List:
            {
                var result = await _apiClient.GetCatalogAsync(credential, cancellationToken);
                if (!result.IsSuccess || result.Value is null)
                {
                    return PortalResponse.Failed(ErrorFor(result.Status));
                }

                return new PortalResponse(Apps: result.Value.Apps
                    .Select(a => new PortalApp(
                        a.Id, a.DisplayName, a.Description, a.Category, a.HasIcon, a.Version, a.Publisher,
                        a.UpdatedAt, a.State))
                    .ToList());
            }

            case PortalOperation.Icon:
            {
                var result = await _apiClient.GetCatalogIconAsync(request.AppId!.Value, credential, cancellationToken);
                if (!result.IsSuccess || result.Value is null)
                {
                    // Rejected is the server's 404: no icon for an entry this
                    // device may see. Not a server fault, and nothing to retry.
                    return PortalResponse.Failed(
                        result.Status == AgentApiStatus.Rejected ? PortalError.BadRequest : ErrorFor(result.Status));
                }

                return new PortalResponse(Icon: result.Value);
            }

            case PortalOperation.Install:
            {
                var who = requestedBy?.Trim() is { Length: > 0 } name ? Truncate(name, 256) : null;
                var result = await _apiClient.RequestCatalogInstallAsync(
                    request.AppId!.Value, new CatalogInstallCommand(who), credential, cancellationToken);
                if (!result.IsSuccess || result.Value is null)
                {
                    return PortalResponse.Failed(ErrorFor(result.Status));
                }

                // An outcome this build does not know is shown as a refusal, not
                // as whatever text the server sent.
                var outcome = KnownOutcomes.Contains(result.Value.Outcome) ? result.Value.Outcome : "Refused";

                _logger.LogInformation(
                    "Company Portal install request for catalog app {AppId} by {User}: {Outcome}.",
                    request.AppId, who ?? "(unknown user)", outcome);

                return new PortalResponse(Outcome: outcome);
            }

            default:
                return PortalResponse.Failed(PortalError.BadRequest);
        }
    }

    private static PortalError ErrorFor(AgentApiStatus status) =>
        status == AgentApiStatus.Unauthorized ? PortalError.NotEnrolled : PortalError.ServerUnavailable;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>Sliding one-minute window per caller and per kind of request.</summary>
    private bool Admit(string callerKey, PortalOperation operation)
    {
        var now = _time.GetUtcNow();
        var (kind, limit) = operation switch
        {
            PortalOperation.Install => ("install", MaxInstallsPerMinute),
            PortalOperation.Icon => ("icon", MaxIconsPerMinute),
            _ => ("list", MaxListsPerMinute),
        };
        var key = callerKey + "|" + kind;

        lock (_gate)
        {
            if (!_windows.TryGetValue(key, out var window))
            {
                window = new Window();
                _windows[key] = window;
            }

            window.Stamps.RemoveAll(t => now - t >= TimeSpan.FromMinutes(1));
            if (window.Stamps.Count >= limit)
            {
                return false;
            }

            window.Stamps.Add(now);

            // Callers come and go with sessions; forget the ones that have gone quiet.
            if (_windows.Count > 64)
            {
                foreach (var stale in _windows.Where(w => w.Value.Stamps.Count == 0).Select(w => w.Key).ToList())
                {
                    _windows.Remove(stale);
                }
            }

            return true;
        }
    }

    private sealed class Window
    {
        public List<DateTimeOffset> Stamps { get; } = [];
    }
}
