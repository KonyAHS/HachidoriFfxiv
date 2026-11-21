// libc++'s std::sort and std::partial_sort, for MSVC builds of hoshidicts.
//
// hoshidicts ranks lookup results with std::ranges::partial_sort and leaves
// Japanese ties to it on purpose ("ties keep the order they have always had",
// src/lookup.cpp). Hachidori's engine is built with Emscripten, i.e. libc++,
// and MSVC's sort and heap break ties differently, so equally ranked results
// came out in another order offline. This reproduces libc++'s algorithms
// (LLVM 22, as Emscripten ships it; libcxx/include/__algorithm): __introsort with its
// insertion sorts and pdqsort-style partitions, for a comparator that is not
// std::less on an arithmetic type, and __partial_sort_impl with __make_heap,
// __sift_down, __sort_heap, __pop_heap, __floyd_sift_down and __sift_up. The
// native engine then orders ties exactly as the browser does.
//
// Adapted from LLVM's libc++, Apache-2.0 WITH LLVM-exception.
#pragma once

#include <bit>
#include <iterator>
#include <type_traits>
#include <utility>

namespace hdw::libcxx {

// __sift_down. With AssumeBothChildren (make_heap over an arithmetic value
// type, LLVM 22) every parent inside `len` has two children.
template <bool AssumeBothChildren = false, class It, class Comp>
void sift_down(It first, Comp& comp, std::iter_difference_t<It> len, std::iter_difference_t<It> start) {
  using D = std::iter_difference_t<It>;
  D child = start;
  if (len < 2 || (len - 2) / 2 < child) return;
  child = 2 * child + 1;
  if constexpr (AssumeBothChildren) {
    child += comp(first[child], first[child + 1]);
  } else if ((child + 1) < len && comp(first[child], first[child + 1])) {
    ++child;
  }
  if (comp(first[child], first[start])) return;
  auto top = std::move(first[start]);
  do {
    first[start] = std::move(first[child]);
    start = child;
    if ((len - 2) / 2 < child) break;
    child = 2 * child + 1;
    if constexpr (AssumeBothChildren) {
      child += comp(first[child], first[child + 1]);
    } else if ((child + 1) < len && comp(first[child], first[child + 1])) {
      ++child;
    }
  } while (!comp(first[child], top));
  first[start] = std::move(top);
}

template <class It, class Comp>
void sift_up(It first, It last, Comp& comp, std::iter_difference_t<It> len);

// __make_heap. Since LLVM 22, a heap of an arithmetic value type (hoshidicts
// sorts uint32_t candidate indices) is built over an odd length, so every
// parent has two children, and an even range's last element is sifted up
// afterwards. That builds a different heap from older libc++ and so breaks
// ranking ties differently; Hachidori's Emscripten build has it.
template <class It, class Comp>
void make_heap(It first, It last, Comp& comp) {
  using D = std::iter_difference_t<It>;
  constexpr bool assume_both_children = std::is_arithmetic_v<std::iter_value_t<It>>;
  const D n = last - first;
  const D sift_down_n = assume_both_children ? ((n & 1) ? n : n - 1) : n;
  if (n > 1) {
    for (D start = (sift_down_n - 2) / 2; start >= 0; --start)
      libcxx::sift_down<assume_both_children>(first, comp, sift_down_n, start);
    if constexpr (assume_both_children) libcxx::sift_up(first, last, comp, n);
  }
}

template <class It, class Comp>
It floyd_sift_down(It first, Comp& comp, std::iter_difference_t<It> len) {
  using D = std::iter_difference_t<It>;
  It hole = first;
  It child_i = first;
  D child = 0;
  while (true) {
    child_i += D(child + 1);
    child = 2 * child + 1;
    if ((child + 1) < len && comp(*child_i, *(child_i + D(1)))) {
      ++child_i;
      ++child;
    }
    *hole = std::move(*child_i);
    hole = child_i;
    if (child > (len - 2) / 2) return hole;
  }
}

template <class It, class Comp>
void sift_up(It first, It last, Comp& comp, std::iter_difference_t<It> len) {
  if (len > 1) {
    len = (len - 2) / 2;
    It ptr = first + len;
    if (comp(*ptr, *--last)) {
      auto t = std::move(*last);
      do {
        *last = std::move(*ptr);
        last = ptr;
        if (len == 0) break;
        len = (len - 1) / 2;
        ptr = first + len;
      } while (comp(*ptr, t));
      *last = std::move(t);
    }
  }
}

template <class It, class Comp>
void pop_heap(It first, It last, Comp& comp, std::iter_difference_t<It> len) {
  if (len > 1) {
    auto top = std::move(*first);
    It hole = libcxx::floyd_sift_down(first, comp, len);
    --last;
    if (hole == last) {
      *hole = std::move(top);
    } else {
      *hole = std::move(*last);
      ++hole;
      *last = std::move(top);
      libcxx::sift_up(first, hole, comp, hole - first);
    }
  }
}

template <class It, class Comp>
void sort_heap(It first, It last, Comp& comp) {
  using D = std::iter_difference_t<It>;
  for (D n = last - first; n > 1; --last, (void)--n) libcxx::pop_heap(first, last, comp, n);
}

template <class It, class Comp>
It partial_sort(It first, It middle, It last, Comp comp) {
  if (first == middle) return last;
  libcxx::make_heap(first, middle, comp);
  const auto len = middle - first;
  It i = middle;
  for (; i != last; ++i) {
    if (comp(*i, *first)) {
      std::iter_swap(i, first);
      libcxx::sift_down(first, comp, len, 0);
    }
  }
  libcxx::sort_heap(first, middle, comp);
  return i;
}

// ------------------------------------------------------------------ std::sort

template <class It, class Comp>
void sort3(It x, It y, It z, Comp& c) {
  if (!c(*y, *x)) {
    if (!c(*z, *y)) return;
    std::iter_swap(y, z);
    if (c(*y, *x)) std::iter_swap(x, y);
    return;
  }
  if (c(*z, *y)) {
    std::iter_swap(x, z);
    return;
  }
  std::iter_swap(x, y);
  if (c(*z, *y)) std::iter_swap(y, z);
}

template <class It, class Comp>
void sort4(It x1, It x2, It x3, It x4, Comp& c) {
  libcxx::sort3(x1, x2, x3, c);
  if (c(*x4, *x3)) {
    std::iter_swap(x3, x4);
    if (c(*x3, *x2)) {
      std::iter_swap(x2, x3);
      if (c(*x2, *x1)) std::iter_swap(x1, x2);
    }
  }
}

template <class It, class Comp>
void sort5(It x1, It x2, It x3, It x4, It x5, Comp& c) {
  libcxx::sort4(x1, x2, x3, x4, c);
  if (c(*x5, *x4)) {
    std::iter_swap(x4, x5);
    if (c(*x4, *x3)) {
      std::iter_swap(x3, x4);
      if (c(*x3, *x2)) {
        std::iter_swap(x2, x3);
        if (c(*x2, *x1)) std::iter_swap(x1, x2);
      }
    }
  }
}

// __insertion_sort and __insertion_sort_unguarded: the same moves, the latter
// relying on an element to its left that stops the scan.
template <class It, class Comp>
void insertion_sort(It first, It last, Comp& comp, bool guarded) {
  if (first == last) return;
  It i = first;
  for (++i; i != last; ++i) {
    It j = i;
    --j;
    if (comp(*i, *j)) {
      auto t = std::move(*i);
      It k = j;
      j = i;
      do {
        *j = std::move(*k);
        j = k;
      } while ((!guarded || j != first) && comp(t, *--k));
      *j = std::move(t);
    }
  }
}

template <class It, class Comp>
bool insertion_sort_incomplete(It first, It last, Comp& comp) {
  using D = std::iter_difference_t<It>;
  switch (last - first) {
    case 0:
    case 1:
      return true;
    case 2:
      if (comp(*--last, *first)) std::iter_swap(first, last);
      return true;
    case 3:
      libcxx::sort3(first, first + D(1), --last, comp);
      return true;
    case 4:
      libcxx::sort4(first, first + D(1), first + D(2), --last, comp);
      return true;
    case 5:
      libcxx::sort5(first, first + D(1), first + D(2), first + D(3), --last, comp);
      return true;
  }
  It j = first + D(2);
  libcxx::sort3(first, first + D(1), j, comp);
  const unsigned limit = 8;
  unsigned count = 0;
  for (It i = j + D(1); i != last; ++i) {
    if (comp(*i, *j)) {
      auto t = std::move(*i);
      It k = j;
      j = i;
      do {
        *j = std::move(*k);
        j = k;
      } while (j != first && comp(t, *--k));
      *j = std::move(t);
      if (++count == limit) return ++i == last;
    }
    j = i;
  }
  return true;
}

template <class It, class Comp>
std::pair<It, bool> partition_with_equals_on_right(It first, It last, Comp& comp) {
  using D = std::iter_difference_t<It>;
  It begin = first;
  auto pivot = std::move(*first);
  do {
    ++first;
  } while (comp(*first, pivot));
  if (begin == first - D(1)) {
    while (first < last && !comp(*--last, pivot)) {
    }
  } else {
    while (!comp(*--last, pivot)) {
    }
  }
  const bool already_partitioned = first >= last;
  while (first < last) {
    std::iter_swap(first, last);
    do {
      ++first;
    } while (comp(*first, pivot));
    do {
      --last;
    } while (!comp(*last, pivot));
  }
  It pivot_pos = first - D(1);
  if (begin != pivot_pos) *begin = std::move(*pivot_pos);
  *pivot_pos = std::move(pivot);
  return {pivot_pos, already_partitioned};
}

template <class It, class Comp>
It partition_with_equals_on_left(It first, It last, Comp& comp) {
  using D = std::iter_difference_t<It>;
  It begin = first;
  auto pivot = std::move(*first);
  if (comp(pivot, *(last - D(1)))) {
    while (!comp(pivot, *++first)) {
    }
  } else {
    while (++first < last && !comp(pivot, *first)) {
    }
  }
  if (first < last) {
    while (comp(pivot, *--last)) {
    }
  }
  while (first < last) {
    std::iter_swap(first, last);
    do {
      ++first;
    } while (!comp(pivot, *first));
    do {
      --last;
    } while (comp(pivot, *last));
  }
  It pivot_pos = first - D(1);
  if (begin != pivot_pos) *begin = std::move(*pivot_pos);
  *pivot_pos = std::move(pivot);
  return first;
}

template <class It, class Comp>
void introsort(It first, It last, Comp& comp, std::iter_difference_t<It> depth, bool leftmost = true) {
  using D = std::iter_difference_t<It>;
  constexpr D limit = 24;
  constexpr D ninther_threshold = 128;
  while (true) {
    const D len = last - first;
    switch (len) {
      case 0:
      case 1:
        return;
      case 2:
        if (comp(*--last, *first)) std::iter_swap(first, last);
        return;
      case 3:
        libcxx::sort3(first, first + D(1), --last, comp);
        return;
      case 4:
        libcxx::sort4(first, first + D(1), first + D(2), --last, comp);
        return;
      case 5:
        libcxx::sort5(first, first + D(1), first + D(2), first + D(3), --last, comp);
        return;
    }
    if (len < limit) {
      libcxx::insertion_sort(first, last, comp, leftmost);
      return;
    }
    if (depth == 0) {
      libcxx::partial_sort(first, last, last, comp);
      return;
    }
    --depth;
    {
      const D half_len = len / 2;
      if (len > ninther_threshold) {
        libcxx::sort3(first, first + half_len, last - D(1), comp);
        libcxx::sort3(first + D(1), first + (half_len - 1), last - D(2), comp);
        libcxx::sort3(first + D(2), first + (half_len + 1), last - D(3), comp);
        libcxx::sort3(first + (half_len - 1), first + half_len, first + (half_len + 1), comp);
        std::iter_swap(first, first + half_len);
      } else {
        libcxx::sort3(first + half_len, first, last - D(1), comp);
      }
    }
    if (!leftmost && !comp(*(first - D(1)), *first)) {
      first = libcxx::partition_with_equals_on_left(first, last, comp);
      continue;
    }
    auto [i, partitioned] = libcxx::partition_with_equals_on_right(first, last, comp);
    if (partitioned) {
      const bool fs = libcxx::insertion_sort_incomplete(first, i, comp);
      if (libcxx::insertion_sort_incomplete(i + D(1), last, comp)) {
        if (fs) return;
        last = i;
        continue;
      }
      if (fs) {
        first = ++i;
        continue;
      }
    }
    libcxx::introsort(first, i, comp, depth, leftmost);
    leftmost = false;
    first = ++i;
  }
}

template <class It, class Comp>
void sort(It first, It last, Comp comp) {
  using D = std::iter_difference_t<It>;
  const D n = last - first;
  // 2 * __log2i(n): __log2i is the index of the highest set bit.
  const D depth = n > 0 ? 2 * static_cast<D>(std::bit_width(static_cast<std::make_unsigned_t<D>>(n)) - 1) : 0;
  libcxx::introsort(first, last, comp, depth);
}

}  // namespace hdw::libcxx
