# Dflat Language
A new language D♭ that is the same as C# but easier to write and faster to execute

**Version** 0.0.19  2026/10/30 by CPU

## D♭ — Dflat Language Specification (draft)

Status: design in progress. Decisions below reflect the latest choices; earlier alternatives are omitted.

---

## Abstract

- **Name**: Dflat. Logo: **D♭**. Source extension: **`.df`**.
- **Position**: between C# 12 and Rust. Short, clean, modern C-style syntax; no legacy C# compatibility; no Rust lifetime annotations or pedantic syntax.
- **Goals**: speed and simplicity, a C++ replacement. Also targets very small devices (micro-controllers, C64-class machines).
- **Inspiration**: semantics and memory model from Rust; syntax from C# and Dart.
- **Planned**: native Entity Framework syntax, Vulkan integration (not yet designed).

---

# 1. Basic syntax

## 1.1 Lexical rules

- **Comments**: `// line` and `/* block */`.
- **Statement end**: JavaScript rules. A line break ends a statement when the statement can end there; `;` is needed only for several statements on one line. Is not a syntax error to add `;` when not needed.
- **Identifiers**: have to start with a letter and can be composed of letters, numbers, and underscores. They are case sensitive. A name that starts with `_` or with a digit is a compile error (`string _secret`, `i32 9lives`); underscores inside names are fine. A `_` written before a type is the weak marker (`_Texture2D texture`, 12), never part of a name.
- **Naming rules**:
  - Built in types (`i32`, `string`, `arr`) are lowercase
  - Types (classes, traits, enums): PascalCase; otherwise level 2 warning
  - Methods: camelCase is recommended but not enforced. Warning only if the same function name is defined with different capitalizations.
  - The non-public methods that implement properties and operators (`get_len()`, `read_pos(k)`, 6.7) are snake_case, to keep them apart from the regular methods (provisional rule).
  - Constants declared with `const`: UPPER_SNAKE_CASE (`const u64 SECRET_CODE_PHASE2`); otherwise level 3 warning. Generic value parameters are not constants (`class Matrix<u64 Rows, u64 Cols>`).
  - Static properties (stdlib constants): UPPERCASE (`Math.PI`).
  - Variables, parameters, fields, properties, enum entries: no imposed case (camelCase suggested for variables and parameters).
  - `Root` is a global static class giving access to the stdlib; always written `Root`, can be aliased.
- **`...`**: variadic parameter suffix; no other use.
- **Reserved words**: `as` (used in `alias` [Ref: Namespaces]; other uses to be defined), `this` [Ref: Classes], `atEnd` [Ref: atEnd].

## 1.2 Labels
Labels are used as target for jumping around (including controlled jumps like break and continue)
- They are defined as `#name#`: starts and ends with `#`, no spaces, at least one letter.
- Allowed characters: letters (Unicode included), digits, `_ - + ! ? $ %`.
- Labels are allowed anywhere in the code

### 1.3 Number literals

| Literal | Type |
|---|---|
| `123` | native signed integer (64 bits on x64 arch, 32 bits on x32 archs, 8 bits on 8 bit archs) |
| `123u` | `u32` |
| `123l` | `i64` |
| `123ul` | `u64` |
| `123s` / `123us` | `i16` / `u16` |
| `123b` | `byte` (that is an alias of `u8`) |
| `1.5` | native real |
| `1.5f`, `2f` | `r32` |
| `6.022e23` | native real |
| `0x1F` | hex; size = smallest type holding the digits (1–2 → 8 bit, 3–4 → 16, 5–8 → 32, 9–16 → 64), zero-filled, widened further if needed; signed; `u` suffix for unsigned |
| `0b1010` | binary, same rules as hex |
| `1_000_000` | `_` digit separator |

- Suffixes are case-insensitive.
- Unsuffixed literals take the native size of the target (or the size forced by the native-size compiler option [Ref: Compiler]). A value that doesn't fit takes the smallest wider type that does (`300` on an 8-bit target → `i16`).
- Reals fit by range only (precision loss accepted). The target architecture decides the best native real format (for example `r64` on 64-bit, `r32` on 32-bit, `r16` on 8- and 16-bit, or software emulation where faster).
- Literals adapt to the declared type; a value that doesn't fit is a compile error.
- `0x00000000DEADBEEF` is a 64-bit value; hex and binary always describe raw bits.
- A literal transmuted [Ref: Conversions] to a pointer is zero-extended to the pointer size: `[*i32]0xA0000`.

## 1.4 Characters and strings

- Chars: `'a'`. Strings: `"text"`.
- Escapes: `\n \r \t \\ \' \" \0`, code points `\u{1F600}` (hex) and `\#{123}` (decimal).
- An invalid code point in a literal (surrogate, above `0x10FFFF`) is a compile error.
- Interpolated strings: `$"Hello {name}, total {x:D,02}"`; `\{` and `\}` for literal braces [Ref: Formatting]).

## 1.5 Declarations and namespaces

```
i32 a = 10
const i32 LIMIT = 100
var count = 10                      // type inferred
i32 id, string name = getUser()     // multiple return values
var userId, var userName = getUser()
existing, _, i32 fresh = f()        // mix existing, discard, new
x, y = y, x                         // swap: right side evaluated first
i32 total, string name, bool enabled = record    // tuple destructuring
i32 onlyCount, _, bool onlyActive = record       // partial, _ skips an item
```

- Type-first; C#-style scope.
- Definite assignment: reading a variable before it is assigned on every path reads its zero value (null for objects) and gives a level 1 warning.
- No shadowing of locals: a variable with the same name as a local of an enclosing block is a compile error.
- `const`: read-only value and pointer, global guarantee.
- `var`: type taken from the initializer; if it can't be decided → compile error (`var x = null`, `var l = []`). Allowed in multiple returns when the return type is known. Coexists with partial forms like `arr a = [...]` / `vec v = [...]` / `map m = [k: v, ...]` / `hmap h = [k: v, ...]` / `set s = [...]` / `hset s = [...]`.
- A tuple or literal must be assigned, passed or returned; alone it's a compile error.
- Tuple destructuring: the items of a tuple can be unpacked into separate variables in one statement; `_` skips an item.

## 1.6 Accessibility rules

A few keywords are used to define who can call and use part of the code. Mostly classes, properties, and methods.
It is important to note that 2 special cases are used to decide who is allowed to see it (visibility), and what kind of reference the caller holds (owned, writable `(&)` or read-only `&`).

### 1.6.1 Visibility

| Keyword | Who can use the member |
|---|---|
| (none) | private: only the class itself (default); in a trait, only the code of the trait and, read-only, its sub-traits (11.2) |
| `asparent` | the object itself and its children, through their parent reference (12.5) |
| `internal` | the namespace and its sub-namespaces |
| `public` | everyone |
| `unrestricted` | everyone, and it includes `public` |

- The visibility keyword goes before `static`, `fun` or the field type. Only one visibility keyword can be used on a member.
- `asparent` is not public. It can change the parent, because only the object itself and its own children can call it.
- An overriding function takes the visibility of the original: none is written on it (11.1.2).
- A subclass sees everything when the source of the parent is available, public only for compiled libraries (11.1.2).
- Constructors have a visibility; a private constructor can be called only by the class body (11.1.1).
- A visibility keyword before `R` or `W` sets the visibility of that accessor; the property visibility applies to the other one (11.1.4).

### 1.6.2 Access through a reference

- Owned variable or writable reference `(&)`: all the members visible to the caller can be used.
- Read-only reference `&`:
  - properties can be read; assigning to them is a compile error;
  - a `public` method cannot be called;
  - an `unrestricted` method can be called;
  - an `asparent` method can be called by the object itself and by its children (12.5).
- `unrestricted` is meant for methods that do not change the object or where the developer takes responsibility. The compiler does not perform checks.


# 2. Built-in types
These are the default types available with the language. In some restricted architectures they may have limitations or being not available.

All types support `.toStr()` to be converted to `string`.
For classes, if not overridden, the default is: class name and the values of the first 3 fields, one level only. `Px { a: Py{}, b: 3, c: BTree{}, … }`

## 2.1 Numbers

| Type | Description |
|---|---|
| `i8` `i16` `i32` `i64` `i128` `i256` | signed integers (sizes not supported in hardware are software-emulated; on very small targets, types wider than 4 bytes may not be available at all) |
| `u8` `u16` `u32` `u64` `u128` `u256` | unsigned integers |
| `r16` | IEEE 754 binary16 (GPU half) |
| `r32` `r64` `r128` | IEEE floats (`r128` software-emulated) |
| `dec` | fixed-point, 32 + 32 bits |
| `byte` | alias of `u8` |
| `int` `uns` `real` | target-dependent aliases, to force a specific size for global data or registers; not allowed in `dfoot` |

- **Native size**: one compiler option sets the native size used by `int` / `uns` / `real` and by unsuffixed literals. It can force a size below or above the CPU's own. [Ref: Compiler]
- `uns` has the size of `int`: the cpu size by default, or the size forced by `--native-int` (19.1). The lengths and indexes of the collections use it, and so does `.hash` (6.2). A `uns` smaller than the cpu size limits the length of the collections and the size of `.hash`: the compiler gives a warning (19.3).
- Numbers are **not nullable** unless declared `type?`.

## 2.2 Scalars

| Type | Description |
|---|---|
| `bool` | true / false |
| `char` | 32-bit Unicode code point |
| `date` | instant: 64-bit, ms since 1 Jan 1970 UTC; a null `date` is represented by the minimum `i64` value |
| `time` | time of day, millisecond resolution; no date, no zone; stored as an `i32` (ms since midnight, max 86,399,999); a null `time` is represented by `-1` |
| `clock` | u64 monotonic counter, 0.1 ns, from `Clock()`; intervals only; `Clock()` never returns 0; null = 0 |
| `lapse` | difference between two dates or two clocks (3.4) |
| `addr` | native type holding an address as a number; size from the target definition (64-bit by default, 16 on C64), stored in the most efficient way; comparison and transmute only, arithmetic through pointer methods (12.1); cannot be dereferenced; prints as hex in groups of 4 digits separated by spaces; null is 0 (unmapped address) |

## 2.3 `addr`
Used to define a memory address.
It is a native type holding an address as a number; It's size comes from the target definition (64-bit by default, 16 on C64), stored in the most efficient way.

It supports only comparison and transmute, arithmetic is done through pointer methods [Ref: Pointers]; cannot be dereferenced.

It prints as hex in groups of 4 digits separated by spaces; null is 0 (unmapped address)

Converting an `addr` to a pointer results in an `unsafe` pointer.

## 2.4 Composite types

| Type | Description |
|---|---|
| tuple | an ordered set of different types accessed by order and/or by name [Ref: Tuples] |
| `string` | UTF-8 text [Ref: Strings] |
| `dfoot` | Dflat Fast Open Objects Tree, used as serializable tree of objects (similar to JSON) [Ref: Dfoot] |

- The collections `arr`, `vec`, `list`, `map`, `hmap`, `set` and `hset` are described in section [Ref: Collections]
- GUID is defined by the EF layer, platform-specific.
- Lengths and default indexes are `uns` everywhere (2.1, 6.1).

## 2.5 Operators

| Kind | Operators |
|---|---|
| arithmetic | `+ - * / %` |
| bitwise | `&` `|` `^` `~x` (not) `<<` `>>` |
| logical | `!` `&&` `^^` `||` |
| comparison | `== != < > <= >=`, `is` |
| assignment | `= += -= *= /= %= &= |= ^= <<= >>= ??=` |
| increment | `++x x++ --x x--` (not atomic) |
| ranges | `a..b` exclusive, `a..=b` inclusive, `a..`, `..b` |
| null-safe | `?.` `??` |
| ownership | `->x` move, `x!` copy, `@x` start reference counting, `_x` weak reference (12.7) |
| pointers | `&x` address (read-only pointer), `(&)x` writable pointer or writable argument |

- Assignments are not expressions (`++`/`--` are expressions with the usual ++<var> to increment and then get return the value and <val>++ to increment and return the original value).
- `+` evaluates left to right; with a string or char operand it concatenates: `1 + 2 + "a"` = `"3a"`, `'a' + 'b'` = `"ab"`. Char codes need a transmute: `[u32]'a' + [u32]'b'`.
- Overflow wraps unless a specific `catch` for `OverflowException` is in the same function (silent exception, 15.4).
- Division by zero and similar errors are always checked explicitly (no CPU traps).
- Precedence (Rust): postfix, unary, `* / %`, `+ -`, shifts, `&`, `^`, `|`, comparisons and `is`, `&&`, `^^`, `||`, ranges, `??`, `? { }`.
- String vs char: `==` is true only if the string is exactly that one character; `!=` is its opposite (a longer string is always `!=` a char); `<` `>` `<=` `>=` compare the char as a one-character string.
- Char vs integer: compared by code point (`c == 13`), level 2 warning. In `+` a char is always text: `'a' + 1` = `"a1"`.
- Operator overloading: allowed for custom classes, not for built-in types. Overloading `==` on a map key class requires a matching hash.


### 2.5.1 Operator Precedence

| Precedence | Level | Operators Associativity |
|---|---|---
| 1 Highest | () . ?. | Left to Right |
| 2 | () `casting` [] `transmute` . | Left to Right |
| 3	| ++ -- (postfix) `x!` (copy) | Left to Right |
| 4	| ++ -- + - (prefix) ! ~ @ _ -> & (&) | Right to Left |
| 5	| \* / %  | Left to Right |
| 6	| + -	| Left to Right |
| 7	| << >>	| Left to Right |
| 8	| < <= > >=	| Left to Right |
| 9	| == !=	is | Left to Right |
| 10	| &	 | Left to Right |
| 11	| ^	| Left to Right |
| 12	| `|` | Left to Right |
| 13	| && | Left to Right |
| 14	| ^^ | Left to Right |
| 15	| `||` | Left to Right |
| 16 | `..` `..=` (ranges) | Not associative |
| 17 | `??` | Right to Left |
| 18 | ? {} (switch expression) | Right to Left |
| 19 Lowest | = += -= \*= /= %= &= \|= ^= <<= >>= ??= | Right to Left |

# 3. Non basic types

## 3.1 Enums
Named list of values.

```
enum Status { Active, Inactive, Pending: 10 }

enum NPCStatus { Dead, Idle, Fighting, Sleeping, Eating, Walking }

public enum Color Colors {
  Red: Color(1, 0, 0), Green: Color(0, 1, 0), Blue: Color(0, 0, 1),
  
  fun Colors Closest(Color c) { 
    r32 distR = (c.r - Red.r) * (c.r - Red.r) + (c.g - Red.g) * (c.g - Red.g) + (c.b - Red.b) * (c.b - Red.b)
    r32 distG = (c.r - Green.r) * (c.r - Green.r) + (c.g - Green.g) * (c.g - Green.g) + (c.b - Green.b) * (c.b - Green.b)
    r32 distB = (c.r - Blue.r) * (c.r - Blue.r) + (c.g - Blue.g) * (c.g - Blue.g) + (c.b - Blue.b) * (c.b - Blue.b)
  
	if distR <= distG && distR <= distB then return Colors.Red
	if distG <= distR && distG <= distB then return Colors.Green
	return Colors.Blue
  }
}

```

- `enum [ValueType] Name { [values], ... }`

The values are `i32` by default; values are constants (constant objects are allowed); aliases allowed; compared by value; methods are only static.

### 3.1.1 Bitfields

A bitfield is a named set of independent flags stored in one number.

```
bitfield32 Flags { CanRead: 0, CanWrite: 1, CanSeek: 2, IsNetwork: 3 }
```

- `bitfield8`, `bitfield16`, `bitfield32` and `bitfield64` set the size. The number after each flag name is its bit position, zero based; a position outside the size is a compile error (position 31 is valid in a `bitfield32`).
- A bitfield value is considered a number. Flags combine with `|`.
- `.None` and `.All` exist on every bitfield without declaring them. `.All` has only the defined bits set.
- Methods (a flag or a combination of flags):

| Method | Description |
|---|---|
| `set(flags)` | Sets the flags to 1 |
| `clear(flags)` | Sets the flags to 0 |
| `setAs(flags, bool value)` | Sets the flags to the boolean value |
| `toggle(flags)` | Flips the flags |
| `bool isSet(flags)` | True if all the flags are set |
| `bool isAnySet(flags)` | True if at least one of the flags is set |

These methods return the value and do not change the original bitfield variable.


- `~value` (bitwise not, 2.5) flips only the defined bits. Casting an integer to a bitfield masks off every bit that is not defined, so a bitfield never holds undefined bits: `[Flags]~[u32]value` is the same as `~value`.

```
Flags flag = Flags.None
Flags updatedFlags = flag.set(Flags.CanRead).clear(Flags.CanSeek).toggle(Flags.IsNetwork)
Flags both = flag.set(Flags.CanRead | Flags.CanWrite)
```

- Not decided: whether `set`, `clear`, `setAs` and `toggle` modify the value in place or return a new value (the chained example assumes they return a value); nullability of bitfields (7.1).

## 3.2 Tuples
`(string name, i32 age, string address, dec salary)`
A tuple is an ordered set of different types accessed by order and/or by name.

Examples: `(i32, string)` or `(i32 val, string name)`
Tuples can be also anonymous: `(i32 , string, string, r64)`

Tuple need to have **at least 2 elements**; items can be accessed by name (`record.label`) if the names are specified or by index (`record[1]`).
The index is a literal, a `const` or an enum value only; out of range is a compile error. Indexes apply left to right: in an array of tuples, `myTuple[2][1]` is the second item of the third tuple.

An item of a tuple can be a writable reference `(&)Type` (`((&)Item value, bool isValid)`, the result of `writableNext()`, 6.7). The borrow lives as long as the tuple. A tuple that holds a `(&)` follows the rule of the iterators and the views (6.5): if it is not given away it lives only inside the scope that created it, so it cannot outlive the iterator it comes from.


## 3.3 Date and Time
Used to store the dates and times.

- A `date` is UTC based and does not store a timezone. It is a 64-bit integer, in ms since 1 Jan 1970 UTC; a null `date` is represented by the minimum `i64` value.
A `date` is nullable by default (`date?` is a compile error).

- A `time` is a time of day, millisecond resolution; no date, no zone; stored as an `i32` (ms since midnight, max 86,399,999); a null `time` is represented by `-1`.


- Calendar dates and zoned date-times are classes, `CalendarDate` and `ZonedDateTime`, in an optional DateTime library: available only when imported, so programs that don't need them stay small.
- There is no local date-time type: a generic zone used with `ZonedDateTime` covers wall-clock values.

- Subtracting two dates `date - date` give a `lapse`. Two values of the same type can be compared; a null date or clock sorts first.

### 3.3.1 Date Members and Functions

| Item | Kind | Description |
|---|---|---|
| `year`, `month`, `day` | property (read-only) | Calendar parts (UTC) |
| `hour`, `minute`, `second`, `millisecond` | property (read-only) | Time parts (UTC) |
| `dayOfWeek` | property (read-only) | `DayOfWeek` enum |
| `dayOfYear` | property (read-only) | 1 to 366 |
| `timeOfDay` | property (read-only) | The time part as a `time` |
| `unixMilliseconds` | property (read-only) | Milliseconds since 1970-01-01 UTC (`i64`) |
| `DayOfWeek` | enum | `{ Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, Sunday }` |
| `date(string)` | constructor | Uses the [Ref: Parsing] default formats, with or without time. Input with a zone is converted to UTC; input with no zone is UTC. |
| `date(i64)` | constructor | Milliseconds since 1970-01-01 UTC |
| Invalid string | rule | Gives null plus a silent `InvalidDateTimeFormatException` (ID 24) |
| `date.fromUnixTime(i64)` | static method | Date from `unixMilliseconds` (milliseconds since 1970-01-01 UTC) |
| `date.parse(string)` | static method | Forwards to the date formatting rules (default formats) |
| `date.parse(string, format)` | static method | Forwards to the date formatting rules (explicit format) |
| `addYears`, `addMonths`, `addDays` | method | Return a new `date` |
| `addHours`, `addMinutes`, `addSeconds`, `addMilliseconds` | method | Return a new `date` |
| `startOfDay()` | method | Same day at 00:00:00.000 |
| `toStr()`, `toStr(format)` | method | Formats the date [Ref: Formatting] |
| `date - date` | operator | Gives a `lapse` (already exists) |


### 3.3.2 Time Members and Functions

| Item | Kind | Description |
|---|---|---|
| `hour`, `minute`, `second`, `millisecond` | property (read-only) | Time parts, 24-hour clock |
| `totalMilliseconds` | property (read-only) | Milliseconds since midnight (`i32`, 0 to 86,399,999) |
| `time(string)` | constructor | Uses the [Ref: Parsing] default formats: `HH[:mm[:ss[.fff]]]` or `h[:mm[:ss[.fff]]] am/pm` |
| `time(i32)` | constructor | Milliseconds since midnight |
| Invalid string | rule | Gives null plus a silent `InvalidDateTimeFormatException` (ID 24) |
| Out-of-range `i32` | rule | Gives null plus a silent `OutOfRangeCastException` (existing rule) |
| `time.parse(string)` | static method | Forwards to the time formatting rules (default formats) |
| `time.parse(string, format)` | static method | Forwards to the time formatting rules (explicit format) |
| `addHours`, `addMinutes`, `addSeconds`, `addMilliseconds` | method | Return a new `time`. They wrap around midnight and raise no exception. |
| `toStr()`, `toStr(format)` | method | Formats the time [Ref: Formatting] |
| `time - time` | operator | Gives a `lapse` |
| `date + time` | operator | Produces an updated date |
| `date - time` | operator | Produces an updated date |

## 3.4 `lapse`
Used to show an elapsed time span.
Signed 64-bit; raw value = `value × 8 + code`.

| Code (bits 2-1-0) | Meaning | Range |
|---|---|---|
| `000` | null | — |
| `010` | date lapse, ms | ±36 million years |
| `011` | clock lapse, 0.1 ns | ±3.6 years |
| `100` | year lapse | beyond ±36M years |
| `111` | clock lapse overflow | — |

- `date − date` → date lapse; `clock − clock` → clock lapse; `date − clock` → date lapse. `date + time` → date. `date − time` → date. `time − time` → date lapse.
- Mixed units convert to date lapse, rounding away from zero. Null propagates.
- Lapses format as numbers in their own unit.
- There is no `lapse` literal: a number converts implicitly to a `lapse`, in milliseconds, wherever a `lapse` is expected (`lapse l = 1500`, `Root.Time.sleep(250)`, `timeout: 5000`).
  - An integer gives a date lapse (ms); beyond ±36 million years, a year lapse.
  - A real gives a clock lapse (0.1 ns), so the fraction keeps the precision below the millisecond: `0.5` is half a millisecond. Beyond the clock range (±3.6 years) it gives a date lapse, rounding away from zero. NaN gives null.

## 3.5 Conversions
- **Cast `(type)`**: keeps the value; out of range saturates (level 2 warning), or raises `OutOfRangeCastException` if a specific catch exists. From `r`: truncates toward zero (Rust rules), +∞ → max, −∞ → min (0 for unsigned), NaN → 0.
- **To `time`**: a value out of range converts to null and raises `OutOfRangeCastException` if a specific catch exists (silent).
- **Transmute `[type]`**: same bits; truncates low bits or zero-extends. `[bool]`: 0, null, `""`, NaN and `'\0'` are false, anything else is true. `[char]`: invalid → U+0000, or `InvalidCharacterCodePointException` if a specific catch exists.
- **Implicit**: only widening that keeps the value: same family (`i8 → i64`, `r32 → r64`) and unsigned to a wider signed (`u16 → i32`); any scalar type → bool; any type → `string` when the target is a `string` (calls `toStr()`). An implicit conversion is equivalent to a cast.
- **To string**: a null `string` converts to `""`; a null of any other type converts to `"null"`.
- **Pointers**: created from numbers or other pointers by transmute only (`[*u64]address`); pointer → `addr` also with `=`.
- **To bool**: numbers 0 / NaN = false; `char` `\0`; `date` / `time` / `clock` null; `lapse` zero or null; `string` null/empty; collections and tuples cannot be cast or transmuted to bool.

# 4. Strings

```
string a = "hello"
string b = a                 // the same string
b += " world"                // a is "hello world" too
string c = a!                // a copy
a = "other"                  // a is rebound; compile error while b still borrows the old string
str.trim()                   // in place
str = str.trim()             // in place
string t = str.trim()        // a new string, str unchanged
```

- UTF-8; `.len` = chars, `.sizeof` = bytes.
- `""` and null are the same: a null string prints as nothing (`"a" + null + "b"` = `"ab"`); a null value of another type prints `null` (8.1).
- `text[pos]` reads the character at position `pos` (characters, not bytes); `text[pos] = c` writes it in place.
- Every type has `toStr()` to convert it to a `string`.
- Ownership: a string follows the rules of the objects (12). `b = a` is the same string (a borrow), `->a` moves it, `a!` copies it. A change made through `b` (`b += "x"`) is seen through `a`.
- Assigning to a string variable (`a = "other"`, `a = b.trim()`) rebinds the variable, it does not change the string: the old string is dropped by its owner, and a borrow of it that is still alive is a compile error.
- The owner frees the string at the end of its scope; a borrower never frees it.
- `==` compares the content: the same string (same pointer, a moved string included, 4.4.2) is `true`, a different length in bytes is `false`, otherwise the bytes are compared. `<` `>` `<=` `>=` compare the bytes (6.2).
- A string that is being iterated cannot be modified, like the collections (6.4). A block string counts its iterators at run time (4.4.4); for the other strings the check is done at compile time only.
- A string added to a collection (`add`, `insert`, `c[k] = s`, `fill`, literals) is always copied, and the copy is shrunk (4.4.5).

## 4.1 Modifying a string

- `+=`, `text[pos] = c` and `clear()` always work in place.
- The other modifying methods (`trim`, `trimLeft`, `trimRight`, `substr`, `replace`, `toLower`, `toUpper`, `padLeft`, `padRight`, `insert`, `remove`, `replaceAt`, `append`, `prepend`, `Random.shuffle`) work in place when their result is not used (`str.trim()`) or is assigned back to the same string (`str = str.trim()`, `obj.name = obj.name.trim()`, chains included: `str = str.trim().toUpper()`).
- Any other use builds a new string and leaves the original unchanged: `b = str.trim()`, `f(str.trim())`, `if str.trim() == ""`. In a chain the first call builds the new string and the next calls work in place on it (`b = str.trim().toUpper()`).
- A known alias counts as the same string (`b = str`, then `str = b.trim()` works in place); when the compiler cannot prove it, rebinding the borrowed string is a compile error.
- Constant strings (`const`, literals, read-only borrows) are never modified in place: a modification in place on them is a compile error.
- The rule applies only to the methods of `string`; the methods of user classes handle it on their own.

## 4.2 Char functions
Tests follow Unicode categories. `toUpper` / `toLower` are culture-invariant for now (locale: open item).

| Definition | Description |
|---|---|
| `bool <char>.isLetter()` | Unicode letter |
| `bool <char>.isDigit()` | Unicode decimal digit |
| `bool <char>.isLetterOrDigit()` | Letter or digit |
| `bool <char>.isWhiteSpace()` | Unicode white space |
| `bool <char>.isUpper()` | Uppercase letter |
| `bool <char>.isLower()` | Lowercase letter |
| `bool <char>.isHexDigit()` | `0`–`9`, `a`–`f`, `A`–`F` |
| `bool <char>.isPunctuation()` | Unicode punctuation |
| `bool <char>.isSymbol()` | Unicode symbol |
| `bool <char>.isControl()` | Control character |
| `bool <char>.isAscii()` | Code point below 128 |
| `char <char>.toUpper()` | Uppercase form, culture-invariant |
| `char <char>.toLower()` | Lowercase form, culture-invariant |

## 4.3 String functions

The functions that return a `string` follow 4.1: in place when the result is not used or is assigned back to the same string, a new string otherwise.

| Function | Definition |
|---|---|
| `string <string>.trim()` | Removes all non-printable characters and all types of spaces from both ends of the string |
| `string <string>.trim(string c)` | Removes all specified characters from both ends of the string |
| `string <string>.trimLeft()` | Removes all non-printable characters and all types of spaces from the left of the string |
| `string <string>.trimLeft(string c)` | Removes all specified characters from the left of the string |
| `string <string>.trimRight()` | Removes all non-printable characters and all types of spaces from the right of the string |
| `string <string>.trimRight(string c)` | Removes all specified characters from the right of the string |
| `string <string>.substr(u32 start, u32 len)` | Produces a string containing the characters starting from `start` (zero-based) up to `len` length. Does not fail if `len` extends past the end |
| `string <string>.replace(string toFind, string replacement, bool caseSensitive = true)` | Returns a string where all occurrences of the specified string are replaced with the replacement string |
| `i64 <string>.indexOf(string val, bool caseSensitive = true)` | Returns the first zero-based index of the specified string, or `-1` if not found |
| `i64 <string>.indexOf(char val, bool caseSensitive = true)` | Returns the first zero-based index of the specified character, or `-1` if not found |
| `i64 <string>.lastIndexOf(string val, bool caseSensitive = true)` | Returns the last zero-based index of the specified string, or `-1` if not found |
| `i64 <string>.lastIndexOf(char val, bool caseSensitive = true)` | Returns the last zero-based index of the specified character, or `-1` if not found |
| `bool <string>.contains(string val, bool caseSensitive = true)` | Returns `true` if the specified string exists within the string |
| `bool <string>.startsWith(string val, bool caseSensitive = true)` | Returns `true` if the string begins with the specified string |
| `bool <string>.endsWith(string val, bool caseSensitive = true)` | Returns `true` if the string ends with the specified string |
| `arr<string> <string>.split(string separator)` | Splits the string into an array of substrings bounded by the specified separator string |
| `string <string>.toLower()` | Returns a copy of the string with all characters converted to lowercase |
| `string <string>.toUpper()` | Returns a copy of the string with all characters converted to uppercase |
| `string <string>.padLeft(u32 totalWidth, char paddingChar = ' ')` | Pads the left side of the string with `paddingChar` until it reaches `totalWidth` length |
| `string <string>.padRight(u32 totalWidth, char paddingChar = ' ')` | Pads the right side of the string with `paddingChar` until it reaches `totalWidth` length |
| `arr<byte> <string>.toCStr(StringEncoding encoding = StringEncoding.UTF8)` | Produces a byte array with the representation of the string as regular C string; the array is a copy owned by the caller |
| `string <string>.insert(u32 pos, string text)` | Inserts `text` before the character at `pos` |
| `string <string>.remove(u32 pos, u32 len)` | Removes `len` characters starting from `pos`. Does not fail if `len` extends past the end |
| `string <string>.replaceAt(u32 pos, u32 len, string text)` | Replaces `len` characters starting from `pos` with `text` |
| `string <string>.append(string text)` | Adds `text` at the end |
| `string <string>.prepend(string text)` | Adds `text` at the start |
| `void <string>.clear()` | Empties the string, always in place |
| `void <string>.shrink()` | Stores the string in the smallest form that holds it (4.4); the text does not change |

`StringEncoding` is a default enum used to emit strings in specific encodings (used for interpos):
`public enum String.StringEncoding { UTF8, UTF16, UTF32, UTF16BE, UTF32BE, ASCII, ISO88591 }`

## 4.4 Strings detailed implementation

This layout is for 32- and 64-bit targets; small targets (8 and 16 bits) have their own implementation.

- A `string` is a pointer to its first allocation; a null pointer is the empty string. The first allocation is the identity of the string: it never moves, and borrows point to it.
- The first 4 bits of every allocation give its class:

| Class | Meaning |
|---|---|
| 0–7 | single-block string of 32, 64, 128, 256, 512, 1024, 2048 or 4096 bytes |
| 8–10 | free |
| 11 | literal: read-only sequence of bytes |
| 12 | free |
| 13 | moved: the string lives elsewhere |
| 14 | free |
| 15 | block string |

- Every field of every header is aligned to 32 bits. Sizes are in bytes; a pointer is 8 bytes on 64-bit targets and 4 on 32-bit targets, and the starts in the tables are for 64-bit targets unless a 32-bit column is given.

### 4.4.1 Single-block strings

| Start | Size | Field | Description |
|---|---|---|---|
| 0 | 2 | class + length in bytes | class (4 bits, 0–7) and length in bytes (12 bits) |
| 2 | 2 | length in chars | |
| 4 | … | text | the text and a final 0 |

- A single-block string is always a valid C string.
- Capacity in bytes (size − 4 header − 1 final 0):

| Class | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 |
|---|---|---|---|---|---|---|---|---|
| Size | 32 | 64 | 128 | 256 | 512 | 1024 | 2048 | 4096 |
| Text bytes | 27 | 59 | 123 | 251 | 507 | 1019 | 2043 | 4091 |

- A string that outgrows its class moves to the next class that holds it; beyond 4091 bytes it becomes a block string.

### 4.4.2 Moved strings

| Start | Size | Field | Description |
|---|---|---|---|
| 0 | 1 | class | 13 (4 bits), 4 free bits |
| 1 | 3 | padding | |
| 4 | 8 (4 on 32-bit) | new location | the single-block string or the block string root that holds the text |

- When a string moves (a larger class, a block string, `shrink()`), its first allocation becomes class 13 and points to the new location. Every read already checks the class, so a borrow follows the pointer.
- Only the first allocation is ever class 13: a second move updates its pointer, so there is never a chain.
- The first allocation is freed together with the data when the string ends.

### 4.4.3 Literals

| Start | Size | Field | Description |
|---|---|---|---|
| 0 | 1 | class | 11 (4 bits), 4 free bits |
| 1 | 3 | padding | |
| 4 | 8 | length in chars | |
| 12 | 8 | length in bytes | |
| 20 | … | text | the text and a final 0 |

- The compiler builds them in read-only memory; they have no size limit and every function reads them as strings.
- A literal stays as is while it is only read. When the compiler sees a modification, the text is copied into a single-block or block string at the first modification.

### 4.4.4 Block strings

Meant for long text that is modified at any point (editor-like) without copying the whole string.

- Root of a block string: 60 bytes on 64-bit targets, 48 on 32-bit targets.

| Start | Size | Start (32-bit) | Size (32-bit) | Field | Description |
|---|---|---|---|---|---|
| 0 | 1 | 0 | 1 | class | 15 (4 bits), 4 free bits |
| 1 | 1 | 1 | 1 | flags | none defined yet |
| 2 | 2 | 2 | 2 | iterator counter | open iterators; while above zero the string cannot be modified |
| 4 | 8 | 4 | 4 | first block | start of every walk |
| 12 | 8 | 8 | 4 | last block | `+=` and `append` in O(1) |
| 20 | 8 | 12 | 8 | total length in chars | `.len` in O(1) |
| 28 | 8 | 20 | 8 | total length in bytes | `.sizeof` in O(1) |
| 36 | 8 | 28 | 4 | cursor: block | block of the last modification |
| 44 | 8 | 32 | 8 | cursor: char index | char index where the cursor block starts |
| 52 | 8 | 40 | 8 | cursor: byte offset | byte offset where the cursor block starts; turns the byte positions found by a search into char indices without a walk |

- Blocks are 4 KB. Header of a block: 24 bytes on 64-bit targets (payload 4072), 12 on 32-bit targets (payload 4084).

| Start | Size | Start (32-bit) | Size (32-bit) | Field | Description |
|---|---|---|---|---|---|
| 0 | 8 | 0 | 4 | next block | null on the last block |
| 8 | 8 | 4 | 4 | previous block | null on the first block; moving left from the cursor and merging with the left neighbour |
| 16 | 2 | 8 | 2 | bytes used | bytes of text in this block |
| 18 | 2 | 10 | 2 | chars | chars in this block |
| 20 | 4 | — | — | free | |

- A code point never straddles two blocks, so each block can be decoded on its own.
- The counts are per block, not running totals: a change touches only its block and the totals of the root.
- Finding position `n`: from the cursor, forward or backward, or from the first or the last block when they are closer, skipping whole blocks by their char count.
- The cursor is updated only by the operations that modify the string; plain reads never touch it, so reading is pure (`const` strings and threads are safe). An iterator keeps its own position.
- Changes in place: an insertion moves at most the bytes of one block; a full block is split in two; two neighbours under half full are merged.

### 4.4.5 Shrinking

- A string never moves to a smaller form on its own.
- `shrink()` stores the string in the smallest form that holds it: a smaller class, the blocks of a block string packed full, or a single-block string when the text fits in 4091 bytes.


# 5. `dfoot`
`Dfoot` is a specific Dflat structure used to serialize and deserialize a tree of items. It is inspired by JSON.
- Recursive ordered tree, JSON-like, not JSON-compatible. Meant for human-readable maps, so the key rules of [Ref Collections] are looser here (details later).
- Keys: macrotype + value (signed number, unsigned number, string, bool, date); `r`, `dec`, `int`/`uns`/`real` not allowed.
- Values: any Dflat type, classes included (details later).
TODO


# 6. Collections

## 6.1 General

A collection holds several items in one object. Dflat has seven: `arr`, `vec`, `list`, `map`, `hmap`, `set` and `hset`.

| Type | Description |
|---|---|
| `arr<T>` | array: fixed size set at initialization (compile-time or runtime), contiguous; cannot grow or shrink (6.8.1) |
| `vec<T>` | vector: contiguous and growable (Rust `Vec` rules) (6.8.2) |
| `list<T>` | singly linked list, growable (6.8.3) |
| `map<K, V>` | sorted map (B-tree); alias `bmap` (6.8.4) |
| `hmap<K, V>` | hash map (6.8.5) |
| `set<T>` | sorted set (B-tree); alias `bset` (6.8.6) |
| `hset<T>` | hash set (6.8.7) |

- Collections are objects: they are nullable by default (`type?` is a compile error, 7.1) and they own their elements (12).
- `==` between two collections compares identity only, never the content. `c!` makes a copy (`.clone()`, 12).
- Lengths and default indexes are `uns` everywhere; `.len` is the number of items.
- Every collection implements the trait `Collection<Item>` and one of its sub-traits `IndexCollection`, `MapCollection` or `SetCollection` (6.7).
- A collection has an internal counter of its open iterators and views: while it is above zero the size of the collection cannot change (6.4).
- By access: `arr`, `vec`, `list` are indexed by position (0 to `.len` - 1); `map` and `hmap` hold key/value pairs; `set` and `hset` hold each value once.
- Hash against B-tree: `hmap` and `hset` are hash tables (fast lookup, no order); `map` and `set` are B-trees (kept sorted, O(log n)); `list` is a singly linked list used by position. The items of a `list` need no key rules.
- Type, size and capacity: the size of an `arr` and the capacity of a `vec` are given only at initialization and are not part of the type.
- `arr<T>` is a class like any other, even if the code generated for it is direct: it has ownership, a hash, methods and properties, and it can be an item of another collection (`list<arr<i32>> myZip` is valid). It cannot be subclassed. It has no limit on the types (11.3.12). `vec`, `list`, `map`, `hmap`, `set` and `hset` are ordinary generic classes (11.3) with their own copy limit (11.3.12).
- Ownership of an `arr`: the variable that declares the array owns it, and drops the items and the block at the end of its scope; `b = a` is a borrow; `->a` moves it; a field that is an array is dropped by its class (12).
- Index checks happen at compile time when possible, otherwise at runtime: an index outside the collection is a compile error when it can be detected at compile time. At runtime the check stays in every build (like Go, C#, Java and Rust): in Debug builds it raises `IndexOutOfRangeException` (not silent, catchable); in Release builds it is a `Panic`, and a `catch` for `IndexOutOfRangeException` is ignored with a level 3 warning. The rule is the same for `arr`, `vec` and `list`, and the check compares the index with `.len` before any work, so a failing `c[i]` costs O(1) in the three of them.
- The seven collections are written by the compiler as generated C code (speed first). Users can write their own collection classes in Dflat; the traits that impose the signatures are in 6.7 and the custom collections in 6.9.
- A collection owns its items (Rust model, 12): the drop code of the items is generated per item type. A result that is not assigned is destroyed at the end of the statement, `c[k] = v` drops the previous item, `->c[k]` moves the item out (6.6) and a `for` loop only borrows (6.4).
- An owned object that loses its owner is destroyed and its `atEnd` runs: this covers `clear()` and `fill(...)` on every collection, `c[i] = v` and any other operation that drops an owned object (12).
- Any allocation can run out of memory: a `vec` that grows, `reserve`, creating an `arr`, a `list` that adds a node. In Debug builds it raises `OutOfMemoryException` (15.3, catchable, not silent); in Release builds it is a `Panic` that ends the program (15.5). A size that does not fit (`n * size` overflowing `uns`) is reported in the same way, with no separate `OverflowException`. A literal size above the maximum is a compile error.
- Moving an object into a collection that owns its items needs `->` when the object already exists (`list[3] = ->task`, `list.add(->task)`, `insert`, `replace`): the plain form with an existing object is a compile error, so the passage of the ownership is always visible. An object created in the same statement (`list.add(Task("a"))`) needs no marker. Basic values are copied and need no marker (12.4); a string is always copied and the copy is shrunk (4).
- Printing (`toStr()`, string interpolation, `Root.Console.log`): a collection prints as `[a, b, c]`, a map as `[k1: v1, k2: v2]`. Only the first 3 items are printed; when there are more, `, ...]` and the number of items in parentheses follow: `[k1: v1, k2: v2, k3: v3, ...] (37 items)`. The rule is the same for every collection, its views and `.keys` / `.values`, and for a collection inside another one (`[[1, 2, 3, ...] (4 items), [5]]`). A null collection prints `null`.
- The members are listed in 6.6; each collection has its own section for the details (6.8).

## 6.2 Keys

Applies to the keys of `map` / `hmap` and to the items of `set` / `hset`.

- Types that can be keys: all the native types (the numeric types, `bool`, `char`, `string`, `date`, `time`, `clock`, `lapse`, enums, bitfields), composite types (tuples, and structs when they are defined, 11.1.5) whose components are all valid key types, classes that implement the trait `Keyable` (11.2.1), and generic sets whose members are all valid key types (11.3.6.1).
- Types that cannot be keys (compile error): `arr` and all the other collections (also as a component of a tuple), iterators and views, counted objects (`@T`, `(@)T`) and weak references (`_T`), which are not stable by definition (12.7), and classes that do not implement `Keyable` (the error names the trait).
- Nullable types can be keys (`i16?`), and null is a valid key; it sorts first (6.2.2). The empty string `""` is a valid key: for a `string`, `""` and null are the same value (7.1), so there is no separate null string key.
- Keys are always read-only through the collection.
- Sorted collections (`map`, `set`): a key is found and ordered by its sort key and `compareTo` (6.2.2, 6.8.4.1). Hashed collections (`hmap`, `hset`): the hash chooses where to look, and two keys are the same when the hashes match and `equals` agrees (6.2.1, 6.8.5.1).
- `bool` can be a key (two possible values). Reals can be keys, NaN included: in `map` and `set` every NaN is the same key and sorts last (6.2.2); in `hmap` and `hset` every NaN is the same key too. In all four `-0.0` and `+0.0` are the same key.
- A `string` is a pointer to its first allocation, and a null pointer is the empty string (4.4): a null pointer alone cannot mark an empty slot of a hash table with string keys, because `""` is a valid key. The hash tables mark the used slots in a separate mask (6.8.5.1), so `""` is a normal key.

### 6.2.1 Hash

- `.hash` is a property of every type, of size `uns` (2.1). The hash of the native types cannot be overridden. No hash depends on a memory address: the same content always gives the same hash, also between runs. A saved map stores its keys and values, never the hashes or the table.
- `$` is the ordered hash combine: `h = (rotl(h, 5) ^ part) * K`, with `K = 0x517cc1b727220a95` for a 64-bit `uns` and `0x9e3779b9` for a 32-bit one (FxHash). Unlike a xor, the result depends on the order and `(x, x)` does not give 0. Collisions are accepted: the goal is that the same content always gives the same hash.
- How the hash is calculated:
  - A native value whose size is at most `uns` uses its own value as the hash, widened with zeros: a signed value is not sign-extended (an `i32` of -1 gives `0x00000000FFFFFFFF` with a 64-bit `uns`), so no value has a bit set above its width.
  - A value wider than `uns` (an `i256` with a 64-bit `uns`, a wide `real`) combines its parts in order with `$`.
  - `real`: its bit pattern after normalizing (not a numeric conversion), widened with zeros (an `r32` with a 64-bit `uns`): a `-0.0` (or an unnormalized zero) becomes `+0.0`, and every NaN becomes the canonical NaN of the architecture. A `real` wider than `uns` combines its parts with `$`.
  - Strings: rapidhash V3 (the full variant, seed 0) on the full text, nothing of the header (4.4), so the hash depends only on the content and is the same in every form of the string (single-block, block string, literal). With a 32-bit `uns` the 64-bit result is folded: `hi ^ lo`. An architecture definition can choose rapidhash nano (low power targets); up to 16 bytes of text the two give the same result. A string key is its own shrunk copy (6.1) and is hashed once when it is stored; a lookup hashes its argument once. A null string is `""` (7.1) and hashes as `""`: `0x0338DC4BE2CECDAE` with a 64-bit `uns`, `0xE1F611E5` with a 32-bit one.
  - Tuples: `$` over the hashes of the components, in order: `h` starts as the hash of the first component, then `h = h $ next`. A tuple of one component has the hash of that component. The names of the fields are not part of it.
  - Null: a fixed constant, the same for all types, `"null".hash`: `0x2AA52E3F0BF438BD` with a 64-bit `uns`, `0x21511682` with a 32-bit one. It has a bit set above the width of every type that `uns` holds with spare bits (`x8?`, `x16?`, `bool?`, and `x32?` with a 64-bit `uns`), so no real value can equal it; `x8?`, `x16?` and `bool?` always take at least 4 bytes, so with a 32-bit `uns` the 32-bit constant serves them. A full-width nullable native (`x64?` and `uns?` with a 64-bit `uns`, `x32?` with a 32-bit one) has no spare bit: in a hash table its key slot holds two `uns`, the null constant as tag and 0 (6.8.5.1). On microarchitectures `null.hash` is 0 and the clash with 0 is accepted.
  - Classes (the default hash, based on the content): `"ns.Class".hash $ hash of field 1 $ hash of field 2 $ ...`
    - Every class that does not override `get_hash()` gets its own generated default, with its own fully qualified name and all its fields, the inherited ones included: for `B : A`, the default of `B` starts from `"ns.B".hash`. A class that inherits an override uses that code as it is: if `A` overrides `get_hash()` and `B` does not, a `B` hashes with the code of `A`. All classes have a namespace (`Root.` classes included).
    - The fields are combined in the order they are stored in memory.
    - A stable field gives its own hash. A field is stable when it is a native, an owned object (an owned child included) whose members are all stable, or an owned object whose class overrides `get_hash()` (its `get_hash()` gives the hash, whatever its members are).
    - An unstable field gives the hash of its fully qualified type name followed by the field name, with no separator: `"list<my.ns.Something>b".hash`, `"arr<i32>c".hash`, `"Root.IO.Filef".hash`. Collections, iterators, views, every sub-collection class and every reference field (an object that is not owned) are unstable: only owned objects are followed, so a cycle of references cannot make the hash recurse forever.
    - A null field gives the null constant.
    - The names of the fields are part of the hash of a class, not of the hash of a tuple.
  - A collection (the default ones and the custom ones that do not override `get_hash()`) hashes as the hash of its type name only: `"vec<i32>".hash`. All its fields would be unstable, so this is the value the class rule gives, without depending on how the collection is built. The content is never hashed.
- The hash of a type is the hash of its fully qualified name: `"i32".hash`, `"my.fully.namespace.Class".hash`.
  - Built-in types (natives and the default collections) are written without namespace; all classes have a namespace (`Root.` included).
  - Generic arguments go inside `<>`, separated by `,` with no spaces, each written by the same rule: `"list<my.ns.Something>"`.
  - Structs: their qualified name only (not the implementation).
  - Tuples: `(tuple,` followed by the types of the components separated by `,`, then `)`, with no field names, nested tuples included: `(i32, string, (uns, Obj))` is `"(tuple,i32,string,(tuple,uns,full.ns.Obj))".hash`.
  - Nullable types: the name followed by `?`: `"i64?".hash`.
- `Root.Class` offers `get_hash()` and `equals(any?)` (11.1.3). A class can override `get_hash()`, `equals` and `compareTo`; no warning is given for any of them. `.hash` on an object always calls `get_hash()` through the virtual dispatch: the override of the dynamic class runs, also through a variable of a parent type. `==` on class objects calls `equals` only when the class overrides it (known at compile time), and compares identity otherwise.
- Identity of a key in `hmap` and `hset`: the hash chooses where to look; two keys are the same when the hashes match and `equals` agrees. For the natives up to `uns` the hash is the value, so a match of the hash is enough. Two different keys with the same hash are both kept; the same key replaces the value (`add` returns `false`). There is no collision exception.

### 6.2.2 Order

- There is no `.order`. Ordering uses the trait `Comparable<T>` (11.2.1) with the mandatory method `int compareTo(T value)`, which returns -1, 0 or 1 (`int` is the signed native integer).
- Orderings:
  - numbers, `char`, `bool` (false before true), `date`, `time`, `clock` and `lapse` by value; enums by their numeric value (an enum can have explicit values), as `sort` does;
  - `real`: NaN is greater than any number, NaN against NaN gives 0, and `-0.0` equals `+0.0`, so the order is total;
  - strings by bytes (the same as code point order in UTF-8), culture-invariant for now;
  - tuples item by item, left to right;
  - null before any value (7.1);
  - classes that provide no order (the default implementation of `compareTo`): their hashes are compared. The order is deterministic but has no meaning (it is not alphabetical); two different objects with the same hash compare as 0.
- The only requirement of an order: consistent and total, `a < b` always gives the same answer.

### 6.2.3 Stability of keys

- A key must keep the same hash for its entire stay in a collection. Value types (natives, strings, tuples of them) do this by definition. A class object used as a key must implement `Keyable` (11.2.1) and must not change while it is a key. A change is not always detected: the collection recomputes the key's hash when the key is used, and raises an exception if it differs from the stored one.
- Stable means: within one run, while the key is in the collection, `get_hash()`, `compareTo` and `equals` give the same results. The guarantee is on the user; the detection is best effort and lazy (an entry that was changed and is not used is not reported).
- The collection owns its keys (6.6), so a key cannot vanish while it is in the collection.

## 6.3 Literals

```
list<i32> numbers = [1, 2, 3]
var samples = [1, 2, 3]                 // arr<native integer>, 3 elements
arr names = ["a", "b", "c"]             // arr<string>, 3 elements
vec values = [1, 2, 3]                  // vec<native integer>
set<string> tags = ["red", "blue"]
hset colors = ["red", "blue"]          // hset<string>
map ages = ["one": 1, "two": 2]         // map<string, native integer>
map words = [1: "one", 2: "two"]        // map<native integer, string>
map<string, i32> codes = [name: 1, prefix + "x": 2]  // keys are expressions
list<arr<i32>> grid = [[1, 2], [3, 4]]  // nested literals need the full type
```

- A literal is the right side of an assignment.
- `[...]` can be assigned to `arr`, `vec`, `list`, `set` and `hset`; `[key: value, ...]` to `map` and `hmap`: the `:` makes it a map literal. Literals are arrays (`arr`) unless declared otherwise, and a literal with `:` is a `map` unless declared otherwise. Mixing items with and without `:` (`[1, 2: true]`) is a compile error.
- Map keys are ordinary expressions, like the values: `"one"` is a string, `name` is the value of the variable `name`, `MyEnum.blue` is an enum value, `a.f(x)` is calculated. Parentheses are only grouping.
- A map literal is lowered as an empty map followed by the adds, in order; a set literal as an empty set followed by the adds.
- A duplicate value in a set literal or a duplicate key in a map literal is a compile error when they are constants (literals and enum values); two different keys with the same hash are not an error (6.2.1). With keys calculated at run time a duplicate is found only at run time, and the later value wins (as with `add`, 6.6).
- Nested literals need the full type specification: `var` and partial forms (`arr`, `vec`, ...) are a compile error for them.
- Types are inferred from literals; integers and reals default to the native size (1.3), mixed `[1, 2.5]` → native real; other mixes are errors; empty literals need a declared type (`map<i32, bool> flags = []` is an empty map).
- Size or capacity on the declaration: `arr<T>[size]` is the size, `vec<T>[n]`, `hmap<K, V>[n]` and `hset<T>[n]` are the capacity (initially empty). The same form on `list`, `map` and `set` is a compile error.

## 6.4 Iteration

```
for i32 item in numbers { }                      // arr, vec, list: index 0 up to .len - 1
for (&)Task task in tasks { task.done = true }   // read/write borrow of each item
for (string key, i32 value) in ages { }          // map, hmap: (key, value) tuples
for string key in ages.keys { }
for i32 value in ages.values { }
for (var key, var value) in ages { }
for var (key, value) in ages { }
for i32 index = 1..=10 { }                       // a range is a virtual collection
```

- Every collection has a default iterator; the specifics are given in the section of each collection.
- `arr`, `vec` and `list` iterate by position, like `index = 0; index < .len; index++`. `set` iterates sorted, `hset` in an unspecified order. `map` iterates sorted by key, `hmap` in an unspecified order.
- A range such as `1..=10` is a virtual collection: the `for` creates an anonymous iterator on it.
- `var` can be used as the type of the loop variable when the type can be inferred (10.3).
- The loop variable borrows the item; the collection keeps the ownership (there is no consuming loop). `for Item x in c` gives a read-only `x`; `for (&)Item x in c` gives a read/write `x` (`x.done = true` changes the object in the collection). The marker is written like on a parameter: `(&)Type variable` (13.1).
- The items of a `set` / `hset` and the keys of a `map` / `hmap` are always read-only: `(&)` is a compile error on them. Keys are structural: changing one means removing the entry and adding it again.
- On maps, `for (string key, i32 value) in ages` gives a read-only key and value; `for (string key, (&)i32 value) in ages` changes the value in place; `for ((&)string key, i32 value) in ages` is a compile error. `for string key in ages.keys` is always read-only; `for (&)i32 age in ages.values` is allowed.
- On a basic value type without `(&)`, `x` is a copy: assigning to it (`x += 1`) changes only the copy and gives a level 3 warning. With `(&)` the item in the collection is changed in place.
- A collection is read-only in size while it has open iterators or views:
  - A collection has an internal counter of its open iterators and views. Creating an iterator or a view increments it; the default `atEnd` of the iterator or of the view decrements it, through the `asparent` method `releaseIterator()` of the collection. An explicit iterator (made with `getIterator()`) counts too: it holds a reference to its collection, declared with `as parent` (6.7), to decrement the counter. The collection is the parent of its iterators and views: when it ends, it nulls their reference to it (12.5). The counter and its checks are provided by the trait `Collection` (11.2), so maps, sets, views and custom collections get them too.
  - While the counter is above zero the size of the collection cannot change: a changing operation raises `ModifyingIteratedCollectionException` (15.3, not silent). In Debug builds it is catchable; in Release builds it is a `Panic`, and a `catch` for it is ignored with a level 3 warning (the same rule as `IndexOutOfRangeException`, 6.1). Every changing method of a collection checks the counter at runtime.
  - Forbidden while the counter is above zero: `add`, `add(collection)`, `insert`, `pop`, `remove`, `removeAll`, `removeAt`, `resize`, `truncate`, `fillAtCapacity`, `fill(count, value)`, `reserve`, `shrinkToFit`, `sort`, `sortUnstable`, `reverse`, `copyFrom`, and `->c[i]` on a `list` (the node is removed).
  - Allowed: reads, `swap`, `replace`, `c[i] = v` (objects included), `fill(value)`, `clear()`, `->c[i]` on `arr` and `vec`, and changes made in place to the value or to the fields of an item.
  - Compile-time proof: the compiler proves only the direct case, when the body of the loop calls a changing member, or assigns an element, of the same variable (or field path) that the loop iterates; it is a compile error (Rust style, no cost at runtime). Everything else stays a runtime check (C# style). The proof will be improved later.
- Iterating custom collections: 6.9. For the collections of the compiler the `for` is emitted as a direct loop in C.

## 6.5 Views

```
View<arr<i32>> head = scores[..3]     // read-only view, no copy
arr<i32> middle = scores[2..5]!       // copy
```

- `c[a..b]`, `c[..b]`, `c[a..]` give a view: a reference to a part of the real collection, not a copy. A view is always read-only; there are no writable views.
- A view is a dedicated class, `View<C>`, where `C` is the type of the collection it comes from: a `vec<string>` can only produce a `View<vec<string>>`. Each view class holds what its collection needs for the view to exist (for example a start and a length for `arr` and `vec`, a start node, an end node and a count for `list`, 6.8.3). The view classes of the collections are similar but are not the same class. Trees and maps will probably need dedicated views (`IndexView`, `MapView`); that is decided with those collections.
- On an `IndexCollection` the operator `[a..b]` calls the non-public method `get_view(uns? start, uns? end)` (6.7), which returns `View<C>`. A missing bound (`[..b]`, `[a..]`) is passed as null.
- Range views exist only on `arr`, `vec` and `list`. There are no ranges on keys (`map`, `hmap`, `set`, `hset`). The `.keys` and `.values` of a map are views too (6.6). Creating a view of a `list` is O(n), because the start node has to be found.
- Iterators and `for` can run over a view: it has a begin and an end.
- Lock: the same rules as the iterators (6.4). A view is a child of its collection, declared with `as parent` like an iterator, and increments the counter of open iterators and views of its collection, so the collection keeps its size while the view is used.
- Where iterators and views live: an iterator or a view is a child of its collection (`as parent`, 6.7). If not given away, it lives only inside the scope that created it (the call to `getIterator()` or `c[a..b]`) and ends before its collection. If it is moved with `->`, it is an adopted child (12.5): when the collection ends first, it nulls the reference of the iterator or view, and using it raises `UsageOfDestroyedObjectException`. The collection becomes changeable again only when the last holder ends.
- `c[a..b]!` copies the items into a new collection of type `C` and leaves the original unchanged. It needs `.clone()` (12) to be fully implemented.

## 6.6 Members

| Member | arr | vec | list | map | hmap | set | hset |
|---|---|---|---|---|---|---|---|
| `.len`, `.sizeof` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `.capacity`, `reserve`, `shrinkToFit` | – | ✓ | – | – | ✓ | – | ✓ |
| `.keys` | – | – | – | ✓ | ✓ | – | – |
| `.values` | – | – | – | ✓ | ✓ | – | – |
| `c[k]` read | ✓ | ✓ | ✓ | ✓ | ✓ | – | – |
| `c[k] = v` | replace | replace | replace | set or add | set or add | – | – |
| `c[a..b]` view | ✓ | ✓ | ✓ | – | – | – | – |
| `add(v)` | – | ✓ | ✓ | `add(k, v)` bool | `add(k, v)` bool | bool | bool |
| `add(collection)` | – | ✓ | ✓ | merge | merge | count | count |
| `insert(index, v)` | – | ✓ | ✓ | – | – | – | – |
| `pop((&)out)` bool | – | ✓ | – | – | – | – | – |
| `has(v)` | ✓ | ✓ | ✓ | by value | by value | ✓ | ✓ |
| `hasKey(k)` | – | – | – | ✓ | ✓ | – | – |
| `indexOf(v)` | ✓ | ✓ | ✓ | – | – | – | – |
| `keyOf(v)` | – | – | – | ✓ | ✓ | – | – |
| `remove(v)` bool | – | ✓ | ✓ | by value | by value | ✓ | ✓ |
| `removeAll(v)` count | – | ✓ | ✓ | by value | by value | – | – |
| `removeAt(k / range)` | – | ✓ | ✓ | by key | by key | – | – |
| `tryGet(k, (&)out[, default])` bool | – | – | – | ✓ | ✓ | – | – |
| `replace(pos, value)` | ✓ | ✓ | ✓ | – | – | – | – |
| `swap(key, new)` | – | – | – | ✓ | ✓ | – | – |
| `swap(i, j)` | ✓ | ✓ | ✓ | – | – | – | – |
| `swap(key1, key2)` | – | – | – | ✓ | ✓ | – | – |
| `->c[k]` moves the item out | zeroed | zeroed | removed | removed | removed | removed | removed |
| `clear()` | defaults | defaults | defaults | ✓ | ✓ | ✓ | ✓ |
| `fill(value)` | ✓ | ✓ | ✓, and `fill(count, value)` | – | – | – | – |
| `fillAtCapacity(value)`, `resize` | – | ✓ | – | – | – | – | – |
| `truncate` | – | ✓ | ✓ | – | – | – | – |
| `sort`, `sortUnstable`, `reverse` | ✓ | ✓ | ✓ | – | – | – | – |
| `copyFrom` | ✓ | ✓ | ✓ | – | – | – | – |
| `transformTo<Kind>()` | ✓ | ✓ | ✓ | – | – | – | – |
| `+=` | – | – | – | `add` | `add` | `add` | `add` |
| `-=` | – | – | – | `removeAt`, a set of keys | `removeAt`, a set of keys | `remove`, a collection | `remove`, a collection |
| set operations and tests | – | – | – | – | – | ✓ | ✓ |

A member marked `–` is a compile error on that collection (for example `hasKey` on a `vec`). The trait that provides each member is in 6.7.

- `.len`: count of items. `.sizeof`: bytes to store the object itself; it exists on every type, natives included (6.7). For a collection it is the header plus the allocated block of items (the capacity, not the length), without what the items own; for a `list` it is the header plus all the nodes.
- `.capacity`: items that fit without growing. `reserve(n)` makes room for at least `n` items in all (the capacity becomes at least `n`; nothing changes when it already is), and the room stays until a shrink; `shrinkToFit()` lowers the capacity to the length. On `hmap` and `hset` the capacity is counted on the directory and `shrinkToFit()` returns a `bool` (6.8.5.1).
- `.keys`: keys of a map (`map`, `hmap`). `.values`: values of a map. Both are views (6.5): read-only, they increment the counter of open iterators and views of the map (6.4), they have `len`, `has` and an iterator, and they can be stored and passed. `ages.keys!` makes a `vec<Key>` and `ages.values!` a `vec<Value>`. Keys are never writable; values can be changed in a loop with `(&)` (6.4). To walk the positions of `arr`, `vec` and `list` use a range (`0..list.len`, 6.4).
- `c[k] = v`: on `arr`, `vec` and `list` it replaces an existing item only; an index past the end raises `IndexOutOfRangeException` (not silent). On `map` and `hmap` it replaces the value, or adds the key and the value when the key is missing.
- Reading a missing map key gives the null or zero value of the value type; no exception. Use `hasKey` or `tryGet` to know whether the key exists. With objects as values the result is null, so `map["a"].name` raises `NullPointerException` like any other access through null.
- `add(v)`: appends to `vec` and `list`. `add(k, v)`: adds the pair to a map, or replaces the value of an existing key; returns `false` when it replaced, `true` when the key is new. On sets it returns `true` if the value was added.
- A map owns its keys and its values: existing objects are moved in with `m.add(->key, ->value)` (6.1). The keys are read-only through the map (6.2).
- `add(collection)`: appends the items of another collection to `vec` and `list`; on maps it merges another map (the new value wins, no return value); on sets it returns the number of items actually added.
  - On `vec` and `list`: items of basic types are copied. Objects are moved with `v.add(->other)`: the items move, the other collection ends and its variable becomes null (12); a plain `v.add(other)` clones the items, with the rules of `other!` (12.6), and leaves `other` unchanged. The same collection as source and target raises `ModifyingProtectedSourceException`.
  - On maps: only a map of the same kind (`map` with `map`, `hmap` with `hmap`); mixing them is a compile error. `m.add(other)` clones the entries (12.6) and leaves `other` unchanged; `m.add(->other)` moves them.
  - On sets: any collection with the same item type (`set` and `hset` mixed included). Basic items and strings are copied, objects are cloned (12.6) and `other` is unchanged; `s.add(->other)` moves them.
- `insert(index, value)`: `index` goes from `0` to `.len` (`.len` appends); a larger index raises `IndexOutOfRangeException`.
- `pop((&)out)`: removes the last item of a `vec` and moves it into `out` (the ownership passes to `out`); it returns `true` (the `isValid` of the result). On an empty `vec` it returns `false` and `out` gets the default value of its type (null for objects, `0` for numbers, `""` for `string`). There is no other form of `pop` and no `tryPop`; no exception is raised. The previous value of `out` is dropped.
- `has(v)`: true if the value is present (on maps, searches the values). `hasKey(k)`: true if the key exists.
- `indexOf(v)`: index of the first match, or the maximum `uns` value when not found (the `string` `indexOf` keeps returning `-1`).
- `keyOf(v)`: key of the first entry found with that value, or the default value of the key type when there is none; it raises no exception. Use `hasKey` to test a key.
- `remove(v)`: removes the first match and returns `true` if something was removed. `removeAll(v)`: removes all matches and returns the number removed. On maps both work by value. Sets hold a value once, so they have `remove` only.
- `removeAt(k / range)`: removes by index or range on `vec` and `list`, by key on maps. The range forms are those of 6.5 (`a..b`, `a..=b`, `..b`, `a..`); the items removed end. A range outside the collection raises `IndexOutOfRangeException` (as `copyFrom`).
- `bool tryGet(k, (&)out[, default])`: reads a map entry into `out` (`(&)` because the call writes to it) and returns `true` when the key exists. When the key is missing it returns `false` and `out` gets `default`, or the default value of its type when no `default` is given.
  - For objects `out` is a borrow: the map keeps owning the object. The borrow is valid while the map exists and is not structurally modified (the rules of the views, 6.4, 6.5).
  - `(&)` only lets `tryGet` write the variable: assigning to `out` afterwards (`out = otherDog`) re-points the variable and leaves the map unchanged; replacing the value in the map needs `map[k] = v`. Changing the fields of the object through `out` changes the object in the map.
- Replace and swap, with the old item and the ownership:
  - `var old = sequence.replace(pos, value)` (`arr`, `vec`, `list`): puts the new item at `pos` and returns the old one; the ownership of the old item moves to the caller and the collection owns the new one. A new object that already exists needs `->` (6.1). If the result is not assigned, the old item ends at the end of the statement.
  - `sequence.swap(posA, posB)` only exchanges two items of `arr`, `vec` or `list`; no ownership changes. There is no `swap(position, new)` form (use `replace`).
  - `Item old = map.swap(Key key, Item new)`: the item in the map is replaced by `new`; the ownership of the old item moves to the caller and the map owns `new`. A missing key adds the pair and returns a null (default) item. The `swap` forms of maps are confirmed with the maps.
  - `map.swap(key1, key2)` exchanges the two values: if both keys exist the values are exchanged; if only one exists its value moves to the missing key and the original key is removed; if both are missing nothing happens.
- `->c[k]` moves the item out of the collection (ownership passes to the receiver; a basic value type is copied). The slot is zeroed (null for objects, `0` for numbers) in `arr` and `vec`, and the item is removed in `list`, `map`, `hmap`, `set` and `hset`. On maps `->m[k]` gives only the value and the key ends with the entry; `(key, value) = ->m[k]` is a compile error. On sets the key is the item: `->s[item]` removes the item equal to `item` and gives it.
- `+=` and `-=` on maps and sets call the members, so they follow the same rules (a custom collection gets them from its members); `|=`, `&=` and `^=` are not defined on collections:
  - maps: `m += (key, value)` (a tuple) calls `add(key, value)`; `m += other` calls `add(other)`; `m -= key` calls `removeAt(key)`; `m -= s`, with `s` a `set` or an `hset` of the key type, removes every key of `s` with its value.
  - sets: `s += v` calls `add(v)` and `s += other` calls `add(other)`; `s -= v` calls `remove(v)`; `s -= other` removes every item of `other`, a collection with the same item type (the in-place difference).
- `clear()`: on `arr`, `vec` and `list` it cleans up the values: every item is set to the default value and the size is kept (the objects dropped end, 6.1). On maps and sets it removes all items. To remove all the items of a `vec` or a `list`, use `truncate(0)`.
- `fill(value)`: sets every item up to the current `.len` (the whole array for `arr`; on an empty `list` it does nothing). `fillAtCapacity(value)` (`vec`): fills the whole capacity and sets `.len` to the capacity.
- The value of `fill` can be any value: basic values, strings (each item gets its own copy, shrunk, 4), dates, times and every other type that is not a class. `null` is allowed: every item becomes null and the objects they held are no longer owned by the collection (they end, running their `atEnd` if they have one). A class instance is a compile error (the items would share one object).
- `list` also has `fill(count, value)`: it adds `count` items with that value, under the same rules (`myList.fill(256, 'c')`).
- `resize(newLen, value)`: sets `.len`; new items get `value`, extra items are cut. `truncate(len)`: cuts the `vec` or the `list` to `len` items (no effect if shorter; the items cut end); on a `vec` the capacity is unchanged.
- Sorting: `sort(SortDirection.ASC)` sorts in place with the default comparer (`compareTo` of the items, 6.2); `sort()` is ascending. `sort(lambda)` uses a temporary custom sorter. `SortDirection` has the entries `ASC` and `DESC`. The lambda returns a bool (true is -1, "first goes before second"; false is 1) or a number (negative, zero, positive). `sort` is stable; `sortUnstable` is not (it needs no extra memory). `reverse()` flips the current order in place.
- Sorting algorithms: on `arr` and `vec`, `sort` (stable) is a merge sort with run detection (TimSort style) and `sortUnstable` is pdqsort (as Rust and Go). On `list`, `sort` and `sortUnstable` are both a bottom-up merge sort that relinks the nodes directly, with no temporary array: only the pointers move. The comparison goes through `compareTo` (6.2), or through the function given to `sort`.
- `copyFrom(dstIndex, srcCollection, srcIndex, count)` and `copyFrom(dstIndex, srcCollection, range)`: copy items from another index collection into this one; it works between any index collections (`arr`, `vec`, `list`). It copies for real (object items are cloned, 12), it is not a move. A range outside either collection raises `IndexOutOfRangeException`, or is a compile error when it can be detected at compile time; this check must always be there. Using the same collection as source and destination is a compile error when detectable, otherwise a non-silent `ModifyingProtectedSourceException` is raised.
- On `vec` and `list`, `dstIndex` goes from `0` to `.len`: the items past the end are appended, so `copyFrom` is the only way to write past the end and it never leaves gaps; a larger `dstIndex` raises `IndexOutOfRangeException`.
- `transformTo<Kind>()` converts a whole collection into another kind: `vec<i32> v = myArr.transformTo<vec>()`.
  - It is a moving method: the original ends and the items go to the new collection without being cloned. Using `myArr` after the call is a compile error, as after `->myArr`.
  - The item type is taken from the original (`arr<i32>` gives `vec<i32>`). `transformTo<vec<i32>>()` is accepted too; a different item type is a compile error.
  - Between `arr`, `vec` and `list`. `vec` to `arr` can reuse the block with no copy, after shrinking it to the length.

```
arr source = [1, 2, 3]
arr destination = [1, 2, 3, 4]
destination.copyFrom(3, source, 0..3)    // compile error: 3 items do not fit from index 3 of 4 items

names.sort(SortDirection.DESC)
names.sort((first, second) { first.len < second.len })
```

## 6.7 Collection traits

Every collection implements the trait `Collection` and one of its three sub-traits. The traits impose the signatures of the members that every collection must have, so the compiler can map `.len`, `for in`, `c[k]` and the literals to them (6.9 shows how a custom collection uses them).

| Trait | Implemented by | Items |
|---|---|---|
| `Collection<Item>` | all seven | |
| `IndexCollection<Item>` : `Collection<Item>` | `arr`, `vec`, `list` | accessed by position, 0 to `.len` - 1 |
| `MapCollection<Key, Value>` : `Collection<Value>` | `map`, `hmap` | key/value pairs |
| `SetCollection<Item>` : `Collection<Item>` | `set`, `hset` | each value held once |

- Rule: whatever is shared by the collections stays in the trait (in `Collection` when all seven have it, like `.len`); whatever is specific to one collection is defined by that collection class. Every collection is in a trait.
- `arr` is a class (6.1) that implements `IndexCollection`; the code generated for it is direct.
- `Collection<Item>` has: the read-only `len` property (the number of items, `uns`), `has(v)`, `->c[k]` and `clear()`. `.sizeof` exists on every type (`Root.Class`, see the table below). The meaning of each member is in 6.6.
- `Collection<Item>` has no `getIterator()`: each sub-trait declares its own, typed (an override that returns a subtype of the declared return type is not allowed):

```
trait IndexCollection<Item> : Collection<Item> {
  public fun IndexIterator<Item> getIterator()
}

trait MapCollection<Key, Value> : Collection<Value> {
  public fun MapIterator<Key, Value> getIterator()
}

trait SetCollection<Item> : Collection<Item> {
  public fun SetIterator<Item> getIterator()
}
```
- `Item` is the type of the items. A map passes its `Value` (`has(v)` on a map searches the values); the key type is given by `MapCollection<Key, Value>`. `->c[k]` is a requirement of `Collection<Item>` whose key is given by each sub-trait: a position in `IndexCollection`, the key in `MapCollection`, the item in `SetCollection`.
- The sub-traits extend `Collection<Item>` with a generic parent trait (11.3.2): a class that implements `IndexCollection<T>` implements `Collection<T>` too.
- Iteration is done by an iterator: an object that implements the trait `Iterator`. `Iterator` is a non-generic marker trait with no members, and it has three sub-traits:

| Iterator | Description |
|---|---|
| `IndexIterator<Item>` | used for linear and sorted collections like arrays and lists |
| `MapIterator<Key, Value>` | used for key/value collections |
| `SetIterator<Item>` | used for sets of unique keys |

```
trait Iterator { }

trait IndexIterator<Item> : Iterator {
  public fun (Item value, bool isValid) next()
  public fun ((&)Item value, bool isValid) writableNext()
}
```

- Whether `{ }` is written for an empty trait follows the trait grammar (11.2).
- The cursor is internal to each iterator and cannot be reached from outside, so the trait does not specify it: the trait fixes only the methods. Iterators are specific to a collection type, so the reference to the collection (used to check the end and to release the counter of 6.4) lives in each concrete iterator class, not in the trait. The collection is the parent of its iterators: the class declares it with `as parent` on a reference parameter (at most one per class). When the collection ends, it nulls their reference to it (12.5).
- `next()` returns a tuple whose last item is the bool `isValid`; `writableNext()` returns the same tuple with the item as a writable borrow `(&)` (3.2; the one `for (&)` uses). When `isValid` is `false` the other items of the tuple are not valid (the iteration is over), so null values and items are not a problem for the iteration.
- The iterators follow a contract written with the trait. First rule: an iterator returns each element of the collection once and only once.
- The iterators of the index collections (6.7.1) and of the sets (6.7.3) are defined; the members of `MapIterator` come later.
- The collections of the compiler do not build an iterator object: the `for` is emitted as a direct loop in C (6.4).
- The methods behind the properties and the operators: each trait defines a set of non-public methods for its properties and its operators. They can be virtual, and every class that implements the trait must implement them. They are internal: only someone writing a custom collection needs them. They are snake_case (1.1, provisional). When the argument type of an operator is not given (`->c[k]`), it is the generic form valid for all collections; each kind of collection fixes the argument type (`uns` for `IndexCollection`, `Key` for `MapCollection`, and the same for the sets). More methods will be added with the maps and the sets.

| Written as | Method | Defined in |
|---|---|---|
| `x.sizeof` | `get_sizeof()` | `Root.Class` |
| `c.len` | `get_len()` | `Collection` |
| `->c[k]` | `move_out(k)` (generic form) | `Collection` |
| `->c[uns k]` | `move_out(uns k)` | `IndexCollection` |
| `->c[Key k]` | `move_out(Key k)` | `MapCollection` (later) |
| `->c[Item k]` | `move_out(Item k)` | `SetCollection` |
| `c[k]` (read) | `read_pos(uns k)` | `IndexCollection` |
| `c[k] = value` | `set_pos(uns k, value)` | `IndexCollection` |
| `c[a..b]`, `c[..b]`, `c[a..]` | `get_view(uns? a, uns? b)`, returns `View<C>`; a missing bound is null (6.5) | `IndexCollection` |

- `len` stays a property; `getIterator`, `next` and `writableNext` are regular methods and keep their names.

```
asparent fun releaseIterator() // In the Collection definition

class ListIterator<Item>(&list<Item> collection as parent) : IndexIterator<Item> {
  Node<Item> cursor = collection.first
  override fun (Item value, bool isValid) next() { ... }
  override fun ((&)Item value, bool isValid) writableNext() { ... }
  } atEnd {
  collection.releaseIterator()
}
```

### 6.7.1 IndexCollection

- Implemented by `arr`, `vec` and `list`: the items are accessed by position, `c[i]`, from 0 to `.len` - 1.
- Members: `c[i]` read, `c[i] = v`, `c[a..b]` view, `indexOf`, `replace(pos, value)`, `swap(i, j)`, `sort`, `sortUnstable`, `reverse`, `copyFrom`, `transformTo<Kind>()`, `fill(value)`. `vec` and `list` also have `add(v)`, `add(collection)`, `insert`, `remove`, `removeAll`, `removeAt` and `truncate`; `arr` does not provide them (compile error).
- `list` adds the overload `fill(count, value)` (6.6).
- Iterator: `IndexIterator`. In the default collections the iterator of `arr` and `vec` keeps a `uns` position and the iterator of `list` a pointer to the node (6.8.3). `next()` returns `(Item value, bool isValid)` and `writableNext()` returns `((&)Item value, bool isValid)`.

### 6.7.2 MapCollection

- Implemented by `map` and `hmap`: key/value pairs. Null is a valid key (6.2). The keys are always read-only in a loop (6.4).
- Members: `.keys`, `.values`, `c[k]` read, `c[k] = v` (replaces or adds), `add(k, v)`, `add(map)`, `hasKey`, `keyOf`, `tryGet`, `remove(v)`, `removeAll(v)`, `removeAt(k)`, `swap(key, new)`, `swap(key1, key2)`, `+=` and `-=` (6.6).
- Iterator (not yet defined, first idea): `next()` returns `(Key key, Value value, bool isValid)`. The sub-traits are defined later; the cursors are in 6.8.4.1 (`map`) and 6.8.5.1 (`hmap`).

### 6.7.3 SetCollection

- Implemented by `set` and `hset`: each value is held once. Null is a valid item (6.2). The items are always read-only in a loop (6.4).
- Members: 6.6 and 6.8.6. `->s[item]` moves the item out (the key is the item); `swap` is not defined on sets.
- Iterator: `SetIterator<Item>`; `next()` returns `(Item value, bool isValid)`. There is no `writableNext()`: the items are read-only. The cursors are those of `map` (6.8.4.1) and `hmap` (6.8.5.1), whose code the sets use.

```
trait SetIterator<Item> : Iterator {
  public fun (Item value, bool isValid) next()
}
```

## 6.8 Default collections

The seven collections generated by the compiler as C code (6.1). The table of the members is in 6.6; each type has its own section with the details.

### 6.8.1 Array `arr`

```
var buffer = arr<i32>[100]              // 100 elements, not initialized
arr<i32>[100] samples = []              // same, size on the declaration
arr<arr<i32>> rows = [[1, 2], [3, 4, 5]]    // jagged: sizes can differ
arr<i32> flat = arr<i32>[width * height]
flat[row * width + column] = 7          // a matrix on a flat array
```

- Fixed size set at initialization (compile-time or runtime), contiguous; it cannot grow or shrink. The size is not part of the type.
- `arr<T>[size]`: the size can be on either side; a mismatch (`arr<i32>[100] a = arr<i32>[120]`) is a compile error.
- Arrays are not initialized automatically: `fill(value)` sets every element. Reading an element before it is filled or assigned gives a level 1 warning. Arrays of objects start as null.
- No built-in multi-dimensional arrays: use an `arr` of `arr`, or a flat `arr` indexed by `row * width + column`.
- Default iterator: index 0 up to `.len` - 1.
- Trait: `IndexCollection` (6.7.1). `arr` is a class (6.1): it has ownership, a hash, methods and properties, and it can be an item (`list<arr<i32>>`). It cannot be subclassed. The code generated for it is direct.
- Ownership: the variable that declares the array owns it and drops the items and the block at the end of its scope; `b = a` is a borrow; `->a` moves; a field that is an array is dropped by its class. The objects inside are owned by the array (12).
- Differences from the trait: `c[k] = v` replaces only; `clear()` sets every item to the default value; `fill(value)` fills the whole array. `add`, `add(collection)`, `insert`, `remove`, `removeAll`, `removeAt`, `truncate` and `pop` are not available (compile error).
- Default iterator: `IndexIterator`, keeping a `uns` position.

### 6.8.2 Vectors `vec`

```
vec<i32> numbers = [1, 2, 3]
vec<string>[50] parts                   // empty, capacity 50
numbers.add(4)
numbers.insert(0, 9)
i32 last
bool isValid = numbers.pop((&)last)     // false (and last = 0) when the vec is empty
```

- Contiguous and growable (Rust `Vec` rules). `vec<T>` is empty; `vec<T>[n]` is empty with capacity `n`.
- Trait: `IndexCollection` (6.7.1). It has all the members of `Collection` and `IndexCollection`.
- Specific to `vec`: `pop`, `fillAtCapacity`, `resize`, `.capacity`, `reserve`, `shrinkToFit` and the `[n]` capacity form. `truncate` is shared with `list`.
- Growth policy and capacity:
  - Growing: the capacity is at least what was requested; it does not have to be exact. Shrinking is exact: after `shrinkToFit()` the capacity equals the length.
  - The capacity doubles until the block reaches `double-grow-limit` (default 4mb), then grows by the factor `further-grow-factor` (default 1.5) each time. A fixed increment would lose the amortized O(1) of `add` on large blocks. Both are compiler parameters (19). A fixed-increment option is kept only for small targets.
  - The first allocation is 16 items (16 times the size of the item), made on the first `add`, unless a capacity is given (`vec<T>[n]`).
- Default iterator: `IndexIterator`, keeping a `uns` position, index 0 up to `.len` - 1.

### 6.8.3 Lists `list`

- A singly linked list: the list keeps the number of items, the first node and the last node, and each node holds an item and points to the next node. There are no double links. The list owns its items (6.1): the nodes own the objects they hold.
- Every node is allocated separately; the list header has pointers to the first and the last node. There are two kinds of nodes: the pointer to the next node plus the value (basic types), or the pointer to the next node plus the pointer to the object. A node has no pointer to the list (the iterators and the views hold it). Natural size, no padding.
- Trait: `IndexCollection` (6.7.1). It has all the members of `Collection` and `IndexCollection`.
- Specific to `list`: `fill(count, value)` (6.6). `fill(value)` of the trait sets every existing item. `truncate(n)` is available, as on `vec`.
- It has no capacity: `.capacity`, `reserve`, `shrinkToFit`, `fillAtCapacity` and the `[n]` form do not apply. `resize` and `pop` are not available.
- Default iterator: `IndexIterator` from the first node to the last; one step follows the link of the node to the next node, O(1).
  - The iterator holds two references: one to its list, declared with `as parent` (6.7), used to release the counter of 6.4 when the iterator ends, and one to the current node.
  - The end of the list is marked by a null next pointer, so the node reference is nullable. The pointer of the list to its last node is only a fast way to add items at the end; the iterators do not use it. When the list ends, it nulls the reference of its iterators, so the node reference of an iterator is never used after its nodes are gone: the check of an adopted child (12.5) raises `UsageOfDestroyedObjectException` first.
  - `writableNext()` is syntactic sugar: it marks that the iterator changes the content of the node in place, through the node pointer. It does not change the structure of the list.
  - The iterator needs no explicit pointers: its references to the list and to the node are compiler-guaranteed (12.1, 6.7).
- Views (6.5): `l[a..b]` reaches the start in O(a), then O(1) per step. A `list` view holds a start node pointer, an end node pointer and a count; it is read-only (no `add`) and it increments the counter of 6.4 like the iterators.
- Cost of the members:

| Member | Cost | Note |
|---|---|---|
| `.len` | O(1) | stored count |
| `c[i]`, `c[i] = v` | O(n) | walk to the position |
| `add(v)` | O(1) | uses the last node |
| `add(collection)` | O(m) | m = items added |
| `insert(index, v)` | O(1) at 0 and at `.len`, otherwise O(n) | |
| `removeAt(index)`, `->c[i]` | O(1) at 0, otherwise O(n) | removing the last node needs the previous one |
| `remove(v)`, `removeAll(v)`, `has(v)`, `indexOf(v)` | O(n) | |
| `replace(pos, value)`, `swap(i, j)` | O(n) | walk to the positions |
| `clear()` | O(n) | sets every item to the default value |
| `truncate(n)` | O(n) | walks to the new last node and drops the rest |
| `sort`, `sortUnstable` | O(n log n) | bottom-up merge sort by relinking the nodes, no item copies |
| `reverse()` | O(n) | relinks the nodes |
| `copyFrom` | O(n + m) | |
| `c[a..b]` view | O(a) to reach the start | read-only, then O(1) per step |

### 6.8.4 Maps `map` (`bmap`)

```
map ages = ["one": 1, "two": 2]
bool isNew = ages.add("three", 3)       // false if the value of an existing key was replaced
ages["four"] = 4                        // adds the key and the value
i32 age = ages["one"]
```

- `map` (alias `bmap`) is sorted by key (B-tree). Key rules: 6.2.
- Trait: `MapCollection` (6.7.2). It has all the members of `Collection` and `MapCollection`; `add(map)` merges (the new value wins).
- Default iterator: (key, value) tuples sorted by key. `.keys` iterates the keys and `.values` the values.
- A missing key on read gives the null or zero value of the value type; no exception (`hasKey` and `tryGet` tell whether the key exists). A null key is valid and sorts first (6.2).
- No `.capacity`, `reserve`, `shrinkToFit` or `[n]` capacity form.

#### 6.8.4.1 B-tree implementation

The same code serves `map` and `set` (a set is a map without values).

- A B+ tree with two kinds of nodes, branch and leaf. A branch holds only what is needed to descend; a leaf holds the keys and the values (or the links to them). All the leaves are at the same depth. The leaves are linked to the next and to the previous leaf, and the map holds pointers to the first and the last leaf.
- Navigation uses a sort key, not the hash, so a `map` is sorted by key:
  - natives: the value turned into a `uns` that keeps the order (signed: the sign bit flipped; real: the usual bit trick). Exact, no collisions. A `real` is normalized first (`-0.0` becomes `+0.0`, every NaN the canonical NaN, which sorts after every number);
  - strings: the first 8 bytes, big-endian, the bytes beyond the length being 0 (so `"ab"` sorts before `"abc"` without reading the text); equal sort keys are resolved with `compareTo` on the full text;
  - classes and tuples: `compareTo` (6.2.2).
- No parent pointer: insert and delete keep the path of the descent on a small stack (the depth stays under 10 even for huge maps), so a split does not rewrite the parent of every moved child.
- Byte 0 of every node is a marker, a bitfield: bit 0 leaf, bit 1 multi-type key (a key of a generic set type, 11.3.6.1), bit 2 key with pointer. A tree never mixes layouts, each layout has its own code, and one bit test tells a branch from a leaf.
- Branch node: a header of 8 bytes (byte 0 the marker, bytes 1 and 2 the number of entries in use, the rest reserved), then all the separators, then all the children. About 256 bytes; the fanout is what fits after the header. A separator is the smallest key of its child (the separator of the first child is never read); the leaf resolves the exact key.
- Leaf node: a header of 24 bytes (bytes 0 to 7: byte 0 the marker, bytes 1 and 2 the number of entries in use, the rest reserved; bytes 8 to 15 the next leaf; bytes 16 to 23 the previous leaf; 0 at the ends), then all the keys, then all the values, so a search scans only the keys. The entries are kept packed and sorted (no valid flag per entry).
- A leaf holds 16 entries, a compiler parameter (19): its size is 24 + 16 × the size of an entry. The root leaf is a normal leaf.
- Key slot (sizes in bytes). The leaf always keeps the keys: the pointer refers to the key object owned by the leaf, so `equals` is always possible, `.keys` gets the real key, and the key is destroyed with its entry.

| Key type | Sort key exact | Leaf key slot | Comparison |
|---|---|---|---|
| native up to `uns` | yes | 8: sort key (the value is recovered from it) | sort key only |
| string | no | 16: sort key + pointer | sort key; on a tie `compareTo` on the text |
| wide native (`i256`) | no | 16: sort key (top 64 bits) + pointer (no class header, stored as an object) | sort key; on a tie the full value |
| class, default order | yes (6.2.2) | 16: sort key (the hash) + pointer | sort key only |
| class with its own `compareTo` | no | 16: sort key 0 + pointer | always `compareTo` |
| tuple | no | 16: sort key of the first component (0 if it has none) + pointer | sort key; on a tie item by item |

  - The pointer of a class with the default order is not used to compare: it is kept for `.keys`, `equals` and the drop.
  - Multi-type keys (a key of a generic set type, 11.3.6.1): the tag (the hash of the type, 6.2.1) first, in 8 more bytes. All member types exact: 16 bytes (type + sort key); any member not exact: 24 bytes (type + sort key + pointer, the pointer unused for exact members).
- A separator has the shape of the leaf key slot; its pointer refers to the key owned by the leaf (the key is not copied). When the first key of a leaf is removed, the separator that refers to it is updated to the new first key, through the descent path (O(depth)), so a separator never dangles. Entries in a 256-byte branch:

| Layout | Separator | Child | Entries |
|---|---|---|---|
| single-type, exact | 8 | 8 | 15 |
| single-type, with pointer | 16 | 8 | 10 |
| multi-type, exact | 16 | 8 | 10 |
| multi-type, with pointer | 24 | 8 | 7 |

- Value slot: a value up to 64 bytes is inline, in a slot of the exact size of its type (an `i32` takes 4 bytes). A string value takes a 64-byte slot that holds the first allocation of the string: a single-block string of class 0 or 1, up to 59 bytes of text (4.4.1), or a moved string (class 13) that points to the real string (4.4.2); no extra flag. A class is never copied into the node: the slot holds a pointer. Any value above 64 bytes goes through a pointer.
- The null key: null sorts first (6.2.2). The null entry is kept in the map header (a flag plus its value slot), outside the tree, for every key type: a full-width native (`i64?`) has no free sort key for null. Iteration gives it first (last when going backwards).
- Deletion is relaxed: a leaf is fixed (it borrows from a neighbour or merges with it, possibly again one level up) only when it drops below 4 entries; an empty leaf is always removed. The fix is local and done in the same call. Bulk operations (`removeAll(v)`, removing a range of keys, `clear`) remove everything in one pass and rebalance once at the end.
- Iterator: the current leaf and an index in it. Forward from the first leaf through the next links, backwards from the last leaf through the previous links; it ends where there is no next (previous) leaf, and the null entry comes first (last backwards). The counter of open iterators and views comes from the trait `Collection` (6.4): while it is above zero nothing splits, merges or shifts, so the leaf and the index stay valid. Writing a value in place with `for (&)` moves nothing and is allowed. `.keys` and `.values` use the same iterator. The order is the order of the sort keys.

### 6.8.5 Hash maps `hmap`

```
hmap<string, i32> scores = []
hmap<string, i32>[100] bigScores         // empty, capacity 100
```

- `hmap` is a hash map. Key rules: 6.2; the identity of a key is its hash plus `equals` (6.2.1).
- Default iterator: (key, value) tuples in an unspecified order, the same for the same content and history (6.8.5.1). `.keys` iterates the keys and `.values` the values.
- Trait: `MapCollection` (6.7.2). The members are the same as `map` (6.8.4).
- Specific to `hmap`: `.capacity`, `reserve`, `shrinkToFit` and the `[n]` capacity form.
- Which targets have `hmap` is decided by each architecture definition. A target with `hmap` and `null.hash` = 0 must handle the clash of null with the key 0.

#### 6.8.5.1 Hash table implementation

The same code serves `hmap` and `hset` (a hash set is a hash map with an empty value type). The table is an extendible hash: a directory of pointers to buckets, which split and merge one at a time, so the table never rehashes as a whole. The sizes below are for a 64-bit `uns`.

- Directory: a `vec` of bucket pointers, 2^d entries, where d is the global depth. It is indexed by the top d bits of the scrambled hash. A bucket of local depth L is pointed to by 2^(d−L) consecutive entries.
- Bucket: up to 16 entries, unsorted. A full bucket splits only if the next bit of the scrambled hash separates its entries; otherwise it takes an extension (a block with the same layout, linked by the next pointer). A bucket can have a chain of extensions. No data is ever dropped.
- Directory doubling: each pointer is copied twice, then the bucket splits. There is no rehash, because the full hash is stored in the key slot. The cost spike is accepted: it happens a logarithmic number of times.
- An empty `hmap` allocates nothing. The first `add` creates the directory with d = 0 and one bucket.
- Scrambles: `.hash` is not changed. The bucket index uses the top bits of `hash * 0x9E3779B97F4A7C15` (Fibonacci hashing); the filter uses the top 8 bits of `hash * 0xC2B2AE3D27D4EB4F`. Both multipliers are odd, so each scramble is a bijection: two different hashes never give the same scrambled hash. A multi-type key (11.3.6.1) XORs the hash of its type with the hash of its value before scrambling.
- Filter: 8 bits per entry, inside a bucket only. A lookup compares the filter first, then the full hash, then `equals` (6.2.1).
- Header of the `hmap` (48 bytes):

| Offset | Content |
|---|---|
| 0 | the directory (`addr`) |
| 8 | d (`u8`), then 7 bytes reserved |
| 16 | the number of items (`uns`) |
| 24 | the counter of open iterators and views (`uns`, 6.4) |
| 32 | C(d) and C(d−1), 4 bytes each |
| 40 | the number of extensions (`uns`) |

- Bucket and extension (the same layout):

| Offset | Content |
|---|---|
| 0 | marker, a bitfield: bit 0 extension, bit 1 stuck, bit 2 key with pointer, bit 3 multi-type key |
| 1 | local depth (`u8`; an extension copies the one of its bucket) |
| 2 | occupancy mask (`u16`, one bit per slot) |
| 4 | 4 bytes reserved |
| 8 | the next extension (`addr`, 0 for none) |
| 16 | 16 filter bytes |
| 32 | 16 key slots of K bytes |
| 32 + 16 × K | 16 value slots of V bytes |

- Key slot (K, in bytes). The bucket always keeps the keys: the pointer refers to the key object owned by the bucket.

| Key type | K | Content |
|---|---|---|
| native up to `uns`, nullable with spare bits (6.2.1) | 8 | the value (the hash is the value) |
| string, wide native, tuple, class | 16 | the full hash + a pointer to the key |
| full-width nullable native (`i64?`, `u64?`, `uns?`) | 16 | tag + value; the null key is the null constant + 0 |
| multi-type, all members exact | 16 | the hash of the type + the value |
| multi-type, any member not exact | 24 | the hash of the type + the full hash + a pointer |

- Value slot (V): as in `map` (6.8.4.1): the exact size of its type (an `i32` takes 4 bytes), inline up to 64 bytes; a string takes 64 bytes (up to 59 bytes of text inline, otherwise a moved string), a class is a pointer, any value above 64 bytes goes through a pointer. The read rule of the values that move with their node is the same as in `map`. `hset` has V = 0. Example: an `hmap<i64, i64>` bucket is 32 + 128 + 128 = 288 bytes.
- Each combination of key layout and value type has its own copy of the code (11.3.12).
- The null key is a normal entry with the null hash (6.2.1); all the nulls are the same key.
- Merges:
  - an extension goes back into the block before it (the last two links of a chain) when their used slots are at most 14 (the capacity minus 2);
  - two sibling buckets merge when their used slots together are 8 or fewer.
- Directory shrink, checked after a merge: C(k) is the number of distinct buckets with local depth k. The directory shrinks by 1 bit when C(d) = 0, by 2 bits when C(d) = 0 and C(d−1) = 0, never by more in one check; then the counts are recomputed with one pass. The header keeps C(d) and C(d−1) when d ≥ 5; for d ≤ 4 the buckets are scanned directly.
- Unsticking (splitting a bucket that has extensions) is tried on a directory doubling and on a shrink attempt, only when the number of extensions is above 0.
- A stuck bucket holds more than 16 keys with the same hash (since the scramble is a bijection): it is marked with bit 1 of the marker and skipped by the scans, and it blocks a shrink below its depth. Widening it may be added later (99).
- Capacity: `.capacity` is the number of entries held without growing the directory. `hmap<K, V>[n]` and `reserve(n)` (for `n` entries in all, 6.6) size the directory and split its buckets so that they fit without a doubling, assuming a well spread hash: n / 11 buckets (a split bucket is about 69% full), rounded up to a power of two. `shrinkToFit()` does every merge that is possible (extensions and siblings) and shrinks the directory as far as the counts allow, without the 2-bit limit of a check; it returns `false` when nothing changed.
- Iterator: three fields: the index in the directory (`uns`), the block (`addr`, the bucket or one of its extensions) and the slot (`uns`). It walks the directory in order, each bucket and then its extensions, following the occupancy mask; after a bucket of local depth L it jumps to the index + 2^(d−L), so each bucket is visited once. The order is unspecified but the same for the same content and history. The counter of open iterators and views blocks the changes (6.4); `for (&)` changes the values in place.

### 6.8.6 Sets `set` (`bset`)

```
set<string> tags = []
bool added = tags.add("red")                 // false if already there
u64 count = tags.add(otherTags)              // number actually added
set<string> both = tags.intersection(otherTags)
tags += otherTags                            // tags.add(otherTags)
tags -= "red"                                // tags.remove("red")
Dog rex = ->dogs[rexToFind]                  // takes the item out of the set
```

- Trait: `SetCollection` (6.7.3).
- `set` (alias `bset`) is sorted (B-tree). A value is held once; adding a duplicate is ignored. Key rules: 6.2.
- `add(val)` → `bool` (true if added); `add(collection)` → number actually added (any collection with the same item type, 6.6); `has(val)` → `bool`; `remove(val)` → `bool`; `->s[item]`; `clear()`; `.len`. No `.capacity`, `reserve`, `shrinkToFit` or `[n]` capacity form.
- Literal: `[...]`, lowered as an empty set plus the adds; `set tags = [...]` takes the item type from the literal (6.3).
- `set` and `hset` differ only in the implementation: they can be mixed in every operation (`add(collection)`, `+=`, `-=`, the new sets and the tests). A result has the kind of the left operand (the set the member is called on). Maps of different kinds stay a compile error (6.6).
- New sets: `union(other)`, `intersection(other)`, `difference(other)` (items of this set not in `other`), `symmetricDifference(other)` (items in one set only). Basic items and strings are copied, objects are cloned (12.6); both originals are unchanged. These members exist on sets only.
- In place: `+=` and `-=` (6.6): `+=` adds a value or a collection (the union), `-=` removes a value or the items of a collection (the difference). `|=`, `&=` and `^=` are not defined. No operators that return a new set (`set1 | set2` is not defined).
- Tests: `isSubsetOf(other)` (every item of this set is in `other`), `isSupersetOf(other)`, `isDisjointFrom(other)` → `bool`.
- Iteration (`for T item in tags`): sorted.

### 6.8.7 Hash sets `hset`

- Trait: `SetCollection` (6.7.3). `hset` is a hash set. The members and the operations are the same as `set` (6.8.6).
- Specific to `hset`: `.capacity`, `reserve`, `shrinkToFit` and the `[n]` capacity form.
- Iteration (`for T item in tags`): in an unspecified order, the same for the same content and history. A new set with an `hset` on the left is an `hset`, so its order is unspecified too.
- Implementation: the code of `hmap` with an empty value type (6.8.5.1). Likewise `set` is generated from the code of `map` (6.8.4.1).

## 6.9 Custom collections (partial)

Users can write their own collection classes in Dflat. A custom collection either implements the traits of 6.7 (`Collection` and one of its three sub-traits), or is a subclass of one of the default collections. This section stays vague for now: everything in it is a first design.

- A collection trait takes the item type as a generic argument, and the class that implements it passes its own type parameter: `class MyCollection<Item> : IndexCollection<Item> { }`. There is no reserved type name; a generic parameter is always declared in the header (11.3).
- A class that implements `Collection` gives a read-only `len` (11.1.4, through `get_len()`, 6.7) and the methods behind the properties and the operators of its trait (6.7). A class that implements a sub-trait gives the `getIterator()` of that sub-trait, which returns an iterator. The iterators of the index collections and of the sets are defined (6.7); the members of the iterator of maps come later.
- `next()` returns a tuple whose last item is the bool `isValid`: `(Item value, bool isValid)`; `writableNext()` returns the item as a writable borrow.
- The cursor and the reference to the collection belong to the iterator class, not to the trait (6.7): each iterator keeps them as private fields, and the collection is declared as its parent with `as parent`.

Sketch:

```
class MyIterator<Item>(&MyCollection<Item> collection as parent) : IndexIterator<Item> {
  uns cursor = 0
  override fun (Item value, bool isValid) next() { ... }
  override fun ((&)Item value, bool isValid) writableNext() { ... }
} atEnd {
  parent.releaseIterator()
}
```

First idea for maps (not yet defined: the members and the cursor types are open):

```
trait MapIterator<Key, Value> : Iterator {
  public fun (Key key, Value value, bool isValid) next()
}
```

- `for x in c` on a custom collection asks `getIterator()` once and calls `next()` until `isValid` is `false`. For the collections of the compiler no iterator object is built: the `for` is a direct loop in C.
- While the iterator exists the collection cannot change its size (6.4). The counter and its checks come from the trait `Collection` through `executeAfter` blocks (11.2), so a custom collection gets them without writing them.
- Open point for classes: whether the default collections (`vec`, `list`, `map`, `hmap`, `set`, `hset`) can be subclassed, and how the overridden methods are dispatched. `arr` cannot be subclassed.
- The indexer `c[k]` of a custom collection is given by the methods `read_pos` and `set_pos` of its trait (6.7).
- `+=` and `-=` call the public members (`add`, `remove`, `removeAt`, 6.6), so a custom map or set gets them from its members.
- For a later version: how a custom class receives the literals, subclassing of the default collections, and lists of pointers and references (with the pointers work).

---



# 7. Nulls

## 7.1 Nulls
- `date`, `time`, `clock` and `lapse` are already nullable and a variable of these types without a value starts as null. A `clock` prints as its number, a `lapse` as its number in its own unit, and null as `null`.

- Basic native types (integers, reals, `dec`, `bool`, `char`, enums) are not nullable by default: nullable only as `type?`.
- Other native types (`string`, `date`, `time`, `clock`, `lapse`, `addr`), pointers and all objects (classes, collections) are nullable by default; `type?` on them is a compile error.
- Strings: `""` and null are the same; `string` is already nullable, so `string?` is a compile error.
- Null propagates in arithmetic; equals only null; sorts first.
- Null object access raises `NullPointerException`; no `x!` force-unwrap.
- `?.` `??` `??=`; `i8 b = maybe ?? 0` unwraps into a non-nullable target.
- `a == null` may call an overloaded `==`; `a is null` always checks the reference.
- Tuples are not nullable

## 7.2 `enforce` statement
`enforce` is a way to guarantee that one or more variables are **not null**
- `enforce a, b { } else { }`: the only unwrap for `type?` (and for weak references). Strings cannot be enforced.
- On a weak reference the block holds a temporary usage of the object; on a read/write counted object (`(@)T`) the block holds its lock (12.7).


---


# 8. Formatting

Used in `$"{expr:spec}"` and `toStr("spec")`. Options in any order; letters act as separators; reserved letters as fill/separator chars are a compile error.

## 8.1 Numbers
In formatting Numbers all letters are case-insensitive except `h` / `H`

| Option | Meaning |
|---|---|
| `bd` `bh` `bH` `bb` `bo` `b64` | base: decimal (default), hex lower/upper, binary, octal, base-64 digits |
| `D[sep][size][mode]` | decimals: `sep` = symbol (`l` = locale), leading `0` on size = fixed, else max; `D0` = none; modes `u` half-up (default), `b` banker's, `c` ceil, `f` floor, `t` truncate |
| `S` / `S-` | sign on negatives only, before the first digit (default) |
| `S+` | sign always, before the first digit |
| `St-` / `St+` | same, at the left edge of the width |
| `S()` | negatives in parentheses |
| `w<n>` | width (no width = no padding) |
| `f<char>` | fill character |
| `a<` `a>` `a^` `a.` | align left, right, center, on the decimal symbol |
| `g<sep><size>[<size><sep>...]+` | grouping, right to left; trailing `r` = left to right; `L` = locale |
| `e[<lit>]<min>` | scientific (8.2) |

- Hex / binary show raw bits (no negative hex). Width overflow prints the full number.
- Special values: `NaN`, `∞`, `-∞`, `null` (`INF` / `-INF` on targets without Unicode).
- Default for reals: as C# (shortest round-trip). Example: `12345.toStr("f0w10")` → `0000012345`.

## 8.2 Scientific format `e`

- Mantissa: one non-zero digit before the decimal symbol (as C++, C#, Rust, Python).
- `<lit>`: text printed literally before the exponent. `+` in it = exponent sign always; `-` = exponent sign on negatives only. Escapes: `\[ \] \+ \-`; everything else is literal.
- `<min>`: minimum exponent digits; `0<n>` = zero-padded, `#<n>` = space-padded; absent = unpadded.
- Combines with `D`, `S`, `w`, `f`, `a`.
- Plain `e` = `e[e+]D6a<` (exponent unpadded).
- Zero prints `0`; special values as in 8.1.

Examples with `1234.56`:

| Spec | Output |
|---|---|
| `e[E+]02D2` | `1.23E+03` |
| `e[e^]02D2` | `1.23e^03` |
| `e[e-]#3D2` | `1.23e  3` |
| `e[x10^]D1` | `1.2x10^3` |

## 8.3 Strings

| Option | Meaning |
|---|---|
| `w<n>` / `wc<n>` | width in characters / display columns |
| `f<char>` | fill |
| `a<` `a^` `a>` | alignment |
| `t<'…'` `t>'…'` | truncate keeping start / end, with marker (counted in the width) |
| `cU` `cL` `cS` `cT` | upper, lower, sentence (`Hello world`), title (`Hello World`) |
| `q` | quoted and escaped |
| `s` `sl` `sr` | trim both / left / right |

## 8.4 Dates and times

- C# custom pattern letters: `d f F g h H k m M s t y z`.
- A `time` uses only the letters of the time parts: `h H m s f F t k`. A date-only letter (`d M y g z w q i r e j`) used with a `time` is a compile error.
- Additions: `w` week, `q` quarter, `i` ISO week, `r` ISO year, `e` day of year (3 wide), `j` day of week; a doubled letter pads with a space.
- Alignment: `a<20`, `a^20`, `a>20`; no truncation.
- Locale names and first day of week: later.

# 9. Parsing
Integers, reals, `dec`, dates and times offer a `<type> <type>.parse(text[, default[, format]])` and bool `<type>.tryParse(text, (&)<type> result[, format])`

```
i32 count = i32.parse(text, 0)
i32 total = i32.parse(text)                       // default: the zero value
bool parsed = r64.tryParse("12.3", (&)amount)
i32 code = i32.parse(text, 0, "0x{bh}")           // with a format
i32 balance = i32.parse(text, 0, "{S}{S()}")       // "-10" or "(10)"
```

- `T T.parse(string text, T default = zero, string format = none)` and `bool T.tryParse(string text, (&)T result, string format = none)` exist for integers, reals, `dec`, `date` and `time`. On failure `parse` returns the default, `tryParse` returns `false`; no exception.


Without a format:
- Ignorable characters before and after the number are skipped; only the first number is read, and a space or any other non-number character ends it. No digits → the default.
- `.` is the only decimal symbol (English); thousands separators are not accepted, so `,` ends the number: `"1,234.5"` → `1.0`, `"1.234"` → `1.234`; as integers both give `1`. Locale separators need a format.
- Integers drop the decimals.
- Signed types: a `-` right before the digits is the sign (`"x-12"` → `-12`). Unsigned types: a minus gives `0`, or an `OverflowException` if a specific catch exists.
- Out of range saturates, or raises `OutOfRangeCastException` if a specific catch exists (silent).
- Reals: exponent accepted (`"1.5e3"` → `1500.0`); `NaN`, `∞`, `-∞`, `INF`, `-INF` are recognized; extra digits round to the nearest representable value. `dec` uses banker's rounding.

With a format:
- Layout: `"<prefix>{spec}[{alternative}...]<suffix>"`, using the 8.1 options inside the braces. The prefix and suffix are shared by all alternatives; adjacent placeholders are alternatives for the same value, tried in order, the first that matches wins.
- Parsing is strict: nothing is skipped automatically, not even spaces.
- Hex digits are case-insensitive. `\{` and `\}` are literal braces.
- Static text between placeholders makes the format invalid. A mismatch or invalid format gives the default / `false`.
- For really different inputs, call `tryParse` once per format.

Dates and times:
- Without a format, a `date` is parsed only from `yyyy-MM-dd [HH[:mm[:ss]]]`, `yyyy/MM/dd [HH[:mm[:ss]]]` or ISO 8601. Input with no zone is UTC; input with a zone is converted to UTC.
- A `time` is parsed from `HH[:mm[:ss[.fff]]]` (hours `0`–`23`) or `h[:mm[:ss[.fff]]] am/pm` (hours `1`–`12`; `am` / `pm` case-insensitive, with or without a space, at the end; `12 am` is midnight, `12 pm` is noon).

The format is specified exactly in the same way the printing format is specified.


# 10. Programming statements

## 10.1 Control flow
`if` does not require parentheses for the expression. It is followed by a statement `{ ... }` or a single instruction prefixed with a `then`

```
if a > 0 {
} else {
}
if a > 0 then jump done           // single statement, same line, no else
```

## 10.2 while
Supported both `while {}` and `{} while`.

```
while i < n { }
{ a-- } while a > 0
```

## 10.3 for
```
for u8 i = 0..10 { }
for u16 i = 0..=100 step i += 2 { }
for u8 i = 10..=1 { }                 // start > end: counts down
for char c in "text" { }
for u64 k in 0..list.len { }
for string message in messages { }
for (&)Task task in tasks { }               // read/write borrow of the items
for (string key, i32 value) in map { }      // map: (key, value) tuples
for (var key, var value) in map { }
for var (key, value) in map { }
```
- A range whose start is greater than its end counts down.
- The `for` variable always has a type; `var` is accepted when the type can be inferred (also in tuple forms); an untyped `for x in ...` is a compile error.
- The loop variable is read-only by default; `(&)Type variable` makes it a read/write borrow of the item. Rules for the items, keys and basic values: 6.4.
- Iterating collections (default iterators, maps, ranges as virtual collections): [Ref: Collections]

## 10.4 jumping
```
break                             // innermost loop
break #Found#                     // to label #Found#
continue                          // no label
jump #retry#                      // to a label
```

- `break` / `continue` outside a loop: compile error.
- Jumps go to the same or an outer block, never into an inner one.

## 10.5 Switch

```
switch val {
  1: one(), 2: two()
  3, 4, 5: { b = 1; b++ }
  6..=9:                          // range; the body continues on the next lines
    b = 2
    jump 3
  Red: jump 3                     // jump to another case by value
  default: jump #out#             // explicit label
}
```
- Inside a switch, `jump name` targets a case; `jump #name#` always targets a label.
- No fall-through; `default`; a jump loop between cases is a level 1 warning.

## 10.6 Switch expression

```
bool res = val ? { 1: true, default: false }
string yn = ok ? { "yes", "no" } // Equivalent to other languages ternary operator
i32 sign = value ? { ..=-1: -1, 0: 0, 1..: 1 }
```

- Case values: literals, enum entries, sets (`3, 4, 5:`) and ranges (`400..=499:`, `..-1:`, `1..:`); the same in switch expressions.
- A case body is a block, a statement on the same line (several cases can share a line, separated by commas), or the statements on the following lines up to the next case.



# 11. Classes and traits

## 11.1 Classes

```
public class Circle(r64 radius) : Shape {
  public Circle() : Circle(1.0), Named("circle")
  override fun r64 Area() { r.Math.PI * radius * radius }
}
```

- Header: `class Name(params) : [parent class], [traits]`. `Root.Class` is the implicit base of every class and is never written; writing it in a header is a level 1 warning.
- The parent class, if any, goes before all traits; a class listed after a trait is a compile error. Names are resolved from the context; when a name is ambiguous the namespace qualifies it.
- A class and a trait with the same name in the same namespace is a compile error.
- `notclonable` before `class` (after the visibility: `public notclonable class File`) makes the class not clonable: `clone()` and `a!` on it are a compile error (12.6).
- `atEnd` is a reserved word: it cannot be the name of a type, trait, variable, parameter or field.
- No `new`: `Circle c = Circle(2.0)`.
- `this` is the current object. It is optional (members are reachable by name) and needed only to disambiguate (`this.name = name`). It can be passed as an argument (`registry.Add(this)`), following the normal parameter rules; `->this` is a compile error (an object cannot give away its own ownership).
- Type checks: `if v is Circle c { }` (reference, `c` scoped to the block), `if v! is Circle c { }` (copy); upward checks are a level 2 warning.


## 11.1.1 Class constructors and default constructor

```
public class Foo(i32 bar, string privateBaz) {
  public i32 bar                       // sets the visibility of the field bar
  u64 doubled = 2 * 2                  // field initializer

  public Foo(i32 total) : Foo(total, "x") {
    doubled = 7                        // specific initialization code
  }
}

Foo foo = Foo(5, "a")
```

- A constructor has a visibility, the class name and parameters. Several constructors can exist as long as their parameters differ (13.2 rules, including the ambiguity error).
- Primary constructor: written in the class header; its parameters become private fields. Redeclaring a name in the body (with or without `public`) does not create a new field: it sets the visibility of the existing one. The same name with a different type is a compile error. An initializer on the redeclaration is allowed (level 3 warning, the value in the body wins).
- Primary constructors are better without default values in the parameters; secondary constructors can have them (style, not enforced).
- Secondary constructors chain `: Class(...)`, plus parent and trait constructors, and can have a body with their own initialization code.
- Default constructor: generated like in C#, when the class declares no constructor.
- Construction order: parent, traits in listed order, class body.
- Private constructors are allowed; only the class body (its static functions included) can call them. A subclass cannot, so a class with only private constructors cannot be inherited.
- A constructor that throws: the exception propagates and the object is destroyed; the `atEnd` blocks registered so far run, and fields not yet reached hold their zero value (null for objects).
- A trailing `atEnd` after a constructor is a destructor fragment (11.4, 14.2). An `atEnd` written inside the constructor body is a normal scope cleanup.

## 11.1.2 Class functions and properties

```
public class Foo(i32 a) {
  public static i32 count = 0
  public static const i32 MAX_ITEMS = 100
  public static fun Foo create(i32 value) { Foo(value * 2) }
}

Foo foo = Foo.create(5)
```

- `override` mandatory; `parent.Method()` reaches the overridden version. An overriding function takes the visibility of the original: none is written on it.
- Visibility: private by default, `public`, `internal` (namespace + sub-namespaces). A subclass sees everything when the parent's source is available, public only for compiled libraries (at its own risk). No `sealed` (private constructors give the same effect, 11.1.1).
- Static members: `static` goes after the visibility and before `fun` or the field type. Functions and properties; accessed as `ClassName.name`.
- A static function has no `this` and cannot touch instance fields; it can call the private constructors of its class.
- Static members are inherited by subclasses (`Child.create()`). A static function cannot be overridden, and declaring one with the same signature in a subclass is a compile error.
- A generic class has one copy of its static members per type instance (`Stack<i32>` and `Stack<string>`).
- Static properties: an initializer is allowed, without one they hold the zero value. They are initialized at program start in declaration order (a compiler option allows lazy initialization on first use). An initializer that reads a static property of another class is evaluated on demand; a cycle is a compile error.
- `static const` declares a class constant (UPPER_SNAKE_CASE, 1.1); a plain `static` property is mutable.
- Static properties are never touched by `atEnd`.

## 11.1.3 Equality

```
override fun bool equals(T other) {
  if T is i32 then return value == other
  if T is string then return value == i32.parse(other)
  return false
}
```

- `==` on class objects resolves in this order: `equals` if defined; otherwise an operator `==` overload; otherwise identity (same object).
- `!=` is always the negation of what `==` resolves to and cannot be overloaded.
- Defining both `equals` and an operator `==` is a level 2 warning (the overload is dead code).
- The identity of a key in a hash collection: two keys are the same when the hashes match and `equals` agrees (6.2.1). A class that overrides `equals` should keep equal objects with the same hash, otherwise equal keys can be stored twice. No warning is given for an override of `get_hash()`, `equals` or `compareTo`.
- `bool equals(T other)` is a generic function: `T` is implicit and is the type of the argument; inside a generated instance `other` has type `T`. This allows comparing with other types, and each type decides which ones it supports.
- `T is type` checks the generic type at compile time (the branch that does not match is not emitted); `other is type` checks the value at runtime (11.1). Both can be used in the same function.
- A more generic type can match before a more specific one, so the order of the checks matters. A branch that can never run because an earlier check always matches is a level 3 warning.
- Every path of `equals` must return a value, otherwise it is a compile error. The upward-check warning (level 2) does not fire in generic code when a check is true for one `T` only.
- Null is handled by the compiler before `equals` is called: the function never receives a null argument.
- `Root.Class` offers `get_hash()` to implement the `.hash` of the object (6.2.1) and `equals(any?)`. Ordering is given by the trait `Comparable<T>` (11.2.1).
- The default `equals` (overridable) is consistent with the default hash: equal objects have the same hash.
  1. The classes must be exactly the same.
  2. The native fields are compared first, the cheap ones (integers, dates, ...) before strings and composite fields; the comparison stops at the first difference.
  3. Owned objects are compared recursively with the same default `equals`. Ownership cannot form a cycle, so the recursion ends.
  4. Collections, iterators, views, every sub-collection class and every reference field (an object that is not owned): equal only if it is the same object (the same address); the content is never compared. Both null: equal; one null: different. Two objects that refer to different but equal objects are not equal by default; a class that needs it overrides `equals`.
  - A clone is not necessarily equal to the original (a collection field of the clone is another object).
  - `has`, `indexOf` and `remove(v)` use `equals`; they do not involve the identity of keys.

## 11.1.4 Fields and properties

A field and a property are the same thing: the compiler tells them apart, developers do not need to. A plain field such as `i32 count` is a read/write property with automatic accessors (a private stored value plus a getter and a setter).
```
public i32 myFieldProperty = 3
public i32 mySecondProperty { R: { field + 1 } W: { field = value - 1 } } = 3
public readwrite i32 myProp { R: { field * 2 } }              // W is the default: field = value
public i32 level { R: { field } private W: { field = 0 } }    // private writer
```

- Access: a property is read/write by default. The access `readonly`, `readwrite` or `writeonly` is written when the body is not defined and must be provided by the class (trait members, 11.2). When blocks are written they say what is allowed. Reading a property that cannot be read, or writing one that cannot be written, is a compile error.
- In a class, `readonly` on a field without accessor blocks is a shorthand for a public reader and a private writer: `public readonly i32 width` is the same as `public i32 width { R: { field } private W: { field = value } }`. The class can still assign it; from outside it can only be read. In a trait, `readonly` keeps the meaning above.
- Accessor blocks: `R: { ... }` calculates and returns the value (as in any function, the last expression is the result); `W: { ... }` runs when the property is assigned and gets the new value in `value`. A block that is not written gets its default: `R` is `{ field }` and `W` is `{ field = value }`.
- `field` is the stored value of the property and `value` the incoming value of `W`. Both exist only inside the accessor blocks. Outside the accessors the name of the property always goes through them, so there is no recursion.
- The storage exists only when an accessor uses `field` (the default accessors do). A purely computed property takes no memory.
- A start value is written after the property (`= 3`) and is allowed in classes only, never in traits. It goes through the `W` code when one is defined (in the second example `field` starts at 2 and `R` returns 3).
- Visibility: a visibility keyword before `R` or `W` sets the visibility of that accessor; the visibility of the property applies to the other one.
- An exception that is not caught inside an accessor is a `Panic` (15.5): accessors have no error part.
- Primary constructor parameters (11.1.1) become fields with the default accessibility rules. They can be redeclared in the body of the class or trait when they need something more specific.
- Static properties work in the same way, but their accessors can use only static functions and other static fields or properties (11.1.2).
- Moving a property out with `->`: 12.4.

### 11.1.4.1 Field groups

```
public class Sprite {
  public pos { r32 x, r32 y }
  public scale { r32 x = 1.0, r32 y = 1.0 }
}

s.pos.x++                      // a plain field of the sprite
s.pos = (10.0, 20.0)           // sets pos.x and pos.y in order
var p = s.pos                  // a tuple copy: (r32 x, r32 y)
```

- A field group is a name that organizes fields inside a class. It is not a struct and not a tuple: it is not a value of its own.
- It is written as the name of the group followed by the fields in braces, separated by `,`. It has no type before the name, so it is not confused with a property and its accessor blocks.
- Each field of a group is a field of the class (`s.pos.x`), with the rules of 11.1.4: start values and accessor blocks. The visibility of the group applies to all its fields.
- The group alone cannot be passed, returned, stored or referenced (`&s.pos` is a compile error).
- Assigning a tuple to the group (`s.pos = (10.0, 20.0)`) sets its fields in order; the tuple must have the same number of items, each convertible to its field. Reading the group as an expression gives a tuple copy with the names and types of its fields.

## 11.1.5 Structs (to be defined)

Dflat will have structs: generic value types that do not live on the heap, like the Rust structs. They are not defined yet and need a section of their own. They are different from `dfoot` (5). Two points to settle: a struct may not be allowed to hold objects or, if it can, the objects it holds are owned by the struct.

## 11.2 Traits
They are similar to C# interfaces and Rust traits

```
trait Named {
  public string name
  Named(string n) { name = n }
  public fun string Label()                     // no body: must be implemented
  public fun string Hello() { $"Hi {Label()}" }
}
trait Shape : Named { public fun r64 Area() }
```

- Single parent class (listed first in the header); any number of traits; base of everything: `Root.Class` (implicit, never written, 11.1).
- Traits are implemented only in the class header; a trait cannot be added to an existing type (built-in or library) from outside its declaration.
- Traits: properties, methods with or without body, constructor when they have properties; one parent trait; a supertrait inherited twice exists once.
- A property in a trait declares how it is accessed (read only, read/write or write only, 11.1.4). It can have a default implementation (`R` and `W` blocks); when it has none the class must implement it, otherwise it is a compile error. A class can use the implementation of the trait or override it with `override`, giving the code for everything; a conflict is a compile error and a part not specified falls to the default implementation. A property that is read only in the trait stays read only in the class.
- A trait has no start values (`= 3`). Its constructor can set values; the `W` code is used when one is defined.
- How a property is read or written through a value of a trait type (`any Shape`) is left to the implementation of the compiler.
- Same public member in two traits: the qualified name is needed only for the clash, `TraitA.member` (inside the class), `px.TraitA.member` (outside). The unqualified name is a compile error that asks for the qualified name. Private members never clash: the compiler aliases them (`TraitA.member`, `TraitB.member`) without anything written, because only the code of their own trait sees them.
- Visibility in a trait: the rules of 1.6.1, as in classes. A member without a visibility keyword is private; a public member is written `public`. A private member is seen only by the code of its trait (methods, accessors, constructor, `executeBefore` and `executeAfter` blocks), not by the class that implements the trait. A method without a body that the class implements keeps the visibility of the trait: the implementation is written with `override` and no keyword.
- Sub-traits: a sub-trait reads every member of its parent traits, the private ones included, read-only, and can call all their methods, the private ones included. To change a private field of the parent it calls a method of the parent. This is not `asparent`, which opens a member to the child objects (12.5).
- Initialization: a public trait field must be initialized in every constructor of the class that implements the trait, directly or through the trait constructor; otherwise it is a compile error (`class ABC must initialize member "field" from trait MyTrait`). A private trait field is initialized only by the trait (its constructor); when the trait does not set it, it keeps its zero value (null for objects).
- Diamond: a supertrait inherited twice exists once, with its fields. It is a compile error when two traits give different default implementations of the same member of the shared supertrait, or call the constructor of the shared supertrait with different arguments.
- Trait-typed values: the compiler chooses static or dynamic dispatch; force with `specific Shape` (one type, known at compile time) / `any Shape` (any implementing type, dispatched at runtime). `list<any Shape>` holds mixed shapes.
- `executeAfter`: a trait method can carry a block `executeAfter { }`. The class must implement the method; the block runs after the implementation of the class and cannot be overridden or removed by the class.
  - `executeAfter` is one contextual keyword, written like `atEnd`; it is a keyword only right after the signature of a trait method.
  - The block runs only when the implementation succeeds (if `getIterator()` fails, the counter of 6.4 is not incremented). This is the difference with the `atEnd` of a function (14.2), which runs on every exit, exceptions included.
  - The block reads the return value as `<method>.result` (for example `getIterator.result`), read-only: it cannot change it.
  - The blocks are chained: every trait of the hierarchy that defines an `executeAfter` for the method runs its block, like `atEnd`: from the most specific trait to the root (`IndexCollection`, then `Collection`), once per call. A call to the parent implementation inside the class does not start the chain again.
  - A method with a default body and a block is written `fun f() { default body } executeAfter { ... }`. The class can override the default body; the block still runs.
- `executeBefore`: a trait method can carry a block `executeBefore { }`, with the same rules as `executeAfter`, except that it runs before the implementation of the class. The class can override the method, but the code of the trait always runs first. When the block raises an exception, the implementation does not run. Both blocks can be on the same method (`executeBefore` first). The order of a chain of `executeBefore` blocks is decided when it is needed.
- The trait `Collection` uses `executeAfter` to provide the counter of open iterators and views and its checks (6.4), so every collection gets them, the custom ones included. The counter is a private field of `Collection`; `IndexCollection` (a sub-trait, so it reads the counter but cannot write it) increments it through the private method `acquireIterator()`. `releaseIterator()` is `asparent`: only the children of the collection (iterators and views) can call it, even through a read-only reference, and it is called only by their default `atEnd`; its default body is empty, so a class writes it only when it needs more, and the `executeAfter` takes the counter back in every case; a release with the counter at zero raises `InvalidIteratorReleaseException` (not silent, 15.3). `checkUnlocked()` raises `ModifyingIteratedCollectionException` while the counter is above zero: a collection that can change its size must not change it while it is iterated, and the trait methods that change the size will call it from an `executeBefore` (6.4).

```
trait Collection<Item> {
  uns iteratorCounter                            // private: only Collection writes it
  fun acquireIterator() { iteratorCounter++ }    // private: sub-traits can call it, child objects cannot
  fun checkUnlocked() {                          // private: for the executeBefore of the changing methods
    if iteratorCounter > 0 throw ModifyingIteratedCollectionException()
  }

  // len, has(v), ->c[k] and clear() (6.7) are public

  asparent fun releaseIterator() { }
    executeAfter {
      if iteratorCounter == 0 throw InvalidIteratorReleaseException()
      iteratorCounter--
    }
}

trait IndexCollection<Item> : Collection<Item> {
  public fun IndexIterator<Item> getIterator()
    executeAfter {
      acquireIterator()
    }
}
```

### 11.2.1 Basic traits

| Trait | Members | Description |
|---|---|---|
| `Comparable<T>` | `int compareTo(T value)` (mandatory) | ordering: returns -1, 0 or 1 (`int` is the signed native integer); used by the sorted collections and by `sort` (6.2.2, 6.6) |
| `Keyable` | a private counter of the collections that hold the object as a key and the stored hash (both `uns`) | the class can be a key (6.2); checks that its hash does not change while it is a key (6.2.3) |

- `Comparable<T>` replaces `.order`, which is removed. When a class provides nothing, the default order of 6.2.2 (compare the hashes) is the default implementation.
- `.hash` is not a trait: it is a property of every type (6.2.1).
- `Keyable`:
  - A class declares it explicitly; it is never implied by the use of the class as a key type, so it cannot be derived by mistake. A class without `Keyable` used as a key type is a compile error that names the trait.
  - It is inherited like any other trait, with no opt-out for a subclass. A custom trait can extend it, as long as the logic of `Keyable` is kept.
  - The counter and the stored hash are one per object, shared by every collection that holds it.
  - Two `executeAfter` blocks (11.2), on `get_hash()` and on `compareTo`, so the check runs after every override and cannot be removed. Both start from the counter: at 0 nothing is done (the object is not a key, its hash can be anything); when it goes from 0 to 1 the hash is computed and stored; above 0 the hash (`get_hash.result`, or recomputed through `get_hash()` in the block of `compareTo`) is compared with the stored one, and an exception is raised when they differ.
  - The check runs only for `Keyable` objects: value-type keys (natives, strings, tuples of them) are never hashed again.
  - Each use of a key by a collection runs the check. A key that is already in the collection keeps the counter as it is; a new key is checked first and, if the check passes, it is added and the counter is incremented. The counter counts entries, not uses. Reading a missing key (`m[x]`) runs the check too, without incrementing, so a problem shows up as early as possible.
  - The stored hash is never cleared: at 0 it is overwritten at the next step from 0 to 1. Example: `Person p`, `m[p] = true`, remove `p` (counter 0), `p.age++`, `m[p] = true` (valid: technically a new key), `p.age++`, `m.tryGet(p, (&)out)` raises the exception.
  - The exception for a changed hash is a normal exception (not a panic, not silent; its name is open, 99). It reports the problem and does not repair the collection. The object is invalid as a key (not as an object) until its hash is exactly the stored one again. After the exception the behavior of the collection is no longer deterministic.
  - A `Keyable` class with a field of a collection, iterator or view type (owned or a reference) gets a level 1 warning (19.3): its identity can change without changing its hash.
  - Use the trait `Keyable` on a class only if it really needs to be used as a key in a collection. It substantially increases the cost of comparing objects and of calculating their hash. The reason: a hash check is added to every `get_hash()` and `compareTo` call while the object is a key.

## 11.3 Generics

Rust model (code generated per type, checked at the definition), Dflat syntax.

### 11.3.1 Functions

```
fun Item min<Item : Numeric>(Item first, Item second) {
  first.Compare(second) ? { first, second }
}

trait Priced {
  fun r64 Price()
}

fun Item pick<Item : Numeric, Key : Named && Priced>(Item value, Key key) { }

fun r64 sum<Source : Sequence>(Source items) where Source.Item : Numeric { }
```

- A constraint is a trait, a class or a set (11.3.6). A class names the class and all its subclasses; a trait names every type that implements it.
- Constraints on the type parameters are written inline, `<Item : Numeric>`, however many parameters there are. `where` is for the constraints that are not on a plain parameter: the placeholder of a trait (`Source.Item`, 11.3.4) or another type expression. Both forms can be used in the same declaration. Entries of a `where` clause are separated by commas.
- Repeating in `where` a constraint already written inline on the same parameter (`<Item : Numeric> ... where Item : Numeric`) is a level 1 warning. A different constraint on that parameter in `where` is a compile error: join it inline with `&&`.
- Several constraints are joined with `&&`: the type must satisfy all of them. No negative constraints.
- There are no `Hashable`, `Orderable` or `Printable` constraints: every type already has `.hash` and `toStr()` (6.1, 6.2), and `.hash` is sized to the architecture (`uns`). Ordering is the trait `Comparable<T>` (11.2.1).
- The body is checked at the definition, using only what the constraints guarantee, against every type that can reach it (11.3.6, 11.3.11).

### 11.3.2 Classes and traits

```
class Stack<Item> {
  list<Item> items
  fun Push(->Item value) { items.add(->value) }
  fun Item Pop() { ... }
}

trait Container<Item> {
  fun Item Get(u64 index)
  fun Add(->Item value)
}

trait Indexed<Item> : Container<Item> {         // a generic parent trait: the child passes its parameter to the parent
  fun Item First()
}

trait HashCollection<Item> {
  public fun bool has(Item value)
}
trait ABCCollection<Item> : HashCollection<Item> {      // the same list of arguments
  fun Item first()
}
trait ABCMap<Key, Value> : HashCollection<Value> {      // a part of the parameters (the pairs are searched by value)
  fun Value lookup(Key key)
}
trait Names<Item> : Container<string> {                 // a concrete type
  fun string joined()
}

class Names : Container<string> { ... }
class Cache<Key, Value> { hmap<Key, Value> entries }
class Registry<Item : Named> { list<Item> items }

Stack<i32> numbers = Stack<i32>()
```

- Several parameters are comma-separated; types and value parameters can be mixed.
- A generic trait can be implemented several times by one class with different types.
- A generic trait can extend one generic trait (the parent trait): `trait ABCCollection<Item> : HashCollection<Item>`.
  - The arguments of the parent are types built from the parameters of the child: the same list (`ABCCollection<Item> : HashCollection<Item>`), a part of them (`ABCMap<Key, Value> : HashCollection<Value>`) or a concrete type (`Names<Item> : Container<string>`).
  - The parent can itself have a parent: the chain goes up one level at a time, and each level is a copy made for its own arguments (11.3.12 counts them).
  - A class that implements the child implements the whole chain and must provide the members of every trait of it, with the arguments of the child: `class Words : ABCCollection<string>` is also a `HashCollection<string>`. A value of the child trait is accepted where the parent is expected.
  - A trait has one parent: a second parent trait is an error. A parent that has placeholders (11.3.4) is not supported yet.
- `Stack<i32>` and `Stack<string>` are two different classes made from the same code: they have nothing in common as types, and a function that takes one does not accept the other.

### 11.3.3 Value parameters

```
class Grid<u64 Width, u64 Height, Cell> { arr<Cell>[Width * Height] cells }
```

- A value parameter is declared with its type first; it is a compile-time constant and part of the type.
- Allowed types: integers (`i*`, `u*`), `bool`, `char`, `r16` `r32` `r64` `r128`, `dec`.
- NaN → compile error; `-0` is normalized to `0`.
- Arithmetic in types: only `+`, `*`, `<<`, on unsigned integers, saturating at the max. Signed operands are cast to unsigned (negatives → 0); real operands are cast to unsigned with ceiling.

### 11.3.4 Placeholders

```
trait Sequence {
  placeholder Item
  public fun (Item value, bool isValid) next()
}

class Countdown : Sequence with (Item is u32) { ... }

fun printAll<Source : Sequence>(Source items) { ... }
```

- `placeholder` declares a type inside a trait that each implementing class fixes once, with `with (Name is type, ...)` in the header.
- Users of the trait don't name the placeholder.
- A constraint on the placeholder uses `where` (11.3.1): `where Source.Item : Numeric`.

### 11.3.5 Inference

- The compiler infers type arguments; if it can't → compile error. Explicit arguments are always allowed: `min<u8>(3, 5)`.
- A name followed by `<…>(` is read as a generic call (lookahead). When the code could also read as comparisons → level 3 warning; parentheses force comparisons.
- Unsuffixed literals follow 1.3: `min(3, 5)` → native signed integer.

### 11.3.6 Generic sets

```
Generic Strings { char, string }
public Generic Shapes { Circle, Square }
Generic Anything { Numeric, Strings }
Generic NullableSmallNums { i16, i16?, i8, i8?, u8, u8?, u16, u16? }

fun Item smaller<Item : Numeric>(Item first, Item second) { ... }
fun r64 average(Numeric first, Numeric second) { ... }
```

- `Generic Name { types }` is a named set of types, computed at compile time. The members are built-in types, classes, traits or other sets. The name starts with an uppercase letter.
- A set is declared at namespace level. Its visibility follows the other declarations: private by default, `public` or `internal` to expose it. Sets declared inside a class: open item (99).
- A class member means the class and all its subclasses (the code is checked with what the class guarantees). A trait member means every type implementing it.
- A set is used as a constraint (`<Item : Numeric>`): every use of `Item` in one call is the same type. It can also be written directly as the type of a parameter (`Numeric first, Numeric second`): each argument is independent. Only the combinations that are actually called are generated.
- As a constraint or as the type of a parameter, a set exists only at compile time: it names alternatives. `Box<Num>(x)` is a constructor call whose argument selects the member (11.3.9); `var b = Box<Num>(5.5)` is a `Box<r64>`. A set can also be the type of a value (11.3.6.1).
- Operators and members are checked only where they are used, against every type that can reach the use. With `Generic Mixed { uns, char, string }`, the expression `first < second` in `fun Compare(Mixed first, Mixed second)` needs `<` between all combinations of the members.
- The body of a generic class or function that receives a set is compiled once per member (or per combination); a branch that does not apply to the member is not compiled (11.3.7).

#### 11.3.6.1 Generics as value types

```
Generic Nums { i32, u64, u8 }
Generic Pets { my.ns.Dog, my.ns.Cat }

Nums k = 5b                              // u8
k = count                                // count is an i32
if k is i32 i { print(i) }               // i is the value as i32, only in the block
string text = k is ? { i32 x: "int", u64 x: "big", u8 x: "small" }
map<Nums, string> names = [5: "five", 5ul: "big five"]   // compile error for 5: i32, u64 and u8 all fit
Pets p = Puppy()                         // tag "my.ns.Dog": Puppy is a subclass of Dog
```

- A set can be the type of a variable, a field, a parameter, a return value, a key or an item of a collection. It is a closed union: every possible type is known at compile time (unlike `any`).
- A value of a set type is the pair (tag, value). The tag is the hash of the type of the member (6.2.1): natives and the default collections without namespace (`"i32".hash`), classes fully qualified (`"my.ns.Dog".hash`), generic arguments inside `<>` (`"list<my.ns.Dog>".hash`), tuples as `(tuple,...)` (`"(tuple,i32,string)".hash`), nullable types with `?` (`"i64?".hash`). The compiler knows the tag of every member, so no other tag exists.
- For a class member the tag is the member as it is listed in the set, never the dynamic class of the object: a `Puppy` in `Pets` has the tag of `my.ns.Dog`. Inside the case the object is still checked by its real class (`if x is Puppy y`). The tags of a set are therefore closed: one per listed member.
- Storage: the value is inline up to `uns` size, otherwise a pointer (a native wider than `uns`, a string, an object, a tuple), owned by the value. A null value of a nullable member is the null constant (6.2.1).
- Assignment: a value of a member type converts implicitly; any other type is a compile error. A number literal takes its default type (1.3) when the set lists it, otherwise the only listed type of the same kind (integer or real); when more than one fits it is a compile error, and the literal is written with its suffix or cast (`5b`, `(u64)5`).
- Reading: only through `if k is T x { }` (one type; `x` exists only in the block) or `k is ? { T x: ..., ... }` (several types, with the coverage check of 11.3.7). Both compare the tag of `k` with the tag of `T`. `x` is a copy for natives and strings, a borrow for objects. A type that is not a member of the set is a compile error. No member, operator or cast works directly on `k`.
- Equality, hash and order come from the pair: two values are equal only when both the tag and the value are equal (`'A'` and `i32 65` differ, `i32 0` and `r32 0.0` too); the hash combines the tag and the hash of the value (`$`, 6.2.1); the order compares the tags first, then the values (6.2.2). As a key of a `map` or a `set` it uses the multi-type layouts of 6.8.4.1.
- A set used as a constraint or as the type of a parameter keeps its meaning: one generated copy per type, at compile time.
- Not available on microarchitectures.

Predefined sets:

| Set | Members |
|---|---|
| `Signed` | `i8` `i16` `i32` `i64` `i128` `i256` `int` |
| `Unsigned` | `u8` `u16` `u32` `u64` `u128` `u256` `uns` |
| `Integers` | `Signed`, `Unsigned` |
| `Reals` | `r16` `r32` `r64` `r128` `real` |
| `Numeric` | `Integers`, `Reals`, `dec` |
| `Strings` | `char`, `string` |

- `int`, `uns` and `real` are aliases known at compile time (3.1): they match the type they stand for on the target, and never count as a duplicate case or as an extra copy.
- `char` is not part of `Numeric`; `dec` is.

### 11.3.7 Type switch

```
Generic Convertible { Numeric, char, string }

fun r64 toReal(Convertible item) {
  r64 result = item is ? {
    Numeric: (r64)item,
    char: (r64)(u32)item,
    string: r64.parse(item)
  }
  result
}
```

- `value is ? { type: expression, ... }` is the switch expression of 10.6 applied to the type of `value`. A case is a type, a class, a trait or a set; `default` is allowed.
- Only the matching case is compiled in each copy.
- Coverage is checked at the definition: a type that can reach the switch and has no matching case and no `default` is a compile error.
- The first matching case wins, so the order matters. A case that can never match because the earlier ones already cover all its types is a level 2 warning (dead code).
- `T is type` (11.1.3) stays the form for one check; `value is ? { }` is the form for many.
- Cases are separated by commas.
- On an object the switch is chosen at run time by the class of the object (`if v is Circle c`, 11.1, is the form for one check). A case can bind a variable: `x` is the object seen as the case type; it is a borrow (`->x` is a compile error, `->obj` still works). Each case is its own scope, so the cases can use the same name.

```
obj is ? {
  Dog x: x.woof(),
  Cat x: x.meow(),
  Animal: print("Not supported")
}
```

- The more specific classes go first: a case after a parent class that already matches is never reached (level 2 warning).

### 11.3.8 Null in generic code

- A set that contains a type that can be null forces the generic code to handle null: numbers with `?` (`i16?`), `date`, `time` and objects (classes).
- `string` does not count: a null `string` always falls back to the empty string (7.1).
- `T?` is not allowed in generic code. Nullable alternatives are listed in the set (`NullableSmallNums` in 11.3.6).

### 11.3.9 Converting a value to a member of a set

```
Generic ABC { u64, u32, u16 }     // a u8 value becomes u64 (leftmost)
Generic CBA { u16, u32 }          // a u8 value becomes u16
Generic DEF { u64, u8 }           // a u8 value stays u8 (exact type first)
Generic Num { i64, r64 }          // a u8 value becomes i64 (unsigned to a wider signed)
Generic Narrow { i16, u32 }       // an i32 value: compile error, no member keeps the value
```

The value on the right is evaluated first. If it converts implicitly (section 6) to a member of the set, it converts; otherwise it is a compile error. The member is chosen in this order:

1. The exact type of the value (same macrotype and same size).
2. A member of the same macrotype that holds the value, leftmost first.
3. A member of a compatible macrotype reached by an implicit conversion of section 6 (unsigned to a wider signed), leftmost first.
4. Nothing fits: compile error. The message lists the members tried and why each one failed.

- For classes: the exact class, then the nearest parent class that is a member, then left to right.
- The leftmost rule is the one of sets; the overload resolution of 13.2 (smallest widening) is not used.

### 11.3.10 Classes inside generics

- A `Box<Animal>` holds `Dog` and `Cat` objects as `Animal`: only the `Animal` members are visible, an `override` still runs the `Dog` version, and `v is Dog d` (11.1) gets the `Dog` back.
- A `Box<Dog>` is converted to `Box<Animal>` automatically, with the Rust model: the conversion moves the box and the original can no longer be used, so no one sees one box as two types.
- The same holds for the collections: an owned `vec<Dog>` moves into a `vec<Animal>` (`vec<Animal> animals = ->dogs`); the items keep their class.
- A read-only borrow converts too: a `vec<Dog>` (or a `Box<Dog>`) can be passed as `&vec<Animal>`, because through it the items are only read. A writable borrow `(&)vec<Animal>` of a `vec<Dog>` is a compile error: it could put a `Cat` into it.

### 11.3.11 Ownership in generic code

- A parameter of type `Item` without `->` is borrowed for every `Item`. `->Item value` moves an object and copies a value type (a number): using a number again after `->` is never an error, using an object again is.
- The move check is done at the definition, with the Rust rule: an unconstrained `Item` is move-only, so using the value twice after `->` is a compile error even if every call uses numbers. A constraint to value types (`<Item : Numeric>`) allows it.

### 11.3.12 Limits of the generated copies

- Three limits stop runaway code generation. Each one is set by its own compiler parameter, from 1 to 256:

| Limit | Meaning | Default |
|---|---|---|
| Chain depth | nested instantiations, a generic that instantiates another one that instantiates another | 16 |
| Copies per generic | different type combinations of one generic in the user code | 16 |
| Copies per library collection | the same, for each collection defined by this specification (`vec`, `list`, `map`, `hmap`, `set`, `hset`) | 64 |

- The copies are counted per generic: 64 different `list` types and 64 different `map` types are allowed. A custom collection, even one built on the library ones, uses the user limit. `arr<T>` (a class with direct generated code, 6.1) has no limit.
- The same type arguments are one copy: `smaller<i32>` called many times is generated once. Identical machine code produced by different types is merged by the backend (the linker); the limits count the type combinations before any merging.
- Exceeding a limit is a compile error with a clear message, for example: `Generic XYZ defined in file abc.df was used with more than 16 instances in function FunName in file def.df`. For the chain depth the message lists the chain.

### 11.3.13 Not in the language

- Default type arguments (`<Item = i32>`).
- Specialization: two implementations of one generic for overlapping types are a compile error; a different behavior for one type is written with the type switch (11.3.7) or as a separate type.
- `T?` (11.3.8).

### 11.3.14 Libraries

- A compiled library that uses generics only internally exports nothing for them: the copies it needs are already generated.
- A public generic offered for any type needs its code in the user's compiler. The plan is to export a minimal intermediate form ("bytecode"), not the source, that the compiler finishes for each new type. This is for after 0.1.0 (open item in 99).


## 11.4 Class `atEnd` (Destructor code)

```
public class Foo(i32 a) {
  File file = File("c:/logs.txt")

  public Foo(string path) {
    File logs2 = File(path)
    a = (i32)logs2.size
  } atEnd {
    logs2 is atEnd.
  }
} atEnd {
  Root.Console.Log("Goodbye!")
  file is atEnd.
}
```

- The destructor of a class is the `atEnd` written after the closing brace of the class. It is private (cannot be called) and runs when the object is freed; it follows Rust `Drop` rules (the last owner going away, moves, reference counting).
- A constructor can also have a trailing `atEnd`: a destructor fragment, registered when that constructor runs and executed when the object dies. It sees what is new inside that constructor (locals and parameters) and the class members, not the locals of other constructors.
- Order when an object dies: the destructor fragments in reverse order of registration (the last constructor registered runs first, up to the first one encountered), then the class `atEnd` as the last one. A subclass runs all its own blocks first, then the parent's, then the traits' (the reverse of construction).
- The variables used by a destructor fragment stay alive until it runs; the compiler sees which ones from the block.
- Fields and constructor locals not reached (for example when a constructor throws) hold their zero value, so cleanup code uses `x is atEnd.`, which does nothing on null (12.4).
- Order at scope end: scope `atEnd` blocks from last to first, then the class `atEnd` of every owned object still alive.
- Once the final result is decided, ordinary exceptions raised by later class cleanup are ignored. A double free is a `Panic`.
- Static properties are not touched by the class `atEnd`.


---


# 12. Memory and ownership

Rust model, Dflat syntax.

| Syntax | Meaning |
|---|---|
| `b = a` | same object, no ownership change (compiler checks lifetimes) |
| `->a` | ownership moves: `Item b = ->a`, `f(->a)`, `list.add(->a)` |
| `a!` | copy: a shortcut for `a.clone()` (12.6) |
| `.clone()` | method: a deep copy that follows the kind of each field (12.6); overridable |
| `.tryClone()` | method: like `.clone()`, raises `ObjectNotClonableException` when the object cannot be cloned (12.6) |
| `@T name` | a reference counted object, read-only (Rust `Rc<T>`, 12.7) |
| `(@)T name` | a reference counted object, read/write, with a lock (12.7) |
| `@a` | a plain object starts reference counting; `a` is lost, as after a move (12.7) |
| `_T name` | a weak reference to a counted object (Rust `Weak<T>`): a field, a variable or a parameter (`public _Texture2D texture`, 12.7) |
| `_a` | makes a weak reference to the counted object `a`: `_Dog w = _a` (12.7) |
| `x is done.` | releases a usage or a weak reference (12.7) |

- Single owner; freed at the end of the owner's scope, running the class `atEnd` (11.4). A counted object is owned by the counting system (12.7).
- Strings follow these rules (4): `b = a` is the same string, `->a` moves, `a!` copies; the owner frees the string at the end of its scope.
- Function parameters borrow; returning moves ownership to the caller.
- Borrows (`&T` read-only, `(&)T` writable) follow Rust: a borrow puts a strong lock on the object: while it is alive the object cannot be released, moved with `->` or given away. A borrower does not own the object, so it cannot release it, move it with `->` or give it away. A borrow cannot outlive its lender: storing it beyond the call (a `&T` field included), returning it (a function returning `&Dog`) or handing it to something that keeps running after the lender's scope (a fire-and-forget async call) is a compile error; to keep an object after the call, take `@` (12.7). Counted objects cannot be borrowed (12.7).
- Collections own their elements: adding moves in, removing moves out, reading borrows. This is true for `arr` too: the variable that declares an array owns it, `b = a` borrows it, `->a` moves it, and a field that is an array is dropped by its class (6.1).
- An owned object that loses its owner is destroyed and its `atEnd` runs (`clear()`, `fill(...)`, `c[i] = v` and any other operation that drops an owned object, 6.1).
- Iterators and views: if not given away, they live only inside the scope that created them; when moved or borrowed they follow the rules of this section, and their collection becomes changeable again only when the last holder ends (6.5).
- A weak reference is declared with `_` before its type (`_Texture2D texture`), like `&T` (borrowed) and `*T` (pointer); it does not own the object and starts as null. It exists only on counted objects and is used through `enforce w { } else { }` (12.7).
- `@`, `_`, `is done.`, `usersCount`, `weakrefsCount` and `removeRefCounting()` are built-in operations of the language (12.7).

## 12.1 Pointers

```
*i32 reader = &value                          // read-only
(*)i32 writer = (&)value                      // writable
*i32{count} samples = ...                     // bounded
(*)i32{65536} screen = [*i32]0xA0000          // memory-mapped, unsafe
writer[] = 5                                  // same as writer[0]
i32 third = samples[2]
string label = itemPointer[].Name
```

- Types, prefix only: `*T` read-only (default), `(*)T` read/write. Nesting reads left to right: `**i32`, `(*)*i32`.
- Bounds: `{count}` in entries, constant or variable, copied at creation; for read-only and writable pointers. Constant indexes are checked at compile time, variable ones at runtime.
- Usage by index only: `p[i]`, `p[]` = `p[0]`, fields `p[].Field`. No prefix `*` dereference, no infix `->`.
- `&x` gives a read-only pointer to `x`; `(&)x` a writable one; `(&)` on a `const` is a compile error. Whether a pointer is writable is always written explicitly where it is created or passed.
- No untyped pointers: `*byte` stands in for C `void*`.
- No pointer arithmetic (`p + 4` is a compile error); use the methods below.
- `null` is the null pointer. Pointers are nullable by default, and the null pointer is the 0 pointer.
- A reference is a pointer that the compiler generates and guarantees to be valid. It is what normal code uses. Explicit pointers are for low-level work (hardware drivers, memory-mapped access, C interop).
- In safe code an explicit pointer exists only when the compiler can prove that its address is valid or null. Anything the compiler cannot prove needs unsafe code.
- Conversions between pointers and numbers or `addr`: transmute only (3.5); pointer → `addr` also with `=`. `addr` → pointer via transmute (`[*i32]someAddress`) is always unsafe.
- Between pointer types: `(*)T` converts to `*T` implicitly (autocast). `*T` to `(*)T` is a compile error; the way to do it exists only in unsafe code (defined with `unsafe`).
- Logical model: base, size and cursor. The underlying implementation is decided by the compiler: base and size are kept only for bound checks, an unbounded pointer is just the cursor, and only the cursor crosses into C.
- Pointers coming from outside the program (other libraries, OS calls) or built from numbers are always unsafe.
- `unsafe` marks code that does operations the compiler cannot verify. It can be written on a scope (a block), on a function (a function is a scope) or on a class. A class marked `unsafe` can be used only from unsafe code.

Methods (plain = steps of the pointed type, `Raw` = bytes):

| Method | Description |
|---|---|
| `Add(steps)` / `AddRaw(bytes)` | move the cursor, in place |
| `InRange(steps)` / `InRangeRaw(bytes)` | true if moving by the signed amount stays within the bounds |
| `GetOffset()` / `GetOffsetRaw()` | cursor − base, `i64` |
| `GetOffset(other)` / `GetOffsetRaw(other)` | cursor − other address, `i64` |
| `GetAddress()` | cursor as `addr` |
| `GetAddress(steps)` / `GetAddressRaw(bytes)` | `addr` at cursor + amount, cursor unchanged |
| `IsAligned()` | `bool`: cursor aligned to `r.Alignment.Arch` |
| `IsAligned(bytes)` | `bool`: cursor aligned to `bytes` |

## 12.2 Alignment

```
u64 typeAlignment  = Root.Alignment.Type<i32>()
u64 archAlignment  = Root.Alignment.Arch
u64 cacheAlignment = Root.Alignment.Cache
bool onArch  = Root.Alignment.IsAligned(samples)
bool onType  = Root.Alignment.IsAligned<r64>(samples)
bool onCache = Root.Alignment.IsAlignedCache(samples)
```

| Member | Returns |
|---|---|
| `Type<T>()` | `u64`, natural alignment of `T` |
| `Arch` | `u64`, architecture alignment |
| `Cache` | `u64`, cache line alignment |
| `IsAligned(p)` | `bool`, against `Arch` (same as `p.IsAligned()`) |
| `IsAligned<T>(p)` | `bool`, against `Type<T>()` |
| `IsAlignedCache(p)` | `bool`, against `Cache` |

- All values are compile-time constants, usable in array sizes and value parameters.
- `Arch` comes from the target definition; `Cache` from a compiler option, default 64. Builds for other cache sizes use specific targeted compilations.

## 12.3 Volatile

```
volatile (*)u8{65536} screen = [*u8]0xA0000
u8 pixel = screen[128]                        // read once, pixel is a normal value
fun wait(volatile &clock now) { }
```

- `volatile` prefixes any reference or pointer type.
- Every access goes to memory: the compiler never reuses a previously read value.
- To keep a value, copy it into a normal variable.
- Passing a volatile where a non-volatile is expected: no compile error, behavior undefined.


## 12.4 Moving fields and finalizing variables

```
A a = ...
function(->a.c)                    // a.c is null afterwards, a stays usable

} atEnd {
  c is atEnd.                      // finalizer: destructor, then null
}
```

- `->a.c` moves a field out of an object (also chains such as `->a.c.d`). The field holds null afterwards and the object stays usable; its class `atEnd` sees the field as null. Using a variable after `->` raises `UsageOfDestroyedObjectException`, or is a compile error when the compiler can detect it.
- The owner of `a`, or a writable borrow `(&)A a`, can move a field out; a read-only borrow cannot (compile error).
- `->` on a variable or field of a basic type is ignored and behaves like a plain copy; it is a level 1 warning (`i32 a; call(->a)` is the same as `call(a)`).
- Moving out of a stored field or property: a value is copied; an object is passed with its ownership and the field becomes null. If the property is computed (its `R` returns a new object that is not stored), `->` gives away that new object, which needs no owner here; the property value is nullified in every case (11.1.4). Collections: `->c[k]` (6.6).
- Moving an object into a collection that owns its items needs `->` when the object already exists (`list[3] = ->task`, `list.add(->task)`, `insert`, `swap`); the plain form with an existing object is a compile error. An object created in the same statement needs no marker. Basic values are copied and need no marker (6.1); a string is always copied and the copy is shrunk (4).
- Finalizer: `f is atEnd.` is a statement only, never an expression. The final dot is mandatory, and only a line break or `;` may follow it (`f is atEnd.close()` is a compile error).
- `f is atEnd.` releases the ownership held by the variable `f`: the single owner is destroyed running its full destructor (11.4). `f` becomes null; on a null `f` it does nothing.
- On a counted or weak variable it is a compile error: it would read as destroying the object, while the meaning is "I do not need it anymore"; the form is `f is done.` (12.7). One way to do each thing.
- On a borrowed variable (not an owner) it is a compile error. Using `f` after the finalizer raises `UsageOfDestroyedObjectException`, or is a compile error when the compiler can detect it.
- It works on fields, locals and parameters that own the object, and replaces the explicit `close()` calls in cleanup code.
- `atEnd of f { f is atEnd. }` is legal and not flagged: the block can use `f` first and release it explicitly.

## 12.5 Parent and child objects

```
class ListIterator<Item>(&list<Item> collection as parent) : IndexIterator<Item> {
  Node<Item> cursor = collection.first
}
```

- An object can have one parent: the object that guarantees its existence (an iterator or a view and its collection, a DataSet and its DB connection). The parent knows its children: when it ends, it nulls their reference to it. It does not destroy them: a child keeps its own owner.
- The parent is declared in the parameter list of the class with `as parent` after a reference parameter (`&` or `(&)`). A class has at most one `as parent`; a second one is a compile error, and so is `as parent` on a parameter that is not a reference. In the body the parent is used by its parameter name. `parent` is a keyword only right after `as`.
- A class with no `as parent` has no parent.
- If the ownership of the child never changes, the child always ends before its parent, so it needs no checks.
- A child item whose ownership changes (moved with `->`, returned by a function, or put into a field or a collection) is an adopted child: it can outlive its parent, which can end at any moment: on every method call it checks its reference to the parent. If it is null, the child runs its own `atEnd` (without calling the parent) and raises `UsageOfDestroyedObjectException` (15.3). The compiler emits these checks only for adopted children and gives a level 2 warning (19.3).
- A method marked `asparent` can be called only by the object itself and by its children, through their parent reference, even when it is read-only. It is not public. It can change the parent.
- `unrestricted` replaces `public` in the visibility slot and includes it: the method can also be called through a read-only reference (`&`). A plain `public` method needs an owned or writable reference. Properties can be read through `&`; assigning to them is a compile error.

## 12.6 Cloning

```
Dog copy = dog!                               // same as dog.clone()
Dog other = dog.tryClone()                    // ObjectNotClonableException when it cannot be cloned
any Shape s2 = shape.tryClone()               // a trait type needs tryClone

public notclonable class File { }
```

- `a!` is a shortcut for `a.clone()`. Every class is clonable by default; a class changes the behavior by overriding `clone()`, and an override can do anything.
- The default `clone()` builds a new object and fills each field by its kind:

| Field | In the clone |
|---|---|
| native (numbers, `bool`, `char`, enums, bitfields, structs) | duplicated, it is a value |
| owned `string` | copied: a new string with the same text |
| owned object | deep cloned with the same rules; null when it cannot be cloned |
| owned child (an object that has a parent, 12.5) | null: a child is never duplicated, a new one comes from the functions of its parent |
| counted object (`@T`, `(@)T`, 12.7) | another user of the same object (the count goes up by one); never cloned recursively |
| borrowed (`&`, `(&)`) | null |
| weak reference (`_T`) | another weak reference to the same object |

- Any field that cannot be duplicated, for any reason, becomes null in the clone.
- No remapping: a weak reference always points to a counted object (12.7), and a clone never copies a counted object (a counted field is shared), so a weak reference of the clone points to the same object as the original's. This holds also when a counted object is cloned and one of its weak fields points to the object itself: the clone is a plain new object while `clone()` runs, so a weak reference cannot point to it.
- A clone never inherits the children of the original: a cloned collection has copies of its items and no iterators or views.
- Collections: the items follow the rules of the fields (owned items are cloned, children become null). `c!` and `c[a..b]!` (6.5) clone the items.
- Objects that cannot be cloned; calling `clone()` or `a!` on them is a compile error:
  - a class marked `notclonable`, written before `class` (`public notclonable class File`); it is meant for classes that hold a resource (files, sockets, connections);
  - an object that has a parent (`as parent`, 12.5): a new child comes from the functions of its parent;
  - an `unsafe` class (12.1) and a class with pointer fields (`*T`, `(*)T`); there is no opt-in, a class that needs it writes its own `clone()`.
- `tryClone()` returns the clone or raises the non-silent `ObjectNotClonableException` (15.3) when the object cannot be cloned; the result is then null. It is needed when the class is not known at compile time (a value of a trait type, `any Shape`): there `clone()` and `a!` are a compile error.
- On a counted variable `a!` makes a new object, a copy, counted from 1 (`@Dog third = first!`, 12.7).

## 12.7 Reference counting and weak references

The Rust model: `@T` is `Rc<T>`, `_T` is `Weak<T>`, `(@)T` is a counted object with a lock.

```
@Dog first = Dog("Rex")              // created counted, 1 user
Dog plain = Dog("Fido")
@Dog moved = @plain                  // plain starts counting and is lost, as after a move
@Dog second = first                  // another user of the same object, count 2
@Dog third = first!                  // a new object, a copy of first, counted from 1

fun keepDog(@Dog dog) { }            // the function holds its own usage
keepDog(first)                       // count +1 for the call
keepDog(->moved)                     // the caller gives its usage away, count unchanged

u64 users = first.usersCount
u64 weakRefs = first.weakrefsCount

_Dog watcher = _first                // weak reference, not a user
enforce watcher {
  watcher.bark()                     // a temporary usage while the block runs
} else {
  // the object is gone
}

second is done.                      // usage released, second becomes null
Dog alone = third.removeRefCounting()   // a plain object when third is the last user, otherwise null

list<@Dog> pack = []
pack.add(first)                      // the list holds its own usage

(@)Dog dogRw = Dog("Max")            // read/write counted object
enforce dogRw { dogRw.name = "Rex" } // the lock is held inside the block

class Node(string name) {
  _Node parent                       // weak to the parent
  list<@Node> children               // strong to the children
}
@Node root = Node("root")
@Node leaf = Node("leaf")
leaf.parent = _root
root.children.add(leaf)
```

Users and owner
- The owner of a counted object is the counting system. The variables, fields, parameters and items that hold it are its users, and each holds one usage: they protect the object from deletion and never delete it. When the last usage is released the count is 0 and the object is destroyed (its `atEnd` runs, then what it owns ends, its counted fields releasing their usages); this is final, the count cannot be raised again from 0.
- Counting is declared only by the type (`@T`, `(@)T`), or by `var x = @T()` and `var x = f()` (where `f` returns `@T`) when the type is inferred; the type of the variable is what the compiler uses. There is one kind of counting.
- Every object type can be null, counted objects and weak references included. A null holds no usage: the release at the end of the scope and `is done.` do nothing on it. A null item in a `list<@Dog>` is valid.
- An existing plain object starts counting only with the marker `@` in the expression (`@Dog moved = @plain`, `dogs.add(@plain)`); `plain` is lost afterwards, as after a move, and the counting system becomes the owner. `@Dog rc = plain` without the marker is a compile error, and `->plain` into a counted receiver is not used for this (`->` always moves a plain ownership). `@x` on an already counted `x` changes nothing.
- An object cannot make itself counted: `@this` is a compile error (like `->this`, 11.1), and `this` cannot be passed where a counted object is expected (`registry.add(this)` into a `list<@Dog>`); whoever holds the counted object does it (`registry.add(aDog)`).
- `@Dog second = first` adds a user. A counted object cannot be borrowed: `Dog view = first` and `&Dog view = first` with a counted `first` are compile errors, and so is `(&)` on it; it is used through a new usage (`@`, or `(@)` to write).
- `!` is always a clone: `@Dog third = first!` makes a new object counted from 1 (12.6). The default clone of an object does not clone its counted fields: the clone becomes another user of the same objects.
- `==` works as on plain objects (11.1.3); `@` changes nothing.
- Counted objects and weak references cannot be keys of a map or items of a set (6.2).

Functions
- `fun f(@Dog dog)` takes a usage: the count goes up when entering and down when returning; the function uses the object read-only. `fun f((@)Dog dog)` is the read/write form, as `&T` / `(&)T`. A counted argument to a `Dog`, `&Dog` or `(&)Dog` parameter is a compile error.
- `->a` is allowed if and only if the code owns `a`, counted or not. An `@Dog dog` parameter holds one usage, so it can pass it down with `->dog` (no count change), or store the object with `=` (count +1) and release its own usage on return. A `&` borrower owns nothing and can never use `->`.
- `f(->rc)` gives the caller's usage to the function: no count change on the call, `rc` cannot be used afterwards, and the usage is released when the function is done, unless it passes it on. Other users are never affected.
- A method call on a counted object (`first.bark()`) is a usage until the method returns, so the object cannot be released while the method runs. The compiler may leave out the count when another usage of the caller already keeps the object alive for the whole call (the variable it is called on, a field of an object the caller holds).
- A function returning `@Dog` passes the usage of the returned variable to the caller (`return dog` moves it, the count does not change).

Releasing
- A variable that holds a usage or a weak reference releases it automatically when its scope ends. `x is done.` releases it early; it works only on `@`, `(@)` and `_` variables (on a plain owned object it is a compile error: `is atEnd.` destroys it, 12.4).
- A variable that releases its usage (`is done.`), transfers it (`->`) or successfully removes the counting becomes null; the cleanup at the end of the scope does not release it a second time.
- `x.removeRefCounting()` (Rust `Rc::try_unwrap`) moves the object out of the counting: for the last user it returns a plain owned object and the counting block is freed once no weak reference needs it; weak references see the object as gone. When other users exist it returns null and `x` stays valid. Valid on `@` and `(@)` variables and fields (compile error on `_` and on a plain object).
- `usersCount` gives the number of users, `weakrefsCount` the number of weak references; both are valid on every counted object (`weakrefsCount` is 0 when nothing points at it) and a compile error on a type that cannot be counted.

Weak references
- A weak reference exists only on a counted object; it does not count as a user. It is made with `_` in the type and in the expression (`_Dog weak = _first`, `var weak2 = _first`); `=` assigns it. Each weak reference adds one to `weakrefsCount`.
- It is used only through `enforce w { } else { }` (7.2): inside the block it is a temporary usage of the object; the else branch runs when the object is gone (its user count reached 0).

Fields and collections
- An `@T` or `(@)T` field is a user like any other: it releases its usage when its object dies. A `_T` field is a weak reference, seen as gone once the object is destroyed. A `&T` field is not allowed (a borrow cannot outlive its lender, 12).
- A collection of counted objects (`list<@Dog>`) holds its own usage for each item (`pack.add(first)` adds a user). Collections are structurally immutable while being iterated (6.4); the loop variable is a temporary usage, acquired at the start of each iteration and released at its end, with no borrow involved: `for var dog in pack { }` and `for @Dog dog in pack { }` are the same. The collection keeps its own usage of the item and cannot change while the loop runs, so the compiler may leave out the count of the loop variable.
- The members that search (`has`, `indexOf`, `remove`, `removeAll`) take a counted object or null; a plain object is a compile error. The items compare by the rules of their objects (`equals`, `==`, or identity, 11.1.3). The value searched is only looked at: it gains no usage.
- The members that give an item pass its usage to the caller: `@Dog old = pack.replace(0, other)`. A counted `var` comes only from a function that returns `@T` (`var x = f()`); for a counted item the type is written (`var old = pack.replace(0, other)` is a compile error that asks for `@Dog old`).

Read-only and read/write
- `@T` is a read-only counted object, `(@)T` a read/write one. `(@)T` includes a lock in its runtime representation, so it is a distinct counted type.
- Reads never need the lock; a write needs the caller to hold the object's lock, and writing without it raises `WritingOnRefCountWithoutLockingException` (not silent, 15.3). `enforce x { }` takes the lock and releases it at the end of the block (`enforce dogRw { dogRw.name = "Rex" }`).
- Reads without the lock always see a valid state of the object, but not necessarily the latest one; several reads that must be consistent hold the lock.

Cycles
- As in Rust, nothing at runtime: an object does not know who counts it (it holds only the two counts), and the developer breaks a cycle with a weak reference (the `Node` above). Where the compiler can detect a possible cycle (two classes with `@` fields of each other's type, or a class with an `@` field of its own type) it gives a level 1 warning for possible zombie objects (19.3).

---

# 13. Functions

```
fun u64 fib(u64 n) { ... }
fun log(string msg) { }                        // void
fun i32, string getUser() { return 1, "a" }    // multiple values
fun (i32 v, string n) getPair() { return (1, "a") }   // tuple
fun u64 getVal() { c }                         // single expression returns
```

## 13.1 Parameters

The definition says how a parameter is passed, ownership included. The call marks only what the reader must notice (writing, moving) or what the caller chooses (copying).

| Definition | Example | Description |
|---|---|---|
| `f(i32 x)` | `f(x)` | copied |
| `f(&i32 x)` | `f(x)` | by reference, read-only |
| `f((&)i32 x)` | `f((&)x)` | by reference, writable; `(&)` required in the call |
| `f(volatile &i32 x)` | `f(x)` | volatile, read-only |
| `f(Item o)` | `f(o)` | borrowed, read-only |
| `f((&)Item o)` | `f((&)o)` | borrowed, read/write; `(&)` required in the call |
| `f(Item o)` | `f(o!)` | copied by the caller, not part of the definition |
| `f(->Item o)` | `f(->o)` | ownership moves; `->` required in the call |
| `f(@Item o)` | `f(o)` | a usage of a counted object, read-only: count +1 for the call (12.7) |
| `f(@Item o)` | `f(->o)` | the caller's usage is given to the function, count unchanged (12.7) |
| `f((@)Item o)` | | a usage of a counted object, read/write (12.7) |

- Native data: integers, reals, chars, bools, enums, pointers. Objects: class instances.
- A counted argument is accepted only by `@` and `(@)` parameters; `f(Item o)`, `f(&Item o)` and `f((&)Item o)` with a counted argument are compile errors (12.7).
- A mismatched call is a compile error.
- Defaults anywhere; named arguments when needed (`hello(age: 27)`).
- Positional arguments stay in the order of the parameters; named arguments can be written in any position.

## 13.2 Overloading

- Functions can share a name when the number, the order or the types of their parameters differ; the return type alone doesn't count.
- A call that matches more than one function is a compile error, including when default values make it ambiguous (`f(i32 a, i32 b = 0)` and `f(i32 a)` with `f(5)`).
- Literals: the native-size signed type wins (`i64` on 64-bit), even over an exact `i32`.
- Variables: exact type, then smallest same-family widening; other families are not candidates; still tied → compile error.
- Conversion to `string` always ranks last: with `f(i32)`, `f(u64)` and `f(string)`, `f(33)` picks `f(i32)` on 32-bit and `f(u64)` on 64-bit, never `f(string)`.

## 13.3 Lambdas

```
fun string greet = (string name) { "hello " + name }
sort(items, (a, b) { a < b })
list.Filter((x; limit) { x < limit })      // limit borrowed
list.Filter((x; limit!) { x < limit })     // copied
list.Filter((x; ->limit) { x < limit })    // moved in
list.Filter((x; _limit) { x < limit })     // weak
```

- Parameters before `;`, captures after it. **Captures are mandatory**: using an unlisted outer variable is a compile error.
- Constants need no listing; an unlisted local `const` is copied in automatically (level 1 warning).
- Function types: `i32(string, u8)`, `fun(i32)` for void.

## 13.4 Variadic parameters

```
fun LogAll(string level, string messages...) {
  for string message in messages { r.Console.Log($"{level}: {message}") }
}
LogAll("info", "started", "ready")
```

- `T name...` accepts any number of `T` arguments; it is equivalent to passing a collection of them.

---

# 14. Program cycle

## 14.1 Entry point

```
fun int main() { return 0 }
fun main(arr<string> args) { }
fun int main(string args...) { return 0 }
```

- A program starts either from top-level code or from a `main` function in the root file (`program.df` / `main.df`); having both is a compile error. [Ref: Namespaces, Imports, Aliases]
- `main` is a top-level function, outside any class.
- Return type: `int` (exit code) or void (exit code 0).
- Parameters: none, `arr<string> args`, or `string args...` (an `arr<string>`).
- The command-line arguments are also available from `Root.GetArgs()` (17.1); with a parameter, `args` and `Root.GetArgs()` are the same object.
- More than one `main` is a compile error; a `main` in a library is a compile error.

## 14.2 `atEnd`

`atEnd` is code that runs at the end of something. It is not a function and cannot be called. `atEnd` is a reserved word and is written without parentheses. It has five forms:

| # | Form | Attached to | Runs when |
|---|---|---|---|
| 1 | `atEnd of a, b { }` | the listed variables | the listed objects go out of scope; it still runs while at least one of them exists |
| 2 | `} atEnd { }` | a function | the function exits |
| 3 | `} atEnd { }` | any other statement or block | the statement exits |
| 4 | `} atEnd { }` | a class | the object dies (destructor, 11.4) |
| 5 | `} atEnd { }` | a constructor | the object dies (destructor fragment, 11.4) |

```
fun Item popQueue(Queue queue) {
  mutex.Lock()
  atEnd of mutex { mutex.Unlock() }

  if queue.len == 0 { return }      // unlocked
  Item next = queue.Pop()           // if it throws: unlocked
  return next
}

fun blah() {
  File f = abc atEnd of f { f is atEnd. }
  Mutex mx
} atEnd {
  mx is atEnd.
}

if File.exists(name) {
  File second = File(name + ".txt")
  len = second.size
} atEnd {
  second is atEnd.
}
```

- Form 1 can be written on its own line or right after a statement. It sees only the listed objects, which must be declared before it (or in the same statement).
- Forms 2, 3, 4 and 5 are written after a closing brace and see everything inside what they follow: for a statement, the union of its scopes and subscopes (the branches of an `if` / `else` included). A local of a branch that was not taken holds its zero value (null for objects).
- The same name declared with different types in two scopes of a statement that has an `atEnd` is a compile error.
- A trailing `atEnd` always runs once its statement is reached, even if no branch was executed. After a loop it runs once, when the loop exits, not on every iteration.
- Order at the exit of a scope or function: the form 1 blocks first (from last to first), then the trailing `atEnd` as the final cleanup. Several blocks of the same level run from the last to the first.
- Only the variables used by an `atEnd` are kept alive until it runs.
- A compile-time definition: it runs automatically on every exit (normal end, `return`, `break`, `jump`, an exception passing through, a catch that leaves the scope). It cannot be called directly.
- If a catch handles an exception and execution continues, `atEnd` runs at the normal end of the scope.
- Not run on `Panic`. On a non-panic exception that reaches the top level, it runs.
- `return value`: the value is computed first, then `atEnd` runs.
- May contain `break` and `jump` only within its own block. `return` and `throw` are allowed.
- `return` inside `atEnd` overrides the function's result on a normal exit. While an exception is passing through, its return value is ignored and the exception keeps propagating.
- `throw` inside `atEnd` while another exception is propagating → `Panic`.
- The last block to run sets the result.
- `atEnd of f { ...; f is atEnd. }` is legal and not flagged (12.4).

# 15. Errors

### 15.1 Syntax

```
i32 ratio = 10 / divisor
catch DivisionByZeroException ex { }
catch NetworkException { }                    // no variable
catch SilentException ex { }                  // by trait
catch Exception { }                           // all exceptions that are not silent, without getting the actual exception (sinkhole)
catch all ex { }                              // everything, silent exceptions included
catch all { }
throw Exception("message")
throw MyException("message", 123)
throw Panic(3)
```

- No `try`; a `catch` handles everything raised above it in its block; unhandled → caller; top level → program ends.
- Catches are checked in the order written: the first one that handles the exception runs, the following ones are ignored. A catch that doesn't handle the exception is skipped, and a later one can.
- `catch Type` handles that type and its subtypes, silent ones included. A catch can also name a trait.
- `Exception` is the root of all exceptions. `catch Exception` and `catch variable` (the same as `catch Exception variable`) handle only the exceptions that are not silent, never a silent one.
- `catch SilentException` handles every silent exception and no other.
- `catch { }` (no type, no variable) is the same as `catch all { }`, with a level 2 warning.
- `catch all [variable]` handles everything, silent exceptions included. After `catch`, `all` is always the catch-all keyword (fixed parser rule), so `catch all all { }` is valid.
- After a catch that doesn't leave the scope, execution continues with the code after it.
- `throw` inside a catch (including `throw ex`) passes the exception to the enclosing level; later catches in the same block don't see it.
- An error raised inside a catch body and not handled there → `Panic`.
- The exception a catch handles (its variable included) belongs to the catch and is released when the catch ends. The variable cannot be stored outside the catch (assigned to another variable, put in a field or in a collection, or returned): that is a compile error. To keep the information, copy `Id` and `Message`; to send the exception up, `throw` it.

### 15.2 Exception class

```
class Exception {
  u64 Id
  string Message                // max 2 KB
  string Function               // minified header of the failing function, e.g. i32 divide(i32, i32); max 1 KB
  u64 Line                      // debug builds; 0 in release
  StackTrace StackTrace         // debug builds; null in release
}
```

- Constructors: message first and optional, then the subclass fields: `MyException("Whoa a message!", 123)`, `MyException()`.
- The fields added by a subclass are the extra values of that exception. They have a fixed size (no variable length) and are stored inline in the exception record (15.7.5).
- `Message` and `Function` are stored C-style (null-terminated) in fixed buffers of 2 KB and 1 KB, truncated in bytes when the exception is built, never cutting a UTF-8 character.
- Standard runtime error messages: the exception name written with spaces.
- Subclasses only when they carry extra fields.
- Release, exception that is not silent: `Id`, `Message`, `Function` and the extra values are kept; `Line` is 0 and `StackTrace` is null.
- Release, silent exception: only the `Id` and the type are kept. `Message` gives the name of the exception (read from its type), `Function` is null, `Line` is 0, `StackTrace` is null and the extra values hold their zero value (null for objects). The arguments of its constructor are still evaluated.
- Debug builds keep the full description of silent exceptions too, and may store more information than listed here.

### 15.3 Standard exceptions

`Root.Exception` is the base; the standard ones are subclasses in `Root`.

| ID | Exception | Silent fallback |
|---|---|---|
| 0–9 | reserved | |
| 10 | `Exception` | |
| 11 | `NullPointerException` | |
| 12 | `OverflowException` | wraps |
| 13 | `InvalidCastException` | |
| 14 | `DivisionByZeroException` | |
| 15 | `OutOfMemoryException` | |
| 16 | retired (was `ChangingRefCountTypeException`: there is one kind of counting, 12.7); not reused | |
| 17 | `OutOfRangeCastException` | saturates (3.5) |
| 18 | `InvalidCharacterCodePointException` | U+0000 (3.5) |
| 19 | `IndexOutOfRangeException` | |
| 20 | `ModifyingProtectedSourceException` | |
| 21–23 | retired (were `NaNUsedAsKeyException`, `InvalidMapKeyException`, `EmptyVectorAccessException`; the collections use the fallbacks without an exception, 6.2, 6.6); not reused | |
| 24 | `InvalidDateTimeFormatException` | null (3.3) |
| 25 | `FileNotFoundException` | |
| 26 | `InvalidFileAccessException` | |
| 27 | `FileAccessException` | `false`, `0` or a null `date`, depending on the attribute (17.6.3.1) |
| 28 | `DirectoryNotEmptyException` | `delete` returns `false` (17.6.5) |
| 29 | `FileNameAlreadyExistsException` | the file is skipped; `createDirectory` returns the existing directory (17.6.5) |
| 30 | `InvalidPathException` | |
| 31 | `FullPathNotFoundException` | |
| 32 | `SeekBeforeBeginException` | position 0 (17.6.8.5) |
| 33 | `FileAlreadyExistsException` | |
| 34 | `FileAccessToADirectoryException` | |
| 35 | `ReadOnlyItemCannotBeWrittenException` | |
| 36 | `ConflictingSharingException` | |
| 37 | `InvalidStreamModesException` | |
| 38 | `NoMoreResourcesException` | |
| 39 | `AccessToAClosedStreamException` | |
| 40 | `StreamModeNotAllowedException` | |
| 41 | `SeekNotSupportedByStreamException` | |
| 42 | `InvalidBufferRangeException` | |
| 43 | `GenericIOErrorException` | |
| 44 | `FileRemovedWhileOpenException` | |
| 45 | `StreamCloseFailureException` | |
| 46 | `ModifyingIteratedCollectionException` | |
| 47 | `InvalidIteratorReleaseException` (6.4, 11.2) | |
| 48 | `UsageOfDestroyedObjectException` (12.4, 12.5) | |
| 49 | `ObjectNotClonableException` (12.6) | |
| 50 | `WritingOnRefCountWithoutLockingException` (12.7) | |

- The list grows during development; each new standard exception takes the next ID.
- `UsageOfDestroyedObjectException`: raised when an object that is gone is used: a child whose parent ended (12.5), or a variable released with `->` or `is atEnd.` (12.4). It is not silent and is catchable in Debug and Release builds (no `Panic` in Release).
- `OutOfMemoryException`: any allocation can raise it (6.1). In Debug builds it is catchable and not silent; in Release builds it is a `Panic` that ends the program. A size that does not fit (`n * size` overflowing `uns`) is reported in the same way.
- IDs are 64 bits. 0–255 are reserved for `Root`; every other exception has as ID a hash of its full name.

### 15.4 Silent exceptions

```
class MySilentException : Exception, SilentException { ... }
```

- A silent exception is only a notice (like an alert or assertion): it is returned like any other exception, but a caller without a matching catch discards it and goes on with the value the function returned. It never stops the program.
- Standard silent exceptions have their own fallback (table above); custom ones just continue.
- A class is silent when it implements the `SilentException` trait (a marker trait with a dummy method). Subclasses of a silent exception are silent too.
- The silent flag is stored in the exception record (bit 31 of `length`, 15.7.5).
- A silent exception needs a specific catch: its own type, a parent class below `Exception`, the `SilentException` trait, or `catch all`. `catch Exception` and `catch variable` don't handle it.
- A silent exception never leaves the function where it was raised, unless that function throws it explicitly:
  - an operation that raises a silent exception (the overflow of `+`, a saturating cast, ...) is seen only by a catch of the same function; without one, the fallback applies in place and nothing is created;
  - a function returns a silent exception to its caller only with an explicit `throw` of it, or by catching it and throwing it again (`catch OverflowException ex { throw ex }`). This also holds for the library functions that document silent exceptions.
- A silent exception returned by a function is seen by the immediate caller only, when a catch of the caller handles it and covers the call. Without one it is ignored and execution continues with the value the function returned (its fallback). It never goes further up.
- Calling a function that can emit only silent exceptions does not make the caller raise. Only a matching catch of the caller, or a `throw` of its own, matters.

```
fun i64 mul(i64 a, i64 b) { a * b }                   // can overflow: silent, seen only by a catch in mul
fun i64 limited(i64 a) {
  if a > 100 { throw LimitException("too big") }      // LimitException is silent: returned to the caller
  return a
}
fun i64 use(i64 a) {
  return limited(a)
  catch LimitException { return -1 }                  // sees the exception thrown by limited
}
fun i64 useWithoutCatch(i64 a) {
  return limited(a)                                   // ignored here (and released): this function cannot raise
}
```

### 15.5 Panic and exit codes

- `Panic(code)`: critical exit, not an exception; not catchable, no cleanup, exits with `code`.
- Every panic prints `Panic <code>` on stdout and exits with `code`. An unhandled exception first prints its `Message` on its own line.
- Unhandled exception at the top level, unhandled error inside a catch body, an exception that is not silent leaving an `atEnd` block (15.7.6), an exception not caught inside a property accessor (11.1.4), an index outside a collection in a Release build (6.1), a change of the size of a collection with open iterators or views in a Release build (6.4), out of memory in a Release build (6.1), double free: exit with -1000 for now. Every panic case will get its own code later.

### 15.6 Targets without exceptions

- On targets that can't support exceptions (e.g. C64), `catch` blocks are ignored and the error part does not exist: functions return only their result and there are no exception records.
- A silent exception (raised by the runtime or by `throw`) is ignored and falls back to its silent behavior.
- A non-silent exception (raised by the runtime or by `throw`) terminates the program.

### 15.7 Implementation

#### 15.7.1 Principles

- An exception is a value that a function returns next to its result: `(result, error)`, as in Rust and Swift. There is no unwinding. `error` is a pointer to the exception record, null when there is no exception.
- `error` cannot be read or written by Dflat code, only by the compiler. The back end decides where it lives (for example a register reserved for it on each target architecture).

#### 15.7.2 Which functions return the error

- Every function returns `(result, error)`, except the functions the compiler can prove cannot raise: those return only the result and their callers never check anything. There is no `safe` keyword; the compiler decides.
- A function can raise when something can leave it:
  - a non-silent exception, coming from a `throw`, from an operation that raises (integer `/` and `%`, array and string indexing, access to a null object, ...), or from a call to a function that can emit non-silent exceptions;
  - a silent exception that it throws explicitly (a `throw` of a silent exception, or a rethrow of one in a catch).
- Calling a function that can emit only silent exceptions does not make the caller raise. Only a matching catch of the caller, or a rethrow, matters.
- An exception does not make a function raise when it is dealt with inside it:
  - a non-silent exception is fully handled when every point that can raise it is covered by a matching catch, and the bodies of those catches cannot raise a non-silent exception;
  - a silent exception that the function does not catch is ignored there (15.4).
- Example of a function that cannot raise (no error part):

```
fun i64 safeDivide(i64 a, i64 b) {
  return a / b
  catch DivisionByZeroException { return 1 }
}
```

- Example of a function that returns the error, because it throws a silent exception explicitly. A caller without a matching catch ignores it, but still releases the record (15.7.4):

```
fun i64 limited(i64 a) {
  if a > 100 { throw LimitException("too big") }
  return a
}
```

- Virtual functions and trait functions: a slot has no error part only if all its implementations have none. One implementation that returns the error makes the slot return it.
- Where the compiler cannot know what a callee emits (callbacks, function pointers, functions of compiled libraries, which are known only by the "returns the error" bit of 15.8), the call counts as one that can emit non-silent exceptions. At run time a silent exception returned by it is ignored (and released) when no catch handles it (15.7.4), and a non-silent one goes up.

#### 15.7.3 Return values

- A function without a result (void) returns only the error.
- With several results or a tuple, the error is one more element after them.
- When a function returns an exception, its result slots hold the zero value (null for objects).
- A constructor that can raise returns `(object, error)`; when it raises, the object is null.

#### 15.7.4 Call site and catches

- After a call to a function that returns the error, the caller looks at it: if it is not null, it runs the matching catch of this function (the catches covering the call statement, in the order written).
- An exact `catch Type` is one compare of the `id`; a catch for a base class walks the parent types in the descriptor; a catch for the `SilentException` trait checks the silent flag; `catch all` always matches.
- If no catch matches and the exception is not silent, the function returns it to its own caller (this function returns the error too), after the `atEnd` blocks of the scopes left have run. Debug builds add the function to the stack trace.
- If no catch matches and the exception is silent, it is ignored and execution continues with the value the function returned (its fallback, 15.3).
- A function the compiler marks as safe returns only its result and its callers check nothing. Any other function always returns `(result, error)`, and `error` can be a null pointer. If it is not null, the caller owns the exception record and must release it, whether a catch handles it, it is ignored because it is silent, or it is passed to the caller's own caller.
- An exception that nobody handles reaches the top of the program: its message is printed, then `Panic -1000`.
- `throw` and the operations that raise a non-silent exception create the exception record and return it. An operation that raises a silent exception creates a record only when a matching catch of the same function covers it; otherwise the fallback applies in place.
- Because a silent record in Release holds only the header, the compiler may use one static record per exception type and allocate nothing.

#### 15.7.5 Exception record

```c
// One per exception type: static, shared by all records of that type
typedef struct DfExceptionDescriptor {
  uint64_t id;                                 // 0-255 reserved for Root, otherwise a hash of the full name
  const char* name;
  const struct DfExceptionDescriptor* parent;  // parent type, null for Exception
} DfExceptionDescriptor;

// One per raised exception, packed layout
typedef struct DfException {
  uint64_t id;                                 // copy of descriptor->id, for the one-compare catch
  uint32_t length;                             // bit 31: silent flag; bits 0-30: size of the whole record in bytes
  const DfExceptionDescriptor* descriptor;     // 20 bytes up to here on 64-bit targets
  // Release, exception that is not silent:
  char message[2048];
  char functionName[1024];
  // Debug builds add the line, the stack trace and anything else useful
  // The extra values of the specific exception follow, inline, fixed size per type
} DfException;
```

- A silent exception in Release is only the header: `id`, `length` and `descriptor`, 20 bytes on 64-bit targets. Its `Message` is the `name` of the descriptor.
- `length` always tells the real size of the record, so a record can be copied or freed without knowing its type. Records are smaller than 2 GB.
- The callee allocates and fills the record and gives it back with ownership. The caller owns it until the catch that handles it ends, or until it is dropped. A function that returns it to its own caller passes the ownership on.
- The caller releases the record it owns in these cases, and only in these:
  - the catch that handles it ends, by reaching its end or by `return`, `break`, `continue` or `jump` out of its body;
  - the body of the catch throws a different exception: the original is released and the new one goes up;
  - it is silent and no catch around the call handles it: it is released and the call goes on.
- `throw` of the catch variable passes the same record up, so it is not released. A non-silent exception that no catch handles goes up to the next caller, which owns it from then on.
- A `Panic` does no cleanup, so a record that reaches the top of the program, or that is alive when a panic happens, is not released.
- A static record (15.7.4) is never allocated, so releasing it does nothing.
- Small architectures (no heap, no multitasking or async) use a predefined memory space for the record.

#### 15.7.6 Constructors and `atEnd` blocks

- A constructor that raises destroys the half-built object before returning (11.1.1): the `atEnd` blocks registered so far run and the fields not yet reached hold their zero value. The caller gets `(null, error)`. Field initializers run as part of the constructor.
- `atEnd` blocks have no error part, as the `drop` of Rust returns nothing. An exception that is not silent and is not handled inside an `atEnd` block is a `Panic`, whether or not another exception is propagating. Who needs to know about an error (for example when closing a stream) calls `close()` explicitly and catches it there.
- Silent exceptions inside an `atEnd` block follow 15.4 like anywhere else.
- Silent exceptions in a constructor (or a field initializer) are never returned to the caller. A `throw` of a silent exception there can only be handled by a catch of the same constructor that covers it; otherwise it does nothing and the construction goes on. A constructor that can raise only silent exceptions returns just the object. Non-silent exceptions follow the first bullet of this section.

### 15.8 Libraries

- The compiler needs one piece of information per exported function of a compiled Dflat library: whether it returns `(result, error)` or only the result. There are no lists of the exceptions a library can raise.
- A library function is identified by its name, parameters, results and the error part. Changing any of them breaks compatibility in the same way.
- Libraries do not exist yet. Two ways are planned to describe the exported functions (open):
  - a `def` section listing every exported function with its whole definition;
  - a query interface in COM style, for example `lib.queryFunction("name", <parameters>, <results>, <safe or not>)`, that returns the function pointer, or null if the library does not support that function.
- External (C) libraries are considered safe: their functions never return an error. If they crash, it is a `Panic`. A Dflat wrapper can turn their own error reporting (for example the `HRESULT` of Win32, with the real result passed back in an output parameter) into `(result, error)`.
- Standard library functions implemented by the runtime in C report an exception in a temporary slot between their return and the wrapper that the compiler generates at the call site; the wrapper moves it into `error`. The temporary slot is never visible to the program and is empty between statements.
- A standard library function written in Dflat follows the same rules as any other function. A silent exception that its specification documents for it must be thrown by the public function the user calls, or caught and thrown again there (15.4); a helper that throws for its caller is not enough.

### 15.9 C functions (minimal)

```
extern fun addr gfx_create()
extern fun bool gfx_window_open(addr ctx, i32 width, i32 height)
extern fun gfx_texture_set_data(addr tex, arr<byte> data)
```

- `extern fun` declares a function of a C library: no body, and the Dflat name is the C name. `extern` is a word only in front of `fun`.
- It is declared at the top level, never in a class or an enum. C has no overloads, so the name is used once.
- It never returns an error (15.8); a crash inside it is a `Panic`. A library usually keeps it non-public and wraps it in its classes.
- Types:

| Dflat | C |
|---|---|
| `i8` … `i64`, `u8` … `u64`, `int`, `uns` | `int8_t` … `uint64_t` (`int` and `uns` with their native size) |
| `r32`, `r64` | `float`, `double` |
| `bool` | `bool` |
| `addr` | `void*`: an opaque address (2.3), stored, passed, returned and compared with another `addr` or with `null` |
| `string` (parameter) | `const char*`: UTF-8 ended by a 0, valid only during the call |
| `arr<T>` of numbers or `bool` (parameter) | two C parameters, `T*` and `size_t` (the length); the C function can change the items |

- A return is a number, `bool`, `addr` or nothing. Not yet: structs, callbacks, returned strings or arrays, varargs, objects.
- Where the C code comes from: Compiler_Options.md (`csource=`, temporary until the libraries bring their own C files).

---

# 16 Namespaces, imports, aliases

```
namespace clinic.planning         // one per file, no braces
namespace local                   // name from the folder path
import stdrnd                     // full namespace path of the library (to be defined)
alias Root as r
alias Root.Console as console
alias u8 as byte8
```

- The root file declares its namespace explicitly; `local` = root namespace + folder path. In a multi-file compilation a root file without a namespace line is a compile error; a single-file compilation does not need one.
- The root file: a file compiled on its own (given directly, or named by `source=` in a project file) is the root whatever its name; this is a single-file compilation. A program of several files has `main.df` or `program.df` as its root file, and a library has `lib.df`, at the top of its folder. A `source=` that names one of these files makes a multi-file compilation; any other name makes a single-file compilation.
- Only the root file can have top-level code (when `main()` is not needed). Every other file has only declarations (classes, traits, enums, functions, constants, aliases); top-level code or a `main` there is a compile error.
- Every other file must be in the namespace of its folder: the root namespace plus the folder path from the root. It can be written in full or as `local`; any other namespace is a compile error. So no file can add a class to a namespace outside its own folder (for example a program cannot add `gfx.MyContext` to a library) and reach its protected members.
- Namespaces are case-insensitive, and so is the match with the folder names (`Gfx/` is `gfx`). The folders of a namespace use the ASCII rule below.
- `import` takes the full namespace path of what it brings in; a bare name is not enough. It brings in all the files of the folder of that namespace.
- Where an import is looked for: the stdlib (`Root`), the folders of the program, and the libraries listed with `include=` in the project file (Compiler_Options.md). Each library is a root folder whose root file declares its root namespace. The import goes to the root whose namespace starts its path, then down the folders of the rest of the path.
- What is compiled: the root file, what it reaches through imports and, in a multi-file compilation, the other `.df` files of the folder of the root file (without an import). A single-file compilation ignores the other files of its folder. Any other `.df` file is ignored.
- Two roots with the same namespace, or a root whose namespace is inside the namespace of another root, are a compile error. The stdlib counts as a root: no library can declare a namespace inside `Root`, because `internal` members are visible to the sub-namespaces (1.6.1).
- A single-file compilation can import only its own subfolders and the stdlib; `include=` lines of its project file are ignored with a warning (until compiled libraries exist).
- Folder names used for `local` namespaces must be ASCII letters, digits and `_` only, no exceptions; any other folder is ignored by the build (level 3 warning). Two folders whose names differ only by case are a compile error.
- `alias <original> as <new>`; aliases are pure aliases (interchangeable with the original). They can also go to members and functions: `alias Root.Console as c` and `alias Root.Console.Log as log`

---

# 17 Standard Library
DFlat comes with a basic standard library called `Root`
It contains basic file access and streams, console, random, etc.

Check the [DFlat Standard library](dflat-stdlib.md) document for the full specification.



# 18 RESERVED

# 19 Compiler

- `program.df` > `main.df` > `lib.df`: the first one found is the root (executable, or library for `lib.df`); a skipped root file gives a level 1 warning. `dfcompiler --force-lib folder/` forces a library.


## 19.1 Compiler Directives and the preprocessor

Before the compilation a preprocessor builds the code that is actually compiled: it removes every part that is not valid for the current variables and replaces it with empty lines, so the line numbers do not change and the messages keep pointing to the real lines. The preprocessor is not part of the language: its syntax is separate and the language never sees the directives.

```
##define good = (os == windows && cpu == x64) || (os == linux && (cpu == x64 || cpu == arm))
##if good
Print("supported")
##elseif os == osx
Print("macOS")
##else
##emiterror "this platform is not supported"
##endif
```

- A directive starts with `##` in the first column, then the keyword, then optional text. It ends at the end of the line. The text cannot contain `#`. Indented lines are not directives.
- Directives:

| Directive | Description |
|---|---|
| `##define <variable> = <value>` | defines a variable; there is no `##undef` |
| `##if <condition>` | the lines that follow are kept when the condition is true |
| `##elseif <condition>` | tested only when the previous conditions of the block were false |
| `##else` | kept when all the previous conditions of the block were false |
| `##endif` | ends the block |
| `##region <text>` / `##endregion` | fold the code between them, no other effect; regions nest and pair like braces; unmatched → level 3 warning; `##region parts #2#` is a compile error (`#` in the text) |
| `##emiterror <string>` | the compiler prints the string and stops with an error |
| `##emitwarning <string>` | the compiler prints the string as a warning and goes on |

- The rules of the variables:
  - The predefined variables (the table below) cannot be redefined or reassigned. A variable whose name is one of their values (`windows`, `x64`, `debug`, `on`, `true`...) is an error, for example `the variable windows cannot be used: it matches one value of os`.
  - A variable defined in the source can be defined again by a later `##define`. A variable defined in the project file (`CompilerVar`, see the compiler options) cannot be redefined, in the source or in the project file.
  - A variable can be compared with another variable (`##if a == b`). In a comparison a word on the right is the value of the variable when it names a variable, and the word itself otherwise. A word that is not a value of a predefined variable (`os == windoes`) is an error.
- Variables are never bool unless they are defined as a bool. The value of a variable is a static string (the name of an enum value, such as `linux`), a number or a bool (`true` / `false`). `##define todo = true` defines a bool; the result of a condition can be defined too (`##define good = os == windows && cpu == x64`) and is a bool.
- A condition is `<variable> <operator> <value>`, or just a bool variable. The operators are `==`, `!=`, `<`, `<=`, `>` and `>=`. Conditions are joined with `&&` and `||`, and parentheses group them. `&&` and `||` have the same precedence and are evaluated from left to right.
- `==` and `!=` work on every value. `<`, `<=`, `>` and `>=` work only when the value is a number: strings and enums have no order.
- The unary `!` exists only for a bool variable (`##if !todo`). There is no other negation: for a variable that is not a bool use `!=` (`exceptions != on`).
- A preprocessor error is an error: a misuse (`!` on a variable that is not a bool, an order comparison on an enum, an unknown variable, an unmatched `##else` or `##endif`) stops the compilation with a message.
- The variables set by the compiler:

| Variable | Values |
|---|---|
| `os` | `windows`, `linux`, `osx` |
| `cpu` | one of the cpus of the table below |
| `arch` | `<os>_<cpu>`, for example `linux_x64`; test `arch` for the combination, `os` or `cpu` alone for one of them |
| `bits` | 8, 16, 32 or 64: the size of the cpu (table below) or of a custom architecture |
| `int`, `uns`, `real` | the native size in bits of the type: the size of the cpu (64 on x64, 32 on x86), or the size forced by `--native-int` and `--native-real`; `uns` has the size of `int` |
| `endian` | `little`, `big`: from the cpu (table below; a custom architecture can say otherwise) |

The cpus (the names are Dflat names, not the names of clang):

| `cpu` | Bits | Endian |
|---|---|---|
| `x86` | 32 | little |
| `x64` | 64 | little |
| `arm32` | 32 | little |
| `armeb` | 32 | big |
| `arm64` | 64 | little |
| `arm64be` | 64 | big |
| `arm64_32` | 64 (32-bit addresses) | little |
| `riscv32` | 32 | little |
| `riscv64` | 64 | little |
| `ppc32` | 32 | big |
| `ppc64` | 64 | big |
| `ppc64le` | 64 | little |
| `mips` | 32 | big |
| `mipsel` | 32 | little |
| `mips64` | 64 | big |
| `mips64el` | 64 | little |
| `sparc` | 32 | big |
| `sparcel` | 32 | little |
| `sparcv9` | 64 | big |
| `avr` | 8 | little |
| `m68k` | 32 | big |

- A cpu the compiler does not build for yet is a compile error: `<sparc> is not yet a supported cpu`.
| `build` | `debug` (the default), `release` (`--release`) |
| `exceptions` | `on`, `off` (`off` when the target has no exceptions, 15.6) |
| `ram` | a number of bytes; written as `<number>[kb\|mb\|gb]` (`ram >= 64kb`). 4gb unless a custom architecture says otherwise |
| `version` | the language version: the version of this specification that the compiler was built for; `version.major`, `version.minor` and `version.build` are numbers |
| `coptlev`, `coptmode` | the values of the options `--COptLev` (a number) and `--COptMode` (`size` or `speed`) |


## 19.2 Toolchain

- **dfcompiler**: the only compiler, written in .NET 10. It runs the preprocessor (19.1), parses, type checks, emits C and calls clang. Its options are listed in `Compiler/Compiler_Options.md`. A minimal or zero libc, calling the OS directly, is planned (`--libc none`).
- **dfparser** `[--tokens] file.df`: the parser alone (hand-written lexer and recursive-descent parser, shared with dfcompiler); it prints the syntax tree. It has no version of its own yet.
- **Native-size option**: forces the native size of integers and reals [Ref: Number literals].

### 19.3 Compiler Warnings

Level 1 = most important, level 3 = least.

| Level | Warning | Section |
|---|---|---|
| 1 | Skipped root file | 19 |
| 1 | Unlisted local `const` auto-copied into a lambda | 13.3 |
| 1 | Jump loop between switch cases | 10.5 |
| 1 | Variable read before assignment | 1.5 |
| 1 | Array element read before fill or assignment | 6.8.1 |
| 1 | `Root.Class` written in a class header | 11.1 |
| 1 | `Keyable` class with a field of a collection, iterator or view type | 11.2.1 |
| 1 | `->` on a variable or field of a basic type | 12.4 |
| 1 | Same constraint repeated inline and in `where` | 11.3.1 |
| 1 | Possible cycle of counted objects (zombie objects): two classes with `@` fields of each other's type, or a class with an `@` field of its own type | 12.7 |
| 2 | Cast out of range saturates | 3.5 |
| 2 | Upward type check | 11.1 |
| 2 | Type name not PascalCase | 1.1 |
| 2 | `catch { }` without type or variable | 15.1 |
| 2 | Char compared with an integer | 2.5 |
| 2 | `equals` and operator `==` both defined | 11.1.3 |
| 2 | Unreachable case in a type switch | 11.3.7 |
| 2 | Extra checks emitted for a child object moved with `->` (its parent may end first) | 12.5 |
| 3 | Unmatched `##region` / `##endregion` | 19.1 |
| 3 | `const` name not UPPER_SNAKE_CASE | 1.1 |
| 3 | Folder ignored (invalid name) | 16 |
| 3 | Generic call chosen over comparisons | 11.3.5 |
| 3 | Primary parameter redeclared with an initializer | 11.1.1 |
| 3 | Unreachable branch of a generic check | 11.1.3 |
| 3 | `uns` smaller than the cpu size (limits the length of the collections and the size of `.hash`) | 2.1 |

---




# 99. Open items

- Variadic parameters: position in the parameter list, passing an existing collection
- Locale details
- Date formatting week rules
- Panic exit codes (all -1000 for now)
- Whether standard messages keep the word "Exception"
- Class destructor that throws (15.x)
- Lexing of reals ending in a dot (`0.`): a real needs digits on both sides of the dot
- Binding in generic checks (`T is i32`: type of `other` inside the branch)
- Generics: `Generic` sets declared inside a class (for now namespace level only)
- Generics: a variable declared with a set as type argument (`Box<Num> b`) and the type it takes
- Generics: `where` on class and trait headers
- Generics: whether `clock`, `lapse`, `addr` and pointers (nullable by default, 7.1) count as real nullables in a set, like `date`, `time` and objects (11.3.8)
- Generics: whether the level 1 warning of `->` on a basic type (12.4) fires inside generic code
- Generics: names of the compiler parameters for the chain depth, the copies per generic and the copies per library collection (11.3.12)
- Generics: minimal intermediate form exported by compiled libraries for public generics (11.3.14), after 0.1.0
- Stdlib: strings, files, time, collections extras (queue/stack)
- Bitfields: whether `set`, `clear`, `setAs` and `toggle` modify the value in place or return a new value; nullability of bitfields (3.1.1, 7.1)
- Functional collection members (`filter`, `map`, `reduce`)
- Collections: the members of `MapIterator` (the three sub-traits of `Iterator` are decided, 6.7; the index and set iterators are defined, 6.7.1, 6.7.3)
- Collections: `clear()` on maps and sets now differs from `clear()` on the index collections (which keeps the size, 6.6); to be confirmed with the maps and the sets
- Collections: how a custom class receives literals (`[...]`, `[k: v]`) (`c[k]` is decided, 6.7; `+=` and `-=` call the members, 6.6)
- Structs: the definition (11.1.5)
- Collections: views are read-only; revisit with tasks (6.5), the full Rust borrow model stays an option
- Collections: shared C helpers against one copy per type (code size, 11.3.12); the B-tree (6.8.4.1) and the hash table (6.8.5.1) are decided
- Collections: widening of the stuck buckets of `hmap` / `hset` (6.8.5.1)
- Keys: the name of the exception raised by `Keyable` for a changed hash (11.2.1)
- Collections: the lifetime of a borrow of an item taken outside a `for` loop (`(&)c[k]`), which an insert or a delete can move
- Collections: the other non-silent exceptions of the collection operations, to be formalized during the implementation (out of memory is decided, 6.1, 15.3)
- Strings: borrowed strings in the advanced cases (who releases them, lifetimes across fields and collections)
- Ownership: when a local borrow ends (last use or end of scope); when a temporary object inside an expression ends
- Reference counting (12.7): `->` from a counted variable into a `->T` parameter or a plain receiver (removing the counting); `->pack[0]` on a list of counted items; counted natives and strings (`@i32`, `@string`); counted traits and downcasts; `&` parameters of a primary constructor; whether a weak reference can be made without `_` in the expression; which methods a read-only `@T` can call; the call form of a `(@)T` parameter
- Reference counting: the lock of `(@)T` (waiting or try-lock, the else branch, the order for `enforce a, b`, nested locking, deadlock), an expression form of `enforce` and several failure cases in one `enforce`
- Reference counting: generics with counted type arguments (`Stack<@Dog>`), after 0.1.0; the `Rc` / `Arc` split, with threads; counted captures of lambdas, and the weak capture of a plain variable (13.3)
- Properties: position of `readonly` / `readwrite` / `writeonly` next to `public` and `static`, and their use together with a body (11.1.4); `override` of a property with only one half; access to a property through a trait-typed value (`any`), left to the compiler
- DateTime library: import path, timezone database, parsing for `CalendarDate` / `ZonedDateTime`
- Import paths of the standard library parts (`import stdrnd`)
- Pointers: automatic bound for `&` on `arr` / `vec` / `list`; bound methods on unbounded pointers
- `addr` in `dfoot` (fixed memory maps ok, runtime addresses problematic)
- Safe / unsafe logic
- Function pointers (objects); C function tables (COM vtables, Vulkan dispatch tables)
- C interop: struct layout, calling conventions, callbacks, returned strings and arrays, UTF-16 strings, ownership across the boundary, unions, bit flags, varargs (the minimal extern fun is in 15.9)
- Deferred: async/await, locks/atomics, tasks, SIMD, EF syntax, Vulkan, shaders
- Pointers: the exact way to convert `*T` to `(*)T` in unsafe code, the syntax of the `unsafe` block and what it allows, handling of null pointers in safe code, which cases the compiler can prove valid, pointer equality, `.hash` and `toStr()`, and pointers in generics

---

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
- 12.6 A weak field of a clone is another weak reference to the same object; the remapping of weak references into the clone tree is removed (a weak reference points only to a counted object, which a clone never copies)
- 12.7 The search members of a collection of counted objects take a counted object or null and compare by the rules of the objects; the members that give an item pass its usage, and a counted item needs its type written (`@Dog old = pack.replace(...)`, not `var`)
- 12.6 Example: `Dog other = dog.tryClone()` (was `Dog?`: objects are nullable already, `T?` on them is a compile error, 7.1)
- 15.3 ID 16 retired (was `ChangingRefCountTypeException`, no use with one kind of counting); IDs 49 `ObjectNotClonableException` and 50 `WritingOnRefCountWithoutLockingException`; 99: the open item on `ChangingRefCountTypeException` removed
