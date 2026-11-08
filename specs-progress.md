# Specs progress (Specs0.0.28, compiler 0.0.80alpha, 2026/11/08)

What is left of the design work and what the compiler does not build yet. It replaces `To_discuss_for_collections.txt` and `ownership-management.md`; everything decided there is in the specification (Specs<section> is a section of dflat-spec.md). The point numbers of the collections are kept from the old file.

Tags: TOBEDISCUSSED (open, needed shortly), UNDERDISCUSSION (being discussed), PROBABLY DECIDED (agreed, not yet in the spec), FUTURE (left on purpose for a later version). PROPOSAL: an idea, not confirmed.

# Part 1 - Collections

### NEXT WEEK (from 2026-11-02): custom collections (Specs6.9, marked "partial")

- The members and the cursor types of the map iterator (`MapIterator`); subclassing of the default collections (`vec`, `list`, `map`, `hmap`, `set`, `hset`; `arr` cannot) and the dispatch of the overridden methods; how a custom class receives the literals; `get_view` for custom collections (point 34); lists of pointers and references (with the pointers work).

### 45. [DECIDED 2026-11-07] Code size of the generic maps

Closed: the heavy code is the shared runtime C (df_bt, df_ht); the limits stay as a safety net, per kind of collection (Compiler_Options.md; options built 0.0.78alpha). Was: Specs11.3.12 limits the copies per library collection to 64; each (Key, Value) pair is one copy. The four node layouts of the B-tree (branch and leaf, single-type and multi-type key, Specs6.8.4.1) add to the number of variants of the tree code. See also point 72.

### 66. [DECIDED 2026-11-07, Specs13.4, built 0.0.78alpha] Variadic parameters and existing collections

Specs13.4, Specs99: the position of a variadic parameter and passing an existing collection (`f(...v)`). A `vec` as the variadic argument is the natural case.

### 1 / 50. [TOBEDISCUSSED] The members of MapIterator

Specs6.7, 6.7.2, Specs99: the shape of `MapIterator` and its `next()` (the cursors of `map` and `hmap` are defined in Specs6.8.4.1 and 6.8.5.1; `SetIterator` is decided in Specs6.7.3).

### 79. [DECIDED 2026-11-07, declared 0.0.78alpha; raised when Keyable is built] The name of the Keyable exception: ModifiedObjectUsedAsKeyException, ID 52 (Specs11.2.1, 15.3)

Specs11.2.1, Specs99: the exception raised when the hash of a key changed. (Exception IDs are set at Specs0.1.0.)

### [DECIDED 2026-11-07, Specs6.6, built 0.0.78alpha] clear(): arr and vec keep the size, list / maps / sets remove all the items (forbidden while iterated)

Specs99: `clear()` on maps and sets empties them, while `clear()` on the index collections keeps the size (Specs6.6); to be confirmed.

### Pending check

- The values of `"null".hash` and `"".hash` in Specs6.2.1: the C runtime, the compiler and a Python port agree (64 and 32 bits); a check against the reference code of rapidhash is still to do (running it was blocked).

### [DECIDED 2026-11-08, Specs0.0.27, built 0.0.79alpha except where noted] The spec-99 items settled on 2026-11-08

- Bitfields (Specs3.1.1, 7.1): a call whose result is used gives a new value, a single call alone changes the variable; a chain alone changes nothing; the form alone is a compile error in a read-only place; `Flags?` nullable.
- Properties (Specs11.1.4, 11.2): modifiers in any order, on the property or on one accessor; `readonly` with a `W:` block; `replace` replaces the whole property; a read through a trait type runs the class accessors. Replacing a trait property: built 0.0.82alpha.
- Generics (Specs11.3.6, 11.3.8, 12.4): sets inside a class; a set as declared type argument holds set values (`arr<Num> a = [12, 12.0]`); a set is nullable when a member is; the `->` warning only when every reaching type is basic. Not built: set values (11.3.6.1) and with them `arr<Num>` and the nullability of sets.
- Borrowed strings (Specs4): a field shares the string; a copy with a level 3 warning when the original ends first; collections store shrunk copies. Built 0.0.80alpha.
- `enforce timeout 500 expr` (Specs7.2.6).
- [DECIDED 2026-11-08, Specs0.0.28; the rename built 0.0.81alpha, trait properties built 0.0.82alpha] The keyword `override` is renamed `replace` everywhere; `override` is no longer reserved, no error for the old word. Trait properties (Specs11.2): one without accessor blocks is a simple field in the class (no `replace`); a class can replace a trait property (`field` is the trait's storage, the replacement has the final word); direct memory access unless some class replaces it, otherwise and in compiled libraries a call.
- Standard messages keep "Exception" (Specs15.3); panic exit codes stay -1000 until 0.1.0.

# Part 2 - Ownership and reference counting

The decided rules are in Specs12 and 12.7 (Specs0.0.19). Principle: the Rust model and implementation is the reference; deviations only when decided explicitly.

### Borrows and temporaries (moved from the collections)

- 83. [DECIDED, built 0.0.73alpha: the lock of borrowed items, Specs12, 13.1.1] Lifetime of borrows of items. A borrow of an item taken outside a `for` loop (`(*)i32 p = (&)m[k]`, `f((&)m[k])`, `(&)v[3]`) points into the collection. An insert or delete in a `map` moves the inline values of a leaf (Specs6.8.4.1), and an `add` that reallocates a `vec` moves its items, so the borrow points to the wrong place. Inside `for (&)` it is safe (the counter blocks changes, Specs6.4); `tryGet(k, (&)out)` is safe. PROPOSAL: a borrow of an item increments the counter of the collection, like an iterator, as long as the borrow lives; meanwhile a change raises `ModifyingIteratedCollectionException`. This needs the compiler to track the lifetime of borrows in general. Prerequisite of the writable map values (step 5 of the map build order).
- 85. [DECIDED, built: a temporary ends with its statement] Temporary objects that never end. A new object that is not kept and not returned (a temporary inside an expression) is never ended by the compiler. For a view its counter is never given back, so its collection stays locked for good (`r.Console.log(v[1..3])` then `v.add(5)` raises `ModifyingIteratedCollectionException`; the same with `v[1..3].len` or a view passed to a function). PROPOSAL: a temporary ends at the end of the statement that made it, like the temporary strings, unless it is kept (a variable, a field, an item, a returned value, an argument moved with `->`).
- DECIDED (built: the last use, as Rust): how the end of a local borrow of a plain object is decided. Example: `&Dog view = plainDog`, `show(view)`, then `plainDog is atEnd.` is legal because `view` is not used afterwards.

### Counted objects

- DECIDED (2026-11-07, written in Specs0.0.26, built 0.0.65-0.0.68alpha): all the users of a counted object share its ownership; `->rc` moves one usage (the receiver gains it, `rc` becomes null, the count does not change) (Specs12.7).
- DECIDED (2026-11-07, written in Specs0.0.26, built 0.0.65-0.0.68alpha): `->rc` into a `->T` parameter or a plain receiver (`Dog p = ->rc`) is a compile error that points to `removeRefCounting()` (Specs12.7). `removeRefCounting()` stays the only way out of the counting.
- DECIDED (2026-11-07, written in Specs0.0.26, built 0.0.65-0.0.68alpha): `->pack[i]` on counted collections works as on plain ones (a `list` loses the node, `arr` / `vec` keep null); the usage moves to a counted receiver; a plain receiver is the compile error above (Specs12.7).
- DECIDED (Specs12.7, built): collections of counted items: `pack[0].bark()` takes a temporary usage for the call, `@Dog first = pack[0]` holds a usage until `first` ends while the list keeps its own (no borrows). The loop variable is decided (Specs12.7).
- DECIDED (2026-10-31, written in Specs12.7, built): `has`, `indexOf`, `remove` and `removeAll` of a `list<@T>` / `vec<@T>` take a counted object or null (a plain object is a compile error) and compare the objects by their own rules (`equals`, `==`, or identity, Specs11.1.3); `var old = c.replace(i, v)` is a compile error that asks for `@T old` (the counted `var` of Specs12.7 is decided before the types are known, for calls of functions only).
- DECIDED (Specs12.7, built): fields. An `@Dog` field is a user and releases its usage when its object dies; a `_Dog` field is a weak reference, null once the object is gone; a `&Dog` field is not allowed (a borrow cannot outlive its lender). Written in Specs12.7 as it follows from the decided rules; to confirm.
- DECIDED (2026-11-07, written in Specs0.0.26, built 0.0.65-0.0.68alpha): a plain primary parameter of an object or a collection (`class Ken(Dog d)`; strings and arrays give their field a copy) used only during the construction is a read-only lend, not a field; used as a field it is given implicitly (level 1 warning), and using the argument after the call is a compile error (Specs11.1.1, 19.3).
- DECIDED (Specs12.7, built): an object moves to and from counting only through an explicit operation (`@plain`, `removeRefCounting()`); the old runtime switch `.asRefCount` is removed.
- DECIDED (2026-11-07, written in Specs0.0.26, built 0.0.65-0.0.68alpha): a weak reference needs `%` in the expression (`%Dog w = first` is a compile error) (Specs12.7).
- DECIDED (2026-11-07, written in Specs0.0.26, built 0.0.65-0.0.68alpha): through a read-only `@T` only `unrestricted` and static methods, no field writes; through `&@T` every public method, holding the lock (Specs12.7).
- DECIDED (2026-11-07, written in Specs0.0.26, built 0.0.65-0.0.68alpha): counted value types and strings (`@i32`, `&@i32`, `@string`, `&@string`): a counting block on the heap; `&@T` writes need the lock; a `&@string` is also read only inside `enforce` (statement or expression form), no automatic lock; a new variable or parameter shares the cell; `=` on a `&@T` value writes the shared cell (lock needed), on a read-only `@T` it gives a new cell with a copy that locks automatically and gives the zero value (or null for a nullable target) when the lock fails; reads give no guarantee on the value; strings out of a `&@string` are always compact copies (Specs12.7, 13.1). Built for variables, parameters, fields, results and items (0.0.69alpha); not yet: static counted values, list<&@T> of values.
- DECIDED (2026-11-07, written in Specs0.0.26, built 0.0.65-0.0.68alpha): counted traits: `@Animal a = @Dog()` shares the counter; a type check keeps the marker (`@`, `&@`, `%`) and is a new usage for the branch; a plain `Dog d` from a counted value is a compile error (Specs12.7).
- DECIDED (2026-11-07, written in Specs0.0.26, built 0.0.71alpha): objects in tuples are owned as in structs (Specs3.2, a copy clones them); counted values among several results, each with its own marker (Specs13.1.1). Not yet: &@T among several results.
- DECIDED (2026-11-07, Specs0.0.26, built 0.0.72alpha): maps own object values (Specs6.6; the copies clone), map<K, @T>; tryGet redone as bool, value (Specs6.6, 2026-11-07, built 0.0.73alpha): Dog d read-only, &Dog d read/write, the map locked while lent; use after a certain move is a compile error (Specs12.4; branches and loops do not count, x == null allowed); list<&@T> of values, static counted values, &@T among several results; extern functions refuse counted values. Counted properties: built 0.0.77alpha.
- DECIDED (2026-11-07, Specs0.0.26): `unrestricted` checked (built 0.0.74alpha); the borrow rule on method calls, read-only borrowing locals and `&Dog d` (13.1.1, built 0.0.75alpha); counted properties (Specs11.1.4, built 0.0.77alpha); `&T` results of values, a lend from `&@T` only inside its enforce (13.1.1, built 0.0.76alpha).

### Read/write counted objects and enforce

- DECIDED (2026-10-31, written in Specs7.2): the new `enforce`: checks (null, alive, locks in variable order), clauses `null [on ...]`, `lockfail [on ...]`, `catch E [ex] [on ...]`, `atEnd`, the order of the clauses, the releases at the very end, `return` / `break`, `else` only alone, `default` (the catch-all, 2026-11-01), the expression form (the variables of the expression are enforced, clauses give values, 2026-11-01). Built: steps 1-4 (2026-10-31/11-01: the clauses, the checks, null, catch / atEnd, catch on x, return). Not built: `default`, the expression form, `(@)T` and `lockfail`. Before: today the compiler has only `enforce a, b { } else { }` for `T?` and weak references.

Decided (Specs12.7): `@T` read-only, `(@)T` read/write with a lock in the object; reads never lock; writes need the lock; `enforce x { }` takes it and releases it when the `enforce` ends (Specs7.2.5); writing without it is an exception.
- SUPERSEDED (the markers of Specs0.0.23, 13.1): at the call only the writing forms are marked (`a((@)rc)`, as `b((&)v)`); read-only is written only in the definition (`a(rc)`). No transformation between read-only and read/write: it is decided at creation.
- Decided 2026-11-02 (Specs7.2.1, 7.2.2, 12.7): waiting with a timeout, `@Player() timeout 100` as the default of the object (0 when not written: tried once), `enforce p timeout 500` per variable wins; the lock, the queue of waiters and the default timeout live in the counting block; atomic changes; built 2026-11-02 without threads (see the reference counting point). Open: `timeout` in the expression form, nested locking in method bodies (reentrant lock or lock held for the whole call), the order of the locks and deadlocks with threads (FUTURE). The exception for a write without the lock is `WritingOnRefCountWithoutLockingException`, ID 50.
- Not mapped yet: mutation through several users (Rust `RefCell`, `Mutex`, `RwLock`, `Rc::get_mut`, `Rc::make_mut`).

### FUTURE

- Generics with counted type arguments (`Stack<@MyRC>`, not needed for Specs0.1.0). Design recorded: generics are allowed but not mixed: a generic declares in its header whether its type parameter is plain or counted (`class Cache<T>` with `->T`, the cache owns the objects; `class SharedCache<@T>` with `map<string, @T> items`, `put(string key, @T value)`, `get(string key) @T`), so they are two generics and the arguments must match. New construct proposed: named type sets, `generic NotRCs { i32, string, MyObj }` and `generic SomeRCs { @MyObj, @AnotherObj, @string }`, used as `class Stack<T in NotRCs>` (no refcounts, moves are moves) and `class StackRefs<T in SomeRCs>` (everything counted, assignment adds a usage, weak references and `is done.` legal); a set that mixes plain and counted types is a compile error. Open: the list is closed (the user writes a set for their own classes), the relation with trait constraints (`:`). Considered: no special rule (`push` makes the stack a user, `pop` hands the usage to the caller), but `=` in a generic body means different things for `Dog` and `@Dog`, and errors would surface at the instantiation.
- The `Rc` / `Arc` split (single-thread and thread-safe counting), after threads; and `(@)T` used in parallel (Rust `Arc<Mutex<T>>`).

# Part 3 - Later versions (left on purpose)

- A tree collection (a tree of items with parents and children, for the program; not the B+ tree inside `map` and `set`): its members, iterators and views (Specs6.5 already expects dedicated views for trees). Not designed yet.
- 19. Debug and Release becoming a runtime choice (like Java and C#); today the index check and `ModifyingIteratedCollectionException` are fixed by `--debug` / `--release`, and Release panics with `Panic -1000` (a single panic code, Specs99).
- 63. How a custom class receives the literals `[...]` and `[k: v]` (Specs6.9, Specs99); `c[k]` is decided (`read_pos`, `set_pos`), `+=` and `-=` call the members.
- 64. Subclassing the default collections: the compiler's direct code for `c[i]`, `for` and the hot paths does not call methods; if a class overrides `add`, which one runs? PROPOSAL: a subclass loses the direct paths and goes through the virtual calls. `arr` cannot be subclassed.
- 67. Structs in collections (Specs11.1.5): a `vec<Point>` of structs holds values inline, which changes the ownership and the drop code.
- 68. Lists of pointers and references, after the pointers work (Specs12.1).
- 69. Functional members built on 2026-11-05 (Specs6.6: `filter`, `map<U>`, `reduce`); queue / stack are left.
- 71. Embedded Dflat classes against hand-written C: `vec` and `list` (and the maps) are Dflat classes in a prelude with C hot paths; you asked for hand-written C versions too, to compare. PROPOSAL: finish the surface of the language first, then write the C versions of `vec`, `list`, `hmap`, `hset` and compare on a fixed set of programs.
- 72. Code size: a copy per type or shared helpers working on byte sizes and a comparer (Specs11.3.12). PROPOSAL: copies per type for numbers and small value types; shared helpers for objects when the output size becomes a problem.
- 74. Small architectures (Specs15.7.5, 6.1): collections need a heap; does a small target get only `arr`, or `vec` with a fixed capacity? There `null.hash` is 0 and multi-type keys are not available.
- 75. Collections and threads: shared collections need locks or immutable views; nothing is specified (see the `Rc` / `Arc` split above).
- Widening of the stuck buckets of `hmap` / `hset` (Specs6.8.5.1, Specs99).

# Part 4 - Not built yet (compiler 0.0.44alpha)

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
- 18. A failed allocation ends the program (a panic) instead of raising `OutOfMemoryException` in Debug (Specs6.1, 15.3); a size that does not fit (`n * size` overflowing) ends it the same way since 2026-11-01.
- 34. `c[a..b]!` (clone is built, the copy of a view is not) and `get_view` for custom collections (Specs6.5).
- 35. Cloning (Specs12.6) is built for objects, `vec`, `list` (also of counted items), and since 2026-10-31 `map`, `hmap`, `set`, `hset` (a new collection, then `add(this)`), the plain `add(other)` and `copyFrom` (object items cloned, the items written over end); `tryClone()` since 2026-10-31; not yet: tuple fields with strings or objects, the object items of the set operations (sets hold no objects yet). `fill` of an object stays a compile error (Specs6.6).
- 36. Lambdas and function types (Specs13.3): built in part on 2026-11-05 (Specs13.3.6 lists what is left: counted and weak captures, &a of strings and arrays, modes in function types, function types in fields and collections, `f1!`, the `&a` lock); `sort(lambda)` and `filter` / `map<U>` / `reduce` built on 2026-11-05; still to do: sorting objects and tuples with the default comparer, `filter` of objects and arrays.
- 37. Conversion of collections of related types (Specs11.3.10, 11.3.7), for any generic class.
- 40. Destructuring tuples other than `(k, v)` of a map in the header of a `for`.

### arr, vec, list

- 20, 73. `arr` ownership (Specs6.8.1) is built since 2026-11-01 for local arrays (also `var x = [...]` and results of functions that return a new array), borrows to reading functions and to the standard library, `return a`, `->a` into a field, and array fields (a new array, a move, or a copy of plain items; the class frees them). Also built: a local assigned again with a new array, `->b` or null (the old block ends), `->a` into a `->arr<T>` parameter (it ends with the function unless moved on or returned), static array fields (they end with the program), arrays in collections (`arr`, `vec`, `list` of arrays: owned like fields, removed and replaced arrays end, `takeAt` / `replace` / `pop` hand them out) and in tuples (a typed local, field or static tuple owns a copy of its arrays). Also built: `var` tuples, and several return values (the arrays returned are the caller's own; `arr<T> x, i32 n = f()` owns x). Also built (2026-11-02): tuples inside tuples, arrays received into existing owners (`x, n = f()`), a local assigned a borrowed array (`a = b`, a run-time flag), views used at once (`a[x..y].len`, `for`, an index, a copy, a runtime read), tuple temporaries (`f().values.len`, `g(f())`) and results nobody keeps (`f()` alone). Not yet: map values that are arrays (object values are not built either); collections of tuples that hold arrays (a compile error); the array a local borrows from (`b` in `a = b`, `arr<T> c = b`) is not freed; a view kept in a variable stops its array from being freed; a local made a borrow by `a = b` cannot give its array away (`return a`, `->a`, `v.pop((&)a)`: it is then not freed); a new array given to a borrowing parameter (`f([Dog("a")])`) is not freed.
- 23. The level 1 warning for an element read before it is filled (Specs6.8.1, 19.3): arrays start as zeros.
- 28. The compiler parameters `double-grow-limit` and `further-grow-factor` (fixed at 4mb and 1.5) and the fixed-increment growth option for small targets (Specs6.8.2).
- 30. `arr` and `vec` sort with a stable merge sort (`sort`) and a heap sort (`sortUnstable`), not TimSort and pdqsort (Specs6.6); the class `compareTo` does not exist yet.

### map, hmap, set, hset

- 38, 43. `map`: keys and values that are objects, tuples or wide natives, multi-type keys (Specs11.3.6.1, 6.8.4.1), clock and lapse keys (their null is 0); object values in literals.
- 46, 48. Object keys in `hmap` (step 8 of its build order) and in `map`, after `Keyable`.
- 50. No iterator object for `hmap` (the `for` loop and the views use the cursor directly).
- 52, 55, set step 7. Objects as items of `set` and `hset` and in the set operations (`union` ...) and `add(collection)`: they need clone and `Keyable`; the usual warning until then.
- 53. `m += ->other`. A literal as the argument (`m += [k: v]`, `m.add([k: v])`, `s.add([2, 3])`, `v.add([2, 3])`) is typed by the parameter since 2026-11-01.
- 55. A view or a call result as the collection of `-=`. Cost note: every `set` / `hset` instance brings `vec`, `list` and the other set kind of its item type (the overloads of `add`).
- 78 step 5. Writable map values with `(&)` (needs `for (&)`, point 5, and point 83).
- 79. `Keyable` (Specs11.2.1): needs the chained `executeAfter` (point 9) and the default hash, `compareTo` and `equals` of classes.
- 84. Multi-type keys (Specs11.3.6.1).
- 86. `.hash`: namespaces in the class names (always `Root.`), multi-type values, wide natives, `Exception` values, the `Keyable` check, the hash of a type (`i32.hash`).

### Ownership (Specs12, 12.7, compiler phase 4)

- Reference counting (Specs12.7, phase 4b). Steps 1 to 7 are built (2026-10-30): `list<@T>` and `vec<@T>` (step 7; since 2026-10-31 also `has`, `indexOf`, `remove`, `removeAll`, `replace`, `pop`, `fill`, `transformTo`, `reverse`; not yet `sort`, `resize`, `fillAtCapacity`, views, `getIterator`, `->c[i]` (open, Specs99), `map<K, @T>` (maps hold no objects yet) and `arr<@T>`), `removeRefCounting()` (step 6), weak references (step 5, see below), `@T` fields (body and header), their assignments and release with the holder, the cycle warning (level 1, also for a field of the class's own type), `@T` local variables, `var x = @T()`, `=` between counted variables, the release at the end of the scope, `is done.`, `usersCount`, `weakrefsCount`, `@plain`, the errors for borrowing a counted object, `@this` and `@` on a borrow, `@T` parameters, `f(->rc)`, `@T` results (also `var x = f()`), the errors for a counted argument to a plain, `&`, `(&)` or `->T` parameter. Built 2026-11-02: `(@)T` and `(_)T` (variables, parameters, fields), `@T() timeout`, the lock in `enforce` with `timeout`, `lockfail`, the write check (`WritingOnRefCountWithoutLockingException`); not yet: the queue of waiters and timed waits (no threads: a busy lock fails at once), the check of writes made by methods, `timeout` in the expression form (`(@)T` results and `list<(@)T>` / `vec<(@)T>` built later the same day). Not built: `@T` in traits, overrides and extern functions, `->k.field`, static counted fields and counted properties, a counted result used directly in an expression (`make().bark()`), counted keys (`@T` is not a type argument yet), local `&T` declarations (not parsed), a method call taking a usage, counted variables in multiple assignments and inside `atEnd` blocks. The old `.asRefCount`, `.asSharedRefCount` and `.isRC` were never built.
- Weak references are built on the Specs0.0.19 rules (2026-10-30). The Gfx library (Libraries/Gfx), its Demo and Dha Flatty Bird still use the old weak references on plain objects and `_=`, so they do not compile until they are redone.
- DECIDED (2026-11-01, written in Specs12, built, point 85): a new object that nothing keeps (an argument to a borrowing parameter, a receiver) ends when its statement ends, as the full expressions of C++.
- Phase 4a gaps: objects in tuples are not owned and not destroyed (in arrays: point 20); static fields are not tracked; use after a move is found at run time (`NullPointerException`), not at compile time; no lifetime check for borrows.
- Phase 4c: collections that own their elements fully (the drop of `arr`, point 20), compile-time use-after-move and lifetimes.
