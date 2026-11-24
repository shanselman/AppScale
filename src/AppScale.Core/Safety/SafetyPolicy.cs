using AppScale.Core.Backends;
using AppScale.Core.Targets;

namespace AppScale.Core.Safety;

/// <summary>Fail-closed exclusion rules. A target that any rule rejects is never touched by any backend.</summary>
public static class SafetyPolicy
{
    /// <summary>Shell, logon, credential, security and system UI processes. Compared by file name, case-insensitive.</summary>
    public static readonly IReadOnlySet<string> ExcludedExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "lsass.exe", "winlogon.exe", "logonui.exe", "consent.exe", "credentialuibroker.exe", "csrss.exe",
        "dwm.exe", "explorer.exe", "searchhost.exe", "startmenuexperiencehost.exe", "shellexperiencehost.exe",
        "textinputhost.exe", "lockapp.exe", "securityhealthsystray.exe", "securityhealthhost.exe",
        "msmpeng.exe", "smartscreen.exe", "useraccountcontrolsettings.exe", "taskmgr.exe", "mmc.exe",
        "regedit.exe", "sihost.exe", "applicationframehost.exe", "systemsettings.exe", "windowsterminal.exe",
        "1password.exe", "keepass.exe", "keepassxc.exe", "bitwarden.exe", "dashlane.exe", "lastpass.exe",
    };

    public static ScaleFailureCode Check(TargetInfo target, out string reason)
    {
        if (target.ProcessId == Environment.ProcessId) { reason = "AppScale does not scale itself."; return ScaleFailureCode.Excluded; }
        if (target.IsElevated) { reason = "Elevated (administrator) processes are excluded."; return ScaleFailureCode.Elevated; }
        if (target.AccessDenied || target.Executable is null) { reason = "Process could not be inspected (protected, elevated or another user)."; return ScaleFailureCode.AccessDenied; }
        if (ExcludedExecutables.Contains(target.Executable.FileName)) { reason = $"{target.Executable.FileName} is a shell, security or system process."; return ScaleFailureCode.Excluded; }
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windir) && target.Executable.FullPath.StartsWith(windir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { reason = "Windows system components are excluded."; return ScaleFailureCode.Excluded; }
        reason = "";
        return ScaleFailureCode.None;
    }
}
