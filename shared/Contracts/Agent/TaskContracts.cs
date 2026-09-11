namespace EndpointPlatform.Contracts.Agent;

/// <summary>One task handed to an agent for execution.</summary>
/// <param name="TaskId">Server task identity; echoed back with the result.</param>
/// <param name="Type">Task type name (matches the server's DeviceTaskType).</param>
/// <param name="PayloadJson">Typed payload document, or null for payload-free tasks.</param>
/// <param name="ExpiresAt">
/// The server's deadline for the task, in UTC, or null from a server that
/// predates the field. The server never hands out a task past this -- an
/// expired task is marked Expired at claim time -- so this exists for the
/// agent's own belt-and-braces check on the destructive executors: a restart
/// that arrives after its deadline (a long stall between claim and execution,
/// or a badly skewed clock) is refused rather than carried out late. Optional
/// and last so an agent that does not read it still deserialises the record.
/// </param>
public sealed record AgentTask(Guid TaskId, string Type, string? PayloadJson, DateTimeOffset? ExpiresAt = null);

/// <summary>Response to the agent's task poll: zero or more tasks to run now.</summary>
public sealed record AgentTaskListResponse(IReadOnlyList<AgentTask> Tasks);

/// <summary>Result the agent posts back after running a task.</summary>
/// <param name="Succeeded">Whether the operation completed successfully.</param>
/// <param name="Message">Short human-readable outcome or failure reason (no secrets).</param>
/// <param name="ResultJson">Optional structured result document (no secrets).</param>
public sealed record AgentTaskResult(bool Succeeded, string? Message, string? ResultJson);
