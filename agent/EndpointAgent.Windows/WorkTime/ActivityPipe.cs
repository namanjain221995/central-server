using System.IO.Pipes;
using EndpointAgent.Windows.SessionNotice;

namespace EndpointAgent.Windows.WorkTime;

/// <summary>
/// The activity pipe between the in-session sampler and the service. Named and trust-
/// checked exactly like the screen-lock and session-notice pipes: the sampler only sends
/// to a server it can prove is in session 0 (the service).
/// </summary>
public static class ActivityPipe
{
    public const string Name = "EndpointPlatformAgent.Activity";

    public static SessionNoticePipe.Trust EvaluateServer(NamedPipeClientStream client) =>
        SessionNoticePipe.EvaluateServer(client);
}
