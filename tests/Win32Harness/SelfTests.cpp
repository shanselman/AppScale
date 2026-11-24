// Win32Harness — unattended self-tests (drift, synthetic, monitors, detours, screenshot).
#include "Harness.h"

#include <commctrl.h>
#include <shellscalingapi.h>

#include <algorithm>
#include <set>

namespace {

constexpr UINT kDriftDpis[] = {72, 96, 120, 144, 192, 240};

bool RectEq(const RECT& a, const RECT& b) {
    return a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;
}

// For controls whose height is system-determined (drop-down combo) only x/y/width are ours.
bool ControlMatches(int i, const RECT& actual, UINT dpi) {
    RECT e = ExpectedControlRect(i, dpi);
    if (kControls[i].heightIsSystemDetermined)
        return actual.left == e.left && actual.top == e.top && actual.right == e.right;
    return RectEq(actual, e);
}

void Finish(json::Builder& result, bool pass, int failCode = 1) {
    result.Add("pass", pass);
    g.exitCode = pass ? 0 : failCode;
    result.Add("exitCode", g.exitCode);
    g.log.Write("result", result);
    if (!g.opt.resultPath.empty()) WriteResultFile(g.opt.resultPath, result.Str());
    DestroyWindow(g.hwnd);
}

json::Builder Environment() {
    json::Builder env;
    OSVERSIONINFOEXW v{};
    v.dwOSVersionInfoSize = sizeof(v);
    using RtlGetVersionFn = LONG(WINAPI*)(OSVERSIONINFOEXW*);
    if (auto rtl = reinterpret_cast<RtlGetVersionFn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion"))) rtl(&v);
    env.Add("osMajor", v.dwMajorVersion).Add("osMinor", v.dwMinorVersion).Add("osBuild", v.dwBuildNumber)
        .Add("processAwareness", AwarenessName(GetThreadDpiAwarenessContext()))
        .Add("getDpiForSystem", g_real.GetDpiForSystem())
        .Add("overrideAtStart", g.dpi.Override())
        .Add("overrideSource", g.dpi.OverrideSource())
        .Add("nativeAdapterStatus", g_native.status);
    json::Builder mons(true);
    EnumDisplayMonitors(nullptr, nullptr,
                        [](HMONITOR m, HDC, LPRECT, LPARAM p) -> BOOL {
                            reinterpret_cast<json::Builder*>(p)->Push(MonitorJson(m));
                            return TRUE;
                        },
                        reinterpret_cast<LPARAM>(&mons));
    env.Add("monitors", mons);
    return env;
}

json::Builder SnapJson(const LayoutSnapshot& s) {
    json::Builder b;
    b.Add("dpi", s.dpi).Add("windowW", s.windowW).Add("windowH", s.windowH).Add("clientW", s.client.right)
        .Add("clientH", s.client.bottom).Add("tmHeight", s.tmHeight).Add("tmAveCharWidth", s.tmAveCharWidth);
    json::Builder c;
    for (int i = 0; i < kControlCount; ++i) c.Add(json::Utf8(kControls[i].name), s.controls[i]);
    b.Add("controls", c);
    return b;
}

// ------------------------------------------------------------------------------------------------
// Hit testing: the centre of each control must resolve to that control.
json::Builder HitTest(UINT dpi, int* failures, int* windowFromPointMismatches) {
    json::Builder arr(true);
    for (int i = 0; i < kControlCount; ++i) {
        RECT e = ExpectedControlRect(i, dpi);
        RECT a{};
        GetWindowRect(g.controls[i], &a);
        MapWindowPoints(HWND_DESKTOP, g.hwnd, reinterpret_cast<POINT*>(&a), 2);
        POINT c{(e.left + e.right) / 2, (e.top + std::min(e.bottom, a.bottom)) / 2};
        HWND child = ChildWindowFromPointEx(g.hwnd, c, CWP_SKIPINVISIBLE | CWP_SKIPTRANSPARENT);
        POINT sc = c;
        ClientToScreen(g.hwnd, &sc);
        HWND top = WindowFromPoint(sc);
        // WindowFromPoint can legitimately return a child of the control (combo edit) or the
        // parent (statics answer HTTRANSPARENT) or another app's window if we are occluded.
        bool wfpOk = top == g.controls[i] || GetParent(top) == g.controls[i];
        bool ok = child == g.controls[i];
        if (!ok) ++*failures;
        if (!wfpOk) ++*windowFromPointMismatches;
        json::Builder h;
        h.Add("control", kControls[i].name).Add("x", c.x).Add("y", c.y).Add("childWindowFromPointEx", ok)
            .Add("windowFromPoint", wfpOk);
        arr.Push(h);
    }
    return arr;
}

// ------------------------------------------------------------------------------------------------
void RunDrift() {
    json::Builder result;
    result.Add("test", "drift").Add("environment", Environment());
    const UINT startOverride = g.dpi.Override();
    const UINT startEff = g.dpi.Effective(g.hwnd);
    const UINT os = g.dpi.OsDpi(g.hwnd);
    const LayoutSnapshot start = CaptureLayout();
    result.Add("osDpi", os).Add("startEffectiveDpi", startEff).Add("startLayout", SnapJson(start));

    LayoutSnapshot reference[std::size(kDriftDpis)]{};
    bool haveRef[std::size(kDriftDpis)]{};
    int steps = 0, rectFailures = 0, clientFailures = 0, protocolFailures = 0, refMismatches = 0, restoreMismatches = 0;
    int hitFailures = 0, wfpMismatches = 0;
    json::Builder failures(true);
    int failureCount = 0;
    json::Builder perDpi(true);
    json::Builder hitTests;
    auto fail = [&](const std::string& what) {
        if (failureCount++ < 25) failures.Push(what);
    };

    for (int round = 0; round < g.opt.rounds; ++round) {
        for (size_t k = 0; k < std::size(kDriftDpis); ++k) {
            const UINT dpi = kDriftDpis[k];
            LRESULT r = 0;
            if (!SendSetDpi(dpi, &r) || r != 1) {
                ++protocolFailures;
                fail("SetDpi(" + std::to_string(dpi) + ") returned " + std::to_string(r));
            }
            LRESULT gd = SendGetDpi();
            if (LOWORD(gd) != dpi || HIWORD(gd) != os) {
                ++protocolFailures;
                fail("GetDpi after SetDpi(" + std::to_string(dpi) + ") = " + std::to_string(LOWORD(gd)) + "/" + std::to_string(HIWORD(gd)));
            }
            LayoutSnapshot s = CaptureLayout();
            ++steps;
            for (int i = 0; i < kControlCount; ++i) {
                if (!ControlMatches(i, s.controls[i], dpi)) {
                    ++rectFailures;
                    fail("round " + std::to_string(round) + " dpi " + std::to_string(dpi) + " control " + json::Utf8(kControls[i].name) + " mismatch");
                }
            }
            if (s.client.right != DpiProvider::Scale(kLogicalClientW, dpi) || s.client.bottom != DpiProvider::Scale(kLogicalClientH, dpi)) {
                ++clientFailures;
                fail("round " + std::to_string(round) + " dpi " + std::to_string(dpi) + " client " + std::to_string(s.client.right) + "x" +
                     std::to_string(s.client.bottom));
            }
            if (!haveRef[k]) {
                reference[k] = s;
                haveRef[k] = true;
                json::Builder d;
                d.Add("dpi", dpi).Add("layout", SnapJson(s));
                RECT combo = s.controls[4];
                d.Add("comboSystemHeight", combo.bottom - combo.top);
                perDpi.Push(d);
                hitTests.Add(std::to_string(dpi), HitTest(dpi, &hitFailures, &wfpMismatches));
            } else if (memcmp(&reference[k], &s, sizeof(s)) != 0) {
                ++refMismatches;
                fail("round " + std::to_string(round) + " dpi " + std::to_string(dpi) + " differs from round-0 layout");
            }
        }
        // Return to the starting state and compare byte-for-byte.
        LRESULT r = 0;
        SendSetDpi(startOverride, &r);
        LayoutSnapshot back = CaptureLayout();
        if (memcmp(&back, &start, sizeof(back)) != 0) {
            ++restoreMismatches;
            fail("round " + std::to_string(round) + " return-to-start layout not byte-identical");
        }
        if ((round & 7) == 0) PumpMessages(0);
    }
    const LayoutSnapshot end = CaptureLayout();
    const bool pass = rectFailures == 0 && clientFailures == 0 && protocolFailures == 0 && refMismatches == 0 &&
                      restoreMismatches == 0 && hitFailures == 0;
    result.Add("rounds", g.opt.rounds).Add("dpis", "72,96,120,144,192,240").Add("steps", steps)
        .Add("controlRectFailures", rectFailures).Add("clientSizeFailures", clientFailures)
        .Add("protocolFailures", protocolFailures).Add("roundReferenceMismatches", refMismatches)
        .Add("returnToStartMismatches", restoreMismatches)
        .Add("hitTestFailures", hitFailures).Add("windowFromPointMismatchesInformational", wfpMismatches)
        .Add("endLayoutByteIdenticalToStart", memcmp(&end, &start, sizeof(end)) == 0)
        .Add("snapshotBytes", static_cast<unsigned>(sizeof(LayoutSnapshot)))
        .Add("perDpi", perDpi).Add("hitTests", hitTests).Add("failures", failures);
    Finish(result, pass);
}

// ------------------------------------------------------------------------------------------------
void RunSynthetic() {
    json::Builder result;
    result.Add("test", "synthetic").Add("environment", Environment());
    HWND hwnd = g.hwnd;
    const UINT startOverride = g.dpi.Override();
    HDC dc = GetDC(hwnd);
    const UINT gdfwBefore = g_real.GetDpiForWindow(hwnd);
    const UINT gdfsBefore = g_real.GetDpiForSystem();
    const int lpxBefore = g_real.GetDeviceCaps(dc, LOGPIXELSX);
    const int lpyBefore = g_real.GetDeviceCaps(dc, LOGPIXELSY);
    ReleaseDC(hwnd, dc);
    const LayoutSnapshot before = CaptureLayout();
    RECT wrBefore{};
    GetWindowRect(hwnd, &wrBefore);

    const UINT synth = gdfwBefore == 144 ? 192 : 144;
    RECT suggested{0, 0, DpiProvider::Scale(kLogicalClientW, synth), DpiProvider::Scale(kLogicalClientH, synth)};
    AdjustWindowRectExForDpi(&suggested, kWindowStyle, TRUE, kWindowExStyle, gdfwBefore);
    OffsetRect(&suggested, wrBefore.left - suggested.left + 8, wrBefore.top - suggested.top + 8);  // move a little: position honoured?

    const size_t eventsBefore = g.dpiEvents.size();
    g.sendingSynthetic = true;
    LRESULT lr = SendMessageW(hwnd, WM_DPICHANGED, MAKEWPARAM(synth, synth), reinterpret_cast<LPARAM>(&suggested));
    g.sendingSynthetic = false;

    dc = GetDC(hwnd);
    const UINT gdfwAfter = g_real.GetDpiForWindow(hwnd);
    const UINT gdfsAfter = g_real.GetDpiForSystem();
    const int lpxAfter = g_real.GetDeviceCaps(dc, LOGPIXELSX);
    const int lpyAfter = g_real.GetDeviceCaps(dc, LOGPIXELSY);
    ReleaseDC(hwnd, dc);
    const LayoutSnapshot during = CaptureLayout();
    RECT wrDuring{};
    GetWindowRect(hwnd, &wrDuring);
    const LRESULT gd = SendGetDpi();
    int rectFailures = 0;
    for (int i = 0; i < kControlCount; ++i)
        if (!ControlMatches(i, during.controls[i], synth)) ++rectFailures;
    const bool eventRecorded = g.dpiEvents.size() == eventsBefore + 1 && g.dpiEvents.back().synthetic;
    int hitFailures = 0, wfp = 0;
    json::Builder hits = HitTest(synth, &hitFailures, &wfp);
    json::Builder duringSnapshot = Snapshot();

    // Restore the original state through the protocol and require a byte-identical layout.
    LRESULT r = 0;
    SendSetDpi(startOverride, &r);
    SetWindowPos(hwnd, nullptr, wrBefore.left, wrBefore.top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    const LayoutSnapshot after = CaptureLayout();
    RECT wrAfter{};
    GetWindowRect(hwnd, &wrAfter);

    json::Builder os;
    os.Add("getDpiForWindowBefore", gdfwBefore).Add("getDpiForWindowAfter", gdfwAfter)
        .Add("getDpiForSystemBefore", gdfsBefore).Add("getDpiForSystemAfter", gdfsAfter)
        .Add("logPixelsXBefore", lpxBefore).Add("logPixelsXAfter", lpxAfter)
        .Add("logPixelsYBefore", lpyBefore).Add("logPixelsYAfter", lpyAfter);
    const bool osUnchanged = gdfwBefore == gdfwAfter && gdfsBefore == gdfsAfter && lpxBefore == lpxAfter && lpyBefore == lpyAfter;
    const bool layoutFollowed = during.dpi == synth && rectFailures == 0 &&
                                during.client.right == DpiProvider::Scale(kLogicalClientW, synth) &&
                                during.client.bottom == DpiProvider::Scale(kLogicalClientH, synth);
    const bool positionHonoured = RectEq(wrDuring, suggested);
    const bool protocolReportsSynth = LOWORD(gd) == synth && HIWORD(gd) == gdfwBefore;
    const bool restored = memcmp(&after, &before, sizeof(after)) == 0 && RectEq(wrAfter, wrBefore);
    // Documented invariant for genuine notifications: "the current DPI for a window always equals
    // the last DPI sent by WM_DPICHANGED". A synthetic message necessarily breaks it.
    const bool docInvariantBroken = gdfwAfter != synth;

    json::Builder semantics;
    semantics.Add("genuine", "Sent by the OS after the window's monitor DPI changed; GetDpiForWindow already equals wParam inside "
                             "the handler; GetDpiForSystem is unaffected; the OS rescales non-client area, menu bar and "
                             "scrollbars itself; lParam is an OS-computed RECT (see --selftest-monitors for measurements)")
        .Add("synthetic", "Only our WndProc sees it; OS DPI state, non-client area and system-drawn parts are unchanged; "
                          "harness treats it as an in-process override request")
        .Add("docInvariantWindowDpiEqualsLastWmDpiChangedBroken", docInvariantBroken);

    const bool pass = lr == 0 && eventRecorded && osUnchanged && layoutFollowed && positionHonoured && protocolReportsSynth &&
                      restored && hitFailures == 0;
    result.Add("syntheticDpi", synth).Add("handlerReturn", static_cast<long long>(lr))
        .Add("suggestedRect", RectToJson(suggested)).Add("windowRectAfterSynthetic", RectToJson(wrDuring))
        .Add("eventRecordedAsSynthetic", eventRecorded)
        .Add("osState", os).Add("osStateUnchanged", osUnchanged)
        .Add("layoutFollowedSyntheticDpi", layoutFollowed).Add("controlRectFailures", rectFailures)
        .Add("suggestedPositionHonoured", positionHonoured)
        .Add("protocolGetDpi", json::Builder().Add("effective", LOWORD(gd)).Add("os", HIWORD(gd)))
        .Add("protocolReportsSyntheticAsEffective", protocolReportsSynth)
        .Add("hitTestFailures", hitFailures).Add("hitTests", hits)
        .Add("restoredByteIdentical", restored)
        .Add("layoutBefore", SnapJson(before)).Add("layoutDuring", SnapJson(during))
        .Add("snapshotDuring", duringSnapshot)
        .Add("semantics", semantics);
    Finish(result, pass);
}

// ------------------------------------------------------------------------------------------------
struct MonitorEntry {
    HMONITOR handle;
    RECT work;
    UINT dpi;
    std::wstring device;
};

void RunMonitors() {
    json::Builder result;
    result.Add("test", "monitors").Add("environment", Environment());
    HWND hwnd = g.hwnd;
    std::vector<MonitorEntry> mons;
    EnumDisplayMonitors(nullptr, nullptr,
                        [](HMONITOR m, HDC, LPRECT, LPARAM p) -> BOOL {
                            MONITORINFOEXW mi{};
                            mi.cbSize = sizeof(mi);
                            GetMonitorInfoW(m, &mi);
                            UINT x = 0, y = 0;
                            g_real.GetDpiForMonitor(m, MDT_EFFECTIVE_DPI, &x, &y);
                            reinterpret_cast<std::vector<MonitorEntry>*>(p)->push_back({m, mi.rcWork, x, mi.szDevice});
                            return TRUE;
                        },
                        reinterpret_cast<LPARAM>(&mons));
    std::set<UINT> distinct;
    for (auto& m : mons) distinct.insert(m.dpi);
    const bool mixed = distinct.size() > 1;
    const UINT overrideDpi = g.dpi.Override();
    RECT home{};
    GetWindowRect(hwnd, &home);

    // Visit every monitor, then return home.
    std::vector<size_t> order;
    for (size_t i = 0; i < mons.size(); ++i) order.push_back(i);
    HMONITOR homeMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    for (size_t i = 0; i < mons.size(); ++i)
        if (mons[i].handle == homeMon) order.push_back(i);

    json::Builder moves(true);
    int expectedTransitions = 0, observedTransitions = 0, failures = 0, spurious = 0, syntheticSeen = 0;
    for (size_t step = 0; step < order.size(); ++step) {
        const MonitorEntry& m = mons[order[step]];
        const UINT osBefore = g.dpi.OsDpi(hwnd), effBefore = g.dpi.Effective(hwnd);
        const LayoutSnapshot sBefore = CaptureLayout();
        RECT wrBefore{};
        GetWindowRect(hwnd, &wrBefore);
        const size_t evBefore = g.dpiEvents.size();
        SetWindowPos(hwnd, nullptr, m.work.left + 40, m.work.top + 40, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        PumpMessages(400);
        const UINT osAfter = g.dpi.OsDpi(hwnd), effAfter = g.dpi.Effective(hwnd);
        const LayoutSnapshot sAfter = CaptureLayout();
        RECT wrAfter{};
        GetWindowRect(hwnd, &wrAfter);
        HMONITOR landed = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        json::Builder evs(true);
        int genuine = 0;
        for (size_t e = evBefore; e < g.dpiEvents.size(); ++e) {
            const DpiChangedEvent& ev = g.dpiEvents[e];
            if (ev.synthetic) ++syntheticSeen;
            else ++genuine;
            json::Builder j;
            j.Add("synthetic", ev.synthetic).Add("wParamDpi", ev.wParamDpi).Add("suggested", RectToJson(ev.suggested))
                .Add("osDpiBefore", ev.osDpiBefore).Add("osDpiAfter", ev.osDpiAfter)
                .Add("effectiveBefore", ev.effectiveBefore).Add("effectiveAfter", ev.effectiveAfter)
                .Add("windowAfter", RectToJson(ev.windowAfter)).Add("tmHeightAfter", ev.tmHeightAfter);
            evs.Push(j);
        }
        const bool dpiDiffers = m.dpi != osBefore;
        bool ok = landed == m.handle;
        if (dpiDiffers) {
            ++expectedTransitions;
            if (genuine > 0) ++observedTransitions;
            ok = ok && genuine > 0 && osAfter == m.dpi && effAfter == (overrideDpi ? overrideDpi : m.dpi);
        } else {
            if (genuine > 0) ++spurious;
            ok = ok && effAfter == effBefore && sAfter.windowW == sBefore.windowW && sAfter.windowH == sBefore.windowH;
        }
        int rectFailures = 0;
        for (int i = 0; i < kControlCount; ++i)
            if (!ControlMatches(i, sAfter.controls[i], effAfter)) ++rectFailures;
        ok = ok && rectFailures == 0;
        if (!ok) ++failures;
        json::Builder mv;
        mv.Add("step", static_cast<unsigned>(step)).Add("target", m.device).Add("targetDpi", m.dpi)
            .Add("landedOnTarget", landed == m.handle)
            .Add("osDpiBefore", osBefore).Add("osDpiAfter", osAfter)
            .Add("effectiveBefore", effBefore).Add("effectiveAfter", effAfter)
            .Add("windowBefore", RectToJson(wrBefore)).Add("windowAfter", RectToJson(wrAfter))
            .Add("tmHeightBefore", sBefore.tmHeight).Add("tmHeightAfter", sAfter.tmHeight)
            .Add("clientAfterW", sAfter.client.right).Add("clientAfterH", sAfter.client.bottom)
            .Add("controlRectFailures", rectFailures)
            .Add("wmDpiChangedGenuine", genuine).Add("events", evs).Add("ok", ok);
        moves.Push(mv);
    }
    SetWindowPos(hwnd, nullptr, home.left, home.top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    PumpMessages(200);

    json::Builder dpis(true);
    for (UINT d : distinct) dpis.Push(d);
    const bool pass = failures == 0 && syntheticSeen == 0;
    result.Add("monitorCount", static_cast<unsigned>(mons.size())).Add("distinctEffectiveDpis", dpis)
        .Add("mixedDpi", mixed).Add("noMixedDpi", !mixed)
        .Add("overrideDpi", overrideDpi)
        .Add("expectedTransitions", expectedTransitions).Add("observedGenuineTransitions", observedTransitions)
        .Add("spuriousGenuineEvents", spurious).Add("moveFailures", failures)
        .Add("moves", moves)
        .Add("note", mixed ? "mixed-DPI monitors present: genuine WM_DPICHANGED transitions were exercised"
                           : "all monitors report the same effective DPI: no genuine WM_DPICHANGED transition can occur "
                             "on this machine without changing display settings (not done)");
    Finish(result, pass);
}

// ------------------------------------------------------------------------------------------------
// Detours: a "naive" code path that queries OS APIs directly, as an unmodified app would.
struct NaiveProbe {
    UINT window, system, monitor;
    int logPixelsX, logPixelsY;
    HRESULT monitorHr;
};

NaiveProbe ProbeNaive(HWND hwnd) {
    NaiveProbe p{};
    p.window = ::GetDpiForWindow(hwnd);
    p.system = ::GetDpiForSystem();
    HDC dc = GetDC(hwnd);
    p.logPixelsX = ::GetDeviceCaps(dc, LOGPIXELSX);
    p.logPixelsY = ::GetDeviceCaps(dc, LOGPIXELSY);
    ReleaseDC(hwnd, dc);
    UINT y = 0;
    p.monitorHr = ::GetDpiForMonitor(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), MDT_EFFECTIVE_DPI, &p.monitor, &y);
    return p;
}

json::Builder ProbeJson(const NaiveProbe& p) {
    json::Builder b;
    b.Add("GetDpiForWindow", p.window).Add("GetDpiForSystem", p.system).Add("GetDeviceCapsLOGPIXELSX", p.logPixelsX)
        .Add("GetDeviceCapsLOGPIXELSY", p.logPixelsY).Add("GetDpiForMonitor", p.monitor)
        .Add("GetDpiForMonitorHr", static_cast<long>(p.monitorHr));
    return b;
}

struct WorkerProbe {
    UINT windowOfHarness = 0, system = 0, windowOfWorker = 0;
};

DWORD WINAPI WorkerThread(void* param) {
    auto* w = static_cast<WorkerProbe*>(param);
    w->windowOfHarness = ::GetDpiForWindow(g.hwnd);
    w->system = ::GetDpiForSystem();
    HWND own = CreateWindowExW(0, L"STATIC", L"worker", WS_POPUP, 0, 0, 10, 10, nullptr, nullptr, g.instance, nullptr);
    w->windowOfWorker = own ? ::GetDpiForWindow(own) : 0;
    if (own) DestroyWindow(own);
    return 0;
}

json::Builder g_messageBoxMeasurement;
bool g_messageBoxMeasured = false;

BOOL CALLBACK FindMessageBox(HWND w, LPARAM p) {
    wchar_t cls[16]{};
    GetClassNameW(w, cls, 16);
    wchar_t title[64]{};
    GetWindowTextW(w, title, 64);
    if (wcscmp(cls, L"#32770") == 0 && IsWindowVisible(w) && wcscmp(title, L"AppScale MessageBox probe") == 0) {
        *reinterpret_cast<HWND*>(p) = w;
        return FALSE;
    }
    return TRUE;
}

// Keeps ticking until the box is gone: a posted IDOK is not always honoured on this OS build, so
// later ticks fall back to EndDialog (same thread as the MessageBox modal loop).
void CALLBACK MessageBoxTimer(HWND, UINT, UINT_PTR, DWORD) {
    HWND box = nullptr;
    EnumThreadWindows(GetCurrentThreadId(), FindMessageBox, reinterpret_cast<LPARAM>(&box));
    if (!box) return;
    if (!g_messageBoxMeasured) {
        g_messageBoxMeasurement = MeasureForeignWindow(box);
        g_messageBoxMeasured = true;
        PostMessageW(box, WM_COMMAND, IDOK, 0);
        return;
    }
    EndDialog(box, IDOK);
}

json::Builder MeasureSystemDialog() {
    g_messageBoxMeasured = false;
    UINT_PTR t = SetTimer(nullptr, 0, 250, MessageBoxTimer);
    MessageBoxW(g.hwnd, L"AppScale detours self-test: measuring a system MessageBox. It closes automatically.",
                L"AppScale MessageBox probe", MB_OK | MB_ICONINFORMATION);
    KillTimer(nullptr, t);
    json::Builder b = g_messageBoxMeasurement;
    if (!g_messageBoxMeasured) b.Add("measured", false);
    return b;
}

json::Builder TooltipFont(UINT os, UINT eff) {
    HWND tip = CreateWindowExW(WS_EX_TOPMOST, TOOLTIPS_CLASSW, nullptr, WS_POPUP | TTS_ALWAYSTIP, CW_USEDEFAULT, CW_USEDEFAULT,
                               CW_USEDEFAULT, CW_USEDEFAULT, g.hwnd, nullptr, g.instance, nullptr);
    HFONT f = tip ? reinterpret_cast<HFONT>(SendMessageW(tip, WM_GETFONT, 0, 0)) : nullptr;
    NONCLIENTMETRICSW ncOs{}, ncEff{};
    ncOs.cbSize = ncEff.cbSize = sizeof(NONCLIENTMETRICSW);
    SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, sizeof(ncOs), &ncOs, 0, os);
    SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, sizeof(ncEff), &ncEff, 0, eff);
    json::Builder b;
    b.Add("freshTooltipFontHeight", FontHeightOf(f)).Add("statusFontHeightForOsDpi", ncOs.lfStatusFont.lfHeight)
        .Add("statusFontHeightForEffectiveDpi", ncEff.lfStatusFont.lfHeight)
        .Add("followsEffective", FontHeightOf(f) == ncEff.lfStatusFont.lfHeight && os != eff);
    if (tip) DestroyWindow(tip);
    return b;
}

void RunDetours() {
    json::Builder result;
    result.Add("test", "detours").Add("environment", Environment());
    if (!g_native.loaded) {
        result.Add("skipped", true).Add("reason", g_native.status);
        Finish(result, false, 2);
        return;
    }
    HWND hwnd = g.hwnd;
    const UINT os = g.dpi.OsDpi(hwnd);
    const UINT eff = g.dpi.Effective(hwnd);
    result.Add("osDpi", os).Add("effectiveDpi", eff).Add("installMask", g_native.installMask)
        .Add("meaningful", os != eff);

    struct Phase { const char* name; UINT32 mask; };
    const Phase phases[] = {
        {"none", 0},
        {"GetDpiForWindow", APPSCALE_HOOK_GETDPIFORWINDOW},
        {"+GetDpiForSystem", APPSCALE_HOOK_GETDPIFORWINDOW | APPSCALE_HOOK_GETDPIFORSYSTEM},
        {"+GetDeviceCaps", APPSCALE_HOOK_GETDPIFORWINDOW | APPSCALE_HOOK_GETDPIFORSYSTEM | APPSCALE_HOOK_GETDEVICECAPS},
        {"+GetDpiForMonitor", APPSCALE_HOOK_RESEARCH_SET},
    };
    json::Builder phaseArr(true);
    int semanticFailures = 0;
    json::Builder failures(true);
    auto fail = [&](const std::string& s) {
        ++semanticFailures;
        failures.Push(s);
    };

    for (const Phase& ph : phases) {
        g_native.setEnabledMask(ph.mask);
        g_native.resetStats();
        // Exercise the UI the way a normal session would: relayout, a fresh tooltip, a dialog.
        ApplyLayout("detours-phase", nullptr);
        PumpMessages(100);
        json::Builder tooltip = TooltipFont(os, eff);
        json::Builder dlgPlain;
        ShowDiagnosticsDialog(hwnd, false, true, &dlgPlain);

        const NaiveProbe p = ProbeNaive(hwnd);
        const NaiveProbe real{g_real.GetDpiForWindow(hwnd), g_real.GetDpiForSystem(), 0, 0, 0, S_OK};
        // An unmodified app lays out with MulDiv(logical, GetDpiForWindow(hwnd), 96).
        int naiveFollows = 0;
        for (int i = 0; i < kControlCount; ++i) {
            const ControlSpec& c = kControls[i];
            RECT naive{DpiProvider::Scale(c.x, p.window), DpiProvider::Scale(c.y, p.window),
                       DpiProvider::Scale(c.x + c.w, p.window), DpiProvider::Scale(c.y + c.h, p.window)};
            RECT want = ExpectedControlRect(i, eff);
            if (naive.left == want.left && naive.top == want.top) ++naiveFollows;
        }
        const bool winOn = (ph.mask & APPSCALE_HOOK_GETDPIFORWINDOW) != 0;
        const bool sysOn = (ph.mask & APPSCALE_HOOK_GETDPIFORSYSTEM) != 0;
        const bool capsOn = (ph.mask & APPSCALE_HOOK_GETDEVICECAPS) != 0;
        const bool monOn = (ph.mask & APPSCALE_HOOK_GETDPIFORMONITOR) != 0;
        const std::string tag = std::string("phase ") + ph.name + ": ";
        if (p.window != (winOn ? eff : real.window)) fail(tag + "GetDpiForWindow=" + std::to_string(p.window));
        if (p.system != (sysOn ? eff : real.system)) fail(tag + "GetDpiForSystem=" + std::to_string(p.system));
        HDC dc = GetDC(hwnd);
        const int realLpx = g_real.GetDeviceCaps(dc, LOGPIXELSX);
        const int naiveHorzRes = ::GetDeviceCaps(dc, HORZRES), realHorzRes = g_real.GetDeviceCaps(dc, HORZRES);
        ReleaseDC(hwnd, dc);
        if (p.logPixelsX != (capsOn ? static_cast<int>(eff) : realLpx)) fail(tag + "LOGPIXELSX=" + std::to_string(p.logPixelsX));
        if (naiveHorzRes != realHorzRes) fail(tag + "HORZRES not passed through");
        UINT realMon = 0, dummy = 0;
        g_real.GetDpiForMonitor(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), MDT_EFFECTIVE_DPI, &realMon, &dummy);
        if (p.monitor != (monOn ? eff : realMon)) fail(tag + "GetDpiForMonitor=" + std::to_string(p.monitor));

        // Isolation and ABI/error behaviour.
        SetLastError(0xBEEF);
        const UINT nullDpi = ::GetDpiForWindow(nullptr);
        const DWORD nullErr = GetLastError();
        SetLastError(0);
        const UINT nullDpiReal = g_real.GetDpiForWindow(nullptr);
        const DWORD nullErrReal = GetLastError();
        if (nullDpi != nullDpiReal || nullErr != nullErrReal) fail(tag + "invalid-hwnd behaviour differs from OS");
        const UINT desktopNaive = ::GetDpiForWindow(GetDesktopWindow());
        const UINT desktopReal = g_real.GetDpiForWindow(GetDesktopWindow());
        if (desktopNaive != desktopReal) fail(tag + "foreign (desktop) window was virtualized");
        HWND shell = FindWindowW(L"Shell_TrayWnd", nullptr);
        const UINT shellNaive = shell ? ::GetDpiForWindow(shell) : 0;
        const UINT shellReal = shell ? g_real.GetDpiForWindow(shell) : 0;
        if (shellNaive != shellReal) fail(tag + "foreign (Shell_TrayWnd) window was virtualized");
        WorkerProbe wp{};
        if (HANDLE th = CreateThread(nullptr, 0, WorkerThread, &wp, 0, nullptr)) {
            WaitForSingleObject(th, 5000);
            CloseHandle(th);
        }
        if (wp.system != real.system) fail(tag + "GetDpiForSystem virtualized on a non-owner thread");

        json::Builder iso;
        iso.Add("invalidHwndResult", nullDpi).Add("invalidHwndLastError", nullErr)
            .Add("invalidHwndResultReal", nullDpiReal).Add("invalidHwndLastErrorReal", nullErrReal)
            .Add("desktopWindowNaive", desktopNaive).Add("desktopWindowReal", desktopReal)
            .Add("shellTrayNaive", shellNaive).Add("shellTrayReal", shellReal)
            .Add("workerThreadGetDpiForHarnessWindow", wp.windowOfHarness)
            .Add("workerThreadGetDpiForSystem", wp.system)
            .Add("workerThreadGetDpiForOwnWindow", wp.windowOfWorker)
            .Add("horzResNaive", naiveHorzRes).Add("horzResReal", realHorzRes);

        json::Builder pj;
        pj.Add("phase", ph.name).Add("enabledMask", ph.mask).Add("naive", ProbeJson(p))
            .Add("realGetDpiForWindow", real.window).Add("realGetDpiForSystem", real.system)
            .Add("realLogPixelsX", realLpx).Add("realMonitorDpi", realMon)
            .Add("naiveLayoutControlsMatchingEffective", naiveFollows)
            .Add("naiveLayoutFollowsEffective", naiveFollows == kControlCount)
            .Add("isolation", iso)
            .Add("tooltip", tooltip)
            .Add("templateDialogUncompensated", dlgPlain)
            .Raw("adapterStats", g_native.StatsJson());
        phaseArr.Push(pj);
        g.log.Write("detours.phase", pj);
    }

    // With every hook enabled: what still renders at the OS DPI?
    g_native.setEnabledMask(APPSCALE_HOOK_RESEARCH_SET);
    ApplyLayout("detours-os-owned", nullptr);
    PumpMessages(100);
    json::Builder osOwned;
    {
        RECT wr{};
        GetWindowRect(hwnd, &wr);
        POINT tl{0, 0};
        ClientToScreen(hwnd, &tl);
        MENUBARINFO mbi{};
        mbi.cbSize = sizeof(mbi);
        GetMenuBarInfo(hwnd, OBJID_MENU, 0, &mbi);
        SCROLLBARINFO sbi{};
        sbi.cbSize = sizeof(sbi);
        GetScrollBarInfo(g.controls[5], OBJID_VSCROLL, &sbi);
        const int menuH = mbi.rcBar.bottom - mbi.rcBar.top;
        const int sbW = sbi.rcScrollBar.right - sbi.rcScrollBar.left;
        json::Builder cap;
        cap.Add("GetSystemMetrics_SM_CYCAPTION", GetSystemMetrics(SM_CYCAPTION))
            .Add("ForDpi_os", GetSystemMetricsForDpi(SM_CYCAPTION, os))
            .Add("ForDpi_effective", GetSystemMetricsForDpi(SM_CYCAPTION, eff))
            .Add("measuredNonClientTopIncludingMenu", tl.y - wr.top);
        json::Builder menu;
        menu.Add("measuredMenuBarHeight", menuH).Add("SM_CYMENU_os", GetSystemMetricsForDpi(SM_CYMENU, os))
            .Add("SM_CYMENU_effective", GetSystemMetricsForDpi(SM_CYMENU, eff))
            .Add("followsEffective", menuH == GetSystemMetricsForDpi(SM_CYMENU, eff) && os != eff);
        json::Builder sb;
        sb.Add("measuredListVScrollWidth", sbW).Add("SM_CXVSCROLL_os", GetSystemMetricsForDpi(SM_CXVSCROLL, os))
            .Add("SM_CXVSCROLL_effective", GetSystemMetricsForDpi(SM_CXVSCROLL, eff))
            .Add("followsEffective", sbW == GetSystemMetricsForDpi(SM_CXVSCROLL, eff) && os != eff);
        json::Builder dlgPlain, dlgComp;
        ShowDiagnosticsDialog(hwnd, false, true, &dlgPlain);
        ShowDiagnosticsDialog(hwnd, true, true, &dlgComp);
        osOwned.Add("caption", cap).Add("menuBar", menu).Add("scrollBar", sb)
            .Add("tooltip", TooltipFont(os, eff))
            .Add("templateDialogUncompensated", dlgPlain)
            .Add("templateDialogCompensated", dlgComp)
            .Add("systemMessageBox", MeasureSystemDialog())
            .Add("snapshot", Snapshot());
    }
    result.Add("phases", phaseArr).Add("osOwnedWithAllHooks", osOwned)
        .Raw("finalAdapterStats", g_native.StatsJson())
        .Add("semanticFailures", semanticFailures).Add("failures", failures);
    Finish(result, semanticFailures == 0);
}

// ------------------------------------------------------------------------------------------------
void RunScreenshot() {
    json::Builder result;
    result.Add("test", "screenshot");
    PumpMessages(300);
    RedrawWindow(g.hwnd, nullptr, nullptr, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
    PumpMessages(100);
    UINT w = 0, h = 0;
    HRESULT hr = SaveClientAreaPng(g.hwnd, g.opt.screenshotPath, &w, &h);
    result.Add("path", g.opt.screenshotPath).Add("hr", static_cast<long>(hr)).Add("width", w).Add("height", h)
        .Add("osDpi", g.dpi.OsDpi(g.hwnd)).Add("effectiveDpi", g.dpi.Effective(g.hwnd))
        .Add("snapshot", Snapshot());
    Finish(result, SUCCEEDED(hr));
}

}  // namespace

bool WriteResultFile(const std::wstring& path, const std::string& text) {
    std::wstring dir = path.substr(0, path.find_last_of(L"\\/"));
    if (!dir.empty() && dir != path) {
        for (size_t i = 3; i <= dir.size(); ++i)
            if (i == dir.size() || dir[i] == L'\\' || dir[i] == L'/') CreateDirectoryW(dir.substr(0, i).c_str(), nullptr);
    }
    HANDLE f = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    BOOL ok = WriteFile(f, text.data(), static_cast<DWORD>(text.size()), &written, nullptr);
    CloseHandle(f);
    return ok && written == text.size();
}

void RunSelfTest() {
    switch (g.opt.mode) {
        case Mode::Drift: RunDrift(); break;
        case Mode::Synthetic: RunSynthetic(); break;
        case Mode::Monitors: RunMonitors(); break;
        case Mode::Detours: RunDetours(); break;
        case Mode::Screenshot: RunScreenshot(); break;
        default: break;
    }
}
