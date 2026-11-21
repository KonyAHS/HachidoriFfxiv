// hdw_memory_stats reads Emscripten's mallinfo(); the Windows CRT has none.
// Force-included into bindings.cpp. The plugin never calls hdw_memory_stats.
#pragma once
#include <malloc.h>
struct mallinfo { size_t uordblks; size_t arena; size_t fordblks; size_t hblkhd; };
inline struct mallinfo mallinfo() { return {}; }
