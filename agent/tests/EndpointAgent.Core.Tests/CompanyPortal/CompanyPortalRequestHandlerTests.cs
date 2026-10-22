using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.CompanyPortal;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.CompanyPortal;

/// <summary>
/// What the service does with a portal request, proven against a fake server:
/// it relays, maps the server's answer to the portal's words, and limits each
/// caller before the server is asked.
/// </summary>
public sealed class CompanyPortalRequestHandlerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid AppId = Guid.Parse("7d2f9c1e-4b6a-4c3d-9e8f-0a1b2c3d4e5f");

    private static readonly CatalogEntry Entry = new(
        AppId, "Contoso App", "Does things", "Tools", true, "2.1.0", "Contoso", Start.AddDays(-1), "UpdateAvailable");

    private static CompanyPortalRequestHandler Build(FakeApi api, bool enrolled = true, ManualClock? clock = null) =>
        new(api, new FakeCredentialStore(enrolled), NullLogger<CompanyPortalRequestHandler>.Instance, clock ?? new ManualClock(Start));

    // ------------------------------------------------------------ enrolment

    [Fact]
    public async Task Without_a_credential_every_request_is_not_enrolled_and_the_server_is_never_asked()
    {
        var api = new FakeApi();
        var handler = Build(api, enrolled: false);

        foreach (var request in new[] { PortalRequest.ListApps, PortalRequest.IconOf(AppId), PortalRequest.InstallOf(AppId) })
        {
            var response = await handler.HandleAsync(request, "session:1", null);

            response.Error.ShouldBe(PortalError.NotEnrolled);
        }

        api.Calls.ShouldBe(0);
    }

    // ----------------------------------------------------------------- list

    [Fact]
    public async Task A_catalog_is_mapped_field_for_field()
    {
        var api = new FakeApi { Catalog = AgentApiResult<CatalogListResponse>.Success(new CatalogListResponse([Entry])) };

        var response = await Build(api).HandleAsync(PortalRequest.ListApps, "session:1", null);

        response.Ok.ShouldBeTrue();
        var app = response.Apps.ShouldNotBeNull().ShouldHaveSingleItem();
        app.Id.ShouldBe(Entry.Id);
        app.DisplayName.ShouldBe(Entry.DisplayName);
        app.Description.ShouldBe(Entry.Description);
        app.Category.ShouldBe(Entry.Category);
        app.HasIcon.ShouldBe(Entry.HasIcon);
        app.Version.ShouldBe(Entry.Version);
        app.Publisher.ShouldBe(Entry.Publisher);
        app.UpdatedAt.ShouldBe(Entry.UpdatedAt);
        app.State.ShouldBe(Entry.State);
    }

    [Theory]
    [InlineData(AgentApiStatus.Unauthorized, PortalError.NotEnrolled)]
    [InlineData(AgentApiStatus.TransientFailure, PortalError.ServerUnavailable)]
    [InlineData(AgentApiStatus.Rejected, PortalError.ServerUnavailable)]
    public async Task A_catalog_the_server_would_not_give_is_one_of_the_portals_words(AgentApiStatus status, PortalError expected)
    {
        var api = new FakeApi { Catalog = new AgentApiResult<CatalogListResponse>(null, status) };

        var response = await Build(api).HandleAsync(PortalRequest.ListApps, "session:1", null);

        response.Error.ShouldBe(expected);
        response.Apps.ShouldBeNull();
    }

    // ----------------------------------------------------------------- icon

    [Fact]
    public async Task An_icon_is_fetched_for_the_requested_app_and_returned_as_is()
    {
        var icon = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var api = new FakeApi { Icon = AgentApiResult<byte[]>.Success(icon) };

        var response = await Build(api).HandleAsync(PortalRequest.IconOf(AppId), "session:1", null);

        response.Icon.ShouldBe(icon);
        api.IconRequestedFor.ShouldBe(AppId);
    }

    [Theory]
    [InlineData(AgentApiStatus.Rejected, PortalError.BadRequest)]
    [InlineData(AgentApiStatus.Unauthorized, PortalError.NotEnrolled)]
    [InlineData(AgentApiStatus.TransientFailure, PortalError.ServerUnavailable)]
    public async Task An_icon_the_server_would_not_give_is_one_of_the_portals_words(AgentApiStatus status, PortalError expected)
    {
        // Rejected is the server's 404 -- no icon for that entry -- and is the
        // portal's own mistake, not the server being down.
        var api = new FakeApi { Icon = new AgentApiResult<byte[]>(null, status) };

        var response = await Build(api).HandleAsync(PortalRequest.IconOf(AppId), "session:1", null);

        response.Error.ShouldBe(expected);
    }

    // -------------------------------------------------------------- install

    [Theory]
    [InlineData("  CONTOSO\\alice  ", "CONTOSO\\alice")]
    [InlineData("CONTOSO\\alice", "CONTOSO\\alice")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public async Task An_install_passes_the_user_trimmed_and_blank_as_nobody(string? requestedBy, string? expected)
    {
        var api = new FakeApi { Install = AgentApiResult<CatalogInstallResponse>.Success(new CatalogInstallResponse("Queued", Guid.NewGuid())) };

        var response = await Build(api).HandleAsync(PortalRequest.InstallOf(AppId), "session:1", requestedBy);

        response.Outcome.ShouldBe("Queued");
        api.InstallRequestedFor.ShouldBe(AppId);
        api.InstallCommand.ShouldNotBeNull().RequestedBy.ShouldBe(expected);
    }

    [Fact]
    public async Task An_install_truncates_a_user_name_longer_than_the_record_allows()
    {
        var api = new FakeApi { Install = AgentApiResult<CatalogInstallResponse>.Success(new CatalogInstallResponse("Queued", null)) };
        var name = new string('u', 300);

        await Build(api).HandleAsync(PortalRequest.InstallOf(AppId), "session:1", name);

        api.InstallCommand.ShouldNotBeNull().RequestedBy.ShouldBe(name[..256]);
    }

    [Theory]
    [InlineData("Queued", "Queued")]
    [InlineData("AlreadyInstalled", "AlreadyInstalled")]
    [InlineData("AlreadyInProgress", "AlreadyInProgress")]
    [InlineData("Refused", "Refused")]
    [InlineData("Scheduled", "Refused")]
    [InlineData("queued", "Refused")]
    [InlineData("", "Refused")]
    public async Task An_outcome_this_build_does_not_know_is_shown_as_a_refusal(string fromServer, string expected)
    {
        var api = new FakeApi { Install = AgentApiResult<CatalogInstallResponse>.Success(new CatalogInstallResponse(fromServer, null)) };

        var response = await Build(api).HandleAsync(PortalRequest.InstallOf(AppId), "session:1", null);

        response.Outcome.ShouldBe(expected);
    }

    [Theory]
    [InlineData(AgentApiStatus.Unauthorized, PortalError.NotEnrolled)]
    [InlineData(AgentApiStatus.TransientFailure, PortalError.ServerUnavailable)]
    [InlineData(AgentApiStatus.Rejected, PortalError.ServerUnavailable)]
    public async Task An_install_the_server_would_not_take_is_one_of_the_portals_words(AgentApiStatus status, PortalError expected)
    {
        var api = new FakeApi { Install = new AgentApiResult<CatalogInstallResponse>(null, status) };

        var response = await Build(api).HandleAsync(PortalRequest.InstallOf(AppId), "session:1", null);

        response.Error.ShouldBe(expected);
    }

    // --------------------------------------------------------------- limits

    [Fact]
    public async Task Lists_are_admitted_up_to_the_limit_then_refused_then_admitted_again_a_minute_later()
    {
        var clock = new ManualClock(Start);
        var api = new FakeApi { Catalog = AgentApiResult<CatalogListResponse>.Success(new CatalogListResponse([])) };
        var handler = Build(api, clock: clock);

        for (var i = 0; i < CompanyPortalRequestHandler.MaxListsPerMinute; i++)
        {
            (await handler.HandleAsync(PortalRequest.ListApps, "session:1", null)).Ok.ShouldBeTrue($"list {i + 1} is within the limit");
        }

        var refused = await handler.HandleAsync(PortalRequest.ListApps, "session:1", null);
        refused.Error.ShouldBe(PortalError.TooManyRequests);
        api.Calls.ShouldBe(CompanyPortalRequestHandler.MaxListsPerMinute, "a refused request must not reach the server");

        clock.Advance(TimeSpan.FromSeconds(61));

        (await handler.HandleAsync(PortalRequest.ListApps, "session:1", null)).Ok.ShouldBeTrue("the window has slid past");
    }

    /// <summary>
    /// A first load fetches every tile at once. That must not spend the list
    /// allowance, or the refresh right after it would be refused.
    /// </summary>
    [Fact]
    public async Task Icons_have_their_own_larger_window_so_a_first_load_does_not_block_the_next_list()
    {
        var clock = new ManualClock(Start);
        var api = new FakeApi
        {
            Catalog = AgentApiResult<CatalogListResponse>.Success(new CatalogListResponse([])),
            Icon = AgentApiResult<byte[]>.Success([1]),
        };
        var handler = Build(api, clock: clock);

        for (var i = 0; i < CompanyPortalRequestHandler.MaxIconsPerMinute; i++)
        {
            (await handler.HandleAsync(PortalRequest.IconOf(AppId), "session:1", null)).Ok.ShouldBeTrue($"icon {i + 1} is within the limit");
        }

        (await handler.HandleAsync(PortalRequest.IconOf(AppId), "session:1", null)).Error.ShouldBe(PortalError.TooManyRequests);
        (await handler.HandleAsync(PortalRequest.ListApps, "session:1", null)).Ok.ShouldBeTrue("icons must not have spent the list allowance");

        // And the other way round: a caller out of lists may still fetch icons.
        for (var i = 1; i < CompanyPortalRequestHandler.MaxListsPerMinute; i++)
        {
            await handler.HandleAsync(PortalRequest.ListApps, "session:1", null);
        }

        (await handler.HandleAsync(PortalRequest.ListApps, "session:1", null)).Error.ShouldBe(PortalError.TooManyRequests);
        clock.Advance(TimeSpan.FromSeconds(61));
        (await handler.HandleAsync(PortalRequest.IconOf(AppId), "session:1", null)).Ok.ShouldBeTrue("the icon window has slid past on its own");
    }

    [Fact]
    public async Task Installs_have_their_own_smaller_limit_independent_of_the_other_windows()
    {
        var api = new FakeApi
        {
            Catalog = AgentApiResult<CatalogListResponse>.Success(new CatalogListResponse([])),
            Icon = AgentApiResult<byte[]>.Success([1]),
            Install = AgentApiResult<CatalogInstallResponse>.Success(new CatalogInstallResponse("Queued", null)),
        };
        var handler = Build(api);

        // Exhaust lists and icons first: that must not touch the install allowance.
        for (var i = 0; i < CompanyPortalRequestHandler.MaxListsPerMinute; i++)
        {
            await handler.HandleAsync(PortalRequest.ListApps, "session:1", null);
        }

        for (var i = 0; i < CompanyPortalRequestHandler.MaxIconsPerMinute; i++)
        {
            await handler.HandleAsync(PortalRequest.IconOf(AppId), "session:1", null);
        }

        (await handler.HandleAsync(PortalRequest.IconOf(AppId), "session:1", null)).Error.ShouldBe(PortalError.TooManyRequests);

        (await handler.HandleAsync(PortalRequest.ListApps, "session:1", null)).Error.ShouldBe(PortalError.TooManyRequests);

        for (var i = 0; i < CompanyPortalRequestHandler.MaxInstallsPerMinute; i++)
        {
            (await handler.HandleAsync(PortalRequest.InstallOf(AppId), "session:1", null)).Ok.ShouldBeTrue($"install {i + 1} is within the limit");
        }

        (await handler.HandleAsync(PortalRequest.InstallOf(AppId), "session:1", null)).Error.ShouldBe(PortalError.TooManyRequests);
    }

    [Theory]
    [InlineData(PortalOperation.List, CompanyPortalRequestHandler.MaxListsPerMinute)]
    [InlineData(PortalOperation.Icon, CompanyPortalRequestHandler.MaxIconsPerMinute)]
    [InlineData(PortalOperation.Install, CompanyPortalRequestHandler.MaxInstallsPerMinute)]
    public async Task Each_limit_is_counted_per_caller(PortalOperation operation, int limit)
    {
        var api = new FakeApi
        {
            Catalog = AgentApiResult<CatalogListResponse>.Success(new CatalogListResponse([])),
            Icon = AgentApiResult<byte[]>.Success([1]),
            Install = AgentApiResult<CatalogInstallResponse>.Success(new CatalogInstallResponse("Queued", null)),
        };
        var handler = Build(api);
        var request = new PortalRequest(operation, operation == PortalOperation.List ? null : AppId);

        for (var i = 0; i < limit; i++)
        {
            (await handler.HandleAsync(request, "session:1", null)).Ok.ShouldBeTrue();
        }

        (await handler.HandleAsync(request, "session:1", null)).Error.ShouldBe(PortalError.TooManyRequests);
        (await handler.HandleAsync(request, "session:2", null)).Ok.ShouldBeTrue("another session has its own allowance");
    }

    [Fact]
    public async Task A_refused_request_does_not_itself_count_against_the_caller()
    {
        var clock = new ManualClock(Start);
        var api = new FakeApi { Install = AgentApiResult<CatalogInstallResponse>.Success(new CatalogInstallResponse("Queued", null)) };
        var handler = Build(api, clock: clock);

        for (var i = 0; i < CompanyPortalRequestHandler.MaxInstallsPerMinute + 5; i++)
        {
            await handler.HandleAsync(PortalRequest.InstallOf(AppId), "session:1", null);
        }

        // The first ten stamps expire together; the five refusals left no stamps
        // behind to keep the caller locked out.
        clock.Advance(TimeSpan.FromSeconds(61));

        (await handler.HandleAsync(PortalRequest.InstallOf(AppId), "session:1", null)).Ok.ShouldBeTrue();
    }

    // ---------------------------------------------------------------- fakes

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeCredentialStore(bool enrolled) : IDeviceCredentialStore
    {
        public ValueTask<DeviceCredential?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(enrolled ? new DeviceCredential(Guid.CreateVersion7(), "key", "secret") : null);

        public ValueTask SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<bool> HasCredentialAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(enrolled);
        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// The three catalog calls, scripted; every other call throws, so a test that
    /// strays off the portal's path fails loudly instead of quietly succeeding.
    /// </summary>
    private sealed class FakeApi : IAgentApiClient
    {
        public AgentApiResult<CatalogListResponse> Catalog { get; set; } = AgentApiResult<CatalogListResponse>.Transient();
        public AgentApiResult<byte[]> Icon { get; set; } = AgentApiResult<byte[]>.Transient();
        public AgentApiResult<CatalogInstallResponse> Install { get; set; } = AgentApiResult<CatalogInstallResponse>.Transient();

        public int Calls { get; private set; }
        public Guid? IconRequestedFor { get; private set; }
        public Guid? InstallRequestedFor { get; private set; }
        public CatalogInstallCommand? InstallCommand { get; private set; }

        public Task<AgentApiResult<CatalogListResponse>> GetCatalogAsync(DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Catalog);
        }

        public Task<AgentApiResult<byte[]>> GetCatalogIconAsync(Guid appId, DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            Calls++;
            IconRequestedFor = appId;
            return Task.FromResult(Icon);
        }

        public Task<AgentApiResult<CatalogInstallResponse>> RequestCatalogInstallAsync(
            Guid appId, CatalogInstallCommand command, DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            Calls++;
            InstallRequestedFor = appId;
            InstallCommand = command;
            return Task.FromResult(Install);
        }

        public Task<AgentApiResult<EnrollResponse>> EnrollAsync(EnrollRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EnrollmentRequestResponse>> RequestEnrollmentAsync(EnrollmentRequestRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EnrollmentClaimResponse>> ClaimEnrollmentAsync(EnrollmentClaimRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<HeartbeatResponse>> HeartbeatAsync(HeartbeatRequest r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<InventoryResponse>> UploadInventoryAsync(InventoryReport r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<UsbPolicyResponse>> ReportUsbAsync(UsbReport r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentTaskListResponse>> ClaimTasksAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> PostTaskResultAsync(Guid id, AgentTaskResult r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentPolicyListResponse>> GetPoliciesAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> PostComplianceAsync(AgentPolicyComplianceReport r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<RedeemSecretResponse>> RedeemSecretAsync(string secretReference, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadPackageAsync(Guid packageId, Stream destination, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadDriverPackageAsync(Guid packageId, Stream destination, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<AgentUpdateInfo>> GetAgentUpdateInfoAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> DownloadAgentUpdateAsync(Guid releaseId, Stream destination, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<BitLockerEscrowStatusResponse>> GetBitLockerEscrowStatusAsync(DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<EscrowRecoveryKeyResponse>> EscrowRecoveryKeyAsync(EscrowRecoveryKeyRequest r, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<ChatPollResponse>> PollChatMessagesAsync(Guid sessionId, int afterSeq, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentApiResult<Unit>> SendChatMessageAsync(Guid sessionId, string body, DeviceCredential c, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
