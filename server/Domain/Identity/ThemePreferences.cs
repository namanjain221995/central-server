namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// How an administrator wants the console to look: light, dark, or following
/// their operating system.
/// </summary>
/// <remarks>
/// Stored as these lower-case words rather than as an enum's name, because the
/// console sends and receives them as-is and a future value must not make an
/// older server refuse to load the account row (the strict enum read is what
/// made rolling back agent 1.16.0's server need a data fix).
/// </remarks>
public static class ThemePreferences
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    /// <summary>The longest value; the column is sized from it.</summary>
    public const int MaxLength = 8;

    public static readonly IReadOnlyList<string> All = [System, Light, Dark];

    public static bool IsValid(string? value) => value is System or Light or Dark;
}
