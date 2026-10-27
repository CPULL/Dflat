namespace dfcompiler;

// C runtime embedded in every generated program (standard C library version).
public static class Runtime {
  public const string Source = """
/* ---- Dflat runtime (libc version) ---- */
#ifndef _WIN32
#define _POSIX_C_SOURCE 200809L
#endif
#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <time.h>
#include <errno.h>
#ifdef _WIN32
#include <windows.h>
#endif

/* uns: the native unsigned size of the target (spec 3.1, 6.1); lengths and indexes of the collections. DF_UNS_BITS is defined by the compiler */
#if DF_UNS_BITS == 32
typedef uint32_t df_uns;
#define DF_UNS_MAX UINT32_MAX
#else
typedef uint64_t df_uns;
#define DF_UNS_MAX UINT64_MAX
#endif

/* a string is a pointer to its first allocation (spec 4.4); NULL is the empty string */
typedef struct df_strh df_strh;
typedef df_strh* df_string;

/* the text of a string, for the code that reads it */
typedef struct {
  const char* p;   /* UTF-8 bytes, followed by a 0 */
  uint64_t len;    /* byte length */
} df_sv;

/* ---- panic ---- */

/* set by the console when it changes the terminal mode (Console.read): gives the terminal back before a panic */
static void (*df_panic_hook)(void) = NULL;

/* critical exit: prints "Panic <code>" on stdout, no cleanup */
static void df_panic(int64_t code) {
  if (df_panic_hook) {
    df_panic_hook();
  }
  fflush(stdout);
  printf("Panic %lld\n", (long long)code);
  fflush(stdout);
  _Exit((int)code);
}

#define DF_PANIC_UNHANDLED (-1000)

/* out of memory is not catchable yet: message and panic */
static void df_out_of_memory(void) {
  fflush(stdout);
  printf("Out Of Memory Exception\n");
  df_panic(DF_PANIC_UNHANDLED);
}

static void* df_alloc(uint64_t n) {
  void* p = malloc(n ? n : 1);
  if (!p) {
    df_out_of_memory();
  }
  return p;
}

/* ---- exceptions (error register, spec 10.7) ---- */

typedef struct df_descriptor {
  uint64_t id;
  const char* name;
  const struct df_descriptor* parent;
  bool silent;
} df_descriptor;

struct df_class;

typedef struct {
  const struct df_class* cls;   /* NULL for the exceptions raised by the runtime; the class of an exception object */
  uint64_t id;
  const df_descriptor* descriptor;
  char* message;     /* C-style, max 2 KB */
  char* function;    /* C-style, max 1 KB: minified header of the failing function */
  uint64_t line;     /* 0 in release */
} df_exception;

static const df_descriptor df_desc_Exception = { 10, "Exception", NULL, false };
static const df_descriptor df_desc_NullPointerException = { 11, "NullPointerException", &df_desc_Exception, false };
static const df_descriptor df_desc_OverflowException = { 12, "OverflowException", &df_desc_Exception, true };
static const df_descriptor df_desc_InvalidCastException = { 13, "InvalidCastException", &df_desc_Exception, false };
static const df_descriptor df_desc_DivisionByZeroException = { 14, "DivisionByZeroException", &df_desc_Exception, false };
static const df_descriptor df_desc_OutOfMemoryException = { 15, "OutOfMemoryException", &df_desc_Exception, false };
static const df_descriptor df_desc_ChangingRefCountTypeException = { 16, "ChangingRefCountTypeException", &df_desc_Exception, false };
static const df_descriptor df_desc_OutOfRangeCastException = { 17, "OutOfRangeCastException", &df_desc_Exception, true };
static const df_descriptor df_desc_InvalidCharacterCodePointException = { 18, "InvalidCharacterCodePointException", &df_desc_Exception, true };
static const df_descriptor df_desc_IndexOutOfRangeException = { 19, "IndexOutOfRangeException", &df_desc_Exception, false };
static const df_descriptor df_desc_ModifyingProtectedSourceException = { 20, "ModifyingProtectedSourceException", &df_desc_Exception, false };
static const df_descriptor df_desc_InvalidDateTimeFormatException = { 24, "InvalidDateTimeFormatException", &df_desc_Exception, true };
static const df_descriptor df_desc_FileNotFoundException = { 25, "FileNotFoundException", &df_desc_Exception, false };
static const df_descriptor df_desc_InvalidFileAccessException = { 26, "InvalidFileAccessException", &df_desc_Exception, false };
static const df_descriptor df_desc_FileAccessException = { 27, "FileAccessException", &df_desc_Exception, true };
static const df_descriptor df_desc_DirectoryNotEmptyException = { 28, "DirectoryNotEmptyException", &df_desc_Exception, true };
static const df_descriptor df_desc_FileNameAlreadyExistsException = { 29, "FileNameAlreadyExistsException", &df_desc_Exception, true };
static const df_descriptor df_desc_InvalidPathException = { 30, "InvalidPathException", &df_desc_Exception, false };
static const df_descriptor df_desc_FullPathNotFoundException = { 31, "FullPathNotFoundException", &df_desc_Exception, false };
static const df_descriptor df_desc_SeekBeforeBeginException = { 32, "SeekBeforeBeginException", &df_desc_Exception, true };
static const df_descriptor df_desc_FileAlreadyExistsException = { 33, "FileAlreadyExistsException", &df_desc_Exception, false };
static const df_descriptor df_desc_FileAccessToADirectoryException = { 34, "FileAccessToADirectoryException", &df_desc_Exception, false };
static const df_descriptor df_desc_ReadOnlyItemCannotBeWrittenException = { 35, "ReadOnlyItemCannotBeWrittenException", &df_desc_Exception, false };
static const df_descriptor df_desc_ConflictingSharingException = { 36, "ConflictingSharingException", &df_desc_Exception, false };
static const df_descriptor df_desc_InvalidStreamModesException = { 37, "InvalidStreamModesException", &df_desc_Exception, false };
static const df_descriptor df_desc_NoMoreResourcesException = { 38, "NoMoreResourcesException", &df_desc_Exception, false };
static const df_descriptor df_desc_AccessToAClosedStreamException = { 39, "AccessToAClosedStreamException", &df_desc_Exception, false };
static const df_descriptor df_desc_StreamModeNotAllowedException = { 40, "StreamModeNotAllowedException", &df_desc_Exception, false };
static const df_descriptor df_desc_SeekNotSupportedByStreamException = { 41, "SeekNotSupportedByStreamException", &df_desc_Exception, false };
static const df_descriptor df_desc_InvalidBufferRangeException = { 42, "InvalidBufferRangeException", &df_desc_Exception, false };
static const df_descriptor df_desc_GenericIOErrorException = { 43, "GenericIOErrorException", &df_desc_Exception, false };
static const df_descriptor df_desc_FileRemovedWhileOpenException = { 44, "FileRemovedWhileOpenException", &df_desc_Exception, false };
static const df_descriptor df_desc_StreamCloseFailureException = { 45, "StreamCloseFailureException", &df_desc_Exception, false };
static const df_descriptor df_desc_ModifyingIteratedCollectionException = { 46, "ModifyingIteratedCollectionException", &df_desc_Exception, false };
static const df_descriptor df_desc_InvalidIteratorReleaseException = { 47, "InvalidIteratorReleaseException", &df_desc_Exception, false };
static const df_descriptor df_desc_UsageOfDestroyedObjectException = { 48, "UsageOfDestroyedObjectException", &df_desc_Exception, false };

/* The exception of a function is a local of that function (df_err in the generated code) and travels back to the caller as the second
   value it returns. The runtime functions written in C cannot return it that way: they leave it here, and the code generated around
   the call moves it into the local df_err at once. This slot is empty between statements. */
static df_exception* df_pending = NULL;

/* copies at most maxBytes - 1 bytes plus the terminator, never cutting a UTF-8 character */
static char* df_cstr_truncated(const char* text, uint64_t length, uint64_t maxBytes) {
  uint64_t keep = length;
  if (keep > maxBytes - 1) {
    keep = maxBytes - 1;
    while (keep > 0 && ((unsigned char)text[keep] & 0xC0) == 0x80) {
      keep--;
    }
  }
  char* copy = (char*)df_alloc(keep + 1);
  memcpy(copy, text, keep);
  copy[keep] = 0;
  return copy;
}

/* the exception records alive: the count is printed at exit when the environment variable DF_EXC_COUNT is set (to look for leaks) */
static int64_t df_exc_live = 0;

static void df_exc_report(void) {
  if (getenv("DF_EXC_COUNT")) {
    printf("exceptions alive at exit: %lld\n", (long long)df_exc_live);
  }
}

static df_exception* df_new_exception(const df_descriptor* descriptor, const char* message, uint64_t messageLength, const char* function) {
  df_exception* exception = (df_exception*)df_alloc(sizeof(df_exception));
  exception->cls = NULL;
  exception->id = descriptor->id;
  exception->descriptor = descriptor;
  exception->message = df_cstr_truncated(message, messageLength, 2048);
  exception->function = df_cstr_truncated(function, strlen(function), 1024);
  exception->line = 0;
  df_exc_live++;
  return exception;
}

/* runtime error: the message is the exception name written with spaces */
static void df_raise(const df_descriptor* descriptor, const char* function) {
  char spaced[256];
  uint64_t length = 0;
  for (const char* letter = descriptor->name; *letter && length < sizeof spaced - 2; letter++) {
    if (letter != descriptor->name && *letter >= 'A' && *letter <= 'Z') {
      spaced[length++] = ' ';
    }
    spaced[length++] = *letter;
  }
  spaced[length] = 0;
#ifdef DF_RELEASE
  /* in a release build an index outside a collection, or a collection changed while it is iterated, is a panic, not an exception (spec 6.1, 6.4) */
  if (descriptor->id == 19 || descriptor->id == 46) {
    fflush(stdout);
    printf("%s\n", spaced);
    df_panic(DF_PANIC_UNHANDLED);
  }
#endif
  df_pending = df_new_exception(descriptor, spaced, length, function);
}

/* true when the exception is of that type or a subtype; a silent exception is not an 'Exception' catch (spec 15.1, 15.4) */
static bool df_exc_is(const df_exception* exception, uint64_t id) {
  for (const df_descriptor* descriptor = exception->descriptor; descriptor; descriptor = descriptor->parent) {
    if (descriptor->id == id) {
      return !(id == 10 && exception->descriptor->silent);
    }
  }
  return false;
}

/* a library function that would raise a silent exception leaves a note; the caller raises it only when a catch exists */
static const df_descriptor* df_silent_desc = NULL;

static void df_silent_note(const df_descriptor* descriptor) {
  df_silent_desc = descriptor;
}

/* unhandled exception: its message, then Panic -1000 */
static void df_panic_unhandled(const df_exception* error) {
  fflush(stdout);
  printf("%s\n", error ? error->message : "");
  df_panic(DF_PANIC_UNHANDLED);
}

/* ---- strings (spec 4.4) ----
   The class is in the low 4 bits of the first byte.
   0-7  single block of 32 << class bytes: byte length in the high 4 bits of byte 0 and in byte 1, char length in bytes 2-3, text at byte 4
   11   literal: char length and byte length, each as two 32-bit words (low first), at bytes 4 and 12; text at byte 20
   13   moved: pointer to the new location at byte 4
   15   block string: a root (df_sroot) and a chain of 4 KB blocks (df_sblock), for the strings above 4091 bytes
   The text of classes 0-11 ends with a 0. A class 11 with the high bit of its first byte (DF_STR_HEAP) is on the heap; a literal of the
   program has it clear and is never freed. */

#define DF_STR_LITERAL 11
#define DF_STR_MOVED 13
#define DF_STR_BLOCK 15
#define DF_STR_SINGLE_MAX 4091
#define DF_STR_HEAP 0x80
#define DF_BLOCK_SIZE 4096

/* the layout of spec 4.4.4: every field aligned to 32 bits (60 / 24 bytes on 64-bit targets, 48 / 12 on 32-bit targets) */
#pragma pack(push, 4)
typedef struct df_sblock {
  struct df_sblock* next;
  struct df_sblock* prev;
  uint16_t used;       /* bytes of text in this block */
  uint16_t chars;      /* chars in this block */
#if UINTPTR_MAX > 0xFFFFFFFFu
  uint32_t spare;
#endif
} df_sblock;

typedef struct {
  uint8_t cls;         /* 15 */
  uint8_t flags;
  uint16_t iters;      /* open iterators (not used yet) */
  df_sblock* first;
  df_sblock* last;
  uint64_t chars;
  uint64_t bytes;
  df_sblock* cur;      /* cursor: the block of the last change, and where it starts */
  uint64_t curChar;
  uint64_t curByte;
} df_sroot;
#pragma pack(pop)

_Static_assert(sizeof(df_sroot) == (UINTPTR_MAX > 0xFFFFFFFFu ? 60 : 48), "block string root layout (spec 4.4.4)");
_Static_assert(sizeof(df_sblock) == (UINTPTR_MAX > 0xFFFFFFFFu ? 24 : 12), "block header layout (spec 4.4.4)");

#define DF_BLOCK_PAYLOAD ((uint64_t)(DF_BLOCK_SIZE - sizeof(df_sblock)))
#define DF_BLOCK_TEXT(b) ((char*)(b) + sizeof(df_sblock))

static bool df_is_block(const uint8_t* t) {
  return t && (t[0] & 0x0F) == DF_STR_BLOCK;
}

static void df_bs_copy_to(const df_sroot* r, char* out) {
  for (const df_sblock* b = r->first; b; b = b->next) {
    memcpy(out, DF_BLOCK_TEXT(b), b->used);
    out += b->used;
  }
}

/* the text of a block string as one piece, for the code that needs it so: a copy that lives until 16 more are made */
#define DF_FLAT_RING 16
static char* df_flat_ring[DF_FLAT_RING];
static unsigned df_flat_next = 0;

static const char* df_bs_flat(const df_sroot* r) {
  char* text = (char*)malloc(r->bytes + 1);
  if (!text) {
    df_out_of_memory();
  }
  df_bs_copy_to(r, text);
  text[r->bytes] = 0;
  free(df_flat_ring[df_flat_next]);
  df_flat_ring[df_flat_next] = text;
  df_flat_next = (df_flat_next + 1) % DF_FLAT_RING;
  return text;
}

/* the allocation that holds the text (follows a moved string); NULL for the empty string */
static const uint8_t* df_str_res(df_string s) {
  const uint8_t* h = (const uint8_t*)s;
  if (h && (h[0] & 0x0F) == DF_STR_MOVED) {
    memcpy(&h, h + 4, sizeof h);
  }
  return h;
}

/* a 64-bit length stored as two 32-bit words, low first */
static uint64_t df_str_w64(const uint8_t* at) {
  uint32_t w[2];
  memcpy(w, at, sizeof w);
  return (uint64_t)w[0] | ((uint64_t)w[1] << 32);
}

static df_sv df_view(df_string s) {
  df_sv v = { "", 0 };
  const uint8_t* h = df_str_res(s);
  if (!h) {
    return v;
  }
  if ((h[0] & 0x0F) == DF_STR_LITERAL) {
    v.p = (const char*)h + 20;
    v.len = df_str_w64(h + 12);
  } else if (df_is_block(h)) {
    v.len = ((const df_sroot*)h)->bytes;
    v.p = v.len ? df_bs_flat((const df_sroot*)h) : "";
  } else {
    v.p = (const char*)h + 4;
    v.len = (uint64_t)(h[0] >> 4) | ((uint64_t)h[1] << 4);
  }
  return v;
}

/* a view the caller keeps as long as it likes (a for loop over a string): *keep is freed by the caller when it is not NULL */
static df_sv df_view_keep(df_string s, char** keep) {
  const uint8_t* h = df_str_res(s);
  *keep = NULL;
  if (!df_is_block(h)) {
    return df_view(s);
  }
  const df_sroot* r = (const df_sroot*)h;
  *keep = (char*)malloc(r->bytes + 1);
  if (!*keep) {
    df_out_of_memory();
  }
  df_bs_copy_to(r, *keep);
  (*keep)[r->bytes] = 0;
  df_sv v = { *keep, r->bytes };
  return v;
}

static uint64_t df_str_len(df_string s) {
  const uint8_t* h = df_str_res(s);
  if (df_is_block(h)) {
    return ((const df_sroot*)h)->bytes;
  }
  return df_view(s).len;
}

/* character count (UTF-8 code points), kept in the header */
static uint64_t df_str_chars(df_string s) {
  const uint8_t* h = df_str_res(s);
  if (!h) {
    return 0;
  }
  if ((h[0] & 0x0F) == DF_STR_LITERAL) {
    return df_str_w64(h + 4);
  }
  if (df_is_block(h)) {
    return ((const df_sroot*)h)->chars;
  }
  uint16_t chars;
  memcpy(&chars, h + 2, sizeof chars);
  return chars;
}

static uint64_t df_utf8_count(const char* p, uint64_t len) {
  uint64_t n = 0;
  for (uint64_t i = 0; i < len; i++) {
    if (((unsigned char)p[i] & 0xC0) != 0x80) {
      n++;
    }
  }
  return n;
}

/* a new string of `bytes` bytes and `chars` chars, in the smallest class that holds it; the caller writes the text into *text
   (the final 0 is already there). The empty string is NULL */
static char df_str_none[1];

/* DF_STR_TRACE (a define given to the C compiler by hand): the strings still alive are printed when the program ends */
#ifdef DF_STR_TRACE
static int64_t df_str_live = 0;
static void df_str_trace_report(void) {
  fprintf(stderr, "strings alive at exit: %lld\n", (long long)df_str_live);
}
static bool df_str_hooked = false;
#define DF_STR_TRACE_ALLOC() do { if (!df_str_hooked) { df_str_hooked = true; atexit(df_str_trace_report); } df_str_live++; } while (0)
#define DF_STR_TRACE_FREE() (df_str_live--)
#else
#define DF_STR_TRACE_ALLOC() ((void)0)
#define DF_STR_TRACE_FREE() ((void)0)
#endif

static df_string df_str_alloc(uint64_t bytes, uint64_t chars, char** text) {
  if (bytes == 0) {
    *text = df_str_none;
    return NULL;
  }
  DF_STR_TRACE_ALLOC();
  uint8_t* h;
  if (bytes <= DF_STR_SINGLE_MAX) {
    unsigned cls = 0;
    while (((uint64_t)32 << cls) - 5 < bytes) {
      cls++;
    }
    h = (uint8_t*)df_alloc((uint64_t)32 << cls);
    h[0] = (uint8_t)(cls | ((bytes & 0x0F) << 4));
    h[1] = (uint8_t)(bytes >> 4);
    uint16_t c = (uint16_t)chars;
    memcpy(h + 2, &c, sizeof c);
    *text = (char*)h + 4;
  } else {
    h = (uint8_t*)df_alloc(20 + bytes + 1);
    uint32_t w[4] = { (uint32_t)chars, (uint32_t)(chars >> 32), (uint32_t)bytes, (uint32_t)(bytes >> 32) };
    h[0] = DF_STR_LITERAL | DF_STR_HEAP;
    h[1] = h[2] = h[3] = 0;
    memcpy(h + 4, w, sizeof w);
    *text = (char*)h + 20;
  }
  (*text)[bytes] = 0;
  return (df_string)h;
}

/* ---- block strings (spec 4.4.4): the counts are per block, a code point never straddles two blocks ---- */

static df_sroot* df_bs_new(void) {
  df_sroot* r = (df_sroot*)df_alloc(64);   /* 64 bytes: a small text can take its place as a class 1 (shrink) */
  memset(r, 0, 64);
  r->cls = DF_STR_BLOCK;
  DF_STR_TRACE_ALLOC();
  return r;
}

/* a new block after 'after' (NULL: at the start) */
static df_sblock* df_block_add(df_sroot* r, df_sblock* after) {
  df_sblock* b = (df_sblock*)df_alloc(DF_BLOCK_SIZE);
  memset(b, 0, sizeof *b);
  b->prev = after;
  b->next = after ? after->next : r->first;
  if (b->next) {
    b->next->prev = b;
  } else {
    r->last = b;
  }
  if (after) {
    after->next = b;
  } else {
    r->first = b;
  }
  return b;
}

static void df_block_unlink(df_sroot* r, df_sblock* b) {
  if (b->prev) {
    b->prev->next = b->next;
  } else {
    r->first = b->next;
  }
  if (b->next) {
    b->next->prev = b->prev;
  } else {
    r->last = b->prev;
  }
  if (r->cur == b) {
    r->cur = NULL;
  }
  free(b);
}

static void df_bs_free_blocks(df_sroot* r) {
  df_sblock* b = r->first;
  while (b) {
    df_sblock* next = b->next;
    free(b);
    b = next;
  }
  r->first = r->last = r->cur = NULL;
  r->chars = r->bytes = r->curChar = r->curByte = 0;
}

/* writes the bytes after the text of block b, adding blocks after it when it is full; returns the last block written */
static df_sblock* df_bs_fill_after(df_sroot* r, df_sblock* b, const char* p, uint64_t n) {
  while (n > 0) {
    uint64_t room = b ? DF_BLOCK_PAYLOAD - b->used : 0;
    uint64_t take = n <= room ? n : room;
    if (take < n) {
      while (take > 0 && ((unsigned char)p[take] & 0xC0) == 0x80) {
        take--;
      }
    }
    if (take == 0) {
      b = df_block_add(r, b);
      continue;
    }
    uint64_t chars = df_utf8_count(p, take);
    memcpy(DF_BLOCK_TEXT(b) + b->used, p, take);
    b->used = (uint16_t)(b->used + take);
    b->chars = (uint16_t)(b->chars + chars);
    r->bytes += take;
    r->chars += chars;
    p += take;
    n -= take;
  }
  return b;
}

static void df_bs_push(df_sroot* r, const char* p, uint64_t n) {
  df_bs_fill_after(r, r->last, p, n);
}

/* appends the text of any string (block by block for a block string) */
static void df_bs_push_str(df_sroot* r, df_string s) {
  const uint8_t* h = df_str_res(s);
  if (df_is_block(h)) {
    for (const df_sblock* b = ((const df_sroot*)h)->first; b; b = b->next) {
      df_bs_push(r, DF_BLOCK_TEXT(b), b->used);
    }
    return;
  }
  df_sv v = df_view(s);
  df_bs_push(r, v.p, v.len);
}

/* the byte offset of the k-th char of a block (the end of its text when k is past it) */
static uint64_t df_block_offset(const df_sblock* b, uint64_t k) {
  const char* t = DF_BLOCK_TEXT(b);
  uint64_t n = 0;
  for (uint64_t i = 0; i < b->used; i++) {
    if (((unsigned char)t[i] & 0xC0) != 0x80) {
      if (n == k) {
        return i;
      }
      n++;
    }
  }
  return b->used;
}

/* the block that holds char 'pos' (pos == the total: the end) and where it starts; it starts from the cursor, the first or the last
   block, whichever is closest, and walks whole blocks by their char count. It only reads. */
static df_sblock* df_bs_seek(const df_sroot* r, uint64_t pos, uint64_t* startChar, uint64_t* startByte) {
  df_sblock* b = r->first;
  uint64_t c = 0;
  uint64_t y = 0;
  uint64_t best = pos;
  if (r->cur) {
    uint64_t d = pos >= r->curChar ? pos - r->curChar : r->curChar - pos;
    if (d < best) {
      best = d;
      b = r->cur;
      c = r->curChar;
      y = r->curByte;
    }
  }
  if (r->last && r->chars - pos < best) {
    b = r->last;
    c = r->chars - r->last->chars;
    y = r->bytes - r->last->used;
  }
  while (b && pos < c && b->prev) {
    b = b->prev;
    c -= b->chars;
    y -= b->used;
  }
  while (b && pos >= c + b->chars && b->next) {
    c += b->chars;
    y += b->used;
    b = b->next;
  }
  *startChar = c;
  *startByte = y;
  return b;
}

/* the char at a position (pos below the total) */
static uint32_t df_bs_char_at(const df_sroot* r, uint64_t pos) {
  uint64_t c, y;
  df_sblock* b = df_bs_seek(r, pos, &c, &y);
  const unsigned char* t = (const unsigned char*)DF_BLOCK_TEXT(b) + df_block_offset(b, pos - c);
  uint32_t cp = t[0];
  int extra = cp < 0x80 ? 0 : (cp & 0xE0) == 0xC0 ? 1 : (cp & 0xF0) == 0xE0 ? 2 : (cp & 0xF8) == 0xF0 ? 3 : 0;
  cp &= extra == 0 ? 0xFF : (0x3F >> extra);
  for (int k = 1; k <= extra; k++) {
    cp = (cp << 6) | (t[k] & 0x3F);
  }
  return cp;
}

/* in place: 'len' chars at 'pos' are replaced by the bytes 'ins'. Only the blocks touched change: a full block is split, a block left
   empty goes away, two neighbours that fit in one are merged. The cursor ends on the block of the change. */
static void df_bs_splice(df_sroot* r, uint64_t pos, uint64_t len, const char* ins, uint64_t insBytes) {
  if (pos > r->chars) {
    pos = r->chars;
  }
  if (len > r->chars - pos) {
    len = r->chars - pos;
  }
  uint64_t c = 0;
  uint64_t y = 0;
  df_sblock* b = r->first ? df_bs_seek(r, pos, &c, &y) : NULL;
  uint64_t off = b ? df_block_offset(b, pos - c) : 0;
  /* removal, from b on */
  uint64_t left = len;
  df_sblock* at = b;
  uint64_t from = off;
  while (left > 0 && at) {
    char* t = DF_BLOCK_TEXT(at);
    uint64_t end = from;
    uint64_t n = 0;
    while (end < at->used && n < left) {
      end++;
      while (end < at->used && ((unsigned char)t[end] & 0xC0) == 0x80) {
        end++;
      }
      n++;
    }
    memmove(t + from, t + end, at->used - end);
    at->used = (uint16_t)(at->used - (end - from));
    at->chars = (uint16_t)(at->chars - n);
    r->bytes -= end - from;
    r->chars -= n;
    left -= n;
    df_sblock* next = at->next;
    if (at->used == 0 && at != b) {
      df_block_unlink(r, at);
    }
    at = next;
    from = 0;
  }
  /* insertion at (b, off) */
  if (insBytes > 0) {
    if (!b) {
      b = df_bs_fill_after(r, NULL, ins, insBytes);
      b = r->first;
      c = 0;
      y = 0;
    } else if (b->used + insBytes <= DF_BLOCK_PAYLOAD) {
      char* t = DF_BLOCK_TEXT(b);
      uint64_t chars = df_utf8_count(ins, insBytes);
      memmove(t + off + insBytes, t + off, b->used - off);
      memcpy(t + off, ins, insBytes);
      b->used = (uint16_t)(b->used + insBytes);
      b->chars = (uint16_t)(b->chars + chars);
      r->bytes += insBytes;
      r->chars += chars;
    } else {
      /* b keeps its text up to off; the inserted bytes and the rest of b follow, in b and in new blocks after it */
      uint64_t tailBytes = b->used - off;
      char* tail = (char*)malloc(tailBytes ? tailBytes : 1);
      if (!tail) {
        df_out_of_memory();
      }
      memcpy(tail, DF_BLOCK_TEXT(b) + off, tailBytes);
      uint64_t tailChars = df_utf8_count(tail, tailBytes);
      b->used = (uint16_t)off;
      b->chars = (uint16_t)(b->chars - tailChars);
      r->bytes -= tailBytes;
      r->chars -= tailChars;
      df_sblock* last = df_bs_fill_after(r, b, ins, insBytes);
      df_bs_fill_after(r, last, tail, tailBytes);
      free(tail);
    }
  }
  if (!b) {
    return;
  }
  /* a block left empty goes; neighbours that fit in one block are merged */
  if (b->used == 0 && (b->prev || b->next)) {
    df_block_unlink(r, b);
    r->cur = NULL;
    return;
  }
  if (b->next && b->used + b->next->used <= DF_BLOCK_PAYLOAD && (b->used < DF_BLOCK_PAYLOAD / 2 || b->next->used < DF_BLOCK_PAYLOAD / 2)) {
    df_sblock* n = b->next;
    memcpy(DF_BLOCK_TEXT(b) + b->used, DF_BLOCK_TEXT(n), n->used);
    b->used = (uint16_t)(b->used + n->used);
    b->chars = (uint16_t)(b->chars + n->chars);
    df_block_unlink(r, n);
  }
  if (b->prev && b->prev->used + b->used <= DF_BLOCK_PAYLOAD && (b->used < DF_BLOCK_PAYLOAD / 2 || b->prev->used < DF_BLOCK_PAYLOAD / 2)) {
    df_sblock* p = b->prev;
    c -= p->chars;
    y -= p->used;
    memcpy(DF_BLOCK_TEXT(p) + p->used, DF_BLOCK_TEXT(b), b->used);
    p->used = (uint16_t)(p->used + b->used);
    p->chars = (uint16_t)(p->chars + b->chars);
    df_block_unlink(r, b);
    b = p;
  }
  if (r->bytes == 0) {
    df_bs_free_blocks(r);
    return;
  }
  r->cur = b;
  r->curChar = c;
  r->curByte = y;
}

static df_string df_bs_from(const char* p, uint64_t len) {
  df_sroot* r = df_bs_new();
  df_bs_push(r, p, len);
  return (df_string)r;
}

/* a new string with a copy of the bytes */
static df_string df_str_make(const char* p, uint64_t len) {
  if (len > DF_STR_SINGLE_MAX) {
    return df_bs_from(p, len);
  }
  char* text;
  df_string s = df_str_alloc(len, df_utf8_count(p, len), &text);
  memcpy(text, p, len);
  return s;
}

static df_string df_str_lit(const char* p, uint64_t len) {
  return df_str_make(p, len);
}

static df_string df_str_concat(df_string a, df_string b) {
  uint64_t total = df_str_len(a) + df_str_len(b);
  if (total > DF_STR_SINGLE_MAX) {
    df_sroot* r = df_bs_new();
    df_bs_push_str(r, a);
    df_bs_push_str(r, b);
    return (df_string)r;
  }
  df_sv x = df_view(a), y = df_view(b);
  char* text;
  df_string s = df_str_alloc(x.len + y.len, df_str_chars(a) + df_str_chars(b), &text);
  memcpy(text, x.p, x.len);
  memcpy(text + x.len, y.p, y.len);
  return s;
}

static df_string df_str_from_buf(const char* buf) {
  return df_str_make(buf, strlen(buf));
}

static df_string df_str_from_cstr(const char* text) {
  return df_str_make(text, strlen(text));
}

/* a copy of the text: the Message of an exception stays valid after the exception is released */
static df_string df_str_dup_cstr(const char* text) {
  return df_str_make(text, strlen(text));
}

/* ---- ownership of strings (spec 4, 12) ----
   The owner frees a string at the end of its scope; a borrower never does. A literal of the program is never freed. */

static void df_str_free(df_string s) {
  uint8_t* h = (uint8_t*)s;
  if (!h) {
    return;
  }
  if ((h[0] & 0x0F) == DF_STR_MOVED) {
    uint8_t* to;
    memcpy(&to, h + 4, sizeof to);
    df_str_free((df_string)to);
    DF_STR_TRACE_FREE();
    free(h);
    return;
  }
  if ((h[0] & 0x0F) == DF_STR_LITERAL && !(h[0] & DF_STR_HEAP)) {
    return;
  }
  if (df_is_block(h)) {
    df_bs_free_blocks((df_sroot*)h);
  }
  DF_STR_TRACE_FREE();
  free(h);
}

/* a copy that its receiver owns (a literal is shared: it is never freed) */
static df_string df_str_dup(df_string s) {
  const uint8_t* h = df_str_res(s);
  if (!h || ((h[0] & 0x0F) == DF_STR_LITERAL && !(h[0] & DF_STR_HEAP))) {
    return (df_string)h;
  }
  if (df_is_block(h) && ((const df_sroot*)h)->bytes > DF_STR_SINGLE_MAX) {
    df_sroot* r = df_bs_new();   /* a copy is in the smallest form: blocks packed full, or a single block */
    df_bs_push_str(r, s);
    return (df_string)r;
  }
  df_sv v = df_view(s);
  char* text;
  df_string r = df_str_alloc(v.len, df_str_chars(s), &text);
  memcpy(text, v.p, v.len);
  return r;
}

/* a variable: the old string ends when the variable owned it; 'fresh' says whether the variable owns the new one */
static void df_str_set(df_string* slot, bool* owned, df_string value, bool fresh) {
  df_string old = *slot;
  if (old == value) {
    *owned = *owned || fresh;
    return;
  }
  if (*owned) {
    df_str_free(old);
  }
  *slot = value;
  *owned = fresh;
}

/* a place that always owns its string (a field, an item): a borrowed value is copied */
static void df_str_put(df_string* slot, df_string value, bool fresh) {
  df_string old = *slot;
  if (old == value) {
    return;
  }
  *slot = fresh ? value : df_str_dup(value);
  df_str_free(old);
}

/* the value of an owning place: a new string is kept, a borrowed one is copied */
static df_string df_str_own(df_string value, bool fresh) {
  return fresh ? value : df_str_dup(value);
}

/* a + b when the result takes the place of a, which is freed (code that builds a text); catx frees b too */
static df_string df_str_cat(df_string a, df_string b) {
  df_string r = df_str_concat(a, b);
  df_str_free(a);
  return r;
}

static df_string df_str_catx(df_string a, df_string b) {
  df_string r = df_str_concat(a, b);
  df_str_free(a);
  df_str_free(b);
  return r;
}

/* the end of a variable that may own its string */
static void df_str_drop(df_string* slot, bool* owned) {
  if (*owned) {
    df_str_free(*slot);
  }
  *slot = NULL;
  *owned = false;
}

/* ---- changes in place (spec 4.1): the string keeps its identity (its first allocation), so a borrow sees the change ----
   A string that outgrows its allocation moves: its first allocation becomes class 13 (the high 4 bits keep its old class) and points to
   the new one; a second move frees the previous target. A literal of the program, or the empty string, is never written: the variable
   gets a new string of its own instead. */

/* the text bytes an allocation can hold (0 for a literal of the program, which is never written) */
static uint64_t df_str_cap(const uint8_t* t) {
  unsigned cls = t[0] & 0x0F;
  if (cls <= 7) {
    return ((uint64_t)32 << cls) - 5;
  }
  if (cls == DF_STR_LITERAL && (t[0] & DF_STR_HEAP)) {
    return df_str_w64(t + 12);
  }
  return 0;
}

static char* df_str_text(uint8_t* t) {
  return (char*)t + ((t[0] & 0x0F) <= 7 ? 4 : 20);
}

static void df_str_set_lens(uint8_t* t, uint64_t bytes, uint64_t chars) {
  if ((t[0] & 0x0F) <= 7) {
    t[0] = (uint8_t)((t[0] & 0x0F) | ((bytes & 0x0F) << 4));
    t[1] = (uint8_t)(bytes >> 4);
    uint16_t c = (uint16_t)chars;
    memcpy(t + 2, &c, sizeof c);
  } else {
    uint32_t w[4] = { (uint32_t)chars, (uint32_t)(chars >> 32), (uint32_t)bytes, (uint32_t)(bytes >> 32) };
    memcpy(t + 4, w, sizeof w);
  }
  df_str_text(t)[bytes] = 0;
}

/* the byte offset of a char position in a text (the end when the position is past it) */
static uint64_t df_utf8_offset(const char* p, uint64_t len, uint64_t chars) {
  uint64_t n = 0;
  for (uint64_t i = 0; i < len; i++) {
    if (((unsigned char)p[i] & 0xC0) != 0x80) {
      if (n == chars) {
        return i;
      }
      n++;
    }
  }
  return len;
}

/* the place takes the new string 'value' as its text and keeps its identity: a block string keeps its root and takes the blocks of
   value; a smaller form points to value from its first allocation (class 13); the empty string or a literal is simply replaced */
static void df_str_become(df_string* slot, bool* owned, df_string value) {
  uint8_t* f = (uint8_t*)*slot;
  uint8_t* t = (uint8_t*)df_str_res(*slot);
  if (!t || (!df_is_block(t) && (t[0] & 0x0F) == DF_STR_LITERAL && !(t[0] & DF_STR_HEAP))) {
    *slot = value;
    *owned = value != NULL;
    return;
  }
  if (df_is_block(t)) {
    df_sroot* r = (df_sroot*)t;
    df_bs_free_blocks(r);
    df_sroot* v = (df_sroot*)df_str_res(value);
    if (df_is_block((const uint8_t*)v) && (uint8_t*)v == (uint8_t*)value) {
      r->first = v->first;
      r->last = v->last;
      r->chars = v->chars;
      r->bytes = v->bytes;
      v->first = v->last = NULL;
      df_str_free(value);
    } else {
      df_bs_push_str(r, value);
      df_str_free(value);
    }
    return;
  }
  if (f != t) {
    df_str_free((df_string)t);
  } else {
    unsigned old = (f[0] & 0x0F) <= 7 ? (f[0] & 0x0F) : 0x0F;
    f[0] = (uint8_t)(DF_STR_MOVED | (old << 4));
  }
  memcpy(f + 4, &value, sizeof value);
}

/* room for 'bytes' bytes (4091 at most) in a string that is not a block string, keeping its first 'keep' bytes; returns the allocation
   to write into (bytes > 0) */
static uint8_t* df_str_room(df_string* slot, bool* owned, uint64_t bytes, uint64_t keep) {
  uint8_t* f = (uint8_t*)*slot;
  uint8_t* t = (uint8_t*)df_str_res(*slot);
  if (t && bytes <= df_str_cap(t)) {
    return t;
  }
  char* text;
  uint8_t* n = (uint8_t*)df_str_alloc(bytes, 0, &text);
  if (t && keep) {
    memcpy(text, df_str_text(t), keep);
  }
  if (!t || df_str_cap(t) == 0) {
    *slot = (df_string)n;   /* the empty string or a literal: the variable gets a string of its own */
    *owned = true;
    return n;
  }
  if (f != t) {
    df_str_free((df_string)t);   /* moved already: the previous target was reached only through the first allocation */
  } else {
    unsigned old = (f[0] & 0x0F) <= 7 ? (f[0] & 0x0F) : 0x0F;
    f[0] = (uint8_t)(DF_STR_MOVED | (old << 4));
  }
  memcpy(f + 4, &n, sizeof n);
  return n;
}

static void df_str_clear(df_string* slot, bool* owned) {
  uint8_t* t = (uint8_t*)df_str_res(*slot);
  if (!t) {
    return;
  }
  if (df_is_block(t)) {
    df_bs_free_blocks((df_sroot*)t);   /* it stays a block string, empty */
    return;
  }
  if (df_str_cap(t) == 0) {
    *slot = NULL;   /* a literal is not written */
    *owned = false;
    return;
  }
  df_str_set_lens(t, 0, 0);
}

/* s += text */
static void df_str_append(df_string* slot, bool* owned, df_string add) {
  df_sv a = df_view(add);
  if (a.len == 0) {
    return;
  }
  bool same = df_str_res(add) == df_str_res(*slot);
  df_string source = same ? df_str_dup(add) : add;   /* s += s: the text may move while it is copied */
  uint8_t* target = (uint8_t*)df_str_res(*slot);
  uint64_t len = df_str_len(*slot);
  if (df_is_block(target)) {
    df_bs_push_str((df_sroot*)target, source);   /* the end of the last block, then new blocks */
    if (same) {
      df_str_free(source);
    }
    return;
  }
  if (len + df_str_len(source) > DF_STR_SINGLE_MAX) {
    df_sroot* r = df_bs_new();   /* beyond 4091 bytes it becomes a block string */
    df_bs_push_str(r, *slot);
    df_bs_push_str(r, source);
    df_str_become(slot, owned, (df_string)r);
    if (same) {
      df_str_free(source);
    }
    return;
  }
  a = df_view(source);
  uint64_t chars = df_str_chars(*slot) + df_str_chars(source);
  uint8_t* t = df_str_room(slot, owned, len + a.len, len);
  memcpy(df_str_text(t) + len, a.p, a.len);
  df_str_set_lens(t, len + a.len, chars);
  if (same) {
    df_str_free(source);
  }
}

/* the string at *slot takes the text of 'value', a new string that ends here (s.trim() and s = s.trim() in place) */
static void df_str_take(df_string* slot, bool* owned, df_string value) {
  const uint8_t* t = df_str_res(*slot);
  if (t == df_str_res(value)) {
    return;
  }
  if (df_is_block(t) || df_str_len(value) > DF_STR_SINGLE_MAX) {
    df_str_become(slot, owned, value);   /* a block string stays one; a text beyond 4091 bytes is a block string */
    return;
  }
  df_sv v = df_view(value);
  if (!t || df_str_cap(t) == 0) {
    *slot = value;   /* the empty string or a literal: the new string becomes the variable's own */
    *owned = value != NULL;
    return;
  }
  if (v.len == 0) {
    df_str_clear(slot, owned);
  } else {
    uint8_t* room = df_str_room(slot, owned, v.len, 0);
    memcpy(df_str_text(room), v.p, v.len);
    df_str_set_lens(room, v.len, df_str_chars(value));
  }
  df_str_free(value);
}

/* the smallest form: a moved string goes back into its first allocation when it fits, otherwise into the smallest class */
static void df_str_shrink(df_string* slot) {
  uint8_t* f = (uint8_t*)*slot;
  uint8_t* t = (uint8_t*)df_str_res(*slot);
  if (df_is_block(t)) {
    df_sroot* r = (df_sroot*)t;
    if (r->bytes <= DF_STR_SINGLE_MAX) {
      df_sv v = df_view(*slot);
      uint64_t chars = r->chars;
      unsigned old = f[0] >> 4;
      if (f != t && old <= 7 && v.len <= ((uint64_t)32 << old) - 5) {
        memmove(f + 4, v.p, v.len);   /* back into the first allocation */
        f[0] = (uint8_t)old;
        df_str_set_lens(f, v.len, chars);
        df_str_free((df_string)t);
        return;
      }
      if (f == t && v.len <= 59) {
        df_bs_free_blocks(r);   /* the root takes the text as a class 1 (it has 64 bytes) */
        memmove(f + 4, v.p, v.len);
        f[0] = 1;
        df_str_set_lens(f, v.len, chars);
        return;
      }
      df_string n = df_str_make(v.p, v.len);
      if (f == t) {
        df_bs_free_blocks(r);
        f[0] = (uint8_t)(DF_STR_MOVED | (0x0F << 4));
        memcpy(f + 4, &n, sizeof n);
      } else {
        memcpy(f + 4, &n, sizeof n);
        df_str_free((df_string)t);
      }
      return;
    }
    uint64_t blocks = 0;
    for (df_sblock* b = r->first; b; b = b->next) {
      blocks++;
    }
    if (blocks > (r->bytes + DF_BLOCK_PAYLOAD - 1) / DF_BLOCK_PAYLOAD) {
      df_sroot* packed = df_bs_new();   /* the blocks packed full */
      df_bs_push_str(packed, (df_string)r);
      df_bs_free_blocks(r);
      r->first = packed->first;
      r->last = packed->last;
      r->chars = packed->chars;
      r->bytes = packed->bytes;
      packed->first = packed->last = NULL;
      df_str_free((df_string)packed);
    }
    return;
  }
  if (!t || t == f) {
    return;   /* not moved: the first allocation is its identity and stays */
  }
  df_sv v = df_view(*slot);
  uint64_t chars = df_str_chars(*slot);
  unsigned old = f[0] >> 4;
  if (old <= 7 && v.len <= ((uint64_t)32 << old) - 5) {
    memmove(f + 4, v.p, v.len);
    f[0] = (uint8_t)old;
    df_str_set_lens(f, v.len, chars);
    df_str_free((df_string)t);
    return;
  }
  char* text;
  uint8_t* n = (uint8_t*)df_str_alloc(v.len, chars, &text);
  if (n && df_str_cap(n) >= df_str_cap(t)) {
    df_str_free((df_string)n);
    return;
  }
  memcpy(text, v.p, v.len);
  memcpy(f + 4, &n, sizeof n);
  df_str_free((df_string)t);
}

/* insert, remove, replaceAt and prepend in place: 'len' chars at 'pos' are replaced by the text of ins (NULL: nothing). A block string
   changes only the blocks touched; a smaller string is rebuilt and taken */
static void df_str_splice_in(df_string* slot, bool* owned, uint64_t pos, uint64_t len, df_string ins) {
  uint8_t* t = (uint8_t*)df_str_res(*slot);
  if (df_is_block(t)) {
    df_string copy = df_str_res(ins) == t ? df_str_dup(ins) : NULL;
    df_sv v = df_view(copy ? copy : ins);
    df_bs_splice((df_sroot*)t, pos, len, v.p, v.len);
    df_str_free(copy);
    return;
  }
  df_sv s = df_view(*slot);
  df_sv v = df_view(ins);
  uint64_t from = df_utf8_offset(s.p, s.len, pos);
  uint64_t to = from + df_utf8_offset(s.p + from, s.len - from, len);
  uint64_t total = s.len - (to - from) + v.len;
  char* text = (char*)malloc(total ? total : 1);
  if (!text) {
    df_out_of_memory();
  }
  memcpy(text, s.p, from);
  memcpy(text + from, v.p, v.len);
  memcpy(text + from + v.len, s.p + to, s.len - to);
  df_string changed = df_str_make(text, total);
  free(text);
  df_str_take(slot, owned, changed);
}

/* the string an item of a collection keeps (spec 4): always its own copy, in its smallest form. A new string already in its
   smallest form is kept; a borrowed one, a moved one or one in a bigger class than it needs is copied */
static df_string df_str_item(df_string value, bool fresh) {
  if (!fresh) {
    return df_str_dup(value);
  }
  const uint8_t* h = (const uint8_t*)value;
  if (!h) {
    return value;
  }
  unsigned cls = h[0] & 0x0F;
  if (cls == DF_STR_LITERAL || (cls <= 7 && (cls == 0 || df_str_len(value) > ((uint64_t)32 << (cls - 1)) - 5))) {
    return value;
  }
  if (cls == DF_STR_BLOCK && ((const df_sroot*)h)->bytes > DF_STR_SINGLE_MAX) {
    df_str_shrink(&value);   /* the blocks packed full */
    return value;
  }
  df_string copy = df_str_dup(value);
  df_str_free(value);
  return copy;
}

/* a place that has no flag of its own (a field, an item, a tuple item): it always owns */
static bool df_str_owner = true;

/* new strings that are only read in a statement (an operand, an argument): freed when the statement ends. A function frees the
   ones above the mark it took when it started, so the temporaries of its callers stay */
static df_string* df_tmps = NULL;
static size_t df_tmp_top = 0;
static size_t df_tmp_cap = 0;

static df_string df_tmp(df_string s) {
  if (!s) {
    return s;
  }
  if (df_tmp_top == df_tmp_cap) {
    df_tmp_cap = df_tmp_cap ? df_tmp_cap * 2 : 64;
    df_tmps = (df_string*)realloc(df_tmps, df_tmp_cap * sizeof(df_string));
    if (!df_tmps) {
      df_out_of_memory();
    }
  }
  df_tmps[df_tmp_top++] = s;
  return s;
}

static void df_tmp_drop(size_t mark) {
  while (df_tmp_top > mark) {
    df_str_free(df_tmps[--df_tmp_top]);
  }
}

/* the same string is equal; then the byte lengths, then the bytes (spec 4) */
static bool df_str_eq(df_string a, df_string b) {
  if (df_str_res(a) == df_str_res(b)) {
    return true;
  }
  df_sv x = df_view(a), y = df_view(b);
  return x.len == y.len && memcmp(x.p, y.p, x.len) == 0;
}

/* ---- objects (classes): every object starts with its class descriptor ---- */

typedef struct df_trait {
  uint64_t id;
  const char* name;
} df_trait;

/* how one class implements one trait: the function for each method and the offset of each property */
typedef struct {
  const df_trait* trait;
  const void* const* methods;
  const uint32_t* offsets;
} df_impl;

typedef struct df_class {
  uint64_t id;
  const char* name;
  const struct df_class* parent;
  const void* const* vtable;
  df_string (*str)(const void*);
  const df_impl* impls;
  uint32_t nimpls;
  void (*drop)(void*);   /* ends the object: its atEnd, then the objects it owns */
  df_uns (*hash)(const void*);   /* .hash (spec 6.2.1): the override of get_hash() or the default; NULL when the program uses no hash */
} df_class;

typedef struct { const df_class* cls; } df_object;

/* x is Class: the class of the object, or one of its parents */
static bool df_is(const void* obj, const df_class* cls) {
  if (obj == NULL) {
    return false;
  }
  for (const df_class* c = ((const df_object*)obj)->cls; c; c = c->parent) {
    if (c == cls) {
      return true;
    }
  }
  return false;
}

static const df_impl* df_impl_of(const void* obj, const df_trait* trait) {
  const df_class* c = ((const df_object*)obj)->cls;
  for (uint32_t i = 0; i < c->nimpls; i++) {
    if (c->impls[i].trait == trait) {
      return &c->impls[i];
    }
  }
  return NULL;
}

/* x is TraitName */
static bool df_has_trait(const void* obj, const df_trait* trait) {
  return obj != NULL && df_impl_of(obj, trait) != NULL;
}

/* a destroyed object keeps its memory but points to this class */
static df_string df_dead_str(const void* obj) {
  (void)obj;
  return df_str_lit("(destroyed)", 11);
}

static const df_class df_dead_class = { 0, "(destroyed)", NULL, NULL, df_dead_str, NULL, 0, NULL, NULL };

#define DF_IS_DEAD(p) (((const df_object*)(p))->cls == &df_dead_class)

/* releases the ownership held by a variable: the object ends; ending a destroyed object is a double free */
static void df_drop(void* obj) {
  if (obj == NULL) {
    return;
  }
  const df_class* c = ((const df_object*)obj)->cls;
  if (c == &df_dead_class) {
    fflush(stdout);
    printf("Double free\n");
    df_panic(DF_PANIC_UNHANDLED);
  }
  if (c != NULL && c->drop != NULL) {
    c->drop(obj);
  }
}

/* the adopted children of a parent object (spec 12.5): a child moved with -> is registered with its parent, which nulls the child's reference to it
   when it ends. Each entry holds the address of the parent reference of the child and of its slot (the position + 1, 0 = not registered); a child
   that ends first takes itself out (the last entry moves into its place) */
typedef struct df_kid { void** parentRef; df_uns* slotRef; } df_kid;
typedef struct df_kids { df_kid* items; df_uns len; df_uns cap; } df_kids;

static void df_kids_add(df_kids** owner, void** parentRef, df_uns* slotRef) {
  df_kids* kids = *owner;
  if (kids == NULL) {
    kids = (df_kids*)df_alloc(sizeof(df_kids));
    kids->items = NULL;
    kids->len = 0;
    kids->cap = 0;
    *owner = kids;
  }
  if (kids->len == kids->cap) {
    df_uns cap = kids->cap < 4 ? 4 : kids->cap * 2;
    df_kid* bigger = (df_kid*)df_alloc(cap * sizeof(df_kid));
    if (kids->len > 0) {
      memcpy(bigger, kids->items, kids->len * sizeof(df_kid));
    }
    free(kids->items);
    kids->items = bigger;
    kids->cap = cap;
  }
  kids->items[kids->len].parentRef = parentRef;
  kids->items[kids->len].slotRef = slotRef;
  kids->len++;
  *slotRef = kids->len;
}

static void df_kids_remove(df_kids* kids, df_uns slot) {
  if (kids == NULL || slot == 0 || slot > kids->len) {
    return;
  }
  df_uns index = slot - 1;
  kids->len--;
  if (index != kids->len) {
    kids->items[index] = kids->items[kids->len];
    *kids->items[index].slotRef = index + 1;
  }
}

/* the parent ends: its adopted children lose their reference to it */
static void df_kids_release(df_kids** owner) {
  df_kids* kids = *owner;
  if (kids == NULL) {
    return;
  }
  for (df_uns i = 0; i < kids->len; i++) {
    *kids->items[i].parentRef = NULL;
  }
  free(kids->items);
  free(kids);
  *owner = NULL;
}

/* the owner of an exception record is done with it (a catch ended, a silent exception was ignored): its buffers and the record are freed */
static void df_exc_release(df_exception* exception) {
  if (exception == NULL) {
    return;
  }
  if (exception->cls != NULL) {
    df_drop(exception);   /* an exception object of the program: its destructor runs */
  }
  free(exception->message);
  free(exception->function);
  free(exception);
  df_exc_live--;
}

static df_string df_str_obj(const void* obj) {
  return obj ? ((const df_object*)obj)->cls->str(obj) : df_str_lit("null", 4);
}

/* ---- arrays (arr<T>): pointer and length; a null array has no pointer ---- */

typedef struct { df_string* p; df_uns len; } df_arr_string;
typedef struct { uint8_t* p; df_uns len; } df_arr_u8;

static void* df_calloc(df_uns count, df_uns size) {
  void* p = calloc(count ? count : 1, size ? size : 1);
  if (!p) {
    df_out_of_memory();
  }
  return p;
}

/* true when the index is inside; otherwise raises IndexOutOfRangeException and the caller checks df_err */
static bool df_idx_ok(df_uns len, df_uns index, const char* function) {
  if (index < len) {
    return true;
  }
  df_raise(&df_desc_IndexOutOfRangeException, function);
  return false;
}

/* an index given as a signed number: a negative one (or one past the range of uns) becomes a value no array has */
static df_uns df_idx_arg(int64_t v) {
  return v < 0 || (uint64_t)v > DF_UNS_MAX ? DF_UNS_MAX : (df_uns)v;
}

/* an index given as an unsigned number wider than uns: one past the range becomes a value no array has */
static df_uns df_idx_wide(uint64_t v) {
  return v > DF_UNS_MAX ? DF_UNS_MAX : (df_uns)v;
}

static df_string df_str_from_i64(int64_t v) {
  char buf[32];
  snprintf(buf, sizeof buf, "%lld", (long long)v);
  return df_str_from_buf(buf);
}

static df_string df_str_from_u64(uint64_t v) {
  char buf[32];
  snprintf(buf, sizeof buf, "%llu", (unsigned long long)v);
  return df_str_from_buf(buf);
}

/* a printed collection shows its first DF_PRINT_ITEMS items (spec 6.1): the end is "]", or ", ...] (37 items)" when there are more */
#define DF_PRINT_ITEMS 3

static df_string df_str_coll_end(df_string r, uint64_t total) {
  if (total <= DF_PRINT_ITEMS) {
    return df_str_catx(r, df_str_lit("]", 1));
  }
  char buf[48];
  snprintf(buf, sizeof buf, ", ...] (%llu items)", (unsigned long long)total);
  return df_str_catx(r, df_str_from_buf(buf));
}

static df_string df_str_from_bool(bool v) {
  return v ? df_str_lit("true", 4) : df_str_lit("false", 5);
}

static df_string df_str_from_char(uint32_t c) {
  char buf[4];
  uint64_t n;
  if (c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF)) {
    c = 0;
  }
  if (c < 0x80) {
    buf[0] = (char)c;
    n = 1;
  } else if (c < 0x800) {
    buf[0] = (char)(0xC0 | (c >> 6));
    buf[1] = (char)(0x80 | (c & 0x3F));
    n = 2;
  } else if (c < 0x10000) {
    buf[0] = (char)(0xE0 | (c >> 12));
    buf[1] = (char)(0x80 | ((c >> 6) & 0x3F));
    buf[2] = (char)(0x80 | (c & 0x3F));
    n = 3;
  } else {
    buf[0] = (char)(0xF0 | (c >> 18));
    buf[1] = (char)(0x80 | ((c >> 12) & 0x3F));
    buf[2] = (char)(0x80 | ((c >> 6) & 0x3F));
    buf[3] = (char)(0x80 | (c & 0x3F));
    n = 4;
  }
  return df_str_make(buf, n);
}

/* shortest text that reads back as the same value (like C#) */
static df_string df_str_from_real(double v, int maxDigits, bool isFloat) {
  char buf[64];
  if (isnan(v)) {
    return df_str_lit("NaN", 3);
  }
  if (isinf(v)) {
    return v > 0 ? df_str_lit("\xE2\x88\x9E", 3) : df_str_lit("-\xE2\x88\x9E", 4);
  }
  if (v == 0) {
    return signbit(v) ? df_str_lit("-0", 2) : df_str_lit("0", 1);
  }
  int digits;
  for (digits = 1; digits <= maxDigits; digits++) {
    snprintf(buf, sizeof buf, "%.*e", digits - 1, v);
    double back = strtod(buf, NULL);
    if (isFloat ? (float)back == (float)v : back == v) {
      break;
    }
  }
  if (digits > maxDigits) {
    digits = maxDigits;
  }
  snprintf(buf, sizeof buf, "%.*e", digits - 1, v);
  int exponent = atoi(strchr(buf, 'e') + 1);
  if (exponent >= -5 && exponent < 15) {
    /* plain notation, as C# prints it */
    int decimals = digits - 1 - exponent;
    snprintf(buf, sizeof buf, "%.*f", decimals < 0 ? 0 : decimals, v);
  } else {
    for (char* c = buf; *c; c++) {
      if (*c == 'e') {
        *c = 'E';
      }
    }
  }
  return df_str_from_buf(buf);
}

static df_string df_str_from_r64(double v) {
  return df_str_from_real(v, 17, false);
}

static df_string df_str_from_r32(float v) {
  return df_str_from_real((double)v, 9, true);
}

/* ---- conversions to bool ---- */

static bool df_rbool(double v) {
  return v != 0 && !isnan(v);
}

/* ---- console ---- */

/* a block string is written block by block */
static void df_str_fwrite(df_string text, FILE* out) {
  const uint8_t* h = df_str_res(text);
  if (df_is_block(h)) {
    for (const df_sblock* b = ((const df_sroot*)h)->first; b; b = b->next) {
      fwrite(DF_BLOCK_TEXT(b), 1, b->used, out);
    }
    return;
  }
  df_sv s = df_view(text);
  fwrite(s.p, 1, (size_t)s.len, out);
}

static void df_console_log(df_string text) {
  df_str_fwrite(text, stdout);
  fputc('\n', stdout);
}

static void df_console_log_error(df_string text) {
  fflush(stdout);
  df_str_fwrite(text, stderr);
  fputc('\n', stderr);
}

/* no newline */
static void df_console_print(df_string text) {
  df_str_fwrite(text, stdout);
}

#ifdef _WIN32
#include <conio.h>
#include <io.h>
#else
#include <termios.h>
#include <unistd.h>
#include <signal.h>

/* Console.read puts the terminal in raw mode (no line buffer, no echo) and leaves it so between calls, so the keys pressed meanwhile
   are neither echoed nor held until Enter; readLine, the end of the program, a panic and the signals that stop it give it back */
static struct termios df_tty_saved;
static bool df_tty_raw = false;
static bool df_tty_hooked = false;

static void df_tty_restore(void) {
  if (df_tty_raw) {
    tcsetattr(STDIN_FILENO, TCSANOW, &df_tty_saved);
    df_tty_raw = false;
  }
}

static void df_tty_signal(int sig) {
  df_tty_restore();
  signal(sig, SIG_DFL);
  raise(sig);
}

static void df_tty_enter_raw(void) {
  if (df_tty_raw || tcgetattr(STDIN_FILENO, &df_tty_saved) != 0) {
    return;
  }
  struct termios raw = df_tty_saved;
  raw.c_lflag &= (tcflag_t)~(ICANON | ECHO);
  raw.c_cc[VMIN] = 0;
  raw.c_cc[VTIME] = 0;
  if (tcsetattr(STDIN_FILENO, TCSANOW, &raw) != 0) {
    return;
  }
  df_tty_raw = true;
  if (!df_tty_hooked) {
    df_tty_hooked = true;
    atexit(df_tty_restore);
    df_panic_hook = df_tty_restore;
    signal(SIGINT, df_tty_signal);
    signal(SIGTERM, df_tty_signal);
    signal(SIGHUP, df_tty_signal);
    signal(SIGQUIT, df_tty_signal);
  }
}
#endif

/* waits for Enter; the line keeps its newline when the input has one; "" at end of input */
static df_string df_console_get_input(void) {
  fflush(stdout);
#ifndef _WIN32
  df_tty_restore();
#endif
  uint64_t capacity = 128;
  uint64_t length = 0;
  char* buffer = (char*)df_alloc(capacity);
  int next;
  while ((next = getc(stdin)) != EOF) {
    if (length == capacity) {
      char* bigger = (char*)df_alloc(capacity * 2);
      memcpy(bigger, buffer, length);
      buffer = bigger;
      capacity *= 2;
    }
    buffer[length++] = (char)next;
    if (next == '\n') {
      break;
    }
  }
  return df_str_make(buffer, length);
}

/* one UTF-8 character from a byte reader; 0 at end of input */
static uint32_t df_decode_utf8(int first, int (*read_byte)(void)) {
  if (first < 0x80) {
    return (uint32_t)first;
  }
  int extra = (first & 0xE0) == 0xC0 ? 1 : (first & 0xF0) == 0xE0 ? 2 : (first & 0xF8) == 0xF0 ? 3 : 0;
  uint32_t code = (uint32_t)(first & (0x3F >> extra));
  for (int index = 0; index < extra; index++) {
    int next = read_byte();
    if (next == EOF) {
      return 0;
    }
    code = (code << 6) | (uint32_t)(next & 0x3F);
  }
  return code;
}

static int df_read_stdin_byte(void) {
  return getc(stdin);
}

/* Console.read (stdlib 2): the character of a key already pressed, without waiting, echo or Enter; -1 (null) when no key is
   waiting or the key has no character (arrows, function keys). Enter is '\n' on every OS. Not a terminal: the next character
   of the input, -1 at its end */
static int64_t df_console_read(void) {
  fflush(stdout);
#ifdef _WIN32
  if (_isatty(_fileno(stdin))) {
    if (!_kbhit()) {
      return -1;
    }
    wint_t key = _getwch();
    if (key == 0 || key == 0xE0) {
      _getwch();                /* arrows and function keys: two codes */
      return -1;
    }
    if (key >= 0xD800 && key <= 0xDBFF) {
      wint_t low = _getwch();
      return 0x10000 + (((int64_t)key - 0xD800) << 10) + ((int64_t)low - 0xDC00);
    }
    return key == '\r' ? '\n' : (int64_t)key;
  }
#else
  if (isatty(STDIN_FILENO)) {
    df_tty_enter_raw();
    unsigned char first;
    if (read(STDIN_FILENO, &first, 1) != 1) {
      return -1;
    }
    if (first == 0x1B) {
      /* an escape sequence (arrows, function keys) arrives in one piece: skip it; Esc alone is its character */
      unsigned char next;
      if (read(STDIN_FILENO, &next, 1) != 1) {
        return 0x1B;
      }
      if (next == '[' || next == 'O') {
        unsigned char part;
        while (read(STDIN_FILENO, &part, 1) == 1 && !(part >= 0x40 && part <= 0x7E)) {
        }
      }
      return -1;
    }
    if (first < 0x80) {
      return first;
    }
    int extra = (first & 0xE0) == 0xC0 ? 1 : (first & 0xF0) == 0xE0 ? 2 : (first & 0xF8) == 0xF0 ? 3 : 0;
    int64_t code = first & (0x3F >> extra);
    for (int index = 0; index < extra; index++) {
      unsigned char part;
      if (read(STDIN_FILENO, &part, 1) != 1) {
        return -1;
      }
      code = (code << 6) | (part & 0x3F);
    }
    return code;
  }
#endif
  int first = getc(stdin);
  if (first == EOF) {
    return -1;
  }
  return (int64_t)df_decode_utf8(first, df_read_stdin_byte);
}

/* Console.clear (stdlib 2): empties the console and puts the cursor at the top-left corner; nothing when the output is not a console */
static void df_console_clear(void) {
  fflush(stdout);
#ifdef _WIN32
  HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
  CONSOLE_SCREEN_BUFFER_INFO info;
  if (!GetConsoleScreenBufferInfo(out, &info)) {
    return;
  }
  DWORD cells = (DWORD)info.dwSize.X * (DWORD)info.dwSize.Y;
  COORD home = { 0, 0 };
  DWORD written;
  FillConsoleOutputCharacterW(out, L' ', cells, home, &written);
  FillConsoleOutputAttribute(out, info.wAttributes, cells, home, &written);
  SetConsoleCursorPosition(out, home);
#else
  if (!isatty(STDOUT_FILENO)) {
    return;
  }
  fputs("\x1b[H\x1b[2J", stdout);
  fflush(stdout);
#endif
}

/* Root.exit (stdlib 1): ends the program at once; the output is flushed, atEnd blocks and destructors do not run */
static void df_exit(int64_t code) {
  fflush(stdout);
  fflush(stderr);
  exit((int)code);
}

static void df_init(void) {
#ifdef _WIN32
  SetConsoleOutputCP(CP_UTF8);
  SetConsoleCP(CP_UTF8);
#endif
  atexit(df_exc_report);
}

/* ---- program arguments (spec 17.1): index 0 is the program name ---- */

#ifdef _WIN32
#include <shellapi.h>
#pragma comment(lib, "shell32.lib")
#endif

static df_arr_string df_args;

static void df_set_args(int argc, char** argv) {
#ifdef _WIN32
  /* the C arguments use the ANSI code page: read the wide command line and convert it to UTF-8 */
  int count = 0;
  wchar_t** wide = CommandLineToArgvW(GetCommandLineW(), &count);
  if (wide) {
    df_args.p = (df_string*)df_calloc((df_uns)count, sizeof(df_string));
    df_args.len = (df_uns)count;
    for (int index = 0; index < count; index++) {
      int bytes = WideCharToMultiByte(CP_UTF8, 0, wide[index], -1, NULL, 0, NULL, NULL);
      char* text = (char*)df_alloc((uint64_t)bytes);
      WideCharToMultiByte(CP_UTF8, 0, wide[index], -1, text, bytes, NULL, NULL);
      df_args.p[index] = df_str_make(text, (uint64_t)(bytes > 0 ? bytes - 1 : 0));
    }
    LocalFree(wide);
    return;
  }
#endif
  df_args.p = (df_string*)df_calloc((df_uns)argc, sizeof(df_string));
  df_args.len = (df_uns)argc;
  for (int index = 0; index < argc; index++) {
    df_args.p[index] = df_str_from_cstr(argv[index]);
  }
}

static df_arr_string df_get_args(void) {
  return df_args;
}

/* ---- checked integer division ---- */

/* on division by zero: raise DivisionByZeroException and return 0; the caller checks df_err */
#define DF_DIV_SIGNED(T, NAME, MIN) \
  static T df_div_##NAME(T a, T b, const char* function) { \
    if (b == 0) { df_raise(&df_desc_DivisionByZeroException, function); return 0; } \
    if (b == -1) return (T)(0 - (uint64_t)a); \
    return (T)(a / b); \
  } \
  static T df_mod_##NAME(T a, T b, const char* function) { \
    if (b == 0) { df_raise(&df_desc_DivisionByZeroException, function); return 0; } \
    if (b == -1) return 0; \
    return (T)(a % b); \
  }

#define DF_DIV_UNSIGNED(T, NAME) \
  static T df_div_##NAME(T a, T b, const char* function) { \
    if (b == 0) { df_raise(&df_desc_DivisionByZeroException, function); return 0; } \
    return (T)(a / b); \
  } \
  static T df_mod_##NAME(T a, T b, const char* function) { \
    if (b == 0) { df_raise(&df_desc_DivisionByZeroException, function); return 0; } \
    return (T)(a % b); \
  }

DF_DIV_SIGNED(int8_t, i8, INT8_MIN)
DF_DIV_SIGNED(int16_t, i16, INT16_MIN)
DF_DIV_SIGNED(int32_t, i32, INT32_MIN)
DF_DIV_SIGNED(int64_t, i64, INT64_MIN)
DF_DIV_UNSIGNED(uint8_t, u8)
DF_DIV_UNSIGNED(uint16_t, u16)
DF_DIV_UNSIGNED(uint32_t, u32)
DF_DIV_UNSIGNED(uint64_t, u64)

/* ---- saturating casts ---- */

#define DF_SAT_INT(T, NAME, LO, HI) \
  static T df_sat_##NAME##_i(int64_t v) { \
    return v < (int64_t)(LO) ? (T)(LO) : (v > (int64_t)(HI) ? (T)(HI) : (T)v); \
  } \
  static T df_sat_##NAME##_u(uint64_t v) { \
    return v > (uint64_t)(HI) ? (T)(HI) : (T)v; \
  } \
  static T df_sat_##NAME##_r(double v) { \
    if (isnan(v)) return 0; \
    if (v <= (double)(LO)) return (T)(LO); \
    if (v >= (double)(HI)) return (T)(HI); \
    return (T)v; \
  }

DF_SAT_INT(int8_t, i8, INT8_MIN, INT8_MAX)
DF_SAT_INT(int16_t, i16, INT16_MIN, INT16_MAX)
DF_SAT_INT(int32_t, i32, INT32_MIN, INT32_MAX)
DF_SAT_INT(uint8_t, u8, 0, UINT8_MAX)
DF_SAT_INT(uint16_t, u16, 0, UINT16_MAX)
DF_SAT_INT(uint32_t, u32, 0, UINT32_MAX)

/* the same casts leaving a note of the silent OutOfRangeCastException when the value does not fit (used where a catch handles it) */
#define DF_SAT_CHK(T, NAME, LO, HI) \
  static T df_satc_##NAME##_i(int64_t v) { \
    T r = df_sat_##NAME##_i(v); \
    if ((int64_t)r != v) df_silent_note(&df_desc_OutOfRangeCastException); \
    return r; \
  } \
  static T df_satc_##NAME##_u(uint64_t v) { \
    T r = df_sat_##NAME##_u(v); \
    if ((uint64_t)r != v) df_silent_note(&df_desc_OutOfRangeCastException); \
    return r; \
  } \
  static T df_satc_##NAME##_r(double v) { \
    T r = df_sat_##NAME##_r(v); \
    if (isnan(v) || v <= (double)(LO) - 1.0 || v >= (double)(HI) + 1.0) df_silent_note(&df_desc_OutOfRangeCastException); \
    return r; \
  }

DF_SAT_CHK(int8_t, i8, INT8_MIN, INT8_MAX)
DF_SAT_CHK(int16_t, i16, INT16_MIN, INT16_MAX)
DF_SAT_CHK(int32_t, i32, INT32_MIN, INT32_MAX)
DF_SAT_CHK(uint8_t, u8, 0, UINT8_MAX)
DF_SAT_CHK(uint16_t, u16, 0, UINT16_MAX)
DF_SAT_CHK(uint32_t, u32, 0, UINT32_MAX)

static int64_t df_sat_i64_i(int64_t v) { return v; }
static int64_t df_sat_i64_u(uint64_t v) { return v > (uint64_t)INT64_MAX ? INT64_MAX : (int64_t)v; }
static int64_t df_sat_i64_r(double v) {
  if (isnan(v)) return 0;
  if (v <= -9223372036854775808.0) return INT64_MIN;
  if (v >= 9223372036854775807.0) return INT64_MAX;
  return (int64_t)v;
}
static uint64_t df_sat_u64_i(int64_t v) { return v < 0 ? 0 : (uint64_t)v; }
static uint64_t df_sat_u64_u(uint64_t v) { return v; }
static uint64_t df_sat_u64_r(double v) {
  if (isnan(v) || v <= 0) return 0;
  if (v >= 18446744073709551615.0) return UINT64_MAX;
  return (uint64_t)v;
}

/* the checked casts of the 64-bit types */
static int64_t df_satc_i64_i(int64_t v) { return v; }
static int64_t df_satc_i64_u(uint64_t v) { if (v > (uint64_t)INT64_MAX) df_silent_note(&df_desc_OutOfRangeCastException); return df_sat_i64_u(v); }
static int64_t df_satc_i64_r(double v) {
  if (isnan(v) || v <= -9223372036854775809.0 || v >= 9223372036854775808.0) df_silent_note(&df_desc_OutOfRangeCastException);
  return df_sat_i64_r(v);
}
static uint64_t df_satc_u64_i(int64_t v) { if (v < 0) df_silent_note(&df_desc_OutOfRangeCastException); return df_sat_u64_i(v); }
static uint64_t df_satc_u64_u(uint64_t v) { return v; }
static uint64_t df_satc_u64_r(double v) {
  if (isnan(v) || v <= -1.0 || v >= 18446744073709551616.0) df_silent_note(&df_desc_OutOfRangeCastException);
  return df_sat_u64_r(v);
}

/* ---- date, clock, lapse (spec 3.2, 3.3) ---- */

/* null date = minimum i64; null clock = 0; null lapse = 0 */
static int64_t df_time_now(void) {
#ifdef _WIN32
  FILETIME ft;
  GetSystemTimePreciseAsFileTime(&ft);
  uint64_t t = ((uint64_t)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
  return (int64_t)((t - 116444736000000000ULL) / 10000ULL);
#else
  struct timespec ts;
  clock_gettime(CLOCK_REALTIME, &ts);
  return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
#endif
}

/* monotonic counter in 0.1 ns units, never 0 */
static uint64_t df_time_clock(void) {
  uint64_t value;
#ifdef _WIN32
  static LARGE_INTEGER frequency;
  LARGE_INTEGER counter;
  if (frequency.QuadPart == 0) {
    QueryPerformanceFrequency(&frequency);
  }
  QueryPerformanceCounter(&counter);
  uint64_t f = (uint64_t)frequency.QuadPart;
  uint64_t n = (uint64_t)counter.QuadPart;
  value = (n / f) * 10000000000ULL + (n % f) * 10000000000ULL / f;
#else
  struct timespec ts;
  clock_gettime(CLOCK_MONOTONIC, &ts);
  value = (uint64_t)ts.tv_sec * 10000000000ULL + (uint64_t)ts.tv_nsec * 10ULL;
#endif
  return value ? value : 1;
}

/* ISO 8601, UTC: 2026-10-11T14:03:22.123Z */
static df_string df_str_from_date(int64_t ms) {
  if (ms == INT64_MIN) {
    return df_str_lit("null", 4);
  }
  int64_t days = ms / 86400000;
  int64_t rem = ms % 86400000;
  if (rem < 0) {
    rem += 86400000;
    days--;
  }
  int64_t z = days + 719468;
  int64_t era = (z >= 0 ? z : z - 146096) / 146097;
  int64_t doe = z - era * 146097;
  int64_t yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
  int64_t y = yoe + era * 400;
  int64_t doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
  int64_t mp = (5 * doy + 2) / 153;
  int64_t d = doy - (153 * mp + 2) / 5 + 1;
  int64_t m = mp < 10 ? mp + 3 : mp - 9;
  if (m <= 2) {
    y++;
  }
  char buf[64];
  snprintf(buf, sizeof buf, "%04lld-%02lld-%02lldT%02lld:%02lld:%02lld.%03lldZ",
    (long long)y, (long long)m, (long long)d, (long long)(rem / 3600000),
    (long long)(rem / 60000 % 60), (long long)(rem / 1000 % 60), (long long)(rem % 1000));
  return df_str_from_buf(buf);
}

static df_string df_str_from_clock(uint64_t c) {
  return c == 0 ? df_str_lit("null", 4) : df_str_from_u64(c);
}

/* lapse = value * 8 + code: 2 date lapse (ms), 3 clock lapse (0.1 ns), 4 year lapse, 7 clock overflow */
static int64_t df_date_diff(int64_t a, int64_t b) {
  if (a == INT64_MIN || b == INT64_MIN) {
    return 0;
  }
  const int64_t limit = (int64_t)1 << 60;
  int64_t d;
  if (!__builtin_sub_overflow(a, b, &d)) {
    if (d > -limit && d < limit) {
      return d * 8 + 2;
    }
    return (d / 31556952000LL) * 8 + 4;
  }
  return ((a / 31556952000LL) - (b / 31556952000LL)) * 8 + 4;   /* years, beyond +-36 million */
}

static int64_t df_clock_diff(uint64_t a, uint64_t b) {
  if (a == 0 || b == 0) {
    return 0;
  }
  bool negative = a < b;
  uint64_t d = negative ? b - a : a - b;
  if (d >= ((uint64_t)1 << 60)) {
    return 7;
  }
  int64_t v = negative ? -(int64_t)d : (int64_t)d;
  return v * 8 + 3;
}

static bool df_lapse_bool(int64_t l) {
  return (l >> 3) != 0 || l == 7;
}

static df_string df_str_from_lapse(int64_t l) {
  if (l == 0) {
    return df_str_lit("null", 4);
  }
  if (l == 7) {
    return df_str_lit("overflow", 8);
  }
  return df_str_from_i64(l >> 3);
}

/* a number as a lapse in milliseconds (spec 3.4): an integer is a date lapse (a year lapse beyond +-36 million years) */
static int64_t df_lapse_from_ms(int64_t ms) {
  const int64_t limit = (int64_t)1 << 60;
  if (ms > -limit && ms < limit) {
    return ms * 8 + 2;
  }
  return (ms / 31556952000LL) * 8 + 4;
}

/* a real is a clock lapse (0.1 ns), a date lapse beyond the clock range (rounding away from zero); NaN is null */
static int64_t df_lapse_from_ms_real(double ms) {
  if (isnan(ms)) {
    return 0;
  }
  const double limit = 1152921504606846976.0;   /* 2^60 */
  double units = ms * 1e7;
  if (units > -limit && units < limit) {
    return (int64_t)llround(units) * 8 + 3;
  }
  double whole = ms > 0 ? ceil(ms) : floor(ms);
  if (whole > -limit && whole < limit) {
    return (int64_t)whole * 8 + 2;
  }
  double years = trunc(ms / 31556952000.0);
  return years >= limit ? (int64_t)(limit - 1) * 8 + 4 : years <= -limit ? -(int64_t)(limit - 1) * 8 + 4 : (int64_t)years * 8 + 4;
}

/* Time.sleep (stdlib 3): at least the lapse; zero, negative and null return at once */
static void df_time_sleep(int64_t l) {
  int64_t value = l >> 3;
  int code = (int)(l & 7);
  if (l == 0 || (code != 7 && value <= 0)) {
    return;
  }
  const uint64_t most = (uint64_t)1 << 62;      /* about 146 years */
  uint64_t ns;
  if (code == 2) {
    ns = (uint64_t)value >= most / 1000000 ? most : (uint64_t)value * 1000000;
  } else if (code == 3) {
    ns = ((uint64_t)value + 9) / 10;
  } else {
    ns = most;                                    /* years and the clock overflow */
  }
  fflush(stdout);
#ifdef _WIN32
  /* a high resolution timer when the OS has one (Windows 10 1803), else Sleep, rounded up to the millisecond */
#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif
  HANDLE timer = CreateWaitableTimerExW(NULL, NULL, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
  while (ns > 0) {
    uint64_t part = ns > 3600000000000ULL ? 3600000000000ULL : ns;   /* at most one hour per wait */
    bool waited = false;
    if (timer) {
      LARGE_INTEGER due;
      due.QuadPart = -(LONGLONG)((part + 99) / 100);                   /* relative, in 100 ns units */
      waited = SetWaitableTimer(timer, &due, 0, NULL, NULL, FALSE) && WaitForSingleObject(timer, INFINITE) == WAIT_OBJECT_0;
    }
    if (!waited) {
      Sleep((DWORD)((part + 999999) / 1000000));
    }
    ns -= part;
  }
  if (timer) {
    CloseHandle(timer);
  }
#else
  struct timespec wait = { (time_t)(ns / 1000000000ULL), (long)(ns % 1000000000ULL) };
  struct timespec left;
  while (nanosleep(&wait, &left) != 0 && errno == EINTR) {
    wait = left;
  }
#endif
}

/* ---- random: xoshiro256**, seeded through splitmix64 ---- */

static uint64_t df_rng[4];
static bool df_rng_ready = false;

static uint64_t df_splitmix(uint64_t* state) {
  uint64_t z = (*state += 0x9E3779B97F4A7C15ULL);
  z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
  z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
  return z ^ (z >> 31);
}

static void df_rand_seed(uint64_t seed) {
  for (int i = 0; i < 4; i++) {
    df_rng[i] = df_splitmix(&seed);
  }
  df_rng_ready = true;
}

static uint64_t df_rotl(uint64_t x, int k) {
  return (x << k) | (x >> (64 - k));
}

static uint64_t df_rand_next(void) {
  if (!df_rng_ready) {
    df_rand_seed((uint64_t)df_time_now() ^ df_time_clock());
  }
  uint64_t result = df_rotl(df_rng[1] * 5, 7) * 9;
  uint64_t t = df_rng[1] << 17;
  df_rng[2] ^= df_rng[0];
  df_rng[3] ^= df_rng[1];
  df_rng[1] ^= df_rng[2];
  df_rng[0] ^= df_rng[3];
  df_rng[2] ^= t;
  df_rng[3] = df_rotl(df_rng[3], 45);
  return result;
}

/* both ends included; swapped when min > max; no modulo bias */
static int64_t df_rand_int(int64_t a, int64_t b) {
  if (a > b) {
    int64_t t = a;
    a = b;
    b = t;
  }
  uint64_t range = (uint64_t)b - (uint64_t)a + 1;
  if (range == 0) {
    return (int64_t)df_rand_next();
  }
  uint64_t threshold = (0 - range) % range;
  uint64_t r;
  do {
    r = df_rand_next();
  } while (r < threshold);
  return (int64_t)((uint64_t)a + r % range);
}

/* [0, 1], both included */
static double df_rand_real(void) {
  return (double)(df_rand_next() >> 11) / 9007199254740991.0;
}

/* [min, max) */
static double df_rand_real_range(double a, double b) {
  double u = (double)(df_rand_next() >> 11) * (1.0 / 9007199254740992.0);
  return a + (b - a) * u;
}

/* ---- math helpers: NaN propagates in Min, Max, Clamp ---- */

#define DF_MATH_REAL(T, N, LOG) \
  static T df_min_##N(T a, T b) { if (isnan(a) || isnan(b)) return (T)NAN; return a < b ? a : b; } \
  static T df_max_##N(T a, T b) { if (isnan(a) || isnan(b)) return (T)NAN; return a > b ? a : b; } \
  static T df_sign_##N(T v) { return v > 0 ? (T)1 : (v < 0 ? (T)-1 : (v != v ? v : (T)0)); } \
  static T df_clamp_##N(T v, T lo, T hi) { if (isnan(v)) return v; return v < lo ? lo : (v > hi ? hi : v); } \
  static T df_lerp_##N(T a, T b, T t) { return a + (b - a) * t; } \
  static T df_invlerp_##N(T a, T b, T v) { return (v - a) / (b - a); } \
  static T df_logbase_##N(T x, T b) { return LOG(x) / LOG(b); }

DF_MATH_REAL(double, r64, log)
DF_MATH_REAL(float, r32, logf)

#define DF_MATH_INT(T, N) \
  static T df_min_##N(T a, T b) { return a < b ? a : b; } \
  static T df_max_##N(T a, T b) { return a > b ? a : b; } \
  static T df_clamp_##N(T v, T lo, T hi) { return v < lo ? lo : (v > hi ? hi : v); }

#define DF_MATH_SIGNED(T, N) \
  static T df_abs_##N(T v) { return v < 0 ? (T)(0 - (uint64_t)v) : v; } \
  static T df_sign_##N(T v) { return v > 0 ? (T)1 : (v < 0 ? (T)-1 : (T)0); }

DF_MATH_INT(int8_t, i8)
DF_MATH_INT(int16_t, i16)
DF_MATH_INT(int32_t, i32)
DF_MATH_INT(int64_t, i64)
DF_MATH_INT(uint8_t, u8)
DF_MATH_INT(uint16_t, u16)
DF_MATH_INT(uint32_t, u32)
DF_MATH_INT(uint64_t, u64)
DF_MATH_SIGNED(int8_t, i8)
DF_MATH_SIGNED(int16_t, i16)
DF_MATH_SIGNED(int32_t, i32)
DF_MATH_SIGNED(int64_t, i64)

/* ---- end of runtime ---- */
""";
}
