// Windows-only additions to Hachidori's wasm/bindings.cpp for the FFXIV plugin.
// SPDX-License-Identifier: GPL-3.0-or-later
#include <clocale>
#include <string>

#ifndef HDW_HACHIDORI_VERSION
#define HDW_HACHIDORI_VERSION "unknown"
#endif
#ifndef HDW_HACHIDORI_COMMIT
#define HDW_HACHIDORI_COMMIT "unknown"
#endif
#ifndef HDW_HOSHIDICTS_COMMIT
#define HDW_HOSHIDICTS_COMMIT "unknown"
#endif
#ifndef HDW_DICTIONARY_FORMAT
#define HDW_DICTIONARY_FORMAT 0
#endif

#define HDW_STRINGIFY_(x) #x
#define HDW_STRINGIFY(x) HDW_STRINGIFY_(x)

extern "C" {

// Narrow paths reach std::filesystem::path, which converts them with the CRT
// locale's code page. The plugin passes UTF-8, so a profile folder with
// non-ASCII characters would otherwise not open. This DLL links the CRT
// statically, so the setting stays inside it. Returns 1 on success.
__declspec(dllexport) int hdw_native_init(void) {
  return std::setlocale(LC_CTYPE, ".UTF8") != nullptr ? 1 : 0;
}

// What this DLL was built from, as JSON.
__declspec(dllexport) const char* hdw_native_version(void) {
  static const std::string json =
      std::string{"{\"hachidori\":\""} + HDW_HACHIDORI_VERSION + "\",\"hachidoriCommit\":\"" + HDW_HACHIDORI_COMMIT +
      "\",\"hoshidictsCommit\":\"" + HDW_HOSHIDICTS_COMMIT +
      "\",\"dictionaryFormat\":" HDW_STRINGIFY(HDW_DICTIONARY_FORMAT) "}";
  return json.c_str();
}

}  // extern "C"
