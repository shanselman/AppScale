# Milestone 3 evidence: WinForms and WPF cooperative adapters

Both harnesses in this lane are standalone apps. They do **not** reference `AppScale.Core`, and they implement only cooperative protocol v1 ([ADR 0002](../adr/0002-cooperative-protocol.md)):

- `SetProp(hwnd, "AppScale.Cooperative.v1", 1)` on every top-level window.
- `AppScale.SetDpi.v1` handling: wParam is an absolute DPI from 72 to 240, or 0 to reset. The reply is 1 (accepted) or 0 (rejected).
- `AppScale.GetDpi.v1` handling: the reply is `MAKELONG(effectiveDpi, osDpi)`.
- `APPSCALE_DPI` is read at startup.

The override is **app-wide**: a `SetDpi` sent to any cooperative window applies to every window in the process, and new windows (dialogs) inherit it.

| Harness | Source | Executable |
|---|---|---|
| WinForms | `tests\WinFormsHarness` | `artifacts\bin\WinFormsHarness\release\WinFormsHarness.exe` |
| WPF | `tests\WpfHarness` | `artifacts\bin\WpfHarness\release\WpfHarness.exe` |

## Environment (observed, not changed)

| Item | Value |
|---|---|
| OS | Windows 11, `Microsoft Windows NT 10.0.26340.0`, x64 |
| .NET | SDK 10.0.404, runtime `.NET 10.0.15` |
| Process awareness | PerMonitorV2: WinForms via `Application.SetHighDpiMode`, WPF via `app.manifest` |
| System DPI | 144 |
| DISPLAY1 (primary) | 3840×2560 at (0,0), **144 DPI** (150%) |
| DISPLAY2 | 1920×1280 at (931,2560), **120 DPI** (125%) |
| DISPLAY3 | 3840×2560 at (−3840,5), **144 DPI** |
| DISPLAY4 | 3840×2560 at (3840,0), **144 DPI** |

Monitor DPIs come from `GetDpiForMonitor(MDT_EFFECTIVE_DPI)`, as recorded in the self-test result JSON. The harness windows open on DISPLAY1, so the OS DPI is 144.

## Commands

```powershell
dotnet build tests\WinFormsHarness -c Release   # 0 warnings, 0 errors
dotnet build tests\WpfHarness -c Release        # 0 warnings, 0 errors

# 50 rounds × [72,96,120,144,192,240], every change sent via SendMessage(AppScale.SetDpi.v1) to the harness's own HWND
artifacts\bin\WinFormsHarness\release\WinFormsHarness.exe --selftest --result %TEMP%\appscale-m3\winforms-selftest.json
artifacts\bin\WpfHarness\release\WpfHarness.exe           --selftest --result %TEMP%\appscale-m3\wpf-selftest.json
# Startup-override variant (10 rounds)
$env:APPSCALE_DPI=120; ...exe --selftest --rounds 10 --result ...-selftest-env120.json

# Screenshots (APPSCALE_DPI = 96 / 144 / 192 → 100 / 150 / 200 %)
$env:APPSCALE_DPI=96; ...exe --screenshot docs\evidence\screenshots\<winforms|wpf>-100.png --result ...

# External protocol check (throwaway script in %TEMP%\appscale-m3\external-check.ps1; not in repo)
external-check.ps1 -Exe <exe> -Name <winforms|wpf> -EnvDpi 144   # and -EnvDpi 120
```

Other options:

- `--exit-after <ms>` closes the window automatically.
- `--log <path>` writes JSON Lines diagnostics. The default path is `%LOCALAPPDATA%\AppScale\logs\<harness>-<pid>.jsonl`. Each line records OS DPI (`GetDpiForWindow`), effective DPI, awareness and scale (as separate fields), window and client rects, every protocol message, and every `WM_DPICHANGED`.
- `--rounds N` sets the number of self-test rounds.
- `--no-popup-scale` (WPF only) turns off popup scaling.

With no flags the harness runs interactively. Its **View** menu (75/100/125/150/200/250 %, Reset) sends `SetDpi` to the window's own HWND, which is the same path an external sender uses.

## External protocol check (out of process)

The script launches the exe with `APPSCALE_DPI` set and finds the visible window of that PID that has the property set. It then sends messages with `SendMessageTimeoutW(SMTO_BLOCK|SMTO_ABORTIFHUNG, 2000 ms)`.

| Harness | APPSCALE_DPI | GetPropW | GetDpi at start | SetDpi(192) → GetDpi | SetDpi(300) | SetDpi(0) → GetDpi | Result |
|---|---|---|---|---|---|---|---|
| WinForms | 144 | 1 | eff 144 / os 144 | 1 → 192 / 144 | 0 | 1 → 144 / 144 | **pass** |
| WPF | 144 | 1 | eff 144 / os 144 | 1 → 192 / 144 | 0 | 1 → 144 / 144 | **pass** |
| WinForms | 120 | 1 | **eff 120 / os 144** | 1 → 192 / 144 | 0 | 1 → 144 / 144 | **pass** |
| WPF | 120 | 1 | **eff 120 / os 144** | 1 → 192 / 144 | 0 | 1 → 144 / 144 | **pass** |

The 120 run matters because 144 equals the OS DPI. It shows the startup override is honoured, not just defaulted.

## WinForms results

**Mechanism.** The harness uses `Application.SetHighDpiMode(PerMonitorV2)`, `AutoScaleMode.Dpi` and `AutoScaleDimensions = 96`. The original logical (96-DPI) bounds and font sizes are snapshotted once. On every change:

- Each control gets `Bounds = MulDiv(logical, eff, 96)`.
- Each control gets a new `Font` with point size `pt × eff / systemDpi`. GDI converts points using the system DPI, so this yields `lfHeight = −MulDiv(pt, eff, 72)`.
- The form sets `ClientSize` from the scaled content.

On a genuine `WM_DPICHANGED` the base handler runs first, then the harness re-applies the absolute effective DPI.

Investigated alternatives:

- **`Control.Scale(SizeF(eff/current))` repeatedly**: rejected. Over 50 rounds the measurement showed drift in **50/50 rounds**, a maximum drift of **2 px** at 96 DPI, and fonts not scaled.
- **`DeviceDpi`**: read-only, so it cannot be redirected. Internals that use `LogicalToDeviceUnits` stay at the OS DPI (see the table below).

Full run: 50 rounds, 300 steps, start DPI 144 (no override).

| Area | Result |
|---|---|
| Startup override | `APPSCALE_DPI=120` → GetDpi 120/144 from outside. The 10-round self-test from that start passed: 1794 bounds checks, 0 failed, 0 drift. |
| Live change | Every `SetDpi` returned 1, and GetDpi reported the requested DPI at all 300 steps. `SetDpi(50)` and `SetDpi(300)` returned 0. |
| Layout (client controls) | 8274 bounds checks against `MulDiv(logical, eff, 96)`: x, y, w and h of 7 controls at every step, plus the start, reset, dialog and move checks. Client width was checked separately. **0 failed, max error 0 px**. Combo height is excluded; it is set by the font. |
| Fonts | 300 checks of `LOGFONT.lfHeight == −MulDiv(9, eff, 72) ±1`: 0 failed. |
| Text sharpness | GDI text is rasterised natively at the effective size (see `winforms-100/150/200.png`); there is no bitmap stretching. |
| MenuStrip / ToolStrip | Text follows, but height has an OS-DPI floor. Menu strip is 36 / 36 / 42 px and tool strip 38 / 38 / 41 px at eff 96 / 144 / 192 (OS 144). Padding and image sizes come from private DeviceDpi-scaled fields; setting `ImageScalingSize` did not change the item height. |
| File dropdown (submenu) | Item height 34 / 34 / 40 at eff 96 / 144 / 192; width 143 / 172 / 196. Partially follows: text follows, padding stays at the OS DPI. |
| ContextMenuStrip | Item height 24 / 32 / 38 at eff 96 / 144 / 192. `DeviceDpi` stays 144. Partially follows. |
| ToolTip | The owner-drawn tooltip with the scaled font **follows**: 230×23, 344×37, 465×48. The native (common-control) tooltip **stays** at 256×30 at every effective DPI. |
| ListView | Row height follows (19 / 29 / 36). The vertical scrollbar **stays** 26 px, which equals `GetSystemMetricsForDpi(SM_CXVSCROLL, os)`; at the effective DPI it would be 17 / 34. |
| Glyphs | Check-box glyph, combo arrow and toolstrip overflow glyph are theme-drawn at the OS DPI. This is visible in `winforms-100.png` and `winforms-200.png`. |
| Modal dialog | Opened at eff 192 (OS 144): client 640×264 = MulDiv(320×132, 192, 96); all bounds OK; the dialog's own GetDpi reports 192/144. |
| Non-client | The caption, buttons and frame are drawn by the OS at the OS DPI, and their size is unchanged by the override (visible in the screenshots). |
| Common dialogs | Not tested; they are shell-owned. |
| Hit testing | 2100 tests (7 controls × 300 steps) with `ChildWindowFromPointEx` descent from the client: 0 failed. A separate `GetChildAtPoint` chain: 0 failed. `WindowFromPoint` (screen-level) is informational: it matched 0/2100 because another process's window (`Chrome_RenderWidgetHostHWND`) covered the test area even with `TopMost`, so on-screen hit testing was **not** proven. |
| Drift | After each of the 50 rounds, every measured bound was compared with the baseline: **0 mismatches**. |
| Genuine DPI change | The window moved itself to DISPLAY2 (120 DPI) and back. One `WM_DPICHANGED` arrived per move. With override 192: OS 120 / eff 192, client width 1164 on both monitors. Without override: eff followed the OS (client 728 at 120, 873 at 144). Bounds were OK in all four steps. |
| Reset | `SetDpi(0)` → eff 144 = OS 144. |
| Timing (measurement only) | Layout per rescale (send → return): median **91.3 ms**, p95 143.5 ms, min 0.84 ms, max 185.4 ms (n = 359). Paint pump: median 19.9 ms. A 5-round run earlier measured a 55.7 ms median, so expect machine-load variance. |

## WPF results

**Mechanism.** The window content is wrapped as `Border Outer` → `Grid Inner` with `Inner.LayoutTransform = ScaleTransform(eff / osDpi)`. The window uses `UseLayoutRounding` and `SizeToContent`. The ratio is always recomputed from the absolute effective DPI and the current `GetDpiForWindow`. `Window.OnDpiChanged` re-applies the effective DPI after WPF's own per-monitor handling.

Under a LayoutTransform, WPF lays out and rasterises text and geometry at the final size. This is vector re-rendering, not bitmap scaling (see `wpf-200.png`). The HWND is obtained via `WindowInteropHelper`, and messages are hooked with `HwndSource.AddHook`.

**Popups.** Separate popup windows were measured with the popup scaling on and off (`--no-popup-scale`):

- **Menu submenus and the ComboBox dropdown follow already**, with no extra code. Their `Popup` is declared in the control template, inside the scaled tree, and WPF carries the placement target's transform over to the popup.
- **ContextMenu and ToolTip do not follow.** Their popup is not a descendant of the scaled content.
- First attempt, rejected: a class handler on `FrameworkElement.LoadedEvent` meant to put a transform on `PopupRoot`. It never fired for popup content; the logs show only main-window elements, because WPF broadcasts `Loaded` only into subtrees that have instance handlers.
- What works: class handlers on `ContextMenu.OpenedEvent` and `ToolTip.OpenedEvent` set `LayoutTransform = ScaleTransform(eff / popupOsDpi)` on the ContextMenu or ToolTip element itself. Open ones are re-scaled when the override changes.

Full run: 50 rounds, 300 steps, start DPI 144 (no override).

| Area | Result |
|---|---|
| Startup override | `APPSCALE_DPI=120` → GetDpi 120/144 from outside. The 10-round self-test from that start passed: 2124 bounds checks, 0 failed, 0 drift. |
| Live change | Every `SetDpi` returned 1, and GetDpi reported the requested DPI at all 300 steps. `SetDpi(50)` and `SetDpi(300)` returned 0. |
| Layout | 9804 checks against `MulDiv(logical, eff, 96)`: x, y, w and h of 8 elements in physical px (via `TransformToAncestor` × os/96) at every step, plus the start, reset, dialog and move checks. Client width was checked separately. **0 failed, max error 1 px** (layout rounding). Known label: 240×24 / 360×36 / 480×48 px at 100 / 150 / 200 %. |
| Text and vector sharpness | Text, the ellipse, the Bézier path, 1-px grid lines and a 9-px TextBlock render crisply at 200 % (`wpf-200.png`); no resampling blur was observed. Scrollbars, check glyphs and combo arrows are WPF-drawn, so they follow too, unlike in WinForms. |
| Menu / ToolBar | Inside the scaled root, so they follow fully (`wpf-*.png`). |
| Menu submenu (File) | 131×80 / 196×120 / 261×160 at eff 96 / 144 / 192. Follows **with or without** the popup hook. |
| ComboBox dropdown | 245×91 / 368×136 / 491×181. Follows with or without the hook. |
| ContextMenu | With hook: 127×80 / 191×120 / 255×160 (**follows**). Without: 191×120 at every DPI (**stays at OS DPI**). |
| ToolTip | With hook: 163×27 / 244×40 / 325×53 (**follows**). Without: 244×40 at every DPI (stays). |
| Popup assertion | 8 checks of the popup HWND size at eff against `MulDiv(size at OS DPI, eff, os)` ±2 px: 0 failed. |
| Modal dialog | eff 192 / OS 144: client 640×264, bounds OK, dialog GetDpi 192/144. |
| Non-client | The caption and frame stay at the OS DPI (OS-drawn). RenderTargetBitmap screenshots show the client area only. |
| Hit testing | 2400 tests (8 elements × 300 steps) of each kind, all 0 failed: `Outer.InputHitTest` at the element centre, `VisualTreeHelper.HitTest`, and a screen round-trip (`PointToScreen` → round to whole px → `PointFromScreen` → `InputHitTest`). Real mouse input was not injected. |
| Drift | 0 mismatches after each of 50 rounds. Compared: 8 element bounds, client size and window size. |
| Genuine DPI change | `SetWindowPos` to DISPLAY2 (120) and back, with one `WM_DPICHANGED` per move. With override 192 the scale was 1.6 on the 120 monitor and 1.3333 on the 144 monitor, and the client width stayed 1520 on both. Without override, eff followed the OS (client 950 at 120, 1140 at 144). Bounds were OK in all steps. |
| Reset | `SetDpi(0)` → eff 144 = OS 144, transform scale 1.0. |
| Timing (measurement only) | Layout per rescale (`SendMessage` → `UpdateLayout` returned): median **8.3 ms**, p95 11.8 ms, min 0.05 ms, max 16.0 ms (n = 362). Render pump: median 0.57 ms. |

## Screenshots

| File | Effective DPI | Image size (px) | Method |
|---|---|---|---|
| `screenshots/winforms-100.png` | 96 (OS 144) | 586×317 | `PrintWindow(PW_RENDERFULLCONTENT)` cropped to the DWM frame (includes the caption) |
| `screenshots/winforms-150.png` | 144 | 877×415 | same |
| `screenshots/winforms-200.png` | 192 | 1168×522 | same |
| `screenshots/wpf-100.png` | 96 (OS 144) | 760×242 | `RenderTargetBitmap` of the root content at OS DPI (no caption) |
| `screenshots/wpf-150.png` | 144 | 1140×363 | same |
| `screenshots/wpf-200.png` | 192 | 1520×484 | same |

## Injection-target mode (`--no-cooperative`)

This mode was added for the injection approach, where a Detours DLL lies about DPI and then sends `WM_DPICHANGED`. With `--no-cooperative`, each harness behaves as a stock PMv2 app:

- It sets no `AppScale.Cooperative.v1` prop and does not handle `AppScale.SetDpi.v1` or `AppScale.GetDpi.v1`.
- It does not read `APPSCALE_DPI`, and it has no View menu.
- WinForms uses designer-style `AutoScaleMode.Dpi` / `AutoScaleDimensions(96,96)`, set inside `SuspendLayout`, with an ambient Segoe UI 9pt font and no AppScale re-layout.
- WPF has no `LayoutTransform` and no popup hook.
- Combining it with `--selftest` is rejected (exit 2).

The harness only logs; it never alters the framework's handling of `WM_DPICHANGED`.

Logged events (JSONL):

| Event | Contents |
|---|---|
| `startup` | `mode` = stock |
| `window.created` | Initial state |
| `stock.observed` | Logged at `load` / `contentRendered`, and after each `WM_DPICHANGED` |
| `wm.dpichanged` | wParam, suggested RECT, and a *before* snapshot |
| `wm.dpi-related` | `WM_GETDPISCALEDSIZE` and related messages |

Every snapshot carries:

- `GetDpiForWindow`, awareness, window and client rect
- the framework DPI: `Control.DeviceDpi` (WinForms), or `VisualTreeHelper.GetDpi` and `PixelsPerDip` (WPF)
- font metrics: LOGFONT `lfHeight` / `heightPx` (WinForms) or `FontSize` in DIP (WPF)
- the physical bounds of the known label (240×24 logical), the Apply button, list columns, and menu/tool strip heights

Commands:

```powershell
$env:APPSCALE_DPI = "120"   # deliberately set, to prove it is ignored
artifacts\bin\WinFormsHarness\release\WinFormsHarness.exe --no-cooperative --exit-after 15000 --log $env:TEMP\appscale-m3\winforms-stock.jsonl
artifacts\bin\WpfHarness\release\WpfHarness.exe         --no-cooperative --exit-after 15000 --log $env:TEMP\appscale-m3\wpf-stock.jsonl
```

The out-of-process check was a throwaway script, `%TEMP%\appscale-m3\stock-check.ps1`, which is not in the repo. For each harness it:

1. Found the window by process ID.
2. Called `GetPropW`.
3. Sent `GetDpi` and `SetDpi(192)` with `SendMessageTimeoutW`.
4. Produced a genuine OS DPI change by moving the window with `SetWindowPos` to DISPLAY2 (120 DPI) and back to DISPLAY1 (144 DPI).

Results for both harnesses:

- The prop was 0, the `GetDpi` reply was 0 and the `SetDpi(192)` reply was 0 (unhandled).
- The log shows `envAppScaleDpi` null and no startup override, although `APPSCALE_DPI=120` was set.

Real `WM_DPICHANGED` response (from the logs):

| Step | WinForms GetDpiForWindow / DeviceDpi | WinForms label px | WinForms font lfHeight | WinForms client | WPF GetDpiForWindow / VisualTreeHelper DPI | WPF label px | WPF client |
|---|---|---|---|---|---|---|---|
| Startup on 144 | 144 / 144 | 359×35 | −18 | 873×369 | 144 / 144 (PixelsPerDip 1.5) | 360×36 | 1140×363 |
| Before snapshot in `WM_DPICHANGED` (→120) | 120 / **144** | 359×35 | −15 | 873×369 | 120 / **144** | 300×30 (projection) | 1140×363 |
| After moving to 120 | 120 / 120 | 300×30 | −15 | 728×307 | 120 / 120 (1.25) | 300×30 | 950×300 |
| After moving back to 144 | 144 / 144 | 360×36 | −18 | 873×369 | 144 / 144 (1.5) | 360×36 | 1140×363 |

Observations:

- **Before snapshot.** Inside `WM_DPICHANGED`, `GetDpiForWindow` already reports the new DPI, but the framework DPI (`DeviceDpi` / `VisualTreeHelper.GetDpi`) still reports the old value until the framework processes the message.
- **WinForms startup.** WinForms' startup AutoScale gave the known label 359×35 rather than the exact 360×36; this is its usual rounding. After a real round trip it was 360×36.
- **WPF before-snapshot label.** The WPF label px in the before snapshot is DIP × the new `GetDpiForWindow` value, so it is a projection and not the rendered size.

Both frameworks therefore follow a genuine `WM_DPICHANGED` natively, including fonts, layout and client size. This is the baseline an injected DPI lie has to reproduce. **Not yet tested:** whether either framework follows an *injected* lie. That depends on the injector (lane-native), and those results belong in the injection evidence.

## What this does NOT prove

- **Third-party WinForms and WPF apps do not implement this protocol.** These harnesses show that an app which opts in *can* render natively at an arbitrary effective DPI. Nothing here changes an unmodified app.
- Qt, Electron/Chromium, WinUI 3, MFC/Win32 and Java were not tested.
- Common file and print dialogs, `MessageBox`, and the OS caption and non-client area remain at the OS DPI. No API in either framework moves them.
- WinForms ToolStrip/MenuStrip padding, native tooltips, scrollbars and themed glyphs remain at the OS DPI, so WinForms coverage is partial by design.
- Screen-level hit testing (`WindowFromPoint`) for WinForms could not be proven because another process's window covered the harness; WPF hit tests are in-process. Real mouse input, keyboard focus visuals, IME, accessibility (UIA bounding rects) and multi-window drag were not tested.
- Only one machine was used: one OS build, OS DPIs of 144 and 120, effective DPIs from 72 to 240. Timings are single-run measurements on a loaded desktop, not performance targets.
- The app-wide override semantics (SetDpi on any window affects all windows in the process) are a harness design choice; the ADR does not specify them.

## Conclusions

- **WPF: works well.** One `LayoutTransform(eff/os)` on the root plus a small hook for ContextMenu and ToolTip produces native, crisp rendering at the effective DPI:
  - all measured content follows within ±1 px
  - all four popup types follow
  - no drift; genuine DPI changes keep the absolute effective DPI
  - rescale median 8.3 ms

  Only the OS-drawn caption and frame stay at the OS DPI.
- **WinForms: works for client content, partially for chrome.** Absolute re-layout from a 96-DPI snapshot plus font recreation is exact: 0 px error, correct LOGFONT heights, no drift, and genuine DPI changes are handled. `Control.Scale` with ratios drifts and must not be used.
  - Stays at the OS DPI: anything WinForms or comctl32 sizes from `DeviceDpi` or system metrics. That covers ToolStrip/menu padding (a height floor), native tooltips, scrollbar widths, themed glyphs and the caption.
  - Owner-drawn replacements can fix individual items, as shown with the tooltip.
  - Rescale is much slower than WPF: median 91 ms in the full run.

## References (Microsoft Learn, checked for this milestone)

- Automatic scaling / high-DPI in Windows Forms: https://learn.microsoft.com/dotnet/desktop/winforms/forms/autoscale
- `HighDpiMode` enum: https://learn.microsoft.com/dotnet/api/system.windows.forms.highdpimode
- .NET 8 breaking change, top-level window scaling with PerMonitorV2 and AutoScaleMode: https://learn.microsoft.com/dotnet/core/compatibility/windows-forms/8.0/top-level-window-scaling
- .NET 6 ScaleControl call optimisation: https://learn.microsoft.com/dotnet/core/compatibility/windows-forms/6.0/optimize-scalecontrol-calls
- What's new in Windows Forms .NET 7 (high-DPI improvements): https://learn.microsoft.com/dotnet/desktop/winforms/whats-new/net70
- Declaring managed apps DPI-aware (WPF per-monitor): https://learn.microsoft.com/windows/win32/hidpi/declaring-managed-apps-dpi-aware
- `WM_DPICHANGED`: https://learn.microsoft.com/windows/win32/hidpi/wm-dpichanged
- High DPI desktop application development on Windows: https://learn.microsoft.com/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows
