# Specs progress (Specs0.0.19, compiler 0.0.41alpha, 2026/10/30)

What is left of the design work and what the compiler does not build yet. It replaces `To_discuss_for_collections.txt` and `ownership-management.md`; everything decided there is in the specification (Specs<section> is a section of dflat-spec.md). The point numbers of the collections are kept from the old file.

Tags: TOBEDISCUSSED (open, needed shortly), UNDERDISCUSSION (being discussed), PROBABLY DECIDED (agreed, not yet in the spec), FUTURE (left on purpose for a later version). PROPOSAL: an idea, not confirmed.

# Part 1 - Collections

### 36. [TOBEDISCUSSED] sort(lambda) and lambdas

Specs6.6: `sort(lambda)` uses a temporary custom sorter; the lambda returns a bool (true is -1) or a number. Lambdas and function types are in Specs13 and are not built, so `sort(lambda)` and every functional member are "feature not supported".
Question: the order of the lambda work against the collections work.
PROPOSAL: lambdas before `sort(lambda)`; the default comparer for objects with the class `compareTo` (Specs6.2.2).
Open gaps of the lambda semantics: what a bool lambda returns for false (0 or 1), and whether a numeric lambda is judged only by its sign.

### 45. [TOBEDISCUSSED] Code size of the generic maps

Specs11.3.12 limits the copies per library collection to 64; each (Key, Value) pair is one copy. The four node layouts of the B-tree (branch and leaf, single-type and multi-type key, Specs6.8.4.1) add to the number of variants of the tree code. See also point 72.

### 66. [TOBEDISCUSSED] Variadic parameters and existing collections

Specs13.4, Specs99: the position of a variadic parameter and passing an existing collection (`f(...v)`). A `vec` as the variadic argument is the natural case.

### 1 / 50. [TOBEDISCUSSED] The members of MapIterator

Specs6.7, 6.7.2, Specs99: the shape of `MapIterator` and its `next()` (the cursors of `map` and `hmap` are defined in Specs6.8.4.1 and 6.8.5.1; `SetIterator` is decided in Specs6.7.3).

### 79. [TOBEDISCUSSED] The name of the Keyable exception

Specs11.2.1, Specs99: the exception raised when the hash of a key changed. (Exception IDs are set at Specs0.1.0.)

### [TOBEDISCUSSED] clear() on maps and sets

Specs99: `clear()` on maps and sets empties them, while `clear()` on the index collections keeps the size (Specs6.6); to be confirmed.

### Pending check

- The values of `"null".hash` and `"".hash` in Specs6.2.1: the C runtime, the compiler and a Python port agree (64 and 32 bits); a check against the reference code of rapidhash is still to do (running it was blocked).

# Part 2 - Ownership and reference counting

The decided rules are in Specs12 and 12.7 (Specs0.0.19). Principle: the Rust model and implementation is the reference; deviations only when decided explicitly.

### Borrows and temporaries (moved from the collections)

- 83. [TOBEDISCUSSED] Lifetime of borrows of items. A borrow of an item taken outside a `for` loop (`(*)i32 p = (&)m[k]`, `f((&)m[k])`, `(&)v[3]`) points into the collection. An insert or delete in a `map` moves the inline values of a leaf (Specs6.8.4.1), and an `add` that reallocates a `vec` moves its items, so the borrow points to the wrong place. Inside `for (&)` it is safe (the counter blocks changes, Specs6.4); `tryGet(k, (&)out)` is safe. PROPOSAL: a borrow of an item increments the counter of the collection, like an iterator, as long as the borrow lives; meanwhile a change raises `ModifyingIteratedCollectionException`. This needs the compiler to track the lifetime of borrows in general. Prerequisite of the writable map values (step 5 of the map build order).
- 85. [TOBEDISCUSSED] Temporary objects that never end. A new object that is not kept and not returned (a temporary inside an expression) is never ended by the compiler. For a view its counter is never given back, so its collection stays locked for good (`r.Console.log(v[1..3])` then `v.add(5)` raises `ModifyingIteratedCollectionException`; the same with `v[1..3].len` or a view passed to a function). PROPOSAL: a temporary ends at the end of the statement that made it, like the temporary strings, unless it is kept (a variable, a field, an item, a returned value, an argument moved with `->`).
- UNDERDISCUSSION (preference: last use, as Rust; the alternative is the end of the scope): how the end of a local borrow of a plain object is decided. Example: `&Dog view = plainDog`, `show(view)`, then `plainDog is atEnd.` is legal because `view` is not used afterwards.

### Counted objects

- UNDERDISCUSSION: what "owns" means in the `->` rule for counted variables (Specs12.7: `->a` is allowed if and only if the code owns `a`; a counted variable holds a usage).
- UNDERDISCUSSION: `a(->rc)` is accepted only by `@T` and `(@)T` parameters; a `->T` parameter receives a plain owned object, so `a(->rc)` into it is a compile error (the receiver always knows from its signature if the object is counted).
- UNDERDISCUSSION (idea, conditional): `->rc` as the way to remove the refcounting: the receiver decides what arrives (`@Dog shared = ->rc` passes the usage, `Dog plain = ->rc` removes the refcounting and is null if `rc` is not the last user), the same for functions (`->T` parameter) and for lists (`->pack[0]`). `removeRefCounting()` could then go away. Open: whether `rc` is lost or stays valid when it fails (`removeRefCounting()` leaves it valid), and a `->T` parameter possibly receiving null.
- UNDERDISCUSSION (preference, "not sure yet" on the result): `->pack[0]` on a `list<@Dog>` works like `removeRefCounting()`: if the list is the only user you get the whole object and the list loses the item; otherwise null with a silent exception. Open: the result is a plain `Dog` or a moved `@Dog` usage.
- UNDERDISCUSSION: collections of counted items: `pack[0].bark()` takes a temporary usage for the call, `@Dog first = pack[0]` holds a usage until `first` ends while the list keeps its own (no borrows). The loop variable is decided (Specs12.7).
- UNDERDISCUSSION (first reaction "seems right"): fields. An `@Dog` field is a user and releases its usage when its object dies; a `_Dog` field is a weak reference, null once the object is gone; a `&Dog` field is not allowed (a borrow cannot outlive its lender). Written in Specs12.7 as it follows from the decided rules; to confirm.
- UNDERDISCUSSION: a `&` parameter in a primary constructor header cannot be used with refcounts: it is not known where the borrow ends.
- UNDERDISCUSSION: an object moves to and from counting only through an explicit operation (`@plain`, `removeRefCounting()`); the old runtime switch `.asRefCount` is removed.
- Open: whether a weak reference can be made without the marker (`_Dog w = first`, with a counted `first`): Specs12.7 makes it with `_` in the expression, as `@plain`.
- Open: which methods can be called on a read-only `@T` (all the methods, or only those that do not write, as `unrestricted` through `&`, Specs12.5).
- Open: `ChangingRefCountTypeException` (Specs15.3) has no use since there is one kind of counting.
- PROBABLY DECIDED (Java style): counted natives and strings (`@i32`, `(@)i32`, `@string`, `(@)string`) are valid: a native is a memory portion that stores the value and carries the refcount, like any other counted object (same counting block). This also makes `@X` valid in generic code whatever `X` is. Ownership, weak-reference, clone and locking rules are identical to class instances. `@i32 a = @10` and `@i32 b = a` are two users of the same counted value; `@i32 c = a!` is a new counted object with the same value.
- UNDERDISCUSSION: counted traits (`@any Shape`): the count is on the object, `other = shape` adds a user. A downcast (`if sh is Bl b { }`) gives a counted reference of the same kind as the main variable and adds a user: `sh` and `b` are both valid inside the block, `b` is released at the end of its scope.

### Read/write counted objects and enforce

Decided (Specs12.7): `@T` read-only, `(@)T` read/write with a lock in the object; reads never lock; writes need the lock; `enforce x { }` takes and releases it; writing without it is an exception.
- UNDERDISCUSSION: at the call only the writing forms are marked (`a((@)rc)`, as `b((&)v)`); read-only is written only in the definition (`a(rc)`). No transformation between read-only and read/write: it is decided at creation.
- UNDERDISCUSSION: an expression form `i32 val = enforce myRcRW.callFun()` holding the lock for that call only (`enforce` is only a statement in Specs7.2). Open: the result when the lock or the object is unavailable.
- Open: the meaning of the else branch for a lock (busy lock, or null/gone), waiting or try-lock, the lock order for `enforce a, b`, holding the lock over several statements (check then write), nested locking in method bodies (reentrant lock or lock held for the whole call), deadlock. The exception for a write without the lock is `WritingOnRefCountWithoutLockingException` (Specs12.7, 2026-10-30).
- UNDERDISCUSSION: weak references and downcasts in `enforce`: `enforce w is Blah b { b.something() }` checks in one statement that `w` is alive and is a `Blah`; `b` is a temporary user for the block; the else branch covers both failures. Preference: the reason is carried by a silent exception (Specs15.4). The plain `else { }` ignores the reason.
- UNDERDISCUSSION: `enforce` with several failure cases. Proposals: `enforce a, b { ... } catch silent1 ex on a { } silent2 ex on b { }` with a `null on b { }` clause; and (2026-10-30, with `(@)RC a`, `@RC b`, `_WR c`) `enforce a, b, c { ... } null on c { ... } lock failed on a { ... } exception ex on b { ... }`: the failures are a null object, a lock failure and silent or regular exceptions. Open: several failing variables (first failure only, aggregate exception, one exception per variable, or one `enforce` per variable), whether both handlers run, `else` together with them.
- Not mapped yet: mutation through several users (Rust `RefCell`, `Mutex`, `RwLock`, `Rc::get_mut`, `Rc::make_mut`).

### FUTURE

- Generics with counted type arguments (`Stack<@MyRC>`, not needed for Specs0.1.0). Design recorded: generics are allowed but not mixed: a generic declares in its header whether its type parameter is plain or counted (`class Cache<T>` with `->T`, the cache owns the objects; `class SharedCache<@T>` with `map<string, @T> items`, `put(string key, @T value)`, `get(string key) @T`), so they are two generics and the arguments must match. New construct proposed: named type sets, `generic NotRCs { i32, string, MyObj }` and `generic SomeRCs { @MyObj, @AnotherObj, @string }`, used as `class Stack<T in NotRCs>` (no refcounts, moves are moves) and `class StackRefs<T in SomeRCs>` (everything counted, assignment adds a usage, weak references and `is done.` legal); a set that mixes plain and counted types is a compile error. Open: the list is closed (the user writes a set for their own classes), the relation with trait constraints (`:`). Considered: no special rule (`push` makes the stack a user, `pop` hands the usage to the caller), but `=` in a generic body means different things for `Dog` and `@Dog`, and errors would surface at the instantiation.
- The `Rc` / `Arc` split (single-thread and thread-safe counting), after threads; and `(@)T` used in parallel (Rust `Arc<Mutex<T>>`).
- The capture forms of lambdas (Specs13.3: borrow, copy, move, weak) have no counted form; the weak capture `(x; _limit)` of a plain variable does not match "weak references exist only on counted objects".

# Part 3 - Later versions (left on purpose)

- 19. Debug and Release becoming a runtime choice (like Java and C#); today the index check and `ModifyingIteratedCollectionException` are fixed by `--debug` / `--release`, and Release panics with `Panic -1000` (a single panic code, Specs99).
- 63. How a custom class receives the literals `[...]` and `[k: v]` (Specs6.9, Specs99); `c[k]` is decided (`read_pos`, `set_pos`), `+=` and `-=` call the members.
- 64. Subclassing the default collections: the compiler's direct code for `c[i]`, `for` and the hot paths does not call methods; if a class overrides `add`, which one runs? PROPOSAL: a subclass loses the direct paths and goes through the virtual calls. `arr` cannot be subclassed.
- 67. Structs in collections (Specs11.1.5): a `vec<Point>` of structs holds values inline, which changes the ownership and the drop code.
- 68. Lists of pointers and references, after the pointers work (Specs12.1).
- 69. Functional members (`filter`, `map`, `reduce`) and queue / stack; they need lambdas (point 36).
- 71. Embedded Dflat classes against hand-written C: `vec` and `list` (and the maps) are Dflat classes in a prelude with C hot paths; you asked for hand-written C versions too, to compare. PROPOSAL: finish the surface of the language first, then write the C versions of `vec`, `list`, `hmap`, `hset` and compare on a fixed set of programs.
- 72. Code size: a copy per type or shared helpers working on byte sizes and a comparer (Specs11.3.12). PROPOSAL: copies per type for numbers and small value types; shared helpers for objects when the output size becomes a problem.
- 74. Small architectures (Specs15.7.5, 6.1): collections need a heap; does a small target get only `arr`, or `vec` with a fixed capacity? There `null.hash` is 0 and multi-type keys are not available.
- 75. Collections and threads: shared collections need locks or immutable views; nothing is specified (see the `Rc` / `Arc` split above).
- Widening of the stuck buckets of `hmap` / `hset` (Specs6.8.5.1, Specs99).

# Part 4 - Not built yet (compiler 0.0.41alpha)

What the specification decides and the compiler does not build. The point numbers are the old collection points.

### Traits and iterators

- 1, 56. `MapIterator` (members open) and `SetIterator<Item>` (Specs6.7.3: `next()` returns `(Item value, bool isValid)`) do not exist; `Iterator` is a marker trait and `IndexIterator<Item>` has `next()`.
- 2, 65. The traits `MapCollection` and `SetCollection` (Specs6.7) do not exist, so a custom class cannot be a map or a set; only `IndexCollection<Item>` with `getIterator()` is built.
- 4, 32. The iterator of `vec` and `list` is a child object of its collection; the kind of pointer it holds waits for pointers.
- 5. `writableNext()` and `for (&)Item x in c` (Specs3.2, 6.4, 6.7): a borrow inside a tuple is not a type the compiler has.
- 9. The `executeAfter` chain has one link only (a sub-trait cannot redeclare the method); the changing methods (`add`, `remove`, ...) are not trait methods, so they carry no iterator check in custom collections (Specs11.2, 6.9). Needed by `Keyable`.
- 11. The non-public methods `get_view` and `read_pos` for custom collections (Specs6.7); only `get_len()` is built.

### Rules shared by the collections

- 6, 7. The counter of open iterators and views and the change checks on `arr`: an `arr` has no header yet (Specs6.4, 6.6).
- 8. The compile-time proof of a change inside a loop (Specs6.4): only the runtime check is built.
- 18. A failed allocation ends the program (a panic) instead of raising `OutOfMemoryException` in Debug (Specs6.1, 15.3); a huge `vec<T>[n]`, `arr<T>[n]` or `fill(count, value)` is not checked for overflow of `n * size`.
- 34. `c[a..b]!` (clone is built, the copy of a view is not) and `get_view` for custom collections (Specs6.5).
- 35. Cloning (Specs12.6) is built since 2026-10-30 for objects, `vec` and `list` (also of counted items); not yet: `tryClone()` (`ObjectNotClonableException` has no ID), maps and sets, tuple fields with strings or objects. The collection members that need a clone of each item (`fill` of an object, the plain `add(other)`, `copyFrom`, the object items of the set operations) still give their old errors: they can use it now.
- 36. Lambdas and function types (Specs13.3), so `sort(lambda)` and the functional members; sorting objects and tuples with the default comparer.
- 37. Conversion of collections of related types (Specs11.3.10, 11.3.7), for any generic class.
- 40. Destructuring tuples other than `(k, v)` of a map in the header of a `for`.

### arr, vec, list

- 20, 73. `arr` is a class with ownership (Specs6.1, 6.8.1, 12): today it is a value `{pointer, length}` copied freely (`b = a` shares the block); its block is never freed and its items are not dropped when it ends (only replaced items end: `a[i] = x`, `fill(null)`, `swap`), so arrays in objects or variables, and their strings, leak until the program ends.
- 23. The level 1 warning for an element read before it is filled (Specs6.8.1, 19.3): arrays start as zeros.
- 24, 25. With objects: the plain `add(other)` and `copyFrom` (Specs6.6) can now use clone, not wired yet; `add(->other)` is built.
- 28. The compiler parameters `double-grow-limit` and `further-grow-factor` (fixed at 4mb and 1.5) and the fixed-increment growth option for small targets (Specs6.8.2).
- 30. `arr` and `vec` sort with a stable merge sort (`sort`) and a heap sort (`sortUnstable`), not TimSort and pdqsort (Specs6.6); the class `compareTo` does not exist yet.

### map, hmap, set, hset

- 38, 43. `map`: keys and values that are objects, tuples or wide natives, multi-type keys (Specs11.3.6.1, 6.8.4.1), clock and lapse keys (their null is 0); object values in literals.
- 46, 48. Object keys in `hmap` (step 8 of its build order) and in `map`, after `Keyable`.
- 50. No iterator object for `hmap` (the `for` loop and the views use the cursor directly).
- 52, 55, set step 7. Objects as items of `set` and `hset` and in the set operations (`union` ...) and `add(collection)`: they need clone and `Keyable`; the usual warning until then.
- 53. `m += ->other`, and a map literal as the argument (`m += [k: v]`, `m.add([k: v])`): the overload resolution does not type a literal.
- 55. A view or a call result as the collection of `-=`. Cost note: every `set` / `hset` instance brings `vec`, `list` and the other set kind of its item type (the overloads of `add`).
- 78 step 5. Writable map values with `(&)` (needs `for (&)`, point 5, and point 83).
- 79. `Keyable` (Specs11.2.1): needs the chained `executeAfter` (point 9) and the default hash, `compareTo` and `equals` of classes.
- 84. Multi-type keys (Specs11.3.6.1).
- 86. `.hash`: namespaces in the class names (always `Root.`), multi-type values, wide natives, `Exception` values, the `Keyable` check, the hash of a type (`i32.hash`).

### Ownership (Specs12, 12.7, compiler phase 4)

- Reference counting (Specs12.7, phase 4b). Steps 1 to 7 are built (2026-10-30): `list<@T>` and `vec<@T>` (step 7; the members that give or compare items, `map<K, @T>` and `arr<@T>` are not yet), `removeRefCounting()` (step 6), weak references (step 5, see below), `@T` fields (body and header), their assignments and release with the holder, the cycle warning (level 1, also for a field of the class's own type), `@T` local variables, `var x = @T()`, `=` between counted variables, the release at the end of the scope, `is done.`, `usersCount`, `weakrefsCount`, `@plain`, the errors for borrowing a counted object, `@this` and `@` on a borrow, `@T` parameters, `f(->rc)`, `@T` results (also `var x = f()`), the errors for a counted argument to a plain, `&`, `(&)` or `->T` parameter. Not built: `(@)T` and its lock with `enforce`, `(@)T` parameters, `@T` in traits, overrides and extern functions, `->k.field`, static counted fields and counted properties, a counted result used directly in an expression (`make().bark()`), counted keys (`@T` is not a type argument yet), local `&T` declarations (not parsed), a method call taking a usage, counted variables in multiple assignments and inside `atEnd` blocks. The old `.asRefCount`, `.asSharedRefCount` and `.isRC` were never built.
- Weak references are built on the Specs0.0.19 rules (2026-10-30). The Gfx library (Libraries/Gfx), its Demo and Dha Flatty Bird still use the old weak references on plain objects and `_=`, so they do not compile until they are redone.
- `x is atEnd.` on a weak variable is a compile error (Specs12.4); the compiler gives a different error for it today (the counted case is built).
- Phase 4a gaps: temporaries inside an expression are never destroyed (point 85); objects in arrays and tuples are not owned and not destroyed; static fields are not tracked; constructor `atEnd` fragments (need secondary constructors); `atEnd` after other statements; use after a move is found at run time (`NullPointerException`), not at compile time; no lifetime check for borrows.
- Phase 4c: collections that own their elements fully (the drop of `arr`, point 20), compile-time use-after-move and lifetimes.
