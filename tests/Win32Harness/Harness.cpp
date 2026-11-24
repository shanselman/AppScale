// Win32Harness — window, cooperative protocol, layout from logical units, diagnostics.
#include "Harness.h"

#include <commctrl.h>
#include <shellscalingapi.h>

#include <cstdio>
#include <cwchar>

App g;
RealApis g_real;

namespace {

enum : int {
    IDC_LABEL = 100, IDC_EDIT, IDC_APPLY, IDC_CHECK, IDC_COMBO, IDC_LIST, IDC_STATUS, IDC_DIAGBTN, IDC_DIAGPANEL,
    IDM_EXIT = 1000, IDM_SCALE_75, IDM_SCALE_100, IDM_SCALE_125, IDM_SCALE_150, IDM_SCALE_200, IDM_SCALE_250,
    IDM_SCALE_RESET, IDM_DIAGNOSTICS, IDM_SYNTHETIC_144,
    kExitTimer = 1, kDialogAutoCloseTimer = 2,
};

struct ScaleMenuItem { int id; UINT dpi; const wchar_t* text; };
constexpr ScaleMenuItem kScaleItems[] = {
    {IDM_SCALE_75, 72, L"Scale &75% (72 DPI)"},     {IDM_SCALE_100, 96, L"Scale &100% (96 DPI)"},
    {IDM_SCALE_125, 120, L"Scale 1&25% (120 DPI)"}, {IDM_SCALE_150, 144, L"Scale &150% (144 DPI)"},
    {IDM_SCALE_200, 192, L"Scale &200% (192 DPI)"}, {IDM_SCALE_250, 240, L"Scale 2&50% (240 DPI)"},
};

}  // namespace

const ControlSpec kControls[kControlCount] = {
    {IDC_LABEL, L"STATIC", L"Name:", SS_LEFT | SS_CENTERIMAGE, 0, 10, 12, 80, 20, false, L"label"},
    {IDC_EDIT, L"EDIT", L"Hello AppScale", ES_AUTOHSCROLL | WS_TABSTOP, WS_EX_CLIENTEDGE, 95, 10, 200, 24, false, L"edit"},
    {IDC_APPLY, L"BUTTON", L"&Apply", BS_PUSHBUTTON | WS_TABSTOP, 0, 305, 9, 95, 28, false, L"button"},
    {IDC_CHECK, L"BUTTON", L"&Enable feature", BS_AUTOCHECKBOX | WS_TABSTOP, 0, 10, 46, 190, 24, false, L"checkbox"},
    {IDC_COMBO, L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_VSCROLL | WS_TABSTOP, 0, 210, 44, 190, 200, true, L"combobox"},
    {IDC_LIST, L"LISTBOX", L"", LBS_NOINTEGRALHEIGHT | LBS_DISABLENOSCROLL | WS_VSCROLL | WS_TABSTOP, WS_EX_CLIENTEDGE, 10, 80, 190, 120, false, L"listbox"},
    {IDC_STATUS, L"STATIC", L"", SS_LEFT, 0, 210, 80, 190, 80, false, L"status"},
    {IDC_DIAGBTN, L"BUTTON", L"&Diagnostics...", BS_PUSHBUTTON | WS_TABSTOP, 0, 210, 170, 190, 30, false, L"diagButton"},
    {IDC_DIAGPANEL, L"EDIT", L"", ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL | WS_VSCROLL, WS_EX_CLIENTEDGE, 420, 10, 290, 320, false, L"diagPanel"},
};

// ------------------------------------------------------------------------------------------------
void InitRealApis() {
    g_real.GetDpiForWindow = &::GetDpiForWindow;
    g_real.GetDpiForSystem = &::GetDpiForSystem;
    g_real.GetDeviceCaps = &::GetDeviceCaps;
    g_real.GetDpiForMonitor = reinterpret_cast<HRESULT(WINAPI*)(HMONITOR, int, UINT*, UINT*)>(&::GetDpiForMonitor);
}

static AppScaleNativeRealApis* InjectedRealApis() {
    static AppScaleNativeRealApis real{};
    static bool resolved = false;
    if (!resolved && !g_native.loaded) {
        HMODULE m = GetModuleHandleW(L"AppScale.Native.dll");
        auto get = m ? reinterpret_cast<PFN_AppScaleNative_GetRealApis>(GetProcAddress(m, "AppScaleNative_GetRealApis")) : nullptr;
        if (get && SUCCEEDED(get(&real)) && real.GetDpiForWindow) resolved = true;
    }
    return resolved ? &real : nullptr;
}

bool InjectedAdapterPresent() { return InjectedRealApis() != nullptr; }

UINT InjectedTrueOsDpi(HWND hwnd) {
    AppScaleNativeRealApis* r = InjectedRealApis();
    return r ? r->GetDpiForWindow(hwnd) : 0;
}

UINT DpiProvider::OsDpi(HWND hwnd) const {
    UINT d = hwnd ? g_real.GetDpiForWindow(hwnd) : 0;
    return d ? d : g_real.GetDpiForSystem();
}

UINT DpiProvider::Effective(HWND hwnd) const { return override_ ? override_ : OsDpi(hwnd); }

bool DpiProvider::SetOverride(UINT dpi, const wchar_t* source) {
    if (!IsValid(dpi)) return false;
    override_ = dpi;
    source_ = source;
    return true;
}

void DpiProvider::Reset() {
    override_ = 0;
    source_ = L"none";
}

// ------------------------------------------------------------------------------------------------
bool Logger::Open(const std::wstring& path) {
    std::wstring dir = path.substr(0, path.find_last_of(L"\\/"));
    if (!dir.empty() && dir != path) {
        for (size_t i = 3; i <= dir.size(); ++i)
            if (i == dir.size() || dir[i] == L'\\' || dir[i] == L'/') CreateDirectoryW(dir.substr(0, i).c_str(), nullptr);
    }
    file_ = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    path_ = path;
    QueryPerformanceFrequency(&freq_);
    QueryPerformanceCounter(&start_);
    return file_ != INVALID_HANDLE_VALUE;
}

void Logger::Write(const char* event, json::Builder payload) {
    if (file_ == INVALID_HANDLE_VALUE) return;
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    double ms = 1000.0 * static_cast<double>(now.QuadPart - start_.QuadPart) / static_cast<double>(freq_.QuadPart);
    json::Builder line;
    line.Add("tMs", ms).Add("pid", GetCurrentProcessId()).Add("event", event).Merge(payload);
    std::string s = line.Str() + "\n";
    DWORD written = 0;
    WriteFile(file_, s.data(), static_cast<DWORD>(s.size()), &written, nullptr);
}

Logger::~Logger() {
    if (file_ != INVALID_HANDLE_VALUE) CloseHandle(file_);
}

// ------------------------------------------------------------------------------------------------
std::wstring AwarenessName(DPI_AWARENESS_CONTEXT ctx) {
    if (ctx == nullptr) return L"null";
    struct { DPI_AWARENESS_CONTEXT c; const wchar_t* n; } names[] = {
        {DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, L"PerMonitorV2"},
        {DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE, L"PerMonitor"},
        {DPI_AWARENESS_CONTEXT_SYSTEM_AWARE, L"System"},
        {DPI_AWARENESS_CONTEXT_UNAWARE_GDISCALED, L"UnawareGdiScaled"},
        {DPI_AWARENESS_CONTEXT_UNAWARE, L"Unaware"},
    };
    for (auto& n : names)
        if (AreDpiAwarenessContextsEqual(ctx, n.c)) return n.n;
    return L"Unknown";
}

json::Builder RectToJson(const RECT& r) {
    json::Builder b;
    b.Add("left", r.left).Add("top", r.top).Add("right", r.right).Add("bottom", r.bottom)
        .Add("width", r.right - r.left).Add("height", r.bottom - r.top);
    return b;
}

json::Builder MonitorJson(HMONITOR mon) {
    json::Builder b;
    MONITORINFOEXW mi{};
    mi.cbSize = sizeof(mi);
    GetMonitorInfoW(mon, &mi);
    UINT ex = 0, ey = 0, ax = 0, ay = 0, rx = 0, ry = 0;
    HRESULT h1 = g_real.GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, &ex, &ey);
    g_real.GetDpiForMonitor(mon, MDT_ANGULAR_DPI, &ax, &ay);
    g_real.GetDpiForMonitor(mon, MDT_RAW_DPI, &rx, &ry);
    int mmW = 0, mmH = 0;
    if (HDC dc = CreateDCW(L"DISPLAY", mi.szDevice, nullptr, nullptr)) {
        mmW = GetDeviceCaps(dc, HORZSIZE);
        mmH = GetDeviceCaps(dc, VERTSIZE);
        DeleteDC(dc);
    }
    b.Raw("handle", json::Hex(reinterpret_cast<uint64_t>(mon)))
        .Add("device", mi.szDevice)
        .Add("primary", (mi.dwFlags & MONITORINFOF_PRIMARY) != 0)
        .Add("rect", RectToJson(mi.rcMonitor))
        .Add("work", RectToJson(mi.rcWork))
        .Add("effectiveDpi", ex)
        .Add("effectiveDpiHr", static_cast<long>(h1))
        .Add("angularDpi", ax)
        .Add("rawDpi", rx)
        .Add("scalePercent", ex * 100.0 / 96.0)
        .Add("physicalWidthMm", mmW)
        .Add("physicalHeightMm", mmH);
    return b;
}

std::wstring DescribeMonitor(HMONITOR mon, UINT* effDpi) {
    MONITORINFOEXW mi{};
    mi.cbSize = sizeof(mi);
    GetMonitorInfoW(mon, &mi);
    UINT x = 0, y = 0;
    g_real.GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, &x, &y);
    if (effDpi) *effDpi = x;
    wchar_t buf[256];
    swprintf_s(buf, L"%s %ldx%ld @ (%ld,%ld), %u DPI (%.0f%%)%s", mi.szDevice, mi.rcMonitor.right - mi.rcMonitor.left,
               mi.rcMonitor.bottom - mi.rcMonitor.top, mi.rcMonitor.left, mi.rcMonitor.top, x, x * 100.0 / 96.0,
               (mi.dwFlags & MONITORINFOF_PRIMARY) ? L" primary" : L"");
    return buf;
}

LONG FontHeightOf(HFONT font) {
    LOGFONTW lf{};
    if (font && GetObjectW(font, sizeof(lf), &lf)) return lf.lfHeight;
    return 0;
}

static json::Builder FontJson(HWND hwnd, HFONT font) {
    json::Builder b;
    HDC dc = GetDC(hwnd);
    HGDIOBJ old = SelectObject(dc, font ? font : GetStockObject(DEFAULT_GUI_FONT));
    TEXTMETRICW tm{};
    GetTextMetricsW(dc, &tm);
    wchar_t face[LF_FACESIZE]{};
    GetTextFaceW(dc, LF_FACESIZE, face);
    SIZE ext{};
    const wchar_t sample[] = L"The quick brown fox jumps over the lazy dog";
    GetTextExtentPoint32W(dc, sample, static_cast<int>(wcslen(sample)), &ext);
    SelectObject(dc, old);
    ReleaseDC(hwnd, dc);
    b.Add("face", face).Add("lfHeight", FontHeightOf(font)).Add("tmHeight", tm.tmHeight).Add("tmAscent", tm.tmAscent)
        .Add("tmDescent", tm.tmDescent).Add("tmInternalLeading", tm.tmInternalLeading)
        .Add("tmExternalLeading", tm.tmExternalLeading).Add("tmAveCharWidth", tm.tmAveCharWidth)
        .Add("tmMaxCharWidth", tm.tmMaxCharWidth).Add("sampleExtentCx", ext.cx).Add("sampleExtentCy", ext.cy);
    return b;
}

static json::Builder MetricsJson(UINT os, UINT eff) {
    struct { int idx; const char* name; } sm[] = {
        {SM_CYCAPTION, "SM_CYCAPTION"}, {SM_CYMENU, "SM_CYMENU"}, {SM_CXVSCROLL, "SM_CXVSCROLL"},
        {SM_CYHSCROLL, "SM_CYHSCROLL"}, {SM_CXFRAME, "SM_CXFRAME"}, {SM_CXPADDEDBORDER, "SM_CXPADDEDBORDER"},
        {SM_CXICON, "SM_CXICON"}, {SM_CXSMICON, "SM_CXSMICON"},
    };
    json::Builder b;
    for (auto& m : sm) {
        json::Builder e;
        e.Add("getSystemMetrics", GetSystemMetrics(m.idx))
            .Add("forOsDpi", GetSystemMetricsForDpi(m.idx, os))
            .Add("forEffectiveDpi", GetSystemMetricsForDpi(m.idx, eff));
        b.Add(m.name, e);
    }
    NONCLIENTMETRICSW ncOs{}, ncEff{};
    ncOs.cbSize = ncEff.cbSize = sizeof(NONCLIENTMETRICSW);
    BOOL okOs = SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, sizeof(ncOs), &ncOs, 0, os);
    BOOL okEff = SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, sizeof(ncEff), &ncEff, 0, eff);
    json::Builder spi;
    spi.Add("okOs", okOs != FALSE).Add("okEffective", okEff != FALSE)
        .Add("messageFontHeightOs", ncOs.lfMessageFont.lfHeight).Add("messageFontHeightEffective", ncEff.lfMessageFont.lfHeight)
        .Add("menuFontHeightOs", ncOs.lfMenuFont.lfHeight).Add("menuFontHeightEffective", ncEff.lfMenuFont.lfHeight)
        .Add("statusFontHeightOs", ncOs.lfStatusFont.lfHeight).Add("statusFontHeightEffective", ncEff.lfStatusFont.lfHeight)
        .Add("captionHeightOs", ncOs.iCaptionHeight).Add("captionHeightEffective", ncEff.iCaptionHeight)
        .Add("menuHeightOs", ncOs.iMenuHeight).Add("menuHeightEffective", ncEff.iMenuHeight)
        .Add("messageFontFace", ncOs.lfMessageFont.lfFaceName);
    b.Add("SystemParametersInfoForDpi_NONCLIENTMETRICS", spi);
    return b;
}

// Measures OS-owned non-client geometry that our layout cannot influence.
static json::Builder NonClientJson(HWND hwnd, UINT os, UINT eff) {
    json::Builder b;
    RECT wr{}, cr{};
    GetWindowRect(hwnd, &wr);
    GetClientRect(hwnd, &cr);
    POINT tl{0, 0};
    ClientToScreen(hwnd, &tl);
    MENUBARINFO mbi{};
    mbi.cbSize = sizeof(mbi);
    int menuH = GetMenuBarInfo(hwnd, OBJID_MENU, 0, &mbi) ? mbi.rcBar.bottom - mbi.rcBar.top : -1;
    RECT aOs{0, 0, 100, 100}, aEff{0, 0, 100, 100};
    AdjustWindowRectExForDpi(&aOs, kWindowStyle, FALSE, kWindowExStyle, os);
    AdjustWindowRectExForDpi(&aEff, kWindowStyle, FALSE, kWindowExStyle, eff);
    int ncTop = tl.y - wr.top;
    b.Add("measuredNonClientTop", ncTop).Add("measuredMenuBarHeight", menuH)
        .Add("measuredCaptionPlusFrame", menuH >= 0 ? ncTop - menuH : -1)
        .Add("adjustCaptionPlusFrameForOsDpi", -aOs.top).Add("adjustCaptionPlusFrameForEffectiveDpi", -aEff.top)
        .Add("menuHeightForOsDpi", GetSystemMetricsForDpi(SM_CYMENU, os))
        .Add("menuHeightForEffectiveDpi", GetSystemMetricsForDpi(SM_CYMENU, eff));
    if (HWND list = GetDlgItem(hwnd, IDC_LIST)) {
        SCROLLBARINFO sbi{};
        sbi.cbSize = sizeof(sbi);
        if (GetScrollBarInfo(list, OBJID_VSCROLL, &sbi))
            b.Add("measuredListVScrollWidth", sbi.rcScrollBar.right - sbi.rcScrollBar.left);
        b.Add("vscrollForOsDpi", GetSystemMetricsForDpi(SM_CXVSCROLL, os))
            .Add("vscrollForEffectiveDpi", GetSystemMetricsForDpi(SM_CXVSCROLL, eff));
    }
    return b;
}

json::Builder Snapshot() {
    HWND hwnd = g.hwnd;
    UINT os = g.dpi.OsDpi(hwnd), eff = g.dpi.Effective(hwnd);
    json::Builder b;
    DPI_AWARENESS_CONTEXT wctx = GetWindowDpiAwarenessContext(hwnd);
    DPI_AWARENESS_CONTEXT tctx = GetThreadDpiAwarenessContext();
    json::Builder aw;
    aw.Add("windowContext", AwarenessName(wctx))
        .Add("windowAwareness", static_cast<int>(GetAwarenessFromDpiAwarenessContext(wctx)))
        .Add("threadContext", AwarenessName(tctx))
        .Add("threadAwareness", static_cast<int>(GetAwarenessFromDpiAwarenessContext(tctx)))
        .Add("windowContextDpi", GetDpiFromDpiAwarenessContext(wctx));
    HDC dc = GetDC(hwnd);
    int lpx = g_real.GetDeviceCaps(dc, LOGPIXELSX), lpy = g_real.GetDeviceCaps(dc, LOGPIXELSY);
    ReleaseDC(hwnd, dc);
    HMONITOR mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    UINT mx = 0, my = 0;
    g_real.GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, &mx, &my);
    json::Builder sc;
    sc.Add("getDpiForWindow", g_real.GetDpiForWindow(hwnd))
        .Add("getDpiForSystem", g_real.GetDpiForSystem())
        .Add("deviceCapsLogPixelsX", lpx)
        .Add("deviceCapsLogPixelsY", lpy)
        .Add("monitorEffectiveDpi", mx)
        .Add("osDpi", os)
        .Add("effectiveDpi", eff)
        .Add("overrideDpi", g.dpi.Override())
        .Add("overrideSource", g.dpi.OverrideSource())
        .Add("effectiveScalePercent", eff * 100.0 / 96.0)
        .Add("relativeVisualZoom", static_cast<double>(eff) / static_cast<double>(os ? os : 96));
    RECT wr{}, cr{};
    GetWindowRect(hwnd, &wr);
    GetClientRect(hwnd, &cr);
    b.Raw("hwnd", json::Hex(reinterpret_cast<uint64_t>(hwnd)))
        .Add("awareness", aw)
        .Add("scale", sc)
        .Add("monitor", MonitorJson(mon))
        .Add("windowRect", RectToJson(wr))
        .Add("clientRect", RectToJson(cr))
        .Add("font", FontJson(hwnd, g.font))
        .Add("metrics", MetricsJson(os, eff))
        .Add("nonClient", NonClientJson(hwnd, os, eff))
        .Add("nativeAdapter", g_native.loaded)
        .Add("nonCooperative", g.opt.noCooperative)
        .Add("injectedAdapter", InjectedAdapterPresent())
        .Add("injectedTrueOsDpi", InjectedTrueOsDpi(hwnd));
    return b;
}

void LogSnapshot(const char* event, const char* reason) {
    json::Builder b;
    b.Add("reason", reason).Merge(Snapshot());
    g.log.Write(event, b);
}

// ------------------------------------------------------------------------------------------------
RECT ExpectedControlRect(int i, UINT dpi) {
    const ControlSpec& c = kControls[i];
    int x = DpiProvider::Scale(c.x, dpi), y = DpiProvider::Scale(c.y, dpi);
    return RECT{x, y, x + DpiProvider::Scale(c.w, dpi), y + DpiProvider::Scale(c.h, dpi)};
}

static HFONT CreateFontForDpi(UINT dpi) {
    LOGFONTW lf{};
    lf.lfHeight = -DpiProvider::Scale(kLogicalFontPx, dpi);
    lf.lfWeight = FW_NORMAL;
    lf.lfCharSet = DEFAULT_CHARSET;
    lf.lfQuality = CLEARTYPE_QUALITY;
    wcscpy_s(lf.lfFaceName, L"Segoe UI");
    return CreateFontIndirectW(&lf);
}

LayoutSnapshot CaptureLayout() {
    LayoutSnapshot s;
    memset(&s, 0, sizeof(s));
    s.dpi = g.dpi.Effective(g.hwnd);
    RECT wr{};
    GetWindowRect(g.hwnd, &wr);
    s.windowW = wr.right - wr.left;
    s.windowH = wr.bottom - wr.top;
    GetClientRect(g.hwnd, &s.client);
    for (int i = 0; i < kControlCount; ++i) {
        RECT r{};
        GetWindowRect(g.controls[i], &r);
        MapWindowPoints(HWND_DESKTOP, g.hwnd, reinterpret_cast<POINT*>(&r), 2);
        s.controls[i] = r;
    }
    HDC dc = GetDC(g.hwnd);
    HGDIOBJ old = SelectObject(dc, g.font);
    TEXTMETRICW tm{};
    GetTextMetricsW(dc, &tm);
    SelectObject(dc, old);
    ReleaseDC(g.hwnd, dc);
    s.tmHeight = tm.tmHeight;
    s.tmAveCharWidth = tm.tmAveCharWidth;
    return s;
}

static void UpdateStatusText(UINT os, UINT eff) {
    wchar_t buf[256];
    swprintf_s(buf, L"Effective: %u DPI (%.0f%%)\nOS window DPI: %u\nOverride: %s\nAdapter: %s", eff, eff * 100.0 / 96.0, os,
               g.dpi.Override() ? g.dpi.OverrideSource().c_str() : L"none (follows OS)", g_native.loaded ? L"Detours (experimental)" : L"off");
    SetWindowTextW(g.controls[6], buf);
    if (g.menu) {
        UINT checked = 0;
        for (auto& it : kScaleItems)
            if (g.dpi.Override() == it.dpi) checked = static_cast<UINT>(it.id);
        CheckMenuRadioItem(g.menu, IDM_SCALE_75, IDM_SCALE_RESET, checked ? checked : static_cast<UINT>(IDM_SCALE_RESET), MF_BYCOMMAND);
    }
}

LayoutSnapshot ApplyLayout(const char* reason, const RECT* suggested) {
    HWND hwnd = g.hwnd;
    const UINT os = g.dpi.OsDpi(hwnd);
    const UINT eff = g.dpi.Effective(hwnd);

    // 1. Fonts: recreated from the logical size every time.
    HFONT oldFont = g.font;
    g.font = CreateFontForDpi(eff);
    for (HWND c : g.controls) SendMessageW(c, WM_SETFONT, reinterpret_cast<WPARAM>(g.font), FALSE);
    if (g.tooltip) SendMessageW(g.tooltip, WM_SETFONT, reinterpret_cast<WPARAM>(g.font), FALSE);

    // 2. Child controls: MulDiv from ORIGINAL logical rectangles (no incremental scaling).
    HDWP dwp = BeginDeferWindowPos(kControlCount);
    for (int i = 0; i < kControlCount; ++i) {
        RECT r = ExpectedControlRect(i, eff);
        dwp = DeferWindowPos(dwp, g.controls[i], nullptr, r.left, r.top, r.right - r.left, r.bottom - r.top,
                             SWP_NOZORDER | SWP_NOACTIVATE);
    }
    EndDeferWindowPos(dwp);
    if (oldFont) DeleteObject(oldFont);
    if (g.tooltip) SendMessageW(g.tooltip, TTM_SETMAXTIPWIDTH, 0, DpiProvider::Scale(300, eff));

    // 3. Window: client size from logical units; non-client is drawn by the OS at the OS DPI.
    const int wantW = DpiProvider::Scale(kLogicalClientW, eff), wantH = DpiProvider::Scale(kLogicalClientH, eff);
    RECT adjOs{0, 0, wantW, wantH}, adjEff{0, 0, wantW, wantH};
    AdjustWindowRectExForDpi(&adjOs, kWindowStyle, TRUE, kWindowExStyle, os);
    AdjustWindowRectExForDpi(&adjEff, kWindowStyle, TRUE, kWindowExStyle, eff);
    int corrections = 0;
    if (!IsZoomed(hwnd) && !IsIconic(hwnd)) {
        if (suggested) {
            SetWindowPos(hwnd, nullptr, suggested->left, suggested->top, suggested->right - suggested->left,
                         suggested->bottom - suggested->top, SWP_NOZORDER | SWP_NOACTIVATE);
        } else {
            SetWindowPos(hwnd, nullptr, 0, 0, adjOs.right - adjOs.left, adjOs.bottom - adjOs.top,
                         SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOMOVE);
        }
        for (; corrections < 3; ++corrections) {
            RECT cr{}, wr{};
            GetClientRect(hwnd, &cr);
            if (cr.right == wantW && cr.bottom == wantH) break;
            GetWindowRect(hwnd, &wr);
            SetWindowPos(hwnd, nullptr, 0, 0, (wr.right - wr.left) + (wantW - cr.right), (wr.bottom - wr.top) + (wantH - cr.bottom),
                         SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOMOVE);
        }
    }
    InvalidateRect(hwnd, nullptr, TRUE);
    UpdateStatusText(os, eff);
    UpdateDiagnosticsPanel();

    LayoutSnapshot snap = CaptureLayout();
    g.lastOsDpi = os;
    g.lastEffectiveDpi = eff;
    json::Builder b;
    b.Add("reason", reason).Add("osDpi", os).Add("effectiveDpi", eff).Add("override", g.dpi.Override())
        .Add("wantClientW", wantW).Add("wantClientH", wantH)
        .Add("adjustWindowRectExForOsDpi", RectToJson(adjOs))
        .Add("adjustWindowRectExForEffectiveDpi", RectToJson(adjEff))
        .Add("sizeCorrections", corrections)
        .Add("clientW", snap.client.right).Add("clientH", snap.client.bottom)
        .Add("windowW", snap.windowW).Add("windowH", snap.windowH)
        .Add("tmHeight", snap.tmHeight)
        .Add("suggested", suggested != nullptr)
        .Add("injectedTrueOsDpi", InjectedTrueOsDpi(hwnd));
    g.log.Write("layout", b);
    return snap;
}

void ApplyOverride(UINT dpi, const wchar_t* source) {
    UINT before = g.dpi.Effective(g.hwnd);
    if (dpi == 0) g.dpi.Reset();
    else g.dpi.SetOverride(dpi, source);
    if (g_native.loaded && g_native.setEffectiveDpi) g_native.setEffectiveDpi(g.dpi.Override());
    json::Builder b;
    b.Add("requested", dpi).Add("source", source).Add("effectiveBefore", before);
    g.log.Write("override", b);
    ApplyLayout(dpi ? "override" : "reset", nullptr);
}

void UpdateDiagnosticsPanel() {
    if (!g.controls[8]) return;
    HWND hwnd = g.hwnd;
    UINT os = g.dpi.OsDpi(hwnd), eff = g.dpi.Effective(hwnd);
    HDC dc = GetDC(hwnd);
    int lpx = g_real.GetDeviceCaps(dc, LOGPIXELSX);
    ReleaseDC(hwnd, dc);
    RECT wr{}, cr{};
    GetWindowRect(hwnd, &wr);
    GetClientRect(hwnd, &cr);
    std::wstring text;
    wchar_t line[512];
    auto add = [&](const wchar_t* fmt, auto... args) {
        swprintf_s(line, fmt, args...);
        text += line;
        text += L"\r\n";
    };
    add(L"AWARENESS (how the OS treats us)");
    add(L"  window context: %s", AwarenessName(GetWindowDpiAwarenessContext(hwnd)).c_str());
    add(L"  thread context: %s", AwarenessName(GetThreadDpiAwarenessContext()).c_str());
    add(L"SCALE (what we lay out at)");
    add(L"  effective DPI: %u (%.0f%%)", eff, eff * 100.0 / 96.0);
    add(L"  override: %u [%s]", g.dpi.Override(), g.dpi.OverrideSource().c_str());
    add(L"  relative visual zoom: %.3f", static_cast<double>(eff) / os);
    add(L"OS STATE (real APIs)");
    add(L"  GetDpiForWindow: %u", g_real.GetDpiForWindow(hwnd));
    add(L"  GetDpiForSystem: %u", g_real.GetDpiForSystem());
    add(L"  GetDeviceCaps LOGPIXELSX: %d", lpx);
    add(L"  monitor: %s", DescribeMonitor(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)).c_str());
    add(L"GEOMETRY");
    add(L"  window: %ld,%ld %ldx%ld", wr.left, wr.top, wr.right - wr.left, wr.bottom - wr.top);
    add(L"  client: %ldx%ld", cr.right, cr.bottom);
    add(L"  font lfHeight: %ld", FontHeightOf(g.font));
    add(L"  SM_CYCAPTION sys/os/eff: %d/%d/%d", GetSystemMetrics(SM_CYCAPTION), GetSystemMetricsForDpi(SM_CYCAPTION, os),
        GetSystemMetricsForDpi(SM_CYCAPTION, eff));
    add(L"  WM_DPICHANGED seen: %zu", g.dpiEvents.size());
    add(L"  Detours adapter: %s", g_native.status.c_str());
    if (g.opt.noCooperative) add(L"  mode: NON-COOPERATIVE (no protocol, no APPSCALE_DPI)");
    if (InjectedAdapterPresent()) add(L"  injected AppScale.Native: true OS DPI %u", InjectedTrueOsDpi(hwnd));
    SetWindowTextW(g.controls[8], text.c_str());
}

// ------------------------------------------------------------------------------------------------
static void PaintCanvas(HDC dc) {
    const UINT dpi = g.dpi.Effective(g.hwnd);
    auto S = [dpi](int v) { return DpiProvider::Scale(v, dpi); };
    RECT c{S(kLogicalCanvas.left), S(kLogicalCanvas.top), S(kLogicalCanvas.right), S(kLogicalCanvas.bottom)};
    FillRect(dc, &c, static_cast<HBRUSH>(GetStockObject(WHITE_BRUSH)));
    HPEN grid = CreatePen(PS_SOLID, 1, RGB(220, 220, 235));
    HPEN ink = CreatePen(PS_SOLID, 1, RGB(0, 0, 0));
    HPEN blue = CreatePen(PS_SOLID, 1, RGB(0, 80, 200));
    HGDIOBJ oldPen = SelectObject(dc, grid);
    const int lw = kLogicalCanvas.right - kLogicalCanvas.left, lh = kLogicalCanvas.bottom - kLogicalCanvas.top;
    for (int x = 0; x <= lw; x += 10) {
        int px = c.left + S(x);
        MoveToEx(dc, px, c.top + S(16), nullptr);
        LineTo(dc, px, c.bottom);
    }
    for (int y = 16; y <= lh; y += 10) {
        int py = c.top + S(y);
        MoveToEx(dc, c.left, py, nullptr);
        LineTo(dc, c.right, py);
    }
    // Ruler along the top, ticks every 10 logical px, labels every 50.
    SelectObject(dc, ink);
    HGDIOBJ oldFont = SelectObject(dc, g.font);
    SetBkMode(dc, TRANSPARENT);
    for (int x = 0; x <= lw; x += 10) {
        int px = c.left + S(x);
        MoveToEx(dc, px, c.top, nullptr);
        LineTo(dc, px, c.top + S(x % 50 == 0 ? 14 : 6));
    }
    // One logical inch (96 logical px = effective DPI device px) box.
    SelectObject(dc, blue);
    SelectObject(dc, GetStockObject(NULL_BRUSH));
    RECT inch{c.left + S(10), c.top + S(26), c.left + S(10 + 96), c.top + S(26 + 48)};
    Rectangle(dc, inch.left, inch.top, inch.right, inch.bottom);
    RECT t1{inch.left + S(3), inch.top + S(3), inch.right, inch.bottom};
    DrawTextW(dc, L"1 logical inch", -1, &t1, DT_LEFT | DT_TOP | DT_SINGLELINE);
    RECT t2{c.left + S(120), c.top + S(26), c.right - S(4), c.top + S(60)};
    wchar_t buf[128];
    swprintf_s(buf, L"GDI text @ %u DPI: The quick brown fox 0123456789", dpi);
    DrawTextW(dc, buf, -1, &t2, DT_LEFT | DT_TOP | DT_WORDBREAK);
    // Device-pixel reference: alternating 1px lines (NOT scaled) to judge resampling blur.
    SelectObject(dc, ink);
    int bx = c.left + S(120), by = c.top + S(66);
    for (int i = 0; i < 40; i += 2) {
        MoveToEx(dc, bx + i, by, nullptr);
        LineTo(dc, bx + i, by + S(24));
    }
    RECT t3{bx + 44, by, c.right - S(4), by + S(24)};
    DrawTextW(dc, L"\x2190 1 device-px lines (unscaled)", -1, &t3, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
    SelectObject(dc, oldFont);
    SelectObject(dc, oldPen);
    SelectObject(dc, GetStockObject(BLACK_PEN));
    FrameRect(dc, &c, static_cast<HBRUSH>(GetStockObject(GRAY_BRUSH)));
    DeleteObject(grid);
    DeleteObject(ink);
    DeleteObject(blue);
}

// ------------------------------------------------------------------------------------------------
void PumpMessages(DWORD ms) {
    ULONGLONG end = GetTickCount64() + ms;
    for (;;) {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) {
                PostQuitMessage(static_cast<int>(msg.wParam));
                return;
            }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        ULONGLONG now = GetTickCount64();
        if (now >= end) return;
        MsgWaitForMultipleObjects(0, nullptr, FALSE, static_cast<DWORD>(end - now), QS_ALLINPUT);
    }
}

bool SendSetDpi(UINT dpi, LRESULT* result) {
    DWORD_PTR r = 0;
    BOOL ok = SendMessageTimeoutW(g.hwnd, g.msgSetDpi, dpi, 0, SMTO_ABORTIFHUNG, 2000, &r) != 0;
    if (result) *result = static_cast<LRESULT>(r);
    return ok;
}

LRESULT SendGetDpi() {
    DWORD_PTR r = 0;
    SendMessageTimeoutW(g.hwnd, g.msgGetDpi, 0, 0, SMTO_ABORTIFHUNG, 2000, &r);
    return static_cast<LRESULT>(r);
}

// ------------------------------------------------------------------------------------------------
json::Builder MeasureForeignWindow(HWND hwnd) {
    json::Builder b;
    wchar_t cls[64]{};
    GetClassNameW(hwnd, cls, 64);
    RECT wr{}, cr{};
    GetWindowRect(hwnd, &wr);
    GetClientRect(hwnd, &cr);
    POINT tl{0, 0};
    ClientToScreen(hwnd, &tl);
    HFONT f = reinterpret_cast<HFONT>(SendMessageW(hwnd, WM_GETFONT, 0, 0));
    // Message boxes keep the font on the text child (id 0xFFFF), dialogs on the dialog itself.
    HWND text = GetDlgItem(hwnd, 0xFFFF);
    HFONT tf = text ? reinterpret_cast<HFONT>(SendMessageW(text, WM_GETFONT, 0, 0)) : nullptr;
    b.Add("class", cls)
        .Add("realGetDpiForWindow", g_real.GetDpiForWindow(hwnd))
        .Add("appVisibleGetDpiForWindow", ::GetDpiForWindow(hwnd))
        .Add("awareness", AwarenessName(GetWindowDpiAwarenessContext(hwnd)))
        .Add("windowRect", RectToJson(wr))
        .Add("clientW", cr.right).Add("clientH", cr.bottom)
        .Add("nonClientTop", tl.y - wr.top)
        .Add("fontHeight", FontHeightOf(f))
        .Add("textChildFontHeight", FontHeightOf(tf));
    return b;
}

namespace {
struct DialogCtx {
    bool autoClose;
    json::Builder* measurement;
    UINT templatePointSize;
};

INT_PTR CALLBACK DiagDialogProc(HWND dlg, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
        case WM_INITDIALOG: {
            auto* ctx = reinterpret_cast<DialogCtx*>(lParam);
            SetWindowLongPtrW(dlg, DWLP_USER, lParam);
            wchar_t text[4096];
            GetWindowTextW(g.controls[8], text, 4096);
            SetDlgItemTextW(dlg, 200, text);
            json::Builder m = MeasureForeignWindow(dlg);
            m.Add("templatePointSize", ctx->templatePointSize);
            g.log.Write("dialog", m);
            if (ctx->measurement) *ctx->measurement = m;
            if (ctx->autoClose) SetTimer(dlg, kDialogAutoCloseTimer, 300, nullptr);
            return TRUE;
        }
        case WM_TIMER:
            if (wParam == kDialogAutoCloseTimer) {
                KillTimer(dlg, kDialogAutoCloseTimer);
                EndDialog(dlg, IDOK);
            }
            return TRUE;
        case WM_DPICHANGED: {
            json::Builder b;
            b.Add("wParamDpi", LOWORD(wParam));
            g.log.Write("dialogDpiChanged", b);
            return FALSE;  // let the dialog manager auto-scale (PMv2 dialogs)
        }
        case WM_COMMAND:
            if (LOWORD(wParam) == IDOK || LOWORD(wParam) == IDCANCEL) {
                EndDialog(dlg, LOWORD(wParam));
                return TRUE;
            }
            break;
    }
    return FALSE;
}

void AlignDword(std::vector<WORD>& t) {
    if (t.size() % 2) t.push_back(0);
}
void PushString(std::vector<WORD>& t, const wchar_t* s) {
    for (; *s; ++s) t.push_back(static_cast<WORD>(*s));
    t.push_back(0);
}
void PushDword(std::vector<WORD>& t, DWORD v) {
    t.push_back(LOWORD(v));
    t.push_back(HIWORD(v));
}
void PushItem(std::vector<WORD>& t, DWORD style, short x, short y, short cx, short cy, WORD id, WORD cls, const wchar_t* text) {
    AlignDword(t);
    PushDword(t, style | WS_CHILD | WS_VISIBLE);
    PushDword(t, 0);
    t.push_back(static_cast<WORD>(x));
    t.push_back(static_cast<WORD>(y));
    t.push_back(static_cast<WORD>(cx));
    t.push_back(static_cast<WORD>(cy));
    t.push_back(id);
    t.push_back(0xFFFF);
    t.push_back(cls);
    PushString(t, text);
    t.push_back(0);  // no creation data
}
}  // namespace

INT_PTR ShowDiagnosticsDialog(HWND owner, bool compensateForOverride, bool autoClose, json::Builder* measurement) {
    // The dialog manager scales DLGTEMPLATE fonts by the OS DPI of the dialog. To follow an
    // override we scale the template point size by effective/OS (a cooperative-app technique).
    UINT os = g.dpi.OsDpi(owner), eff = g.dpi.Effective(owner);
    UINT pt = compensateForOverride ? static_cast<UINT>(MulDiv(9, static_cast<int>(eff), static_cast<int>(os))) : 9u;
    std::vector<WORD> t;
    PushDword(t, DS_SETFONT | DS_MODALFRAME | DS_CENTER | WS_POPUP | WS_CAPTION | WS_SYSMENU);
    PushDword(t, 0);
    t.push_back(2);  // cdit
    t.push_back(0); t.push_back(0); t.push_back(280); t.push_back(200);
    t.push_back(0);  // menu
    t.push_back(0);  // class
    PushString(t, L"AppScale Win32Harness - Diagnostics");
    t.push_back(static_cast<WORD>(pt));
    PushString(t, L"Segoe UI");
    PushItem(t, ES_MULTILINE | ES_READONLY | WS_VSCROLL | WS_BORDER, 7, 7, 266, 164, 200, 0x0081, L"");
    PushItem(t, BS_DEFPUSHBUTTON | WS_TABSTOP, 223, 178, 50, 15, IDOK, 0x0080, L"OK");
    AlignDword(t);
    DialogCtx ctx{autoClose, measurement, pt};
    return DialogBoxIndirectParamW(g.instance, reinterpret_cast<LPCDLGTEMPLATEW>(t.data()), owner, DiagDialogProc,
                                   reinterpret_cast<LPARAM>(&ctx));
}

// ------------------------------------------------------------------------------------------------
static LRESULT OnDpiChanged(HWND hwnd, WPARAM wParam, LPARAM lParam) {
    const bool synthetic = g.sendingSynthetic;
    const RECT* suggested = reinterpret_cast<const RECT*>(lParam);
    DpiChangedEvent ev{};
    ev.synthetic = synthetic;
    ev.wParamDpi = LOWORD(wParam);
    ev.suggested = suggested ? *suggested : RECT{};
    ev.tick = GetTickCount();
    // For a genuine message the OS has already switched GetDpiForWindow to the new value.
    ev.osDpiAfter = g.dpi.OsDpi(hwnd);
    ev.effectiveBefore = g.lastEffectiveDpi;
    ev.osDpiBefore = g.lastOsDpi;
    if (synthetic) {
        // A synthetic message cannot change OS state; we treat it as an in-process override request.
        g.dpi.SetOverride(LOWORD(wParam), L"synthetic WM_DPICHANGED");
        if (g_native.loaded && g_native.setEffectiveDpi) g_native.setEffectiveDpi(LOWORD(wParam));
    }
    // Genuine + override: keep the absolute effective DPI, honour the suggested position.
    // (WM_GETDPISCALEDSIZE already sized the suggestion for the effective DPI.)
    ApplyLayout(synthetic ? "WM_DPICHANGED(synthetic)" : "WM_DPICHANGED(genuine)", suggested);
    GetWindowRect(hwnd, &ev.windowAfter);
    ev.effectiveAfter = g.dpi.Effective(hwnd);
    ev.tmHeightAfter = CaptureLayout().tmHeight;
    g.dpiEvents.push_back(ev);

    json::Builder b;
    b.Add("synthetic", synthetic).Add("wParamLoword", LOWORD(wParam)).Add("wParamHiword", HIWORD(wParam))
        .Add("suggestedRect", suggested ? RectToJson(*suggested) : json::Builder())
        .Add("getDpiForWindowInHandler", ev.osDpiAfter)
        .Add("getDpiForSystemInHandler", g_real.GetDpiForSystem())
        .Add("effectiveDpiAfter", ev.effectiveAfter)
        .Add("windowRectAfter", RectToJson(ev.windowAfter))
        .Add("tmHeightAfter", ev.tmHeightAfter)
        .Add("injectedTrueOsDpi", InjectedTrueOsDpi(hwnd));
    g.log.Write("WM_DPICHANGED", b);
    LogSnapshot("snapshot", synthetic ? "after synthetic WM_DPICHANGED" : "after genuine WM_DPICHANGED");
    return 0;
}

static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    if (msg == g.msgSetDpi && g.msgSetDpi != 0) {
        UINT dpi = static_cast<UINT>(wParam);
        bool ok = dpi == 0 || DpiProvider::IsValid(dpi);
        json::Builder b;
        b.Add("wParam", static_cast<unsigned long long>(wParam)).Add("accepted", ok);
        g.log.Write("protocol.SetDpi", b);
        if (!ok) return 0;
        ApplyOverride(dpi, L"AppScale.SetDpi.v1");
        return 1;
    }
    if (msg == g.msgGetDpi && g.msgGetDpi != 0) {
        return MAKELONG(g.dpi.Effective(hwnd), g.dpi.OsDpi(hwnd));
    }
    switch (msg) {
        case WM_DPICHANGED:
            return OnDpiChanged(hwnd, wParam, lParam);
        case WM_GETDPISCALEDSIZE: {
            if (g.opt.noCooperative) break;  // ordinary app: let the OS scale linearly
            // Called before a genuine WM_DPICHANGED: size the suggestion for the effective DPI
            // (client) plus non-client at the new OS DPI.
            UINT newOs = static_cast<UINT>(wParam);
            UINT eff = g.dpi.Override() ? g.dpi.Override() : newOs;
            RECT r{0, 0, DpiProvider::Scale(kLogicalClientW, eff), DpiProvider::Scale(kLogicalClientH, eff)};
            AdjustWindowRectExForDpi(&r, kWindowStyle, TRUE, kWindowExStyle, newOs);
            SIZE* sz = reinterpret_cast<SIZE*>(lParam);
            json::Builder b;
            b.Add("newOsDpi", newOs).Add("effectiveDpi", eff).Add("proposedCx", sz->cx).Add("proposedCy", sz->cy)
                .Add("ourCx", r.right - r.left).Add("ourCy", r.bottom - r.top);
            g.log.Write("WM_GETDPISCALEDSIZE", b);
            sz->cx = r.right - r.left;
            sz->cy = r.bottom - r.top;
            return TRUE;
        }
        case WM_GETMINMAXINFO: {
            auto* mmi = reinterpret_cast<MINMAXINFO*>(lParam);
            mmi->ptMaxTrackSize.x = 16000;  // allow the 250% layout on small monitors
            mmi->ptMaxTrackSize.y = 16000;
            return 0;
        }
        case WM_COMMAND: {
            int id = LOWORD(wParam);
            for (auto& it : kScaleItems)
                if (id == it.id) {
                    ApplyOverride(it.dpi, L"menu");
                    return 0;
                }
            switch (id) {
                case IDM_SCALE_RESET: ApplyOverride(0, L"menu"); return 0;
                case IDM_SYNTHETIC_144: {
                    RECT wr{};
                    GetWindowRect(hwnd, &wr);
                    g.sendingSynthetic = true;
                    SendMessageW(hwnd, WM_DPICHANGED, MAKEWPARAM(144, 144), reinterpret_cast<LPARAM>(&wr));
                    g.sendingSynthetic = false;
                    return 0;
                }
                case IDM_DIAGNOSTICS:
                case IDC_DIAGBTN: ShowDiagnosticsDialog(hwnd, true, false, nullptr); return 0;
                case IDM_EXIT: DestroyWindow(hwnd); return 0;
                case IDC_APPLY: LogSnapshot("snapshot", "Apply button"); return 0;
            }
            break;
        }
        case WM_PAINT: {
            PAINTSTRUCT ps;
            HDC dc = BeginPaint(hwnd, &ps);
            PaintCanvas(dc);
            EndPaint(hwnd, &ps);
            return 0;
        }
        case WM_CTLCOLORSTATIC:
            if (reinterpret_cast<HWND>(lParam) == g.controls[8]) {
                SetBkColor(reinterpret_cast<HDC>(wParam), GetSysColor(COLOR_WINDOW));
                return reinterpret_cast<LRESULT>(GetSysColorBrush(COLOR_WINDOW));
            }
            break;
        case WM_TIMER:
            if (wParam == kExitTimer) {
                KillTimer(hwnd, kExitTimer);
                g.log.Write("exitAfter", json::Builder());
                DestroyWindow(hwnd);
                return 0;
            }
            break;
        case WM_APP_RUN_SELFTEST:
            RunSelfTest();
            return 0;
        case WM_DESTROY:
            LogSnapshot("snapshot", "destroy");
            if (g.font) DeleteObject(g.font);
            g.font = nullptr;
            PostQuitMessage(g.exitCode);
            return 0;
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

static HMENU BuildMenu() {
    HMENU bar = CreateMenu();
    HMENU file = CreatePopupMenu();
    AppendMenuW(file, MF_STRING, IDM_EXIT, L"E&xit");
    HMENU view = CreatePopupMenu();
    for (auto& it : kScaleItems) AppendMenuW(view, MF_STRING, it.id, it.text);
    AppendMenuW(view, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(view, MF_STRING, IDM_SCALE_RESET, L"&Reset (follow OS DPI)");
    AppendMenuW(view, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(view, MF_STRING, IDM_SYNTHETIC_144, L"Send &synthetic WM_DPICHANGED (144)");
    HMENU help = CreatePopupMenu();
    AppendMenuW(help, MF_STRING, IDM_DIAGNOSTICS, L"&Diagnostics...");
    AppendMenuW(bar, MF_POPUP, reinterpret_cast<UINT_PTR>(file), L"&File");
    AppendMenuW(bar, MF_POPUP, reinterpret_cast<UINT_PTR>(view), L"&View");
    AppendMenuW(bar, MF_POPUP, reinterpret_cast<UINT_PTR>(help), L"&Help");
    return bar;
}

bool CreateHarnessWindow(int nCmdShow) {
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WndProc;
    wc.hInstance = g.instance;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
    wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_BTNFACE + 1);
    wc.lpszClassName = L"AppScale.Win32Harness";
    if (!RegisterClassExW(&wc)) return false;

    g.menu = BuildMenu();
    int x = CW_USEDEFAULT, y = CW_USEDEFAULT;
    if (g.opt.mode != Mode::Interactive) {
        MONITORINFO mi{};
        mi.cbSize = sizeof(mi);
        GetMonitorInfoW(MonitorFromPoint(POINT{0, 0}, MONITOR_DEFAULTTOPRIMARY), &mi);
        x = mi.rcWork.left + 40;
        y = mi.rcWork.top + 40;
    }
    wchar_t title[128];
    swprintf_s(title, L"AppScale Win32Harness (pid %lu)%s", GetCurrentProcessId(), g.opt.noCooperative ? L" (non-cooperative)" : L"");
    if (g.opt.noCooperative) {
        for (auto& it : kScaleItems) EnableMenuItem(g.menu, static_cast<UINT>(it.id), MF_BYCOMMAND | MF_GRAYED);
        EnableMenuItem(g.menu, IDM_SCALE_RESET, MF_BYCOMMAND | MF_GRAYED);
        EnableMenuItem(g.menu, IDM_SYNTHETIC_144, MF_BYCOMMAND | MF_GRAYED);
    }
    g.hwnd = CreateWindowExW(kWindowExStyle, wc.lpszClassName, title, kWindowStyle, x, y, 800, 500, nullptr, g.menu, g.instance, nullptr);
    if (!g.hwnd) return false;
    // Cooperative protocol v1 discovery property (docs/adr/0002-cooperative-protocol.md).
    if (!g.opt.noCooperative) SetPropW(g.hwnd, L"AppScale.Cooperative.v1", reinterpret_cast<HANDLE>(1));

    for (int i = 0; i < kControlCount; ++i) {
        const ControlSpec& c = kControls[i];
        g.controls[i] = CreateWindowExW(c.exStyle, c.className, c.text, c.style | WS_CHILD | WS_VISIBLE, c.x, c.y, c.w, c.h,
                                        g.hwnd, reinterpret_cast<HMENU>(static_cast<INT_PTR>(c.id)), g.instance, nullptr);
    }
    const wchar_t* combo[] = {L"75%", L"100%", L"125%", L"150%", L"200%", L"250%"};
    for (auto s : combo) SendMessageW(g.controls[4], CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(s));
    SendMessageW(g.controls[4], CB_SETCURSEL, 1, 0);
    for (int i = 1; i <= 30; ++i) {
        wchar_t item[64];
        swprintf_s(item, L"List item %02d - logical row", i);
        SendMessageW(g.controls[5], LB_ADDSTRING, 0, reinterpret_cast<LPARAM>(item));
    }

    g.tooltip = CreateWindowExW(WS_EX_TOPMOST, TOOLTIPS_CLASSW, nullptr, WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX, CW_USEDEFAULT,
                                CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, g.hwnd, nullptr, g.instance, nullptr);
    struct { int idx; const wchar_t* tip; } tips[] = {
        {2, L"Apply: logs a diagnostics snapshot"}, {3, L"A native checkbox (BS_AUTOCHECKBOX)"},
        {4, L"Combobox (CBS_DROPDOWNLIST)"}, {7, L"Opens a modal dialog built from an in-memory DLGTEMPLATE"},
    };
    for (auto& t : tips) {
        TTTOOLINFOW ti{};
        ti.cbSize = sizeof(ti);
        ti.uFlags = TTF_IDISHWND | TTF_SUBCLASS;
        ti.hwnd = g.hwnd;
        ti.uId = reinterpret_cast<UINT_PTR>(g.controls[t.idx]);
        ti.lpszText = const_cast<wchar_t*>(t.tip);
        SendMessageW(g.tooltip, TTM_ADDTOOLW, 0, reinterpret_cast<LPARAM>(&ti));
    }

    LogSnapshot("snapshot", "created (before layout)");
    ApplyLayout("startup", nullptr);
    ShowWindow(g.hwnd, g.opt.mode == Mode::Interactive ? nCmdShow : SW_SHOWNOACTIVATE);
    UpdateWindow(g.hwnd);
    LogSnapshot("snapshot", "shown");
    if (g.opt.exitAfterMs > 0) SetTimer(g.hwnd, kExitTimer, static_cast<UINT>(g.opt.exitAfterMs), nullptr);
    return true;
}
