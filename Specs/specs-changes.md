# Changes

## Changes in 0.0.3

- 2.1 Labels: allowed characters, at least one letter
- 2.2 Directives: `#region` / `#endregion` (column 1, no `#` in text, nesting, unmatched = level 3 warning)
- 2.3 Unsuffixed literals take native size, widening to fit; reals fit by range; `r16` default without FPU
- 3.1 Single native-size compiler option for literals and `int` / `uns` / `real`, above or below the CPU size
- 4 `var` type inference
- 6 Real → integer cast truncates toward zero
- 8.1 Jump loop between cases: level 1 warning
- 12.1 Collection literals default to native size
- 14.2 Scientific format `e` (new)
- 16 `impl` / `dyn` renamed `specific` / `any`; traits only in the class header; upward type check level 2 warning
- 17 Generics (new): functions, `&&` constraints, `where`, generic classes and traits, value parameters, type arithmetic, `placeholder`, inference and lookahead
- 19.1 Warning levels table (new; 1 = most important)
- Renumbered: 2.1–2.4, 14.2–14.4, 17–20
- 3.4 `dfoot` reference fixed (12.5 → 12.3)
- 2.3 Hex/binary literal size for any digit count; literals transmuted to pointers zero-extended
- 3.2 `addr` type (new)
- 6 Pointer conversions: transmute only; pointer → `addr` with `=`
- 7 `&x` is address-of; `(&)x` writable; `&x` no longer marks by-reference calls
- 9.1 Parameter passing table rewritten: definition declares the mode, call marks only `(&)`, `->`, `!`
- 15.1 Pointers (new): types, bounds, index usage, address-of, fat pointer model, methods
- 15.2 Volatile (new; now 15.3)
- 20 Open items: pointers details, safe/unsafe, function pointers, C interop
- 2.3 Native real format decided by the target architecture (examples: `r64` 64-bit, `r32` 32-bit, `r16` 8/16-bit); replaces the no-FPU rule
- 3.1 Integers wider than 4 bytes may be unavailable on very small targets; `int` / `uns` / `real` description
- 3.2 Null `date` wording: minimum `i64` value; `addr` null is 0
- 5 `string?` is a compile error; strings cannot be enforced
- 3.2 / 15.1 `addr` as generic pointer base: under review
---

## Changes in 0.0.4

- 3.2 `addr` is a native type, cannot be dereferenced; "under review" removed
- 15.1 `addr` → pointer via transmute, always unsafe
- 15.1 Pointer implementation decided by the compiler
- 15.1 `GetAddress()` returns the cursor as `addr`
- 15.1 `IsAligned()` / `IsAligned(bytes)` return `bool`
- 15.2 Alignment (new): `r.Alignment.Type<T>()`, `Arch`, `Cache`, `IsAligned(p)`, `IsAligned<T>(p)`, `IsAlignedCache(p)`; cache alignment compiler option, default 64
- 15.3 Volatile (renumbered from 15.2)
- 20 Open items: `addr` in `dfoot`
- 10 Errors rewritten:
  - 10.1 catch order, `catch Type` without variable, catch by trait, `catch all`, code continues after a catch, `throw` inside a catch, errors inside a catch → `Panic`
  - 10.2 `Exception` class members, constructors (message first, optional), C-style truncation, message text
  - 10.3 Standard exceptions with IDs (0–9 reserved); new `OutOfRangeCastException`, `InvalidCharacterCodePointException`
  - 10.4 Silent exceptions (new): `SilentException` marker trait, fallbacks, specific catch required
  - 10.5 Panic and exit codes: -1000 placeholder
  - 10.6 Targets without exceptions (new)
  - 10.7 Implementation: silent flag in the descriptor
  - 10.8 Scope `atEnd`: compile-time, automatic on every exit, not callable, loop, return value, control flow, exceptions passing through, order
  - 10.9 Class `atEnd` (new): private cleanup, Rust `Drop` rules, order, errors after the result
- 6 / 7 / 12.2 Out-of-range cast, `[char]` transmute, overflow and missing key are silent exceptions
- 15 Freeing runs the class `atEnd`
- 20 Open items: panic codes, missing-key exception name, "Exception" in messages, class `atEnd` with inheritance
---

## Changes in 0.0.5

- 2 `...` is the variadic parameter prefix (was reserved)
- 3.3 `date − clock` → date lapse
- 4 Definite assignment: a read before assignment reads the zero value (null for objects), level 1 warning
- 4 No shadowing of locals: compile error
- 6 Implicit conversions: also unsigned → wider signed (`u16 → i32`); any scalar type → bool; equivalent to a cast
- 6 `[bool]` transmute: 0, null, `""`, NaN, `'\0'` are false, anything else true; collections and tuples not convertible
- 8 Ranges with start > end count down (`10..=1`)
- 8 The `for` variable always has a type; untyped `for x in` is a compile error
- 8.1 Case values can be ranges (`400..=499`, `..-1`, `1..`), also in switch expressions
- 8.1 Case body can continue on the following lines up to the next case
- 9.1 Positional arguments stay in order; named arguments can go in any position
- 9.2 Overloading by number, order or types of parameters; the return type alone doesn't count
- 9.2 An ambiguous call is a compile error, also when caused by default values
- 9.4 Variadic parameters (new): `...T name`
- 10.1 `catch { }` = `catch all { }`, level 2 warning
- 10.2 `Function` format example: `i32 divide(i32, i32)`
- 10.5 Panic output: `Panic <code>` on stdout; unhandled exception prints its message first
- 10.8 `atEnd` sees every variable of its scope; an unreached declaration holds its zero value (null for objects)
- 13 A null string prints as nothing; null values of other types print `null`
- 19.1 Warnings: variable read before assignment (L1), `catch { }` (L2)
- 20 Open items: variadic parameter details
- 2 `as` is a reserved word (used in `alias`)
- 3.4 Tuple items by index: literal, `const` or enum only; out of range is a compile error; indexes apply left to right
- 4 Tuple destructuring, partial with `_` (removed from open items)
- 4.1 / 18 Alias syntax: `alias <original> as <new>`
- 7 String vs char comparisons; char vs integer comparison by code point (level 2 warning); `+` treats a char as text
- 8 `break` to a label uses the label syntax: `break #Found#`
- 18 Typos: "prints a newline", `Console.GetChar()`
- 19.1 Warning: char compared with an integer (L2)
- 4.1 added the full definition for aliases that can also go to sub members
- 2 / 9.4 / 18 Variadic parameters are written `T name...` (was `...T name`)
- 18 Standard library implemented: Console (`Read`, `ReadLine`), Time (`Now`, `Clock`), Math and Math32, Random
- 18 Math: `Min`, `Max`, `Clamp` propagate NaN; `Round` halves away from zero; integer versions of `Abs`, `Sign`, `Min`, `Max`, `Clamp`
- 18 Random: fixed algorithm, default seed from the time, `Int` swaps `min` and `max` if reversed; `Shuffle` waits for collections
- 18 Time: `date` prints as ISO 8601 UTC; `date`, `clock` and `lapse` with null, comparison and subtraction
- 4.1 `alias Root as r` is the form everywhere (the parser now reads it, with dotted names)
---

## Changes in 0.0.6

- 3.4 / 12.1 `arr<T>` (new): array, fixed size set at initialization (compile-time or runtime), not part of the type; replaces `vec<N, T>`
- 3.4 / 12.1 `vec<T>` is now the growable vector (Rust `Vec` rules); `vec<T>[n]` is empty with capacity `n`
- 12.1 Literals are arrays unless declared otherwise; size on either side of the declaration, mismatch is a compile error
- 12.1 Arrays are not initialized automatically; `Fill(value)`; reading before fill or assignment is a level 1 warning; arrays of objects start as null
- 5 Nullability: basic native types need `type?`; other native types, pointers and objects are nullable by default
- 6 Implicit conversion to `string` when the target is a `string`; null `string` → `""`, other nulls → `"null"`
- 9.2 Conversion to `string` always ranks last in overloading
- 13 `Split` returns `arr<string>`; `ToCStr` returns `arr<byte>`
- 15.2 / 17.3 / 18 / 20 `vec<N, T>` references updated to `arr`
- 12.2 `indexOf` on arrays, vectors, lists; `remove` / `removeAt` not on arrays (vectors are now growable)
- 19.1 Warning: array element read before fill or assignment (L1)
- Changelog: 0.0.5 updates in order
- 2 Naming rules: types PascalCase (L2 warning), methods camelCase or PascalCase, `const` UPPER_SNAKE_CASE (L3 warning), stdlib constants UPPERCASE or PascalCase, no imposed case for variables, parameters, fields, properties, enum entries; generic value parameters are not constants
- 2 `Root` is a global static class (stdlib access), always written `Root`
- 4 Example `const i32 LIMIT`
- 4.1 Namespaces case-insensitive, Unicode allowed in code; folder names ASCII letters, digits and `_` only (others ignored, L3); folders differing only by case are a compile error
- 10.3 / 10.7 / 10.9 / 16 / 17 Lowercase `root` fixed to `Root` (`Root.Class`, `Root.Exception`)
- 19.1 Warnings: type name not PascalCase (L2), `const` not UPPER_SNAKE_CASE (L3), folder ignored (L3)
- 2 / 16 `this`: reserved, optional, usable as an argument; `->this` is a compile error
- 3.2 `date` is the UTC instant; new native type `time` (time of day, ms); `time` is nullable like the other non-basic native types (5)
- 3.4 / 12.2.1 Sets (new): `set` (alias `bset`, B-tree) and `hset` (hash); members, new-set operations, in-place operators, subset tests, iteration order; `map` alias `bmap`
- 4.1 `import` takes a full namespace path
- 6.1 Parsing text (new): `Parse` / `TryParse` for integers, reals, `dec`; lenient rules without a format, strict format with alternatives; date and time default formats; no zone means UTC
- 13 `text[pos]` reads a character; large strings behave like a StringBuilder
- 18 `CalendarDate` and `ZonedDateTime` as classes in an optional DateTime library; no local date-time type
- 20 Open items: writing by index into a string, DateTime library details, standard library import paths
- 3.2 `time` stored as an `i32` (ms since midnight); null is `-1`
- 6 Cast to `time`: out of range gives null, `OutOfRangeCastException` if a specific catch exists (silent)
- 9.5 Entry point (new): top-level code or `main`, not both; `main` top-level function; `int` or void return; parameters none, `arr<string> args` or `string args...`; more than one `main`, or `main` in a library, is a compile error
- 18 Program (new): `Root.GetArgs()` returns `arr<string>`, program name at index 0, empty array when there is none; same object as the `args` of `main`
- 18 Chars (new): `IsLetter`, `IsDigit`, `IsLetterOrDigit`, `IsWhiteSpace`, `IsUpper`, `IsLower`, `IsHexDigit`, `IsPunctuation`, `IsSymbol`, `IsControl`, `IsAscii`, `ToUpper`, `ToLower`; Unicode tests; case conversion culture-invariant for now

---

## Changes in 0.0.7

- 1.1 Naming rules: methods are camelCase (recommended, not enforced; warning only if the same function name is defined with different capitalizations); static properties are UPPERCASE (`Math.PI`); existing standard library method names are not converted yet
- 2.4 Collection rows moved to section 6; `dfoot` is section 5
- 10.3 `for`: `var` accepted as the type of the loop variable; tuple forms over maps; reference to 6.4
- 15.3 New exceptions: `IndexOutOfRangeException` (19), `ModifyingProtectedSourceException` (20), `NaNUsedAsKeyException` (21, silent), `InvalidMapKeyException` (22, silent), `EmptyVectorAccessException` (23, silent), `InvalidDateTimeFormatException` (24, silent); the missing-map-key exception now has a name
- 6 Collections rewritten (replaces the old collections section and the collection rows of 2.4; all previous content kept):
  - 6.1 General (new): the seven collections, objects and nullable, identity `==`, copy with `!`, index access, hash against B-tree
  - 6.2 Keys (new): `==` + hash for hashed collections, ordering for sorted ones, `.hash` / `.order` on all types, overridable `equals` and hash on classes, default class hash, orderings of built-ins, `bool` and real keys, NaN keys
  - 6.3 Literals: right side of an assignment, targets of `[...]` and `{ ... }`, duplicates are compile errors, nested literals need the full type, capacity form only on `vec`, `hmap`, `hset`
  - 6.4 Iteration (new): default iterators, maps (tuples, `.keys`, `.values`), ranges as virtual collections, no modification while iterating
  - 6.5 Views (new): `c[a..b]` read-only view, `(&)c[a..b]` writable, `c[a..b]!` copy; only on `arr`, `vec`, `list`; no key ranges
  - 6.6 Members: full matrix of members against collections; `add(k, v)`, merge, `insert`, `pop`, `removeAll`, `keyOf`, `tryGet` with `(&)`, `clear`, `fill`, `fillAtCapacity`, `resize`, `truncate`, `.capacity`, `reserve`, `shrinkToFit`, `sort`, `sortUnstable`, `reverse`, `SortDirection`, `copyFrom`, `indexOf` returns the maximum `u64` when not found
  - 6.7 `arr`: no built-in multi-dimensional arrays, `copyFrom`, `clear` resets to defaults
  - 6.8 `vec` and 6.9 `list` (new sections); `list` has `insert` but no capacity members
  - 6.10 Maps (new section): `map` / `hmap`, missing key, capacity members on `hmap` only
  - 6.11 Sets: operations only between sets of the same kind, capacity members on `hset` only
  - 5 `dfoot`: keys looser, meant for human-readable maps
- 6.6 `Fill` renamed `fill` (camelCase rule)
- 3.3.1 `date` members and functions (new): read-only `year`, `month`, `day`, `hour`, `minute`, `second`, `millisecond`, `dayOfWeek`, `dayOfYear`, `timeOfDay`, `unixMilliseconds`; `DayOfWeek` enum (`Monday` to `Sunday`)
- 3.3.1 `date` constructors: `date(string)` (default formats; input with a zone is converted to UTC, no zone is UTC) and `date(i64)` (milliseconds since 1970-01-01 UTC); an invalid string gives null and a silent `InvalidDateTimeFormatException`
- 3.3.1 `date` functions: `date.fromUnixTime(i64)`, `date.parse(string[, format])` (forwards to the date formatting rules), `addYears`, `addMonths`, `addDays`, `addHours`, `addMinutes`, `addSeconds`, `addMilliseconds`, `startOfDay()`; `date - date` gives a `lapse`
- 3.3.2 `time` members and functions (new): read-only `hour`, `minute`, `second`, `millisecond`, `totalMilliseconds`; constructors `time(string)` and `time(i32)`; an invalid string gives null and a silent `InvalidDateTimeFormatException`; an out-of-range `i32` gives null and a silent `OutOfRangeCastException`
- 3.3.2 `time` functions: `time.parse(string[, format])`, `addHours`, `addMinutes`, `addSeconds`, `addMilliseconds` (wrap around midnight, no exception); `time - time` gives a `lapse`; `date + time` and `date - time` give a `date`
- 3.3 `toStr()` / `toStr(format)` is the existing conversion for `date` and `time` (no `toString`); no `compareTo` (operators and `.order` are used); "UTF based" fixed to "UTC based"
- 7.1 Nulls: `time` added to the nullable-by-default list; tuples are not nullable (the statement that tuples need `type?` is removed from the 0.0.6 changelog)
- 8.4 Formatting dates is now "Dates and times": a `time` uses only `h H m s f F t k`; date-only letters on a `time` are a compile error
- 9 Parsing: `Parse` / `TryParse` are for integers, reals, `dec`, dates and times
- 2.5.1 Operator table: added ranges and `??` (between `||` and the switch expression), `?.`, `x!`, `->`, `&`, `(&)`, and the assignment forms `|=`, `??=`, `_=`; existing priorities unchanged
- 17.4 Math constants renamed `PI`, `PI_HALF`, `E`, `SQRT2`, `R2D`, `D2R`; the class example uses `Math.PI`
- 17.5 `Shuffle`: "needs collections: TODO"
- 17 `list.Sort()` is now `list.sort()`; `d.year` and `d.addDays(3)` in the library introduction
- Numbering: 3.4 Date and Time is now 3.3, 3.5 `lapse` is 3.4, 3.6 Conversions is 3.5; the gap at 7 is closed: Nulls 7, Formatting 8, Parsing 9, Programming statements 10, Classes and traits 11, Memory and ownership 12, Functions 13, Program cycle 14, Errors 15, Namespaces 16, Standard library 17, RESERVED 18, Compiler 19; the duplicate 11.5 is now 10.5 (Switch) and 10.6 (Switch expression); 99 Open items unchanged; Collections stay 6; all references and the warnings table (19.3) updated
- Heading levels fixed for Nulls, Scientific format and Class `atEnd`
- Typos fixed: "classes", "overridden", "Tuples", "guarantee", "Imports"
- 19.3 Warning for arrays read before fill now points to 6.7
- 99 Open items: `.sizeof` for all types, `filter` / `map` / `reduce`, order of enum keys, `tryGet` return value; removed the missing-map-key exception name and the collections extras that are now decided
- Changelog: the updates of each version are collapsed into a single block
- Header date 2026/10/13

---

## Changes in 0.0.8

- 11.1 Classes: `Root.Class` is the implicit base of every class and is never written; writing it in a header is a level 1 warning
- 11.1 Class header: `class Name(params) : [parent class], [traits]`; the parent class goes before all traits (a class after a trait is a compile error); names are resolved from context and the namespace removes any ambiguity
- 11.1 A class and a trait with the same name in the same namespace is a compile error
- 11.1.1 Constructors: visibility, class name and parameters; several constructors allowed when the parameters differ (13.2 rules); the default constructor is generated like in C#
- 11.1.1 Private constructors are allowed; only the class body (including its static functions) can call them
- 11.1.1 A constructor that throws: the exception propagates, the object is destroyed, `atEnd` blocks run, fields not yet reached hold their zero value
- 11.1.1 Primary constructor parameters are private fields; redeclaring the same name in the body (public or not) does not create a new field, it sets the visibility of the existing one; a different type is a compile error; an initializer is allowed (level 3 warning, the body value wins)
- 11.1.2 An overriding function inherits the visibility of the original; no visibility is written on it
- 11.1.2 Static members: `static` after the visibility; functions and properties; inherited by subclasses; static functions cannot be overridden and redeclaring one with the same signature in a subclass is a compile error; generic classes get one copy per type instance
- 11.1.2 Static properties: initializer allowed, zero value without one; initialized at program start in declaration order (a compiler option allows lazy initialization); initializers reading other classes' statics are evaluated on demand, a cycle is a compile error; `static const` for class constants; `atEnd` never touches statics
- 11.1.3 Equality: `==` on classes resolves in this order: `equals`, then an operator `==` overload, then identity; `!=` is always the negation and cannot be overloaded; defining both `equals` and an operator `==` is a level 2 warning; a class with `equals` or `==` but no hash, used in a hash collection, is a level 1 warning; null is handled by the compiler before `equals` is called
- 11.1.3 `bool equals(T other)` is a generic function with an implicit `T`; `T is type` checks the generic type at compile time, `other is type` checks the value at runtime; both are allowed in the same function; every path must return a value; the upward-check warning does not fire when a check is true for one `T` only; an unreachable branch is a level 3 warning
- 12.4 (new) `->` on a field: `->a.c` moves the field out, `a.c` is null afterwards and `a` stays usable; the owner or a writable borrow `(&)A` can do it, a read-only borrow cannot
- 12.4 `->` on a variable or field of a basic type is ignored and behaves like a plain copy; level 1 warning
- 12.4 Finalizer: `f is atEnd.` is a statement only; the final dot is mandatory and only a line break or `;` may follow; `f is atEnd.close()` is a compile error
- 12.4 `f is atEnd.` releases the ownership held by `f`: a single owner is destroyed with its full destructor, a counted object drops the counter and is destroyed at zero; `f` becomes null; null is a no-op; weak references see null after destruction; on a borrowed variable it is a compile error; it replaces `f?.close()` in cleanup code
- 1.1 `atEnd` is a reserved word; it cannot be a type, trait, variable, parameter or field name
- 14.2 / 11.4 `atEnd` has five forms, without parentheses:
  1. `atEnd of a, b { }` ties the code to the listed variables; it sees only the listed objects, declared before it; it runs when they go out of scope and still runs while at least one of them exists
  2. `} atEnd { }` after a function: function cleanup
  3. `} atEnd { }` after any other statement or scope: scope cleanup; it sees the union of all scopes and subscopes of the statement; it always runs once the statement is reached; locals of branches not taken hold their zero value; the same name with different types in two branches is a compile error
  4. `} atEnd { }` after a class: the class destructor, always the last one to run
  5. `} atEnd { }` after a constructor: a destructor fragment, registered when that constructor runs and executed when the object dies, in reverse order of registration; it sees the constructor locals and parameters plus the class members
- 14.2 Order at scope or function exit: the `atEnd of` blocks first, then the trailing `atEnd` as the final cleanup
- 14.2 Only the variables used by an `atEnd` are kept alive until it runs
- 14.2 A subclass runs all its own `atEnd` blocks first, then the parent's, then the traits' (reverse of construction)
- 14.2 `atEnd of f { ...; f is atEnd. }` is legal and not flagged
- 14.2 An `atEnd` placed inside a constructor body is normal scope cleanup; only the one after the constructor is a destructor fragment
- 19.3 Warnings: L1 `Root.Class` written in a header; L1 equality without hash on a hash-collection key; L1 `->` on a basic type; L2 `equals` and operator `==` both defined; L3 primary parameter redeclared with an initializer; L3 unreachable generic branch
- Examples updated: `Root.Class` removed from the headers of the 11.1, 11.3 and 11.4 examples; `?.close()` replaced by `is atEnd.`; the old standalone `atEnd { }` example of 14.2 is now `atEnd of mutex { }`
- 99 Open items: `atEnd` that throws inside a class destructor (15.x); lexing of reals ending in a dot (`0.`); `File.close()` still existing as a method; binding in generic checks
- Header date 2026/10/15


## Changes in the next version (version and date set when merging)

- 17.6 `Root.IO` File manager (new): `File` class with constructor, properties (`path`, `name`, `extension` without the dot, `parent`, `isDirectory`, `isFile`, `size`, `created`, `modified`, `accessed`, `isReadOnly`), static methods (`exists`, `delete`, `copy`, `move`, `createDirectory`, `getAllFiles`, `getTempFile`, `getTempFileName`, `getTempDir`, `combinePaths`, `open`) and object methods (`exists`, `delete`, `copyTo`, `moveTo`, `createDirectory`, `getAllFiles`, `readAll`, `readAllText`, `writeAll`, `writeAllText`, `open`)
- 17.6 Paths normalized with `/` as root and separator, drive as first folder (`/d:/folder/file.txt`); paths of any OS accepted and normalized; `.` and `..` accepted
- 17.6 Enums `fs.DeleteOptions` (bitfield), `fs.OverwriteOptions`, `fs.FileSystemObjectsType`, `fs.FileWriteMode`, `fs.StreamReadWriteAccessMode`, `fs.StreamOpenMode`, `fs.StreamSeekOrigin`
- 17.6 Defaults: `delete` with `None`, `copy` and `move` with `OverwriteOptions.None`, `getAllFiles` with `recursive` false, `Files` and `"*"` (the path is required), text encoding UTF8, write mode `CreateReplace`
- 17.6 `copy` and `move` always include the content of a directory, merge into an existing directory and return nothing when the file is skipped; `createDirectory` never creates parent folders (missing parent: `InvalidPathException`) and returns the existing directory when it already exists
- 17.6 `getAllFiles` returns an `arr<File>` without order; a missing path raises `FileNotFoundException`, an empty path `InvalidPathException`; `match` with `*` and `?`, case-insensitive, on file names; regular expressions delayed until 0.1.0
- 17.6 `writeAll` and `writeAllText` follow a `fs.FileWriteMode` (renamed from `fs.WriteMode`); a missing parent folder raises `FullPathNotFoundException`; a mode that cannot be met raises `FileNotFoundException` (`ReplaceOnly`, `AppendOnly`) or `FileAlreadyExistsException` (`CreateOnly`)
- 17.6 `ForceDelete` tries its best to delete a locked item (OS dependent); no automatic deletion of temporary files (use `atEnd`); whether `getTempFile` creates the file is OS dependent
- 17.6 Attributes and `size` of an item that does not exist give the default values
- 17.6 `fs.DeleteOptions` becomes a bitfield with `Recursive` and `ForceDelete` (`RecursiveAndForceDelete` removed)
- 17.6 Streams (new): `Stream` base class with `canRead`, `canWrite`, `canSeek`, `hasLength`, `position`, `length`, `read`, `write`, `readByte`, `writeByte` (returns `bool`), `seek`, `flush(bool forcePhysical = false)`, `close`; `FileStream : Stream`; `TextStream` trait for any stream; `MemoryStream` and `ConsoleStream` later
- 17.6 The constructor of each stream type opens the stream and takes `readMode` and `writeMode` (`fs.StreamReadWriteAccessMode`: `None`, `Exclusive`, `Shared`) and `openMode` (`fs.StreamOpenMode`, seven values); `File.open` is a shortcut for `FileStream(path, readMode, writeMode, openMode)`; a failed constructor returns null with exceptions; the modes are exposed as read-only constants `READ_MODE`, `WRITE_MODE`, `OPEN_MODE`
- 17.6 A stream is closed when its owner is lost (Rust model); `stream is atEnd.` or `close()` closes it earlier and a second `close()` does nothing; a closed stream cannot be reopened
- 17.6 `read` and `write` accept a whole array or a view, with `start` and `length` relative to the buffer passed; `readAtLeast` and `writeAtLeast` (default 0); `read` returns `uns?` (null at the end of the stream, 0 if nothing is available); a read timeout is a silent exception; `uns` and `int` for sizes and positions (split by use before 0.1.0)
- 17.6 `seek(int position, fs.StreamSeekOrigin origin)` returns the new `uns` position; before the start: silent `SeekBeforeBeginException`, position 0; past the end allowed; `position` always available, `length` 0 when `hasLength` is false
- 17.6 General decisions: binary streams with text helpers, memory streams on every target, `Root.Console` as a shortcut
- 15.3 New exceptions, added to the table: `FileNotFoundException` (25), `InvalidFileAccessException` (26), `FileAccessException` (27, silent), `DirectoryNotEmptyException` (28, silent), `FileNameAlreadyExistsException` (29, silent), `InvalidPathException` (30), `FullPathNotFoundException` (31), `SeekBeforeBeginException` (32, silent), `FileAlreadyExistsException` (33), `FileAccessToADirectoryException` (34), `ReadOnlyItemCannotBeWrittenException` (35), `ConflictingSharingException` (36), `InvalidStreamModesException` (37), `NoMoreResourcesException` (38), `AccessToAClosedStreamException` (39), `StreamModeNotAllowedException` (40), `SeekNotSupportedByStreamException` (41), `InvalidBufferRangeException` (42), `GenericIOErrorException` (43), `FileRemovedWhileOpenException` (44), `StreamCloseFailureException` (45)
- 3.1.1 Bitfields (new, next to the enums): `bitfield8`, `bitfield16`, `bitfield32`, `bitfield64`, zero-based bit positions, `.None`, `.All`, `set`, `clear`, `setAs`, `toggle`, `isSet`, `isAnySet`, `_value` flips only the defined bits, an integer cast to a bitfield masks undefined bits
- 17.6 The cross-references use the numbers of the main spec; the examples use `atEnd of x { }` (14.2) and `x is atEnd.` (12.4); the intro pasted from the standalone file is removed
- 11.4, 14.2 Examples updated to the new `File` API (`size`, `File.exists`)
- 17.5 Empty table row removed
- 99 Open items: `close()` as a method of `File` removed (streams have `close()`); bitfield in-place or new-value decision and nullability added; the open items of files and streams are in 17.6.9
- 11.3 Generics rewritten around sets: 11.3.1 inline constraints for the type parameters and `where` for sub-constraints (placeholders, type expressions), both forms can be mixed; the same constraint repeated is a level 1 warning, a different one on the same parameter is a compile error; `Hashable`, `Orderable` and `Printable` removed (every type has `.hash`, `.order`, `toStr()`; `.hash` is sized to the architecture, `uns`); examples updated
- 11.3.6 `Generic` sets (new): named compile-time sets of types (types, classes, traits, other sets), uppercase names, namespace level, `public` / `internal` to expose; used as a constraint (same type per call) or as a parameter type (independent arguments); never a type of values; predefined `Signed`, `Unsigned`, `Integers`, `Reals`, `Numeric`, `Strings`; `int`, `uns`, `real` are compile-time aliases and never a duplicate; operators are checked where used against every type that can reach them
- 11.3.7 Type switch (new): `value is ? { type: expression, ... }`, only the matching case is compiled per copy, coverage checked at the definition, first match wins, unreachable case is a level 2 warning
- 11.3.8 Null in generic code (new): sets with `?` numbers, `date`, `time` or objects force null handling; `string` does not count; `T?` is not allowed
- 11.3.9 Converting a value to a set member (new): exact type, then same macrotype leftmost, then compatible macrotype leftmost, otherwise a compile error listing what was tried; classes: exact class, nearest parent, left to right
- 11.3.10 Classes inside generics (new): `Box<Animal>` holds subclass objects seen as `Animal`; `Box<Derived>` converts to `Box<Base>` automatically by moving the box (Rust model)
- 11.3.11 Ownership in generic code (new): `->Item` moves an object and copies a value type; moves are checked at the definition, an unconstrained `Item` is move-only
- 11.3.12 Limits (new): chain depth 16, copies per generic 16, copies per library collection 64, each 1 to 256 with its own compiler parameter; counted per generic and per collection; `arr` has no limit; identical machine code is merged by the backend; clear error message when exceeded
- 11.3.13 Not in the language (new): default type arguments, specialization, `T?`
- 11.3.14 Libraries (new): internal generics export nothing; public generics will export a minimal intermediate form, after 0.1.0
- 6.1 `arr<T>` is a primitive; the other collections are ordinary generic classes
- 19.3 Warnings: L1 same constraint repeated inline and in `where`; L2 unreachable case in a type switch
- 99 Open items: `Generic` sets inside a class; variable declared with a set as type argument; conversion of borrowed generic values and of collections; `where` on class and trait headers; `clock`, `lapse`, `addr` and pointers as nullables in a set; `->` warning inside generic code; names of the compiler parameters for the limits; intermediate form of exported generics


## Changes in 0.0.11

- Header: version 0.0.11, 2026/10/20
- 6.1 Index outside a collection: checked in every build; Debug raises `IndexOutOfRangeException` (catchable), Release is a `Panic`, a `catch` for it in Release is ignored with a level 3 warning
- 6.1 The seven collections are generated by the compiler as C code (speed first); users can write their own collection classes; collections own their items (drop code per type, unassigned results destroyed at the end of the statement, `c[k] = v` drops the old item, `for` only borrows)
- 6.2 Maps and sets do not allow null keys (or null set items); `""` is a valid string key; NaN keys are not stored or retrieved, without an exception
- 6.2 How a `string` is stored in the generated C is decided with the strings, not with the collections
- 6.4 `for` loop: read-only variable by default, `(&)Type variable` for a read/write borrow; set items and map keys always read-only; a basic value without `(&)` is a copy (level 3 warning when assigned); no consuming loop
- 6.4 A collection cannot be changed while it is iterated: compile error when provable (Rust style), otherwise a version check at runtime (C# style)
- 6.5 Views are read-only only (the writable view `(&)c[a..b]` and its example are removed) and can only be locals or parameters; while a view exists the collection cannot be changed
- 6.6 `pop((&)out)` is the only form: returns `bool` (the `isValid`), moves the item into `out`, on an empty `vec` returns `false` and `out` gets the default value; no `tryPop`, no exception
- 6.6 `bool tryGet(k, (&)out[, default])`: returns `true` when the key exists
- 6.6 `swap(position, new)`, `swap(key, new)` (return the old item and its ownership), `swap(i, j)`, `swap(key1, key2)` (exchange, with the rules for missing keys)
- 6.6 `->c[k]` moves the item out: zeroed in `arr` and `vec`, removed in `list`, `map`, `hmap`, `set` and `hset`
- 6.6, 6.10 Reading a missing map key gives the null or zero value without an exception
- 6.8 Example of `pop` updated
- 6.12 Custom collections (new, partial): traits `Collection`, `IndexCollection`, `MapCollection`, `SetCollection` with a generic item type; `len` and `getIterator()`; `Iterator` with two or three sub-traits; `next()` returns a tuple ending with `isValid`; direct C loops for the collections of the compiler
- 9 `tryParse` signature written with `(&)<type> result`; `(&)Type variable` is the placement everywhere
- 10.3 `for (&)Type variable in collection` added
- 11.1.4 Fields and properties (new): a field is a property with automatic accessors; `R:` and `W:` blocks with defaults (`field`, `field = value`); `field` and `value`; access `readonly` / `readwrite` / `writeonly` when there is no body; start value after the property (goes through `W`, classes only); visibility of each accessor; storage only when an accessor uses `field`; an exception not caught in an accessor is a `Panic`; primary constructor parameters become fields; static properties use only static members
- 11.2 Trait properties are access contracts with an optional default implementation; the class must implement a missing one; `override` gives the code for everything, parts not given fall to the default; read only in the trait stays read only; no start values in traits, trait constructors can set values; access through `any` left to the compiler
- 11.3.4 The `Sequence` placeholder example uses `next()` returning `(Item value, bool isValid)`
- 12.4 Moving a stored field or property (value copied, object moved and the field nullified) and a computed property (a new object is given away)
- 15.3 Silent exceptions `NaNUsedAsKeyException` (21), `InvalidMapKeyException` (22), `EmptyVectorAccessException` (23) removed; IDs 21 to 23 retired and not reused
- 15.4 Example list of silent operations no longer mentions the missing map key
- 15.5 Panic cases: exception not caught in an accessor, index outside a collection in Release
- 99 Open items: `tryGet` return value removed (settled); new collections, strings and properties items added


## Changes in 0.0.12

- Header: version 0.0.12, 2026/10/21
- 6.12 `GetIterator()` written `getIterator()` (edited by the user in 0.0.11)
- 19.1 Compiler Directives and the preprocessor (rewritten): a preprocessor step before the compilation removes the invalid parts and keeps the line numbers (empty lines); it is not part of the language
- 19.1 All directives start with `##` in the first column and end at the end of the line; the text cannot contain `#`; `#region` / `#endregion` become `##region` / `##endregion`
- 19.1 New directives: `##define <variable> = <value>` (no `##undef`), `##if`, `##elseif`, `##else`, `##endif`, `##emiterror <string>` (prints the string and stops with an error), `##emitwarning <string>` (prints the string as a warning)
- 19.1 Variable values: static strings (enum values), numbers and bools; the result of a condition can be defined as a bool
- 19.1 Conditions: `<variable> <operator> <value>` or a bool variable; operators `==`, `!=`, `<`, `<=`, `>`, `>=`; `&&` and `||` with the same precedence, left to right; parentheses; order operators only on numbers (strings and enums have no order); unary `!` only on bool variables
- 19.1 Misuses are preprocessor errors that stop the compilation
- 19.1 Variables set by the compiler: `os` (`windows`, `linux`, `osx`), `cpu` (`x86`, `x64`, `arm`), `arch` (`<os>_<cpu>`), `bits` (8, 16, 32, 64), `int`, `uns`, `real`, `endian`, `build`, `exceptions`, `ram` (`<number>[kb|mb|gb]`), `version` (`.major`, `.minor`, `.build`), `coptlev` and `coptmode` (`--COptLev`, `--COptMode`)
- 19.3 Warnings table: `##region` / `##endregion`
- 99 Open items: the conditional compilation item is removed


## Changes in 0.0.13

- Header: version 0.0.13, 2026/10/22
- 6 Collections reorganized: 6.7 Collection traits (6.7.1 `IndexCollection`, 6.7.2 `MapCollection`, 6.7.3 `SetCollection`), 6.8 Default collections (6.8.1 `arr`, 6.8.2 `vec`, 6.8.3 `list`, 6.8.4 `map`, 6.8.5 `hmap`, 6.8.6 `set`, 6.8.7 `hset`, the old 6.7 to 6.11 split by type), 6.9 Custom collections (the old 6.12)
- 6.1 Lengths and default indexes are `uns` (were `u64`); every collection implements `Collection` and one of the three sub-traits
- 6.1 `list` is a singly linked list (was a counted B-tree); the table and the sentence about hash against B-tree are updated
- 6.7 `Collection` requires a read-only `len` and `getIterator()`; the sub-traits are implemented by `arr`/`vec`/`list`, `map`/`hmap` and `set`/`hset`; the shape of `next()` for sets is open
- 6.8.3 `list`: singly linked, the list keeps the count, the first node and the last node, no double links, no capacity; default iterator with a node cursor; table with the cost of each member (`.len` O(1), `add` O(1), `insert` and `removeAt` O(1) at index 0 and otherwise O(n), `c[i]` O(n), `sort` O(n log n) by relinking, ...)
- 6.8.4 to 6.8.7 `map`, `hmap`, `set` and `hset` have a section each (the text of the old 6.10 and 6.11 split by type)
- 6.9 A custom collection either implements the traits or is a subclass of a default collection; the section stays vague; open point (kept in the section, not in 99): whether the default collections can be subclassed and how overridden methods are dispatched (`arr` cannot be subclassed)
- References renumbered (6.1, 6.4, 19.3 and 99); the Changes blocks of the previous versions keep the old numbers
- 6.7 Rule: what is shared stays in the trait, what is specific to one collection is defined by that collection class, every collection is in a trait; `arr` is a primitive, the traits do not really exist for it but its properties and members are accessible
- 6.7 `Collection` has `.len`, `.sizeof`, `has(v)`, `->c[k]`, `clear()` and `getIterator()`
- 6.7.1 `IndexCollection` has `c[i]` read and write, `c[a..b]` view, `indexOf`, `swap` (both forms), `sort`, `sortUnstable`, `reverse`, `copyFrom`, `fill(value)`, and (`vec` and `list` only, not `arr`) `add`, `add(collection)`, `insert`, `remove`, `removeAll`, `removeAt`
- 6.7.2 `MapCollection` members listed (`.keys`, `.values`, `c[k]`, `add(k, v)`, `add(map)`, `hasKey`, `keyOf`, `tryGet`, `remove`, `removeAll`, `removeAt`, `swap` forms); the iterators of maps and sets are defined later
- 6.7 The `Iterator` is an object that implements a trait, with a cursor and `*Collection collection` (read-only pointer, 12.1) for the end check; `next()` returns the read-only item and `writableNext()` the `(&)` item; only `IndexIterator` is defined (`uns cursor` for `arr` and `vec`, node pointer for `list`); the sets are defined later
- 6.8.1 to 6.8.7 Each section names its trait and lists only the differences and the members specific to the class (`vec`: `pop`, `fillAtCapacity`, `resize`, `truncate`, capacity members; `list`: `fill(count, value)`; `hmap`, `hset`: capacity members); the `arr` special case is noted
- 6.8.3 The nodes of a `list` own the objects they hold
- 6.6 `fill`: the value can be any value (basic values, strings duplicated, dates, times, other non-class types) or `null`; a class instance is a compile error; `list` also has `fill(count, value)` (`myList.fill(256, 'c')`); `fill(value)` of the trait sets every existing item
- 6.1, 12.4 `->` is mandatory when an object is moved into a collection that owns its items (`list[3] = ->task`, `list.add(->task)`, `insert`, `swap`); the plain form with an object is a compile error; basic values are copied and need no marker
- 6.1 The index out of range rule is the same for `arr`, `vec` and `list`; the failing `c[i]` costs O(1) (the index is compared with `.len` first)
- 6.4, 15.3 Changing a collection during an iteration raises `ModifyingIteratedCollectionException` (new ID 46, not silent): catchable in Debug builds; in Release builds a `Panic`, and a `catch` for it is ignored with a level 3 warning
- 6.7.2, 6.7.3, 6.9, 99 Correction: the first ideas for the iterators of maps and sets (`next()` returns `(Key key, Value value, bool isValid)` for maps, the `MapIterator` sketch, the open shape of `next()` for sets, the open sub-traits and cursor types) are kept and marked "not yet defined"; only the index iterator is defined
- 6.9 Sketch updated to `IndexIterator`; for a later version: indexer and literals of custom classes, subclassing of the default collections, lists of pointers and references
- 11.1.5 New section: structs are generic non-heap value types (like Rust structs), to be defined; objects inside are not allowed or are owned by the struct
- 99 Open items: iterator, runtime check and index-out-of-range items updated (the non-silent exceptions of the collections are formalized during the implementation); new items for the sets, lists of pointers and structs
- 19.1 Restored after the merge: the rules of the variables (predefined ones cannot be redefined, a name equal to a value of a predefined variable is an error, a word on the right of a comparison, variables of the project file), the values of `bits`, `endian`, `build` (default `debug`), `ram` (4gb by default) and `version` (the language version); `dfcompile` and `dfparse` become `dfcompiler` and `dfparser`, `-forcelib` becomes `--force-lib`; 19.2 rewritten
- 19.1 `int`, `uns` and `real` have the size of the cpu: 64 on x64, 32 on x86 (`--native-int` and `--native-real` can force another size)
- 6.1, 12.4 `->` is needed only for an object that already exists; an object created in the same statement (`list.add(Task("a"))`) needs no marker
- 6.6 `fill(null)`: the items become null and the objects they held end (their `atEnd` runs)
- 6.7 `Collection<Item>` is generic: `IndexCollection<Item>` and `SetCollection<Item>` extend `Collection<Item>`, `MapCollection<Key, Value>` extends `Collection<Value>` (`has(v)` on a map searches the values); the key of `->c[k]` is given by each sub-trait
- 11.3.2 A generic trait can extend one generic trait (new; needed by the collection traits): the arguments of the parent are types built from the parameters of the child (the same list, a part of them or a concrete type), the chain can have several levels, the class implements the whole chain, one parent only, a parent with placeholders is not supported yet; examples `ABCCollection<Item> : HashCollection<Item>` and `ABCMap<Key, Value> : HashCollection<Value>`


## Changes in 0.0.14

- Header: version 0.0.14, 2026/10/24
- 1.1 Naming: the non-public methods that implement properties and operators are snake_case (provisional)
- 2.1 `uns` has the size of `int` (the cpu size, or `--native-int`); lengths, indexes and `.hash` use it; a `uns` smaller than the cpu size gives a warning (19.3)
- 2.4 Lengths and default indexes are `uns` (the line still said `u64`)
- 3.2 A tuple item can be a writable reference `(&)Type`; the borrow lives as long as the tuple; a tuple that holds a `(&)` follows the rule of the iterators and views
- 6.1 `arr` is a class like any other (ownership, hash, methods, properties; it can be an item: `list<arr<i32>>`), with direct generated code; it cannot be subclassed; the declaring variable owns it, `b = a` borrows, `->a` moves, an array field is dropped by its class (was: a primitive)
- 6.1 A collection has an internal counter of its open iterators and views
- 6.1, 12 An owned object that loses its owner is destroyed and its `atEnd` runs (`clear()`, `fill(...)`, `c[i] = v`, ...)
- 6.1, 15.3, 15.5 Out of memory: any allocation; `OutOfMemoryException` in Debug (catchable, not silent), `Panic` in Release; an overflowing size is reported the same way (no separate `OverflowException`); a literal size above the maximum is a compile error
- 6.2 `.hash` is mandatory on every type, of size `uns`; native hashes cannot be overridden; how it is calculated: own value up to `uns`, FxHash combine of the parts above (`K` for 64 and 32 bits), `real` by its bits with `-0.0` as `+0.0` and one canonical NaN, rapidhash for strings, ordered combine for tuples, class name plus address for classes (collections included)
- 6.2, 11.1.3 `Root.Class` offers `getHash()` and `equals(any?)`
- 6.2, 6.6, 11.2.1, 11.3.1 `.order` is removed: ordering is the trait `Comparable<T>` with the mandatory `int compareTo(T value)` (-1, 0, 1); the default class order (hashes, then addresses) is its default implementation
- 6.4 The collection is read-only in size while it has open iterators or views: the internal counter (created by an iterator or a view, released by their default `atEnd`; explicit iterators count too), provided by the trait `Collection`; a changing operation raises `ModifyingIteratedCollectionException`; every changing method checks the counter
- 6.4 The list of the forbidden operations (`add`, `add(collection)`, `insert`, `pop`, `remove`, `removeAll`, `removeAt`, `resize`, `truncate`, `fillAtCapacity`, `fill(count, value)`, `reserve`, `shrinkToFit`, `sort`, `sortUnstable`, `reverse`, `copyFrom`, `->c[i]` on a `list`) and of the allowed ones (reads, `swap`, `replace`, `c[i] = v`, `fill(value)`, `clear()`, `->c[i]` on `arr` and `vec`, changes in place)
- 6.4 The compile-time proof covers only the direct case (a changing member or an element assignment on the same variable or field path); everything else is a runtime check
- 6.5 Views are the class `View<C>` (`C` is the type of the collection: `View<vec<string>>`); each view class holds what its collection needs; trees and maps will probably need dedicated views; `[a..b]` calls `get_view(uns? start, uns? end)`, a missing bound is null; a `list` view is created in O(n); iterators and `for` run over a view; the lock is the one of the iterators; `c[a..b]!` copies into a new collection of type `C` (needs `.clone()`)
- 6.5, 12 Where iterators and views live: if not given away, only inside the scope that created them; moved or borrowed, they follow the ownership rules; the collection is changeable again when the last holder ends (replaces "a view can only be a local variable or a parameter")
- 6.6 `.sizeof`: on every type; for a collection, the header plus the allocated block (the capacity), without what the items own; for a `list`, the header plus all the nodes
- 6.6 `add(collection)` on `vec` and `list`: basic items copied, objects moved with `add(->other)` (the other collection ends and its variable becomes null), the plain form with objects is a compile error until cloning, the same collection raises `ModifyingProtectedSourceException`
- 6.6 `indexOf` returns the maximum `uns` when not found (was `u64`)
- 6.6 `removeAt(range)`: the ranges of 6.5; a range outside the collection raises `IndexOutOfRangeException`
- 6.6, 6.7.1, 6.8.3 `swap(position, new)` is removed; `swap(posA, posB)` only exchanges; new `var old = c.replace(pos, value)` (the old item is returned, or ends at the end of the statement when not assigned); the `swap` forms of maps are left for the maps
- 6.6 `clear()` on `arr`, `vec` and `list` sets every item to the default value and keeps the size (was: removes all items on `vec` and `list`); to remove all items, `truncate(0)`
- 6.6, 6.7.1, 6.8.3 `truncate(n)` is available on `list`
- 6.6 Sorting algorithms: `arr` and `vec` `sort` is a merge sort with run detection (TimSort style), `sortUnstable` is pdqsort; `list` uses a bottom-up merge sort that relinks the nodes for both; the comparison goes through `compareTo` or the given function
- 6.6 `copyFrom` works between any index collections; it copies for real (objects cloned), it is not a move; the index check must always be there
- 6.6, 6.7.1 New `transformTo<Kind>()`: a moving conversion between `arr`, `vec` and `list` (the original ends, no cloning; the item type comes from the original, a different one is a compile error; `vec` to `arr` can reuse the block)
- 6.7 `Collection<Item>` has no `getIterator()`: `IndexCollection`, `MapCollection` and `SetCollection` each declare a typed one
- 6.7 `Iterator` is a non-generic marker trait with three sub-traits: `IndexIterator<Item>`, `MapIterator<Key, Value>`, `SetIterator<Item>`; `IndexIterator` has `next()` and `writableNext()`
- 6.7, 6.7.1, 6.8.1, 6.8.2, 6.9 The cursor is internal to each iterator and is not in the trait; the pointer to the collection lives in each concrete iterator class; the iterators follow a contract (first rule: each element once and only once)
- 6.7 The methods behind the properties and the operators: non-public, possibly virtual, mandatory for the implementing classes, snake_case; table `get_sizeof()` (`Root.Class`), `get_len()`, `move_out(k)` (generic form, `uns k` for `IndexCollection`, `Key k` for `MapCollection`), `read_pos(uns k)`, `set_pos(uns k, value)`, `get_view(uns? a, uns? b)`; `.sizeof` moves from `Collection` to `Root.Class`
- 6.8.2 Growth policy: the capacity is at least what was requested when growing and exact after `shrinkToFit()`; doubling up to `double-grow-limit` (default 4mb), then the factor `further-grow-factor` (default 1.5), both compiler parameters; a fixed increment only for small targets; the first allocation is 16 items, on the first `add`, unless a capacity is given
- 6.8.3 Every node is allocated separately; two kinds of nodes (next pointer plus value, next pointer plus object pointer), no pointer to the list, natural size, no padding
- 6.8.3 The `list` iterator holds a pointer to its list and one to the current node; the end is a null next pointer; the pointer to the last node is not used by iterators; `writableNext()` changes the node content in place, never the structure; the pointer kinds are decided with the pointers
- 6.8.3 A `list` view holds a start node pointer, an end node pointer and a count; read-only; it increments the counter like the iterators
- 6.9 The sketch of a custom iterator keeps the cursor and the pointer to the collection in the class; the counter comes from the `executeAfter` blocks of `Collection`
- 11.2 New trait rule `executeAfter { }`: a block after a trait method that runs after the class implementation, only when it succeeds, cannot be overridden or removed, reads `<method>.result` read-only, blocks chained through the hierarchy; `fun f() { body } executeAfter { ... }`; `executeBefore` may come later; `Collection` uses it for the counter; `releaseIterator()` is private
- 11.2.1 Basic traits (new section): `Comparable<T>`
- 11.3.12 `arr<T>` is described as a class with direct generated code (still no copy limit)
- 15.3 New exception `InvalidIteratorReleaseException` (not silent; its ID is set at 0.1.0)
- 15.5 Panic cases: a change of the size of an iterated collection and out of memory, in Release builds
- 19.3 Warning: `uns` smaller than the cpu size (level to be set)
- 99 Open items: `.sizeof` removed (decided); the iterator item now asks only for the members of `MapIterator` and `SetIterator`; new items for the order of the `executeAfter` chain, private state in a trait and `clear()` on maps and sets; the views item no longer says "locals and parameters"; out of memory noted as decided
- 12.1 Pointers are nullable by default (null is the 0 pointer); a reference is a pointer generated and guaranteed valid by the compiler; explicit pointers are for low-level work; in safe code an explicit pointer exists only when its validity can be proven
- 12.1 `(*)T` converts to `*T` implicitly; `*T` to `(*)T` is a compile error, with a way that is allowed only in unsafe code; transmute-only now refers to conversions between pointers and numbers or `addr`
- 12.1 Writability of a pointer is always explicit where it is created or passed
- 12.1 `unsafe` can mark a scope, a function or a class; an unsafe class is usable only from unsafe code
- 6.4 The counter of open iterators and views is released through the `asparent` method `releaseIterator()`; the collection is the parent of its iterators and views and nulls their reference to it when it ends
- 6.5 Iterators and views are children of their collection (`as parent`); if moved with `->` and the collection ends first, using them raises `UsageOfDestroyedObjectException`
- 6.7 The iterator holds a reference to its collection (not a pointer), declared with `as parent`; every method call checks it; checks removed when the iterator is never moved with `->`, otherwise a level 2 warning; `asparent` methods and the `ListIterator` example
- 6.8.3 The `list` iterator holds two references (the list as parent, and the current node); no explicit pointers needed
- 6.9 The custom iterator sketch uses `&MyCollection<Item> collection as parent` instead of `*MyCollection<Item>`
- 11.2 `releaseIterator()` is `asparent` (only children can call it, even through a read-only reference)
- 12.4 Use of a variable after `->` or `is atEnd.` raises `UsageOfDestroyedObjectException`, or is a compile error when detectable (replaces "normal null access")
- 12.5 Parent and child objects (new): `as parent` on one reference parameter of a class (at most one); the parent nulls the reference of its children when it ends; an adopted child (moved with `->`) checks the reference on every method call, with a level 2 warning; a child never moved needs no checks
- 12.5 `asparent` visibility: methods callable only by the children of the object, even through a read-only reference, not public
- 12.5 `unrestricted` visibility: replaces `public` and includes it; callable also through a read-only reference (`&`); a plain `public` method needs an owned or writable reference; properties are readable through `&`, assigning to them is a compile error
- 15.3 New exception `UsageOfDestroyedObjectException` (48): not silent, catchable in Debug and Release
- 19.3 Warning level 2: extra checks emitted for a child object moved with `->`
- 99 Open items: who holds the link to a child after `->`; how the collection tracks its live children; pointer details (conversion in unsafe code, `unsafe` block syntax, null handling in safe code, provable validity, equality, `.hash`, `toStr()`, generics); removed "lists of pointers and references" only if you decide it is resolved
- 12.5, 6.4, 6.5, 6.7, 6.8.3 The parent knows its children and, when it ends, nulls their reference to it; it does not destroy them. A child item moved with `->` (adopted child) checks its parent reference on every method call; a child never moved needs no checks. The two open items on parent and child objects are removed from 99 (the parent holds the links)
- 12.5 An adopted child is a child whose ownership changes: moved with `->`, returned by a function (a return always moves, as in Rust), or put into a field or a collection; an `asparent` method can also be called by the object itself
- 1.6 The duplicated heading is removed; `asparent` in 1.6.1 and 1.6.2: the object itself and its children can call it (as in 12.5)

## Changes in 0.0.15

- 12, 12.6 Cloning (new section): `a!` is a shortcut for `.clone()`; classes are clonable by default and `clone()` can be overridden freely; the default clone fills each field by its kind (native duplicated, owned deep cloned, owned child null, reference counted one more owner, borrowed null, weak another reference); anything that cannot be duplicated becomes null; weak references into the object's own parts are remapped to the copies; a clone has none of the children of the original
- 12.6 Not clonable (compile error): a `notclonable` class, an object that has a parent, an `unsafe` class, a class with pointer fields; through a trait type `clone()` is a compile error and `tryClone()` is used
- 12.6 New `tryClone()`: returns the clone or raises `ObjectNotClonableException`, not silent
- 11.1 New class modifier `notclonable`, written before `class`
- 12 Table: `a!`, `.clone()` rewritten (was: "other references become weak"), `.tryClone()` added
- 15.3 New exception `ObjectNotClonableException` (not silent; its ID is set at 0.1.0)
- 6.6 `add(collection)` on `vec` and `list`: the plain form with objects clones the items with the rules of 12.6 and leaves the source unchanged (was: a compile error until cloning)
- 11.3.10 Conversion of collections: an owned `vec<Dog>` moves into a `vec<Animal>`; a read-only borrow `&vec<Animal>` (and `&Box<Animal>`) of a `vec<Dog>` is allowed; a writable borrow `(&)` is a compile error
- 11.3.7 Type switch on objects: chosen at run time by the class, cases separated by commas, a case can bind a borrowed variable (`Dog x: x.woof()`, the same name in several cases is allowed), a case after a parent that already matches is a level 2 warning
- 99 Open items: conversion of borrowed generic values and of collections removed (decided, 11.3.10)
- 19.1 `cpu` values: `x86`, `x64`, `arm32`, `armeb`, `arm64`, `arm64be`, `arm64_32`, `riscv32`, `riscv64`, `ppc32`, `ppc64`, `ppc64le`, `mips`, `mipsel`, `mips64`, `mips64el`, `sparc`, `sparcel`, `sparcv9`, `avr`, `m68k`, with their size and endianness (Dflat names, not the names of clang; was `x86`, `x64`, `arm`); `bits` and `endian` come from the cpu; a cpu not built yet is a compile error
- 3.4 A number converts implicitly to a `lapse`, in milliseconds (there is no `lapse` literal): an integer gives a date lapse, a real gives a clock lapse (a date lapse beyond ±3.6 years, rounding away from zero; NaN gives null)
- 17 Standard library 0.0.10: `Root.exit(int code)`; `Root.Console.read()` returns a `char?` and does not wait (null when no key is pressed, no echo, no Enter); `Root.Console.clear()`; `Root.Time.sleep(lapse duration)`; `Root.IO.getCurrentDir()`, `setCurrentDir(File | string)`, `getFullPath`, `getRelativePath`
- 17 Standard library 0.0.10, section 7 Running programs (new): `Root.ExecProcess.run` (gives a `RunResult`: `exitCode`, `output`, `errors`, `timedOut`, `error`), `exec` (same console, returns the exit code, raises `FileNotFoundException` when the program is not found), `getEnv` (`""` when missing); arguments as an array, no shell; the `Process` object and `ProcessEndBehavior { Wait, Kill, Detach }` are for later
- 11.2 Traits: the qualified name `TraitA.member` is needed only for a clash of public members; the unqualified name is a compile error that asks for it; private members of two traits never clash, the compiler aliases them
- 11.2, 1.6.1 A private member of a trait is seen only by the code of that trait, not by the implementing class; a sub-trait reads every member of its parent traits read-only (private included) and can call all their methods (private included); not `asparent`
- 11.2 A public trait field must be initialized in every constructor of the implementing class, directly or through the trait constructor (compile error otherwise); a private trait field is initialized only by the trait, or keeps its zero value
- 11.2 Diamond: compile error when two traits give different default implementations of the same member of the shared supertrait, or call its constructor with different arguments
- 11.2 The chain of `executeAfter` blocks runs like `atEnd`, from the most specific trait to the root, once per call; a call to the parent implementation does not start it again
- 11.2 `Collection`: `iteratorCounter` is a private field; new private method `acquireIterator()`, called by the `executeAfter` of `IndexCollection`
- 99 Open items: the order of the `executeAfter` chain and private state in a trait removed (decided, 11.2)
- 6.7, 6.9, 11.2, 11.3.2, 11.3.4 The examples of iterators, collection traits and `Named` write `public` on their public methods and properties
- 11.2 Visibility in a trait follows 1.6.1: no keyword is private, a public member is written `public`; an implemented method keeps the visibility of the trait
- 11.2 `executeBefore { }` (new): runs before the implementation of the class, cannot be overridden; an exception in it stops the call; the order of a chain is decided when needed
- 11.2 `Collection`: `releaseIterator() { }` has an empty default body (the `executeAfter` takes the counter back); new private `checkUnlocked()` for the `executeBefore` of the methods that change the size
- 4 Strings follow the object ownership of 12: `b = a` is the same string, `->a` moves, `a!` copies; a change through `b` is seen through `a`; assigning rebinds the variable (a live borrow of the old string is a compile error); the owner frees the string at the end of its scope, a borrower never does (was: strings are values, never freed)
- 4 `==` on strings compares the content (same pointer `true`, different byte length `false`, then the bytes); `text[pos] = c` writes in place (was: not decided); a string being iterated cannot be modified
- 4.1 Modifying a string (new): `+=`, `text[pos] = c` and `clear()` always in place; the other modifying methods work in place when the result is not used or is assigned back to the same string (chains and known aliases included), a new string otherwise; in place on a constant string is a compile error; user class methods are not covered
- 4.2, 4.3, 4.4 Renumbered: char functions 4.2 (was 4.1), string functions 4.3 (was 4.2), implementation 4.4 (was 4.3)
- 4.3 New string methods: `insert`, `remove`, `replaceAt`, `append`, `prepend`, `clear`, `shrink`; `toCStr` returns a copy owned by the caller
- 4.4 String implementation (new, replaces the small / large strings of 4): a string is a pointer to its first allocation, which never moves; every header field aligned to 32 bits; a 4-bit class: 0–7 single-block strings of 32 to 4096 bytes (4-byte header: class, 12-bit byte length, 16-bit char length; text and a final 0), 11 literal (read-only, 64-bit lengths, copied at the first modification), 13 moved (pointer to the new location), 15 block string; 8–10, 12, 14 free
- 4.4.4 Block strings: a root of 60 bytes (48 on 32-bit targets) with class, flags, iterator counter, first and last block, 64-bit totals and a cursor (block, char index, byte offset); 4 KB blocks with next and previous pointers and 16-bit per-block counts (24-byte header, 12 on 32-bit targets), no running totals; a code point never straddles two blocks; the cursor is updated only by modifications; `shrink()` is the only way to a smaller form; small targets have their own implementation
- 6.2 String storage in the generated C is decided (4.4): a null pointer is `""`, so it cannot mark an empty slot of a hash table by itself
- 6.6 `fill` with a string gives each item its own copy (was: duplicated, they are values)
- 12, 12.6 Strings follow the ownership rules; the default clone copies an owned `string` field (was: duplicated as a native value)
- 99 Open items: string storage and writing by index removed (decided, 4, 4.4); new item for borrowed strings in the advanced cases
- 4, 6.1, 6.6, 12.4 A string added to a collection (`add`, `insert`, `c[k] = s`, `fill`, literals) is always copied, and the copy is shrunk
- 4 The iterator lock of a string is counted at run time for block strings and checked at compile time only for the other strings
- 4.1, 4.2, 4.3, 6.6 The string and char methods are camelCase (`trim`, `substr`, `indexOf`, `toCStr`, `isLetter`, `toUpper` ...; was PascalCase)
- 6.2 The types that can be keys (and items of a set): native numbers, `bool`, `char`, `string`, `date`, `time`, `clock`, `lapse`, enums, bitfields, tuples of valid key types, classes except `arr` and the collections; nullable types, `arr`, collections, tuples with a collection and null objects cannot
- 6.2 Enum keys are ordered by their numeric value
- 6.2 Objects as keys: owned by the collection and read-only through it, not globally immutable; safe with the default hash and order; undefined collection behavior when the fields used by an overridden `get_hash()`, `compareTo` or `equals` change while the object is a key
- 6.2 `Root.Class` offers `get_hash()` (was `getHash()`, 11.1.3 already said `get_hash()`)
- 6.4 Map loops: the value can be writable (`for (string key, (&)i32 value)`, `for (&)i32 age in ages.values`); a writable key is a compile error; keys are structural
- 6.5, 6.6 `.keys` and `.values` are views: read-only, counted by the map, with `len`, `has` and an iterator, can be stored and passed; `ages.keys!` makes a `vec<Key>`, `ages.values!` a `vec<Value>`
- 6.6 A missing key with object values reads as null (`map["a"].name` raises `NullPointerException`)
- 6.6 A map owns its keys and values (`m.add(->key, ->value)`); `->m[k]` gives only the value, the key ends with the entry; `(key, value) = ->m[k]` is a compile error
- 6.6 `add(map)`: same kind only (`map` with `hmap` is a compile error); `m.add(other)` clones, `m.add(->other)` moves
- 6.6 `tryGet` with objects gives a borrow, valid while the map exists and is not structurally modified; assigning to `out` re-points the variable, the map is unchanged
- 6.9 The indexer of a custom collection is given by `read_pos` and `set_pos` (6.7); literals and `|=` / `+=` stay for later
- 99 Open items: the order of enum keys removed (decided, 6.2); the custom collection item asks only for literals and `|=` / `+=`
- 6.7 A repeated paragraph on the cursor of the iterators is removed

## Changes in 0.0.16

- 6.2 Keys rewritten: valid keys are the native types, composite types of valid key types and classes that implement `Keyable`; collections, iterators, views and classes without `Keyable` cannot be keys; nullable types and the null key are allowed (was: not allowed); keys are always read-only through the collection
- 6.2.1 Hash (new): no hash depends on a memory address; `$` is the ordered combine; strings hash the full text and nothing of the header; null hashes as the constant `"null".hash` (a bit above the width of the type when `uns` has spare bits, 0 on microarchitectures); the default class hash is `"ns.Class".hash` combined with the hashes of the fields in memory order, an unstable field (collection, iterator, view, reference) giving the hash of its qualified type name plus the field name (was: the class name and the address); the hash of a type; the identity of a key in `hmap` / `hset` is its hash, a collision raises a silent exception only where a catch exists, otherwise the keys merge
- 6.2.2 Order (new): `real` NaN is greater than any number and equal to NaN, `-0.0` equals `+0.0`; null before any value; the default order of a class compares the hashes, two objects with the same hash compare as 0 (was: then the addresses)
- 6.2.3 Stability of keys (new): a key keeps the same hash for its stay in a collection; a class used as a key implements `Keyable`; detection is lazy (replaces "objects as keys: undefined collection behavior")
- 6.3, 1.5, 6.8.4, 6.8.5 Map literals are `[key: value, ...]` (was `{ key: value }`); keys are ordinary expressions (was: literals, bare names are strings); lowered as an empty map plus the adds; constant duplicates (and constant keys with the same hash in an `hmap`) are compile errors, a duplicate calculated at run time keeps the later value; `map<K, V> m = []` is an empty map
- 6.7.2, 6.7.3, 6.8.4 Null is a valid key and a valid item of a set
- 6.8.4.1 B-tree implementation (new): B+ tree navigated by a sort key; no parent pointer; leaves linked both ways; 8-byte branch header and 24-byte leaf header with a bitfield marker; keys before values; separators are the smallest key of the child and refer to the key in the leaf; 16 entries per leaf (compiler parameter); key slots of 8 or 16 bytes; values up to 64 bytes inline; the null key in the map header; relaxed deletion; the iterator is a leaf plus an index
- 6.8.5 The identity of an `hmap` key is its hash
- 11.1.3 The level 1 warning for `equals` without a hash is removed; no warning for an override of `get_hash()`, `equals` or `compareTo`; the default `equals` (same class, native fields first, owned objects recursively, collections and references by address)
- 11.2.1 New basic trait `Keyable`: explicit, inherited, a counter and a stored hash per object, `executeAfter` checks on `get_hash()` and `compareTo`, a normal exception when the hash of a key changed, a cost note
- 19.3 New level 1 warning: `Keyable` class with a field of a collection, iterator or view type; removed the warning "Equality without hash on a key of a hash collection"
- 99 Open items: the B-tree and the literal lowering removed (decided); new items for multi-type keys, the null hash constant, the names of the `Keyable` and collision exceptions, and the lifetime of borrows of items
- 11.3.6.1 Generics as value types (new): a generic set can be the type of any value, a closed union stored as the pair (tag, value); the tag is the hash of the type of the member (for a class, the member as listed, never the dynamic class); implicit assignment from a member type; read with `if k is T x` or `k is ? { }`; equality, hash and order from the pair; not on microarchitectures (was: a set is not a type of values)
- 6.2, 6.8.4.1 Keys of a generic set type use the multi-type layouts of the B-tree (was: reserved); 99: the multi-type keys item removed
- 6.2, 6.8.4.1 NaN can be a key: in `map` and `set` every NaN is the same key and sorts last (was: a NaN key is never stored or retrieved); for `hmap` and `hset` it is decided with the hash tables

## Changes in 0.0.17

- 6.2.1 Hash: natives are widened with zeros (no sign extension); `real` hashes its normalized bit pattern; strings use rapidhash V3 (full variant, seed 0), folded `hi ^ lo` for a 32-bit `uns`, nano allowed by the architecture definition; a tuple starts from the hash of its first component; the null string hashes as `""`; the null constant is fixed: `0x2AA52E3F0BF438BD` (64-bit `uns`), `0x21511682` (32-bit), used also by `x8?`, `x16?` and `bool?`; full-width nullable natives use a two-`uns` key slot
- 6.2, 6.2.1, 6.8.5 Identity of a key in `hmap` / `hset`: the hashes match and `equals` agrees; different keys with the same hash are both kept (was: the key is its hash, a collision raises a silent exception, the keys merge without a catch); NaN is one key and `-0.0` equals `+0.0`; `""` is a normal key (the used slots are in a mask)
- 6.2.1 `.hash` on an object calls `get_hash()` virtually; every class without an override gets its own generated default (its own name, all its fields), an inherited override is used as it is (was: the class in whose definition the hash is declared); an owned field whose class overrides `get_hash()` is stable; a collection hashes as its type name only (was: like any other class)
- 11.1.3 The identity of a key in a hash collection is the hash plus `equals` (was: the hash only, `equals` does not change a map)
- 6.6, 6.8.5.1 `reserve(n)` makes room for `n` items in all (was: `n` more items)
- 6.1 Printing a collection (new): only the first 3 items, then `, ...]` and the number of items in parentheses, for every collection, view and `.keys` / `.values`
- 6.3 Two different constant keys with the same hash in an `hmap` literal are no longer a compile error
- 6.6 On `hmap` and `hset` the capacity is counted on the directory and `shrinkToFit()` returns a `bool`
- 6.8.5.1 Hash table implementation (new): extendible hashing; a directory of 2^d bucket pointers indexed by the top bits of the scrambled hash; buckets of 16 unsorted entries with chains of extensions; doubling without rehash; nothing allocated before the first `add`; two Fibonacci scrambles (index and 8-bit filter); header, bucket and key slot layouts; merges (extension at 14, siblings at 8); directory shrink by at most 2 bits per check using C(d) and C(d−1); unsticking; stuck buckets; capacity sizing n / 11 buckets; `shrinkToFit()`; the iterator (directory index, block, slot); targets decide whether they have `hmap`
- 6.8.7 `hset` is generated from the `hmap` code and `set` from the `map` code
- 6.7.2 The cursors of `map` and `hmap` are defined (6.8.4.1, 6.8.5.1)
- 99 Open items: the hash table implementation, the null hash constant and the collision exception removed; the widening of stuck buckets added

## Changes in 0.0.18

- 6.6, 6.7.2, 6.8.6 `+=` and `-=` on every map and set, calling the members: maps `m += (key, value)`, `m += other`, `m -= key` (`removeAt`), `m -= s` (a set of keys, removed with their values); sets `s += v` / `s += other` (`add`), `s -= v` (`remove`), `s -= other` (in-place difference); `|=`, `&=` and `^=` removed
- 6.8.6 `set` and `hset` can be mixed in every set operation; the result has the kind of the left operand (was: same kind only, a compile error); maps of different kinds stay a compile error
- 6.8.6 New sets (`union`, `intersection`, `difference`, `symmetricDifference`) copy basic items and strings and clone objects; sets only
- 6.6, 6.8.6 `add(collection)` on sets takes any collection with the same item type; objects cloned, or moved with `->other`
- 6.6, 6.7, 6.7.3 `->s[item]` on sets is valid (`move_out(Item k)`); `swap` is not defined on sets
- 6.7, 6.7.3 `SetIterator<Item>`: `next()` returns `(Item value, bool isValid)`, no `writableNext()`; the cursors of `map` and `hmap`
- 1, 6.3, 6.8.6 Set literals are an empty set plus the adds; `set s = [...]`, `hset s = [...]` and `hmap h = [k: v]` take their types from the literal
- 6.9, 99 `+=` and `-=` of a custom collection come from its members; the open items on the sets and on `|=` / `+=` removed
- 1.1 A name that starts with `_` or with a digit is a compile error (stated explicitly); `_` before a type is the weak marker
- 12, 12.6, 19.3 Weak declarations (new): `_T name` declares a weak field, variable or parameter (was: only `b _= a` made a weak reference); it starts as null, is set with `_=`, and a plain `=` into it is a level 3 warning
- 16 Namespaces: every file is in the namespace of its folder (root namespace + folder path, written in full or as `local`; was: any explicit namespace); folder names match case-insensitively; `import` brings in the files of the folder of the namespace, looked for in the stdlib, the program and the libraries listed with `include=` in the project file; only the files reached through imports are compiled, plus, in a multi-file compilation, the other files of the folder of the root file (a single file ignores them); the root file of a multi-file compilation needs its namespace line; overlapping roots are a compile error, the stdlib included (no library inside `Root`); root files: a single file is its own root, a program has `main.df` or `program.df`, a library `lib.df`; only the root file has top-level code; a single-file compilation imports only its subfolders and the stdlib, and ignores `include=` with a warning
- 15.9 C functions (new, minimal): `extern fun` with no body at the top level; numbers, `bool` and `addr` as they are, `string` as `const char*` during the call, `arr<T>` as `T*` and a length; returns a number, `bool`, `addr` or nothing; never an error; 99 C interop item narrowed
- 11.1.4 `readonly` on a class field without accessor blocks is a shorthand for a public reader and a private writer
- 11.1.4.1 Field groups (new): `pos { r32 x, r32 y }` organizes fields inside a class; `s.pos.x` is a plain field (so `s.pos.x++` works); the group is not a value; a tuple can be assigned to it, and reading it gives a tuple copy

## Changes in 0.0.19

- 12.7 Reference counting and weak references (new, the Rust model): `@T` read-only counted object (`Rc<T>`), `(@)T` read/write counted object with a lock, `_T` weak reference (`Weak<T>`); the counting system owns the object and the holders are users with one usage each; the object dies at count 0, final; counting declared only by the type or `var x = @T()`; `@plain` starts counting (plain is lost), `@Dog rc = plain` is an error, `@this` is an error; counted objects cannot be borrowed (`Dog v = first`, `&Dog v = first`, `(&)` are errors); `!` makes a new counted object from 1; `@T` / `(@)T` parameters take a usage, `f(->rc)` gives it away; `->a` only when the code owns `a`; a method call is a usage; returning `@Dog` passes the usage; release at scope end, `x is done.` early, the released variable becomes null; `removeRefCounting()` (null when other users exist); `usersCount`, `weakrefsCount`; weak references only on counted objects, made with `_x` (`_Dog w = _first`), `=` assigns, used through `enforce`; `@T` fields are users, `_T` fields weak, `&T` fields not allowed; `list<@Dog>` holds a usage per item, the loop variable is a temporary usage; the lock of `(@)T` with `enforce`; cycles as in Rust with a level 1 warning (was: `.asRefCount`, `.asSharedRefCount`, `.isRC`, `ChangingRefCountTypeException` for switching kinds, `=` adding an owner)
- 12 Borrows follow Rust: a borrow locks the object (no release, move or giving away while it lives), a borrower cannot release, move or give away, a borrow cannot outlive its lender (stored, returned, handed to a fire-and-forget call: compile error); the table has `@T`, `(@)T`, `@a`, `_T`, `_a`, `is done.`
- 2.5, 2.5.1, 3.1.1 The bitwise not is `~x` (was `_x`); `_=` removed; `@` and `_` are prefix operators
- 6.2 Counted objects and weak references cannot be keys or set items
- 7.2 `enforce` on a weak reference holds a temporary usage, on a `(@)T` its lock
- 12.4 `is atEnd.` is only for plain owners; on a counted or weak variable it is a compile error (`is done.` releases them)
- 12.6 A counted field in a clone is another user of the same object, never cloned recursively; `a!` on a counted variable makes a new object counted from 1
- 13.1 Parameters `@Item` (a usage, read-only; `->o` gives the caller's usage) and `(@)Item` (read/write); a counted argument to a plain, `&` or `(&)` parameter is a compile error
- 19.3 New level 1 warning: possible cycle of counted objects; removed the level 3 warning for `=` into a weak reference
- 99 Open items for ownership and reference counting added
- 12.7 `var x = f()` is counted when `f` returns `@T`; `removeRefCounting()` also on counted fields; the compiler may leave out the count of a method call and of the loop variable over `list<@T>` when another usage keeps the object alive; a write to a `(@)T` without the lock raises `WritingOnRefCountWithoutLockingException` (new, 15.3); 99: the name of that exception removed
- 12.7, 19.3 The cycle warning also covers a class with an `@` field of its own type

## Changes in 0.0.20

- 12.6 A weak field of a clone is another weak reference to the same object; the remapping of weak references into the clone tree is removed (a weak reference points only to a counted object, which a clone never copies)
- 12.7 The search members of a collection of counted objects take a counted object or null and compare by the rules of the objects; the members that give an item pass its usage, and a counted item needs its type written (`@Dog old = pack.replace(...)`, not `var`)
- 7.2 `enforce` rewritten: the checks (null, alive, locks in variable order), the clauses `null [on ...]`, `lockfail [on ...]`, `catch E [ex] [on ...]` and `atEnd`; a failing check skips the whole `enforce` (only the first matching clause runs, quietly nothing without one); the clauses after the block run top to bottom, a pending exception skips the `atEnd` clauses until a catch handles it, the last `atEnd` always runs; locks and usages are released at the very end; `return` allowed, `break` / `continue` / `jump` out of the block are an error; `else` only alone; the expression form `enforce x.f()` (zero or null when a check fails); 12.7 updated to match; 99: the open items of `enforce` replaced by what is left
- 14.2 A declaration can carry its own `atEnd` (`Some a = Some() atEnd { }` is `atEnd of a`)
- 7.2 `default { }`, the catch-all clause of `enforce` (a failed check without its clause, an exception no catch matches; released after it); 7.2.6 the expression form rewritten: the variables of the expression are enforced, with the clauses of the statement form, the values of the clauses (a single expression or `return`), compatible types, the equivalence with the statement form
- 15.1, 15.2, 15.5, 15.7 The members of `Exception` are camelCase: `id`, `message`, `function`, `line`, `stackTrace` (were `Id`, `Message`, `Function`, `Line`, `StackTrace`)
- 12.6 Example: `Dog other = dog.tryClone()` (was `Dog?`: objects are nullable already, `T?` on them is a compile error, 7.1)
- 15.3 ID 16 retired (was `ChangingRefCountTypeException`, no use with one kind of counting); IDs 49 `ObjectNotClonableException` and 50 `WritingOnRefCountWithoutLockingException`; 99: the open item on `ChangingRefCountTypeException` removed
- 12 A new object that nothing keeps ends when its statement ends (an argument to a borrowing parameter, a receiver); a borrow of it is valid only in that statement; 99: the open item removed
- 7.2.7, 99 `enforce w is Blah b { }` dropped (no longer under discussion)
- 6.8.1 Array ownership written out: the owner of a new array (also `var x = [...]`), borrows (`f(a)`, `b = a`, the standard library), `return a` moves, `->a` only from an owner, array fields own their array (a new one, `->a`, or a copy when the items are plain; objects need `->a` or `a!`), the old array of a field ends
- 6.8.1 Arrays: an owned local assigned again ends its old array; `->arr<T>` parameters own the array (`f(->a)` to a plain `arr<T>` parameter is a compile error); static array fields end with the program
- 6.8.1, 3.2 Arrays in collections (owned like fields, `c[i]` borrows in place, removed and replaced arrays end, `takeAt` / `replace` / `pop` give them to the caller, `fill(a)` is an error) and in tuples (a tuple carries its own copy of its arrays, also a `var` tuple); a `->arr<T>` parameter copies an array of plain items; the arrays of several return values are the caller's own
- 8.1 Number formats rewritten with an explanation and examples for each option; with a `0` fill the sign goes before the zeros (`-0005`, was `000-5`)

## Changes in 0.0.21

- 12.7 `fun (@)T` results (a read/write usage; `var x = f()` is `(@)T`; an `@T` result cannot become `(@)T`) and `list<(@)T>` / `vec<(@)T>` (read/write items, the members that give an item give a read/write usage, the loop variable is enforced for the lock of each item)
- 12.7, 12, 7.2.1, 7.2.2, 13.1 `(_)T`: a weak reference to a read/write object, made only from a `(@)T` or a `(_)T`; through `enforce` it takes a read/write usage and the lock (with `timeout`); `_T` stays read-only also on a `(@)T`; a `(@)T` source is needed for every read/write target (variable, field, parameter)
- 7.2.1, 7.2.2, 12.7 Lock timeouts: `@Player() timeout 100` sets the default wait for the lock of a `(@)T` (milliseconds; 0 when not written: tried once); `enforce p timeout 500 { }` sets it for the variable just before it and wins over the default; no infinite wait; the waiting `enforce` checks its timeout, the waiters are served in order, a timeout is a lock failure (`lockfail` / `default`); every change of the lock state is atomic; 7.2.7: `timeout` in the expression form is open

- 6.8.1 Arrays: a view `a[x..y]` borrows (the array still ends with its owner); a local made a borrow by `a = b` still ends its own old array, and owns again a new array assigned later; tuples inside tuples carry their own arrays; a new tuple nobody keeps ends its arrays with the statement; a result nobody keeps ends its new arrays at once; an existing owner receiving an array from several return values (`dogs, count = pack()`) ends its old one

## Changes in 0.0.22

- 13.3 Lambdas rewritten: parameter types always written, captures after the `;`, a lambda called like a function (also where it is written); the old examples removed
- 13.3.1 Counted captures `@a`, `(@)a` (writes through `enforce a`), `_a`, `(_)a`, and `->a` of a counted object (its usage moves); an unlisted counted object is a compile error; constants need no listing and give no warning (the level 1 warning of 19.3 is removed); no `this`: fields are captured by their bare names and borrow the object; inside the body the captures are used as any variable (ownership passed on only when the lambda owns the object)
- 13.3.2 Function types `<return type | fun>(<parameter type>, ...)` with the parameter modes of 13.1 (`string((&)MyObj)`); nullable by default; calling a null one raises `UnassignedFunctionCallException` (15.3, ID 51, a child of `NullPointerException`)
- 13.3.3 A lambda is a function pointer with a context: `f2 = f1` shares the context and its state, `f1!` makes a new context (owned captures cloned); `f1!` with a `(&)` capture is a compile error
- 13.3.4, 13.3.5 A `(&)a` capture blocks `a` until the last use of the lambda; storable lambdas have owned or counted captures; the implementation as `{ code, context }` with creation and end code, the context on the stack when the lambda does not escape
- 6.6 The `sort` lambda returns an int; only its sign counts
- 13.4 `logAll`, `r.Console.log` (camelCase)
- 11.1.1 Every secondary constructor reaches the primary one (compile error otherwise); a cycle of constructor chains is a compile error; field initializers run before the constructor body; a chain names one constructor of the class or one of the parent, not both; without one the parent's constructor without parameters runs (none: compile error); the same for the constructors of the traits, which can have overloads and parameters; a constructor without a visibility keyword is private
- 11.3.5, 19.3 `a<b>(c)` is read as a generic call without a warning (the level 3 warning is removed); when `a` is not generic, the error shows the form with parentheses
- 11.4 A destructor fragment is registered when its constructor starts its own body (the outer constructor of a chain runs its fragment first); it cannot keep a borrowed object parameter; its locals are declared with their type

## Changes in 0.0.23

- 13.1, 13.1.1 Passing markers redone: a plain parameter or result lends read-only (native values included; the compiler may copy them), `&` means "you may write" and combines (`&T`, `&@T`, `&%T`, `&*T`), `->` gives, `x!` gives a copy, `@` shares, `%` is weak, `*` is a raw pointer; the call repeats the marker of the definition (`f(&x)`, `f(@x)`, `f(%x)`; a new object, a result and `null` need none); tables with the need, the lifecycle, the definition, the call and an example for each (was: `&T` read-only, `(&)T` / `(&)x` writable, `(*)T`, `(@)T`, `(_)T`, `_T`, `_x`, counted arguments without `@`)
- 13.1 Replacing through `&`: the old value ends and the caller owns the new one; `&x` only on what owns its value (an owning variable, a field, an array item, a `&` parameter); `&this` is a compile error
- 1.3 A real needs a digit on both sides of the dot (`0.` is a compile error); removed from 99
- 6.6 `clear()` on maps and sets removes all the items (confirmed, built); removed from 99
- 13.1.1 `&T` results built for objects, collections and arrays: they lend this, a field, an item, a `&` parameter, a global or another `&T` result; a read-only parameter, a `const` or a new value is a compile error; `->&T` is a compile error
- 12 While a borrow is alive, the fields of its lender cannot be written either (Rust style); reads and method calls stay allowed
- 12, 13.1.1 Borrow locks built inside a function (by statement order, until the last use of the borrow); a plain result that lends from a local, or from a local that borrows from one, is a compile error; the string and array results of the members of the runtime (`s.trim()`) are new values
- 13.1.1 `->` results built: a plain result that returns a new object, string or array (or a local that owns one) is a compile error that asks for `->`; `->` on a basic result is a level 1 warning (19.3); an `R` accessor needs no `->`
- 13.1.1 Return values: a plain result lends read-only (from `this`, a lent parameter or static data; from a local it is a compile error that asks for `->`), `->T` gives the ownership, `&T`, `@T`, `&@T`, `%T`, `*T`, `&*T` as for parameters; native values are returned as values (was: returning always moved the ownership)
- 12.1 Pointers: `*x` makes a read-only pointer, `&*x` a writable one, `&*T` is the writable pointer type; `*` on a literal or a `const` points to static data (`fun *string getVersion() { *"1.2.3" }`)
- 12.7, 7.2, 13.3 `(@)T` is now `&@T`, `(_)T` is `&%T`, `_T` / `_x` are `%T` / `%x`; `@x` on a counted `x` gives a new usage and marks the call (`keepDog(@first)`, `pack.add(@first)`)
- 1.1 A `_` before a type is no longer the weak marker (names still cannot start with `_`); 7.2.6 example uses `b`
- 12.3 A volatile parameter is written `volatile T` and is never copied
- Examples updated to the new markers: 1.6, 2.5, 3.2, 6.4, 6.6, 6.7, 6.8, 6.9, 9, 10.3, 11.1.2, 11.1.4.1, 11.2, 11.3.2, 11.3.10, 12, 12.4–12.6, 14.2, 15.9 (`&arr<T>` for a C function that writes the items), 99

## Changes in 0.0.24

- 4.4, 4.4.6 C strings (new): class 12; `string(addr)` borrows a C text and never frees it, `string(->addr)` takes it and frees it with `free()`; read-only, the first change copies it into a regular string; an empty text is `""`
- 7.1 `string?` (new): null is not `""`; converts to and from `string` (null becomes `""`); `==`, `??`, `??=`, `?.`, `enforce`; prints null as `null`; the variable owns its string, a parameter borrows it read-only, a result is owned by the caller; built for variables, parameters and results only
- 4.4.6 `string(addr)` and `string(->addr)` give a `string?`: a null `addr` is null
- 1.6.2, 13.1 Plain parameters of objects, collections and arrays are read-only lends (built): only `unrestricted` methods, `getIterator()`, the members of the collections that do not change them and the `File` members that do not change its path; fields and items cannot be assigned, `&x` of a plain parameter is a compile error
- 15.9 A returned C string is an `addr` given to `string(...)`; removed from 99 (returned strings)
- 13.3.6 Lambdas and function types built in part (new section, the examples are now 13.3.7): what is built and what is not; a function-type result is written `fun ->fun R(P) name()`; a borrowing lambda cannot be assigned to an existing variable or returned until borrow lifetimes are checked
- 15.3 `UnassignedFunctionCallException` (ID 51) built
- 6.6 `sort(lambda)` built on `arr`, `vec` and `list` (any item type, objects included); the example uses typed parameters and an int result (13.3)
- 6.6 Functional members (new): `filter(bool(T))`, `map<U>(U(T))`, `reduce(A start, A(A, T))` on `arr`, `vec` and `list`, giving a new collection of the same kind (filter, map) or a total; removed from 99
- 13.3.6 Built: the fields of the object inside a method (bare names; `this` in a lambda is a compile error), `a!` of objects and arrays, `->a` of arrays
- 13.3.6 Exceptions leaving a lambda built: they go back to the code that called it, as for functions; in `sort`, `filter`, `map` and `reduce` they stop the work and are raised after it
- 7.1 `string?` in fields and in the items of `arr`, `vec` and `list` (built): owned like `string`, freed with the object or the collection, copied by a clone; a loop variable over `string?` items is read-only

## Changes in 0.0.25

- 11.1.5 Structs defined (new section, was "to be defined"): values with methods and no constructors, properties, traits, inheritance or generics, never nullable; fields always public, declared only in the body; creation by name (any subset, the others take their start value or zero) or by position (all the fields); read-only access as in 1.6.2, an `unrestricted` struct method cannot assign a field of `this`; copying clones the objects inside (`=` and `!` are the same); inline buffers `arr<T>[N]`, no `arr` of runtime size; a local struct over 64 KB goes to the heap; layout tags `<fixed>` (C alignment, 4-byte `bool`) and `<compact>` (no alignment, 1-byte `bool`) with plain values only; `.sizeof`, `.offset(field)`; transmute `[T]x` on a struct is live and writable; assigned to a field group, converted to a tuple; `==` field by field, `.hash` as tuples, `toStr()`; removed from 99 (definition, C struct layout)
- 2.5.1 `.` removed from level 2 of the precedence table: it binds tighter than casting and transmute
- 11.1.4.1 A struct with the same structure can be assigned to a field group
- 6.2, 12.6 Structs as keys and in the default `clone()` refer to 11.1.5
- `[Ref: ...]` tags replaced by Markdown links to the headings
- 11.1.5.2 Static functions of a struct: called with the name of the struct, never through a value
- 15.9 Structs with a layout tag passed to C functions by value or by address (`*S`, `&*S`) and returned; removed from "Not yet"
- 11.1.5.7 Transmute: the view is never larger than the original; numbers on either side; no structs with strings or objects; writing needs a place
- 13.3.6 Built: function values in fields, arrays, vecs, lists and tuples (only values that do not borrow), `f1!`
- 6.5 `c[a..b]!` built: objects cloned, counted objects shared (one more user)

## Changes in 0.0.26

- 12.7 `->rc` into a `->T` parameter or a plain receiver is a compile error that points to `removeRefCounting()`; `->pack[i]` on counted collections moves the usage to a counted receiver
- 12.7 Counted value types and strings (new): `@i32`, `&@i32`, `@string`, `&@string`; a new variable or parameter shares the cell; `=` on a `&@T` value writes the shared cell (with the lock), on a read-only `@T` it gives the variable a new cell with a copy (automatic lock from a `&@T`, the zero value or null when it fails); reads without the lock give no guarantee on the value; a `&@string` is read only inside `enforce` and is always copied (compact) into a `@string`, never shared; 13.1 updated
- 12.7 Counted objects through trait types (`@Animal a = @Dog()`); a type check keeps the marker (`@`, `&@`, `%`)
- 12.7 A weak reference needs `%` in the expression (`%Dog w = first` is a compile error); through `@T` only `unrestricted` and static methods, through `&@T` every public method holding the lock
- 11.1.1 The primary constructor rule covers objects and collections; a string or an array parameter gives its field a copy
- 11.1.1, 19.3 A plain primary parameter of an object used only during the construction is a read-only lend, not a field; used as a field it is given implicitly (level 1 warning), and using the argument after the call is a compile error
- 99 Reference counting: the open points of 12.7 settled (the call form of `&@T` was already in 13.1)
- 3.2 Objects in tuples (new): the tuple owns them as a struct does (a copy clones them, a new tuple is taken as it is, they end with the tuple); an object that cannot be cloned makes a copy a compile error; a counted item is one more user on a copy
- 13.1.1 Counted values among several results (`fun @Dog, i32 pick()`), kept by counted targets; a counted target takes a given object (`->Dog`) and starts counting it
- 6.6 Maps of objects: `add` takes the value, `m[k]` lends it, `takeAt` gives it, a replaced or removed one ends; `m.values!`, `add(other)`, `clone()` give clones; `map<K, @T>`
- 6.6 `tryGet(k[, default])` gives `bool, value` (was `tryGet(k, &out[, default])`): an object is lent, read-only (`Dog d`) or read/write (`&Dog d`); while it is used the map cannot change (12); `&Dog d` is valid as a target of several results that lend read/write
- 12 While an item of a collection is borrowed (`Dog d = c[i]`, `tryGet`), the members that change the collection are compile errors until the last use of the borrow
- 1.6.2 `unrestricted` is checked (no field of `this` written, only `unrestricted` methods called on `this` and on what it owns); the visibility order; borrowing locals are read-only
- 11.1.4 Counted properties: `R:` gives a usage, `W:` takes one, a read in an expression takes a temporary usage
- 13.1.1 While a local borrows from an object only its `unrestricted` methods can be called; `&Dog d = ...` read/write borrows; `&T` results of values (numbers, strings, structs: the address of the place); a lend from a `&@T` only inside the `enforce` that holds its lock
- 6.6 `clear()` on `list` removes all the items (as `truncate(0)`); on `list`, maps and sets it is forbidden while iterated (6.4); `arr` and `vec` keep the size
- 11.2.1, 15.3 `ModifiedObjectUsedAsKeyException` (ID 52)
- 11.3.12 The limits are per kind of collection, one shared pair for the custom collections (the option names in Compiler_Options.md)
- 13.4 Variadic parameters defined: the last parameter, a read-only `arr<T>`, no collections as `T`, an existing `arr` or `vec` lent as a whole
- 99 Removed: variadic parameters, the name of the `Keyable` exception, the names of the limits
- 12.4 Use after a move: a compile error when the move is certain (same block, before the use); a branch or a loop does not count; `x == null` is allowed
- 12.7 `list<&@T>` of values: = and the compound operators on an item write its shared cell; static counted values; 13.1.1 `&@T` among several results; extern functions refuse counted parameters and results

## Changes in 0.0.27

- 3.1.1 Bitfield methods: a call whose result is used gives a new value, a single call written alone changes the variable in place; a chain written alone changes nothing (no error, no warning); in a read-only place the form written alone is a compile error; `Flags?` is a nullable bitfield (7.1)
- 4 A field shares the string it is given (`k.label = a`); `a!` or `a.shrink()` stores a copy; when the original ends first the field gets its own copy, with a level 3 warning; collections store shrunk copies (6.1)
- 7.2.6 `enforce timeout 500 expr`: one timeout for every lock of the expression form
- 11.1.4 Property modifiers in any order, on the property (both accessors) or before one accessor; `readonly` with a `W:` block (the class can still write); `override` replaces the whole property
- 11.2 A property read through a trait type runs the accessors of the class of the object
- 11.3.6 Sets declared inside a class; a set as declared type argument holds set values (`arr<Num> a = [12, 12.0]`); 11.3.8 a set is nullable when one of its members is
- 12.4 The level 1 warning of `->` on a basic type fires in generic code only when every type that can reach it is basic
- 15.3 The default messages keep the word "Exception"; 15.5 panic exit codes stay -1000 until 0.1.0
- 99 Removed: the decided items (bitfields, properties, generics, borrowed strings, standard messages, class destructor that throws, `enforce` timeout) and the items already built (borrows of items, the end of a local borrow, `&T` results, counted properties, `unrestricted` checks)

## Changes in 0.0.28

- The keyword `override` is renamed `replace` everywhere (methods and properties, from a parent class or a trait); `override` is no longer a reserved word (11.1, 11.1.4, 11.2 and the examples)
- 11.2 A trait property without accessor blocks is a simple field in the class (nothing to implement, no `replace`); a class can replace a trait property (`field` is the storage of the trait, the replacement has the final word); a property no class replaces is a direct memory access, otherwise (and in compiled libraries) a call

## Changes in 0.0.29

- 11.3.6.1 A set value never assigned holds the generic null (also for sets with no nullable member): it prints `null`, `is` is false, a type switch goes to `default` or raises `UnassignedTypeException`; `k == null` is always false on a set with no nullable member, `k = null` is a compile error there; printing a set value calls the `toStr` of its member type
- 15.3 `UnassignedTypeException` (ID 53)
- 11.2.1 The collection owns its object keys (6.6): a new object or `->x` goes in, a lookup borrows, `->s[item]` gives the object back, the copying paths clone; the example that kept using a key after giving it is replaced; the fields of `Keyable` are not in the default hash, a clone or the text of the object
