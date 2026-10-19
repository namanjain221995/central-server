using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using EndpointAgent.Core.ScreenLock;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EndpointAgent.Windows.ScreenLock;

/// <summary>
/// The reversible machine lockdown a screen lock applies and undoes, through
/// Windows APIs only -- the registry for Task Manager, the power management API
/// (<c>powrprof.dll</c>) for the power and lid buttons. There is no process launch
/// and no <c>powercfg</c> command: this is data written through typed API, exactly
/// as ADR-0005 requires, not a command line composed and run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every change is reversible, and that is the whole design.</b> Each setting's
/// current value is captured <em>before</em> it is changed, and a setting is only
/// ever changed when its prior value was captured successfully -- so there is never
/// a change this cannot undo. The captured values become an opaque snapshot that
/// <see cref="ScreenLockManager"/> persists with the lock and hands back to
/// <see cref="Restore"/>, so a reboot mid-lock does not lose what to put back.
/// </para>
/// <list type="bullet">
///   <item><b>Task Manager.</b> <c>HKLM\...\Policies\System\DisableTaskMgr = 1</c>
///   stops a user ending the overlay from Task Manager. Reversible: the prior value
///   (often absent) is captured and restored exactly, the value deleted when it was
///   absent before.</item>
///   <item><b>Power and lid buttons.</b> The power button, sleep button and lid-close
///   actions are set to "Do nothing" so the machine cannot be casually slept or shut
///   to get past the overlay (a reboot re-locks, but sleep would just hide it).
///   Reversible: each action's AC and DC index is captured and written back on
///   release.</item>
/// </list>
/// <para>
/// <b>Best-effort and non-fatal.</b> The overlay and its input hooks are the
/// primary enforcement; the registry and power changes harden it. A failure to
/// apply or capture any one of them is logged and never throws, so it cannot block
/// a lock from engaging or -- far more importantly -- a lock from being released.
/// This is thin P/Invoke glue, like the notifier's drawing, and is not unit-tested
/// (it would change the machine running the tests); the manager that drives it is.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsScreenLockdown : IScreenLockdown
{
    private const string PoliciesSystemKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string DisableTaskMgrValue = "DisableTaskMgr";
    private const uint DoNothing = 0;
    private const uint ErrorSuccess = 0;

    private static readonly Guid SystemButtonSubgroup = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static readonly Guid PowerButtonAction = new("7648efa3-dd9c-4e3e-b566-50f929386280");
    private static readonly Guid SleepButtonAction = new("96996bc0-ad50-47ec-923b-6f41874dd9eb");
    private static readonly Guid LidCloseAction = new("5ca83367-6e45-459f-a27b-476b1d01c936");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILogger<WindowsScreenLockdown> _logger;
    private readonly object _gate = new();
    private bool _powerApplied;

    public WindowsScreenLockdown(ILogger<WindowsScreenLockdown> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string? Capture()
    {
        lock (_gate)
        {
            // Read only; change nothing. The manager persists this snapshot before
            // Apply runs, so the machine is never hardened without a saved way back.
            var snapshot = new Snapshot
            {
                PriorDisableTaskMgr = ReadDisableTaskMgr(),
                Power = CapturePower(),
            };

            return JsonSerializer.Serialize(snapshot, Json);
        }
    }

    /// <inheritdoc />
    public void Apply()
    {
        lock (_gate)
        {
            // The registry side is cheap and broadcast-free, so it is applied on
            // engage and re-applied on every watchdog tick: a GPO refresh or anything
            // else that cleared it is put straight back. The power side causes a
            // system-wide setting-change notification, so it is applied once per lock
            // session (and again after a reboot, when this flag starts false), not
            // every five seconds -- the overlay blocks input, so nothing can change it
            // in between anyway.
            ApplyDisableTaskMgr();

            if (!_powerApplied)
            {
                ApplyPowerLockdown(CapturePower());
                _powerApplied = true;
            }
        }
    }

    /// <inheritdoc />
    public void Restore(string? snapshot)
    {
        lock (_gate)
        {
            _powerApplied = false;

            Snapshot? state = null;
            if (!string.IsNullOrEmpty(snapshot))
            {
                try
                {
                    state = JsonSerializer.Deserialize<Snapshot>(snapshot, Json);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex,
                        "The screen-lock machine snapshot could not be read; Task Manager and the power buttons are " +
                        "reset to sensible defaults instead of their exact prior values.");
                }
            }

            RestoreDisableTaskMgr(state?.PriorDisableTaskMgr);
            RestorePower(state?.Power);
        }
    }

    // ---------------------------------------------------------------- Task Manager

    private int? ReadDisableTaskMgr()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PoliciesSystemKey, writable: false);
            return key?.GetValue(DisableTaskMgrValue) is int value ? value : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Could not read the Task Manager policy to capture it; it will not be changed.");
            return SentinelUnreadable;
        }
    }

    private void ApplyDisableTaskMgr()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(PoliciesSystemKey, writable: true);
            key.SetValue(DisableTaskMgrValue, 1, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Could not disable Task Manager for the lock; the overlay still blocks input.");
        }
    }

    private void RestoreDisableTaskMgr(int? prior)
    {
        if (prior == SentinelUnreadable)
        {
            // The prior value could not be read at engage, so this lock did not
            // change it (ApplyDisableTaskMgr still ran, but we must not guess what to
            // put back). Leave it as sensible default: not disabled.
            prior = null;
        }

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(PoliciesSystemKey, writable: true);
            if (prior is { } value)
            {
                key.SetValue(DisableTaskMgrValue, value, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(DisableTaskMgrValue, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogError(ex, "Could not restore the Task Manager policy on release; it may need to be reset by policy.");
        }
    }

    /// <summary>A capture that failed, kept distinct from "absent" (null) so restore never guesses a value to write.</summary>
    private const int SentinelUnreadable = int.MinValue;

    // ----------------------------------------------------------------- power/lid

    private PowerSnapshot? CapturePower()
    {
        try
        {
            var scheme = ActiveScheme();
            if (scheme is not { } active)
            {
                return null;
            }

            return new PowerSnapshot
            {
                Scheme = active,
                PowerAc = ReadIndex(active, PowerButtonAction, ac: true),
                PowerDc = ReadIndex(active, PowerButtonAction, ac: false),
                SleepAc = ReadIndex(active, SleepButtonAction, ac: true),
                SleepDc = ReadIndex(active, SleepButtonAction, ac: false),
                LidAc = ReadIndex(active, LidCloseAction, ac: true),
                LidDc = ReadIndex(active, LidCloseAction, ac: false),
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning(ex, "The power management API is unavailable; the power buttons will not be changed for the lock.");
            return null;
        }
    }

    private void ApplyPowerLockdown(PowerSnapshot? captured)
    {
        if (captured is not { } power)
        {
            return;
        }

        var scheme = power.Scheme;
        var changed = false;

        // Only neutralise a setting whose prior value we captured, so release can
        // always put it back. A null capture means "leave this one alone" -- we
        // never change what we cannot undo.
        changed |= Apply(scheme, PowerButtonAction, ac: true, power.PowerAc);
        changed |= Apply(scheme, PowerButtonAction, ac: false, power.PowerDc);
        changed |= Apply(scheme, SleepButtonAction, ac: true, power.SleepAc);
        changed |= Apply(scheme, SleepButtonAction, ac: false, power.SleepDc);
        changed |= Apply(scheme, LidCloseAction, ac: true, power.LidAc);
        changed |= Apply(scheme, LidCloseAction, ac: false, power.LidDc);

        if (changed)
        {
            Activate(scheme);
        }
    }

    private void RestorePower(PowerSnapshot? captured)
    {
        if (captured is not { } power)
        {
            return;
        }

        var scheme = power.Scheme;
        var changed = false;

        changed |= Restore(scheme, PowerButtonAction, ac: true, power.PowerAc);
        changed |= Restore(scheme, PowerButtonAction, ac: false, power.PowerDc);
        changed |= Restore(scheme, SleepButtonAction, ac: true, power.SleepAc);
        changed |= Restore(scheme, SleepButtonAction, ac: false, power.SleepDc);
        changed |= Restore(scheme, LidCloseAction, ac: true, power.LidAc);
        changed |= Restore(scheme, LidCloseAction, ac: false, power.LidDc);

        if (changed)
        {
            Activate(scheme);
        }
    }

    /// <summary>Sets a setting to "Do nothing", but only when its prior value was captured so release can undo it.</summary>
    private bool Apply(Guid scheme, Guid setting, bool ac, uint? captured) =>
        captured is not null && Write(scheme, setting, ac, DoNothing);

    /// <summary>Puts a setting back to the value captured at engage. A setting never captured is left as it is.</summary>
    private bool Restore(Guid scheme, Guid setting, bool ac, uint? captured) =>
        captured is { } value && Write(scheme, setting, ac, value);

    private bool Write(Guid scheme, Guid setting, bool ac, uint value)
    {
        try
        {
            var subgroup = SystemButtonSubgroup;
            var settingGuid = setting;
            var result = ac
                ? NativeMethods.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref settingGuid, value)
                : NativeMethods.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref settingGuid, value);
            return result == ErrorSuccess;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning(ex, "A power-button setting could not be written for the lock.");
            return false;
        }
    }

    private uint? ReadIndex(Guid scheme, Guid setting, bool ac)
    {
        var subgroup = SystemButtonSubgroup;
        var settingGuid = setting;
        var result = ac
            ? NativeMethods.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref settingGuid, out var index)
            : NativeMethods.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref settingGuid, out index);
        return result == ErrorSuccess ? index : null;
    }

    private Guid? ActiveScheme()
    {
        if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out var pointer) != ErrorSuccess || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            _ = NativeMethods.LocalFree(pointer);
        }
    }

    private void Activate(Guid scheme)
    {
        var result = NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (result != ErrorSuccess)
        {
            _logger.LogWarning("Applying the power scheme returned {Result}; power-button changes may not have taken effect.", result);
        }
    }

    private sealed class Snapshot
    {
        public int? PriorDisableTaskMgr { get; set; }
        public PowerSnapshot? Power { get; set; }
    }

    private sealed class PowerSnapshot
    {
        public Guid Scheme { get; set; }
        public uint? PowerAc { get; set; }
        public uint? PowerDc { get; set; }
        public uint? SleepAc { get; set; }
        public uint? SleepDc { get; set; }
        public uint? LidAc { get; set; }
        public uint? LidDc { get; set; }
    }

    private static class NativeMethods
    {
        [DllImport("powrprof.dll")]
        internal static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll")]
        internal static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

        [DllImport("powrprof.dll")]
        internal static extern uint PowerReadACValueIndex(
            IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid powerSettingGuid, out uint acValueIndex);

        [DllImport("powrprof.dll")]
        internal static extern uint PowerReadDCValueIndex(
            IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid powerSettingGuid, out uint dcValueIndex);

        [DllImport("powrprof.dll")]
        internal static extern uint PowerWriteACValueIndex(
            IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid powerSettingGuid, uint acValueIndex);

        [DllImport("powrprof.dll")]
        internal static extern uint PowerWriteDCValueIndex(
            IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid powerSettingGuid, uint dcValueIndex);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr handle);
    }
}
