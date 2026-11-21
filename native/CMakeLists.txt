# Native Windows build of Hachidori's lookup engine for the FFXIV plugin:
# hoshidicts (pinned in sources.json) plus Hachidori's wasm/bindings.cpp glue,
# compiled with MSVC into hdw_native.dll. Driven by native/build.ps1, which
# fetches and patches the sources into native/src first.
cmake_minimum_required(VERSION 3.22.1)
project(hdw_native LANGUAGES C CXX)

set(HDW_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/src" CACHE PATH "Fetched sources")
set(HDW_HACHIDORI_VERSION "unknown" CACHE STRING "")
set(HDW_HACHIDORI_COMMIT "unknown" CACHE STRING "")
set(HDW_HOSHIDICTS_COMMIT "unknown" CACHE STRING "")
set(HDW_DICTIONARY_FORMAT "0" CACHE STRING "")

# One static CRT for everything, so the DLL needs no VC++ redistributable
# inside the game process.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

add_subdirectory("${HDW_SOURCE_DIR}/hoshidicts" hoshidicts EXCLUDE_FROM_ALL)

# zstd's override translation unit uses <stdatomic.h>.
set_target_properties(libzstd_static PROPERTIES C_STANDARD 11)
target_compile_options(libzstd_static PRIVATE /experimental:c11atomics)
# utfcpp picks its std::string overloads from __cplusplus.
target_compile_options(hoshidicts PRIVATE /Zc:__cplusplus)

add_library(hdw_native SHARED
    "${HDW_SOURCE_DIR}/hachidori/wasm/bindings.cpp"
    hdw_native_windows.cpp)
target_include_directories(hdw_native PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/compat"
    "${HDW_SOURCE_DIR}/hoshidicts/src")
target_compile_options(hdw_native PRIVATE /utf-8 /Zc:__cplusplus /EHsc /FImalloc_shim.h)
target_compile_features(hdw_native PRIVATE cxx_std_23)
target_compile_definitions(hdw_native PRIVATE
    HDW_HACHIDORI_VERSION="${HDW_HACHIDORI_VERSION}"
    HDW_HACHIDORI_COMMIT="${HDW_HACHIDORI_COMMIT}"
    HDW_HOSHIDICTS_COMMIT="${HDW_HOSHIDICTS_COMMIT}"
    HDW_DICTIONARY_FORMAT=${HDW_DICTIONARY_FORMAT})
target_link_libraries(hdw_native PRIVATE hoshidicts glaze::glaze unordered_dense utf8proc::utf8proc)
set_target_properties(hdw_native PROPERTIES WINDOWS_EXPORT_ALL_SYMBOLS ON)
