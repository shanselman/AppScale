# ADR 0001 — Architecture

Status: Accepted (experimental prototype)

## Context

The goal is to scale one application independently of the monitor's DPI on Windows 11, without changing
global display settings, without elevation, and without silently modifying unrelated applications.
Windows has no supported API that assigns an arbitrary DPI to another process's window (see the
[High DPI reference](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-reference) and
[GetDpiForWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow): the value comes from
the window's awareness and its monitor).

## Decision

- **AppScale.Core** (C#, .NET 10): the backend contract `IScaleBackend` (Probe, Apply, Reset, GetStatus and
  `BackendCapabilities`: NativeRendering, LiveChange, RequiresRestart, InputSupported, VisualOnly, Experimental),
  structured `ScaleResult`/`ScaleFailureCode` failures, `ScaleMath` (absolute DPI = 96 × scale; relative
  zoom = requested DPI ÷ actual DPI), `SafetyPolicy`, `BackendSelector`, `SettingsStore`, and `ScaleController`.
- **Backends**, in the order they are selected:
  - `cooperative`: native rerendering for applications that implement the [cooperative protocol](0002-cooperative-protocol.md).
    It is verified by reading the DPI back, not by trusting an acknowledgement.
  - `inject`: the main feature. It works with ordinary per-monitor-aware applications that know nothing about
    AppScale. `AppScale.Injector.exe` loads `AppScale.Native.dll` (built with Microsoft Detours) into **the one process
    the user picked**, either when it launches or while it is running. The DLL makes that process's DPI queries
    return the requested DPI. It then sends the app its own `WM_DPICHANGED`, so the app relays itself out using the
    code it already has for moving between monitors. After that the process speaks the cooperative protocol, so apply,
    verify and reset reuse the same code ([ADR 0004](0004-injection-decision-gate.md)).
  - `visual-preview`: DWM thumbnail magnification ([ADR 0003](0003-visual-fallback.md)). It is labelled VISUAL and is
    selected only when the user explicitly allows it.
- **AppScale.Native** (C++): the DPI adapter DLL and the injector.
- **ProfileWatcher** (Core): a background watcher. When the executable of an *enabled* profile starts, it applies that
  profile through the native backends. It never applies a visual fallback automatically.
- **AppScale.Cli** (`appscale.exe`): a diagnostic and automation CLI.
- **AppScale.UI**: a WPF tray controller.
- **Harnesses** (`tests/Win32Harness`, `tests/WinFormsHarness`, `tests/WpfHarness`): owned test applications that
  implement the protocol and log DPI ground truth.

The selection rules are:

1. Run the safety checks first.
2. Then try native backends that are not experimental.
3. Use the visual backend only with explicit consent.
4. Use experimental backends only when they are requested by id and enabled by a feature flag.
5. If none of these apply, fail closed.

## Consequences

- Per-monitor-aware applications whose framework draws its own UI (WPF, Electron, WinForms client areas, and Win32
  client areas) can be natively rescaled without changing them.
- System-aware and DPI-unaware applications can't be rescaled this way, because Windows bitmap-scales them. AppScale
  says so and offers visual magnification instead.
- Some parts of classic Win32 applications are drawn by Windows itself: the caption bar, menu bar, scroll bars,
  common-control tooltips and system dialogs. These stay at the real DPI. Only the OS can fix that; see
  [platform-proposal.md](../platform-proposal.md).
- Every limit is reported as a structured failure, never as a silent success.
