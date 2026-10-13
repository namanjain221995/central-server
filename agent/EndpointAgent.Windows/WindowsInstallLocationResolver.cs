using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using EndpointAgent.Core.Inventory;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Recovers an application's install directory when its uninstall key does not
/// record one.
/// </summary>
/// <remarks>
/// <para>
/// Most uninstall entries omit <c>InstallLocation</c> -- 22 of 36 on the machine
/// this was measured on -- and without it an application cannot be linked to its
/// running processes, so Force Stop is unavailable for it. This recovers the
/// directory from evidence Windows already holds, and only from evidence: no
/// name-to-executable guessing, ever.
/// </para>
/// <para>
/// Two sources, in order of strength:
/// </para>
/// <list type="number">
/// <item>
/// <b>Windows Installer components.</b> For an MSI product, Windows Installer
/// records the path of every file it installed. Taking the common ancestor of
/// those paths gives the install directory as an authoritative fact rather than
/// an inference. This is the strongest evidence available.
/// </item>
/// <item>
/// <b>DisplayIcon.</b> Written by the installer and usually the application's own
/// executable, so its directory is the install directory. Usable far less often
/// than it looks: measured across 22 applications, only 6 had one and 4 of those
/// pointed at the cached <em>installer</em> (<c>VC_redist.x64.exe</c>,
/// <c>python-3.14.7-amd64.exe</c>) rather than the application. Package-cache
/// paths are therefore rejected outright -- resolving Python to its own installer
/// would be worse than leaving it unavailable.
/// </item>
/// </list>
/// <para>
/// Everything produced here is still subject to the matcher's rules on the way
/// out: broad roots, directory boundaries, system pids and the agent's own
/// directory. This widens the evidence, not the permissions.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
/// <param name="agentDirectory">
/// The agent's own install directory, which must never be reported as any
/// application's location. Injected rather than read from
/// <see cref="AppContext.BaseDirectory"/> at the point of use so the guard can be
/// tested: under a test host that property is the test output folder, and a guard
/// that only works in production is a guard nobody has checked.
/// </param>
/// <param name="componentBudget">
/// How long one collection may spend indexing components; defaults to
/// <see cref="DefaultComponentBudget"/>. Tests that compare two collections raise
/// it: on a machine where indexing outlasts the budget, each collection stops at
/// a different component and resolves a different set of locations.
/// </param>
public sealed class WindowsInstallLocationResolver(
    ILogger<WindowsInstallLocationResolver> logger,
    string? agentDirectory = null,
    TimeSpan? componentBudget = null)
{
    private readonly ILogger<WindowsInstallLocationResolver> _logger = logger;
    private readonly string _agentDirectory = (agentDirectory ?? AppContext.BaseDirectory).TrimEnd('\\');

    /// <summary>
    /// Component enumeration walks every component on the machine -- around
    /// 30,000 on a developer workstation. Bounded so inventory can never hang on
    /// an unusual machine; exceeding it means resolution is skipped, not that
    /// inventory fails.
    /// </summary>
    internal static readonly TimeSpan DefaultComponentBudget = TimeSpan.FromSeconds(20);

    private readonly TimeSpan _componentBudget = componentBudget ?? DefaultComponentBudget;

    /// <summary>INSTALLSTATE_LOCAL — the component's key file is on this machine.</summary>
    private const int InstallStateLocal = 3;

    /// <summary>
    /// Paths under these are installer caches, not applications. A DisplayIcon
    /// pointing here names the thing that did the installing.
    /// </summary>
    private static readonly string[] CacheMarkers =
    [
        @"\Package Cache\",
        @"\Windows\Installer\",
        @"\Downloads\",
        @"\Temp\",
    ];

    /// <summary>
    /// The component index for the collection currently in progress.
    /// </summary>
    /// <remarks>
    /// Held across one collection because walking every component on the machine
    /// costs seconds and there are dozens of products to resolve; discarded
    /// between collections because this resolver is a singleton and the index
    /// would otherwise be as old as the service. An application installed after
    /// the agent started would then never resolve until someone restarted it,
    /// which is exactly the kind of staleness Force Stop is built to avoid.
    /// </remarks>
    private Dictionary<string, List<string>>? _componentPathsByProduct;

    /// <summary>
    /// Discards the cached index so the next resolve re-reads the machine.
    /// Called at the start of each collection.
    /// </summary>
    internal void BeginCollection() => _componentPathsByProduct = null;

    /// <summary>
    /// How many times the machine has been walked. Lets a test tell a rebuilt
    /// index from a reused one, which is the whole point of the reset.
    /// </summary>
    internal int IndexBuildCount { get; private set; }

    /// <summary>
    /// The install directory for one application, or null when nothing reliable
    /// can be established.
    /// </summary>
    public string? Resolve(string? productCode, string? displayIcon)
    {
        // Strongest first: what Windows Installer says it actually installed.
        var resolved = string.IsNullOrWhiteSpace(productCode)
            ? null
            : FromInstallerComponents(productCode);

        resolved ??= FromDisplayIcon(displayIcon);

        return Accept(resolved);
    }

    /// <summary>
    /// Filters a candidate directory down to what may actually be reported.
    /// </summary>
    /// <remarks>
    /// Applied to whatever any mechanism produced rather than inside one of them,
    /// so a future third source cannot bypass it.
    /// </remarks>
    internal string? Accept(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || IsAgentsOwnDirectory(candidate))
        {
            return null;
        }

        // Report only what the matcher would act on. A product whose files share
        // just "C:\Program Files" resolves to that bare root, which the matcher
        // refuses -- storing it would put a location in inventory that reads as
        // resolved but can never be acted on, indistinguishable to an operator
        // from a Force Stop that failed. Asking the matcher rather than
        // repeating its list keeps the two from drifting apart.
        return ApplicationProcessMatcher.CanResolve(candidate) ? candidate : null;
    }

    /// <summary>
    /// Whether a resolved directory is the agent's own.
    /// </summary>
    /// <remarks>
    /// The agent is an installed application like any other and resolves like one
    /// -- measured, its product code gives
    /// <c>C:\Program Files\EndpointPlatform\Agent</c>. The matcher already refuses
    /// to terminate anything there, but leaving the location reported would have
    /// the console offer a Force Stop button that always fails. Withholding it
    /// keeps what the operator is offered and what the endpoint will do in
    /// agreement, which matters more than the one row of coverage.
    /// </remarks>
    private bool IsAgentsOwnDirectory(string? resolved)
    {
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return false;
        }

        var self = _agentDirectory;
        var candidate = resolved.TrimEnd('\\');

        return self.StartsWith(candidate + "\\", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(self + "\\", StringComparison.OrdinalIgnoreCase)
            || string.Equals(self, candidate, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The directory the DisplayIcon executable lives in.
    /// </summary>
    /// <remarks>
    /// The value is <c>path,index</c> or a bare path. Only a real, existing
    /// executable outside an installer cache is accepted: anything else is a
    /// pointer to something that is not the application.
    /// </remarks>
    internal string? FromDisplayIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var value = displayIcon.Trim().Trim('"');

        // Strip a trailing icon index: "C:\app\a.exe,0" and "...,-101".
        var comma = value.LastIndexOf(',');
        if (comma > 2 && int.TryParse(value[(comma + 1)..], out _))
        {
            value = value[..comma];
        }

        value = value.Trim().Trim('"');

        if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // An .ico or a DLL says nothing about which process is the application.
            return null;
        }

        if (CacheMarkers.Any(m => value.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        try
        {
            if (!File.Exists(value))
            {
                // A stale pointer to something uninstalled or moved.
                return null;
            }

            return Path.GetDirectoryName(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The common ancestor of the files Windows Installer recorded for a product.
    /// </summary>
    /// <remarks>
    /// The component list is built once per collection and reused: going
    /// product-by-product would re-walk every component on the machine for each
    /// one. Registry components (<c>NN:\SOFTWARE\...</c>) are dropped -- they are
    /// keys, not files.
    /// </remarks>
    private string? FromInstallerComponents(string productCode)
    {
        var index = ComponentIndex();
        if (index is null || !index.TryGetValue(productCode, out var paths) || paths.Count == 0)
        {
            return null;
        }

        var common = CommonDirectory(paths);

        // Null when the recorded files share fewer than two path segments -- they
        // spread across a drive root rather than sitting in one install directory.
        // The matcher would refuse such a root anyway, but returning null reports
        // "unresolved" rather than "resolved to something unusable".
        return common;
    }

    private Dictionary<string, List<string>>? ComponentIndex()
    {
        if (_componentPathsByProduct is not null)
        {
            return _componentPathsByProduct;
        }

        IndexBuildCount++;

        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var component = new StringBuilder(40);
            for (var i = 0u; ; i++)
            {
                if (stopwatch.Elapsed > _componentBudget)
                {
                    _logger.LogInformation(
                        "Install-location resolution stopped after {Elapsed}; {Count} product(s) indexed.",
                        stopwatch.Elapsed, index.Count);
                    break;
                }

                component.Clear();
                component.EnsureCapacity(40);
                var status = NativeMethods.MsiEnumComponents(i, component);
                if (status != 0)
                {
                    break;
                }

                var componentId = component.ToString();

                // Every client, not just the first: a component shared between
                // products would otherwise be attributed to whichever happened to
                // be enumerated first, and the other product would resolve to
                // nothing.
                for (var c = 0u; ; c++)
                {
                    var product = new StringBuilder(40);
                    product.EnsureCapacity(40);
                    if (NativeMethods.MsiEnumClients(componentId, c, product) != 0)
                    {
                        break;
                    }

                    var productCode = product.ToString();

                    var path = new StringBuilder(1024);
                    var length = 1024u;
                    var state = NativeMethods.MsiGetComponentPath(productCode, componentId, path, ref length);

                    // INSTALLSTATE_LOCAL = 3: the file is installed on this
                    // machine. 4 is SOURCE (run-from-source), which is not a local
                    // path worth matching a process against.
                    if (state != InstallStateLocal)
                    {
                        continue;
                    }

                    var value = path.ToString();
                    if (value.Length < 4 || value[1] != ':')
                    {
                        // "22:\SOFTWARE\..." is a registry component, not a file.
                        continue;
                    }

                    if (!index.TryGetValue(productCode, out var list))
                    {
                        list = [];
                        index[productCode] = list;
                    }

                    list.Add(value);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning(ex, "Windows Installer component enumeration is unavailable.");
            _componentPathsByProduct = new Dictionary<string, List<string>>();
            return _componentPathsByProduct;
        }

        _logger.LogDebug(
            "Indexed install paths for {Count} product(s) in {Elapsed}.", index.Count, stopwatch.Elapsed);

        _componentPathsByProduct = index;
        return index;
    }

    /// <summary>The deepest directory containing every one of these files.</summary>
    internal static string? CommonDirectory(IReadOnlyList<string> paths)
    {
        string? common = null;

        foreach (var path in paths)
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            if (common is null)
            {
                common = dir;
                continue;
            }

            common = CommonPrefix(common, dir);
            if (common is null)
            {
                return null;
            }
        }

        return common;
    }

    /// <summary>
    /// The shared leading path of two directories, on segment boundaries.
    /// </summary>
    /// <remarks>
    /// Compared segment by segment rather than character by character, so
    /// <c>...\Contoso</c> and <c>...\ContosoExtra</c> share their parent, not a
    /// spurious "...\Contoso".
    /// </remarks>
    private static string? CommonPrefix(string left, string right)
    {
        var a = left.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var b = right.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        var shared = new List<string>();
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            shared.Add(a[i]);
        }

        // A drive letter alone is not an install directory.
        return shared.Count < 2 ? null : string.Join('\\', shared);
    }

    private static class NativeMethods
    {
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint MsiEnumComponentsW(uint index, StringBuilder componentCode);

        internal static uint MsiEnumComponents(uint index, StringBuilder componentCode) =>
            MsiEnumComponentsW(index, componentCode);

        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint MsiEnumClientsW(
            string componentCode, uint index, StringBuilder productCode);

        internal static uint MsiEnumClients(string componentCode, uint index, StringBuilder productCode) =>
            MsiEnumClientsW(componentCode, index, productCode);

        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int MsiGetComponentPathW(
            string productCode, string componentCode, StringBuilder pathBuffer, ref uint pathBufferSize);

        internal static int MsiGetComponentPath(
            string productCode, string componentCode, StringBuilder pathBuffer, ref uint pathBufferSize) =>
            MsiGetComponentPathW(productCode, componentCode, pathBuffer, ref pathBufferSize);
    }
}
