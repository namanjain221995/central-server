using System.Text.Json;
using EndpointPlatform.Domain.Tasks;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Reads which update ids an <c>InstallSecurityUpdate</c> task names, for the two
/// questions asked of installs still in flight: "is this update installing right
/// now" (the per-device read) and "must install-all leave it alone" (install-all).
/// </summary>
/// <remarks>
/// The payload is server-written (<see cref="TaskPayloads.InstallSecurityUpdate"/>),
/// but it is read back from a jsonb column, so an unreadable one is treated as
/// naming nothing rather than failing the page or the bulk action.
/// </remarks>
internal static class SecurityUpdateInstallTasks
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<string> UpdateIdsOf(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        try
        {
            var payload = JsonSerializer.Deserialize<TaskPayloads.InstallSecurityUpdate>(payloadJson, JsonOptions);
            return payload?.UpdateIds?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
