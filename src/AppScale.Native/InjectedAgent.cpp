// AppScale.Native — injected-mode agent: makes an arbitrary PMv2 app speak cooperative protocol v1
// (docs/adr/0002-cooperative-protocol.md) and drives the app's OWN WM_DPICHANGED path.
//
// Activated only when AppScale.Injector created Local\AppScale.Dpi.<pid> for this process.
// Everything stays inside this process: window properties and subclassing are applied to this
// process's own windows, and WM_DPICHANGED carries a pointer to a RECT in this process.
#include "AppScaleNative.h"
#include "AppScaleInjection.h"
#include "Internal.h"

#include <detours.h>

#include <atomic>
#include <cstdio>
#include <string>
#include <vector>

namespace appscale::agent {
namespace {

constexpr wchar_t kProp[] = L"AppScale.Cooperative.v1";
constexpr DWORD kScanIntervalMs = 250;
constexpr UINT kSendTimeoutMs = 5000;

AppScaleSection* g_section = nullptr;
HANDLE g_sectionHandle = nullptr;
UINT g_msgSet = 0;
UINT g_msgGet = 0;
std::atomic<HWND> g_broadcastTarget{nullptr};
std::atomic<LONG> g_genuineRewritten{0};

struct Tagged {
    HWND hwnd;
    WNDPROC prev;
};
SRWLOCK g_lock = SRWLOCK_INIT;
std::vector<Tagged> g_tagged;
std::vector<HWND> g_nativeCooperative;  // windows that advertised the protocol before we got there

// --------------------------------------------------------------------------------------------
SRWLOCK g_logLock = SRWLOCK_INIT;
LARGE_INTEGER g_freq{}, g_start{};

void Log(const std::string& event, const std::string& fields) {
    if (g_section == nullptr || g_section->logPath[0] == 0) return;
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    char head[160];
    sprintf_s(head, "{\"t\":%.3f,\"pid\":%lu,\"tid\":%lu,\"event\":\"%s\"", (now.QuadPart - g_start.QuadPart) * 1000.0 / g_freq.QuadPart,
              GetCurrentProcessId(), GetCurrentThreadId(), event.c_str());
    std::string line = head;
    if (!fields.empty()) line += "," + fields;
    line += "}\n";
    AcquireSRWLockExclusive(&g_logLock);
    HANDLE f = CreateFileW(g_section->logPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f != INVALID_HANDLE_VALUE) {
        DWORD w = 0;
        WriteFile(f, line.data(), static_cast<DWORD>(line.size()), &w, nullptr);
        CloseHandle(f);
    }
    ReleaseSRWLockExclusive(&g_logLock);
}

std::string Utf8(const wchar_t* s) {
    char buf[512];
    int n = WideCharToMultiByte(CP_UTF8, 0, s, -1, buf, sizeof(buf), nullptr, nullptr);
    std::string out;
    if (n <= 0) return out;
    for (const char* p = buf; *p; ++p) {
        if (*p == '\\' || *p == '"') out += '\\';
        if (static_cast<unsigned char>(*p) >= 0x20) out += *p;
    }
    return out;
}

std::string RectJson(const RECT& r) {
    char b[96];
    sprintf_s(b, "{\"x\":%ld,\"y\":%ld,\"w\":%ld,\"h\":%ld}", r.left, r.top, r.right - r.left, r.bottom - r.top);
    return b;
}

std::string HwndJson(HWND h) {
    wchar_t cls[128]{};
    GetClassNameW(h, cls, 128);
    char b[64];
    sprintf_s(b, "\"hwnd\":\"0x%llX\",\"class\":\"", static_cast<unsigned long long>(reinterpret_cast<UINT_PTR>(h)));
    return b + Utf8(cls) + "\"";
}

void PublishTaggedCount() {
    if (!g_section) return;
    AcquireSRWLockShared(&g_lock);
    LONG n = static_cast<LONG>(g_tagged.size());
    ReleaseSRWLockShared(&g_lock);
    InterlockedExchange(&g_section->taggedWindows, n);
}

// --------------------------------------------------------------------------------------------
WNDPROC PrevProc(HWND hwnd) {
    AcquireSRWLockShared(&g_lock);
    WNDPROC p = nullptr;
    for (auto& t : g_tagged)
        if (t.hwnd == hwnd) p = t.prev;
    ReleaseSRWLockShared(&g_lock);
    return p;
}

std::vector<HWND> TaggedSnapshot() {
    AcquireSRWLockShared(&g_lock);
    std::vector<HWND> v;
    for (auto& t : g_tagged) v.push_back(t.hwnd);
    ReleaseSRWLockShared(&g_lock);
    return v;
}

struct ChildCtx {
    DPI_AWARENESS_CONTEXT parentCtx;
    UINT msg;
    int sent;
    int skippedMixed;
};

BOOL CALLBACK SendToChild(HWND child, LPARAM lp) {
    auto* c = reinterpret_cast<ChildCtx*>(lp);
    DWORD pid = 0;
    GetWindowThreadProcessId(child, &pid);
    if (pid != GetCurrentProcessId()) return TRUE;
    // The OS only sends these to children that share the parent's DPI awareness (mixed-mode
    // children are bitmap-scaled instead); mirror that.
    if (!AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(child), c->parentCtx)) {
        c->skippedMixed++;
        return TRUE;
    }
    DWORD_PTR r = 0;
    SendMessageTimeoutW(child, c->msg, 0, 0, SMTO_ABORTIFHUNG, kSendTimeoutMs, &r);
    c->sent++;
    return TRUE;
}

// Drives the application's own DPI-change code for every tagged top-level window.
// fromOverride/toOverride: 0 means "the window's real OS DPI".
void Broadcast(UINT fromOverride, UINT toOverride, const char* reason) {
    for (HWND w : TaggedSnapshot()) {
        if (!IsWindow(w)) continue;
        const UINT os = RealGetDpiForWindow(w);
        const UINT from = fromOverride ? fromOverride : os;
        const UINT to = toOverride ? toOverride : os;
        if (from == 0 || to == 0 || from == to) continue;
        RECT before{};
        GetWindowRect(w, &before);
        RECT suggested = before;
        const bool sizeable = !IsZoomed(w) && !IsIconic(w);
        if (sizeable) {
            // Scale the outer rect by new/old around its top-left, like the OS does for the client.
            suggested.right = before.left + MulDiv(before.right - before.left, static_cast<int>(to), static_cast<int>(from));
            suggested.bottom = before.top + MulDiv(before.bottom - before.top, static_cast<int>(to), static_cast<int>(from));
        }
        RECT sent = suggested;
        ChildCtx before_{GetWindowDpiAwarenessContext(w), WM_DPICHANGED_BEFOREPARENT, 0, 0};
        EnumChildWindows(w, SendToChild, reinterpret_cast<LPARAM>(&before_));
        g_broadcastTarget = w;
        DWORD_PTR r = 0;
        LRESULT ok = SendMessageTimeoutW(w, WM_DPICHANGED, MAKEWPARAM(to, to), reinterpret_cast<LPARAM>(&sent), SMTO_ABORTIFHUNG,
                                         kSendTimeoutMs, &r);
        g_broadcastTarget = nullptr;
        ChildCtx after_{GetWindowDpiAwarenessContext(w), WM_DPICHANGED_AFTERPARENT, 0, 0};
        EnumChildWindows(w, SendToChild, reinterpret_cast<LPARAM>(&after_));
        RedrawWindow(w, nullptr, nullptr, RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN | RDW_ERASE);
        RECT after{};
        GetWindowRect(w, &after);
        if (g_section) InterlockedIncrement(&g_section->broadcasts);
        char f[256];
        sprintf_s(f, ",\"reason\":\"%s\",\"osDpi\":%u,\"fromDpi\":%u,\"toDpi\":%u,\"sendOk\":%s,\"result\":%lld,\"children\":{\"before\":%d,\"after\":%d,\"skippedMixedAwareness\":%d}",
                  reason, os, from, to, ok ? "true" : "false", static_cast<long long>(r), before_.sent, after_.sent, before_.skippedMixed);
        const bool honoured = EqualRect(&after, &suggested) != FALSE;
        Log("broadcast", HwndJson(w) + f + ",\"rectBefore\":" + RectJson(before) + ",\"suggested\":" + RectJson(suggested) +
                             ",\"rectAfter\":" + RectJson(after) + ",\"suggestedHonoured\":" + (honoured ? "true" : "false"));
    }
}

LRESULT HandleSetDpi(HWND hwnd, WPARAM wParam) {
    const UINT dpi = static_cast<UINT>(wParam);
    const bool valid = dpi == 0 || (dpi >= 72 && dpi <= 240);
    char f[160];
    sprintf_s(f, ",\"wParam\":%llu,\"accepted\":%s,\"effectiveBefore\":%u", static_cast<unsigned long long>(wParam),
              valid ? "true" : "false", EffectiveDpi());
    Log("protocol.SetDpi", HwndJson(hwnd) + f);
    if (!valid) return 0;
    const UINT before = EffectiveDpi();
    if (g_section) InterlockedIncrement(&g_section->setDpiCount);
    if (before == dpi) return 1;
    // Update the hooks first so the app's handler reads the new value from every DPI API.
    SetEffectiveDpiInternal(dpi);
    if (g_section) InterlockedExchange(&g_section->effectiveDpi, static_cast<LONG>(dpi));
    Broadcast(before, dpi, dpi ? "SetDpi" : "Reset");
    return 1;
}

LRESULT CALLBACK AgentWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    WNDPROC prev = PrevProc(hwnd);
    if (g_msgSet != 0 && msg == g_msgSet) return HandleSetDpi(hwnd, wParam);
    if (g_msgGet != 0 && msg == g_msgGet) {
        const UINT os = RealGetDpiForWindow(hwnd);
        const UINT eff = EffectiveDpi();
        return MAKELONG(eff ? eff : os, os);
    }
    const UINT eff = EffectiveDpi();
    if (eff != 0 && hwnd != g_broadcastTarget.load()) {
        if (msg == WM_GETDPISCALEDSIZE) {
            // Genuine monitor move while an override is active: the app keeps its absolute DPI,
            // so keep the current window size (ADR 0002 "Behaviour on OS DPI change").
            RECT wr{};
            GetWindowRect(hwnd, &wr);
            auto* sz = reinterpret_cast<SIZE*>(lParam);
            sz->cx = wr.right - wr.left;
            sz->cy = wr.bottom - wr.top;
            return TRUE;
        }
        if (msg == WM_DPICHANGED) {
            // Present the genuine notification at the effective DPI, keeping the OS position.
            g_genuineRewritten++;
            char f[96];
            sprintf_s(f, ",\"osWParam\":%u,\"presentedDpi\":%u", LOWORD(wParam), eff);
            Log("genuineDpiChangedRewritten", HwndJson(hwnd) + f);
            wParam = MAKEWPARAM(eff, eff);
        }
    }
    if (msg == WM_NCDESTROY) {
        AcquireSRWLockExclusive(&g_lock);
        for (auto it = g_tagged.begin(); it != g_tagged.end(); ++it)
            if (it->hwnd == hwnd) {
                g_tagged.erase(it);
                break;
            }
        ReleaseSRWLockExclusive(&g_lock);
        RemovePropW(hwnd, kProp);
        PublishTaggedCount();
    }
    return prev ? CallWindowProcW(prev, hwnd, msg, wParam, lParam) : DefWindowProcW(hwnd, msg, wParam, lParam);
}

bool IsExcludedClass(const wchar_t* cls) {
    static const wchar_t* kExcluded[] = {L"#32768", L"tooltips_class32", L"IME", L"MSCTFIME UI", L"SysShadow",
                                         L"Default IME", L"#32769", L"ForegroundStaging", L"ApplicationManager_ImmersiveShellWindow"};
    for (auto e : kExcluded)
        if (_wcsicmp(cls, e) == 0) return true;
    return false;
}

BOOL CALLBACK ScanProc(HWND hwnd, LPARAM lp) {
    auto* newly = reinterpret_cast<std::vector<HWND>*>(lp);
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != GetCurrentProcessId() || !IsWindowVisible(hwnd)) return TRUE;
    if (GetWindowLongPtrW(hwnd, GWL_STYLE) & WS_CHILD) return TRUE;
    // Owned popups without a caption (menus, drop-downs, toasts) are not app windows.
    if (GetWindow(hwnd, GW_OWNER) != nullptr && (GetWindowLongPtrW(hwnd, GWL_STYLE) & WS_CAPTION) != WS_CAPTION) return TRUE;
    wchar_t cls[128]{};
    GetClassNameW(hwnd, cls, 128);
    if (IsExcludedClass(cls)) return TRUE;
    AcquireSRWLockShared(&g_lock);
    bool known = false;
    for (auto& t : g_tagged) known |= t.hwnd == hwnd;
    for (HWND n : g_nativeCooperative) known |= n == hwnd;
    ReleaseSRWLockShared(&g_lock);
    if (known) return TRUE;
    if (GetPropW(hwnd, kProp) != nullptr) {
        // The app implements the protocol itself (e.g. Win32Harness without --no-cooperative): leave it alone.
        AcquireSRWLockExclusive(&g_lock);
        g_nativeCooperative.push_back(hwnd);
        ReleaseSRWLockExclusive(&g_lock);
        Log("nativeCooperativeWindow", HwndJson(hwnd));
        return TRUE;
    }
    // Insert before subclassing so a message arriving immediately finds the previous proc.
    AcquireSRWLockExclusive(&g_lock);
    g_tagged.push_back({hwnd, reinterpret_cast<WNDPROC>(GetWindowLongPtrW(hwnd, GWLP_WNDPROC))});
    ReleaseSRWLockExclusive(&g_lock);
    SetLastError(0);
    LONG_PTR old = SetWindowLongPtrW(hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(AgentWndProc));
    DWORD err = GetLastError();
    AcquireSRWLockExclusive(&g_lock);
    for (auto it = g_tagged.begin(); it != g_tagged.end(); ++it)
        if (it->hwnd == hwnd) {
            if (old == 0 && err != 0) g_tagged.erase(it);
            else it->prev = reinterpret_cast<WNDPROC>(old);
            break;
        }
    ReleaseSRWLockExclusive(&g_lock);
    if (old == 0 && err != 0) {
        char f[64];
        sprintf_s(f, ",\"error\":%lu", err);
        Log("subclassFailed", HwndJson(hwnd) + f);
        return TRUE;
    }
    SetPropW(hwnd, kProp, reinterpret_cast<HANDLE>(1));
    newly->push_back(hwnd);
    RECT wr{};
    GetWindowRect(hwnd, &wr);
    char f[200];
    DPI_AWARENESS_CONTEXT ctx = GetWindowDpiAwarenessContext(hwnd);
    sprintf_s(f, ",\"awareness\":%d,\"realDpi\":%u,\"appVisibleDpi\":%u,\"rect\":", static_cast<int>(GetAwarenessFromDpiAwarenessContext(ctx)),
              RealGetDpiForWindow(hwnd), ::GetDpiForWindow(hwnd));
    Log("tagged", HwndJson(hwnd) + f + RectJson(wr));
    return TRUE;
}

std::string StatsString() {
    UINT32 needed = 0;
    StatsJson(nullptr, 0, &needed);
    std::string buf(needed, '\0');
    StatsJson(buf.data(), needed, &needed);
    if (!buf.empty()) buf.pop_back();
    return buf;
}

void InstallAndAck() {
    const bool includeSystem = (g_section->flags & APPSCALE_SECTIONFLAG_INCLUDE_SYSTEM_CALLERS) != 0;
    UINT32 mask = g_section->hookMask ? g_section->hookMask : APPSCALE_HOOK_ALL;
    HRESULT hr = InstallProcessWide(mask, g_section->requestedDpi, includeSystem);
    g_section->hr = hr;
    InterlockedExchange(&g_section->effectiveDpi, SUCCEEDED(hr) ? static_cast<LONG>(g_section->requestedDpi) : 0);
    InterlockedExchange(&g_section->ack, SUCCEEDED(hr) ? APPSCALE_ACK_HOOKED : APPSCALE_ACK_FAILED);
}

DWORD WINAPI AgentThread(void*) {
    if (g_section->loadMode == APPSCALE_LOAD_INJECT) InstallAndAck();
    g_msgSet = RegisterWindowMessageW(L"AppScale.SetDpi.v1");
    g_msgGet = RegisterWindowMessageW(L"AppScale.GetDpi.v1");
    wchar_t exe[MAX_PATH]{};
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    char f[200];
    sprintf_s(f, ",\"loadMode\":%u,\"requestedDpi\":%u,\"hookMask\":%u,\"flags\":%u,\"hr\":%ld,\"ack\":%ld,\"realSystemDpi\":%u,\"exe\":\"",
              g_section->loadMode, g_section->requestedDpi, g_section->hookMask, g_section->flags, g_section->hr, g_section->ack,
              RealGetDpiForSystem());
    Log("agentStart", std::string(f + 1) + Utf8(exe) + "\"");
    if (g_section->ack != APPSCALE_ACK_HOOKED) return 0;

    bool initialBroadcastDone = false;
    DWORD lastStats = GetTickCount();
    for (;;) {
        std::vector<HWND> newly;
        EnumWindows(ScanProc, reinterpret_cast<LPARAM>(&newly));
        if (!newly.empty()) PublishTaggedCount();
        // Injected into a running app that already laid out at the OS DPI: drive it to the
        // requested DPI once its windows are tagged. Launched apps start at the effective DPI.
        if (!initialBroadcastDone && !newly.empty()) {
            initialBroadcastDone = true;
            if (g_section->loadMode == APPSCALE_LOAD_INJECT && EffectiveDpi() != 0) Broadcast(0, EffectiveDpi(), "InjectStartup");
        }
        if (GetTickCount() - lastStats > 10000) {
            lastStats = GetTickCount();
            char g2[64];
            sprintf_s(g2, ",\"genuineRewritten\":%ld,\"stats\":", g_genuineRewritten.load());
            Log("stats", std::string(g2 + 1) + StatsString());
        }
        Sleep(kScanIntervalMs);
    }
}

}  // namespace

bool OnProcessAttach(HMODULE self) {
    wchar_t name[64];
    swprintf_s(name, L"%s%lu", APPSCALE_SECTION_PREFIX, GetCurrentProcessId());
    g_sectionHandle = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
    if (g_sectionHandle == nullptr) return false;
    auto* s = static_cast<AppScaleSection*>(MapViewOfFile(g_sectionHandle, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(AppScaleSection)));
    if (s == nullptr || s->magic != APPSCALE_SECTION_MAGIC || s->version != APPSCALE_SECTION_VERSION ||
        (s->requestedDpi != 0 && (s->requestedDpi < 72 || s->requestedDpi > 240))) {
        if (s) {
            InterlockedExchange(&s->ack, APPSCALE_ACK_FAILED);
            s->hr = E_INVALIDARG;
            UnmapViewOfFile(s);
        }
        CloseHandle(g_sectionHandle);
        g_sectionHandle = nullptr;
        return false;
    }
    g_section = s;
    QueryPerformanceFrequency(&g_freq);
    QueryPerformanceCounter(&g_start);
    // Pin: patched code must never outlive this image. It unloads only with the process.
    HMODULE pinned = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, reinterpret_cast<LPCWSTR>(self), &pinned);
    if (s->loadMode == APPSCALE_LOAD_LAUNCH) {
        // Loaded through the import table added by DetourCreateProcessWithDllExW: restore it, then
        // hook before any application code (and any framework DPI cache) runs.
        DetourRestoreAfterWith();
        InstallAndAck();
    }
    // The agent thread starts running once the loader lock is released.
    HANDLE t = CreateThread(nullptr, 0, AgentThread, nullptr, 0, nullptr);
    if (t) CloseHandle(t);
    return true;
}

}  // namespace appscale::agent
