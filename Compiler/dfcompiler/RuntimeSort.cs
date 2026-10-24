namespace dfcompiler;

// C runtime for sort and sortUnstable of arrays (spec 6.6): added to a program only when it sorts.
// sort is a stable merge sort (it needs a temporary buffer of the size of the array); sortUnstable is a heap sort (no extra memory).
// The default comparers are the ordering of the items: numbers by value, strings by code point, a null date or time first,
// a real NaN last.
public static class RuntimeSort {
  public const string Source = """
/* ---- sort and sortUnstable (spec 6.6) ---- */

typedef int (*df_cmp_fn)(const void*, const void*);

#define DF_CMP_NUM(T, N) \
static int df_cmp_##N(const void* a, const void* b) { \
  T x = *(const T*)a; \
  T y = *(const T*)b; \
  return (x > y) - (x < y); \
}

DF_CMP_NUM(int8_t, i8)
DF_CMP_NUM(int16_t, i16)
DF_CMP_NUM(int32_t, i32)
DF_CMP_NUM(int64_t, i64)
DF_CMP_NUM(uint8_t, u8)
DF_CMP_NUM(uint16_t, u16)
DF_CMP_NUM(uint32_t, u32)
DF_CMP_NUM(uint64_t, u64)
DF_CMP_NUM(bool, bool)

#define DF_CMP_REAL(T, N) \
static int df_cmp_##N(const void* a, const void* b) { \
  T x = *(const T*)a; \
  T y = *(const T*)b; \
  if (x != x) return (y != y) ? 0 : 1; \
  if (y != y) return -1; \
  return (x > y) - (x < y); \
}

DF_CMP_REAL(float, r32)
DF_CMP_REAL(double, r64)

static int df_cmp_str(const void* a, const void* b) {
  df_string x = *(const df_string*)a;
  df_string y = *(const df_string*)b;
  uint64_t n = x.len < y.len ? x.len : y.len;
  int c = n ? memcmp(x.p, y.p, n) : 0;
  if (c != 0) return c < 0 ? -1 : 1;
  return (x.len > y.len) - (x.len < y.len);
}

static int df_sort_order(df_cmp_fn cmp, bool desc, const void* a, const void* b) {
  int c = cmp(a, b);
  return desc ? -c : c;
}

static void df_sort_merge(char* base, char* tmp, df_uns n, df_uns size, df_cmp_fn cmp, bool desc) {
  if (n < 2) return;
  df_uns mid = n / 2;
  df_sort_merge(base, tmp, mid, size, cmp, desc);
  df_sort_merge(base + mid * size, tmp, n - mid, size, cmp, desc);
  df_uns i = 0, j = mid, k = 0;
  while (i < mid && j < n) {
    /* the right item goes first only when it is strictly before the left one: equal items keep their order */
    if (df_sort_order(cmp, desc, base + j * size, base + i * size) < 0) {
      memcpy(tmp + k * size, base + j * size, size);
      j++;
    } else {
      memcpy(tmp + k * size, base + i * size, size);
      i++;
    }
    k++;
  }
  while (i < mid) { memcpy(tmp + k * size, base + i * size, size); i++; k++; }
  while (j < n) { memcpy(tmp + k * size, base + j * size, size); j++; k++; }
  memcpy(base, tmp, n * size);
}

static void df_sort_swap(char* a, char* b, df_uns size) {
  char chunk[64];
  while (size > 0) {
    df_uns part = size < sizeof chunk ? size : sizeof chunk;
    memcpy(chunk, a, part);
    memcpy(a, b, part);
    memcpy(b, chunk, part);
    a += part;
    b += part;
    size -= part;
  }
}

static void df_sort_sift(char* base, df_uns root, df_uns n, df_uns size, df_cmp_fn cmp, bool desc) {
  for (;;) {
    df_uns child = 2 * root + 1;
    if (child >= n) return;
    if (child + 1 < n && df_sort_order(cmp, desc, base + child * size, base + (child + 1) * size) < 0) child++;
    if (df_sort_order(cmp, desc, base + root * size, base + child * size) >= 0) return;
    df_sort_swap(base + root * size, base + child * size, size);
    root = child;
  }
}

static void df_sort(void* items, df_uns n, df_uns size, df_cmp_fn cmp, bool stable, bool desc) {
  char* base = (char*)items;
  if (n < 2 || !base) return;
  if (stable) {
    char* tmp = (char*)df_alloc(n * size);
    df_sort_merge(base, tmp, n, size, cmp, desc);
    free(tmp);
    return;
  }
  for (df_uns start = n / 2; start > 0; start--) df_sort_sift(base, start - 1, n, size, cmp, desc);
  for (df_uns end = n - 1; end > 0; end--) {
    df_sort_swap(base, base + end * size, size);
    df_sort_sift(base, 0, end, size, cmp, desc);
  }
}

/* sort and sortUnstable of a list: a bottom-up merge sort that relinks the nodes, with no temporary array (only the links move). Every node
   starts with the pointer to the next one; the item is at valueOffset. Equal items keep their order. Returns the new first node and sets the last */
typedef struct df_lnode { struct df_lnode* next; } df_lnode;

static df_lnode* df_list_sort(df_lnode* head, df_uns n, size_t valueOffset, df_cmp_fn cmp, bool desc, df_lnode** last) {
  for (df_uns width = 1; width < n; width *= 2) {
    df_lnode* rest = head;
    df_lnode* sorted = NULL;
    df_lnode* tail = NULL;
    while (rest != NULL) {
      df_lnode* left = rest;
      df_uns leftLen = 0;
      while (rest != NULL && leftLen < width) { rest = rest->next; leftLen++; }
      df_lnode* right = rest;
      df_uns rightLen = 0;
      while (rest != NULL && rightLen < width) { rest = rest->next; rightLen++; }
      while (leftLen > 0 || rightLen > 0) {
        df_lnode* take;
        /* the right node goes first only when its item is strictly before the left one */
        if (leftLen == 0 || (rightLen > 0 && df_sort_order(cmp, desc, (char*)right + valueOffset, (char*)left + valueOffset) < 0)) {
          take = right; right = right->next; rightLen--;
        } else {
          take = left; left = left->next; leftLen--;
        }
        if (tail == NULL) { sorted = take; } else { tail->next = take; }
        tail = take;
      }
    }
    tail->next = NULL;
    head = sorted;
    *last = tail;
  }
  return head;
}

""";
}
