namespace dfcompiler;

// C runtime for the counter of open iterators and views of arr (spec 6.4): added to a program only when it uses one.
// An arr has no header ({p, len} only), so the counters live in a side table keyed by the address of the first item (p):
// a loop, a view or an iterator opens its array once (not once per item) and closes it when it ends; a changing operation asks
// whether its array is open. While nothing is open the table is empty and the question costs one comparison.
// Open addressing with linear probing; a removed key leaves a tombstone. No threads yet: one table for the program
// (with tasks it needs a lock or one table per task).
public static class RuntimeArrIter {
  public const string Source = """
/* ---- open iterators and views of arr (spec 6.4) ---- */

/* dead: the array ended while it was still open (a view or an iterator outlived it): its items ended, its block waits for the last close,
   so the address is not reused while a view still holds it; the view raises UsageOfDestroyedObjectException (spec 12.5) */
typedef struct { const void* key; df_uns count; bool dead; } df_ait_slot;

#define DF_AIT_TOMB ((const void*)1)

static df_ait_slot* df_ait_slots = NULL;
static df_uns df_ait_cap = 0;    /* a power of 2, or 0 */
static df_uns df_ait_live = 0;   /* keys with a count above 0 */
static df_uns df_ait_used = 0;   /* live keys and tombstones */

static df_uns df_ait_hash(const void* key) {
  uint64_t x = (uint64_t)(uintptr_t)key;
  x ^= x >> 33; x *= 0xff51afd7ed558ccdULL; x ^= x >> 33;
  return (df_uns)x;
}

/* the slot of the key, or NULL */
static df_ait_slot* df_ait_find(const void* key) {
  if (df_ait_live == 0 || key == NULL) {
    return NULL;
  }
  df_uns mask = df_ait_cap - 1;
  for (df_uns i = df_ait_hash(key) & mask;; i = (i + 1) & mask) {
    df_ait_slot* s = &df_ait_slots[i];
    if (s->key == key) {
      return s;
    }
    if (s->key == NULL) {
      return NULL;
    }
  }
}

static void df_ait_put(df_ait_slot* slots, df_uns cap, const void* key, df_uns count, bool dead) {
  df_uns mask = cap - 1;
  df_uns i = df_ait_hash(key) & mask;
  while (slots[i].key != NULL) {
    i = (i + 1) & mask;
  }
  slots[i].key = key;
  slots[i].count = count;
  slots[i].dead = dead;
}

/* the table holds at most half live keys and tombstones; it grows (or is rebuilt without tombstones) before that */
static void df_ait_room(void) {
  if ((df_ait_used + 1) * 2 <= df_ait_cap) {
    return;
  }
  df_uns cap = df_ait_cap == 0 ? 16 : ((df_ait_live + 1) * 4 > df_ait_cap ? df_ait_cap * 2 : df_ait_cap);
  df_ait_slot* slots = (df_ait_slot*)df_calloc(cap, sizeof(df_ait_slot));
  for (df_uns i = 0; i < df_ait_cap; i++) {
    if (df_ait_slots[i].key != NULL && df_ait_slots[i].key != DF_AIT_TOMB) {
      df_ait_put(slots, cap, df_ait_slots[i].key, df_ait_slots[i].count, df_ait_slots[i].dead);
    }
  }
  free(df_ait_slots);
  df_ait_slots = slots;
  df_ait_cap = cap;
  df_ait_used = df_ait_live;
}

/* the arrays still open at exit: printed when the environment variable DF_AIT_COUNT is set (to look for an open without its close) */
static void df_ait_report(void) {
  if (getenv("DF_AIT_COUNT")) {
    printf("arrays open at exit: %llu\n", (unsigned long long)df_ait_live);
  }
}

/* a loop, a view or an iterator starts using the array whose first item is at p */
static void df_ait_open(const void* p) {
  static bool reported = false;
  if (!reported) {
    reported = true;
    atexit(df_ait_report);
  }
  if (p == NULL) {
    return;   /* an empty array has nothing to change */
  }
  df_ait_slot* s = df_ait_find(p);
  if (s != NULL) {
    s->count++;
    return;
  }
  df_ait_room();
  df_ait_put(df_ait_slots, df_ait_cap, p, 1, false);
  df_ait_live++;
  df_ait_used++;
}

/* ... and stops using it; the last close of a dead array frees its block */
static void df_ait_close(const void* p) {
  df_ait_slot* s = df_ait_find(p);
  if (s == NULL) {
    return;
  }
  if (--s->count == 0) {
    if (s->dead) {
      free((void*)s->key);
    }
    s->key = DF_AIT_TOMB;
    s->dead = false;
    df_ait_live--;
  }
}

/* the open iterators and views of the array (0: it can change) */
static df_uns df_ait_count(const void* p) {
  df_ait_slot* s = df_ait_find(p);
  return s == NULL ? 0 : s->count;
}

/* the array ended while a view or an iterator still holds it */
static bool df_ait_dead(const void* p) {
  df_ait_slot* s = df_ait_find(p);
  return s != NULL && s->dead;
}

/* the block of an array that ends (its items already ended): freed now, or by the last close when it is still open */
static void df_ait_free(void* p) {
  df_ait_slot* s = df_ait_find(p);
  if (s != NULL) {
    s->dead = true;
    return;
  }
  free(p);
}
""";
}
