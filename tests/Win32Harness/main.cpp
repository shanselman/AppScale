// Win32Harness — entry point: argument parsing, APPSCALE_DPI, adapter opt-in, message loop.
#include "Harness.h"

#include <commctrl.h>
#include <shellapi.h>
#include <objbase.h>

#include <cwctype>

namespace {

void Usage() {
    MessageBoxW(nullptr,
                L"Win32Harness [--log <path>] [--result <path>] [--exit-after <ms>] [--rounds <n>] [--no-cooperative]\n"
                L"             [--selftest-drift | --selftest-synthetic | --selftest-monitors |\n"
                L"              --selftest-detours | --screenshot <path.png>]\n\n"
                L"Environment: APPSCALE_DPI=72..240, APPSCALE_EXPERIMENTAL_DETOURS=1,\n"
                L"             APPSCALE_DETOURS_HOOKS=all|none|GetDpiForWindow,GetDpiForSystem,GetDeviceCaps,GetDpiForMonitor",
                L"AppScale Win32Harness", MB_OK | MB_ICONINFORMATION);
}

bool ParseArgs(Options& o) {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return false;
    bool ok = true;
    for (int i = 1; i < argc && ok; ++i) {
        std::wstring a = argv[i];
        auto next = [&](std::wstring& out) {
            if (i + 1 >= argc) return ok = false;
            out = argv[++i];
            return true;
        };
        std::wstring v;
        if (a == L"--log") next(o.logPath);
        else if (a == L"--result") next(o.resultPath);
        else if (a == L"--screenshot") { if (next(o.screenshotPath)) o.mode = Mode::Screenshot; }
        else if (a == L"--exit-after") { if (next(v)) o.exitAfterMs = _wtoi(v.c_str()); }
        else if (a == L"--rounds") { if (next(v)) o.rounds = _wtoi(v.c_str()); }
        else if (a == L"--selftest-drift") o.mode = Mode::Drift;
        else if (a == L"--selftest-synthetic") o.mode = Mode::Synthetic;
        else if (a == L"--selftest-monitors") o.mode = Mode::Monitors;
        else if (a == L"--selftest-detours") o.mode = Mode::Detours;
        else if (a == L"--no-cooperative") o.noCooperative = true;
        else if (a == L"--help" || a == L"-h" || a == L"/?") ok = false;
        else ok = false;
    }
    LocalFree(argv);
    if (o.rounds < 1) o.rounds = 1;
    // Self-tests exercise the protocol/provider, so they are meaningless for an ordinary app.
    if (o.noCooperative && o.mode != Mode::Interactive && o.mode != Mode::Screenshot) ok = false;
    return ok;
}

// Strictly digits, within the protocol's 72..240 range; anything else is ignored (logged).
UINT ReadStartupDpi(std::wstring* raw) {
    wchar_t buf[32]{};
    DWORD n = GetEnvironmentVariableW(L"APPSCALE_DPI", buf, 32);
    if (n == 0 || n >= 32) return 0;
    *raw = buf;
    for (DWORD i = 0; i < n; ++i)
        if (!iswdigit(buf[i])) return 0;
    UINT v = static_cast<UINT>(_wtoi(buf));
    return DpiProvider::IsValid(v) ? v : 0;
}

}  // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, LPWSTR, int nCmdShow) {
    g.instance = instance;
    if (!ParseArgs(g.opt)) {
        Usage();
        return 2;
    }
    if (g.opt.logPath.empty()) {
        wchar_t local[MAX_PATH]{};
        GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
        g.opt.logPath = std::wstring(local) + L"\\AppScale\\logs\\win32harness-" + std::to_wstring(GetCurrentProcessId()) + L".jsonl";
    }
    g.log.Open(g.opt.logPath);
    InitRealApis();

    std::wstring raw;
    UINT startupDpi = g.opt.noCooperative ? 0 : ReadStartupDpi(&raw);
    if (startupDpi) g.dpi.SetOverride(startupDpi, L"APPSCALE_DPI");

    // The experimental adapter must be attached before any window exists.
    if (!g.opt.noCooperative) TryLoadNativeAdapter(startupDpi, g.opt.mode == Mode::Detours);
    else g_native.status = L"off (--no-cooperative)";

    json::Builder start;
    start.Add("commandLine", GetCommandLineW())
        .Add("mode", static_cast<int>(g.opt.mode))
        .Add("appscaleDpiRaw", raw)
        .Add("appscaleDpiAccepted", startupDpi)
        .Add("processAwareness", AwarenessName(GetThreadDpiAwarenessContext()))
        .Add("getDpiForSystem", g_real.GetDpiForSystem())
        .Add("nativeAdapterRequested", g_native.requested)
        .Add("nativeAdapterLoaded", g_native.loaded)
        .Add("nativeAdapterStatus", g_native.status)
        .Add("nonCooperative", g.opt.noCooperative)
        .Add("injectedAdapterAtStart", InjectedAdapterPresent())
        .Add("appVisibleGetDpiForSystem", GetDpiForSystem());
    g.log.Write("start", start);

    HRESULT coHr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
    INITCOMMONCONTROLSEX icc{sizeof(icc), ICC_STANDARD_CLASSES | ICC_WIN95_CLASSES};
    InitCommonControlsEx(&icc);
    if (!g.opt.noCooperative) {
        g.msgSetDpi = RegisterWindowMessageW(L"AppScale.SetDpi.v1");
        g.msgGetDpi = RegisterWindowMessageW(L"AppScale.GetDpi.v1");
    }

    if (!CreateHarnessWindow(nCmdShow)) {
        g.log.Write("error", json::Builder().Add("what", "CreateHarnessWindow failed").Add("lastError", GetLastError()));
        return 3;
    }
    if (g.opt.mode != Mode::Interactive) PostMessageW(g.hwnd, WM_APP_RUN_SELFTEST, 0, 0);

    MSG msg;
    int exitCode = 0;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    exitCode = (msg.message == WM_QUIT) ? static_cast<int>(msg.wParam) : g.exitCode;
    if (g_native.loaded && g_native.shutdown) g_native.shutdown();
    g.log.Write("exit", json::Builder().Add("exitCode", exitCode));
    if (SUCCEEDED(coHr)) CoUninitialize();
    return exitCode;
}
