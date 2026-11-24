# ADR 0002 — Cooperative DPI protocol v1

Status: Accepted (experimental)

## Context

Windows has no supported API that sets an arbitrary DPI for another application's
window. `SetThreadDpiAwarenessContext` and friends change *awareness*, not *scale*.
The only fully coherent way to get native re-rendering at a non-monitor scale is for
the application itself to lay out at a different DPI. AppScale therefore starts with a
**cooperative** contract that owned/opted-in applications implement.

## Decision

An application opts in by implementing this protocol. AppScale never modifies
applications that do not advertise it.

### Units

* Scale is **absolute**: requested DPI = `round(96 × percent / 100)`. 75–250 % → 72–240 DPI.
* *Relative visual zoom* = requested DPI ÷ the window's actual OS DPI. It is only used to
  describe visual (bitmap) magnification and is never sent to applications.

### Startup (restart required)

Environment variable `APPSCALE_DPI=<72..240>` set by the controller when it launches the
process. The application lays out at that effective DPI from its first window. Invalid or
missing values mean "follow the OS".

### Discovery

Each cooperative top-level window sets the window property
`AppScale.Cooperative.v1` (`SetPropW`, value `(HANDLE)1`). No property → not cooperative.

### Live change

`RegisterWindowMessageW(L"AppScale.SetDpi.v1")`, sent with `SendMessageTimeoutW`
(`SMTO_ABORTIFHUNG`, 2000 ms):

* `wParam` = requested absolute DPI (72..240), or `0` to reset to the OS DPI.
* `lParam` = 0. **No pointers cross the process boundary.**
* Return `1` if applied, `0` if rejected (out of range, unsupported).

### Status query

`RegisterWindowMessageW(L"AppScale.GetDpi.v1")`, `wParam = lParam = 0`.
Returns `MAKELONG(effectiveDpi, osDpi)`; `effectiveDpi == osDpi` when no override is active.

### Behaviour on OS DPI change

While an override is active the application keeps the **absolute** requested DPI when it
moves between monitors (it still honours the suggested RECT position from `WM_DPICHANGED`
but sizes for the effective DPI). Reset returns to normal per-monitor-v2 behaviour.

## Consequences

* Applications can implement the protocol themselves, as our harnesses do. Otherwise
  AppScale.Native.dll implements it on behalf of an ordinary application after injection
  ([ADR 0004](0004-injection-decision-gate.md)). The controller does not need to know
  which case it is dealing with.
* An override applies to the whole process (every top-level window), not to a single HWND.
* Stock applications return 0 to both registered messages, so `CooperativeBackend` treats
  them as non-cooperative.
* Synthetic DPI changes do not change OS state: `GetDpiForWindow` still reports the
  monitor DPI, non-client areas (caption, OS menus, scroll bars) still render at OS DPI.
  This divergence is recorded in `docs/compatibility.md`.
* UIPI blocks lower-integrity senders from elevated targets; AppScale excludes elevated
  targets anyway.
