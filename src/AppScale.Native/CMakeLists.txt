# AppScale.Native — DPI API virtualization via Detours. Explicit mode (harness research, Milestone 2)
# and injected mode (loaded into a consenting same-user x64 process by AppScale.Injector).
# Uses Microsoft Detours (MIT) fetched from source at a pinned tag; no binaries are vendored.
include(FetchContent)

FetchContent_Declare(detours
  GIT_REPOSITORY https://github.com/microsoft/Detours.git
  GIT_TAG        v4.0.1
  GIT_SHALLOW    TRUE)
# Detours v4.0.1 has no CMakeLists.txt, so MakeAvailable only downloads it.
FetchContent_MakeAvailable(detours)

set(DETOURS_SRC "${detours_SOURCE_DIR}/src")
add_library(appscale_detours STATIC
  "${DETOURS_SRC}/detours.cpp"
  "${DETOURS_SRC}/modules.cpp"
  "${DETOURS_SRC}/disasm.cpp"
  "${DETOURS_SRC}/image.cpp"
  # creatwth.cpp: DetourCreateProcessWithDllExW (injector --launch) and DetourRestoreAfterWith /
  # DetourIsHelperProcess (DLL).
  "${DETOURS_SRC}/creatwth.cpp"
  "${DETOURS_SRC}/disolx86.cpp"
  "${DETOURS_SRC}/disolx64.cpp"
  "${DETOURS_SRC}/disolia64.cpp"
  "${DETOURS_SRC}/disolarm.cpp"
  "${DETOURS_SRC}/disolarm64.cpp")
target_include_directories(appscale_detours PUBLIC "${DETOURS_SRC}")
# Mirrors the flags of Detours' own makefile; third-party code is compiled at /W3 without /WX.
target_compile_definitions(appscale_detours PRIVATE WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0601 DETOUR_DEBUG=0)
target_compile_options(appscale_detours PRIVATE /W3 /Zi /Gy)
# Detours predates /permissive- (creatwth.cpp: C2362 goto past initialization); compile it permissively.
target_compile_options(appscale_detours PRIVATE /permissive)

add_library(AppScale.Native SHARED
  AppScaleNative.cpp
  InjectedAgent.cpp
  Internal.h
  include/AppScaleInjection.h
  AppScaleNative.def
  include/AppScaleNative.h)
target_include_directories(AppScale.Native PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(AppScale.Native PRIVATE appscale_native_options appscale_detours user32 gdi32 shcore)
set_target_properties(AppScale.Native PROPERTIES OUTPUT_NAME "AppScale.Native")
