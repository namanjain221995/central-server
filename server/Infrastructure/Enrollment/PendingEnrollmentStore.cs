using System.Security.Cryptography;
using System.Text.Json;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EndpointPlatform.Infrastructure.Enrollment;

/// <summary>
/// Holds enrollment requests between an agent asking and an administrator deciding.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="EphemeralSecretStore"/> deliberately: same Redis namespace
/// convention, same 15-minute lifetime, same seal-at-rest, same atomic single-use
/// redemption. Enrollment did not need a second storage idiom.
/// </para>
/// <para>
/// <b>The agent proves possession, it does not present a bearer token.</b> The agent
/// generates a 256-bit request secret and sends only its SHA-256 hash. The server
/// stores the hash and never learns the secret until the claim, where the secret is
/// hashed again and compared. A dump of Redis therefore yields nothing that can
/// claim a credential, and nothing sensitive is ever written to the endpoint's disk
/// by an installer or left in a Downloads folder.
/// </para>
/// </remarks>
public sealed class PendingEnrollmentStore(
    IConnectionMultiplexer redis,
    ISecretProtector protector,
    ILogger<PendingEnrollmentStore> logger)
{
    /// <summary>Dedicated key namespace, so these never collide with cache or secret entries.</summary>
    private const string KeyPrefix = "endpointplatform:enroll-request:";

    private readonly IConnectionMultiplexer _redis = redis ?? throw new ArgumentNullException(nameof(redis));
    private readonly ISecretProtector _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    private readonly ILogger<PendingEnrollmentStore> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Derives the public request id from the agent's private request secret.
    /// </summary>
    /// <remarks>
    /// SHA-256 with no salt on purpose: the input is already 256 bits of entropy, so
    /// there is nothing to brute-force and the derivation must be reproducible by the
    /// server from the secret alone at claim time.
    /// </remarks>
    public static string DeriveRequestId(string requestSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestSecret);
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestSecret));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Derives the request id for a re-enrollment of one machine into one
    /// organization.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately derived from PUBLIC identity rather than a per-request secret:
    /// a re-enrolling agent calls <c>/enroll</c> statelessly every few minutes and
    /// holds no request secret, so the only way its retries can land on the SAME
    /// pending record — and an administrator can decide once rather than per retry
    /// — is for the id to be a pure function of who is asking about what. The
    /// organization comes from the presented token, never from the caller's own
    /// claim, so a caller cannot park requests in a tenant its token does not open.
    /// </para>
    /// <para>
    /// Knowing (or guessing) this id grants nothing. Approval is read server-side
    /// by the enrollment gate, which still requires a valid token for the same
    /// organization; the admin endpoints require authentication; and the agent
    /// claim path refuses re-enrollment records outright (a claim must present a
    /// secret whose hash IS the id, and this id is not the hash of any secret).
    /// The domain prefix keeps it from ever colliding with a secret-derived id.
    /// </para>
    /// </remarks>
    public static string DeriveReEnrollRequestId(Guid organizationId, string machineIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineIdentifier);
        var hash = SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"re-enroll:v1:{organizationId:N}:{machineIdentifier}"));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Records a machine asking to be managed. Returns false when the store is
    /// unreachable, which the caller must surface as a retryable failure rather than
    /// as a rejection.
    /// </summary>
    public async Task<bool> RequestAsync(
        string requestId, PendingEnrollment request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var database = _redis.GetDatabase();

            // NOT NX: an agent that restarts mid-wait re-sends the same request id and
            // must land on its existing record rather than being told it already
            // exists. Overwriting is safe because the id is derived from a secret only
            // that agent holds - but an already-approved request must not be reset to
            // pending, or approval could be undone by a retry.
            var key = KeyPrefix + requestId;
            var existing = await database.StringGetAsync(key);
            if (!existing.IsNullOrEmpty)
            {
                var current = Deserialize(existing!);
                if (current is not null && current.Status != PendingEnrollmentStatus.Pending)
                {
                    // Already decided; leave it alone and let the agent claim or stop.
                    return true;
                }
            }

            await database.StringSetAsync(
                key,
                JsonSerializer.Serialize(request, Json),
                PendingEnrollment.Lifetime);

            return true;
        }
        catch (RedisException ex)
        {
            _logger.LogError(ex, "Could not record an enrollment request; the pending store is unreachable.");
            return false;
        }
    }

    /// <summary>All requests currently awaiting or holding a decision.</summary>
    /// <remarks>
    /// Uses SCAN rather than KEYS so a large keyspace does not block Redis. The result
    /// is a point-in-time view: entries expire on their own, so a request may vanish
    /// between listing and approving, which the approve path treats as "gone" rather
    /// than as an error. Each entry carries its remaining time to live, because
    /// lifetimes now differ by kind and by state (and are refreshed by retries), so
    /// only Redis itself knows when a given record actually dies.
    /// </remarks>
    public async Task<IReadOnlyList<(string RequestId, PendingEnrollment Request, TimeSpan? TimeToLive)>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<(string, PendingEnrollment, TimeSpan?)>();

        try
        {
            var endpoints = _redis.GetEndPoints();
            if (endpoints.Length == 0)
            {
                return results;
            }

            var server = _redis.GetServer(endpoints[0]);
            var database = _redis.GetDatabase();

            await foreach (var key in server.KeysAsync(pattern: KeyPrefix + "*", pageSize: 250)
                               .WithCancellation(cancellationToken))
            {
                var value = await database.StringGetWithExpiryAsync(key);
                if (value.Value.IsNullOrEmpty)
                {
                    continue;
                }

                var request = Deserialize(value.Value!);
                if (request is not null)
                {
                    results.Add((((string?)key)![KeyPrefix.Length..], request, value.Expiry));
                }
            }
        }
        catch (RedisException ex)
        {
            _logger.LogError(ex, "Could not list enrollment requests; the pending store is unreachable.");
        }

        return results.OrderBy(r => r.Item2.RequestedAt).ToList();
    }

    /// <summary>Reads one request, or null when it never existed or has expired.</summary>
    public async Task<PendingEnrollment?> FindAsync(
        string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        try
        {
            var value = await _redis.GetDatabase().StringGetAsync(KeyPrefix + requestId);
            return value.IsNullOrEmpty ? null : Deserialize(value!);
        }
        catch (RedisException ex)
        {
            _logger.LogError(ex, "Could not read an enrollment request; the pending store is unreachable.");
            return null;
        }
    }

    /// <summary>
    /// Records that a token-holding agent is asking to re-enroll an active device,
    /// and answers where that request stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the storage half of the re-enrollment approval gate, called on every
    /// gated <c>/enroll</c> attempt. Retries land on the same key (see
    /// <see cref="DeriveReEnrollRequestId"/>), so the method's job is to move the
    /// record's state honestly without ever moving it backwards: a Pending record is
    /// refreshed (new snapshot, new TTL) because the agent is still knocking; an
    /// Approved record is returned untouched for the caller to claim; a Rejected
    /// record is left to serve out its cooling-off untouched, so an administrator's
    /// "no" is not converted back into a question by the very retry it refused.
    /// </para>
    /// <para>
    /// <see cref="ReEnrollAttemptState.Unavailable"/> means the store could not be
    /// reached. The caller must refuse the enrollment (fail closed): "could not
    /// check for approval" and "approved" must never be confused.
    /// </para>
    /// </remarks>
    public async Task<ReEnrollAttemptOutcome> UpsertReEnrollRequestAsync(
        string requestId, PendingEnrollment candidate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.Kind != PendingEnrollmentKind.ReEnrollment)
        {
            throw new ArgumentException("Only re-enrollment records go through this method.", nameof(candidate));
        }

        try
        {
            var database = _redis.GetDatabase();
            var key = KeyPrefix + requestId;

            var existing = await database.StringGetAsync(key);
            if (!existing.IsNullOrEmpty)
            {
                var current = Deserialize(existing!);

                // A corrupt record, or (unreachably — the id is domain-prefixed) a
                // plain-enrollment record at this key, is never overwritten from an
                // anonymous call; fail closed and let it expire.
                if (current is null || current.Kind != PendingEnrollmentKind.ReEnrollment)
                {
                    _logger.LogError(
                        "Re-enrollment request {RequestId} found an unreadable or foreign record; refusing.",
                        requestId);
                    return ReEnrollAttemptOutcome.Unavailable();
                }

                switch (current.Status)
                {
                    case PendingEnrollmentStatus.Approved:
                        return ReEnrollAttemptOutcome.Approved(current);

                    case PendingEnrollmentStatus.Rejected:
                        return ReEnrollAttemptOutcome.RejectedCoolingOff(current);

                    case PendingEnrollmentStatus.Pending:
                        // Refresh the snapshot the administrator will decide on, and
                        // the TTL, but keep RequestedAt: it answers "since when has
                        // this machine been asking", not "when did it last retry".
                        var refreshed = candidate with { RequestedAt = current.RequestedAt };

                        // Conditional on what was read, so a decision landing between
                        // the read and this write is never clobbered back to Pending.
                        var transaction = database.CreateTransaction();
                        transaction.AddCondition(Condition.StringEqual(key, existing));
                        _ = transaction.StringSetAsync(
                            key, JsonSerializer.Serialize(refreshed, Json), PendingEnrollment.ReEnrollPendingLifetime);

                        if (await transaction.ExecuteAsync())
                        {
                            return ReEnrollAttemptOutcome.PendingRefreshed(refreshed);
                        }

                        // Lost a race with a decision (or another retry). Refusing
                        // this attempt is safe either way: the agent retries, and the
                        // next attempt sees the record as it now is.
                        return ReEnrollAttemptOutcome.Unavailable();
                }
            }

            // First knock (or the previous record expired): create the pending record.
            // NX so two concurrent first knocks produce one record and ONE "created"
            // answer — the caller audits only on created, and a race must not double it.
            var created = await database.StringSetAsync(
                key,
                JsonSerializer.Serialize(candidate, Json),
                PendingEnrollment.ReEnrollPendingLifetime,
                When.NotExists);

            return created
                ? ReEnrollAttemptOutcome.PendingCreated(candidate)
                : ReEnrollAttemptOutcome.Unavailable();
        }
        catch (RedisException ex)
        {
            _logger.LogError(ex, "Could not record a re-enrollment request; the pending store is unreachable.");
            return ReEnrollAttemptOutcome.Unavailable();
        }
    }

    /// <summary>
    /// Redeems an approved re-enrollment exactly once.
    /// </summary>
    /// <remarks>
    /// GETDEL, for the same reason the claim path uses it: an approval is one
    /// administrator decision and must complete at most one re-enrollment. Returns
    /// null when the record is gone or is not an approved re-enrollment — the caller
    /// refuses then, and the agent's next retry re-pends. If the enrollment this
    /// admits subsequently fails to commit, <see cref="RestoreReEnrollApprovalAsync"/>
    /// puts the approval back so the decision is not silently spent on a failure.
    /// </remarks>
    public async Task<PendingEnrollment?> ClaimReEnrollApprovalAsync(
        string requestId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        try
        {
            var database = _redis.GetDatabase();
            var claimed = await database.StringGetDeleteAsync(KeyPrefix + requestId);
            if (claimed.IsNullOrEmpty)
            {
                return null;
            }

            var record = Deserialize(claimed!);
            if (record is { Kind: PendingEnrollmentKind.ReEnrollment, Status: PendingEnrollmentStatus.Approved })
            {
                return record;
            }

            // Deleted something that was not an approved re-enrollment. Only possible
            // if the record changed between the caller's read and this claim; put it
            // back (best effort) rather than eat someone else's state.
            if (record is not null)
            {
                await database.StringSetAsync(
                    KeyPrefix + requestId,
                    JsonSerializer.Serialize(record, Json),
                    LifetimeFor(record),
                    When.NotExists);
            }

            return null;
        }
        catch (RedisException ex)
        {
            _logger.LogError(ex, "Could not claim a re-enrollment approval; the pending store is unreachable.");
            return null;
        }
    }

    /// <summary>
    /// Returns a claimed approval to the store after the enrollment it admitted
    /// failed to commit.
    /// </summary>
    /// <remarks>
    /// Best effort and NX: if the agent has already knocked again and re-pended,
    /// the newer record wins and the administrator decides again — losing an
    /// approval to a race costs one more click, while double-writing could revive
    /// a decision that was already superseded.
    /// </remarks>
    public async Task RestoreReEnrollApprovalAsync(
        string requestId, PendingEnrollment approved, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(approved);

        try
        {
            await _redis.GetDatabase().StringSetAsync(
                KeyPrefix + requestId,
                JsonSerializer.Serialize(approved, Json),
                PendingEnrollment.ReEnrollDecisionLifetime,
                When.NotExists);
        }
        catch (RedisException ex)
        {
            _logger.LogError(
                ex,
                "Could not restore a claimed re-enrollment approval; the administrator will need to approve again.");
        }
    }

    /// <summary>
    /// Records an administrator's decision. The token secret is sealed before it is
    /// written, so Redis never holds it in the clear.
    /// </summary>
    /// <returns>
    /// The updated request, or null when it had already been decided, had expired, or
    /// the store is unreachable. A null return must never be reported as success:
    /// double approval and approve-after-expiry are exactly what this prevents.
    /// </returns>
    public async Task<PendingEnrollment?> DecideAsync(
        string requestId,
        PendingEnrollmentStatus decision,
        Guid? organizationId,
        string? tokenSecret,
        string? approvedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        if (decision == PendingEnrollmentStatus.Pending)
        {
            throw new ArgumentException("A decision cannot be 'Pending'.", nameof(decision));
        }

        try
        {
            var database = _redis.GetDatabase();
            var key = KeyPrefix + requestId;

            var current = await database.StringGetAsync(key);
            if (current.IsNullOrEmpty)
            {
                return null; // expired or never existed
            }

            var request = Deserialize(current!);
            if (request is null || request.Status != PendingEnrollmentStatus.Pending)
            {
                return null; // already decided
            }

            var updated = request with
            {
                Status = decision,
                OrganizationId = organizationId,
                SealedTokenSecret = tokenSecret is null ? null : _protector.Protect(tokenSecret),
                ApprovedBy = approvedBy,
            };

            // Conditional on the value we read, so two administrators clicking Approve
            // at the same moment cannot both succeed. The decided record's lifetime
            // depends on its kind: a decided re-enrollment lives the cooling-off /
            // claim window, a decided enrollment its original 15 minutes.
            var transaction = database.CreateTransaction();
            transaction.AddCondition(Condition.StringEqual(key, current));
            _ = transaction.StringSetAsync(key, JsonSerializer.Serialize(updated, Json), LifetimeFor(updated));

            return await transaction.ExecuteAsync() ? updated : null;
        }
        catch (RedisException ex)
        {
            _logger.LogError(ex, "Could not record an enrollment decision; the pending store is unreachable.");
            return null;
        }
    }

    /// <summary>
    /// Redeems an approved request exactly once, returning the unsealed enrollment
    /// token secret for the caller to feed to the existing enrollment path.
    /// </summary>
    /// <remarks>
    /// Approved requests are removed atomically with GETDEL, so a replayed claim
    /// cannot obtain a second credential. A request that is still pending is left in
    /// place and reported as such, so the agent keeps waiting rather than losing its
    /// place in the queue.
    /// </remarks>
    public async Task<ClaimOutcome> ClaimAsync(
        string requestSecret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestSecret))
        {
            return ClaimOutcome.NotFound();
        }

        var requestId = DeriveRequestId(requestSecret);

        try
        {
            var database = _redis.GetDatabase();
            var key = KeyPrefix + requestId;

            var value = await database.StringGetAsync(key);
            if (value.IsNullOrEmpty)
            {
                return ClaimOutcome.NotFound();
            }

            var request = Deserialize(value!);
            if (request is null)
            {
                return ClaimOutcome.NotFound();
            }

            // Re-enrollment records are never claimable here. Their approval is
            // redeemed by the enrollment gate against the agent's own token; this
            // path redeems a minted token against a request SECRET, and a
            // re-enrollment id is not the hash of any secret. Unreachable in
            // practice (it would take a SHA-256 preimage), but a claim must not be
            // the thing that deletes a record an administrator just approved.
            if (request.Kind == PendingEnrollmentKind.ReEnrollment)
            {
                return ClaimOutcome.NotFound();
            }

            switch (request.Status)
            {
                case PendingEnrollmentStatus.Pending:
                    return ClaimOutcome.StillPending();

                case PendingEnrollmentStatus.Rejected:
                    // Remove it, so a rejected machine stops polling a dead request
                    // instead of retrying until the TTL runs out.
                    await database.KeyDeleteAsync(key);
                    return ClaimOutcome.Rejected();

                case PendingEnrollmentStatus.Approved:
                    // Atomic read-and-delete: two concurrent claims cannot both win.
                    var claimed = await database.StringGetDeleteAsync(key);
                    if (claimed.IsNullOrEmpty)
                    {
                        return ClaimOutcome.NotFound();
                    }

                    var approved = Deserialize(claimed!);
                    if (approved?.SealedTokenSecret is null || approved.OrganizationId is null)
                    {
                        _logger.LogError(
                            "Approved enrollment request {RequestId} carried no token; refusing to issue a credential.",
                            requestId);
                        return ClaimOutcome.NotFound();
                    }

                    try
                    {
                        return ClaimOutcome.Ready(_protector.Unprotect(approved.SealedTokenSecret), approved);
                    }
                    catch (CryptographicException ex)
                    {
                        // Almost always a SecretProtection key mismatch between hosts.
                        _logger.LogError(ex, "An approved enrollment token could not be unsealed.");
                        return ClaimOutcome.NotFound();
                    }

                default:
                    return ClaimOutcome.NotFound();
            }
        }
        catch (RedisException ex)
        {
            _logger.LogError(ex, "Could not claim an enrollment request; the pending store is unreachable.");
            return ClaimOutcome.Unavailable();
        }
    }

    /// <summary>The lifetime a record of this kind and state is (re)written with.</summary>
    private static TimeSpan LifetimeFor(PendingEnrollment record) => record switch
    {
        { Kind: PendingEnrollmentKind.ReEnrollment, Status: PendingEnrollmentStatus.Pending } =>
            PendingEnrollment.ReEnrollPendingLifetime,
        { Kind: PendingEnrollmentKind.ReEnrollment } => PendingEnrollment.ReEnrollDecisionLifetime,
        _ => PendingEnrollment.Lifetime,
    };

    private static PendingEnrollment? Deserialize(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<PendingEnrollment>(value, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Where a gated re-enrollment attempt stands after being recorded.</summary>
public enum ReEnrollAttemptState
{
    /// <summary>An administrator approved; the caller may claim and complete.</summary>
    Approved = 0,

    /// <summary>No record existed; a pending one was created. Audit this one.</summary>
    PendingCreated = 1,

    /// <summary>A pending record already existed and was refreshed. The retry drum; not audited.</summary>
    PendingRefreshed = 2,

    /// <summary>An administrator rejected recently; the refusal stands without re-pending.</summary>
    RejectedCoolingOff = 3,

    /// <summary>The store could not be reached or the record could not be moved. Fail closed.</summary>
    Unavailable = 4,
}

/// <summary>Outcome of <see cref="PendingEnrollmentStore.UpsertReEnrollRequestAsync"/>.</summary>
public sealed record ReEnrollAttemptOutcome(ReEnrollAttemptState State, PendingEnrollment? Record)
{
    public static ReEnrollAttemptOutcome Approved(PendingEnrollment record) =>
        new(ReEnrollAttemptState.Approved, record);

    public static ReEnrollAttemptOutcome PendingCreated(PendingEnrollment record) =>
        new(ReEnrollAttemptState.PendingCreated, record);

    public static ReEnrollAttemptOutcome PendingRefreshed(PendingEnrollment record) =>
        new(ReEnrollAttemptState.PendingRefreshed, record);

    public static ReEnrollAttemptOutcome RejectedCoolingOff(PendingEnrollment record) =>
        new(ReEnrollAttemptState.RejectedCoolingOff, record);

    public static ReEnrollAttemptOutcome Unavailable() => new(ReEnrollAttemptState.Unavailable, null);
}

/// <summary>What a claim attempt found.</summary>
public sealed record ClaimOutcome(
    ClaimStatus Status,
    string? EnrollmentTokenSecret = null,
    PendingEnrollment? Request = null)
{
    public static ClaimOutcome Ready(string tokenSecret, PendingEnrollment request) =>
        new(ClaimStatus.Approved, tokenSecret, request);

    public static ClaimOutcome StillPending() => new(ClaimStatus.Pending);

    public static ClaimOutcome Rejected() => new(ClaimStatus.Rejected);

    public static ClaimOutcome NotFound() => new(ClaimStatus.NotFound);

    public static ClaimOutcome Unavailable() => new(ClaimStatus.Unavailable);
}

public enum ClaimStatus
{
    /// <summary>Approved and redeemed; the caller may now issue a credential.</summary>
    Approved = 0,

    /// <summary>Still waiting on an administrator. The agent should keep polling.</summary>
    Pending = 1,

    /// <summary>Refused by an administrator. The agent must stop polling this request.</summary>
    Rejected = 2,

    /// <summary>Unknown, already claimed, or expired.</summary>
    NotFound = 3,

    /// <summary>The store is unreachable; retryable, and not a rejection.</summary>
    Unavailable = 4,
}
