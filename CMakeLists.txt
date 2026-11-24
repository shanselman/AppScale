# AppScale native superbuild: Detours DPI adapter (AppScale.Native), its injector, and the Win32 ground-truth harness.
# Configure:  cmake -S . -B build/native -A x64
# Build:      cmake --build build/native --config Release
# Or:         scripts/build-native.ps1 -Configuration Release
cmake_minimum_required(VERSION 3.24)

project(AppScaleNative LANGUAGES C CXX)

if(NOT MSVC)
  message(FATAL_ERROR "AppScale native components require MSVC (Windows).")
endif()
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "Only x64 is validated so far. Configure with -A x64.")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
# Static CRT: harness + adapter run without a VC++ redistributable on test machines.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Per-configuration output: artifacts/bin/Win32Harness/<release|debug>/ (exe and dll side by side).
set(APPSCALE_NATIVE_OUT "${CMAKE_CURRENT_SOURCE_DIR}/artifacts/bin/Win32Harness")
foreach(cfg Debug Release RelWithDebInfo MinSizeRel)
  string(TOUPPER "${cfg}" CFG_UPPER)
  string(TOLOWER "${cfg}" cfg_lower)
  set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_${CFG_UPPER} "${APPSCALE_NATIVE_OUT}/${cfg_lower}")
  set(CMAKE_LIBRARY_OUTPUT_DIRECTORY_${CFG_UPPER} "${APPSCALE_NATIVE_OUT}/${cfg_lower}")
  set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY_${CFG_UPPER} "${CMAKE_BINARY_DIR}/lib/${cfg_lower}")
  set(CMAKE_PDB_OUTPUT_DIRECTORY_${CFG_UPPER} "${APPSCALE_NATIVE_OUT}/${cfg_lower}")
endforeach()

# Common flags for AppScale-owned targets (not third-party code).
add_library(appscale_native_options INTERFACE)
target_compile_definitions(appscale_native_options INTERFACE
  UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00 WINVER=0x0A00 NTDDI_VERSION=0x0A00000C)
target_compile_options(appscale_native_options INTERFACE /W4 /WX /permissive- /utf-8 /Zi /EHsc)
target_link_options(appscale_native_options INTERFACE /DEBUG $<$<NOT:$<CONFIG:Debug>>:/OPT:REF /OPT:ICF>)

add_subdirectory(src/AppScale.Native)
add_subdirectory(src/AppScale.Injector)
add_subdirectory(tests/Win32Harness)
