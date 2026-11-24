// Win32Harness — loader for the EXPERIMENTAL AppScale.Native Detours adapter (Milestone 2).
#include "Harness.h"

#include <cwctype>

NativeAdapter g_native;

UINT32 ParseHookMask(const wchar_t* text) {
    if (text == nullptr || *text == 0) return APPSCALE_HOOK_GETDPIFORWINDOW;
    std::wstring s = text;
    for (auto& c : s) c = static_cast<wchar_t>(towlower(c));
    // "all" keeps the Milestone 2 research set (4 query APIs) so evidence stays comparable.
    if (s == L"all") return APPSCALE_HOOK_RESEARCH_SET;
    if (s == L"everything") return APPSCALE_HOOK_ALL;
    if (s == L"none") return 0;
    UINT32 mask = 0;
    size_t start = 0;
    while (start <= s.size()) {
        size_t end = s.find(L',', start);
        std::wstring tok = s.substr(start, end == std::wstring::npos ? std::wstring::npos : end - start);
        if (tok == L"getdpiforwindow") mask |= APPSCALE_HOOK_GETDPIFORWINDOW;
        else if (tok == L"getdpiforsystem") mask |= APPSCALE_HOOK_GETDPIFORSYSTEM;
        else if (tok == L"getdevicecaps") mask |= APPSCALE_HOOK_GETDEVICECAPS;
        else if (tok == L"getdpiformonitor") mask |= APPSCALE_HOOK_GETDPIFORMONITOR;
        else if (tok == L"getsystemdpiforprocess") mask |= APPSCALE_HOOK_GETSYSTEMDPIFORPROCESS;
        else if (tok == L"getsystemmetrics") mask |= APPSCALE_HOOK_GETSYSTEMMETRICS;
        else if (tok == L"systemparametersinfo") mask |= APPSCALE_HOOK_SYSTEMPARAMETERSINFO;
        if (end == std::wstring::npos) break;
        start = end + 1;
    }
    return mask;
}

std::string NativeAdapter::StatsJson() const {
    if (!loaded || !getStatsJson) return "null";
    UINT32 needed = 0;
    getStatsJson(nullptr, 0, &needed);
    std::string buf(needed, '\0');
    if (FAILED(getStatsJson(buf.data(), needed, &needed))) return "null";
    buf.resize(needed ? needed - 1 : 0);
    return buf;
}

void TryLoadNativeAdapter(UINT startupDpi, bool forceInstallAll) {
    wchar_t flag[16]{};
    GetEnvironmentVariableW(L"APPSCALE_EXPERIMENTAL_DETOURS", flag, 16);
    if (wcscmp(flag, L"1") != 0) {
        g_native.status = L"not requested (APPSCALE_EXPERIMENTAL_DETOURS != 1)";
        return;
    }
    g_native.requested = true;
    if (!DpiProvider::IsValid(startupDpi)) {
        g_native.status = L"refused: APPSCALE_DPI missing or outside 72..240";
        return;
    }
    // Load only from our own directory with a full path (no DLL search-order exposure).
    wchar_t path[MAX_PATH];
    DWORD n = GetModuleFileNameW(nullptr, path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) {
        g_native.status = L"failed: GetModuleFileName";
        return;
    }
    wchar_t* slash = wcsrchr(path, L'\\');
    if (slash) *(slash + 1) = 0;
    wcscat_s(path, L"AppScale.Native.dll");
    g_native.module = LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!g_native.module) {
        g_native.status = L"failed: LoadLibrary error " + std::to_wstring(GetLastError());
        return;
    }
    auto init = reinterpret_cast<PFN_AppScaleNative_Initialize>(GetProcAddress(g_native.module, "AppScaleNative_Initialize"));
    auto getReal = reinterpret_cast<PFN_AppScaleNative_GetRealApis>(GetProcAddress(g_native.module, "AppScaleNative_GetRealApis"));
    g_native.setEffectiveDpi = reinterpret_cast<PFN_AppScaleNative_SetEffectiveDpi>(GetProcAddress(g_native.module, "AppScaleNative_SetEffectiveDpi"));
    g_native.setEnabledMask = reinterpret_cast<PFN_AppScaleNative_SetEnabledMask>(GetProcAddress(g_native.module, "AppScaleNative_SetEnabledMask"));
    g_native.getStatsJson = reinterpret_cast<PFN_AppScaleNative_GetStatsJson>(GetProcAddress(g_native.module, "AppScaleNative_GetStatsJson"));
    g_native.resetStats = reinterpret_cast<PFN_AppScaleNative_ResetStats>(GetProcAddress(g_native.module, "AppScaleNative_ResetStats"));
    g_native.shutdown = reinterpret_cast<PFN_AppScaleNative_Shutdown>(GetProcAddress(g_native.module, "AppScaleNative_Shutdown"));
    if (!init || !getReal || !g_native.setEffectiveDpi || !g_native.setEnabledMask || !g_native.getStatsJson ||
        !g_native.resetStats || !g_native.shutdown) {
        g_native.status = L"failed: missing exports";
        return;
    }
    wchar_t hooks[256]{};
    GetEnvironmentVariableW(L"APPSCALE_DETOURS_HOOKS", hooks, 256);
    UINT32 requestedMask = ParseHookMask(hooks);
    AppScaleNativeConfig cfg{};
    cfg.cbSize = sizeof(cfg);
    cfg.apiVersion = APPSCALE_NATIVE_API_VERSION;
    cfg.effectiveDpi = startupDpi;
    cfg.installMask = forceInstallAll ? (APPSCALE_HOOK_RESEARCH_SET | requestedMask) : requestedMask;
    cfg.enabledMask = requestedMask;
    cfg.ownerThreadId = GetCurrentThreadId();
    g_native.initResult = init(&cfg);
    if (FAILED(g_native.initResult)) {
        wchar_t b[64];
        swprintf_s(b, L"failed: Initialize hr=0x%08lX", static_cast<unsigned long>(g_native.initResult));
        g_native.status = b;
        return;
    }
    AppScaleNativeRealApis real{};
    if (SUCCEEDED(getReal(&real))) {
        g_real.GetDpiForWindow = real.GetDpiForWindow;
        g_real.GetDpiForSystem = real.GetDpiForSystem;
        g_real.GetDeviceCaps = real.GetDeviceCaps;
        if (real.GetDpiForMonitor) g_real.GetDpiForMonitor = real.GetDpiForMonitor;
    }
    g_native.installMask = cfg.installMask;
    g_native.enabledMask = cfg.enabledMask & cfg.installMask;
    g_native.loaded = true;
    wchar_t b[128];
    swprintf_s(b, L"loaded (installed 0x%X, enabled 0x%X, effective %u DPI)", g_native.installMask, g_native.enabledMask, startupDpi);
    g_native.status = b;
}
