namespace dfcompiler;

// C runtime of map<K, V> (spec 6.8.4, 6.8.4.1): a B+ tree, added to a program only when it uses a map (chunk "map").
// The tree knows a key as its sort key (a uint64_t that keeps the order of the key type, made by the generated code) plus, for the keys whose sort key
// is not exact (strings), a pointer to the key, compared with cmp when the sort keys are equal. It knows a value only as a block of vs bytes, so one
// runtime serves every map; the shape (vs, kp, cmp) is passed to every call. Leaf: an 8-byte header, the next and the previous leaf, 16 sort keys,
// 16 key pointers when kp, then 16 values. Branch: an 8-byte header, the separators (sort keys, then the key pointers when kp) and the children: 15 of
// each for exact keys, 10 with pointers (248 bytes on 64-bit targets). The separator of a child is its smallest key (the one of the first child is not
// read); with pointers it refers to the key owned by the leaf, so it is updated whenever that key leaves its place as the first of its leaf.
// Header: byte 0 the marker (bit 0 leaf, bit 2 key with pointer), bytes 1-2 the number of entries, bytes 3-4 the entries marked for a bulk removal.
// No parent pointers: insert and remove keep the path of the descent. Relaxed deletion: a node is fixed (merged with a neighbour, or the entries
// shared) only when it drops below 4 entries. The null key lives in the tree header, outside the tree.
public static class RuntimeMap {
  public const string Source = """
/* ---- map: the B+ tree of spec 6.8.4.1 ---- */

#define DF_BT_LEAF_N 16       /* entries of a leaf (spec 6.8.4.1) */
#define DF_BT_MIN 4           /* relaxed deletion: a node is fixed only below 4 entries */
#define DF_BT_LEAF_MARK 0x1   /* bit 0 of the marker: a leaf */
#define DF_BT_PTR_MARK 0x4    /* bit 2: keys with a pointer */
#define DF_BT_DEPTH 48
#define DF_BT_MAX_BRANCH 15

typedef int (*df_bt_cmp)(const void*, const void*);

typedef struct {
  size_t vs;          /* the size of a value */
  bool kp;            /* the keys have a pointer (their sort key is not exact) */
  df_bt_cmp cmp;      /* the order of two key pointers with the same sort key */
  unsigned bn;        /* the children of a branch */
} df_bt_shape;

typedef struct df_bt_leaf {
  uint8_t head[8];
  struct df_bt_leaf* next;
  struct df_bt_leaf* prev;
  /* then DF_BT_LEAF_N sort keys, DF_BT_LEAF_N key pointers when kp, DF_BT_LEAF_N values */
} df_bt_leaf;

typedef struct df_bt {
  void* root;
  df_bt_leaf* first;
  df_bt_leaf* last;
  df_uns count;          /* the entries, the null key included */
  uint32_t height;       /* the levels of branches above the leaves */
  bool has_null;
  void* null_value;      /* the value of the null key */
} df_bt;

typedef struct {
  void* node[DF_BT_DEPTH];
  unsigned at[DF_BT_DEPTH];
  unsigned depth;
} df_bt_path;

static inline df_bt_shape df_bt_shape_of(size_t vs, bool kp, df_bt_cmp cmp) {
  df_bt_shape s = { vs, kp, cmp, kp ? 10u : 15u };
  return s;
}

static inline unsigned df_bt_n(const void* node) {
  const uint8_t* h = (const uint8_t*)node;
  return (unsigned)h[1] | ((unsigned)h[2] << 8);
}

static inline void df_bt_setn(void* node, unsigned n) {
  uint8_t* h = (uint8_t*)node;
  h[1] = (uint8_t)n;
  h[2] = (uint8_t)(n >> 8);
}

static inline unsigned df_bt_marks(const df_bt_leaf* leaf) {
  return (unsigned)leaf->head[3] | ((unsigned)leaf->head[4] << 8);
}

static inline void df_bt_setmarks(df_bt_leaf* leaf, unsigned marks) {
  leaf->head[3] = (uint8_t)marks;
  leaf->head[4] = (uint8_t)(marks >> 8);
}

/* the arrays of a leaf */
static inline uint64_t* df_bt_lk(const void* leaf) {
  return (uint64_t*)((uint8_t*)leaf + sizeof(df_bt_leaf));
}

static inline void** df_bt_lp(const void* leaf) {
  return (void**)(df_bt_lk(leaf) + DF_BT_LEAF_N);
}

static inline uint8_t* df_bt_lv(const void* leaf, const df_bt_shape* s) {
  return s->kp ? (uint8_t*)(df_bt_lp(leaf) + DF_BT_LEAF_N) : (uint8_t*)(df_bt_lk(leaf) + DF_BT_LEAF_N);
}

static inline uint8_t* df_bt_val(const void* leaf, unsigned i, const df_bt_shape* s) {
  return df_bt_lv(leaf, s) + (size_t)i * s->vs;
}

/* the arrays of a branch */
static inline uint64_t* df_bt_bk(const void* branch) {
  return (uint64_t*)((uint8_t*)branch + 8);
}

static inline void** df_bt_bp(const void* branch, const df_bt_shape* s) {
  return (void**)(df_bt_bk(branch) + s->bn);
}

static inline void** df_bt_kids(const void* branch, const df_bt_shape* s) {
  return s->kp ? df_bt_bp(branch, s) + s->bn : (void**)(df_bt_bk(branch) + s->bn);
}

/* the order of two keys: the sort keys, then the pointers when they are equal */
static inline int df_bt_kcmp(const df_bt_shape* s, uint64_t a, const void* ap, uint64_t b, const void* bp) {
  if (a != b) {
    return a < b ? -1 : 1;
  }
  return s->kp ? s->cmp(ap, bp) : 0;
}

/* the sort key of a real: -0.0 is +0.0 and every NaN the canonical one, which sorts after every number (spec 6.2.2) */
static inline uint64_t df_bt_key_real(double value) {
  if (value != value) {
    return 0xFFF8000000000000ULL;
  }
  if (value == 0.0) {
    value = 0.0;
  }
  uint64_t bits;
  memcpy(&bits, &value, 8);
  return (bits & 0x8000000000000000ULL) ? ~bits : bits | 0x8000000000000000ULL;
}

static inline double df_bt_unkey_real(uint64_t key) {
  uint64_t bits = (key & 0x8000000000000000ULL) ? key & 0x7FFFFFFFFFFFFFFFULL : ~key;
  double value;
  memcpy(&value, &bits, 8);
  return value;
}

/* the sort key of a string: its first 8 bytes, big-endian, 0 after the end (spec 6.8.4.1) */
static uint64_t df_bt_key_str(df_string text) {
  df_sv v = df_view(text);
  uint64_t key = 0;
  for (unsigned i = 0; i < 8; i++) {
    key = (key << 8) | (i < v.len ? (uint8_t)v.p[i] : 0);
  }
  return key;
}

static int df_bt_cmp_str(const void* a, const void* b) {
  df_sv x = df_view((df_string)a);
  df_sv y = df_view((df_string)b);
  uint64_t n = x.len < y.len ? x.len : y.len;
  int c = n ? memcmp(x.p, y.p, n) : 0;
  if (c != 0) {
    return c < 0 ? -1 : 1;
  }
  return (x.len > y.len) - (x.len < y.len);
}

/* a string value inline (spec 6.8.4.1): its 64-byte slot holds the first allocation of the string, a single-block string of class 0 or 1 (up to 59
   bytes of text) or a moved string (class 13) that points to the real string, which never moves. A zeroed slot is "". The slot moves with its entry,
   so the string of a class 0 or 1 slot is read in place only while the tree does not change (a statement, or a loop that holds the counter) */
#define DF_BT_SVAL 64

static inline bool df_bt_sval_moved(const uint8_t* slot) {
  return (slot[0] & 0x0F) == DF_STR_MOVED;
}

static inline df_string df_bt_sval_target(const uint8_t* slot) {
  df_string to;
  memcpy(&to, slot + 4, sizeof to);
  return to;
}

/* the string of a slot, read in place: a borrow */
static inline df_string df_bt_sval(uint8_t* slot) {
  if (df_bt_sval_moved(slot)) {
    return df_bt_sval_target(slot);
  }
  return (slot[0] >> 4) == 0 && slot[1] == 0 ? NULL : (df_string)slot;
}

/* the slot gives up what it holds */
static void df_bt_sval_clear(uint8_t* slot) {
  if (df_bt_sval_moved(slot)) {
    df_str_free(df_bt_sval_target(slot));
  }
  memset(slot, 0, DF_BT_SVAL);
}

/* the slot takes value, which the caller gives (its own string): a short one is copied in and freed, a long one is pointed to */
static void df_bt_sval_put(uint8_t* slot, df_string value) {
  df_bt_sval_clear(slot);
  df_sv v = df_view(value);
  if (v.len <= 59) {
    unsigned cls = v.len <= 27 ? 0 : 1;
    slot[0] = (uint8_t)(cls | ((v.len & 0x0F) << 4));
    slot[1] = (uint8_t)(v.len >> 4);
    uint16_t chars = (uint16_t)df_str_chars(value);
    memcpy(slot + 2, &chars, sizeof chars);
    if (v.len > 0) {
      memcpy(slot + 4, v.p, v.len);
    }
    slot[4 + v.len] = 0;
    df_str_free(value);
  } else {
    slot[0] = DF_STR_MOVED;
    memcpy(slot + 4, &value, sizeof value);
  }
}

/* the string leaves the slot: the caller owns it */
static df_string df_bt_sval_take(uint8_t* slot) {
  df_string taken = df_bt_sval_moved(slot) ? df_bt_sval_target(slot) : df_str_dup(df_bt_sval(slot));
  memset(slot, 0, DF_BT_SVAL);
  return taken;
}

static void* df_bt_new_leaf(const df_bt_shape* s) {
  size_t size = sizeof(df_bt_leaf) + DF_BT_LEAF_N * (sizeof(uint64_t) + (s->kp ? sizeof(void*) : 0) + s->vs);
  df_bt_leaf* leaf = (df_bt_leaf*)df_alloc(size);
  memset(leaf, 0, size);
  leaf->head[0] = DF_BT_LEAF_MARK | (s->kp ? DF_BT_PTR_MARK : 0);
  return leaf;
}

static size_t df_bt_branch_size(const df_bt_shape* s) {
  return 8 + s->bn * (sizeof(uint64_t) + (s->kp ? sizeof(void*) : 0) + sizeof(void*));
}

static void* df_bt_new_branch(const df_bt_shape* s) {
  uint8_t* branch = (uint8_t*)df_alloc(df_bt_branch_size(s));
  memset(branch, 0, df_bt_branch_size(s));
  branch[0] = s->kp ? DF_BT_PTR_MARK : 0;
  return branch;
}

/* entries of leaves: count of them from position si of src to position di of dst (the same leaf is fine) */
static void df_bt_lmove(void* dst, unsigned di, const void* src, unsigned si, unsigned count, const df_bt_shape* s) {
  if (count == 0) {
    return;
  }
  memmove(df_bt_lk(dst) + di, df_bt_lk(src) + si, count * sizeof(uint64_t));
  if (s->kp) {
    memmove(df_bt_lp(dst) + di, df_bt_lp(src) + si, count * sizeof(void*));
  }
  memmove(df_bt_val(dst, di, s), df_bt_val(src, si, s), count * s->vs);
}

/* separators and children of branches, the same way */
static void df_bt_bmove(void* dst, unsigned di, const void* src, unsigned si, unsigned count, const df_bt_shape* s) {
  if (count == 0) {
    return;
  }
  memmove(df_bt_bk(dst) + di, df_bt_bk(src) + si, count * sizeof(uint64_t));
  if (s->kp) {
    memmove(df_bt_bp(dst, s) + di, df_bt_bp(src, s) + si, count * sizeof(void*));
  }
  memmove(df_bt_kids(dst, s) + di, df_bt_kids(src, s) + si, count * sizeof(void*));
}

static inline void df_bt_setsep(void* branch, unsigned i, uint64_t key, void* ptr, const df_bt_shape* s) {
  df_bt_bk(branch)[i] = key;
  if (s->kp) {
    df_bt_bp(branch, s)[i] = ptr;
  }
}

static inline void* df_bt_lptr(const void* leaf, unsigned i, const df_bt_shape* s) {
  return s->kp ? df_bt_lp(leaf)[i] : NULL;
}

static inline void* df_bt_bptr(const void* branch, unsigned i, const df_bt_shape* s) {
  return s->kp ? df_bt_bp(branch, s)[i] : NULL;
}

/* the position of a key in a leaf, or where it would go */
static unsigned df_bt_leaf_pos(const void* leaf, uint64_t key, const void* ptr, const df_bt_shape* s, bool* found) {
  unsigned lo = 0, hi = df_bt_n(leaf);
  const uint64_t* keys = df_bt_lk(leaf);
  while (lo < hi) {
    unsigned mid = (lo + hi) / 2;
    if (df_bt_kcmp(s, keys[mid], df_bt_lptr(leaf, mid, s), key, ptr) < 0) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }
  *found = lo < df_bt_n(leaf) && df_bt_kcmp(s, keys[lo], df_bt_lptr(leaf, lo, s), key, ptr) == 0;
  return lo;
}

/* the child of a branch that holds a key: the last one whose separator is not above it (the first separator is not read) */
static unsigned df_bt_child(const void* branch, uint64_t key, const void* ptr, const df_bt_shape* s) {
  unsigned lo = 1, hi = df_bt_n(branch);
  const uint64_t* seps = df_bt_bk(branch);
  while (lo < hi) {
    unsigned mid = (lo + hi) / 2;
    if (df_bt_kcmp(s, seps[mid], df_bt_bptr(branch, mid, s), key, ptr) <= 0) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }
  return lo - 1;
}

static void* df_bt_descend(const df_bt* t, uint64_t key, const void* ptr, const df_bt_shape* s, df_bt_path* path) {
  void* node = t->root;
  path->depth = 0;
  for (uint32_t level = 0; level < t->height; level++) {
    unsigned at = df_bt_child(node, key, ptr, s);
    path->node[path->depth] = node;
    path->at[path->depth] = at;
    path->depth++;
    node = df_bt_kids(node, s)[at];
  }
  return node;
}

/* the value of a key, or NULL */
static void* df_bt_find(const df_bt* t, uint64_t key, const void* ptr, size_t vs, bool kp, df_bt_cmp cmp) {
  if (t->root == NULL) {
    return NULL;
  }
  df_bt_shape s = df_bt_shape_of(vs, kp, cmp);
  void* node = t->root;
  for (uint32_t level = 0; level < t->height; level++) {
    node = df_bt_kids(node, &s)[df_bt_child(node, key, ptr, &s)];
  }
  bool found;
  unsigned at = df_bt_leaf_pos(node, key, ptr, &s, &found);
  return found ? df_bt_val(node, at, &s) : NULL;
}

/* a zeroed entry for a key at position at of a leaf that has room */
static void df_bt_leaf_open(void* leaf, unsigned at, uint64_t key, void* ptr, const df_bt_shape* s) {
  unsigned n = df_bt_n(leaf);
  df_bt_lmove(leaf, at + 1, leaf, at, n - at, s);
  df_bt_lk(leaf)[at] = key;
  if (s->kp) {
    df_bt_lp(leaf)[at] = ptr;
  }
  memset(df_bt_val(leaf, at, s), 0, s->vs);
  df_bt_setn(leaf, n + 1);
}

/* after a split: the new right node and its smallest key go into the parent; the full branches up the path split too */
static void df_bt_insert_up(df_bt* t, df_bt_path* path, uint64_t sep, void* sepPtr, void* right, const df_bt_shape* s) {
  while (path->depth > 0) {
    path->depth--;
    void* branch = path->node[path->depth];
    unsigned at = path->at[path->depth] + 1;
    unsigned n = df_bt_n(branch);
    if (n < s->bn) {
      df_bt_bmove(branch, at + 1, branch, at, n - at, s);
      df_bt_setsep(branch, at, sep, sepPtr, s);
      df_bt_kids(branch, s)[at] = right;
      df_bt_setn(branch, n + 1);
      return;
    }
    /* the full branch and the new child, in order, in a scratch branch with one more place */
    uint64_t seps[DF_BT_MAX_BRANCH + 1];
    void* ptrs[DF_BT_MAX_BRANCH + 1];
    void* kids[DF_BT_MAX_BRANCH + 1];
    for (unsigned i = 0, j = 0; i <= n; i++) {
      if (i == at) {
        seps[i] = sep;
        ptrs[i] = sepPtr;
        kids[i] = right;
      } else {
        seps[i] = df_bt_bk(branch)[j];
        ptrs[i] = df_bt_bptr(branch, j, s);
        kids[i] = df_bt_kids(branch, s)[j];
        j++;
      }
    }
    unsigned total = n + 1;
    unsigned half = total / 2;
    void* other = df_bt_new_branch(s);
    for (unsigned i = 0; i < total; i++) {
      void* into = i < half ? branch : other;
      unsigned place = i < half ? i : i - half;
      df_bt_setsep(into, place, seps[i], ptrs[i], s);
      df_bt_kids(into, s)[place] = kids[i];
    }
    df_bt_setn(branch, half);
    df_bt_setn(other, total - half);
    sep = seps[half];
    sepPtr = ptrs[half];
    right = other;
  }
  void* root = df_bt_new_branch(s);
  df_bt_kids(root, s)[0] = t->root;
  df_bt_kids(root, s)[1] = right;
  df_bt_setsep(root, 1, sep, sepPtr, s);
  df_bt_setn(root, 2);
  t->root = root;
  t->height++;
}

/* the value of a key: the existing one, or a new zeroed one (*added is then true; the tree keeps ptr as the key) */
static void* df_bt_put(df_bt* t, uint64_t key, void* ptr, size_t vs, bool kp, df_bt_cmp cmp, bool* added) {
  df_bt_shape s = df_bt_shape_of(vs, kp, cmp);
  *added = false;
  if (t->root == NULL) {
    void* leaf = df_bt_new_leaf(&s);
    t->root = leaf;
    t->first = (df_bt_leaf*)leaf;
    t->last = (df_bt_leaf*)leaf;
    t->height = 0;
  }
  df_bt_path path;
  df_bt_leaf* leaf = (df_bt_leaf*)df_bt_descend(t, key, ptr, &s, &path);
  bool found;
  unsigned at = df_bt_leaf_pos(leaf, key, ptr, &s, &found);
  if (found) {
    return df_bt_val(leaf, at, &s);
  }
  *added = true;
  t->count++;
  unsigned n = df_bt_n(leaf);
  if (n < DF_BT_LEAF_N) {
    df_bt_leaf_open(leaf, at, key, ptr, &s);
    if (at == 0 && path.depth > 0) {
      /* a new smallest key of the leaf: the separator that refers to the old one follows (the path took this leaf) */
      for (unsigned d = path.depth; d > 0; d--) {
        if (path.at[d - 1] > 0) {
          df_bt_setsep(path.node[d - 1], path.at[d - 1], key, ptr, &s);
          break;
        }
        df_bt_setsep(path.node[d - 1], 0, key, ptr, &s);
      }
    }
    return df_bt_val(leaf, at, &s);
  }
  /* the leaf is full: its upper half goes to a new leaf on its right */
  unsigned half = DF_BT_LEAF_N / 2;
  df_bt_leaf* right = (df_bt_leaf*)df_bt_new_leaf(&s);
  df_bt_lmove(right, 0, leaf, half, n - half, &s);
  memset(df_bt_val(leaf, half, &s), 0, (n - half) * vs);
  df_bt_setn(right, n - half);
  df_bt_setn(leaf, half);
  right->next = leaf->next;
  right->prev = leaf;
  if (leaf->next != NULL) {
    leaf->next->prev = right;
  } else {
    t->last = right;
  }
  leaf->next = right;
  df_bt_leaf* target = at <= half ? leaf : right;
  unsigned place = at <= half ? at : at - half;
  df_bt_leaf_open(target, place, key, ptr, &s);
  if (target == leaf && place == 0 && path.depth > 0) {
    for (unsigned d = path.depth; d > 0; d--) {
      if (path.at[d - 1] > 0) {
        df_bt_setsep(path.node[d - 1], path.at[d - 1], key, ptr, &s);
        break;
      }
      df_bt_setsep(path.node[d - 1], 0, key, ptr, &s);
    }
  }
  df_bt_insert_up(t, &path, df_bt_lk(right)[0], df_bt_lptr(right, 0, &s), right, &s);
  return df_bt_val(target, place, &s);
}

/* the smallest key below child index of the branch at path level d changed: its separator follows, and up the path while it is the first child */
static void df_bt_new_min(df_bt_path* path, unsigned d, unsigned index, uint64_t key, void* ptr, const df_bt_shape* s) {
  for (;;) {
    df_bt_setsep(path->node[d], index, key, ptr, s);
    if (index > 0 || d == 0) {
      return;
    }
    d--;
    index = path->at[d];
  }
}

static void df_bt_free_node(void* node, uint32_t height, const df_bt_shape* s) {
  if (height > 0) {
    for (unsigned i = 0; i < df_bt_n(node); i++) {
      df_bt_free_node(df_bt_kids(node, s)[i], height - 1, s);
    }
  }
  free(node);
}

/* moves entries between two neighbour leaves so that each holds about half */
static void df_bt_leaf_share(void* a, void* b, const df_bt_shape* s) {
  unsigned na = df_bt_n(a), nb = df_bt_n(b);
  unsigned keep = (na + nb) / 2;
  if (na > keep) {
    unsigned k = na - keep;
    df_bt_lmove(b, k, b, 0, nb, s);
    df_bt_lmove(b, 0, a, keep, k, s);
    memset(df_bt_val(a, keep, s), 0, k * s->vs);
    df_bt_setn(a, keep);
    df_bt_setn(b, nb + k);
  } else if (na < keep) {
    unsigned k = keep - na;
    df_bt_lmove(a, na, b, 0, k, s);
    df_bt_lmove(b, 0, b, k, nb - k, s);
    memset(df_bt_val(b, nb - k, s), 0, k * s->vs);
    df_bt_setn(a, keep);
    df_bt_setn(b, nb - k);
  }
}

/* the same for two neighbour branches; (*sep, *sepPtr) is the smallest key below b, its separator in the parent, and follows the change */
static void df_bt_branch_share(void* a, void* b, uint64_t* sep, void** sepPtr, const df_bt_shape* s) {
  unsigned na = df_bt_n(a), nb = df_bt_n(b);
  unsigned keep = (na + nb) / 2;
  if (na > keep) {
    unsigned k = na - keep;
    df_bt_bmove(b, k, b, 0, nb, s);
    df_bt_setsep(b, k, *sep, *sepPtr, s);
    df_bt_bmove(b, 0, a, keep, k, s);
    df_bt_setn(a, keep);
    df_bt_setn(b, nb + k);
    *sep = df_bt_bk(b)[0];
    *sepPtr = df_bt_bptr(b, 0, s);
  } else if (na < keep) {
    unsigned k = keep - na;
    df_bt_bmove(a, na, b, 0, k, s);
    df_bt_setsep(a, na, *sep, *sepPtr, s);
    *sep = df_bt_bk(b)[k];
    *sepPtr = df_bt_bptr(b, k, s);
    df_bt_bmove(b, 0, b, k, nb - k, s);
    df_bt_setn(a, keep);
    df_bt_setn(b, nb - k);
  }
}

/* node (the child at the end of the path) lost entries: fix it when it is below the minimum, and its parents when they lose a child */
static void df_bt_rebalance(df_bt* t, df_bt_path* path, void* node, const df_bt_shape* s) {
  unsigned depth = path->depth;
  bool leaf = true;
  for (;;) {
    unsigned n = df_bt_n(node);
    if (depth == 0) {
      if (leaf && n == 0) {
        free(node);
        t->root = NULL;
        t->first = NULL;
        t->last = NULL;
        t->height = 0;
      } else if (!leaf && n == 1) {
        t->root = df_bt_kids(node, s)[0];
        free(node);
        t->height--;
      }
      return;
    }
    void* parent = path->node[depth - 1];
    unsigned at = path->at[depth - 1];
    unsigned pn = df_bt_n(parent);
    if (n >= DF_BT_MIN) {
      return;
    }
    if (pn < 2) {
      node = parent;
      depth--;
      leaf = false;
      continue;
    }
    unsigned left = at > 0 ? at - 1 : at;
    void* a = df_bt_kids(parent, s)[left];
    void* b = df_bt_kids(parent, s)[left + 1];
    unsigned na = df_bt_n(a), nb = df_bt_n(b);
    if (na + nb <= (leaf ? DF_BT_LEAF_N : s->bn)) {
      if (leaf) {
        df_bt_lmove(a, na, b, 0, nb, s);
        df_bt_leaf* la = (df_bt_leaf*)a;
        df_bt_leaf* lb = (df_bt_leaf*)b;
        la->next = lb->next;
        if (lb->next != NULL) {
          lb->next->prev = la;
        } else {
          t->last = la;
        }
      } else {
        df_bt_bmove(a, na, b, 0, nb, s);
        df_bt_setsep(a, na, df_bt_bk(parent)[left + 1], df_bt_bptr(parent, left + 1, s), s);
      }
      df_bt_setn(a, na + nb);
      free(b);
      df_bt_bmove(parent, left + 1, parent, left + 2, pn - left - 2, s);
      df_bt_setn(parent, pn - 1);
      if (leaf && na == 0) {
        df_bt_new_min(path, depth - 1, left, df_bt_lk(a)[0], df_bt_lptr(a, 0, s), s);
      }
      node = parent;
      depth--;
      leaf = false;
      continue;
    }
    if (leaf) {
      df_bt_leaf_share(a, b, s);
      df_bt_setsep(parent, left + 1, df_bt_lk(b)[0], df_bt_lptr(b, 0, s), s);
      if (na == 0) {
        df_bt_new_min(path, depth - 1, left, df_bt_lk(a)[0], df_bt_lptr(a, 0, s), s);
      }
    } else {
      uint64_t sep = df_bt_bk(parent)[left + 1];
      void* sepPtr = df_bt_bptr(parent, left + 1, s);
      df_bt_branch_share(a, b, &sep, &sepPtr, s);
      df_bt_setsep(parent, left + 1, sep, sepPtr, s);
    }
    return;
  }
}

/* removes a key; its value is copied to out and its key pointer to outKey when they are not NULL; false when the key is not there */
static bool df_bt_remove(df_bt* t, uint64_t key, const void* ptr, size_t vs, bool kp, df_bt_cmp cmp, void* out, void** outKey) {
  if (t->root == NULL) {
    return false;
  }
  df_bt_shape s = df_bt_shape_of(vs, kp, cmp);
  df_bt_path path;
  void* leaf = df_bt_descend(t, key, ptr, &s, &path);
  bool found;
  unsigned at = df_bt_leaf_pos(leaf, key, ptr, &s, &found);
  if (!found) {
    return false;
  }
  if (out != NULL) {
    memcpy(out, df_bt_val(leaf, at, &s), vs);
  }
  if (outKey != NULL) {
    *outKey = df_bt_lptr(leaf, at, &s);
  }
  unsigned n = df_bt_n(leaf);
  df_bt_lmove(leaf, at, leaf, at + 1, n - at - 1, &s);
  memset(df_bt_val(leaf, n - 1, &s), 0, vs);
  df_bt_setn(leaf, n - 1);
  t->count--;
  if (at == 0 && n > 1 && path.depth > 0) {
    df_bt_new_min(&path, path.depth - 1, path.at[path.depth - 1], df_bt_lk(leaf)[0], df_bt_lptr(leaf, 0, &s), &s);
  }
  df_bt_rebalance(t, &path, leaf, &s);
  return true;
}

/* the value of the null key (spec 6.8.4.1: in the header, outside the tree) */
static void* df_bt_put_null(df_bt* t, size_t vs, bool* added) {
  *added = !t->has_null;
  if (!t->has_null) {
    if (t->null_value == NULL) {
      t->null_value = df_alloc(vs);
    }
    memset(t->null_value, 0, vs);
    t->has_null = true;
    t->count++;
  }
  return t->null_value;
}

static void* df_bt_find_null(const df_bt* t) {
  return t->has_null ? t->null_value : NULL;
}

static bool df_bt_remove_null(df_bt* t, size_t vs, void* out) {
  if (!t->has_null) {
    return false;
  }
  if (out != NULL) {
    memcpy(out, t->null_value, vs);
  }
  t->has_null = false;
  t->count--;
  return true;
}

static uint64_t df_bt_node_bytes(const void* node, uint32_t height, const df_bt_shape* s) {
  if (height == 0) {
    return sizeof(df_bt_leaf) + DF_BT_LEAF_N * (sizeof(uint64_t) + (s->kp ? sizeof(void*) : 0) + s->vs);
  }
  uint64_t bytes = df_bt_branch_size(s);
  for (unsigned i = 0; i < df_bt_n(node); i++) {
    bytes += df_bt_node_bytes(df_bt_kids(node, s)[i], height - 1, s);
  }
  return bytes;
}

/* the memory of the nodes and of the slot of the null key (.sizeof, spec 6.6) */
static uint64_t df_bt_bytes(const df_bt* t, size_t vs, bool kp) {
  df_bt_shape s = df_bt_shape_of(vs, kp, NULL);
  return (t->root != NULL ? df_bt_node_bytes(t->root, t->height, &s) : 0) + (t->null_value != NULL ? vs : 0);
}

/* all the entries go; the keys and the values must have been dropped before */
static void df_bt_clear(df_bt* t, bool kp) {
  df_bt_shape s = df_bt_shape_of(0, kp, NULL);
  if (t->root != NULL) {
    df_bt_free_node(t->root, t->height, &s);
  }
  free(t->null_value);
  t->root = NULL;
  t->first = NULL;
  t->last = NULL;
  t->count = 0;
  t->height = 0;
  t->has_null = false;
  t->null_value = NULL;
}

/* a walk over the entries in key order: the null key first, then the leaves */
typedef struct {
  df_bt_leaf* leaf;
  unsigned at;
  bool on_null;
} df_bt_cur;

static inline df_bt_cur df_bt_begin(const df_bt* t) {
  df_bt_cur cur = { t->first, 0, t->has_null };
  return cur;
}

static inline bool df_bt_valid(df_bt_cur* cur) {
  if (cur->on_null) {
    return true;
  }
  while (cur->leaf != NULL && cur->at >= df_bt_n(cur->leaf)) {
    cur->leaf = cur->leaf->next;
    cur->at = 0;
  }
  return cur->leaf != NULL;
}

static inline void df_bt_step(df_bt_cur* cur) {
  if (cur->on_null) {
    cur->on_null = false;
  } else {
    cur->at++;
  }
}

static inline uint64_t df_bt_cur_key(const df_bt_cur* cur) {
  return df_bt_lk(cur->leaf)[cur->at];
}

static inline void* df_bt_cur_ptr(const df_bt_cur* cur) {
  return df_bt_lp(cur->leaf)[cur->at];
}

static inline void* df_bt_cur_value(const df_bt* t, const df_bt_cur* cur, size_t vs, bool kp) {
  if (cur->on_null) {
    return t->null_value;
  }
  df_bt_shape s = df_bt_shape_of(vs, kp, NULL);
  return df_bt_val(cur->leaf, cur->at, &s);
}

/* marks the entry under the cursor for df_bt_sweep (the null key is removed with df_bt_remove_null) */
static inline void df_bt_mark(df_bt_cur* cur) {
  df_bt_setmarks(cur->leaf, df_bt_marks(cur->leaf) | (1u << cur->at));
}

/* bulk removal (spec 6.8.4.1): the entries marked go in one pass (their keys and values must have been dropped), the others are packed into new
   leaves and the branches are built again once. Returns the number removed */
static df_uns df_bt_sweep(df_bt* t, size_t vs, bool kp) {
  if (t->root == NULL) {
    return 0;
  }
  df_bt_shape s = df_bt_shape_of(vs, kp, NULL);
  df_uns removed = 0;
  df_uns leaves = 0;
  df_bt_leaf* first = NULL;
  df_bt_leaf* last = NULL;
  for (df_bt_leaf* leaf = t->first; leaf != NULL; leaf = leaf->next) {
    unsigned marks = df_bt_marks(leaf);
    for (unsigned i = 0; i < df_bt_n(leaf); i++) {
      if (marks & (1u << i)) {
        removed++;
        continue;
      }
      if (last == NULL || df_bt_n(last) == DF_BT_LEAF_N) {
        df_bt_leaf* fresh = (df_bt_leaf*)df_bt_new_leaf(&s);
        fresh->prev = last;
        if (last != NULL) {
          last->next = fresh;
        } else {
          first = fresh;
        }
        last = fresh;
        leaves++;
      }
      unsigned n = df_bt_n(last);
      df_bt_lmove(last, n, leaf, i, 1, &s);
      df_bt_setn(last, n + 1);
    }
  }
  df_bt_free_node(t->root, t->height, &s);
  t->count -= removed;
  t->root = NULL;
  t->height = 0;
  t->first = first;
  t->last = last;
  if (first == NULL) {
    return removed;
  }
  if (last != first && df_bt_n(last) < DF_BT_MIN) {
    df_bt_leaf_share(last->prev, last, &s);
  }
  void** level = (void**)df_alloc(leaves * sizeof(void*));
  uint64_t* mins = (uint64_t*)df_alloc(leaves * sizeof(uint64_t));
  void** minPtrs = (void**)df_alloc(leaves * sizeof(void*));
  df_uns count = 0;
  for (df_bt_leaf* leaf = first; leaf != NULL; leaf = leaf->next) {
    level[count] = leaf;
    mins[count] = df_bt_lk(leaf)[0];
    minPtrs[count] = df_bt_lptr(leaf, 0, &s);
    count++;
  }
  uint32_t height = 0;
  while (count > 1) {
    df_uns groups = (count + s.bn - 1) / s.bn;
    df_uns taken = 0;
    for (df_uns g = 0; g < groups; g++) {
      df_uns size = count / groups + (g < count % groups ? 1 : 0);
      void* branch = df_bt_new_branch(&s);
      for (df_uns j = 0; j < size; j++) {
        df_bt_kids(branch, &s)[j] = level[taken + j];
        df_bt_setsep(branch, (unsigned)j, mins[taken + j], minPtrs[taken + j], &s);
      }
      df_bt_setn(branch, (unsigned)size);
      level[g] = branch;
      mins[g] = df_bt_bk(branch)[0];
      minPtrs[g] = df_bt_bptr(branch, 0, &s);
      taken += size;
    }
    count = groups;
    height++;
  }
  t->root = level[0];
  t->height = height;
  free(level);
  free(mins);
  free(minPtrs);
  return removed;
}

""";
}
