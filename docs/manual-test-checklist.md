# Manual test checklist

Run before calling any build "interactive-ready". Record the OS build, the app version and screenshots in
[compatibility.md](compatibility.md).

## Setup
- [ ] Note each monitor's DPI with `appscale monitors`. Don't change display settings.
- [ ] Opt in through the UI consent panel or with `appscale optin`. Confirm that Apply fails before opting in.

## Per application, at 75, 100, 125, 150, 200 and 250 %
- [ ] Apply live: the backend shown is `inject` or `cooperative`, never a silent visual fallback.
- [ ] Text is sharp, not bitmap-stretched. Compare against the same app on a monitor whose real DPI matches.
- [ ] Layout: no clipped critical controls (OK/Cancel, menus, input fields).
- [ ] Menus, context menus, tooltips and modal dialogs: note which follow and which stay at the OS DPI.
- [ ] Hit-testing: click buttons and checkbox edges, and select text precisely.
- [ ] Keyboard: Tab order and focus rectangles. IME: compose CJK text.
- [ ] Touch and pen (if hardware is available).
- [ ] UI Automation: check Inspect.exe or Narrator bounding rectangles against the visible controls.
- [ ] Drag and drop in and out of the app.
- [ ] Maximize, restore and snap.
- [ ] Move to a monitor with a different DPI: the absolute scale is kept.
- [ ] Reset: the layout returns to the OS DPI (±2 px).

## Lifecycle
- [ ] Remember (enabled profile): close the app and start it again. The watcher applies it within about 1 s.
- [ ] Multiple windows and multiple instances of the same app.
- [ ] Target crashes or is closed while an override is active. The controller shows the failure and doesn't crash.
- [ ] Controller exits: overrides it applied are reset.
- [ ] Emergency disable (tray): every override is reset and Apply is blocked.
- [ ] Disconnect a monitor with an overridden window on it.
- [ ] An unrelated process and global display settings are unchanged (`appscale monitors` before and after).

## Must refuse
- [ ] An elevated app, Explorer, Task Manager, a password manager, a 32-bit app, and a system-aware or unaware app
  (offer visual magnification instead).
