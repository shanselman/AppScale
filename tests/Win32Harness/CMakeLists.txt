add_executable(Win32Harness WIN32
    main.cpp
    Harness.cpp
    SelfTests.cpp
    Screenshot.cpp
    NativeAdapter.cpp
    Harness.h
    Json.h
    Win32Harness.manifest
)

target_include_directories(Win32Harness PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${PROJECT_SOURCE_DIR}/src/AppScale.Native/include
)

target_link_libraries(Win32Harness PRIVATE
    appscale_native_options
    user32 gdi32 comctl32 shcore windowscodecs ole32 shell32
)

# The embedded manifest above declares PerMonitorV2; do not let the linker add its own.
target_link_options(Win32Harness PRIVATE /MANIFEST:EMBED /MANIFESTUAC:NO)

# AppScale.Native.dll is loaded at runtime (opt-in) from the same directory; build it alongside
# but never link it, so the harness runs unmodified when the experiment is off.
add_dependencies(Win32Harness AppScale.Native)
