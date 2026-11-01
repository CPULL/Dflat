namespace dfcompiler;

// C runtime of hmap<K, V> (spec 6.8.5, 6.8.5.1): an extendible hash table, added to a program only when it uses an hmap (chunk "hmap", which brings
// "hash"). A directory of 2^d bucket pointers is indexed by the top d bits of the scrambled hash (hash * 0x9E3779B97F4A7C15); a bucket holds 16
// unsorted entries: a 32-byte header (marker, local depth, occupancy mask, next extension), 16 filter bytes (the top 8 bits of hash *
// 0xC2B2AE3D27D4EB4F), then the 16 key slots and the 16 value slots. The table knows a key only as ks bytes with a hash function and an equality
// (NULL: the bytes), and a value as vs bytes, so one runtime serves every hmap; the shape is passed to every call. A full bucket splits when the next
// bit of the scrambled hash separates its entries (the directory doubles first when the bucket is as deep as it), otherwise it takes an extension.
// A removal merges the last two links of a chain (14 entries or fewer), then two siblings (8 or fewer), then shrinks the directory (by at most 2 bits).
// The null key is a normal entry: its key slot is the one the compiler makes for null.
public static class RuntimeHmap {
  public const string Source = """
/* ---- hmap: the extendible hash table of spec 6.8.5.1 ---- */

#define DF_HT_N 16                /* entries of a bucket */
#define DF_HT_EXT_MARK 0x1        /* bit 0 of the marker: an extension */
#define DF_HT_STUCK_MARK 0x2      /* bit 1: a stuck bucket (more than 16 keys with the same hash) */
#define DF_HT_PTR_MARK 0x4        /* bit 2: keys with a pointer */
#define DF_HT_FULL 0xFFFFu
#if DF_UNS_BITS == 32
#define DF_HT_MAX_DEPTH 28
#else
#define DF_HT_MAX_DEPTH 48
#endif

typedef struct {
  size_t ks;                                   /* the bytes of a key slot */
  size_t vs;                                   /* the bytes of a value slot */
  df_uns (*hash)(const void* key);             /* the hash of a key slot (spec 6.2.1) */
  bool (*eq)(const void* a, const void* b);    /* two key slots hold the same key; NULL: the same bytes */
  bool kp;                                     /* the key slots hold a pointer */
} df_ht_shape;

typedef struct df_hb {
  uint8_t mark;                     /* bit 0 extension, bit 1 stuck, bit 2 key with pointer */
  uint8_t depth;                    /* the local depth (an extension copies the one of its bucket) */
  uint16_t used;                    /* the occupancy mask, one bit per slot */
  uint32_t reserved;
  union { struct df_hb* p; uint64_t pad; } next;   /* the next extension: 8 bytes on every target, so the header is 32 bytes */
  uint8_t filter[DF_HT_N];
  /* then DF_HT_N key slots of ks bytes and DF_HT_N value slots of vs bytes */
} df_hb;

typedef struct {
  df_hb** dir;     /* 2^d bucket pointers; NULL before the first add */
  uint8_t d;       /* the global depth */
  df_uns count;    /* the entries */
  uint32_t cd;     /* C(d): the distinct buckets of local depth d */
  uint32_t cd1;    /* C(d-1) */
  df_uns ext;      /* the extensions */
  df_uns marked;   /* entries removed by df_ht_mark, waiting for df_ht_sweep */
} df_ht;

typedef struct {
  size_t di;       /* the index in the directory */
  df_hb* b;        /* the block: the bucket or one of its extensions; NULL at the end */
  unsigned i;      /* the slot */
} df_ht_cur;

/* the null of a nullable key in its key slot: the null constant, which has a bit above the width of every value kept in one word (spec 6.2.1) */
#define DF_HT_NULL_TAG 0x2AA52E3F0BF438BDull

/* a real key by its bits after normalizing: -0.0 is +0.0, every NaN the canonical one (spec 6.2.1) */
static inline uint64_t df_ht_bits_r32(float x) {
  uint32_t bits;
  if (x == 0.0f) {
    x = 0.0f;
  }
  if (x != x) {
    bits = 0x7FC00000u;
  } else {
    memcpy(&bits, &x, sizeof bits);
  }
  return bits;
}

static inline uint64_t df_ht_bits_r64(double x) {
  uint64_t bits;
  if (x == 0.0) {
    x = 0.0;
  }
  if (x != x) {
    bits = 0x7FF8000000000000ull;
  } else {
    memcpy(&bits, &x, sizeof bits);
  }
  return bits;
}

static inline float df_ht_unbits_r32(uint64_t w) {
  uint32_t bits = (uint32_t)w;
  float x;
  memcpy(&x, &bits, sizeof x);
  return x;
}

static inline double df_ht_unbits_r64(uint64_t bits) {
  double x;
  memcpy(&x, &bits, sizeof x);
  return x;
}

/* the key slot of a string key (spec 6.8.5.1): the full hash, then a pointer to the key, the hmap's own copy (16 bytes on every target) */
typedef struct {
  uint64_t h;
  union { df_string s; uint64_t pad; } k;
} df_ht_skey;

static df_uns df_ht_skey_hash(const void* key) {
  return (df_uns)((const df_ht_skey*)key)->h;
}

/* the hashes first, then the text */
static bool df_ht_skey_eq(const void* a, const void* b) {
  const df_ht_skey* x = (const df_ht_skey*)a;
  const df_ht_skey* y = (const df_ht_skey*)b;
  return x->h == y->h && df_str_eq(x->k.s, y->k.s);
}

static inline uint64_t df_ht_scr(df_uns h) {
  return (uint64_t)h * 0x9E3779B97F4A7C15ull;
}

static inline uint8_t df_ht_filt(df_uns h) {
  return (uint8_t)(((uint64_t)h * 0xC2B2AE3D27D4EB4Full) >> 56);
}

static inline size_t df_ht_idx(const df_ht* t, uint64_t sh) {
  return t->d == 0 ? 0 : (size_t)(sh >> (64 - t->d));
}

/* bit number depth of the scrambled hash, from the top */
static inline unsigned df_ht_bit(uint64_t sh, unsigned depth) {
  return (unsigned)((sh >> (63 - depth)) & 1u);
}

static inline unsigned df_ht_pop(uint16_t m) {
  unsigned n = 0;
  while (m) {
    m &= (uint16_t)(m - 1);
    n++;
  }
  return n;
}

static inline uint8_t* df_ht_key(df_hb* b, unsigned i, const df_ht_shape* s) {
  return (uint8_t*)b + sizeof(df_hb) + i * s->ks;
}

static inline uint8_t* df_ht_val(df_hb* b, unsigned i, const df_ht_shape* s) {
  return (uint8_t*)b + sizeof(df_hb) + DF_HT_N * s->ks + i * s->vs;
}

static inline size_t df_ht_bsize(const df_ht_shape* s) {
  return sizeof(df_hb) + DF_HT_N * (s->ks + s->vs);
}

static inline bool df_ht_same(const void* a, const void* b, const df_ht_shape* s) {
  return s->eq ? s->eq(a, b) : memcmp(a, b, s->ks) == 0;
}

static inline uint64_t df_ht_entry_scr(df_hb* b, unsigned i, const df_ht_shape* s) {
  return df_ht_scr(s->hash(df_ht_key(b, i, s)));
}

static df_hb* df_ht_newb(const df_ht_shape* s, uint8_t depth, uint8_t mark) {
  df_hb* b = (df_hb*)df_calloc(1, df_ht_bsize(s));
  b->depth = depth;
  b->mark = (uint8_t)(mark | (s->kp ? DF_HT_PTR_MARK : 0));
  return b;
}

/* the directory entries of a bucket of local depth depth that holds index idx: span entries from start */
static inline size_t df_ht_span(const df_ht* t, unsigned depth) {
  return (size_t)1 << (t->d - depth);
}

/* puts an entry (its key, filter and value bytes) in the first free slot of the chain of b, adding an extension when every block is full */
static void* df_ht_place(df_ht* t, df_hb* b, const void* key, uint8_t f, const void* value, const df_ht_shape* s) {
  for (;;) {
    if (b->used != DF_HT_FULL) {
      unsigned i = 0;
      while ((b->used >> i) & 1u) {
        i++;
      }
      b->used = (uint16_t)(b->used | (1u << i));
      b->filter[i] = f;
      memcpy(df_ht_key(b, i, s), key, s->ks);
      if (value) {
        memcpy(df_ht_val(b, i, s), value, s->vs);
      } else {
        memset(df_ht_val(b, i, s), 0, s->vs);
      }
      return df_ht_val(b, i, s);
    }
    if (b->next.p == NULL) {
      b->next.p = df_ht_newb(s, b->depth, DF_HT_EXT_MARK);
      t->ext++;
    }
    b = b->next.p;
  }
}

/* frees the extensions of b (the chain after it) */
static void df_ht_free_ext(df_ht* t, df_hb* b) {
  df_hb* e = b->next.p;
  b->next.p = NULL;
  while (e) {
    df_hb* after = e->next.p;
    free(e);
    t->ext--;
    e = after;
  }
}

/* does the next bit of the scrambled hash separate the entries of the chain of b (and the new one, sh)? */
static bool df_ht_separates(df_hb* b, uint64_t sh, const df_ht_shape* s) {
  unsigned bit = df_ht_bit(sh, b->depth);
  for (df_hb* x = b; x; x = x->next.p) {
    for (unsigned i = 0; i < DF_HT_N; i++) {
      if (((x->used >> i) & 1u) && df_ht_bit(df_ht_entry_scr(x, i, s), b->depth) != bit) {
        return true;
      }
    }
  }
  return false;
}

/* every entry of the chain of b (and the new one) has the same scrambled hash: the bucket is stuck */
static bool df_ht_all_same(df_hb* b, uint64_t sh, const df_ht_shape* s) {
  for (df_hb* x = b; x; x = x->next.p) {
    for (unsigned i = 0; i < DF_HT_N; i++) {
      if (((x->used >> i) & 1u) && df_ht_entry_scr(x, i, s) != sh) {
        return false;
      }
    }
  }
  return true;
}

#define DF_HT_NONE (-1000)   /* no depth: a bucket that is created or freed */

/* the depth counts after a change of the local depth of one bucket: C(from) loses one, C(to) gains one (spec 6.8.5.1) */
static void df_ht_count_move(df_ht* t, int from, int to) {
  if (from == t->d) {
    t->cd--;
  } else if (from == t->d - 1) {
    t->cd1--;
  }
  if (to == t->d) {
    t->cd++;
  } else if (to == t->d - 1) {
    t->cd1++;
  }
}

/* the bucket b (local depth L, below d) and its chain become two buckets of depth L + 1, by the next bit of the scrambled hash */
static void df_ht_split(df_ht* t, df_hb* b, size_t start, const df_ht_shape* s) {
  unsigned depth = b->depth;
  /* the entries leave the chain, which is emptied, then each goes back to b or to the new sibling */
  size_t n = 0;
  for (df_hb* x = b; x; x = x->next.p) {
    n += df_ht_pop(x->used);
  }
  size_t es = s->ks + s->vs + 1;
  uint8_t* held = (uint8_t*)df_calloc(n ? n : 1, es);
  size_t k = 0;
  for (df_hb* x = b; x; x = x->next.p) {
    for (unsigned i = 0; i < DF_HT_N; i++) {
      if ((x->used >> i) & 1u) {
        uint8_t* e = held + k * es;
        e[0] = x->filter[i];
        memcpy(e + 1, df_ht_key(x, i, s), s->ks);
        memcpy(e + 1 + s->ks, df_ht_val(x, i, s), s->vs);
        k++;
      }
    }
  }
  df_ht_free_ext(t, b);
  b->used = 0;
  b->mark = (uint8_t)(b->mark & ~DF_HT_STUCK_MARK);
  b->depth = (uint8_t)(depth + 1);
  df_hb* sibling = df_ht_newb(s, (uint8_t)(depth + 1), 0);
  for (k = 0; k < n; k++) {
    uint8_t* e = held + k * es;
    df_hb* to = df_ht_bit(df_ht_scr(s->hash(e + 1)), depth) ? sibling : b;
    df_ht_place(t, to, e + 1, e[0], e + 1 + s->ks, s);
  }
  free(held);
  size_t span = df_ht_span(t, depth);
  for (size_t j = start + span / 2; j < start + span; j++) {
    t->dir[j] = sibling;
  }
  df_ht_count_move(t, (int)depth, (int)depth + 1);
  df_ht_count_move(t, DF_HT_NONE, (int)depth + 1);
}

/* the directory doubles: each pointer is copied twice (no rehash: the full hash is in the key slot) */
static void df_ht_double(df_ht* t) {
  size_t n = (size_t)1 << t->d;
  df_hb** dir = (df_hb**)df_calloc(n * 2, sizeof(df_hb*));
  for (size_t i = 0; i < n; i++) {
    dir[2 * i] = t->dir[i];
    dir[2 * i + 1] = t->dir[i];
  }
  free(t->dir);
  t->dir = dir;
  t->d++;
  t->cd1 = t->cd;
  t->cd = 0;
}

/* unsticking: the buckets with an extension split when the next bit separates their entries (tried on a doubling and on a shrink) */
static void df_ht_unstick(df_ht* t, const df_ht_shape* s) {
  if (t->ext == 0) {
    return;
  }
  size_t n = (size_t)1 << t->d;
  for (size_t i = 0; i < n; ) {
    df_hb* b = t->dir[i];
    size_t span = df_ht_span(t, b->depth);
    if (b->next.p != NULL && !(b->mark & DF_HT_STUCK_MARK) && b->depth < t->d && b->used != 0) {
      unsigned first = 0;
      while (!((b->used >> first) & 1u)) {
        first++;
      }
      if (df_ht_separates(b, df_ht_entry_scr(b, first, s), s)) {
        df_ht_split(t, b, i, s);
      }
    }
    i += span;
  }
}

/* the first add: the directory with one bucket */
static void df_ht_start(df_ht* t, const df_ht_shape* s) {
  t->dir = (df_hb**)df_calloc(1, sizeof(df_hb*));
  t->dir[0] = df_ht_newb(s, 0, 0);
  t->d = 0;
  t->cd = 1;
  t->cd1 = 0;
}

/* the slot of key in its chain: the block and the slot, or false */
static bool df_ht_locate(df_ht* t, const void* key, const df_ht_shape* s, df_hb** block, unsigned* slot) {
  if (t->dir == NULL) {
    return false;
  }
  df_uns h = s->hash(key);
  uint8_t f = df_ht_filt(h);
  for (df_hb* b = t->dir[df_ht_idx(t, df_ht_scr(h))]; b; b = b->next.p) {
    for (unsigned i = 0; i < DF_HT_N; i++) {
      if (((b->used >> i) & 1u) && b->filter[i] == f && df_ht_same(df_ht_key(b, i, s), key, s)) {
        *block = b;
        *slot = i;
        return true;
      }
    }
  }
  return false;
}

/* the value slot of key, or NULL */
static void* df_ht_find(df_ht* t, const void* key, const df_ht_shape* s) {
  df_hb* b;
  unsigned i;
  return df_ht_locate(t, key, s, &b, &i) ? df_ht_val(b, i, s) : NULL;
}

/* the value slot of key, added (zeroed) when the key is missing; *added tells which */
static void* df_ht_put(df_ht* t, const void* key, const df_ht_shape* s, bool* added) {
  void* found = df_ht_find(t, key, s);
  if (found) {
    *added = false;
    return found;
  }
  *added = true;
  if (t->dir == NULL) {
    df_ht_start(t, s);
  }
  df_uns h = s->hash(key);
  uint64_t sh = df_ht_scr(h);
  uint8_t f = df_ht_filt(h);
  for (;;) {
    size_t at = df_ht_idx(t, sh);
    df_hb* b = t->dir[at];
    bool room = false;
    for (df_hb* x = b; x; x = x->next.p) {
      room = room || x->used != DF_HT_FULL;
    }
    if (!room && b->next.p == NULL && df_ht_separates(b, sh, s)) {
      if (b->depth < t->d) {
        df_ht_split(t, b, at & ~(df_ht_span(t, b->depth) - 1), s);
        continue;
      }
      if (t->d < DF_HT_MAX_DEPTH) {
        df_ht_double(t);
        df_ht_unstick(t, s);
        continue;
      }
    }
    if (!room && df_ht_all_same(b, sh, s)) {
      b->mark = (uint8_t)(b->mark | DF_HT_STUCK_MARK);
    }
    t->count++;
    return df_ht_place(t, b, key, f, NULL, s);
  }
}

/* the depth counts again, with one pass over the distinct buckets */
static void df_ht_recount(df_ht* t) {
  t->cd = 0;
  t->cd1 = 0;
  size_t n = (size_t)1 << t->d;
  for (size_t i = 0; i < n; ) {
    df_hb* b = t->dir[i];
    if (b->depth == t->d) {
      t->cd++;
    } else if (b->depth + 1 == t->d) {
      t->cd1++;
    }
    i += df_ht_span(t, b->depth);
  }
}

/* the directory loses bits bits (no bucket is deeper than what is left), then the depth counts are made again */
static void df_ht_shrink_by(df_ht* t, unsigned bits) {
  size_t n = (size_t)1 << (t->d - bits);
  df_hb** dir = (df_hb**)df_calloc(n, sizeof(df_hb*));
  for (size_t i = 0; i < n; i++) {
    dir[i] = t->dir[i << bits];
  }
  free(t->dir);
  t->dir = dir;
  t->d = (uint8_t)(t->d - bits);
  df_ht_recount(t);
}

/* the directory shrinks by 1 bit when no bucket is as deep as it, by 2 when none is one bit less deep either (never more in one check) */
static void df_ht_shrink(df_ht* t, const df_ht_shape* s) {
  if (t->d == 0 || t->cd != 0) {
    return;
  }
  df_ht_shrink_by(t, (t->d >= 2 && t->cd1 == 0) ? 2 : 1);
  df_ht_unstick(t, s);
}

/* the bucket b, whose directory entries start at start, takes the entries of its sibling (same depth, no extensions): one bucket, one bit less deep */
static void df_ht_merge_sibling(df_ht* t, df_hb* b, size_t start, const df_ht_shape* s) {
  size_t span = df_ht_span(t, b->depth);
  size_t other = start ^ span;
  df_hb* sibling = t->dir[other];
  for (unsigned i = 0; i < DF_HT_N; i++) {
    if ((sibling->used >> i) & 1u) {
      df_ht_place(t, b, df_ht_key(sibling, i, s), sibling->filter[i], df_ht_val(sibling, i, s), s);
    }
  }
  size_t first = start < other ? start : other;
  for (size_t j = first; j < first + 2 * span; j++) {
    t->dir[j] = b;
  }
  unsigned depth = b->depth;
  free(sibling);
  b->depth = (uint8_t)(depth - 1);
  df_ht_count_move(t, (int)depth, (int)depth - 1);
  df_ht_count_move(t, (int)depth, DF_HT_NONE);
}

/* the sibling of the bucket b (whose directory entries start at start) when the two can merge with at most limit entries, or NULL */
static df_hb* df_ht_mergeable(df_ht* t, df_hb* b, size_t start, unsigned limit) {
  if (b->depth == 0 || b->next.p != NULL) {
    return NULL;
  }
  df_hb* sibling = t->dir[start ^ df_ht_span(t, b->depth)];
  bool fits = sibling != b && sibling->depth == b->depth && sibling->next.p == NULL && df_ht_pop(b->used) + df_ht_pop(sibling->used) <= limit;
  return fits ? sibling : NULL;
}

/* after a removal from the chain of the bucket at index at: the merges, then the shrink (spec 6.8.5.1) */
static void df_ht_tidy(df_ht* t, size_t at, const df_ht_shape* s) {
  df_hb* b = t->dir[at];
  /* an empty extension leaves its chain */
  for (df_hb* x = b; x->next.p; ) {
    df_hb* e = x->next.p;
    if (e->used == 0) {
      x->next.p = e->next.p;
      free(e);
      t->ext--;
    } else {
      x = e;
    }
  }
  /* the last two links of the chain merge when 14 entries or fewer are left in them */
  if (b->next.p) {
    df_hb* prev = b;
    while (prev->next.p->next.p) {
      prev = prev->next.p;
    }
    df_hb* last = prev->next.p;
    if (df_ht_pop(prev->used) + df_ht_pop(last->used) <= DF_HT_N - 2) {
      for (unsigned i = 0; i < DF_HT_N; i++) {
        if ((last->used >> i) & 1u) {
          df_ht_place(t, prev, df_ht_key(last, i, s), last->filter[i], df_ht_val(last, i, s), s);
        }
      }
      prev->next.p = NULL;
      free(last);
      t->ext--;
    }
  }
  if (b->next.p == NULL) {
    b->mark = (uint8_t)(b->mark & ~DF_HT_STUCK_MARK);
  }
  /* two siblings of the same depth, without extensions, merge when 8 entries or fewer are left in them */
  size_t start = at & ~(df_ht_span(t, b->depth) - 1);
  if (df_ht_mergeable(t, b, start, DF_HT_N / 2)) {
    df_ht_merge_sibling(t, b, start, s);
    df_ht_shrink(t, s);
  }
}

/* removes key: its value goes to value_out and its key slot to key_out (when they are not NULL); false when the key is missing */
static bool df_ht_remove(df_ht* t, const void* key, const df_ht_shape* s, void* value_out, void* key_out) {
  df_hb* b;
  unsigned i;
  if (!df_ht_locate(t, key, s, &b, &i)) {
    return false;
  }
  if (value_out) {
    memcpy(value_out, df_ht_val(b, i, s), s->vs);
  }
  if (key_out) {
    memcpy(key_out, df_ht_key(b, i, s), s->ks);
  }
  b->used = (uint16_t)(b->used & ~(1u << i));
  t->count--;
  df_ht_tidy(t, df_ht_idx(t, df_ht_scr(s->hash(key))), s);
  return true;
}

/* ---- the walk: the directory in order, each bucket and then its extensions (spec 6.8.5.1) ---- */

/* from the cursor, the next used slot (the cursor itself when it is used) */
static void df_ht_settle(const df_ht* t, df_ht_cur* c) {
  while (c->b) {
    while (c->i < DF_HT_N) {
      if ((c->b->used >> c->i) & 1u) {
        return;
      }
      c->i++;
    }
    if (c->b->next.p) {
      c->b = c->b->next.p;
      c->i = 0;
      continue;
    }
    c->di += df_ht_span(t, c->b->depth);
    c->b = c->di < ((size_t)1 << t->d) ? t->dir[c->di] : NULL;
    c->i = 0;
  }
}

static df_ht_cur df_ht_begin(const df_ht* t) {
  df_ht_cur c = { 0, t->dir ? t->dir[0] : NULL, 0 };
  df_ht_settle(t, &c);
  return c;
}

static inline bool df_ht_valid(const df_ht_cur* c) {
  return c->b != NULL;
}

/* the next entry; the walk goes on from the slot after (a slot freed by df_ht_mark is fine) */
static void df_ht_step(const df_ht* t, df_ht_cur* c) {
  c->i++;
  df_ht_settle(t, c);
}

static inline void* df_ht_cur_key(const df_ht_cur* c, const df_ht_shape* s) {
  return df_ht_key(c->b, c->i, s);
}

static inline void* df_ht_cur_value(const df_ht_cur* c, const df_ht_shape* s) {
  return df_ht_val(c->b, c->i, s);
}

/* a bulk removal: the entry under the cursor is freed now, the table is tidied once by df_ht_sweep */
static void df_ht_mark(df_ht* t, df_ht_cur* c) {
  c->b->used = (uint16_t)(c->b->used & ~(1u << c->i));
  t->count--;
  t->marked++;
}

/* every distinct bucket and its extensions are freed, then the directory */
static void df_ht_free_all(df_ht* t) {
  if (t->dir == NULL) {
    return;
  }
  size_t n = (size_t)1 << t->d;
  for (size_t i = 0; i < n; ) {
    df_hb* b = t->dir[i];
    size_t span = df_ht_span(t, b->depth);
    df_ht_free_ext(t, b);
    free(b);
    i += span;
  }
  free(t->dir);
  t->dir = NULL;
  t->d = 0;
  t->cd = 0;
  t->cd1 = 0;
  t->ext = 0;
}

/* after df_ht_mark: the table is built again from the entries left, once; the number of entries removed */
static df_uns df_ht_sweep(df_ht* t, const df_ht_shape* s) {
  df_uns removed = t->marked;
  t->marked = 0;
  if (removed == 0 || t->dir == NULL) {
    return removed;
  }
  df_ht old = *t;
  t->dir = NULL;
  t->count = 0;
  t->ext = 0;
  for (df_ht_cur c = df_ht_begin(&old); df_ht_valid(&c); df_ht_step(&old, &c)) {
    bool added;
    void* slot = df_ht_put(t, df_ht_cur_key(&c, s), s, &added);
    memcpy(slot, df_ht_cur_value(&c, s), s->vs);
  }
  df_ht_free_all(&old);
  return removed;
}

/* all the entries go (what they own is freed by the caller first) */
static void df_ht_clear(df_ht* t) {
  df_ht_free_all(t);
  t->count = 0;
  t->marked = 0;
}

/* the bytes of the directory and of every bucket and extension (.sizeof, spec 6.6) */
static uint64_t df_ht_bytes(const df_ht* t, const df_ht_shape* s) {
  if (t->dir == NULL) {
    return 0;
  }
  uint64_t bytes = ((uint64_t)1 << t->d) * sizeof(df_hb*);
  size_t n = (size_t)1 << t->d;
  for (size_t i = 0; i < n; ) {
    df_hb* b = t->dir[i];
    for (df_hb* x = b; x; x = x->next.p) {
      bytes += df_ht_bsize(s);
    }
    i += df_ht_span(t, b->depth);
  }
  return bytes;
}

/* ---- capacity (spec 6.8.5.1) ---- */

#define DF_HT_FILL 11   /* the entries a bucket holds on average when the hash is well spread (a split bucket is about 69% full) */

/* .capacity: the entries held without growing the directory */
static df_uns df_ht_capacity(const df_ht* t) {
  return t->dir == NULL ? 0 : (df_uns)DF_HT_FILL << t->d;
}

/* reserve(wanted) and hmap<K, V>[n]: the directory (and its buckets) for wanted entries in all, so that they come with no doubling; nothing
   changes when every bucket is already as deep as that */
static void df_ht_reserve(df_ht* t, df_uns wanted, const df_ht_shape* s) {
  uint64_t want = wanted;
  if (want == 0) {
    return;
  }
  uint64_t buckets = (want + DF_HT_FILL - 1) / DF_HT_FILL;
  unsigned depth = 0;
  while (((uint64_t)1 << depth) < buckets && depth < DF_HT_MAX_DEPTH) {
    depth++;
  }
  if (t->dir == NULL) {
    df_ht_start(t, s);
  }
  while (t->d < depth) {
    df_ht_double(t);
  }
  /* every bucket is split down to the depth: the room is allocated now, and no removal shrinks the directory below it at once */
  size_t n = (size_t)1 << t->d;
  for (size_t i = 0; i < n; ) {
    df_hb* b = t->dir[i];
    while (b->depth < depth) {
      df_ht_split(t, b, i, s);
    }
    i += df_ht_span(t, b->depth);
  }
}

/* the entries of the chain of b go back into as few blocks as they need; true when blocks were freed */
static bool df_ht_compact(df_ht* t, df_hb* b, const df_ht_shape* s) {
  size_t blocks = 0;
  size_t n = 0;
  for (df_hb* x = b; x; x = x->next.p) {
    blocks++;
    n += df_ht_pop(x->used);
  }
  if (blocks <= (n + DF_HT_N - 1) / DF_HT_N || blocks == 1) {
    return false;
  }
  size_t es = s->ks + s->vs + 1;
  uint8_t* held = (uint8_t*)df_calloc(n, es);
  size_t k = 0;
  for (df_hb* x = b; x; x = x->next.p) {
    for (unsigned i = 0; i < DF_HT_N; i++) {
      if ((x->used >> i) & 1u) {
        uint8_t* e = held + k * es;
        e[0] = x->filter[i];
        memcpy(e + 1, df_ht_key(x, i, s), s->ks);
        memcpy(e + 1 + s->ks, df_ht_val(x, i, s), s->vs);
        k++;
      }
    }
  }
  df_ht_free_ext(t, b);
  b->used = 0;
  for (k = 0; k < n; k++) {
    uint8_t* e = held + k * es;
    df_ht_place(t, b, e + 1, e[0], e + 1 + s->ks, s);
  }
  free(held);
  if (b->next.p == NULL) {
    b->mark = (uint8_t)(b->mark & ~DF_HT_STUCK_MARK);
  }
  return true;
}

/* shrinkToFit(): every merge that fits (the chains compacted, two siblings that fit in one bucket), then the directory as small as the depths allow,
   with no limit of bits; an empty hmap frees everything. False when nothing changed */
static bool df_ht_fit(df_ht* t, const df_ht_shape* s) {
  if (t->dir == NULL) {
    return false;
  }
  if (t->count == 0) {
    df_ht_free_all(t);
    return true;
  }
  bool changed = false;
  bool again = true;
  while (again) {
    again = false;
    size_t n = (size_t)1 << t->d;
    for (size_t i = 0; i < n && !again; ) {
      df_hb* b = t->dir[i];
      if (b->next.p && df_ht_compact(t, b, s)) {
        changed = true;
      }
      if (df_ht_mergeable(t, b, i, DF_HT_N)) {
        df_ht_merge_sibling(t, b, i, s);
        changed = true;
        again = true;   /* the directory changed under the walk: start again */
        break;
      }
      i += df_ht_span(t, b->depth);
    }
    while (t->d > 0 && t->cd == 0) {
      df_ht_shrink_by(t, 1);
      changed = true;
      again = true;
    }
  }
  return changed;
}

""";
}
