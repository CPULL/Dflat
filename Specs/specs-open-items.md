# 99. Open items

- Locale details
- Date formatting week rules
- Panic exit codes (all -1000 for now; not before 0.1.0)
- Generics: minimal intermediate form exported by compiled libraries for public generics (11.3.14), after 0.1.0
- Stdlib: strings, files, time, collections extras (queue/stack)
- Traits: define `Comparable<T>` (11.2.1, 6.2.2): `compareTo`, how a class declares it, how the sorted collections and `sort` use it; today the keys of `map` and `set` are ordered by hash only
- Collections: the members of `MapIterator` (the three sub-traits of `Iterator` are decided, 6.7; the index and set iterators are defined, 6.7.1, 6.7.3)
- Collections: how a custom class receives literals (`[...]`, `[k: v]`) (`c[k]` is decided, 6.7; `+=` and `-=` call the members, 6.6)
- Collections: views are read-only; revisit with tasks (6.5), the full Rust borrow model stays an option
- Collections: shared C helpers against one copy per type (code size, 11.3.12); the B-tree (6.8.4.1) and the hash table (6.8.5.1) are decided
- Collections: widening of the stuck buckets of `hmap` / `hset` (6.8.5.1)
- Collections: the other non-silent exceptions of the collection operations, to be formalized during the implementation (out of memory is decided, 6.1, 15.3)
- Reference counting: the order of the locks, waiting and deadlocks with threads; nested locking of the same `&@T`
- Reference counting: generics with counted type arguments (`Stack<@Dog>`), after 0.1.0; the `Rc` / `Arc` split, with threads
- DateTime library: import path, timezone database, parsing for `CalendarDate` / `ZonedDateTime`
- Import paths of the standard library parts (`import stdrnd`)
- Pointers: automatic bound for `*` on `arr` / `vec` / `list`; bound methods on unbounded pointers
- `addr` in `dfoot` (fixed memory maps ok, runtime addresses problematic)
- Safe / unsafe logic
- Function pointers (objects); C function tables (COM vtables, Vulkan dispatch tables)
- C interop: calling conventions, callbacks, returned arrays, UTF-16 strings, `string?` as map keys and values and set items (7.1), ownership across the boundary, unions, bit flags, varargs (the minimal extern fun is in 15.9)
- Deferred: async/await, locks/atomics, tasks, SIMD, EF syntax, Vulkan, shaders
- Borrow locks: borrows across functions and loops are not checked yet (method calls: 13.1.1)
- Pointers: the exact way to convert `*T` to `&*T` in unsafe code, the syntax of the `unsafe` block and what it allows, handling of null pointers in safe code, which cases the compiler can prove valid, pointer equality, `.hash` and `toStr()`, and pointers in generics

---
