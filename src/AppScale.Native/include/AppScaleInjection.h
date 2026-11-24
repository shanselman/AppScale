// Shared contract between AppScale.Injector.exe and AppScale.Native.dll (injected mode).
//
// Before the DLL is loaded into a target, the injector creates a page-file-backed section named
// Local\AppScale.Dpi.<pid> (same session, same user). AppScale.Native's DllMain only auto-activates
// when that section exists for its own process id, so loading the DLL any other way (e.g. the
// harness's explicit LoadLibrary) never installs process-wide hooks by accident. The DLL keeps its
// handle open for the life of the process and publishes live status in the same structure.
#pragma once

#include <windows.h>

#define APPSCALE_SECTION_PREFIX L"Local\\AppScale.Dpi."
#define APPSCALE_SECTION_MAGIC 0x50445341u  // 'ASDP'
#define APPSCALE_SECTION_VERSION 1u

// How the DLL was brought into the process.
#define APPSCALE_LOAD_LAUNCH 1u  // DetourCreateProcessWithDllExW: hooks attach in DllMain before app code runs
#define APPSCALE_LOAD_INJECT 2u  // CreateRemoteThread(LoadLibraryW): hooks attach on a worker thread

// ack values written by the DLL.
#define APPSCALE_ACK_PENDING 0
#define APPSCALE_ACK_HOOKED 1
#define APPSCALE_ACK_FAILED -1

#pragma pack(push, 8)
typedef struct AppScaleSection {
    UINT32 magic;            // APPSCALE_SECTION_MAGIC (injector)
    UINT32 version;          // APPSCALE_SECTION_VERSION (injector)
    UINT32 requestedDpi;     // 0 = follow OS, else 72..240 (injector)
    UINT32 loadMode;         // APPSCALE_LOAD_* (injector)
    UINT32 hookMask;         // APPSCALE_HOOK_* to install (injector)
    UINT32 flags;            // APPSCALE_SECTIONFLAG_* (injector)
    volatile LONG ack;       // APPSCALE_ACK_* (DLL)
    LONG hr;                 // HRESULT of hook installation (DLL)
    volatile LONG effectiveDpi;   // live effective DPI, 0 = following OS (DLL)
    volatile LONG taggedWindows;  // windows currently advertising the protocol (DLL)
    volatile LONG setDpiCount;    // accepted AppScale.SetDpi.v1 messages (DLL)
    volatile LONG broadcasts;     // WM_DPICHANGED messages sent by the DLL (DLL)
    WCHAR logPath[MAX_PATH]; // JSON Lines log path, empty = no log (injector)
} AppScaleSection;
#pragma pack(pop)

// Virtualize calls made from OS modules (System32/SysWOW64/WinSxS) too. Off by default: the
// Milestone 2 research showed uxtheme/user32 internals call GetDpiForSystem/GetDeviceCaps heavily.
#define APPSCALE_SECTIONFLAG_INCLUDE_SYSTEM_CALLERS 0x1u
