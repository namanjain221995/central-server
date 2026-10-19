using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Turns an authorized local-account management request into a typed device task.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation converges here so validation, the safety rules and the audited
/// queue step cannot be bypassed by adding an endpoint. The endpoint has already
/// checked the permission and the device scope; this layer adds what only it can
/// see: whether the requested change is safe given the device's known accounts.
/// </para>
/// <para>
/// The safety pre-check runs against the last reported inventory, which may be
/// stale — so it is an early, friendly refusal, not the guarantee. The agent
/// re-checks the same invariants against live Windows state before acting. A device
/// that has never reported inventory yields no accounts to reason about, so the
/// pre-check defers and the agent's live check decides.
/// </para>
/// <para>
/// Passwords never reach this class's persisted output: a secret is handed to the
/// ephemeral store and only its one-time reference is placed in the task payload.
/// </para>
/// </remarks>
public sealed class LocalAccountManagementService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    EphemeralSecretStore secretStore)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly EphemeralSecretStore _secretStore = secretStore;

    /// <summary>
    /// Shown when the ephemeral secret store is unreachable. The operation is refused
    /// rather than downgraded to carrying the password in the persisted task.
    /// </summary>
    private const string SecretUnavailable =
        "The secure secret store is unavailable, so the password could not be handed over safely. "
        + "No change was made; try again once it recovers.";

    /// <summary>Local users last reported by the device, projected for the safety rules.</summary>
    public async Task<IReadOnlyList<LocalAccountView>> GetKnownAccountsAsync(
        Guid deviceId, CancellationToken cancellationToken = default) =>
        await _dbContext.DeviceLocalUsers
            .AsNoTracking()
            .Where(u => u.DeviceId == deviceId)
            .Select(u => new LocalAccountView(u.Sid, u.Name, u.Enabled, u.IsLocalAdministrator))
            .ToListAsync(cancellationToken);

    /// <summary>Resolves a target by SID from the last reported inventory (null when unknown).</summary>
    public async Task<LocalAccountView?> FindAccountAsync(
        Guid deviceId, string sid, CancellationToken cancellationToken = default) =>
        await _dbContext.DeviceLocalUsers
            .AsNoTracking()
            .Where(u => u.DeviceId == deviceId && u.Sid == sid)
            .Select(u => new LocalAccountView(u.Sid, u.Name, u.Enabled, u.IsLocalAdministrator))
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Validates a create request against its profile and the group allow-list, then
    /// queues the task. Returns the resolved configuration so the caller can audit and
    /// display exactly what was asked for.
    /// </summary>
    /// <remarks>
    /// The caller has already checked <c>user.create</c>, the device scope, and — when
    /// <paramref name="administrator"/> is true — <c>user.change_type</c>. This layer
    /// adds the checks that depend on the request's content rather than the operator's
    /// identity.
    /// </remarks>
    public async Task<LocalAccountOperationResult> CreateUserAsync(
        LocalAccountRequestContext context,
        string username,
        string? fullName,
        string? description,
        string password,
        bool enabled,
        bool mustChangePassword,
        bool administrator,
        IReadOnlyList<string>? additionalGroups,
        string? profileKey,
        CancellationToken cancellationToken = default)
    {
        var invalid = ValidateUsername(username);
        if (invalid is not null)
        {
            return LocalAccountOperationResult.Rejected(invalid);
        }

        // An unknown profile is refused rather than silently ignored: the operator
        // believes a baseline was applied, and quietly dropping it would create an
        // account with settings nobody chose.
        var profile = UserConfigurationProfiles.Find(profileKey);
        if (profileKey is not null && profile is null)
        {
            return LocalAccountOperationResult.Rejected(
                $"'{profileKey}' is not a known user configuration profile.");
        }

        var groups = additionalGroups ?? [];
        foreach (var group in groups)
        {
            var groupRefusal = UserConfigurationProfiles.ValidateAdditionalGroup(group);
            if (groupRefusal is not null)
            {
                return LocalAccountOperationResult.Rejected(groupRefusal);
            }
        }

        // The plaintext goes to the ephemeral store; only the reference is persisted.
        var secretRef = await _secretStore.StoreAsync(context.DeviceId, password, cancellationToken);
        if (secretRef is null)
        {
            return LocalAccountOperationResult.Rejected(SecretUnavailable);
        }

        var payload = new TaskPayloads.CreateLocalUser(
            username.Trim(),
            fullName,
            description,
            secretRef,
            enabled,
            mustChangePassword,
            administrator,
            groups,
            profile?.Key ?? "custom");

        return await QueueAsync(context, DeviceTaskType.CreateLocalUser, payload, cancellationToken);
    }

    public async Task<LocalAccountOperationResult> DeleteUserAsync(
        LocalAccountRequestContext context, string sid, CancellationToken cancellationToken = default)
    {
        var target = await FindAccountAsync(context.DeviceId, sid, cancellationToken);
        var accounts = await GetKnownAccountsAsync(context.DeviceId, cancellationToken);

        var refusal = LocalAccountSafetyRules.ValidateDelete(sid, accounts);
        if (refusal is not null)
        {
            return LocalAccountOperationResult.Rejected(refusal);
        }

        var payload = new TaskPayloads.LocalUserTarget(sid, target?.Username ?? sid);
        return await QueueAsync(context, DeviceTaskType.DeleteLocalUser, payload, cancellationToken);
    }

    public async Task<LocalAccountOperationResult> SetUserEnabledAsync(
        LocalAccountRequestContext context, string sid, bool enabled, CancellationToken cancellationToken = default)
    {
        var target = await FindAccountAsync(context.DeviceId, sid, cancellationToken);

        if (!enabled)
        {
            var accounts = await GetKnownAccountsAsync(context.DeviceId, cancellationToken);
            var refusal = LocalAccountSafetyRules.ValidateDisable(sid, accounts);
            if (refusal is not null)
            {
                return LocalAccountOperationResult.Rejected(refusal);
            }
        }

        var payload = new TaskPayloads.SetLocalUserEnabled(sid, target?.Username ?? sid, enabled);
        var type = enabled ? DeviceTaskType.EnableLocalUser : DeviceTaskType.DisableLocalUser;
        return await QueueAsync(context, type, payload, cancellationToken);
    }

    public async Task<LocalAccountOperationResult> ResetPasswordAsync(
        LocalAccountRequestContext context, string sid, string password, CancellationToken cancellationToken = default)
    {
        var target = await FindAccountAsync(context.DeviceId, sid, cancellationToken);
        var secretRef = await _secretStore.StoreAsync(context.DeviceId, password, cancellationToken);
        if (secretRef is null)
        {
            return LocalAccountOperationResult.Rejected(SecretUnavailable);
        }

        var payload = new TaskPayloads.ResetLocalUserPassword(sid, target?.Username ?? sid, secretRef);
        return await QueueAsync(context, DeviceTaskType.ResetLocalUserPassword, payload, cancellationToken);
    }

    public async Task<LocalAccountOperationResult> ForcePasswordChangeAsync(
        LocalAccountRequestContext context, string sid, CancellationToken cancellationToken = default)
    {
        var target = await FindAccountAsync(context.DeviceId, sid, cancellationToken);
        var payload = new TaskPayloads.LocalUserTarget(sid, target?.Username ?? sid);
        return await QueueAsync(context, DeviceTaskType.ForceLocalUserPasswordChange, payload, cancellationToken);
    }

    /// <summary>Promotes or demotes by changing real BUILTIN\Administrators membership.</summary>
    public async Task<LocalAccountOperationResult> ChangeAccountTypeAsync(
        LocalAccountRequestContext context, string sid, bool administrator, CancellationToken cancellationToken = default)
    {
        var target = await FindAccountAsync(context.DeviceId, sid, cancellationToken);

        if (!administrator)
        {
            var accounts = await GetKnownAccountsAsync(context.DeviceId, cancellationToken);
            var refusal = LocalAccountSafetyRules.ValidateDemote(sid, accounts);
            if (refusal is not null)
            {
                return LocalAccountOperationResult.Rejected(refusal);
            }
        }

        var payload = new TaskPayloads.ChangeLocalUserType(sid, target?.Username ?? sid, administrator);
        return await QueueAsync(context, DeviceTaskType.ChangeLocalUserType, payload, cancellationToken);
    }

    public async Task<LocalAccountOperationResult> ChangeGroupMembershipAsync(
        LocalAccountRequestContext context,
        string groupSid,
        string memberSid,
        bool add,
        CancellationToken cancellationToken = default)
    {
        var group = await _dbContext.DeviceLocalGroups
            .AsNoTracking()
            .Where(g => g.DeviceId == context.DeviceId && g.Sid == groupSid)
            .Select(g => g.Name)
            .SingleOrDefaultAsync(cancellationToken);

        var member = await FindAccountAsync(context.DeviceId, memberSid, cancellationToken);

        // Removing from Administrators is a demotion by another name, so it gets the
        // same last-administrator protection as ChangeAccountType.
        if (!add && string.Equals(groupSid, DeviceLocalGroup.AdministratorsSid, StringComparison.OrdinalIgnoreCase))
        {
            var accounts = await GetKnownAccountsAsync(context.DeviceId, cancellationToken);
            var refusal = LocalAccountSafetyRules.ValidateDemote(memberSid, accounts);
            if (refusal is not null)
            {
                return LocalAccountOperationResult.Rejected(refusal);
            }
        }

        var payload = new TaskPayloads.LocalGroupMembership(
            groupSid, group ?? groupSid, memberSid, member?.Username ?? memberSid);

        var type = add ? DeviceTaskType.AddLocalUserToGroup : DeviceTaskType.RemoveLocalUserFromGroup;
        return await QueueAsync(context, type, payload, cancellationToken);
    }

    private async Task<LocalAccountOperationResult> QueueAsync(
        LocalAccountRequestContext context,
        DeviceTaskType type,
        object payload,
        CancellationToken cancellationToken)
    {
        var task = await _taskService.QueueAsync(
            context.OrganizationId, context.DeviceId, type, payload,
            context.ActorId, context.ActorDisplay, cancellationToken);

        return task is null
            ? LocalAccountOperationResult.DeviceNotFound()
            : LocalAccountOperationResult.Queued(task.Id);
    }

    /// <summary>
    /// Rejects names Windows will not accept, plus the reserved names that would let a
    /// created account impersonate a built-in principal.
    /// </summary>
    public static string? ValidateUsername(string username)
    {
        var trimmed = (username ?? string.Empty).Trim();

        if (trimmed.Length is 0 or > 20)
        {
            return "Username must be between 1 and 20 characters.";
        }

        // Characters Windows forbids in a SAM account name.
        const string invalidCharacters = "\"/\\[]:;|=,+*?<>@";
        if (trimmed.Any(c => invalidCharacters.Contains(c) || char.IsControl(c)))
        {
            return "Username contains characters Windows does not allow.";
        }

        if (trimmed.EndsWith('.'))
        {
            return "Username must not end with a period.";
        }

        string[] reserved =
        [
            "administrator", "guest", "system", "network service", "local service",
            "defaultaccount", "wdagutilityaccount",
        ];

        return reserved.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            ? $"'{trimmed}' is a reserved Windows account name."
            : null;
    }
}

/// <summary>Who is acting, on which device, in which organization.</summary>
public sealed record LocalAccountRequestContext(
    Guid OrganizationId, Guid DeviceId, Guid ActorId, string ActorDisplay);

public sealed record LocalAccountOperationResult(
    LocalAccountOperationStatus Status, Guid? TaskId, string? Error)
{
    public static LocalAccountOperationResult Queued(Guid taskId) =>
        new(LocalAccountOperationStatus.Queued, taskId, null);

    public static LocalAccountOperationResult Rejected(string error) =>
        new(LocalAccountOperationStatus.Rejected, null, error);

    public static LocalAccountOperationResult DeviceNotFound() =>
        new(LocalAccountOperationStatus.DeviceNotFound, null, null);
}

public enum LocalAccountOperationStatus
{
    Queued = 0,

    /// <summary>Refused by a safety rule or input validation — never reached the device.</summary>
    Rejected = 1,

    DeviceNotFound = 2,
}
