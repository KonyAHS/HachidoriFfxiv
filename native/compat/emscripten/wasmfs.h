// hdw_init_storage's OPFS backend is browser-only; the native build never calls it.
#pragma once
typedef void* backend_t;
inline backend_t wasmfs_create_opfs_backend(void) { return nullptr; }
inline int wasmfs_create_directory(const char*, int, backend_t) { return -1; }
