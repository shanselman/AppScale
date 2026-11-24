# ADR 0003 — Visual fallback: DWM thumbnails first

Status: Accepted (read-only prototype)

## Context

Some applications can't be natively rescaled: system-aware or DPI-unaware applications, applications that cache DPI,
and applications whose framework bypasses the hooks. For these, the specification asks for a clearly labelled visual
magnification fallback. The candidates are Windows Graphics Capture (WGC) with a GPU renderer, or DWM thumbnails
([screen capture docs](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture)).

## Decision

Start with **DWM thumbnails** (`DwmRegisterThumbnail`), shown in a separate preview window that is labelled
"VISUAL MAGNIFICATION — bitmap, read-only":

- They need no capture consent prompt, no GPU device-loss handling, and no capture of pixel content into our process.
  The compositor draws the target directly, so AppScale never reads application content.
- Protected content stays protected. Minimized or closed sources give a blank thumbnail or an "unavailable" state, and
  nothing crashes.
- The zoom is `requested DPI ÷ actual DPI`.

WGC + DirectComposition remains a candidate for a later milestone, if cropping or effects are needed. IddCx virtual
monitors are a separate research branch and are **not** an MVP dependency. They are rejected for now because they
require installing a driver, and their window placement, input and focus fidelity are hard to get right.

## Consequences

- There is no input forwarding. The preview is look-only until the specification's criteria are proven: coordinate
  transforms, focus, IME and accessibility. `ScaleMath.PreviewToSource` exists, with tests, for that future step.
- AppScale never silently switches from native rendering to a bitmap. Visual mode requires an explicit button press,
  and the profile watcher never auto-applies it.
