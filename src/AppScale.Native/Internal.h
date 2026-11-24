// AppScale.Native — internal interface between the hook core and the injected protocol agent.
#pragma once

#include <windows.h>

namespace appscale {

// Real OS entry points (Detours trampolines once hooked).
UINT RealGetDpiForWindow(HWND hwnd);
UINT RealGetDpiForSystem();

UINT32 EffectiveDpi();              // 0 = following the OS
void SetEffectiveDpiInternal(UINT32 dpi);

// Installs hooks for the whole process (ownerThread = 0). Returns an HRESULT.
HRESULT InstallProcessWide(UINT32 installMask, UINT32 effectiveDpi, bool includeSystemCallers);
UINT32 InstalledMask();
// UTF-8 JSON statistics (same shape as AppScaleNative_GetStatsJson).
void StatsJson(char* buffer, UINT32 cb, UINT32* needed);

namespace agent {
// Called from DllMain(DLL_PROCESS_ATTACH). Returns false if this process was not prepared by
// AppScale.Injector (no Local\AppScale.Dpi.<pid> section): the DLL then stays passive.
bool OnProcessAttach(HMODULE self);
}  // namespace agent

}  // namespace appscale
