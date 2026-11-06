# DFLAT C INTEROP - BRAINSTORMING NOTES (2026-11-05)
Nothing is final. Based on spec 13.1 (functions) and 4 (strings).

## BASIS
- Markers (13.1): plain = read-only lend, & = writable, -> = give, x! = copy,
  @ share, % weak, * raw pointer, &* writable raw pointer.
- Dflat types: int / uns arch-dependent, i<size> / u<size>, addr = generic address.
- Working mode: brainstorming; observations, not a stream of questions.

## THREE INTEROP CASES
- DD: Dflat calls Dflat - ownership markers already cover it.
- CD: C calls our Dflat library - our library defines buffer ownership, the C side adapts.
- DC: Dflat calls a foreign C library - the foreign library defines it, Dflat adapts.
- Marshalling runs in opposite directions in CD and DC.

## MAPPING IDEAS (C <-> Dflat)
- const char* <-> string (immutable). Writable char* buffer <-> &arr<byte>.
- int* outWidth style out parameters <-> & (writable borrow). In-out is the same &;
  a pointer to a variable that is read then written is written explicitly (&uns destLen).
- Pointer + length: the explicit form (&*byte + uns) is low level but usable.
  Wish: replace it with &arr<byte>; problem: the C length is not always next to the
  buffer (before, elsewhere, via pointer, in the return value, none). Annotations for
  the position were suggested, but "a lot of syntax for nothing" - undecided.
- void** <-> &addr (probable). Functions returning const char* <-> string.
- C callbacks <-> Dflat lambda/function type, written return(params) name.
- C int is i32 (not the Dflat int); C long is platform-dependent.
- Unions = different views over the same struct memory. Bitfields already supported.

## STRINGS
- null and "" are the same string (section 4); plain string stays non-nullable.
- string? is an opt-in nullable string
  string(addr) and string(->addr).
- string(addr) idea: a header with class 12 holding the C
  pointer, read-only, converted to a normal string at the first edit.
- Who owns/frees C memory varies per library: nothing assumed.
- Observation: string(->addr) must free with the allocator it came from (C free);
  memory from sqlite3_free or CoTaskMemFree cannot be handed over.
- A string result for C: toCStr(StringEncoding) already exists (UTF8, UTF16, ...).

## OWNERSHIP / WHO FREES
- C headers and libraries do not express it; it is in the manual at best.
- Wanted: a marker saying who frees. Candidates: [~functionName] just before the item that should be freed and by calling which function. [~] for standard free(addr)
  Parameter: the receiver owns it and must free it. Result: the caller frees it.
- The library's free function is an ordinary function call, not the problem.

## HEADERS AND LIBRARIES
- Standard C libraries (.dll .so .a .lib) contain symbol names only, no signatures
  (debug info aside). The .h is the source of signatures.
- Dflat has no headers: the compiler can write the list of functions itself.
  Options: embedded in the .lib, a .dfh next to it, and a .h for C users (must be a
  separate file, a C compiler cannot read it from the archive). Undecided.
- Hypothesis: one header format (.dfh) for Dflat and foreign libraries.
- Tool: reads a .h and generates a .dfh (name maybe "libh"), only what is possible.
  After annotations are added the tool is not used again except for a new library
  version; no merge mechanism.
- .dfh targets: a shared base, with a target attached only to the items that differ
  (syntax to be defined).

## FOREIGN LIBRARY OPTIONS (all four wanted)
- A: hand-written declarations (the old extern fun is not to be used as is).
- B: a bindgen-like tool, written in Dflat.
- C: bindings packaged as an ordinary Dflat library (a design project, constraining).
- D: dynamic loading by name (a new world).
- A is the foundation; B generates A; C packages A.

## C PREPROCESSOR
- Plan: a minimal in-house preprocessor, with clang for complex cases (clang -E -dD
  keeps macro definitions). Clang output depends on the target platform.

## VULKAN HEADER SCAN (vulkan_core.h, 775 functions)
- 464 functions need struct pointers; 257 need no struct; handles 1049 parameters.
- pAllocator appears 117 times (nearly always NULL); 114 count+pointer pairs.
- Returns: 475 void, 283 VkResult.
- Headers cloned in the workspace under /home/claude/vk-src (not delivered).

## STRUCTS
- Defined set of fields over memory; may have methods, but no state. Classes are for
  full power; structs are structured data. Easy to implement (tuples exist).
- Open: whether a struct may hold a string (owned allocation).
- Structs are the big blocker for Vulkan, Win32 and raylib; not for SQLite's everyday
  API, zlib, libsodium, stb_image, GLFW, libc.
- Next blockers: nullable string, variadic functions, callbacks with captured state.

## EXAMPLE LIBRARIES
- SQLite: needs no structs; options: C lib + .dfh, transpile to a Dflat library, or a
  single sqlite.df source file (the Dflat counterpart of "here is the .c and .h").
  Open: whether transpiling is a goal in itself.
- Others: stb_image, libcurl, GLFW, raylib, libsodium, C standard library.
- Win32 API: an excellent candidate (BOOL is 4 bytes, A/W pairs, stdcall on x86,
  GetLastError, structs, callbacks, import libs; win32metadata describes the API).

## OPEN
- Struct layout and by-value/by-pointer rules; structs holding strings.
- Position of C length parameters; the ownership marker syntax.
- Header format and location (.h / .dfh / embedded), .dfh target syntax.
- Variadic functions; callbacks with state; C calling conventions per function.


## Freeing annotations
fun [~CoTaskMemFree] addr SHGetKnownFolderPath(...)
fun i32 getWindowHandler(i32 size, [~releaseWindowHandler] &addr winHdlrPointer)
fun releaseWindowHandler(addr winHdlr)