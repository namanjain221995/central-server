namespace EndpointPlatform.Contracts;

/// <summary>
/// Constants shared by the Agent API and the Windows agent.
/// </summary>
/// <remarks>
/// Both sides compile against this file, so a header name or route prefix cannot
/// drift between them. The wire format itself is documented in
/// <c>docs/agent-protocol.md</c>.
/// </remarks>
public static class AgentProtocol
{
    /// <summary>
    /// Version of the agent wire protocol. The agent sends it on every request and
    /// the server refuses versions it does not understand, so an agent fleet can be
    /// upgraded gradually without the server having to guess what a payload means.
    /// </summary>
    public const int Version = 1;

    /// <summary>Route prefix for every agent-facing endpoint.</summary>
    public const string RoutePrefix = "/agent/v1";

    public static class Headers
    {
        /// <summary>Carries <see cref="Version"/>.</summary>
        public const string ProtocolVersion = "X-Agent-Protocol-Version";

        /// <summary>The enrolled device's identifier.</summary>
        public const string DeviceId = "X-Agent-Device-Id";

        /// <summary>Agent build version, for fleet upgrade visibility.</summary>
        public const string AgentVersion = "X-Agent-Version";

        /// <summary>
        /// Device credential, presented as <c>keyId.secret</c>. Sent only over TLS.
        /// A dedicated header rather than <c>Authorization: Bearer</c> so that agent
        /// credentials can never be confused with (or replayed as) administrator
        /// bearer tokens by any intermediary or log pipeline.
        /// </summary>
        public const string Credential = "X-Agent-Credential";
    }

    /// <summary>Agent-facing endpoint paths, relative to <see cref="RoutePrefix"/>.</summary>
    public static class Routes
    {
        public const string Enroll = "/enroll";

        /// <summary>
        /// Anonymous. An unenrolled machine asks to be managed; an administrator
        /// decides. Grants nothing on its own.
        /// </summary>
        public const string EnrollRequest = "/enroll/request";

        /// <summary>
        /// Anonymous. The agent proves possession of its request secret and, once an
        /// administrator has approved, receives its device credential exactly once.
        /// </summary>
        public const string EnrollClaim = "/enroll/claim";
        public const string Heartbeat = "/heartbeat";
        public const string Inventory = "/inventory";

        /// <summary>
        /// Agent posts its USB device report (POST) here and receives the
        /// authoritative USB storage policy in the response. Separate from
        /// <see cref="Inventory"/> because it fires on device arrival and removal
        /// rather than on the inventory cycle.
        /// </summary>
        public const string Usb = "/usb";

        /// <summary>Agent claims queued tasks (GET) here.</summary>
        public const string Tasks = "/tasks";

        /// <summary>Agent posts a task result to {RoutePrefix}/tasks/{id}/result.</summary>
        public const string TaskResultSuffix = "/result";

        /// <summary>Agent pulls its effective policies (GET) here.</summary>
        public const string Policies = "/policies";

        /// <summary>Agent posts compliance to {RoutePrefix}/policies/compliance.</summary>
        public const string PolicyComplianceSuffix = "/compliance";

        /// <summary>
        /// Agent redeems a one-time secret reference at {RoutePrefix}/secrets/redeem.
        /// Used for local-account passwords, which never travel in a persisted task payload.
        /// </summary>
        public const string SecretRedeem = "/secrets/redeem";

        /// <summary>
        /// Automatic BitLocker recovery-password escrow.
        /// <c>/bitlocker/escrow</c> (POST) accepts an endpoint-sealed envelope;
        /// <c>/bitlocker/escrow-status</c> (GET) reports which protectors are
        /// already filed so the agent can skip collecting them.
        /// <para>
        /// The POST body carries ciphertext only. A plaintext recovery password is
        /// not accepted here and could not be stored if it were sent: the envelope
        /// validator rejects anything that is not a well-formed sealed envelope.
        /// </para>
        /// </summary>
        public const string BitLockerEscrow = "/bitlocker/escrow";

        /// <summary>Escrow status for this device, at {RoutePrefix}/bitlocker/escrow-status.</summary>
        public const string BitLockerEscrowStatus = "/bitlocker/escrow-status";

        /// <summary>Agent downloads package content from {RoutePrefix}/packages/{id}/content.</summary>
        public const string Packages = "/packages";

        /// <summary>
        /// Agent self-update: <c>/agent-update/latest</c> returns the published
        /// release the agent should be on; <c>/agent-update/{id}/content</c>
        /// streams that release's MSI. Both authenticated; the agent verifies
        /// hash and signature itself regardless.
        /// </summary>
        public const string AgentUpdate = "/agent-update";

        /// <summary>
        /// Agent downloads approved driver-package archives from
        /// {RoutePrefix}/driver-packages/{id}/content. Separate from software packages
        /// because they are different artefacts with different gates -- and because a
        /// shared route would let one id be served by whichever catalogue happened to
        /// contain it.
        /// </summary>
        public const string DriverPackages = "/driver-packages";

        /// <summary>Suffix for the package content stream.</summary>
        public const string PackageContentSuffix = "/content";

        /// <summary>
        /// The Company Portal catalog for this device: <c>/catalog</c> lists what
        /// the device may offer, <c>/catalog/{id}/icon</c> streams a tile's PNG,
        /// and <c>POST /catalog/{id}/install</c> asks for an app by id. The server
        /// decides; the device only names a published entry.
        /// </summary>
        public const string Catalog = "/catalog";

        public const string CatalogIconSuffix = "/icon";

        public const string CatalogInstallSuffix = "/install";

        /// <summary>
        /// Live IT-support chat for this device: the agent reads its own session's
        /// messages from <c>/chat/{sessionId}/messages</c> (GET) and posts an
        /// employee message there (POST). Both are scoped to the authenticated
        /// device, so a credential reaches only its own session.
        /// </summary>
        public const string Chat = "/chat";

        /// <summary>Suffix for a chat session's message collection, under <see cref="Chat"/>/{sessionId}.</summary>
        public const string ChatMessagesSuffix = "/messages";
    }
}
