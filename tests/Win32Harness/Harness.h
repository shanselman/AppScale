// Win32Harness — shared declarations (Milestones 0 and 1, plus the Milestone 2 adapter loader).
#pragma once

#include <windows.h>

#include <string>
#include <vector>

#include "AppScaleNative.h"
#include "Json.h"

// ---------------------------------------------------------------------------------------------
// Real OS entry points. Default to the OS imports; when the experimental Detours adapter is
// loaded they are replaced by its trampolines so "OS DPI" is never polluted by our own hooks.
struct RealApis {
    UINT (WINAPI* GetDpiForWindow)(HWND) = nullptr;
    UINT (WINAPI* GetDpiForSystem)(void) = nullptr;
    int (WINAPI* GetDeviceCaps)(HDC, int) = nullptr;
    HRESULT (WINAPI* GetDpiForMonitor)(HMONITOR, int, UINT*, UINT*) = nullptr;
};
extern RealApis g_real;
void InitRealApis();

// ---------------------------------------------------------------------------------------------
// Injectable DPI provider: every layout decision goes through this, never GetDpiForWindow.
class DpiProvider {
public:
    static constexpr UINT kMinDpi = 72;
    static constexpr UINT kMaxDpi = 240;
    static bool IsValid(UINT dpi) { return dpi >= kMinDpi && dpi <= kMaxDpi; }
    // Always scales an ORIGINAL logical (96-DPI) value; never feed a previously scaled value back.
    static int Scale(int logical, UINT dpi) { return MulDiv(logical, static_cast<int>(dpi), 96); }

    UINT OsDpi(HWND hwnd) const;          // actual OS DPI of the window (real GetDpiForWindow)
    UINT Effective(HWND hwnd) const;      // override if active, else OS DPI
    UINT Override() const { return override_; }
    const std::wstring& OverrideSource() const { return source_; }
    bool SetOverride(UINT dpi, const wchar_t* source);
    void Reset();

private:
    UINT override_ = 0;
    std::wstring source_ = L"none";
};

// ---------------------------------------------------------------------------------------------
// Experimental Detours adapter (AppScale.Native.dll), loaded only when explicitly opted in.
struct NativeAdapter {
    HMODULE module = nullptr;
    bool requested = false;
    bool loaded = false;
    HRESULT initResult = E_NOT_SET;
    UINT32 installMask = 0;
    UINT32 enabledMask = 0;
    std::wstring status = L"not requested";
    PFN_AppScaleNative_SetEffectiveDpi setEffectiveDpi = nullptr;
    PFN_AppScaleNative_SetEnabledMask setEnabledMask = nullptr;
    PFN_AppScaleNative_GetStatsJson getStatsJson = nullptr;
    PFN_AppScaleNative_ResetStats resetStats = nullptr;
    PFN_AppScaleNative_Shutdown shutdown = nullptr;

    std::string StatsJson() const;
};
extern NativeAdapter g_native;
// Loads the adapter iff APPSCALE_EXPERIMENTAL_DETOURS=1 and APPSCALE_DPI is valid. Must be called
// before any window is created.
void TryLoadNativeAdapter(UINT startupDpi, bool forceInstallAll);
UINT32 ParseHookMask(const wchar_t* text);

// ---------------------------------------------------------------------------------------------
enum class Mode { Interactive, Drift, Synthetic, Monitors, Detours, Screenshot };

struct Options {
    Mode mode = Mode::Interactive;
    std::wstring logPath;
    std::wstring resultPath;
    std::wstring screenshotPath;
    int exitAfterMs = 0;
    int rounds = 50;
    // Behave like an ordinary PMv2 app: no protocol, no APPSCALE_DPI, no adapter; lay out from
    // GetDpiForWindow/WM_DPICHANGED only. Target for AppScale.Injector (milestone2-injection).
    bool noCooperative = false;
};

// Diagnostics only: the true OS DPI when AppScale.Native was injected into this process by
// AppScale.Injector (via its exported real-API trampolines). 0 when not injected.
UINT InjectedTrueOsDpi(HWND hwnd);
bool InjectedAdapterPresent();

class Logger {
public:
    bool Open(const std::wstring& path);
    void Write(const char* event, json::Builder payload);
    const std::wstring& Path() const { return path_; }
    ~Logger();

private:
    HANDLE file_ = INVALID_HANDLE_VALUE;
    std::wstring path_;
    LARGE_INTEGER freq_{}, start_{};
};

// ---------------------------------------------------------------------------------------------
// Logical (96-DPI) layout. Never mutated; every layout pass scales from these.
struct ControlSpec {
    int id;
    const wchar_t* className;
    const wchar_t* text;
    DWORD style;
    DWORD exStyle;
    int x, y, w, h;
    bool heightIsSystemDetermined;  // e.g. drop-down combo: closed height comes from the font
    const wchar_t* name;
};
constexpr int kControlCount = 9;
extern const ControlSpec kControls[kControlCount];
constexpr int kLogicalClientW = 720;
constexpr int kLogicalClientH = 340;
constexpr int kLogicalFontPx = 12;  // 9 pt at 96 DPI
// GDI drawing surface in client coordinates (logical).
constexpr RECT kLogicalCanvas = {10, 210, 410, 330};

#pragma pack(push, 4)
struct LayoutSnapshot {
    UINT dpi;
    LONG windowW, windowH;
    RECT client;
    RECT controls[kControlCount];
    LONG tmHeight, tmAveCharWidth;
};
#pragma pack(pop)

struct DpiChangedEvent {
    bool synthetic;
    UINT wParamDpi;
    RECT suggested;
    UINT osDpiBefore, osDpiAfter;
    UINT effectiveBefore, effectiveAfter;
    RECT windowAfter;
    LONG tmHeightAfter;
    DWORD tick;
};

struct App {
    HINSTANCE instance = nullptr;
    HWND hwnd = nullptr;
    HWND controls[kControlCount]{};
    HWND tooltip = nullptr;
    HFONT font = nullptr;
    HMENU menu = nullptr;
    DpiProvider dpi;
    Options opt;
    Logger log;
    UINT msgSetDpi = 0;
    UINT msgGetDpi = 0;
    bool sendingSynthetic = false;
    UINT lastOsDpi = 0;         // as of the last layout pass
    UINT lastEffectiveDpi = 0;  // as of the last layout pass
    int exitCode = 0;
    std::vector<DpiChangedEvent> dpiEvents;
    std::vector<std::string> getDpiScaledSizeLog;
};
extern App g;

constexpr UINT WM_APP_RUN_SELFTEST = WM_APP + 1;
constexpr DWORD kWindowStyle = WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN;
constexpr DWORD kWindowExStyle = 0;

// Harness.cpp
bool CreateHarnessWindow(int nCmdShow);
LayoutSnapshot ApplyLayout(const char* reason, const RECT* suggested);
LayoutSnapshot CaptureLayout();
RECT ExpectedControlRect(int index, UINT dpi);
json::Builder Snapshot();
void LogSnapshot(const char* event, const char* reason);
std::wstring AwarenessName(DPI_AWARENESS_CONTEXT ctx);
std::wstring DescribeMonitor(HMONITOR mon, UINT* effDpi = nullptr);
json::Builder MonitorJson(HMONITOR mon);
json::Builder RectToJson(const RECT& r);
void PumpMessages(DWORD ms);
bool SendSetDpi(UINT dpi, LRESULT* result);
LRESULT SendGetDpi();
// Modal dialog from an in-memory DLGTEMPLATE. compensate=true scales the template font by
// effective/OS DPI so the dialog follows the override; autoClose closes it after measuring.
INT_PTR ShowDiagnosticsDialog(HWND owner, bool compensateForOverride, bool autoClose, json::Builder* measurement);
json::Builder MeasureForeignWindow(HWND hwnd);
void ApplyOverride(UINT dpi, const wchar_t* source);
void UpdateDiagnosticsPanel();
LONG FontHeightOf(HFONT font);

// SelfTests.cpp
void RunSelfTest();
bool WriteResultFile(const std::wstring& path, const std::string& json);

// Screenshot.cpp
HRESULT SaveClientAreaPng(HWND hwnd, const std::wstring& path, UINT* width, UINT* height);
