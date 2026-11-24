// AppScale.Native — Detours-based DPI API virtualization.
//
// Two ways to activate it, both strictly process-local (Detours patches code only in the process
// that loaded the DLL; there are no global hooks):
//  1. Explicit: a test executable (Win32Harness) LoadLibrary()s it and calls AppScaleNative_Initialize.
//  2. Injected: AppScale.Injector.exe loads it into a user-chosen, same-user, non-elevated x64
//     process and publishes a per-process section (AppScaleInjection.h). DllMain then installs all
//     hooks process-wide and makes the target speak cooperative protocol v1.
// See docs/evidence/milestone2-detours.md and docs/evidence/milestone2-injection.md.
#pragma once

#include <windows.h>

#ifdef __cplusplus
extern "C" {
#endif

#define APPSCALE_NATIVE_API_VERSION 1u

// Individually toggleable interception points.
#define APPSCALE_HOOK_GETDPIFORWINDOW         0x01u  // user32!GetDpiForWindow
#define APPSCALE_HOOK_GETDPIFORSYSTEM         0x02u  // user32!GetDpiForSystem
#define APPSCALE_HOOK_GETDEVICECAPS           0x04u  // gdi32!GetDeviceCaps (LOGPIXELSX/Y only)
#define APPSCALE_HOOK_GETDPIFORMONITOR        0x08u  // shcore!GetDpiForMonitor (MDT_EFFECTIVE_DPI only)
#define APPSCALE_HOOK_GETSYSTEMDPIFORPROCESS  0x10u  // user32!GetSystemDpiForProcess (own process only)
#define APPSCALE_HOOK_GETSYSTEMMETRICS        0x20u  // user32!GetSystemMetrics -> GetSystemMetricsForDpi
#define APPSCALE_HOOK_SYSTEMPARAMETERSINFO    0x40u  // user32!SystemParametersInfoW (3 font/metric actions)
// The four APIs evaluated by the Milestone 2 harness research (docs/evidence/milestone2-detours.md).
#define APPSCALE_HOOK_RESEARCH_SET            0x0Fu
#define APPSCALE_HOOK_ALL                     0x7Fu

typedef struct AppScaleNativeConfig {
    UINT32 cbSize;         // sizeof(AppScaleNativeConfig)
    UINT32 apiVersion;     // APPSCALE_NATIVE_API_VERSION
    UINT32 effectiveDpi;   // 72..240; 0 = pass everything through
    UINT32 installMask;    // hooks to attach (APPSCALE_HOOK_*)
    UINT32 enabledMask;    // hooks that actively virtualize (subset of installMask)
    DWORD  ownerThreadId;  // only windows/calls on this UI thread are virtualized (0 = whole process)
} AppScaleNativeConfig;

// Pointers that always reach the real OS implementation (trampolines when hooked).
typedef struct AppScaleNativeRealApis {
    UINT    (WINAPI* GetDpiForWindow)(HWND);
    UINT    (WINAPI* GetDpiForSystem)(void);
    int     (WINAPI* GetDeviceCaps)(HDC, int);
    HRESULT (WINAPI* GetDpiForMonitor)(HMONITOR, int, UINT*, UINT*);
} AppScaleNativeRealApis;

typedef HRESULT (WINAPI* PFN_AppScaleNative_Initialize)(const AppScaleNativeConfig* config);
typedef HRESULT (WINAPI* PFN_AppScaleNative_SetEffectiveDpi)(UINT32 dpi);
typedef HRESULT (WINAPI* PFN_AppScaleNative_SetEnabledMask)(UINT32 mask);
typedef HRESULT (WINAPI* PFN_AppScaleNative_GetRealApis)(AppScaleNativeRealApis* apis);
// UTF-8 JSON statistics. Returns S_OK, or HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER) with *needed set.
typedef HRESULT (WINAPI* PFN_AppScaleNative_GetStatsJson)(char* buffer, UINT32 cb, UINT32* needed);
typedef HRESULT (WINAPI* PFN_AppScaleNative_ResetStats)(void);
typedef HRESULT (WINAPI* PFN_AppScaleNative_Shutdown)(void);

HRESULT WINAPI AppScaleNative_Initialize(const AppScaleNativeConfig* config);
HRESULT WINAPI AppScaleNative_SetEffectiveDpi(UINT32 dpi);
HRESULT WINAPI AppScaleNative_SetEnabledMask(UINT32 mask);
HRESULT WINAPI AppScaleNative_GetRealApis(AppScaleNativeRealApis* apis);
HRESULT WINAPI AppScaleNative_GetStatsJson(char* buffer, UINT32 cb, UINT32* needed);
HRESULT WINAPI AppScaleNative_ResetStats(void);
HRESULT WINAPI AppScaleNative_Shutdown(void);

#ifdef __cplusplus
}
#endif
