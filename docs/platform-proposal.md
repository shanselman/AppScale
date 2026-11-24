# Platform proposal: an OS-owned per-window effective DPI override

Status: **Proposal only.** None of the APIs below exist in Windows. They are hypothetical and are
written down to show what an application-independent solution would need from the platform.

## Why the platform has to do this

AppScale's experiments (see [compatibility.md](compatibility.md) and [ADR 0004](adr/0004-detours-decision-gate.md))
point to a structural problem. An application's idea of its DPI can be changed: through cooperation, or through
interception inside the process. But the OS keeps its own state, which the application does not control:

- non-client area metrics and caption rendering
- `WM_DPICHANGED` suggested rectangles
- hit-testing and coordinate virtualization
- child-window DPI
- UI Automation bounding rectangles

Nothing outside the OS can keep these consistent with the application's state.

## Sketch

```c
// HYPOTHETICAL — not a real API.
HRESULT SetWindowDpiOverride(HWND hwnd, UINT dpi, DPI_OVERRIDE_FLAGS flags);   // absolute, or monitor-relative
HRESULT ClearWindowDpiOverride(HWND hwnd);
HRESULT GetWindowDpiOverride(HWND hwnd, UINT* dpi, DPI_OVERRIDE_FLAGS* flags);
```

Required semantics:

- **Consent.** The call must come from the user (through Settings or a broker), not from arbitrary processes. Elevated,
  protected, and secure-desktop windows are excluded.
- **Semantics.** Callers must be able to choose between *absolute* DPI (96 × scale) and *monitor-relative* zoom. The choice
  must be visible, and must survive moving the window to another monitor.
- **Coherence.** `GetDpiForWindow`, `GetSystemMetricsForDpi`, the non-client area, `AdjustWindowRectExForDpi` inputs,
  `WM_DPICHANGED` (with a valid suggested RECT), and `WM_GETDPISCALEDSIZE` must all agree. Child HWNDs follow according
  to their DPI hosting behavior.
- **Input and accessibility.** Hit-testing, `WM_NCHITTEST`, and UI Automation bounding rectangles must stay accurate.
- **Reset.** The override is cleared on process exit and with a single "reset all" action. It is never persisted inside the target.

## Open questions

- How should the OS handle applications that cache DPI at startup? Should it require a restart, or provide a capability flag?
- How should this interact with DPI awareness levels? System-aware applications would need bitmap stretching instead.
- Should applications be able to declare that they don't support an override?
