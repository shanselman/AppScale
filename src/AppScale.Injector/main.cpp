// AppScale.Injector — brings AppScale.Native.dll into one user-chosen x64 process so it speaks the
// cooperative protocol v1 (docs/adr/0002-cooperative-protocol.md) with a forced DPI.
//
//   AppScale.Injector --launch <exe> [--dpi N] [--log <path>] [--hooks <mask>] [--include-system-callers]
//                     [--cwd <dir>] [--timeout <ms>] [-- <args...>]
//   AppScale.Injector --inject <pid> [--dpi N] [--log <path>] [--hooks <mask>] [--include-system-callers] [--timeout <ms>]
//   AppScale.Injector --check <pid>        (eligibility only, never touches the target)
//
// Safety: refuses elevated, protected, WOW64/non-x64, AppContainer, other-user/other-session and
// denylisted system processes before opening any write/thread handle. Prints one JSON line.
#include <windows.h>

#include <detours.h>
#include <tlhelp32.h>

#include <cstdio>
#include <cwchar>
#include <string>
#include <vector>

#include "AppScaleInjection.h"
#include "AppScaleNative.h"

namespace {

enum Exit : int {
    kOk = 0,
    kUsage = 2,
    kNotX64 = 10,
    kElevated = 11,
    kProtected = 12,
    kOtherUserOrSession = 13,
    kAppContainer = 14,
    kDenylisted = 15,
    kOpenFailed = 16,
    kAlreadyInjected = 17,
    kDllMissing = 18,
    kInjectorElevated = 19,
    kLaunchFailed = 20,
    kRemoteLoadFailed = 21,
    kAckTimeout = 22,
    kHookFailed = 23,
    kTargetExited = 24,
};

std::string Utf8(const std::wstring& w) {
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, s.data(), n, nullptr, nullptr);
    s.pop_back();
    return s;
}

std::string Esc(const std::wstring& w) {
    std::string out;
    for (char c : Utf8(w)) {
        if (c == '"' || c == '\\') out += '\\';
        if (static_cast<unsigned char>(c) < 0x20) continue;
        out += c;
    }
    return out;
}

struct Result {
    std::string mode;
    DWORD pid = 0;
    UINT dpi = 0;
    std::wstring target, dll, log;
    LONG ack = APPSCALE_ACK_PENDING;
    LONG hr = 0;
    std::string error, message;
    int code = kOk;
    bool alreadyInjected = false;
};

int Emit(const Result& r) {
    printf("{\"ok\":%s,\"mode\":\"%s\",\"exitCode\":%d,\"pid\":%lu,\"dpi\":%u,\"target\":\"%s\",\"dll\":\"%s\",\"log\":\"%s\",\"ack\":%ld,\"hr\":%ld",
           r.code == kOk ? "true" : "false", r.mode.c_str(), r.code, r.pid, r.dpi, Esc(r.target).c_str(), Esc(r.dll).c_str(),
           Esc(r.log).c_str(), r.ack, r.hr);
    if (!r.error.empty()) printf(",\"error\":\"%s\",\"message\":\"%s\"", r.error.c_str(), r.message.c_str());
    printf("}\n");
    fflush(stdout);
    return r.code;
}

int Fail(Result& r, int code, const char* error, const std::string& message) {
    r.code = code;
    r.error = error;
    r.message = message;
    return Emit(r);
}

std::wstring ExeDir() {
    wchar_t p[MAX_PATH];
    DWORD n = GetModuleFileNameW(nullptr, p, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return L"";
    wchar_t* s = wcsrchr(p, L'\\');
    if (s) *s = 0;
    return p;
}

std::wstring FileName(const std::wstring& path) {
    size_t s = path.find_last_of(L"\\/");
    return s == std::wstring::npos ? path : path.substr(s + 1);
}

bool IsDenylisted(const std::wstring& imagePath) {
    // Session/shell/security infrastructure. AppScale is for ordinary user applications only.
    static const wchar_t* kDeny[] = {L"lsass.exe", L"csrss.exe", L"winlogon.exe", L"wininit.exe", L"services.exe", L"smss.exe",
                                     L"svchost.exe", L"dwm.exe", L"explorer.exe", L"consent.exe", L"LogonUI.exe", L"fontdrvhost.exe",
                                     L"MsMpEng.exe", L"NisSrv.exe", L"sihost.exe", L"ctfmon.exe", L"RuntimeBroker.exe", L"Taskmgr.exe",
                                     L"SearchHost.exe", L"TextInputHost.exe", L"LockApp.exe", L"conhost.exe", L"WerFault.exe",
                                     L"spoolsv.exe", L"audiodg.exe", L"dllhost.exe", L"taskhostw.exe", L"userinit.exe"};
    std::wstring name = FileName(imagePath);
    for (auto d : kDeny)
        if (_wcsicmp(name.c_str(), d) == 0) return true;
    // ShellExperienceHost, StartMenuExperienceHost, SecurityHealthService/Systray, ...
    if (name.size() > 18 && _wcsicmp(name.c_str() + name.size() - 18, L"ExperienceHost.exe") == 0) return true;
    if (_wcsnicmp(name.c_str(), L"SecurityHealth", 14) == 0) return true;
    std::wstring lower = imagePath;
    for (auto& c : lower) c = static_cast<wchar_t>(towlower(c));
    if (lower.find(L"\\windows\\systemapps\\") != std::wstring::npos) return true;
    return false;
}

bool TokenElevated(HANDLE token) {
    TOKEN_ELEVATION e{};
    DWORD cb = 0;
    return GetTokenInformation(token, TokenElevation, &e, sizeof(e), &cb) && e.TokenIsElevated != 0;
}

std::vector<BYTE> TokenUser_(HANDLE token) {
    DWORD cb = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &cb);
    std::vector<BYTE> buf(cb);
    if (cb == 0 || !GetTokenInformation(token, TokenUser, buf.data(), cb, &cb)) buf.clear();
    return buf;
}

// Eligibility checks that need only PROCESS_QUERY_LIMITED_INFORMATION. Returns kOk or a refusal.
int CheckTarget(DWORD pid, Result& r) {
    HANDLE self = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &self)) return Fail(r, kOpenFailed, "selfToken", "cannot open own token");
    const bool selfElevated = TokenElevated(self);
    auto selfUser = TokenUser_(self);
    CloseHandle(self);
    if (selfElevated)
        return Fail(r, kInjectorElevated, "injectorElevated", "AppScale.Injector must not run elevated; run it as the normal user");

    HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!h) return Fail(r, kOpenFailed, "openProcess", "cannot query process (error " + std::to_string(GetLastError()) + ")");
    wchar_t image[MAX_PATH * 2]{};
    DWORD cch = ARRAYSIZE(image);
    QueryFullProcessImageNameW(h, 0, image, &cch);
    r.target = image;
    int rc = kOk;
    USHORT processMachine = 0, nativeMachine = 0;
    PROCESS_PROTECTION_LEVEL_INFORMATION prot{};
    DWORD sessSelf = 0, sessTarget = 0;
    HANDLE tok = nullptr;
    if (IsDenylisted(image)) {
        rc = Fail(r, kDenylisted, "denylisted", "system/shell/security process; AppScale never injects into these");
    } else if (!IsWow64Process2(h, &processMachine, &nativeMachine) || processMachine != IMAGE_FILE_MACHINE_UNKNOWN ||
               nativeMachine != IMAGE_FILE_MACHINE_AMD64) {
        rc = Fail(r, kNotX64, "notX64", "target is WOW64/32-bit or not native x64; only x64 targets are supported");
    } else if (GetProcessInformation(h, ProcessProtectionLevelInfo, &prot, sizeof(prot)) &&
               prot.ProtectionLevel != PROTECTION_LEVEL_NONE) {
        rc = Fail(r, kProtected, "protected", "target is a protected process");
    } else if (!ProcessIdToSessionId(GetCurrentProcessId(), &sessSelf) || !ProcessIdToSessionId(pid, &sessTarget) || sessSelf != sessTarget) {
        rc = Fail(r, kOtherUserOrSession, "otherSession", "target runs in another session");
    } else if (!OpenProcessToken(h, TOKEN_QUERY, &tok)) {
        rc = Fail(r, kElevated, "tokenDenied", "cannot query target token (likely elevated or another user); refusing");
    } else {
        auto user = TokenUser_(tok);
        DWORD ac = 0, cb = 0;
        if (TokenElevated(tok)) {
            rc = Fail(r, kElevated, "elevated", "target is elevated; AppScale only touches non-elevated processes");
        } else if (user.empty() || selfUser.empty() ||
                   !EqualSid(reinterpret_cast<TOKEN_USER*>(user.data())->User.Sid, reinterpret_cast<TOKEN_USER*>(selfUser.data())->User.Sid)) {
            rc = Fail(r, kOtherUserOrSession, "otherUser", "target runs as a different user");
        } else if (GetTokenInformation(tok, TokenIsAppContainer, &ac, sizeof(ac), &cb) && ac != 0) {
            rc = Fail(r, kAppContainer, "appContainer", "target is an AppContainer (sandboxed/packaged) process");
        }
        CloseHandle(tok);
    }
    CloseHandle(h);
    return rc;
}

bool ModuleLoaded(DWORD pid, const wchar_t* name) {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, pid);
    if (snap == INVALID_HANDLE_VALUE) return false;
    MODULEENTRY32W me{sizeof(me)};
    bool found = false;
    for (BOOL ok = Module32FirstW(snap, &me); ok && !found; ok = Module32NextW(snap, &me)) found = _wcsicmp(me.szModule, name) == 0;
    CloseHandle(snap);
    return found;
}

std::wstring DefaultLog(DWORD pid) {
    wchar_t base[MAX_PATH]{};
    if (!GetEnvironmentVariableW(L"LOCALAPPDATA", base, MAX_PATH)) return L"";
    std::wstring dir = std::wstring(base) + L"\\AppScale";
    CreateDirectoryW(dir.c_str(), nullptr);
    dir += L"\\logs";
    CreateDirectoryW(dir.c_str(), nullptr);
    return dir + L"\\native-" + std::to_wstring(pid) + L".jsonl";
}

struct Options {
    std::wstring launchExe, cwd, log;
    std::vector<std::wstring> args;
    DWORD pid = 0;
    bool inject = false, check = false;
    UINT dpi = 0;
    UINT32 hooks = APPSCALE_HOOK_ALL;
    bool includeSystemCallers = false;
    DWORD timeoutMs = 10000;
};

HANDLE CreateSection(DWORD pid, const Options& o, UINT32 loadMode, const std::wstring& log, AppScaleSection** view, DWORD* err) {
    wchar_t name[64];
    swprintf_s(name, L"%s%lu", APPSCALE_SECTION_PREFIX, pid);
    // Default DACL: only this user (and SYSTEM/admins) can open it.
    HANDLE m = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(AppScaleSection), name);
    *err = GetLastError();
    if (!m) return nullptr;
    if (*err == ERROR_ALREADY_EXISTS) {
        CloseHandle(m);
        return nullptr;
    }
    auto* s = static_cast<AppScaleSection*>(MapViewOfFile(m, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(AppScaleSection)));
    if (!s) {
        *err = GetLastError();
        CloseHandle(m);
        return nullptr;
    }
    ZeroMemory(s, sizeof(*s));
    s->version = APPSCALE_SECTION_VERSION;
    s->requestedDpi = o.dpi;
    s->loadMode = loadMode;
    s->hookMask = o.hooks;
    s->flags = o.includeSystemCallers ? APPSCALE_SECTIONFLAG_INCLUDE_SYSTEM_CALLERS : 0;
    wcsncpy_s(s->logPath, log.c_str(), _TRUNCATE);
    MemoryBarrier();
    s->magic = APPSCALE_SECTION_MAGIC;
    *view = s;
    return m;
}

int WaitAck(HANDLE process, AppScaleSection* s, Result& r, DWORD timeoutMs) {
    const ULONGLONG until = GetTickCount64() + timeoutMs;
    while (s->ack == APPSCALE_ACK_PENDING) {
        if (WaitForSingleObject(process, 20) == WAIT_OBJECT_0) {
            DWORD ec = 0;
            GetExitCodeProcess(process, &ec);
            return Fail(r, kTargetExited, "targetExited", "target exited before acknowledging (exit code " + std::to_string(ec) + ")");
        }
        if (GetTickCount64() > until) return Fail(r, kAckTimeout, "ackTimeout", "DLL did not acknowledge within the timeout");
    }
    r.ack = s->ack;
    r.hr = s->hr;
    if (r.ack != APPSCALE_ACK_HOOKED) return Fail(r, kHookFailed, "hookFailed", "DLL loaded but hook installation failed");
    return Emit(r);
}

std::wstring QuoteArg(const std::wstring& a) {
    if (!a.empty() && a.find_first_of(L" \t\"") == std::wstring::npos) return a;
    std::wstring q = L"\"";
    size_t bs = 0;
    for (wchar_t c : a) {
        if (c == L'\\') {
            ++bs;
            continue;
        }
        if (c == L'"') q.append(bs * 2 + 1, L'\\');
        else q.append(bs, L'\\');
        bs = 0;
        q += c;
    }
    q.append(bs * 2, L'\\');
    return q + L"\"";
}

int Launch(const Options& o, Result& r) {
    r.mode = "launch";
    wchar_t full[MAX_PATH * 2]{};
    if (!GetFullPathNameW(o.launchExe.c_str(), ARRAYSIZE(full), full, nullptr) || GetFileAttributesW(full) == INVALID_FILE_ATTRIBUTES)
        return Fail(r, kLaunchFailed, "notFound", "executable not found");
    r.target = full;
    DWORD type = 0;
    if (!GetBinaryTypeW(full, &type) || type != SCS_64BIT_BINARY)
        return Fail(r, kNotX64, "notX64", "executable is not a native x64 image");
    if (IsDenylisted(full)) return Fail(r, kDenylisted, "denylisted", "system/shell/security executable; refusing");
    HANDLE self = nullptr;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &self)) {
        bool elevated = TokenElevated(self);
        CloseHandle(self);
        if (elevated) return Fail(r, kInjectorElevated, "injectorElevated", "AppScale.Injector must not run elevated");
    }
    // Detours takes an ANSI DLL path; refuse paths that do not survive the round trip.
    std::string dllA(MAX_PATH * 2, '\0');
    BOOL lossy = FALSE;
    int n = WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, r.dll.c_str(), -1, dllA.data(), static_cast<int>(dllA.size()), nullptr, &lossy);
    if (n <= 0 || lossy) return Fail(r, kDllMissing, "dllPath", "DLL path is not representable in the ANSI code page");
    dllA.resize(n - 1);

    std::wstring cmd = QuoteArg(full);
    for (auto& a : o.args) cmd += L" " + QuoteArg(a);
    STARTUPINFOW si{sizeof(si)};
    PROCESS_INFORMATION pi{};
    if (!DetourCreateProcessWithDllExW(full, cmd.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED | CREATE_DEFAULT_ERROR_MODE, nullptr,
                                       o.cwd.empty() ? nullptr : o.cwd.c_str(), &si, &pi, dllA.c_str(), nullptr)) {
        DWORD e = GetLastError();
        if (e == ERROR_ELEVATION_REQUIRED)
            return Fail(r, kElevated, "elevationRequired", "executable requires elevation (manifest); AppScale refuses elevated targets");
        return Fail(r, kLaunchFailed, "createProcess", "DetourCreateProcessWithDllExW failed (error " + std::to_string(e) + ")");
    }
    r.pid = pi.dwProcessId;
    r.log = o.log.empty() ? DefaultLog(pi.dwProcessId) : o.log;
    AppScaleSection* s = nullptr;
    DWORD err = 0;
    HANDLE m = CreateSection(pi.dwProcessId, o, APPSCALE_LOAD_LAUNCH, r.log, &s, &err);
    if (!m) {
        TerminateProcess(pi.hProcess, 1);  // our own suspended child, never ran any code
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return Fail(r, kLaunchFailed, "section", "cannot create shared section (error " + std::to_string(err) + ")");
    }
    ResumeThread(pi.hThread);
    int rc = WaitAck(pi.hProcess, s, r, o.timeoutMs);
    UnmapViewOfFile(s);
    CloseHandle(m);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return rc;
}

int Inject(const Options& o, Result& r) {
    r.mode = "inject";
    r.pid = o.pid;
    if (o.pid == GetCurrentProcessId() || o.pid <= 4) return Fail(r, kDenylisted, "invalidPid", "refusing this pid");
    int rc = CheckTarget(o.pid, r);
    if (rc != kOk) return rc;
    if (ModuleLoaded(o.pid, L"AppScale.Native.dll")) {
        r.alreadyInjected = true;
        return Fail(r, kAlreadyInjected, "alreadyInjected", "AppScale.Native.dll is already loaded in the target; drive it via the protocol");
    }
    HANDLE h = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ |
                               SYNCHRONIZE,
                           FALSE, o.pid);
    if (!h) return Fail(r, kOpenFailed, "openProcess", "cannot open target for injection (error " + std::to_string(GetLastError()) + ")");
    r.log = o.log.empty() ? DefaultLog(o.pid) : o.log;
    AppScaleSection* s = nullptr;
    DWORD err = 0;
    HANDLE m = CreateSection(o.pid, o, APPSCALE_LOAD_INJECT, r.log, &s, &err);
    if (!m) {
        CloseHandle(h);
        if (err == ERROR_ALREADY_EXISTS) return Fail(r, kAlreadyInjected, "alreadyInjected", "an AppScale section already exists for this pid");
        return Fail(r, kOpenFailed, "section", "cannot create shared section (error " + std::to_string(err) + ")");
    }
    const SIZE_T cb = (r.dll.size() + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(h, nullptr, cb, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    rc = kOk;
    if (!remote || !WriteProcessMemory(h, remote, r.dll.c_str(), cb, nullptr)) {
        rc = Fail(r, kRemoteLoadFailed, "writeMemory", "cannot write DLL path into target (error " + std::to_string(GetLastError()) + ")");
    } else {
        // kernel32 is mapped at the same address in every process of this boot session.
        auto loadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
        HANDLE t = CreateRemoteThread(h, nullptr, 0, loadLibrary, remote, 0, nullptr);
        if (!t) {
            rc = Fail(r, kRemoteLoadFailed, "createRemoteThread", "CreateRemoteThread failed (error " + std::to_string(GetLastError()) + ")");
        } else {
            WaitForSingleObject(t, o.timeoutMs);
            DWORD ec = 0;
            GetExitCodeThread(t, &ec);
            CloseHandle(t);
            if (ec == 0) rc = Fail(r, kRemoteLoadFailed, "loadLibrary", "LoadLibraryW returned NULL in the target");
        }
    }
    if (remote) VirtualFreeEx(h, remote, 0, MEM_RELEASE);
    if (rc == kOk) rc = WaitAck(h, s, r, o.timeoutMs);
    UnmapViewOfFile(s);
    CloseHandle(m);
    CloseHandle(h);
    return rc;
}

UINT32 ParseHooks(const wchar_t* v) {
    if (_wcsicmp(v, L"all") == 0) return APPSCALE_HOOK_ALL;
    if (_wcsicmp(v, L"research") == 0) return APPSCALE_HOOK_RESEARCH_SET;
    return static_cast<UINT32>(wcstoul(v, nullptr, 0)) & APPSCALE_HOOK_ALL;
}

void Usage() {
    fwprintf(stderr,
             L"AppScale.Injector (x64)\n"
             L"  --launch <exe> [--dpi N] [--log path] [--hooks all|research|0xMASK] [--include-system-callers]\n"
             L"                 [--cwd dir] [--timeout ms] [-- args...]\n"
             L"  --inject <pid> [--dpi N] [--log path] [--hooks ...] [--include-system-callers] [--timeout ms]\n"
             L"  --check <pid>   eligibility checks only\n"
             L"N = 72..240, or 0 to install hooks while following the OS DPI. Then drive it with\n"
             L"AppScale.SetDpi.v1 / AppScale.GetDpi.v1 (docs/adr/0002-cooperative-protocol.md).\n");
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    Options o;
    Result r;
    r.mode = "none";
    bool haveMode = false;
    for (int i = 1; i < argc; ++i) {
        std::wstring a = argv[i];
        auto next = [&]() -> const wchar_t* { return i + 1 < argc ? argv[++i] : nullptr; };
        const wchar_t* v = nullptr;
        if (a == L"--") {
            for (++i; i < argc; ++i) o.args.push_back(argv[i]);
            break;
        } else if (a == L"--launch" && (v = next())) {
            o.launchExe = v;
            haveMode = true;
        } else if ((a == L"--inject" || a == L"--check") && (v = next())) {
            o.pid = wcstoul(v, nullptr, 10);
            (a == L"--inject" ? o.inject : o.check) = true;
            haveMode = true;
        } else if (a == L"--dpi" && (v = next())) {
            o.dpi = wcstoul(v, nullptr, 10);
        } else if (a == L"--log" && (v = next())) {
            o.log = v;
        } else if (a == L"--hooks" && (v = next())) {
            o.hooks = ParseHooks(v);
        } else if (a == L"--cwd" && (v = next())) {
            o.cwd = v;
        } else if (a == L"--timeout" && (v = next())) {
            o.timeoutMs = wcstoul(v, nullptr, 10);
        } else if (a == L"--include-system-callers") {
            o.includeSystemCallers = true;
        } else {
            Usage();
            return Fail(r, kUsage, "usage", "unknown or incomplete argument");
        }
    }
    if (!haveMode || (o.dpi != 0 && (o.dpi < 72 || o.dpi > 240)) || o.hooks == 0) {
        Usage();
        return Fail(r, kUsage, "usage", "need exactly one of --launch/--inject/--check; --dpi must be 0 or 72..240");
    }
    r.dpi = o.dpi;
    r.dll = ExeDir() + L"\\AppScale.Native.dll";
    if (GetFileAttributesW(r.dll.c_str()) == INVALID_FILE_ATTRIBUTES)
        return Fail(r, kDllMissing, "dllMissing", "AppScale.Native.dll not found next to AppScale.Injector.exe");
    if (o.check) {
        r.mode = "check";
        r.pid = o.pid;
        int rc = CheckTarget(o.pid, r);
        return rc == kOk ? Emit(r) : rc;
    }
    if (o.inject) return Inject(o, r);
    return Launch(o, r);
}
