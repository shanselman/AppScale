# ADR 0004 — Injected DPI virtualization: the decision gate

Status: Accepted for x64 per-monitor-aware targets. Rejected for system-aware and unaware targets.

## Context

The goal is to point at any application and have it rerender natively at a chosen DPI. Applications already know how
to change DPI: every per-monitor-v2 application handles `WM_DPICHANGED` when it is dragged between monitors. What the
application lacks is a reason to believe its DPI has changed. Windows has no supported API for this
([GetDpiForWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow) reports the
monitor's DPI, and [WM_DPICHANGED](https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged) is sent only by
the OS). So AppScale lies to the application, but only inside the process the user picked, using
[Microsoft Detours](https://github.com/microsoft/Detours).

## Decision

`AppScale.Native.dll` is loaded either by `AppScale.Injector.exe --launch` (`DetourCreateProcessWithDllExW`, so hooks
are installed before the framework caches DPI) or by `--inject <pid>` (`LoadLibraryW` in a remote thread). Once loaded:

1. It hooks `GetDpiForWindow` (only for this process's windows), `GetDpiForSystem`, `GetSystemDpiForProcess`,
   `GetDpiForMonitor` (effective DPI), `GetDeviceCaps(LOGPIXELSX/Y)`, `GetSystemMetrics`, and the DPI-dependent
   `SystemParametersInfoW` metrics. Every hook calls the original trampoline, preserves error behavior, and passes
   through when no override is active or the calling thread isn't per-monitor aware.
2. It speaks cooperative protocol v1 on the process's behalf ([ADR 0002](0002-cooperative-protocol.md)).
3. On `SetDpi`, it sends each top-level window a `WM_DPICHANGED` with a valid suggested RECT, inside the process. The
   application then relays itself out through its own code. On reset, it sends the real DPI again.

Safety rules, enforced in both the injector and Core:

- Only same-user, non-elevated, non-protected, non-AppContainer x64 processes.
- No shell, security, credential, or %WINDIR% applications.
- No global hooks and no system-wide injection.
- Nothing persists: the DLL leaves when the process exits.
- Every target needs explicit consent, either an Apply on a picked window or an *enabled* profile.

## Evidence

See [compatibility.md](../compatibility.md) and `docs/evidence/milestone2-*.md`.

- **Works natively:**
  - VS Code (Electron) and ScreenToGif (WPF), including their custom title bars.
  - The stock WinForms and WPF harnesses.
  - Client areas of Win32 applications.
- **Does not work:**
  - System-aware applications such as 7-Zip. Their DPI comes from OS DLLs and Windows bitmap-scales them.
  - DPI-unaware applications such as Audacity. AppScale refuses them with `NoBackend`.
  - VLC (Qt 5, per-monitor v1): no visible change when injected live; launch-time injection was inconclusive.
- **Falsification finding:**
  - OS-drawn non-client UI stays at the real DPI no matter which hooks are added: the Win32 caption, menu bar, scroll
    bars, common-control tooltips, and WinForms context menus.
  - Following the specification's rule ("do not keep adding hooks indefinitely"), we stop here. A coherent fix needs
    the OS; see [platform-proposal.md](../platform-proposal.md).

## Consequences

- `inject` is a first-class backend, but compatibility stays per application and is shown as **unknown** until tested.
- Hit-testing, IME, touch and UI Automation after injection have **not** been validated. AppScale must not be presented
  as an accessibility solution.
