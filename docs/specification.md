# AppScale — engineering specification (original brief, verbatim)

> This is the original specification the project was started from. Status of each milestone is tracked in [test-report.md](test-report.md) and [compatibility.md](compatibility.md).

Objective
Build an opt-in Windows 11 utility that scales one application independently of the physical monitor. Prefer native rerendering, with a clearly labeled visual magnification fallback. This is experimental: do not assume Windows exposes a supported arbitrary per-window DPI setter.

Requirements
Select an executable or window, choose 75–250% absolute scale, preview, apply, reset, save per-executable profiles. Distinguish absolute DPI (96 × scale) from relative visual zoom (requested DPI / actual DPI). Never change global display settings. No elevation, persistence in target processes, telemetry, or automatic modification of unrelated applications. Exclude protected, elevated and security-sensitive applications. Require explicit opt-in and provide a safe restart/reset.

Architecture
C# .NET desktop controller (WPF or WinUI 3), native C++ experimental adapter, separate test harnesses. Repository: src/AppScale.UI, src/AppScale.Core, src/AppScale.Native, tests/Win32Harness, tests/WinFormsHarness, tests/WpfHarness, docs/compatibility.md. Build x64 first; add ARM64 and x86 after validation. Define IScaleBackend with Probe, Apply, Reset, GetStatus and capabilities including native rendering, live change, input support. Return structured failures rather than claiming success. Profiles stored locally by executable identity; disabled by default.

Milestone 0 — ground truth
Create a per-monitor-v2-aware Win32 harness with native controls, menu, modal dialog, tooltip, GDI drawing, and diagnostics. Log GetDpiForWindow, GetDpiForSystem, GetDeviceCaps(LOGPIXELSX/Y), awareness context, monitor, window/client rectangles, font metrics, WM_DPICHANGED. Compare actual monitor transitions at 100%, 150%, 200%. Record screenshots and physical dimensions. Establish baseline for text sharpness and input accuracy. Do not confuse awareness with scale.

Milestone 1 — cooperative DPI experiment
Implement an injectable DPI provider inside the owned test harness. Start with fixed override at launch. Use MulDiv from original logical dimensions, recreate fonts and lay out controls. Evaluate GetSystemMetricsForDpi, AdjustWindowRectExForDpi, SystemParametersInfoForDpi. Simulate WM_DPICHANGED only inside the test process using a valid RECT; compare with genuine OS notification. A synthetic message does not change OS DPI state. Do not send a local pointer to another process. Test scale changes repeatedly to catch cumulative rounding and layout drift.

Milestone 2 — native API virtualization research
Use Microsoft Detours in explicitly opted-in test executables to evaluate interception of GetDpiForWindow, GetDpiForSystem, GetDpiForMonitor, GetDeviceCaps DPI queries and related metrics. Begin with one API, instrument call sites, and add only APIs demonstrated necessary. Preserve original calls for unrelated windows and non-DPI metrics. Avoid recursion, preserve error behavior and ABI, and distinguish actual OS DPI from effective application DPI. Keep the adapter process-local and initialize before framework layout where feasible. Do not globally hook processes or bypass security controls. Test native and framework-owned non-client UI separately. If the application caches DPI, require restart rather than promising live scaling. Treat internal framework and OS state divergence as a possible fundamental blocker. Do not ship a generic interception backend until compatibility and consent are established.

Milestone 3 — framework-specific adapters
Test raw Win32, WinForms, WPF, Qt, Electron, WinUI 3, MFC separately. Prefer supported application zoom or configuration mechanisms when available. Evaluate whether native layout, text, icons, menus, popups, child HWNDs and accessibility all follow the requested scale. Maintain a compatibility matrix with OS build, application version, backend, startup/live support, rendering and input results. Do not extrapolate from one framework.

Milestone 4 — visual fallback
Prototype Windows Graphics Capture with a separate preview window and DirectComposition or a suitable GPU renderer. Investigate DWM thumbnails as an alternative. Start read-only; clearly label visual magnification and do not imply native rendering. Only consider interactive input forwarding after proving coordinate transforms, focus, clipping, multi-monitor geometry, keyboard navigation, IME and accessibility. Do not forward input to privileged windows or capture protected content. Handle capture denial, minimized windows and device loss gracefully. A virtual monitor via IddCx is a separate research branch, not an MVP dependency: evaluate driver installation, monitor DPI configuration, window placement, capture and input complexity before committing.

Milestone 5 — UX
Tray utility with window picker, executable identity, actual monitor DPI, requested scale, effective backend and restart-required indicator. Preview before applying. Provide reset and emergency disable. Never silently switch from native to bitmap scaling. Warn when compatibility is unknown. Store settings atomically and allow export/delete. No admin requirement for MVP.

Validation and release gates
Test 75%, 100%, 125%, 150%, 200%, 250%; mixed-DPI monitors; moving between monitors; maximize/restore; dialogs, menus, tooltips, drag/drop, keyboard, IME, touch, accessibility/UI Automation; multiple windows and instances; application restart, crash, controller exit and monitor disconnect. Compare against real DPI transitions. Verify that unrelated processes and global display settings remain unchanged. Require accurate hit testing and no clipping of critical controls before interactive release. Measure CPU/GPU overhead and latency against baseline; report measurements, not invented targets. Fail closed on unsupported applications. Provide automated unit tests for coordinate conversion, profile serialization and backend selection; integration tests with owned harnesses; manual test checklist and screenshots.

Decision gates and falsification
Native virtualization is viable only if application layout and OS-owned geometry remain coherent. If frameworks bypass hooks or native non-client rendering and input cannot be reconciled, narrow support to cooperative adapters or visual preview. Do not keep adding hooks indefinitely. Virtual monitor architecture may be rejected if input/focus fidelity or deployment cost is unacceptable. Accessibility users could be harmed by incorrect hit testing or hidden controls; do not advertise as an accessibility solution until validated. Restart/reset must restore normal behavior. Avoid collecting application content or credentials.

Deliverables
Working harness, diagnostic CLI, controller prototype, compatibility matrix, reproducible build instructions, test report, architecture decision records, and a short demo. First deliver a baseline and cooperative proof of concept; then report findings before expanding scope. Keep experimental code behind feature flags.

Longer-term Windows platform proposal
Investigate an OS-owned effective DPI override per window or application, with explicit consent, monitor-relative versus absolute semantics, coherent coordinate virtualization, non-client rendering, child window behavior, accessibility and DPI notifications. Hypothetical SetWindowDpiOverride/ClearWindowDpiOverride APIs are proposals, not existing APIs.

Primary references for agent verification
https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-reference
https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged
https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow
https://github.com/microsoft/Detours
https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture
https://learn.microsoft.com/en-us/windows-hardware/drivers/display/indirect-display-driver-model-overview

Verification status: these are reference starting points; API coverage, cross-framework effectiveness and virtual-monitor feasibility remain unverified experimentally. The agent must check current documentation and record evidence before making implementation claims. All milestones are proposed work, not completed tests.
