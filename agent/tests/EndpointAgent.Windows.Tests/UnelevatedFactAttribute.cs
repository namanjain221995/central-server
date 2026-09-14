using System.Security.Principal;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// A test that can only prove anything when the run is <em>not</em> elevated.
/// </summary>
/// <remarks>
/// The mirror of <see cref="ElevatedFactAttribute"/>. Some paths take a different
/// route for an elevated caller -- an administrator's UAC-split token, say -- and
/// need a privilege only the service holds to complete it. Under elevation such a
/// test would fail for a reason that has nothing to do with the code, so it is
/// reported as SKIPPED with the reason, not passed and not failed.
/// </remarks>
public sealed class UnelevatedFactAttribute : FactAttribute
{
    public UnelevatedFactAttribute()
    {
        if (IsElevated())
        {
            Skip = "Requires an unelevated test run; the elevated route needs a privilege only the service holds.";
        }
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
