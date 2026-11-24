// AppScale.Native — Detours-based DPI API virtualization (hook core).
//
// Scope rules (see docs/evidence/milestone2-detours.md and milestone2-injection.md):
//  * Process-local only. Detours patches code in *this* process; there are no global hooks.
//  * Explicit mode (harness): only windows owned by / calls made on the configured UI thread are
//    virtualized. Injected mode: the whole process (ownerThread = 0).
//  * Only per-monitor-aware windows/threads are virtualized; DPI-unaware and system-aware contexts
//    are bitmap-scaled by DWM and must keep their real values.
//  * Calls made from OS modules (System32/SysWOW64/WinSxS) are passed through, except
//    GetDpiForWindow (window-scoped: controls inside an app window should agree with the app).
//  * Errors are preserved: when the real API fails we return its failure unchanged.
//  * Re-entrancy is guarded per thread; the last-error value is preserved.
#include "AppScaleNative.h"

#include "Internal.h"

#include <detours.h>
#include <shellscalingapi.h>
#include <intrin.h>
#include <tlhelp32.h>

#include <atomic>
#include <string>
#include <vector>

#pragma intrinsic(_ReturnAddress)

namespace {

constexpr int kMaxCallers = 48;
constexpr int kHookCount = 7;
constexpr int MDT_EFFECTIVE_DPI_VALUE = 0;

struct CallerSlot {
    HMODULE module;
    LONG count;
    bool system;
};

struct HookStats {
    const char* name;
    UINT32 bit;
    bool installed;
    std::atomic<LONG> calls{0};
    std::atomic<LONG> overridden{0};
    std::atomic<LONG> passthroughNotOwned{0};
    std::atomic<LONG> passthroughDisabled{0};
    std::atomic<LONG> passthroughOtherIndex{0};
    std::atomic<LONG> passthroughError{0};
    std::atomic<LONG> passthroughSystemCaller{0};
    std::atomic<LONG> passthroughNotPerMonitor{0};
    std::atomic<LONG> reentrantBypass{0};
    SRWLOCK lock = SRWLOCK_INIT;
    CallerSlot callers[kMaxCallers]{};
    LONG callersDropped = 0;
};

HookStats g_stats[kHookCount] = {
    {"GetDpiForWindow", APPSCALE_HOOK_GETDPIFORWINDOW, false},
    {"GetDpiForSystem", APPSCALE_HOOK_GETDPIFORSYSTEM, false},
    {"GetDeviceCaps", APPSCALE_HOOK_GETDEVICECAPS, false},
    {"GetDpiForMonitor", APPSCALE_HOOK_GETDPIFORMONITOR, false},
    {"GetSystemDpiForProcess", APPSCALE_HOOK_GETSYSTEMDPIFORPROCESS, false},
    {"GetSystemMetrics", APPSCALE_HOOK_GETSYSTEMMETRICS, false},
    {"SystemParametersInfoW", APPSCALE_HOOK_SYSTEMPARAMETERSINFO, false},
};
enum HookIndex { kWindow = 0, kSystem, kDevCaps, kMonitor, kProcess, kMetrics, kSpi };

std::atomic<UINT32> g_effectiveDpi{0};
std::atomic<UINT32> g_enabledMask{0};
DWORD g_ownerThread = 0;  // 0 = whole process (injected mode)
bool g_includeSystemCallers = true;  // explicit (harness) mode keeps the original research semantics
bool g_initialized = false;
const char* g_mode = "none";
SRWLOCK g_initLock = SRWLOCK_INIT;
thread_local int t_depth = 0;
wchar_t g_systemDirs[3][MAX_PATH]{};
size_t g_systemDirLen[3]{};

using PFN_GetDpiForWindow = UINT(WINAPI*)(HWND);
using PFN_GetDpiForSystem = UINT(WINAPI*)(void);
using PFN_GetDeviceCaps = int(WINAPI*)(HDC, int);
using PFN_GetDpiForMonitor = HRESULT(WINAPI*)(HMONITOR, int, UINT*, UINT*);
using PFN_GetSystemDpiForProcess = UINT(WINAPI*)(HANDLE);
using PFN_GetSystemMetrics = int(WINAPI*)(int);
using PFN_SystemParametersInfoW = BOOL(WINAPI*)(UINT, UINT, PVOID, UINT);

// Hold either the original export (not hooked) or the Detours trampoline (hooked).
PFN_GetDpiForWindow Real_GetDpiForWindow = nullptr;
PFN_GetDpiForSystem Real_GetDpiForSystem = nullptr;
PFN_GetDeviceCaps Real_GetDeviceCaps = nullptr;
PFN_GetDpiForMonitor Real_GetDpiForMonitor = nullptr;
PFN_GetSystemDpiForProcess Real_GetSystemDpiForProcess = nullptr;
PFN_GetSystemMetrics Real_GetSystemMetrics = nullptr;
PFN_SystemParametersInfoW Real_SystemParametersInfoW = nullptr;

struct ReentryGuard {
    bool outer;
    ReentryGuard() : outer(t_depth == 0) { ++t_depth; }
    ~ReentryGuard() { --t_depth; }
};

void InitSystemDirs() {
    wchar_t win[MAX_PATH]{};
    UINT n = GetSystemWindowsDirectoryW(win, MAX_PATH);
    if (n == 0 || n >= MAX_PATH - 16) return;
    const wchar_t* subs[3] = {L"\\System32\\", L"\\SysWOW64\\", L"\\WinSxS\\"};
    for (int i = 0; i < 3; ++i) {
        wcscpy_s(g_systemDirs[i], win);
        wcscat_s(g_systemDirs[i], subs[i]);
        g_systemDirLen[i] = wcslen(g_systemDirs[i]);
    }
}

bool IsSystemModule(HMODULE mod) {
    if (mod == nullptr) return false;  // JIT-compiled code, thunks: treat as application code
    wchar_t path[MAX_PATH];
    DWORD n = GetModuleFileNameW(mod, path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return false;
    for (int i = 0; i < 3; ++i)
        if (g_systemDirLen[i] && _wcsnicmp(path, g_systemDirs[i], g_systemDirLen[i]) == 0) return true;
    return false;
}

// Returns true when the caller is an OS module (cached per module). The module path lookup runs
// outside s.lock so a hook firing inside another DLL's DllMain can never deadlock against it.
bool RecordCaller(HookStats& s, void* returnAddress) {
    HMODULE mod = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            static_cast<LPCWSTR>(returnAddress), &mod)) {
        mod = nullptr;
    }
    AcquireSRWLockExclusive(&s.lock);
    for (auto& c : s.callers) {
        if (c.count == 0) break;
        if (c.module == mod) {
            ++c.count;
            bool system = c.system;
            ReleaseSRWLockExclusive(&s.lock);
            return system;
        }
    }
    ReleaseSRWLockExclusive(&s.lock);
    const bool system = IsSystemModule(mod);
    AcquireSRWLockExclusive(&s.lock);
    int i = 0;
    for (; i < kMaxCallers; ++i) {
        if (s.callers[i].count == 0) {
            s.callers[i] = {mod, 1, system};
            break;
        }
        if (s.callers[i].module == mod) {
            ++s.callers[i].count;
            break;
        }
    }
    if (i == kMaxCallers) ++s.callersDropped;
    ReleaseSRWLockExclusive(&s.lock);
    return system;
}

// `dpi` is the caller's single snapshot of g_effectiveDpi: every hook loads it exactly once and uses
// that same value for the decision and the result, so a concurrent reset can never yield 0.
bool Enabled(HookIndex h, UINT dpi) {
    return (g_enabledMask.load() & g_stats[h].bit) != 0 && dpi != 0;
}

bool OnOwnerThread() { return g_ownerThread == 0 || GetCurrentThreadId() == g_ownerThread; }

bool WindowOwned(HWND hwnd) {
    DWORD pid = 0;
    DWORD tid = GetWindowThreadProcessId(hwnd, &pid);
    return tid != 0 && pid == GetCurrentProcessId() && (g_ownerThread == 0 || tid == g_ownerThread);
}

bool PerMonitor(DPI_AWARENESS_CONTEXT ctx) {
    return ctx != nullptr && GetAwarenessFromDpiAwarenessContext(ctx) == DPI_AWARENESS_PER_MONITOR_AWARE;
}
bool ThreadPerMonitor() { return PerMonitor(GetThreadDpiAwarenessContext()); }

// Shared decision for the window-less APIs. Order matters only for the statistics.
bool ShouldOverrideThreadScoped(HookStats& s, HookIndex h, bool systemCaller, UINT dpi) {
    if (!Enabled(h, dpi)) { s.passthroughDisabled++; return false; }
    if (!OnOwnerThread()) { s.passthroughNotOwned++; return false; }
    if (systemCaller && !g_includeSystemCallers) { s.passthroughSystemCaller++; return false; }
    if (!ThreadPerMonitor()) { s.passthroughNotPerMonitor++; return false; }
    return true;
}

UINT WINAPI Hook_GetDpiForWindow(HWND hwnd) {
    HookStats& s = g_stats[kWindow];
    ReentryGuard guard;
    if (!guard.outer) {
        s.reentrantBypass++;
        return Real_GetDpiForWindow(hwnd);
    }
    void* ra = _ReturnAddress();
    s.calls++;
    UINT real = Real_GetDpiForWindow(hwnd);
    DWORD err = GetLastError();
    RecordCaller(s, ra);
    UINT result = real;
    const UINT dpi = g_effectiveDpi.load();
    if (real == 0) {
        s.passthroughError++;  // invalid hwnd etc.: preserve the documented 0 result
    } else if (!Enabled(kWindow, dpi)) {
        s.passthroughDisabled++;
    } else if (!WindowOwned(hwnd)) {
        s.passthroughNotOwned++;
    } else if (!PerMonitor(GetWindowDpiAwarenessContext(hwnd))) {
        s.passthroughNotPerMonitor++;  // unaware/system-aware child HWNDs are DWM-scaled: keep the truth
    } else {
        s.overridden++;
        result = dpi;
    }
    SetLastError(err);
    return result;
}

UINT WINAPI Hook_GetDpiForSystem() {
    HookStats& s = g_stats[kSystem];
    ReentryGuard guard;
    if (!guard.outer) {
        s.reentrantBypass++;
        return Real_GetDpiForSystem();
    }
    void* ra = _ReturnAddress();
    s.calls++;
    UINT real = Real_GetDpiForSystem();
    DWORD err = GetLastError();
    bool sys = RecordCaller(s, ra);
    UINT result = real;
    const UINT dpi = g_effectiveDpi.load();
    if (real == 0) {
        s.passthroughError++;
    } else if (ShouldOverrideThreadScoped(s, kSystem, sys, dpi)) {
        s.overridden++;
        result = dpi;
    }
    SetLastError(err);
    return result;
}

UINT WINAPI Hook_GetSystemDpiForProcess(HANDLE process) {
    HookStats& s = g_stats[kProcess];
    ReentryGuard guard;
    if (!guard.outer) {
        s.reentrantBypass++;
        return Real_GetSystemDpiForProcess(process);
    }
    void* ra = _ReturnAddress();
    s.calls++;
    UINT real = Real_GetSystemDpiForProcess(process);
    DWORD err = GetLastError();
    bool sys = RecordCaller(s, ra);
    UINT result = real;
    const UINT dpi = g_effectiveDpi.load();
    if (real == 0) {
        s.passthroughError++;
    } else if (process != GetCurrentProcess() && GetProcessId(process) != GetCurrentProcessId()) {
        s.passthroughOtherIndex++;  // another process: never lie about it
    } else if (ShouldOverrideThreadScoped(s, kProcess, sys, dpi)) {
        s.overridden++;
        result = dpi;
    }
    SetLastError(err);
    return result;
}

int WINAPI Hook_GetDeviceCaps(HDC hdc, int index) {
    HookStats& s = g_stats[kDevCaps];
    if (index != LOGPIXELSX && index != LOGPIXELSY) {
        // Hot path for every other GDI capability query: no bookkeeping beyond the counter.
        s.passthroughOtherIndex++;
        return Real_GetDeviceCaps(hdc, index);
    }
    ReentryGuard guard;
    if (!guard.outer) {
        s.reentrantBypass++;
        return Real_GetDeviceCaps(hdc, index);
    }
    void* ra = _ReturnAddress();
    s.calls++;
    int real = Real_GetDeviceCaps(hdc, index);
    DWORD err = GetLastError();
    bool sys = RecordCaller(s, ra);
    int result = real;
    const UINT dpi = g_effectiveDpi.load();
    if (real == 0) {
        s.passthroughError++;
    } else if (!ShouldOverrideThreadScoped(s, kDevCaps, sys, dpi)) {
        // counted inside
    } else if (Real_GetDeviceCaps(hdc, TECHNOLOGY) != DT_RASDISPLAY) {
        s.passthroughOtherIndex++;  // printers, metafiles keep real values
    } else {
        HWND w = WindowFromDC(hdc);
        if (w != nullptr && !WindowOwned(w)) {
            s.passthroughNotOwned++;
        } else {
            s.overridden++;
            result = static_cast<int>(dpi);
        }
    }
    SetLastError(err);
    return result;
}

HRESULT WINAPI Hook_GetDpiForMonitor(HMONITOR monitor, int type, UINT* dpiX, UINT* dpiY) {
    HookStats& s = g_stats[kMonitor];
    ReentryGuard guard;
    if (!guard.outer) {
        s.reentrantBypass++;
        return Real_GetDpiForMonitor(monitor, type, dpiX, dpiY);
    }
    void* ra = _ReturnAddress();
    s.calls++;
    HRESULT hr = Real_GetDpiForMonitor(monitor, type, dpiX, dpiY);
    DWORD err = GetLastError();
    bool sys = RecordCaller(s, ra);
    const UINT dpi = g_effectiveDpi.load();
    if (FAILED(hr) || dpiX == nullptr || dpiY == nullptr) {
        s.passthroughError++;
    } else if (type != MDT_EFFECTIVE_DPI_VALUE) {
        s.passthroughOtherIndex++;
    } else if (ShouldOverrideThreadScoped(s, kMonitor, sys, dpi)) {
        s.overridden++;
        *dpiX = *dpiY = dpi;
    }
    SetLastError(err);
    return hr;
}

// Screen/work-area geometry is not a DPI-derived metric; never rescale it.
bool IsGeometryMetric(int index) {
    switch (index) {
        case SM_CXSCREEN: case SM_CYSCREEN: case SM_CXFULLSCREEN: case SM_CYFULLSCREEN:
        case SM_CXMAXTRACK: case SM_CYMAXTRACK: case SM_CXMAXIMIZED: case SM_CYMAXIMIZED:
        case SM_XVIRTUALSCREEN: case SM_YVIRTUALSCREEN: case SM_CXVIRTUALSCREEN: case SM_CYVIRTUALSCREEN:
        case SM_CMONITORS: case SM_CXMINTRACK: case SM_CYMINTRACK:
            return true;
        default:
            return false;
    }
}

int WINAPI Hook_GetSystemMetrics(int index) {
    HookStats& s = g_stats[kMetrics];
    ReentryGuard guard;
    if (!guard.outer) {
        s.reentrantBypass++;
        return Real_GetSystemMetrics(index);
    }
    void* ra = _ReturnAddress();
    s.calls++;
    int real = Real_GetSystemMetrics(index);
    DWORD err = GetLastError();
    bool sys = RecordCaller(s, ra);
    int result = real;
    const UINT dpi = g_effectiveDpi.load();
    if (IsGeometryMetric(index)) {
        s.passthroughOtherIndex++;
    } else if (ShouldOverrideThreadScoped(s, kMetrics, sys, dpi)) {
        // Only rescale indices that GetSystemMetricsForDpi reproduces exactly at the system DPI.
        const UINT sysDpi = Real_GetDpiForSystem();
        if (GetSystemMetricsForDpi(index, sysDpi) != real) {
            s.passthroughOtherIndex++;
        } else {
            s.overridden++;
            result = GetSystemMetricsForDpi(index, dpi);
        }
    }
    SetLastError(err);
    return result;
}

BOOL WINAPI Hook_SystemParametersInfoW(UINT action, UINT param, PVOID pv, UINT winIni) {
    HookStats& s = g_stats[kSpi];
    if (action != SPI_GETNONCLIENTMETRICS && action != SPI_GETICONMETRICS && action != SPI_GETICONTITLELOGFONT) {
        s.passthroughOtherIndex++;
        return Real_SystemParametersInfoW(action, param, pv, winIni);
    }
    ReentryGuard guard;
    if (!guard.outer) {
        s.reentrantBypass++;
        return Real_SystemParametersInfoW(action, param, pv, winIni);
    }
    void* ra = _ReturnAddress();
    s.calls++;
    bool sys = RecordCaller(s, ra);
    const UINT dpi = g_effectiveDpi.load();
    if (ShouldOverrideThreadScoped(s, kSpi, sys, dpi)) {
        // SystemParametersInfoForDpi requires fWinIni = 0 for these GET actions.
        if (SystemParametersInfoForDpi(action, param, pv, 0, dpi)) {
            s.overridden++;
            return TRUE;
        }
        s.passthroughError++;  // e.g. a legacy NONCLIENTMETRICS cbSize: fall back to the real call
    }
    return Real_SystemParametersInfoW(action, param, pv, winIni);
}

void AppendEscaped(std::string& out, const wchar_t* text) {
    char buf[1024];
    int n = WideCharToMultiByte(CP_UTF8, 0, text, -1, buf, sizeof(buf), nullptr, nullptr);
    if (n <= 0) return;
    for (const char* p = buf; *p; ++p) {
        if (*p == '\\' || *p == '"') out += '\\';
        out += *p;
    }
}

template <typename T>
void Attach(T* real, void* hook) {
    DetourAttach(reinterpret_cast<PVOID*>(real), hook);
}
template <typename T>
void Detach(T* real, void* hook) {
    DetourDetach(reinterpret_cast<PVOID*>(real), hook);
}

// Handles of every other thread in this process, opened BEFORE DetourTransactionBegin so that no
// Toolhelp/heap work happens while Detours holds threads suspended. The vector is fully sized here
// and never grows afterwards. Threads created after the snapshot start at their start routine, so
// their instruction pointer cannot be inside the bytes being patched.
struct OtherThreads {
    std::vector<HANDLE> handles;
    ~OtherThreads() {
        for (HANDLE h : handles) CloseHandle(h);
    }
    void Open() {
        HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snap == INVALID_HANDLE_VALUE) return;
        const DWORD pid = GetCurrentProcessId();
        const DWORD self = GetCurrentThreadId();
        THREADENTRY32 te{};
        te.dwSize = sizeof(te);
        size_t count = 0;
        for (BOOL ok = Thread32First(snap, &te); ok; ok = Thread32Next(snap, &te))
            if (te.th32OwnerProcessID == pid && te.th32ThreadID != self) ++count;
        handles.reserve(count);
        te.dwSize = sizeof(te);
        for (BOOL ok = Thread32First(snap, &te); ok && handles.size() < count; ok = Thread32Next(snap, &te)) {
            if (te.th32OwnerProcessID != pid || te.th32ThreadID == self) continue;
            HANDLE h = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_QUERY_LIMITED_INFORMATION,
                                  FALSE, te.th32ThreadID);
            if (h) handles.push_back(h);  // a thread that already exited is simply skipped
        }
        CloseHandle(snap);
    }
};
UINT32 g_lastUpdatedThreads = 0;

// Attaches are queued first (they allocate Detours bookkeeping and trampolines); threads are only
// suspended afterwards, immediately before the commit, so the suspended window is as short as
// possible. Detours itself still allocates one small node per DetourUpdateThread call.
LONG AttachAll(UINT32 mask, bool suspendOtherThreads) {
    OtherThreads others;
    if (suspendOtherThreads) others.Open();
    LONG rc = DetourTransactionBegin();
    if (rc != NO_ERROR) return rc;
    if (mask & APPSCALE_HOOK_GETDPIFORWINDOW) Attach(&Real_GetDpiForWindow, reinterpret_cast<void*>(Hook_GetDpiForWindow));
    if (mask & APPSCALE_HOOK_GETDPIFORSYSTEM) Attach(&Real_GetDpiForSystem, reinterpret_cast<void*>(Hook_GetDpiForSystem));
    if (mask & APPSCALE_HOOK_GETDEVICECAPS) Attach(&Real_GetDeviceCaps, reinterpret_cast<void*>(Hook_GetDeviceCaps));
    if (mask & APPSCALE_HOOK_GETDPIFORMONITOR) Attach(&Real_GetDpiForMonitor, reinterpret_cast<void*>(Hook_GetDpiForMonitor));
    if (mask & APPSCALE_HOOK_GETSYSTEMDPIFORPROCESS) Attach(&Real_GetSystemDpiForProcess, reinterpret_cast<void*>(Hook_GetSystemDpiForProcess));
    if (mask & APPSCALE_HOOK_GETSYSTEMMETRICS) Attach(&Real_GetSystemMetrics, reinterpret_cast<void*>(Hook_GetSystemMetrics));
    if (mask & APPSCALE_HOOK_SYSTEMPARAMETERSINFO) Attach(&Real_SystemParametersInfoW, reinterpret_cast<void*>(Hook_SystemParametersInfoW));
    for (HANDLE h : others.handles) DetourUpdateThread(h);
    rc = DetourTransactionCommit();
    if (rc != NO_ERROR) DetourTransactionAbort();
    g_lastUpdatedThreads = static_cast<UINT32>(others.handles.size());
    return rc;
}

LONG DetachAll(bool suspendOtherThreads) {
    OtherThreads others;
    if (suspendOtherThreads) others.Open();
    LONG rc = DetourTransactionBegin();
    if (rc != NO_ERROR) return rc;
    if (g_stats[kWindow].installed) Detach(&Real_GetDpiForWindow, reinterpret_cast<void*>(Hook_GetDpiForWindow));
    if (g_stats[kSystem].installed) Detach(&Real_GetDpiForSystem, reinterpret_cast<void*>(Hook_GetDpiForSystem));
    if (g_stats[kDevCaps].installed) Detach(&Real_GetDeviceCaps, reinterpret_cast<void*>(Hook_GetDeviceCaps));
    if (g_stats[kMonitor].installed) Detach(&Real_GetDpiForMonitor, reinterpret_cast<void*>(Hook_GetDpiForMonitor));
    if (g_stats[kProcess].installed) Detach(&Real_GetSystemDpiForProcess, reinterpret_cast<void*>(Hook_GetSystemDpiForProcess));
    if (g_stats[kMetrics].installed) Detach(&Real_GetSystemMetrics, reinterpret_cast<void*>(Hook_GetSystemMetrics));
    if (g_stats[kSpi].installed) Detach(&Real_SystemParametersInfoW, reinterpret_cast<void*>(Hook_SystemParametersInfoW));
    for (HANDLE h : others.handles) DetourUpdateThread(h);
    rc = DetourTransactionCommit();
    if (rc != NO_ERROR) DetourTransactionAbort();
    return rc;
}

bool ValidDpi(UINT32 dpi) { return dpi == 0 || (dpi >= 72 && dpi <= 240); }

// Resolves the real exports. Every module used here is a static import of this DLL, so this is
// safe to call from DllMain (no LoadLibrary under the loader lock).
bool ResolveReals() {
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    HMODULE gdi32 = GetModuleHandleW(L"gdi32.dll");
    if (user32 == nullptr || gdi32 == nullptr) return false;
    Real_GetDpiForWindow = reinterpret_cast<PFN_GetDpiForWindow>(GetProcAddress(user32, "GetDpiForWindow"));
    Real_GetDpiForSystem = reinterpret_cast<PFN_GetDpiForSystem>(GetProcAddress(user32, "GetDpiForSystem"));
    Real_GetSystemDpiForProcess = reinterpret_cast<PFN_GetSystemDpiForProcess>(GetProcAddress(user32, "GetSystemDpiForProcess"));
    Real_GetSystemMetrics = reinterpret_cast<PFN_GetSystemMetrics>(GetProcAddress(user32, "GetSystemMetrics"));
    Real_SystemParametersInfoW = reinterpret_cast<PFN_SystemParametersInfoW>(GetProcAddress(user32, "SystemParametersInfoW"));
    Real_GetDeviceCaps = reinterpret_cast<PFN_GetDeviceCaps>(GetProcAddress(gdi32, "GetDeviceCaps"));
    // shcore is linked statically (see CMakeLists) so the import is already resolved.
    Real_GetDpiForMonitor = reinterpret_cast<PFN_GetDpiForMonitor>(&::GetDpiForMonitor);
    return Real_GetDpiForWindow && Real_GetDpiForSystem && Real_GetSystemDpiForProcess && Real_GetSystemMetrics &&
           Real_SystemParametersInfoW && Real_GetDeviceCaps && Real_GetDpiForMonitor;
}

// Caller holds g_initLock.
HRESULT InstallLocked(UINT32 installMask, UINT32 enabledMask, UINT32 effectiveDpi, DWORD ownerThread, bool includeSystemCallers,
                      const char* mode, bool suspendOtherThreads) {
    if (g_initialized) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
    if (!ResolveReals()) return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
    InitSystemDirs();
    g_ownerThread = ownerThread;
    g_includeSystemCallers = includeSystemCallers;
    g_effectiveDpi = effectiveDpi;
    g_enabledMask = enabledMask & installMask;
    LONG rc = installMask ? AttachAll(installMask, suspendOtherThreads) : NO_ERROR;
    if (rc != NO_ERROR) return HRESULT_FROM_WIN32(rc);
    for (auto& s : g_stats) s.installed = (installMask & s.bit) != 0;
    g_mode = mode;
    g_initialized = true;
    return S_OK;
}

}  // namespace

// ------------------------------------------------------------------------------------------------
namespace appscale {

UINT RealGetDpiForWindow(HWND hwnd) { return Real_GetDpiForWindow ? Real_GetDpiForWindow(hwnd) : ::GetDpiForWindow(hwnd); }
UINT RealGetDpiForSystem() { return Real_GetDpiForSystem ? Real_GetDpiForSystem() : ::GetDpiForSystem(); }
UINT32 EffectiveDpi() { return g_effectiveDpi.load(); }
UINT32 ThreadsUpdatedAtInstall() { return g_lastUpdatedThreads; }
void SetEffectiveDpiInternal(UINT32 dpi) { g_effectiveDpi = dpi; }

UINT32 InstalledMask() {
    UINT32 m = 0;
    for (auto& s : g_stats)
        if (s.installed) m |= s.bit;
    return m;
}

HRESULT InstallProcessWide(UINT32 installMask, UINT32 effectiveDpi, bool includeSystemCallers, bool suspendOtherThreads) {
    AcquireSRWLockExclusive(&g_initLock);
    HRESULT hr = InstallLocked(installMask & APPSCALE_HOOK_ALL, installMask & APPSCALE_HOOK_ALL, effectiveDpi, 0,
                               includeSystemCallers, "injected", suspendOtherThreads);
    ReleaseSRWLockExclusive(&g_initLock);
    return hr;
}

void StatsJson(char* buffer, UINT32 cb, UINT32* needed) { AppScaleNative_GetStatsJson(buffer, cb, needed); }

}  // namespace appscale

// ------------------------------------------------------------------------------------------------
extern "C" HRESULT WINAPI AppScaleNative_Initialize(const AppScaleNativeConfig* config) {
    if (config == nullptr || config->cbSize < sizeof(AppScaleNativeConfig) || config->apiVersion != APPSCALE_NATIVE_API_VERSION)
        return E_INVALIDARG;
    if (!ValidDpi(config->effectiveDpi) || (config->installMask & ~APPSCALE_HOOK_ALL) != 0 || config->ownerThreadId == 0)
        return E_INVALIDARG;
    AcquireSRWLockExclusive(&g_initLock);
    HRESULT hr = InstallLocked(config->installMask, config->enabledMask, config->effectiveDpi, config->ownerThreadId, true, "explicit",
                               false);
    ReleaseSRWLockExclusive(&g_initLock);
    return hr;
}

extern "C" HRESULT WINAPI AppScaleNative_SetEffectiveDpi(UINT32 dpi) {
    if (!ValidDpi(dpi)) return E_INVALIDARG;
    g_effectiveDpi = dpi;
    return S_OK;
}

extern "C" HRESULT WINAPI AppScaleNative_SetEnabledMask(UINT32 mask) {
    if ((mask & ~APPSCALE_HOOK_ALL) != 0) return E_INVALIDARG;
    UINT32 installed = appscale::InstalledMask();
    g_enabledMask = mask & installed;
    return (mask & ~installed) ? S_FALSE : S_OK;
}

extern "C" HRESULT WINAPI AppScaleNative_GetRealApis(AppScaleNativeRealApis* apis) {
    if (apis == nullptr) return E_POINTER;
    if (!g_initialized) return E_NOT_VALID_STATE;
    apis->GetDpiForWindow = Real_GetDpiForWindow;
    apis->GetDpiForSystem = Real_GetDpiForSystem;
    apis->GetDeviceCaps = Real_GetDeviceCaps;
    apis->GetDpiForMonitor = Real_GetDpiForMonitor;
    return S_OK;
}

extern "C" HRESULT WINAPI AppScaleNative_ResetStats(void) {
    for (auto& s : g_stats) {
        s.calls = 0;
        s.overridden = 0;
        s.passthroughNotOwned = 0;
        s.passthroughDisabled = 0;
        s.passthroughOtherIndex = 0;
        s.passthroughError = 0;
        s.passthroughSystemCaller = 0;
        s.passthroughNotPerMonitor = 0;
        s.reentrantBypass = 0;
        AcquireSRWLockExclusive(&s.lock);
        for (auto& c : s.callers) c = {};
        s.callersDropped = 0;
        ReleaseSRWLockExclusive(&s.lock);
    }
    return S_OK;
}

extern "C" HRESULT WINAPI AppScaleNative_GetStatsJson(char* buffer, UINT32 cb, UINT32* needed) {
    std::string j = "{\"apiVersion\":" + std::to_string(APPSCALE_NATIVE_API_VERSION) +
                    ",\"initialized\":" + (g_initialized ? "true" : "false") + ",\"mode\":\"" + g_mode + "\"" +
                    ",\"effectiveDpi\":" + std::to_string(g_effectiveDpi.load()) +
                    ",\"enabledMask\":" + std::to_string(g_enabledMask.load()) +
                    ",\"ownerThreadId\":" + std::to_string(g_ownerThread) +
                    ",\"threadsUpdatedAtInstall\":" + std::to_string(g_lastUpdatedThreads) +
                    ",\"includeSystemCallers\":" + (g_includeSystemCallers ? "true" : "false") + ",\"hooks\":[";
    for (int h = 0; h < kHookCount; ++h) {
        HookStats& s = g_stats[h];
        if (h) j += ',';
        j += "{\"api\":\"";
        j += s.name;
        j += "\",\"installed\":";
        j += s.installed ? "true" : "false";
        j += ",\"enabled\":";
        j += (g_enabledMask.load() & s.bit) ? "true" : "false";
        j += ",\"calls\":" + std::to_string(s.calls.load()) + ",\"overridden\":" + std::to_string(s.overridden.load()) +
             ",\"passthroughNotOwned\":" + std::to_string(s.passthroughNotOwned.load()) +
             ",\"passthroughDisabled\":" + std::to_string(s.passthroughDisabled.load()) +
             ",\"passthroughOtherIndex\":" + std::to_string(s.passthroughOtherIndex.load()) +
             ",\"passthroughError\":" + std::to_string(s.passthroughError.load()) +
             ",\"passthroughSystemCaller\":" + std::to_string(s.passthroughSystemCaller.load()) +
             ",\"passthroughNotPerMonitor\":" + std::to_string(s.passthroughNotPerMonitor.load()) +
             ",\"reentrantBypass\":" + std::to_string(s.reentrantBypass.load()) + ",\"callers\":[";
        CallerSlot copy[kMaxCallers];
        AcquireSRWLockShared(&s.lock);
        memcpy(copy, s.callers, sizeof(copy));
        const LONG dropped = s.callersDropped;
        ReleaseSRWLockShared(&s.lock);
        bool first = true;
        for (const auto& c : copy) {
            if (c.count == 0) break;
            wchar_t path[MAX_PATH] = L"<unknown>";
            if (c.module) GetModuleFileNameW(c.module, path, MAX_PATH);
            const wchar_t* name = wcsrchr(path, L'\\');
            j += first ? "{\"module\":\"" : ",{\"module\":\"";
            first = false;
            AppendEscaped(j, name ? name + 1 : path);
            j += "\",\"system\":";
            j += c.system ? "true" : "false";
            j += ",\"count\":" + std::to_string(c.count) + "}";
        }
        j += "],\"callersDropped\":" + std::to_string(dropped) + "}";
    }
    j += "]}";
    UINT32 size = static_cast<UINT32>(j.size() + 1);
    if (needed) *needed = size;
    if (buffer == nullptr || cb < size) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    memcpy(buffer, j.c_str(), size);
    return S_OK;
}

extern "C" HRESULT WINAPI AppScaleNative_Shutdown(void) {
    AcquireSRWLockExclusive(&g_initLock);
    if (!g_initialized || g_ownerThread == 0) {
        // Injected mode is never torn down: the module is pinned and lives as long as the process.
        ReleaseSRWLockExclusive(&g_initLock);
        return S_FALSE;
    }
    g_enabledMask = 0;
    LONG rc = DetachAll(true);
    if (rc == NO_ERROR) {
        for (auto& s : g_stats) s.installed = false;
        g_initialized = false;
    }
    ReleaseSRWLockExclusive(&g_initLock);
    return rc == NO_ERROR ? S_OK : HRESULT_FROM_WIN32(rc);
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved) {
    if (DetourIsHelperProcess()) return TRUE;
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        appscale::agent::OnProcessAttach(module);
    } else if (reason == DLL_PROCESS_DETACH && reserved == nullptr && g_initialized && g_ownerThread != 0) {
        // Explicit mode FreeLibrary while hooks are live: remove them so no patched code jumps into an unloaded image.
        g_enabledMask = 0;
        DetachAll(false);
        g_initialized = false;
    }
    return TRUE;
}
