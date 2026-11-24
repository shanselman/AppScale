# Milestone 2b — injection backend (AppScale.Native.dll + AppScale.Injector.exe)

Status: **measured, EXPERIMENTAL**. This is the scope change from Scott: point AppScale at an arbitrary app,
forcibly change the DPI it sees, and use the app's own DPI-change path.

All numbers below were measured on this machine. Raw logs: `artifacts\evidence\native\inject-*.jsonl`.
Screenshots: `docs\evidence\screenshots\{win32,vscode,screentogif,7zfm}-*.png`.

Environment:
- Windows 11 build 26340 x64. The OS/system DPI is 144.
- Every target ran on DISPLAY1, 3840×2560 at 144 DPI (150 %). The full monitor list is in
  [milestone0-baseline.md](milestone0-baseline.md).

## Architecture

```
Core CooperativeBackend ──(protocol v1: SetDpi/GetDpi registered messages)──► target top-level HWND
          ▲                                                                   │ subclassed in-process by
          │                                                                   ▼
AppScale.Injector.exe ──(DetourCreateProcessWithDllExW | CreateRemoteThread+LoadLibraryW)──► AppScale.Native.dll
                                                                              (Detours hooks + agent thread)
```

**`AppScale.Native.dll` in injected mode** (`InjectedAgent.cpp`):
- **Hooks.** Detours hooks are installed process-wide, with mask 0x7F by default:
  - `GetDpiForWindow`, for HWNDs owned by this process only;
  - `GetDpiForSystem` and `GetSystemDpiForProcess`;
  - `GetDpiForMonitor`, `MDT_EFFECTIVE_DPI` only;
  - `GetDeviceCaps`, `LOGPIXELSX/Y` on screen or window DCs only;
  - `GetSystemMetrics` → `GetSystemMetricsForDpi(effective)`;
  - `SystemParametersInfoW(SPI_GETNONCLIENTMETRICS | SPI_GETICONMETRICS | SPI_GETICONTITLELOGFONT)` →
    `SystemParametersInfoForDpi(effective)`.
- **Pass-through rules.** With no override active, every call goes straight through. The trampolines plus a
  thread-local re-entrancy guard prevent recursion, and error results are preserved.
  - **System-module callers** (System32, SysWOW64, WinSxS) pass through by default. `--include-system-callers`
    turns this off.
  - **Threads that are not per-monitor aware** always pass through (`passthroughNotPerMonitor`). For
    system-aware or unaware UI, DWM bitmap-scales the window, so lying about DPI would desynchronise
    geometry. This is a deliberate safety gate.
- **DPI source.** The startup DPI comes from the named section `Local\AppScale.Dpi.<pid>`, written by the
  injector.
- **Agent thread.** It enumerates the process's top-level windows every 250 ms (`EnumWindows`, filtered to
  this pid). For each new one it:
  1. sets `SetPropW("AppScale.Cooperative.v1")`;
  2. subclasses it (`SetWindowLongPtrW(GWLP_WNDPROC)`) to answer `AppScale.SetDpi.v1` /
     `AppScale.GetDpi.v1` exactly as in [ADR 0002](../adr/0002-cooperative-protocol.md).
- **On SetDpi/reset.** The agent updates the effective DPI, then sends `WM_DPICHANGED` to every tagged
  top-level window. That message has `wParam = MAKEWPARAM(dpi,dpi)` and a suggested RECT equal to the window
  rect scaled by new/old around its top-left; the pointer is a local in-process RECT. The agent also sends
  `WM_DPICHANGED_BEFOREPARENT` / `AFTERPARENT` to per-monitor child HWNDs. On reset it restores the OS DPI the
  same way.
- **Lifetime.** There is no persistence: the DLL lives only as long as the target process.

**`AppScale.Injector.exe`** (x64 console) prints exactly one JSON line. Keys: `ok`, `mode`, `exitCode`, `pid`,
`dpi`, `target`, `dll`, `log`, `ack`, `hr`, `error`, `message`.

```
AppScale.Injector.exe --launch <exe> [--dpi N] [--log path] [--hooks all|research|0xMASK] [--include-system-callers]
                      [--cwd dir] [--timeout ms] [-- args...]
AppScale.Injector.exe --inject <pid> [--dpi N] [--log path] [--hooks ...] [--include-system-callers] [--timeout ms]
AppScale.Injector.exe --check <pid>
```

How each mode loads the DLL:
- `--launch` uses `DetourCreateProcessWithDllExW`, so the hooks are in place before any framework caches DPI.
- `--inject` writes the full DLL path into the target and calls `CreateRemoteThread(LoadLibraryW)`.

Both modes wait for an acknowledgement in the shared section.

Outputs (Release; Debug is the same under `...\debug\`):
`artifacts\bin\Win32Harness\release\AppScale.Injector.exe`, `AppScale.Native.dll` and `Win32Harness.exe`,
side by side.

### Safety refusals and exit codes

| Exit | Meaning |
|---|---|
| 0 | ok |
| 2 | usage, including `--dpi` outside 72..240 |
| 10 | target not x64 (WOW64 / 32-bit / ARM64) |
| 11 | target elevated, or launch needs elevation (740) |
| 12 | protected process |
| 13 | different user or session |
| 14 | AppContainer |
| 15 | denylisted (security-sensitive or shell, e.g. explorer, lsass, csrss, winlogon, AV) |
| 16 | `OpenProcess` failed |
| 17 | already injected |
| 18 | DLL missing |
| 19 | injector itself elevated |
| 20 | launch failed |
| 21 | remote `LoadLibraryW` failed |
| 22 | ack timeout |
| 23 | hook install failed |
| 24 | target exited |

Measured refusals:

| Target | Command | Exit | Message |
|---|---|---|---|
| `C:\Windows\SysWOW64\notepad.exe` | `--launch` | 10 | notX64 (process not created) |
| explorer.exe | `--check` / `--inject` | 15 | denylisted |
| System (pid 4), lsass, a svchost | `--inject` | 16 | `OpenProcess` error 5 (access denied) |
| pid 999999 | `--inject` | 16 | error 87 |
| harness | `--dpi 500` | 2 | usage |
| already-injected harness | `--inject` again | 17 | alreadyInjected |

**UNVERIFIED:** the exit-11 path (elevated target, or elevation required). No elevated test process was
started, deliberately, under the no-elevation rule. Exits 12, 13, 14 and 18–24 also have code paths but
were not triggered in this evidence run.

## Commands used

```powershell
$bin='artifacts\bin\Win32Harness\release'
# 1. Harness without cooperative support, launched with hooks in place at 200 %
& $bin\AppScale.Injector.exe --launch $bin\Win32Harness.exe --dpi 192 --log artifacts\evidence\native\inject-launch-native.jsonl -- --no-cooperative --log artifacts\evidence\native\inject-launch-harness.jsonl
tests\Win32Harness\Invoke-ProtocolProbe.ps1 -ProcessId <pid> -Sequence 144,192,96,0,300
tests\Win32Harness\Capture-Window.ps1 -ProcessId <pid> -Path docs\evidence\screenshots\win32-inject-150.png   # after each SetDpi
# 2. Attach to an already running non-cooperative harness
& $bin\Win32Harness.exe --no-cooperative            # started separately
& $bin\AppScale.Injector.exe --inject <pid> --dpi 192 --log artifacts\evidence\native\inject-attach-native.jsonl
# 3. Third-party apps
& $bin\AppScale.Injector.exe --launch "$env:LOCALAPPDATA\Programs\Microsoft VS Code\Code.exe" --dpi 144 --log artifacts\evidence\native\inject-vscode-native.jsonl -- --user-data-dir artifacts\scratch\vscode-ud --disable-extensions docs
& $bin\AppScale.Injector.exe --launch "C:\Program Files\ScreenToGif\ScreenToGif.exe" --dpi 144 --log artifacts\evidence\native\inject-screentogif-native.jsonl
& $bin\AppScale.Injector.exe --launch "C:\Program Files\7-Zip\7zFM.exe" --dpi 144 --log artifacts\evidence\native\inject-7zfm-native.jsonl
& $bin\AppScale.Injector.exe --launch "C:\Program Files\7-Zip\7zFM.exe" --dpi 192 --log artifacts\evidence\native\inject-7zfm-192-native.jsonl
& $bin\AppScale.Injector.exe --launch "C:\Program Files\7-Zip\7zFM.exe" --dpi 192 --include-system-callers --log artifacts\evidence\native\inject-7zfm-192-sys-native.jsonl
```

`Invoke-ProtocolProbe.ps1` sends the registered messages with `SendMessageTimeout` (2000 ms), exactly as
Core's `CooperativeBackend` does. Core needs no change to drive an injected app.

`Capture-Window.ps1` runs per-monitor-aware (`SetThreadDpiAwarenessContext(-4)`) and uses
`PrintWindow(PW_RENDERFULLCONTENT)` for the whole window. Without the PMv2 thread context, PowerShell gets
virtualised rects (495 px instead of 742 px).

## Results

### A. Win32Harness `--no-cooperative` — a PMv2 Win32 app with no AppScale awareness

In this mode the harness does not set the property, does not register the protocol messages and has no
override. It only does what a well-written PMv2 app does: on `WM_DPICHANGED` it calls `GetDpiForWindow`,
re-lays out from logical values and resizes.

**Launch at 192** (`--launch`, pid 28104):
- ack 1.
- **Start-up.** The app saw `GetDpiForWindow` and `GetDpiForSystem` 192 (real 144) from its first window
  creation, and even logged its own "osDpi" as 192. The lie is complete from the app's point of view.
- Startup client 1440×680, which is MulDiv-exact for 200 %.

Protocol sequence:

| SetDpi | Result | GetDpi effective / os | App-visible `GetDpiForWindow` | Real `GetDpiForWindow` | Client | Window | Screenshot |
|---|---|---|---|---|---|---|---|
| 144 | 1 | 144 / 144 | 144 | 144 | 1080×510 | 1102×596 | `win32-inject-150.png` |
| 192 | 1 | 192 / 144 | 192 | 144 | 1440×680 | 1462×766 | `win32-inject-200.png` |
| 96 | 1 | 96 / 144 | 96 | 144 | 720×340 | 742×426 | `win32-inject-100.png` |
| 0 (reset) | 1 | 144 / 144 | 144 | 144 | 1080×510 | 1102×596 | — |
| 300 | 0 | unchanged | — | — | — | — | — |

- Every relayout was MulDiv-exact.
- The unmodified app logged each injected `WM_DPICHANGED` as **genuine**: from inside the app it is
  indistinguishable from an OS notification.
- `suggestedHonoured = false`. The app sized the window itself from `AdjustWindowRectExForDpi`, and its
  correction loop then fixed the client size, because the real non-client area is still at 144.

Hook statistics for the launch run (calls / overridden):

| API | Calls / overridden | Notes |
|---|---|---|
| `GetDpiForWindow` | 192 / 179 | — |
| `GetDpiForSystem` | 3365 / 41 | 3108 system-caller pass-throughs, mostly uxtheme |
| `GetDeviceCaps` | 2972 / 41 | — |
| `GetDpiForMonitor` | 74 / 41 | — |
| `GetSystemMetrics` | 1632 / 125 | 1400 COMCTL32 pass-throughs |

**Inject into a running instance** (`--inject`, pid 52944):
- ok, loadMode 2 (remote thread).
- The agent broadcast 144→192, and the running app re-laid out to a 1440×680 client.
- The probe sequence 96/144/192/0/300 behaved exactly as in the table above.
- A second `--inject` returned 17 (alreadyInjected), and `--check` returned ok.

**What did not follow** (visible in `win32-inject-*.png`):
- The caption and menu bar stay at the OS (144) size at every effective DPI.
- The combobox's selected text still read "100%". This is a stale app-state string, not a scaling defect.
- Scrollbars are drawn by COMCTL32/USER32, which pass through as system callers, so they stay at OS size.

### B. Visual Studio Code 1.147.0 (Electron/Chromium, PMv2) — **works**

Launched with `--launch --dpi 144` (pid 70912), using a scratch `--user-data-dir` and `--disable-extensions`.
The top-level `Chrome_WidgetWin_1` was tagged.

| SetDpi | Result | Window after | Suggested honoured | Screenshot |
|---|---|---|---|---|
| (start 144) | — | 2182×1361 | — | `vscode-inject-150-baseline.png` |
| 96 | 1 | 1455×907 | yes | `vscode-inject-100.png` |
| 192 | 1 | 2910×1814 | yes | `vscode-inject-200.png` |
| 144 | 1 | 2183×1361 | yes | `vscode-inject-150.png` |

The **whole** UI re-rendered natively at the new scale, with sharp text, including VS Code's custom
(client-drawn) title bar, menus and editor. Chromium reads DPI through `GetDpiForMonitor`:

| API | Calls / overridden | Callers |
|---|---|---|
| `GetDpiForMonitor` | 515 / 515 | all from Code.exe |
| `GetSystemMetrics` | 17 / 13 | Code.exe overridden; uiautomationcore, MSCTF and directmanipulation passed through |
| `GetDpiForSystem` | 146 / 0 | USER32/uxtheme, system callers passed through |
| `SystemParametersInfoW` | 1 / 1 | Code.exe |

The 1-pixel difference between 2182 and 2183 after the round trip comes from Chromium's own rounding of the
suggested rect.

Not verified for VS Code:
- the separate renderer and GPU child processes are **not** injected; they follow the browser process's
  scale factor;
- native context menus and popups were not exercised;
- hit-testing was not measured.

### C. ScreenToGif 2.43.2 (WPF on .NET, PMv2) — **works**

Launched with `--launch --dpi 144` (pid 88844).

| SetDpi | Result | Window after | Suggested honoured | Screenshot |
|---|---|---|---|---|
| (start 144) | — | 750×330 | — | `screentogif-inject-150-baseline.png` |
| 96 | 1 | 500×220 | yes | `screentogif-inject-100.png` |
| 192 | 1 | 1000×440 | yes | `screentogif-inject-200.png` |
| 144 | 1 | 750×330 | yes | `screentogif-inject-150.png` |

WPF re-rendered everything, including its custom window chrome, at the new DPI. Its calls come from
`<unknown>` caller modules (JIT-compiled P/Invoke stubs, which are not system modules, so they are overridden):

| API | Calls / overridden | Notes |
|---|---|---|
| `GetDpiForWindow` | 3 / 3 | — |
| `GetDpiForMonitor` | 12 / 12 | — |
| `GetSystemMetrics` | 43 / 16 | d3d9, gdiplus and MSCTF passed through |

### D. 7-Zip File Manager 26.06 (Win32, **system-DPI-aware**, awareness 1) — **does not work (negative result)**

- **Launch at 144** (`--launch`, agent loadMode 1), **then SetDpi 96/192 via the protocol.** The protocol
  answered (results 1, GetDpi reported the effective value), yet the window and its suggested rect stayed the
  same.
- **Screenshots.** Every capture is pixel-identical at 2112×1684: `7zfm-inject-150-baseline/100/150/200.png`
  (the file names say "inject" because the DLL was injected at launch). A PIL pixel diff returned an empty
  bounding box.
- **Launch at 192**, with and without `--include-system-callers`, gave the same result
  (`7zfm-launch-200.png`, `7zfm-launch-200-syscallers.png`).
- **Overrides.** None. Every DPI query came from system modules (COMCTL32, uxtheme, USER32, gdi32full,
  SHELL32), never from 7zFM.exe, and the threads are not per-monitor aware. The safety gate therefore passed
  everything through: for example, in the `--include-system-callers` run, `GetSystemMetrics` had 715 calls
  and 715 not-per-monitor pass-throughs.

Interpretation:
- A system-aware app has no live DPI-change path, and DWM bitmap-scales it.
- Our gate deliberately does not lie to such threads.
- Removing the gate would at most change the app's startup layout. The window would then also be
  bitmap-scaled by DWM, giving a double-scaling mismatch. This alternative was **not tested** and is not
  recommended.

## What reflows and what does not

| Element | PMv2 Win32 (harness) | Electron (VS Code) | WPF (ScreenToGif) | System-aware Win32 (7-Zip) |
|---|---|---|---|---|
| Client-area layout and text | yes, MulDiv-exact | yes | yes | no |
| Window size | yes (app-driven) | yes (suggested rect) | yes (suggested rect) | no |
| Caption / non-client frame | **no**, OS DPI | yes (client-drawn title bar) | yes (custom chrome) | no |
| Native menu bar | **no**, OS DPI | n/a (custom menu) | n/a | no |
| Scrollbars (USER32/COMCTL32-drawn) | **no** | n/a | n/a | no |
| Tooltips and `MessageBox` / system dialogs | **no** (see [milestone2-detours.md](milestone2-detours.md)) | not tested | not tested | no |
| Mixed child HWNDs | per-monitor children get BEFORE/AFTERPARENT; system-aware children stay DWM-scaled | not tested | not tested | — |
| Hit-testing / input after scaling | not measured under injection | not measured | not measured | — |

## What this does NOT prove

- **Coverage:** three working targets, one per framework family, are not a compatibility guarantee. Qt,
  WinForms, WinUI 3 and MFC were not injected in this lane.
- **Restrictions:** behaviour under elevation, AppContainer, protected processes or other users is unverified
  beyond the refusal codes listed above.
- **Input and accessibility:** input accuracy, accessibility/UI Automation, IME, drag/drop and touch were not
  tested after injection.
- **Robustness:** long-running stability, unloading, crash-safety and CPU overhead were not measured.
- **Thread coverage:** in `--inject` mode, Detours attach runs on the remote thread. Hooks are process-wide
  code patches, but threads blocked inside a hooked function at attach time are not updated
  (`DetourUpdateThread` is only called for the current thread).
- **Anti-cheat and code-integrity:** apps that use anti-cheat or code-integrity protection (Chromium sandboxed
  children, games, etc.) were not tested and may reject the DLL.

## Decision gate

- **Viable, behind per-app consent:** injection plus API virtualization plus the app's own `WM_DPICHANGED`
  path gives **true native re-rendering** for per-monitor-aware apps whose framework renders its own chrome.
  This was shown for Electron and WPF, and for a plain PMv2 Win32 client area.
- **Not viable:**
  - **System-aware or unaware apps:** they have no live path. Use the visual fallback.
  - **Native non-client, menus and system dialogs:** OS-owned UI stays at OS DPI. For classic Win32 apps this
    gives a visibly mixed result that the UX must disclose. It is incoherent but not broken: client hit-testing
    is unaffected.
- **Recommendation:** ship the injection backend as experimental, opt-in per executable, with the refusal list
  above. Detect the target's awareness before offering it (refuse or fall back when it is not PMv2), and keep
  the hook set fixed.

References:
- [WM_DPICHANGED](https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged)
- [WM_DPICHANGED_BEFOREPARENT](https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged-beforeparent)
- [WM_DPICHANGED_AFTERPARENT](https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged-afterparent)
- [WM_GETDPISCALEDSIZE](https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-getdpiscaledsize)
- [GetSystemMetricsForDpi](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetricsfordpi)
- [SystemParametersInfoForDpi](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfofordpi)
- [DetourCreateProcessWithDllEx](https://github.com/microsoft/Detours/wiki/DetourCreateProcessWithDllEx)
- [CreateRemoteThread](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createremotethread)
