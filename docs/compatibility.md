# Compatibility matrix

All results come from real runs on one machine. Each one links to its evidence. **Untested means unknown, not
"probably works."** Don't extrapolate from one framework to another.

**Environment.** Windows 11, build 26340 (x64), RTX 4080 SUPER, four monitors (three at 144 DPI / 150 %, one at
120 DPI / 125 %). Global display settings were never changed.

**Legend**
- **Native**: text and layout are rasterized at the requested DPI, with no bitmap stretching.
- **Partial**: the client area follows the requested DPI, but some OS-drawn UI doesn't.
- **No**: nothing changes.
- **Refused**: AppScale refuses with a structured failure.

## Third-party applications (unmodified)

| Application | Version | Framework / awareness | Backend | Startup (`--launch`) | Live (`--inject`) | Rendering | Input / UIA | Evidence |
|---|---|---|---|---|---|---|---|---|
| VS Code | 1.147.0 | Electron / PMv2 | inject | Native | Native (96/144/192) | Native, including the custom title bar | Not validated | [M2 injection](evidence/milestone2-injection.md), `vscode-inject-*.png` |
| ScreenToGif | 2.43.2 | WPF / PMv2 | inject | Native | Native (500×220 → 1000×440 → 750×330) | Native, including custom chrome | Not validated | [M2 injection](evidence/milestone2-injection.md), `screentogif-inject-*.png` |
| 7-Zip File Manager | 26.06 | Win32 / system-aware | — | No | No | Windows bitmap-scales it | — | `7zfm-*.png` |
| Audacity | 3.7.9 | wxWidgets / unaware | — | — | Refused (`NoBackend`) | Windows bitmap-scales it; visual magnification offered | — | this report |
| VLC | 3.0.24 | Qt 5 / PMv1 | inject | Inconclusive | No (screenshots pixel-identical) | — | — | this report |

## Owned harnesses

| Harness | Mode | Backend | Startup | Live | Layout drift | Non-client / popups | Evidence |
|---|---|---|---|---|---|---|---|
| Win32 (PMv2) | cooperative | cooperative | Native (`APPSCALE_DPI`) | Native, 72–240 | 0 px over 100+ changes | Caption, menu bar, scroll bars and combobox height stay at OS DPI | [M0](evidence/milestone0-baseline.md), [M1](evidence/milestone1-cooperative.md) |
| Win32 (PMv2) | `--no-cooperative` | inject | Native | Native (exact MulDiv layout) | 0 px | Same as above: the falsification finding | [M2](evidence/milestone2-detours.md), [M2 injection](evidence/milestone2-injection.md) |
| WinForms (.NET 10) | cooperative | cooperative | Native | Native, 50 rounds × 6 DPIs | 0 px (8,274 bounds checks) | MenuStrip/ToolStrip height has an OS-DPI floor; native tooltip stays; owner-drawn tooltip follows | [M3](evidence/milestone3-managed-frameworks.md) |
| WinForms (.NET 10) | `--no-cooperative` | inject | — | Native: 240×24 logical label → 480×48 at 200 %, 240×24 at 100 % | 1 px after a 200 → 100 → 150 round trip | `ContextMenuStrip.DeviceDpi` stays at OS DPI | Integration tests, this report |
| WPF (.NET 10) | cooperative | cooperative | Native | Native, 50 rounds × 6 DPIs | 0 px | Popups and menus follow | [M3](evidence/milestone3-managed-frameworks.md) |
| WPF (.NET 10) | `--no-cooperative` | inject | — | Native: `PixelsPerDip` 1.5 → 2 → 1 → 1.5 | 0 px | — | Integration tests, this report |

## Not yet tested

Qt 6, MFC, WinUI 3, Java (Swing/JavaFX), Chromium-based browsers, Office, games, ARM64 and x86 builds, touch, IME,
UI Automation and Narrator after injection, multiple processes of one application (for example Electron renderer
processes), and monitor disconnect while an override is active.

## Summary

- **Works:** native rescaling of unmodified applications is real for frameworks that are per-monitor-v2 aware and draw
  their own UI: WPF, Electron, and WinForms or Win32 client areas.
- **Hard limit:** UI drawn by the OS (Win32 non-client areas, common-control tooltips, system menus) stays at the
  monitor DPI. It can't be lied to from inside the process.
- **Out of reach:** system-aware and unaware applications are bitmap-scaled by Windows, so only visual magnification
  can help them.
- **Small displays:** at 200–250 % on a 1024×768 display, Windows clamps the window and controls can be clipped. See
  the [test report](test-report.md).
