# AppScale.Injector — loads AppScale.Native.dll into ONE user-chosen, same-user, non-elevated x64
# process (new via --launch, or existing via --inject). No global hooks, no persistence.
add_executable(AppScale.Injector main.cpp)
target_link_libraries(AppScale.Injector PRIVATE appscale_native_options appscale_detours advapi32 user32)
target_include_directories(AppScale.Injector PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../AppScale.Native/include")
target_link_options(AppScale.Injector PRIVATE "/MANIFESTUAC:level='asInvoker' uiAccess='false'")
# The DLL must sit next to the injector; both land in artifacts/bin/Win32Harness/<config>/.
add_dependencies(AppScale.Injector AppScale.Native)
