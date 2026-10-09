namespace EndpointPlatform.Contracts.Agent;

/// <summary>
/// Request body for <c>POST /agent/v1/usb</c>: every USB device the endpoint can
/// currently see, plus what the agent is actually enforcing on each.
/// </summary>
/// <remarks>
/// <para>
/// Whole-state, like the inventory upload: the list is everything present right
/// now, and a device the server knows about but that is absent here has been
/// unplugged. That keeps the agent from having to remember what it has already
/// told the server, which is the kind of state that goes wrong across a service
/// restart.
/// </para>
/// <para>
/// Sent on connect and disconnect rather than only on the inventory cycle, so
/// "a stick was plugged into that laptop" is visible in seconds rather than up
/// to a quarter of an hour later.
/// </para>
/// </remarks>
/// <param name="Devices">Everything currently attached.</param>
/// <param name="CollectedAt">When the endpoint enumerated. Server clock still wins for ordering.</param>
public sealed record UsbReport(
    IReadOnlyList<UsbDeviceReport> Devices,
    DateTimeOffset CollectedAt);

/// <summary>
/// One USB device as the endpoint sees it.
/// </summary>
/// <remarks>
/// Every classification and policy field crosses the wire as a <see cref="string"/>,
/// never as a bare enum. An enum serialised by ordinal on one side and read by
/// name on the other is a real failure this codebase has already shipped once;
/// strings make the contract legible in a packet capture and let the server
/// treat an unrecognised value as unknown rather than as whatever member
/// happens to sit at that number.
/// </remarks>
/// <param name="InstanceId">
/// Windows device instance ID, e.g. <c>USB\VID_0781&amp;PID_5581\ABC123</c>. The
/// identity everything else keys off.
/// </param>
/// <param name="DeviceClass">
/// One of <c>Storage</c>, <c>PortableDevice</c>, <c>Keyboard</c>, <c>Mouse</c>,
/// <c>NetworkAdapter</c>, <c>Hub</c>, <c>Other</c>. Anything else is stored as
/// <c>Unknown</c>. <c>PortableDevice</c> — a phone, tablet or camera reached
/// through MTP/PTP — is sent by agents from 1.16.0; an older server stores it
/// as <c>Unknown</c>, which is visible but not grantable, the safe direction.
/// </param>
/// <param name="SerialNumber">
/// The device's serial when Windows exposes one, otherwise null. Never
/// synthesised: a made-up serial would make two different sticks look like one
/// approved device.
/// </param>
/// <param name="EnforcedPolicy">
/// What the agent currently has applied: <c>Restricted</c>, <c>ReadOnly</c>,
/// <c>Enabled</c>, or null when it has not established a state (a device policy
/// does not apply to, or one it has not managed to act on yet).
/// </param>
/// <param name="EnforcementError">
/// Why enforcement did not take effect, if it did not. Reported rather than
/// swallowed so the console can show a device as unenforced instead of quietly
/// implying a control that is not in place.
/// </param>
/// <param name="EnforcementStatus">
/// How far the agent got, from 1.16.0: <c>Verified</c> (Windows reports the
/// device in the enforced state and no storage or portable-device interface is
/// exposed), <c>Unverified</c> (applied, but the state could not be read back),
/// <c>RequiresRestart</c> (Windows accepted the change but applies it only after
/// a restart) or <c>Failed</c>. Null from older agents, which the server treats
/// as applied-but-unverified; a server that predates the field ignores it.
/// </param>
public sealed record UsbDeviceReport(
    string InstanceId,
    string DeviceClass,
    string? VendorId,
    string? ProductId,
    string? SerialNumber,
    string? Manufacturer,
    string? Product,
    string? HardwareIds,
    bool IsConnected,
    string? EnforcedPolicy,
    string? EnforcementError,
    string? EnforcementStatus = null);

/// <summary>
/// Response to a USB report: the authoritative policy for this endpoint.
/// </summary>
/// <remarks>
/// The report doubles as a sync point. An agent that misses an
/// <c>ApplyUsbPolicy</c> task — because it was offline when the grant was
/// issued, or the task expired — still converges the moment the user plugs
/// something in, without an administrator having to notice and re-issue.
/// Both channels carry the same whole-state policy and both fail to the same
/// safe default, so there is one rule to reason about, not two.
/// </remarks>
/// <param name="Grants">Every live grant. Any restrictable device not named here is restricted.</param>
/// <param name="IssuedAt">When the server built this policy, for last-writer-wins on the agent.</param>
public sealed record UsbPolicyResponse(
    IReadOnlyList<UsbPolicyGrant> Grants,
    DateTimeOffset IssuedAt);

/// <param name="InstanceId">The exact device this grant covers.</param>
/// <param name="Policy"><c>ReadOnly</c> or <c>Enabled</c>. No other value grants anything.</param>
/// <param name="ExpiresAt">Absolute UTC deadline, enforced by the agent against its own clock.</param>
public sealed record UsbPolicyGrant(
    string InstanceId,
    string Policy,
    DateTimeOffset ExpiresAt);
