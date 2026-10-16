# Dflat Language
A new language D♭ that is the same as C# but easier to write and faster to execute

**Version** 0.0.9  2026/10/16 by CPU

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
- **Identifiers**: have to start with a letter and can be composed of letters, numbers, and underscores. They are case sensitive. Cannot start with `_` (underscores inside names are fine).
- **Naming rules**:
  - Built in types (`i32`, `string`, `arr`) are lowercase
  - Types (classes, traits, enums): PascalCase; otherwise level 2 warning
  - Methods: camelCase is recommended but not enforced. Warning only if the same function name is defined with different capitalizations.
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

## 1.5. Declarations and namespaces

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
- `var`: type taken from the initializer; if it can't be decided → compile error (`var x = null`, `var l = []`). Allowed in multiple returns when the return type is known. Coexists with partial forms like `arr a = [...]` / `vec v = [...]` / `map m = {...}`.
- A tuple or literal must be assigned, passed or returned; alone it's a compile error.
- Tuple destructuring: the items of a tuple can be unpacked into separate variables in one statement; `_` skips an item.



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
- Lengths and default indexes are `u64` everywhere.

## 2.5 Operators

| Kind | Operators |
|---|---|
| arithmetic | `+ - * / %` |
| bitwise | `&` `|` `^` `_x` (not) `<<` `>>` |
| logical | `!` `&&` `^^` `||` |
| comparison | `== != < > <= >=`, `is` |
| assignment | `= += -= *= /= %= &= |= ^= <<= >>= ??=`, `_=` (weak) |
| increment | `++x x++ --x x--` (not atomic) |
| ranges | `a..b` exclusive, `a..=b` inclusive, `a..`, `..b` |
| null-safe | `?.` `??` |
| ownership | `->x` move, `x!` copy |
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
| 4	| ++ -- + - (prefix) ! _ -> & (&) | Right to Left |
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
| 19 Lowest | = += -= \*= /= %= &= \|= ^= <<= >>= ??= _= | Right to Left |

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

- `_value` (bitwise not, 2.5) flips only the defined bits. Casting an integer to a bitfield masks off every bit that is not defined, so a bitfield never holds undefined bits: `[Flags]_[u32]value` is the same as `_value`.

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

## 3.5 Conversions
- **Cast `(type)`**: keeps the value; out of range saturates (level 2 warning), or raises `OutOfRangeCastException` if a specific catch exists. From `r`: truncates toward zero (Rust rules), +∞ → max, −∞ → min (0 for unsigned), NaN → 0.
- **To `time`**: a value out of range converts to null and raises `OutOfRangeCastException` if a specific catch exists (silent).
- **Transmute `[type]`**: same bits; truncates low bits or zero-extends. `[bool]`: 0, null, `""`, NaN and `'\0'` are false, anything else is true. `[char]`: invalid → U+0000, or `InvalidCharacterCodePointException` if a specific catch exists.
- **Implicit**: only widening that keeps the value: same family (`i8 → i64`, `r32 → r64`) and unsigned to a wider signed (`u16 → i32`); any scalar type → bool; any type → `string` when the target is a `string` (calls `toStr()`). An implicit conversion is equivalent to a cast.
- **To string**: a null `string` converts to `""`; a null of any other type converts to `"null"`.
- **Pointers**: created from numbers or other pointers by transmute only (`[*u64]address`); pointer → `addr` also with `=`.
- **To bool**: numbers 0 / NaN = false; `char` `\0`; `date` / `time` / `clock` null; `lapse` zero or null; `string` null/empty; collections and tuples cannot be cast or transmuted to bool.

# 4. Strings

- UTF-8; `.len` = chars, `.sizeof` = bytes.
- `""` and null are the same: a null string prints as nothing (`"a" + null + "b"` = `"ab"`); a null value of another type prints `null` (8.1).
- Small strings: 64 bytes, 1-byte length, up to 62 bytes.
- Large strings: 32-byte header (char count, byte length, sizeof, block count), content, 8-byte tail (0 or pointer to 4 KB blocks); a compact string is a valid C string.
- `text[pos]` reads the character at position `pos` (characters, not bytes); writing by index is not yet decided.
- Large strings behave like a StringBuilder: every operation (`+=`, `Replace`, …) works in place on the 4 KB blocks, without copying the existing text.
- Every type has `toStr()` to convert it to a `string`.

## 4.1 Char functions
Tests follow Unicode categories. `ToUpper` / `ToLower` are culture-invariant for now (locale: open item).

| Definition | Description |
|---|---|
| `bool <char>.IsLetter()` | Unicode letter |
| `bool <char>.IsDigit()` | Unicode decimal digit |
| `bool <char>.IsLetterOrDigit()` | Letter or digit |
| `bool <char>.IsWhiteSpace()` | Unicode white space |
| `bool <char>.IsUpper()` | Uppercase letter |
| `bool <char>.IsLower()` | Lowercase letter |
| `bool <char>.IsHexDigit()` | `0`–`9`, `a`–`f`, `A`–`F` |
| `bool <char>.IsPunctuation()` | Unicode punctuation |
| `bool <char>.IsSymbol()` | Unicode symbol |
| `bool <char>.IsControl()` | Control character |
| `bool <char>.IsAscii()` | Code point below 128 |
| `char <char>.ToUpper()` | Uppercase form, culture-invariant |
| `char <char>.ToLower()` | Lowercase form, culture-invariant |

## 4.2 String functions

| Function | Definition |
|---|---|
| `string <string>.Trim()` | Removes all non-printable characters and all types of spaces from both ends of the string |
| `string <string>.Trim(string c)` | Removes all specified characters from both ends of the string |
| `string <string>.TrimLeft()` | Removes all non-printable characters and all types of spaces from the left of the string |
| `string <string>.TrimLeft(string c)` | Removes all specified characters from the left of the string |
| `string <string>.TrimRight()` | Removes all non-printable characters and all types of spaces from the right of the string |
| `string <string>.TrimRight(string c)` | Removes all specified characters from the right of the string |
| `string <string>.Substr(u32 start, u32 len)` | Produces a string containing the characters starting from `start` (zero-based) up to `len` length. Does not fail if `len` extends past the end |
| `string <string>.Replace(string toFind, string replacement, bool caseSensitive = true)` | Returns a string where all occurrences of the specified string are replaced with the replacement string |
| `i64 <string>.IndexOf(string val, bool caseSensitive = true)` | Returns the first zero-based index of the specified string, or `-1` if not found |
| `i64 <string>.IndexOf(char val, bool caseSensitive = true)` | Returns the first zero-based index of the specified character, or `-1` if not found |
| `i64 <string>.LastIndexOf(string val, bool caseSensitive = true)` | Returns the last zero-based index of the specified string, or `-1` if not found |
| `i64 <string>.LastIndexOf(char val, bool caseSensitive = true)` | Returns the last zero-based index of the specified character, or `-1` if not found |
| `bool <string>.Contains(string val, bool caseSensitive = true)` | Returns `true` if the specified string exists within the string |
| `bool <string>.StartsWith(string val, bool caseSensitive = true)` | Returns `true` if the string begins with the specified string |
| `bool <string>.EndsWith(string val, bool caseSensitive = true)` | Returns `true` if the string ends with the specified string |
| `arr<string> <string>.Split(string separator)` | Splits the string into an array of substrings bounded by the specified separator string |
| `string <string>.ToLower()` | Returns a copy of the string with all characters converted to lowercase |
| `string <string>.ToUpper()` | Returns a copy of the string with all characters converted to uppercase |
| `string <string>.PadLeft(u32 totalWidth, char paddingChar = ' ')` | Pads the left side of the string with `paddingChar` until it reaches `totalWidth` length |
| `string <string>.PadRight(u32 totalWidth, char paddingChar = ' ')` | Pads the right side of the string with `paddingChar` until it reaches `totalWidth` length |
| `arr<byte> <string>.ToCStr(StringEncoding encoding = StringEncoding.UTF8)` | Produces a byte array with the representation of the string as regular C string |

`StringEncoding` is a default enum used to emit strings in specific encodings (used for interpos):
`public enum String.StringEncoding { UTF8, UTF16, UTF32, UTF16BE, UTF32BE, ASCII, ISO88591 }`

### 4.3 Strings detailed implementation
TODO, differentiate between short strings and block strings.


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
| `arr<T>` | array: fixed size set at initialization (compile-time or runtime), contiguous; cannot grow or shrink (6.7) |
| `vec<T>` | vector: contiguous and growable (Rust `Vec` rules) (6.8) |
| `list<T>` | growable, counted B-tree, O(log n) insert/remove (6.9) |
| `map<K, V>` | sorted map (B-tree); alias `bmap` (6.10) |
| `hmap<K, V>` | hash map (6.10) |
| `set<T>` | sorted set (B-tree); alias `bset` (6.11) |
| `hset<T>` | hash set (6.11) |

- Collections are objects: they are nullable by default (`type?` is a compile error, 7.1) and they own their elements (12).
- `==` between two collections compares identity only, never the content. `c!` makes a copy (`.clone()`, 12).
- Lengths and default indexes are `u64` everywhere; `.len` is the number of items.
- By access: `arr`, `vec`, `list` are indexed by position (0 to `.len` - 1); `map` and `hmap` hold key/value pairs; `set` and `hset` hold each value once.
- Hash against B-tree: `hmap` and `hset` are hash tables (fast lookup, no order); `map` and `set` are B-trees (kept sorted, O(log n)); `list` is a counted B-tree used by position. The items of a `list` need no key rules.
- Type, size and capacity: the size of an `arr` and the capacity of a `vec` are given only at initialization and are not part of the type.
- Index checks happen at compile time when possible, otherwise at runtime: an index outside the collection raises `IndexOutOfRangeException` (not silent), or is a compile error when it can be detected at compile time.
- The members are listed in 6.6; each collection has its own section for the details.

## 6.2 Keys

Applies to the keys of `map` / `hmap` and to the items of `set` / `hset`.

- Hashed collections (`hmap`, `hset`): the key needs `==` and a hash; the hash is sized to the architecture.
- Sorted collections (`map`, `set`): the key needs an ordering.
- `.hash` and `.order` exist on all types. A class can override its hash and `equals`; `==` on class objects calls `equals` only when the class overrides it (known at compile time), and compares identity otherwise.
- Default class hash: the hash of the class name and the memory address. It is meant for collections only (not for `dfoot`).
- Class order when none is declared: the hash values are compared; two different objects with the same hash are ordered by their address.
- Orderings: numbers, `char`, `bool` (false before true), `date` and `time` by value; strings by bytes (the same as code point order in UTF-8), culture-invariant for now; tuples item by item, left to right.
- `bool` can be a key (two possible values). Reals can be keys, except NaN: a NaN key is never stored or retrieved, and a silent `NaNUsedAsKeyException` is raised (15.3).

## 6.3 Literals

```
list<i32> numbers = [1, 2, 3]
var samples = [1, 2, 3]                 // arr<native integer>, 3 elements
arr names = ["a", "b", "c"]             // arr<string>, 3 elements
vec values = [1, 2, 3]                  // vec<native integer>
set<string> tags = ["red", "blue"]
map ages = { one: 1, two: 2 }           // map<string, native integer>
map words = { 1: "one", 2: "two" }      // map<native integer, string>
list<arr<i32>> grid = [[1, 2], [3, 4]]  // nested literals need the full type
```

- A literal is the right side of an assignment.
- `[...]` can be assigned to `arr`, `vec`, `list`, `set` and `hset`; `{ key: value }` to `map` and `hmap`. Literals are arrays (`arr`) unless declared otherwise, and `{ ... }` is a `map` unless declared otherwise.
- A duplicate value in a set literal or a duplicate key in a map literal is a compile error.
- Nested literals need the full type specification: `var` and partial forms (`arr`, `vec`, ...) are a compile error for them.
- Types are inferred from literals; integers and reals default to the native size (1.3), mixed `[1, 2.5]` → native real; other mixes are errors; empty literals need a declared type.
- Map keys are literals (bare names are strings); values may be any expression.
- Size or capacity on the declaration: `arr<T>[size]` is the size, `vec<T>[n]`, `hmap<K, V>[n]` and `hset<T>[n]` are the capacity (initially empty). The same form on `list`, `map` and `set` is a compile error.

## 6.4 Iteration

```
for i32 item in numbers { }                      // arr, vec, list: index 0 up to .len - 1
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
- Modifying a collection while iterating it, or while a view of it exists, is a compile error (the loop borrows the collection).

## 6.5 Views

```
arr<i32> head = scores[..3]           // read-only view, no copy
arr<i32> middle = scores[2..5]!       // copy
reset((&)scores[2..5])                // writable view passed to a function
```

- `c[a..b]`, `c[..b]`, `c[a..]` give a view: a reference to a part of the real collection, not a copy. A view is read-only; `(&)c[a..b]` is a writable view; `c[a..b]!` is a copy of the same type.
- Views exist only on `arr`, `vec` and `list`. There are no ranges on keys (`map`, `hmap`, `set`, `hset`).
- While a view exists, the collection cannot grow or shrink (compile error, ownership is borrowed).

## 6.6 Members

| Member | arr | vec | list | map | hmap | set | hset |
|---|---|---|---|---|---|---|---|
| `.len`, `.sizeof` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `.capacity`, `reserve`, `shrinkToFit` | – | ✓ | – | – | ✓ | – | ✓ |
| `.keys` | ✓ | ✓ | ✓ | ✓ | ✓ | – | – |
| `.values` | – | – | – | ✓ | ✓ | – | – |
| `c[k]` read | ✓ | ✓ | ✓ | ✓ | ✓ | – | – |
| `c[k] = v` | replace | replace | replace | set or add | set or add | – | – |
| `c[a..b]` view | ✓ | ✓ | ✓ | – | – | – | – |
| `add(v)` | – | ✓ | ✓ | `add(k, v)` bool | `add(k, v)` bool | bool | bool |
| `add(collection)` | – | ✓ | ✓ | merge | merge | count | count |
| `insert(index, v)` | – | ✓ | ✓ | – | – | – | – |
| `pop()` | – | ✓ | – | – | – | – | – |
| `has(v)` | ✓ | ✓ | ✓ | by value | by value | ✓ | ✓ |
| `hasKey(k)` | – | – | – | ✓ | ✓ | – | – |
| `indexOf(v)` | ✓ | ✓ | ✓ | – | – | – | – |
| `keyOf(v)` | – | – | – | ✓ | ✓ | – | – |
| `remove(v)` bool | – | ✓ | ✓ | by value | by value | ✓ | ✓ |
| `removeAll(v)` count | – | ✓ | ✓ | by value | by value | – | – |
| `removeAt(k / range)` | – | ✓ | ✓ | by key | by key | – | – |
| `tryGet(k, (&)out[, default])` | – | – | – | ✓ | ✓ | – | – |
| `clear()` | defaults | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `fill(value)` | ✓ | ✓ | – | – | – | – | – |
| `fillAtCapacity(value)`, `resize`, `truncate` | – | ✓ | – | – | – | – | – |
| `sort`, `sortUnstable`, `reverse` | ✓ | ✓ | ✓ | – | – | – | – |
| `copyFrom` | ✓ | ✓ | ✓ | – | – | – | – |
| set operations and tests | – | – | – | – | – | ✓ | ✓ |

A member marked `–` is a compile error on that collection (for example `hasKey` on a `vec`).

- `.len`: count of items. `.sizeof`: bytes to store the object itself (for all types: to be discussed).
- `.capacity`: items that fit without growing. `reserve(n)` makes room for at least `n` more items; `shrinkToFit()` lowers the capacity to the length.
- `.keys`: keys of a map, or the positions of `arr` / `vec` / `list`. `.values`: values of a map.
- `c[k] = v`: on `arr`, `vec` and `list` it replaces an existing item only; an index past the end raises `IndexOutOfRangeException` (not silent). On `map` and `hmap` it replaces the value, or adds the key and the value when the key is missing.
- Reading a missing map key gives the null or zero value of the value type and raises a silent `InvalidMapKeyException` (15.3).
- `add(v)`: appends to `vec` and `list`. `add(k, v)`: adds the pair to a map, or replaces the value of an existing key; returns `false` when it replaced, `true` when the key is new. On sets it returns `true` if the value was added.
- `add(collection)`: appends the items of another collection to `vec` and `list`; on maps it merges another map (the new value wins, no return value); on sets it returns the number of items actually added.
- `insert(index, value)`: `index` goes from `0` to `.len` (`.len` appends); a larger index raises `IndexOutOfRangeException`.
- `pop()`: removes and returns the last item of a `vec`; on an empty `vec` it returns the zero value (null for objects) and raises a silent `EmptyVectorAccessException`.
- `has(v)`: true if the value is present (on maps, searches the values). `hasKey(k)`: true if the key exists.
- `indexOf(v)`: index of the first match, or the maximum `u64` value when not found (the `string` `IndexOf` keeps returning `-1`).
- `keyOf(v)`: key of the first entry found with that value, or the default value of the key type when there is none; it raises no exception. Use `hasKey` to test a key.
- `remove(v)`: removes the first match and returns `true` if something was removed. `removeAll(v)`: removes all matches and returns the number removed. On maps both work by value. Sets hold a value once, so they have `remove` only.
- `removeAt(k / range)`: removes by index or range on `vec` and `list`, by key on maps.
- `tryGet(k, (&)out[, default])`: reads a map entry into `out` (`(&)` because the call writes to it).
- `clear()`: removes all items; on an `arr` it sets every item to the default value.
- `fill(value)`: sets every item up to the current `.len`. `fillAtCapacity(value)` (`vec`): fills the whole capacity and sets `.len` to the capacity.
- `resize(newLen, value)`: sets `.len`; new items get `value`, extra items are cut. `truncate(len)`: cuts the `vec` to `len` items (no effect if shorter), the capacity is unchanged.
- Sorting: `sort(SortDirection.ASC)` sorts in place with the default comparer (the `.order` of the items); `sort()` is ascending. `sort(lambda)` uses a temporary custom sorter. `SortDirection` has the entries `ASC` and `DESC`. The lambda returns a bool (true is -1, "first goes before second"; false is 1) or a number (negative, zero, positive). `sort` is stable; `sortUnstable` is not (it needs no extra memory). `reverse()` flips the current order in place.
- `copyFrom(dstIndex, srcCollection, srcIndex, count)` and `copyFrom(dstIndex, srcCollection, range)`: copy items from another collection into this one; object items are cloned. A range outside either collection raises `IndexOutOfRangeException`, or is a compile error when it can be detected at compile time. Using the same collection as source and destination is a compile error when detectable, otherwise a non-silent `ModifyingProtectedSourceException` is raised.
- On `vec` and `list`, `dstIndex` goes from `0` to `.len`: the items past the end are appended, so `copyFrom` is the only way to write past the end and it never leaves gaps; a larger `dstIndex` raises `IndexOutOfRangeException`.

```
arr source = [1, 2, 3]
arr destination = [1, 2, 3, 4]
destination.copyFrom(3, source, 0..3)    // compile error: 3 items do not fit from index 3 of 4 items

names.sort(SortDirection.DESC)
names.sort((first, second) { first.len < second.len })
```

## 6.7 Array `arr`

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
- Members: `.len`, `.keys`, `c[k]`, `c[k] = v` (replace only), views, `has`, `indexOf`, `clear`, `fill`, `sort`, `sortUnstable`, `reverse`, `copyFrom`. No `add`, `insert`, `remove` or `pop`.

## 6.8 Vectors `vec`

```
vec<i32> numbers = [1, 2, 3]
vec<string>[50] parts                   // empty, capacity 50
numbers.add(4)
numbers.insert(0, 9)
i32 last = numbers.pop()
```

- Contiguous and growable (Rust `Vec` rules). `vec<T>` is empty; `vec<T>[n]` is empty with capacity `n`.
- Default iterator: index 0 up to `.len` - 1.
- Members: everything in the `vec` column of 6.6: `add`, `add(collection)`, `insert`, `pop`, `remove`, `removeAll`, `removeAt`, `clear`, `fill`, `fillAtCapacity`, `resize`, `truncate`, `.capacity`, `reserve`, `shrinkToFit`, `has`, `indexOf`, `sort`, `sortUnstable`, `reverse`, `copyFrom`, views.

## 6.9 Lists `list`

- Growable, counted B-tree used by position: O(log n) insert and remove. It has no capacity: `.capacity`, `reserve`, `shrinkToFit`, `fillAtCapacity` and the `[n]` form do not apply.
- Default iterator: position 0 up to `.len` - 1.
- Members: the `list` column of 6.6: `add`, `add(collection)`, `insert`, `remove`, `removeAll`, `removeAt`, `clear`, `has`, `indexOf`, `sort`, `sortUnstable`, `reverse`, `copyFrom`, views. `fill`, `resize`, `truncate` and `pop` are not available.

## 6.10 Maps `map`, `bmap`, `hmap`

```
map ages = { one: 1, two: 2 }
hmap<string, i32> scores = {}
bool isNew = ages.add("three", 3)       // false if the value of an existing key was replaced
ages["four"] = 4                        // adds the key and the value
i32 age = ages["one"]
```

- `map` (alias `bmap`) is sorted by key (B-tree); `hmap` is a hash map. Key rules: 6.2.
- Default iterator: (key, value) tuples, sorted by key for `map`, unspecified order for `hmap`. `.keys` iterates the keys and `.values` the values.
- `c[k]` read, `c[k] = v` (replaces or adds), `add(k, v)`, `add(map)` (merge, new value wins), `hasKey`, `has(v)`, `keyOf`, `tryGet`, `remove(v)`, `removeAll(v)`, `removeAt(k)`, `clear`.
- A missing key on read gives the null or zero value and a silent `InvalidMapKeyException` (15.3).
- `hmap` also has `.capacity`, `reserve`, `shrinkToFit` and the `[n]` capacity form; `map` has none of them.

## 6.11 Sets `set`, `bset`, `hset`

```
set<string> tags = []
bool added = tags.add("red")                 // false if already there
u64 count = tags.add(otherTags)              // number actually added
set<string> both = tags.intersection(otherTags)
tags |= otherTags
tags -= "red"
```

- `set` (alias `bset`) is sorted (B-tree); `hset` is a hash set. A value is held once; adding a duplicate is ignored. Key rules: 6.2.
- `add(val)` → `bool` (true if added); `add(collection)` → number actually added; `has(val)` → `bool`; `remove(val)` → `bool`; `clear()`; `.len`. `hset` also has `.capacity`, `reserve`, `shrinkToFit` and the `[n]` capacity form; `set` has none of them.
- Operations take two sets of the same kind: `set` with `set` and `hset` with `hset`; mixing them is a compile error.
- New sets: `union(other)`, `intersection(other)`, `difference(other)` (items of this set not in `other`), `symmetricDifference(other)` (items in one set only).
- In place: `|=` (union), `&=` (intersection), `-=` (difference), `^=` (symmetric difference); `+=` is the same as `|=`. With a single value, `|=` / `+=` add it and `-=` removes it. No operators that return a new set (`set1 | set2` is not defined).
- Tests: `isSubsetOf(other)` (every item of this set is in `other`), `isSupersetOf(other)`, `isDisjointFrom(other)` → `bool`.
- Iteration (`for T item in tags`): sorted for `set`, unspecified order for `hset`.

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
Integers, reals, `dec`, dates and times offer a `<type> <type>.parse(text[, default[, format]])` and bool `<type>.tryParse(text, <type>(&)result[, format]])`

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
for u64 k in list.keys { }
for string message in messages { }
for (string key, i32 value) in map { }      // map: (key, value) tuples
for (var key, var value) in map { }
for var (key, value) in map { }
```
- A range whose start is greater than its end counts down.
- The `for` variable always has a type; `var` is accepted when the type can be inferred (also in tuple forms); an untyped `for x in ...` is a compile error.
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
- A class with `equals` or `==` but no matching hash, used in a hash based collection (`hmap`, `hset`), is a level 1 warning.
- `bool equals(T other)` is a generic function: `T` is implicit and is the type of the argument; inside a generated instance `other` has type `T`. This allows comparing with other types, and each type decides which ones it supports.
- `T is type` checks the generic type at compile time (the branch that does not match is not emitted); `other is type` checks the value at runtime (11.1). Both can be used in the same function.
- A more generic type can match before a more specific one, so the order of the checks matters. A branch that can never run because an earlier check always matches is a level 3 warning.
- Every path of `equals` must return a value, otherwise it is a compile error. The upward-check warning (level 2) does not fire in generic code when a check is true for one `T` only.
- Null is handled by the compiler before `equals` is called: the function never receives a null argument.

## 11.2 Traits
They are similar to C# interfaces and Rust traits

```
trait Named {
  string name
  Named(string n) { name = n }
  fun string Label()                     // no body: must be implemented
  fun string Hello() { $"Hi {Label()}" }
}
trait Shape : Named { fun r64 Area() }
```

- Single parent class (listed first in the header); any number of traits; base of everything: `Root.Class` (implicit, never written, 11.1).
- Traits are implemented only in the class header; a trait cannot be added to an existing type (built-in or library) from outside its declaration.
- Traits: properties, methods with or without body, constructor when they have properties; one parent trait; a supertrait inherited twice exists once.
- Same member in two traits: `TraitA.member` (inside), `px.TraitA.member` (outside), unqualified → compile error.
- Trait-typed values: the compiler chooses static or dynamic dispatch; force with `specific Shape` (one type, known at compile time) / `any Shape` (any implementing type, dispatched at runtime). `list<any Shape>` holds mixed shapes.

## 11.3 Generics

Rust model (code generated per type, checked at the definition), Dflat syntax.

### 11.3.1 Functions

```
fun Item min<Item : Numeric>(Item first, Item second) {
  first.Compare(second) ? { first, second }
}

fun Item pick<Item, Key>(Item value, Key key)
  where Item : Numeric && Orderable, Key : Hashable && Named { }

fun printAll<Item>(list<Item> items) where list<Item> : Printable { }
```

- Constraints inline `<Item : Trait>` or in a `where` clause, never both in one declaration (compile error).
- Several traits joined with `&&`. No negative constraints.
- `where` may constrain any type expression; parameters separated by commas.

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

class Names : Container<string> { ... }
class Cache<Key : Hashable, Value> { hmap<Key, Value> entries }

Stack<i32> numbers = Stack<i32>()
```

- Several parameters are comma-separated; types and value parameters can be mixed.
- A generic trait can be implemented several times by one class with different types.

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
  fun Item? Next()
}

class Countdown : Sequence with (Item is u32) { ... }

fun printAll<Source : Sequence>(Source items) { ... }
```

- `placeholder` declares a type inside a trait that each implementing class fixes once, with `with (Name is type, ...)` in the header.
- Users of the trait don't name the placeholder.

### 11.3.5 Inference

- The compiler infers type arguments; if it can't → compile error. Explicit arguments are always allowed: `min<u8>(3, 5)`.
- A name followed by `<…>(` is read as a generic call (lookahead). When the code could also read as comparisons → level 3 warning; parentheses force comparisons.
- Unsuffixed literals follow 1.3: `min(3, 5)` → native signed integer.


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
| `b _= a` | weak reference |
| `a!` | copy |
| `.clone()` | method: owned parts duplicated, counted parts +1 owner, other references become weak; overridable |
| `.asRefCount` | object becomes reference counted; `=` then adds an owner; repeated calls ignored |
| `.asSharedRefCount` | thread-safe counting |
| `.isRC` | true for both kinds |

- Single owner; freed at the end of the owner's scope, running the class `atEnd` (11.4).
- Function parameters borrow; returning moves ownership to the caller.
- Collections own their elements: adding moves in, removing moves out, reading borrows.
- Switching an object between the two counting kinds → `ChangingRefCountTypeException`.
- Weak references are used through `enforce w { } else { }`; inside the block the object is kept alive.
- `.asRefCount` / `.asSharedRefCount` / `.isRC` are built-in operations, not methods.

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
- `&x` gives a read-only pointer to `x`; `(&)x` a writable one; `(&)` on a `const` is a compile error.
- No untyped pointers: `*byte` stands in for C `void*`.
- No pointer arithmetic (`p + 4` is a compile error); use the methods below.
- `null` is the null pointer.
- Conversions: transmute only (3.5); pointer → `addr` also with `=`. `addr` → pointer via transmute (`[*i32]someAddress`) is always unsafe.
- Logical model: base, size and cursor. The underlying implementation is decided by the compiler: base and size are kept only for bound checks, an unbounded pointer is just the cursor, and only the cursor crosses into C.
- Pointers coming from outside the program (other libraries, OS calls) or built from numbers are always unsafe.

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

- `->a.c` moves a field out of an object (also chains such as `->a.c.d`). The field holds null afterwards and the object stays usable; its class `atEnd` sees the field as null.
- The owner of `a`, or a writable borrow `(&)A a`, can move a field out; a read-only borrow cannot (compile error).
- `->` on a variable or field of a basic type is ignored and behaves like a plain copy; it is a level 1 warning (`i32 a; call(->a)` is the same as `call(a)`).
- Finalizer: `f is atEnd.` is a statement only, never an expression. The final dot is mandatory, and only a line break or `;` may follow it (`f is atEnd.close()` is a compile error).
- `f is atEnd.` releases the ownership held by the variable `f`: a single owner is destroyed running its full destructor (11.4); a reference counted object drops the counter by one and is destroyed when it reaches zero. In every case `f` becomes null; on a null `f` it does nothing.
- When the object is destroyed, weak references to it see null (through `enforce`).
- On a borrowed variable (not an owner) it is a compile error. Using `f` after the finalizer is a normal null access.
- It works on fields, locals and parameters that own the object, and replaces the explicit `close()` calls in cleanup code.
- `atEnd of f { f is atEnd. }` is legal and not flagged: the block can use `f` first and release it explicitly.

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

- Native data: integers, reals, chars, bools, enums, pointers. Objects: class instances.
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
catch ex { }                                  // same as catch Exception ex
catch all ex { }                              // everything, silent exceptions included
catch all { }
throw Exception("message")
throw MyException("message", 123)
throw Panic(3)
```

- No `try`; a `catch` handles everything raised above it in its block; unhandled → caller; top level → program ends.
- Catches are checked in order: the first one that handles the exception type runs, the following ones are ignored. A catch that doesn't handle the type is skipped, and a later one can.
- `catch Type` handles that type and its subtypes; a catch can also name a trait.
- `catch variable` is the same as `catch Exception variable`; it does not handle silent exceptions.
- `catch { }` (no type, no variable) is the same as `catch all { }`, with a level 2 warning.
- `catch all [variable]` handles everything, silent exceptions included. After `catch`, `all` is always the catch-all keyword (fixed parser rule), so `catch all all { }` is valid.
- After a catch that doesn't leave the scope, execution continues with the code after it.
- `throw` inside a catch (including `throw ex`) passes the exception to the enclosing level; later catches in the same block don't see it.
- An error raised inside a catch body and not handled there → `Panic`.

### 15.2 Exception class

```
class Exception {
  u64 Id
  *ExceptionTrait Descriptor    // extra data to build the specific exception class
  string Message                // max 2 KB
  string Function               // minified header of the failing function, e.g. i32 divide(i32, i32); max 1 KB
  u64 Line                      // debug builds; 0 in release
  StackTrace StackTrace         // debug builds; null in release
}
```

- Constructors: message first and optional, then the subclass fields: `MyException("Whoa a message!", 123)`, `MyException()`.
- `Message` and `Function` are stored C-style (null-terminated, no length field) and truncated in bytes when the exception is built, never cutting a UTF-8 character.
- Standard runtime error messages: the exception name written with spaces.
- Subclasses only when they carry extra fields.

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
| 16 | `ChangingRefCountTypeException` | |
| 17 | `OutOfRangeCastException` | saturates (3.5) |
| 18 | `InvalidCharacterCodePointException` | U+0000 (3.5) |
| 19 | `IndexOutOfRangeException` | |
| 20 | `ModifyingProtectedSourceException` | |
| 21 | `NaNUsedAsKeyException` | the value is not stored or retrieved (6.2) |
| 22 | `InvalidMapKeyException` | null or zero value (6.10) |
| 23 | `EmptyVectorAccessException` | zero value, null for objects (6.6) |
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

- The list grows during development; each new standard exception takes the next ID.

### 15.4 Silent exceptions

```
class MySilentException : Exception, SilentException { ... }
```

- A silent exception is raised only if a catch for it exists; otherwise execution continues as if nothing happened (like an alert or assertion).
- Standard silent exceptions have their own fallback (table above); custom ones just continue.
- A class is silent when it implements the `SilentException` trait (a marker trait with a dummy method). Subclasses of a silent exception are silent too.
- The silent flag is stored in the descriptor.
- A silent exception needs a specific catch: its own type, a parent below `Exception`, the `SilentException` trait, or `catch all`. `catch Exception` / `catch variable` don't handle it.

### 15.5 Panic and exit codes

- `Panic(code)`: critical exit, not an exception; not catchable, no cleanup, exits with `code`.
- Every panic prints `Panic <code>` on stdout and exits with `code`. An unhandled exception first prints its `Message` on its own line.
- Unhandled exception at the top level, unhandled error inside a catch body, exception inside `atEnd` while another is propagating, double free: exit with -1000 for now. Every panic case will get its own code later.

### 15.6 Targets without exceptions

- On targets that can't support exceptions (e.g. C64), `catch` blocks are ignored.
- A silent exception (raised by the runtime or by `throw`) is ignored and falls back to its silent behavior.
- A non-silent exception (raised by the runtime or by `throw`) terminates the program.

### 15.7 Implementation

- Rust/Swift style: a failing function returns normally with an **error register** holding 0 or a pointer to an error record; the caller jumps to its `catch` or propagates. Functions that can't fail skip the check.
- Error record: `{ id, descriptor*, message, subclass fields }`. Descriptor: `{ id, name, parent*, silent flag }`.
- IDs: 0–255 reserved for `Root`, otherwise a hash of the full name. Exact catch = one compare; base-class catch walks the parents.
- External libraries raise the base types with a short message; richer errors via an exported mapping function.

---

# 16 Namespaces, imports, aliases

```
namespace clinic.planning         // one per file, no braces
namespace local                   // name from the folder path
import stdrnd                     // full namespace path of the library (to be defined)
alias Root as r
alias console as Root.Console
alias u8 as byte8
```

- The root file declares its namespace explicitly; `local` = root namespace + folder path.
- Namespaces are case-insensitive. Namespaces declared in code can use any Unicode letter.
- `import` takes the full namespace path of what it brings in; a bare name is not enough.
- Folder names used for `local` namespaces must be ASCII letters, digits and `_` only, no exceptions; any other folder is ignored by the build (level 3 warning). Two folders whose names differ only by case are a compile error.
- `alias <original> as <new>`; aliases are pure aliases (interchangeable with the original). They can also go to members and functions: `alias Root.Console as c` and `alias Root.Console.Log as log`

---



# 17. Standard library
- Type members live on the types: `d.year`, `d.addDays(3)`, `s.Split(",")`, `c.IsDigit`, `list.sort()`.

## 17.1 Program

| Definition | Example | Description |
|---|---|---|
| `arr<string> GetArgs()` | `arr<string> args = Root.GetArgs()` | Returns the command-line arguments; index 0 is the program name (as in C and Rust). Returns an empty array when there are no arguments and no program name (e.g. micro-controllers). Same object as the `args` parameter of `main` (14.1) |

## 17.2 Console
Access to stdinput and stdoutput and stderror

| Definition | Example | Description |
|---|---|---|
| `Log(string)` | `Root.Console.Log("Hello")` | Converts the object to string if needed, prints on stdout appending a newline |
| `LogError(string)` | `Root.Console.LogError("Hello")` | Converts the object to string if needed, prints on stderr appending a newline |
| `Print(string)` | `Root.Console.Print("Hello")` | Converts the object to string if needed, prints on stdout |
| `char Read()` | `char c = Root.Console.Read()` | Reads a character from stdin and returns it, returns '\0' at end of input |
| `string ReadLine()` | `string line = Root.Console.ReadLine()` | Reads a line from stdin and returns it, the line is not stripped of the newline character, if any. Returns "" at end of input |
| `` | `` |  |

## 17.3 Date and Time
Mostly used to get current date and time and to measure elapsed at nanosecond scale

| Definition | Example | Description |
|---|---|---|
| `date Now()` | `date d = Root.Time.Now()` | Gets the current time in UTC. A `date` prints as ISO 8601, UTC, to the millisecond: `2026-10-11T14:03:22.123Z` |
| `clock Clock()` | `Root.Time.Clock()` | Returns a u64 monotonic counter with a resolution of 0.1 ns; `Clock()` never returns 0 |
| `` | `` |  |

- `date`, `time`, `clock` and `lapse` are already nullable (`type?` is a compile error on them) and a variable of these types without a value starts as null. A `clock` prints as its number, a `lapse` as its number in its own unit, and null as `null`.
- `time` (3.3) is the time of day, millisecond resolution; parsing in section 9.
- Calendar dates and zoned date-times are classes, `CalendarDate` and `ZonedDateTime`, in an optional DateTime library: available only when imported, so programs that don't need them stay small. Import path, timezone database and parsing: to be defined with the library. There is no local date-time type: a generic zone covers wall-clock values.
- `date - date` and `clock - clock` give a `lapse`. Two values of the same type can be compared; a null date or clock sorts first.

## 17.4 Math
Standard mathematical functions. `Root.Math` operates on `r64` while `Root.Math32` operates on `r32` (similar to `Mathf` of C#)

| Name | Definition | Description |
|---|---|---|
| `Sin` | `r64 Sin(r64 v)` | Computes the trigonometric sine of an angle in radians. |
| `Cos` | `r64 Cos(r64 v)` | Computes the trigonometric cosine of an angle in radians. |
| `Tan` | `r64 Tan(r64 v)` | Computes the trigonometric tangent of an angle in radians. |
| `Asin` | `r64 Asin(r64 v)` | Computes the arcsine (inverse sine) in radians, returning a value between $-\pi/2$ and $\pi/2$. |
| `Acos` | `r64 Acos(r64 v)` | Computes the arccosine (inverse cosine) in radians, returning a value between $0$ and $\pi$. |
| `Atan` | `r64 Atan(r64 v)` | Computes the arctangent (inverse tangent) in radians, returning a value between $-\pi/2$ and $\pi/2$. |
| `Atan2` | `r64 Atan2(r64 y, r64 x)` | Computes the 4-quadrant arctangent of $y / x$ using the signs of both arguments to determine the correct quadrant. |
| `Sinh` | `r64 Sinh(r64 v)` | Computes the hyperbolic sine of a value. |
| `Cosh` | `r64 Cosh(r64 v)` | Computes the hyperbolic cosine of a value. |
| `Tanh` | `r64 Tanh(r64 v)` | Computes the hyperbolic tangent of a value. |
| `Sqrt` | `r64 Sqrt(r64 v)` | Computes the non-negative square root of a value. |
| `Cbrt` | `r64 Cbrt(r64 v)` | Computes the cube root of a value. |
| `Pow` | `r64 Pow(r64 v, r64 e)` | Raises a base value to a specified exponent power. |
| `Exp` | `r64 Exp(r64 v)` | Computes $e$ raised to the power of the given value ($e^v$). |
| `Ln` | `r64 Ln(r64 v)` | Computes the natural logarithm (base $e$) of a value. |
| `Log10` | `r64 Log10(r64 v)` | Computes the common logarithm (base 10) of a value. |
| `Log(x, base)` | `r64 Log(r64 x, r64 base)` | Computes the logarithm of $x$ with respect to a custom base. |
| `Abs` | `r64 Abs(r64 v)` | Returns the absolute (non-negative) value of a number. |
| `Sign` | `r64 Sign(r64 v)` | Returns the sign of a number ($1$ for positive, $-1$ for negative, $0$ for zero). |
| `Min` | `r64 Min(r64 v...)` | Returns the smallest of two or more values (at least two); NaN if any is NaN. |
| `Max` | `r64 Max(r64 v...)` | Returns the largest of two or more values (at least two); NaN if any is NaN. |
| `Clamp` | `r64 Clamp(r64 v, r64 min, r64 max)` | Constrains a value to lie between the specified minimum and maximum bounds; NaN stays NaN; `min` greater than `max` is undefined. |
| `Floor` | `r64 Floor(r64 v)` | Rounds a value down to the largest integer less than or equal to the number. |
| `Ceil` | `r64 Ceil(r64 v)` | Rounds a value up to the smallest integer greater than or equal to the number. |
| `Round` | `r64 Round(r64 v)` | Rounds a value to the nearest integer, halves away from zero. |
| `Trunc` | `r64 Trunc(r64 v)` | Truncates the fractional part of a number, rounding toward zero. |
| `Hypot` | `r64 Hypot(r64 v...)` | Computes the square root of the sum of squares of its arguments (Euclidean distance function). |
| `Fmod` | `r64 Fmod(r64 v, r64 d)` | Computes the floating-point remainder of division. |
| `Lerp(a, b, t)` | `r64 Lerp(r64 a, r64 b, r64 t)` | Performs linear interpolation between $a$ and $b$ using parameter $t$ ($0.0$ to $1.0$). |
| `InverseLerp(a, b, v)` | `r64 InverseLerp(r64 a, r64 b, r64 v)` | Finds the interpolation factor $t$ for value $v$ between range $a$ and $b$. |
| `PI` | `r64 PI` | Mathematical constant Pi ($\pi \approx 3.14159$). |
| `PI_HALF` | `r64 PI_HALF` | Mathematical constant Pi divided by two ($\pi/2 \approx 1.57079$). |
| `E` | `r64 E` | Mathematical constant Euler's number ($e \approx 2.71828$). |
| `SQRT2` | `r64 SQRT2` | Mathematical constant square root of two ($\sqrt{2} \approx 1.41421$). |
| `R2D` | `r64 R2D` | Conversion factor from radians to degrees ($180 / \pi$). |
| `D2R` | `r64 D2R` | Conversion factor from degrees to radians ($\pi / 180$). |

- Math functions never raise exceptions: `Sqrt(-1)` is NaN, `Ln(0)` is `-∞`.
- `Abs`, `Sign`, `Min`, `Max` and `Clamp` also exist for every integer type (`Abs` and `Sign` for signed ones only) and return that type; the argument types pick the version (13.2 rules). `Math32` has the real versions only.

## 17.5 Random
A simple random number generator is included in the standard library. The algorithm is fixed (xoshiro256\*\*, seeded with splitmix64), so the same seed gives the same sequence on every target. Without a call to `Seed` it starts from the current time.

| Name | Definition | Description |
|---|---|---|
| `Seed` | `Root.Random.Seed(u64)` | Initialize the seed of the generator with the provided value |
| `Int` | `i64 Int(i64 min, i64 max)` | Returns a random integer between the minimum number and the maximum number (both included, no bias); the two are swapped if `min` is greater than `max` |
| `RealRange` | `r64 RealRange(r64 min, r64 max)` | Returns a random real number between the minimum number and the maximum number (max excluded) |
| `Real` | `r64 Real()` | Returns a real value in the range [0, 1] (1 is included) |
| `Shuffle` | `Shuffle(collection)` | Randomize the order of elements in an ordered collection. It is valid for arrays, vectors, lists, strings (needs collections: TODO) |

---

## 17.6 Root.IO - Files, Directories and Streams

Status: design in progress. The `File` manager class and the `Stream` base class with `FileStream` are specified. Memory, console and text streams, network streams and the async syntax are not (17.6.9).

### 17.6.1 Scope and general decisions

- `Root.IO` holds the basics of file system access (the equivalent of C# `System.IO`): files, directories, paths and streams. In the examples `alias Root.IO as fs` is used.
- `File` is the file manager class: it works at the level of a file manager (like Norton Commander) on files and directories. `Stream` accesses the contents of a file.
- All streams are binary. Helpers (UTF converters) transform them to text.
- `Stream` is the base class. The stream types (`FileStream`, `MemoryStream`, `ConsoleStream`) are subclasses, because one object cannot be a file, a memory and a console stream at the same time. `TextStream` is a trait that can be applied to any stream to add the text methods (17.6.8).
- Memory streams are available on every target. Other parts of `Root.IO` can be unavailable on specific targets.
- `Root.Console` is a special case: a shortcut to read and write. The console (standard input, standard output) is also reachable as streams (to be defined).
- Big errors raise exceptions, but not every failure raises one, to keep async code manageable: many cases use silent exceptions (15.4, 17.6.6).

### 17.6.2 Paths

- Paths are normalized: `/` is the root and the separator on every OS, and a drive letter is the first folder: `/d:/folder/subfolder/file.txt`.
- Paths written in the form of any OS are accepted and normalized (following C#). `.` and `..` are accepted as generic paths, as everywhere.
- A path with illegal characters raises `InvalidPathException` (not silent) when the `File` is created.
- `File.combinePaths(path1, path2)` joins exactly two paths and returns a `string`.
- Drive letters on other OS and network paths: later.

### 17.6.3 The `File` class

`File(string path)` creates a `File` object that refers to a file or a directory. It opens nothing and does not check the path: a path that does not exist is valid, and its `exists()` returns `false`.

A folder listing returns a list of single `File` objects; `isDirectory` tells whether an item is a file or a directory.

#### 17.6.3.1 Properties (read-only)

| Property | Type | Description |
|---|---|---|
| `path` | `string` | Normalized path |
| `name` | `string` | Name of the file or directory |
| `extension` | `string` | Extension of the name, without the dot |
| `parent` | `File` | Containing directory |
| `isDirectory` | `bool` | True for a directory |
| `isFile` | `bool` | True for a file |
| `size` | `u64` | Size in bytes |
| `created` | `date` | Creation date |
| `modified` | `date` | Last modification date |
| `accessed` | `date` | Last access date |
| `isReadOnly` | `bool` | True if the item is read-only (the only attribute in the standard library) |

- If the item cannot be accessed, `size`, `created`, `modified`, `accessed` and `isReadOnly` raise the silent `FileAccessException` and give `0`, a null `date` and `false`.
- For an item that does not exist they give the default values.

#### 17.6.3.2 Static methods

| Definition | Description |
|---|---|
| `bool exists(string path)` | True if the path exists |
| `bool delete(string path, fs.DeleteOptions options = fs.DeleteOptions.None)` | Deletes a file or a directory; true if it was deleted, false if it was not |
| `File copy(string from, string to, fs.OverwriteOptions options = fs.OverwriteOptions.None)` | Copies a file or a directory to the new location and returns the `File` at the new location |
| `File move(string from, string to, fs.OverwriteOptions options = fs.OverwriteOptions.None)` | Moves a file or a directory to the new location and returns the `File` at the new location |
| `File createDirectory(string path)` | Creates the directory and returns it |
| `arr<File> getAllFiles(string path, bool recursive = false, fs.FileSystemObjectsType itemType = fs.FileSystemObjectsType.Files, string match = "*")` | Lists the items of a directory; `path` has no default and an empty path raises `InvalidPathException` |
| `File getTempFile()` | A `File` for a temporary file |
| `string getTempFileName()` | The fully qualified name and path of a temporary file; it does not create the file |
| `string getTempDir()` | The path of the temporary directory |
| `string combinePaths(string path1, string path2)` | Joins two paths |
| `FileStream open(string path, fs.StreamReadWriteAccessMode readMode, fs.StreamReadWriteAccessMode writeMode, fs.StreamOpenMode openMode)` | Shortcut for `FileStream(path, readMode, writeMode, openMode)` (17.6.8.3) |

#### 17.6.3.3 Object methods

| Definition | Description |
|---|---|
| `bool exists()` | True if the item exists |
| `bool delete(fs.DeleteOptions options = fs.DeleteOptions.None)` | As the static `delete`, on this item |
| `File copyTo(string to, fs.OverwriteOptions options = fs.OverwriteOptions.None)` | As the static `copy`, from this item |
| `File moveTo(string to, fs.OverwriteOptions options = fs.OverwriteOptions.None)` | As the static `move`, from this item |
| `File createDirectory()` | As the static `createDirectory`, for this path |
| `arr<File> getAllFiles(bool recursive = false, fs.FileSystemObjectsType itemType = fs.FileSystemObjectsType.Files, string match = "*")` | As the static `getAllFiles`, on this directory |
| `arr<byte> readAll()` | The whole content as bytes |
| `string readAllText(String.StringEncoding encoding = String.StringEncoding.UTF8)` | The whole content as text in the given encoding |
| `writeAll(arr<byte> buffer, fs.FileWriteMode mode = fs.FileWriteMode.CreateReplace)` | Writes the bytes to the file following the write mode |
| `writeAllText(string text, String.StringEncoding encoding = String.StringEncoding.UTF8, fs.FileWriteMode mode = fs.FileWriteMode.CreateReplace)` | Writes the text, in the given encoding, to the file following the write mode |
| `FileStream open(fs.StreamReadWriteAccessMode readMode, fs.StreamReadWriteAccessMode writeMode, fs.StreamOpenMode openMode)` | Shortcut for `FileStream(path, readMode, writeMode, openMode)` on this file (17.6.8.3) |

`readAll`, `readAllText`, `writeAll` and `writeAllText` exist only on the `File` object, not as static methods. They are a simplification: they ignore sharing and read only. Streams (17.6.8) give the finer control.

### 17.6.4 Enums

All the enums are in `Root.IO`.

| Enum | Values |
|---|---|
| `fs.DeleteOptions` | bitfield (3.1.1) with `Recursive` and `ForceDelete`; `None` and `All` are built in; flags combine with `\|` (`Recursive \| ForceDelete`) |
| `fs.OverwriteOptions` | `None`, `Overwrite` (used by `copy`, `copyTo`, `move` and `moveTo`) |
| `fs.FileSystemObjectsType` | `Files`, `Dirs`, `Both` |
| `fs.FileWriteMode` | `CreateOnly`, `ReplaceOnly`, `CreateReplace`, `AppendOnly`, `CreateAppend` (used by `writeAll` and `writeAllText`) |
| `fs.StreamReadWriteAccessMode` | `None`, `Exclusive`, `Shared` (used for both `readMode` and `writeMode` of a stream) |
| `fs.StreamOpenMode` | `OpenExisting`, `CreateOrOpen`, `CreateIfMissing`, `CreateOrTruncate`, `TruncateExisting`, `CreateOrAppend`, `AppendExisting` |
| `fs.StreamSeekOrigin` | `Begin`, `CurrentPosition`, `End` |

`String.StringEncoding` is the existing enum: `UTF8`, `UTF16`, `UTF32`, `UTF16BE`, `UTF32BE`, `ASCII`, `ISO88591`.

File write modes (`fs.FileWriteMode`):

| Value | Missing file | Existing file |
|---|---|---|
| `CreateOnly` | creates it | `FileAlreadyExistsException` |
| `ReplaceOnly` | `FileNotFoundException` | replaces the content |
| `CreateReplace` | creates it | replaces the content |
| `AppendOnly` | `FileNotFoundException` | appends |
| `CreateAppend` | creates it | appends |

Stream open modes (`fs.StreamOpenMode`):

| Value | Missing file | Existing file |
|---|---|---|
| `OpenExisting` | `FileNotFoundException` | opens it, keeps the content |
| `CreateOrOpen` | creates it | opens it, keeps the content |
| `CreateIfMissing` | creates it | `FileAlreadyExistsException` |
| `CreateOrTruncate` | creates it | opens it and empties it |
| `TruncateExisting` | `FileNotFoundException` | opens it and empties it |
| `CreateOrAppend` | creates it | opens it, writes go to the end |
| `AppendExisting` | `FileNotFoundException` | opens it, writes go to the end |

Stream access modes (`fs.StreamReadWriteAccessMode`): `None` means the stream does not read (or does not write). `Exclusive` means the stream takes exclusive access for that operation. `Shared` means other streams can use the file for that operation too (exact meaning to be confirmed, 17.6.9).

### 17.6.5 Behavior

- `delete`: a directory that is not empty is deleted only with the `Recursive` flag; without it the silent `DirectoryNotEmptyException` is raised and `delete` returns `false`. A missing item returns `false`. `ForceDelete` tries its best to delete an item that is locked for some reason (for example used by another `File` object); the result depends on the OS.
- `copy`, `move`: a directory is always copied or moved with everything inside it. If the target directory already exists, the contents are merged (as pasting a folder over a folder on Windows). With `Overwrite` files of the same name are replaced; with `None` those files are skipped and the silent `FileNameAlreadyExistsException` is raised. When the file itself is skipped, `copy` and `move` return nothing (a null `File`). A missing source raises `FileNotFoundException` (not silent).
- `createDirectory`: it never creates missing parent folders; a missing parent raises `InvalidPathException`. If the directory already exists, the silent `FileNameAlreadyExistsException` is raised and the existing directory is returned as a `File`, so the code goes on without failing (a `catch` can still handle the case).
- `getAllFiles`: returns an `arr<File>` in no defined order. A path that does not exist raises `FileNotFoundException`, not an empty result. `recursive` includes the subdirectories; `itemType` selects files, directories or both.
- `match`: only the wildcards `*` and `?` are special; everything else is static text; case is ignored. It applies to the names of files only. Regular expressions: delayed until 0.1.0.
- `readAll`, `readAllText`: a missing file raises `FileNotFoundException` (not silent).
- `writeAll`, `writeAllText`: the write mode decides what happens with an existing or a missing file (table in 17.6.4). A missing parent folder raises `FullPathNotFoundException` (not silent).
- Temporary files: nothing is deleted automatically (the OS usually handles temporary files); whether `getTempFile` creates the file on disk depends on the OS. Use `atEnd` when the file has to be removed, for example `File tempFile = File.getTempFile() atEnd of tempFile { tempFile.delete() }` (14.2).

### 17.6.6 Exceptions

The IDs continue the table in 15.3. For the stream exceptions (33 to 45) whether they are silent is still to be confirmed case by case; for now they are not silent.

| ID | Exception | Silent | Raised by |
|---|---|---|---|
| 25 | `FileNotFoundException` | no | `copy`, `copyTo`, `move`, `moveTo` with a missing source; `readAll`, `readAllText` on a missing file; `getAllFiles` on a missing path; `writeAll`, `writeAllText` in `ReplaceOnly` or `AppendOnly` on a missing file; the `FileStream` constructor on a missing file with `OpenExisting`, `TruncateExisting` or `AppendExisting` |
| 26 | `InvalidFileAccessException` | no | `copy`, `copyTo`, `move`, `moveTo`, `createDirectory`, `getAllFiles`, `readAll`, `readAllText`, `writeAll`, `writeAllText`, `getTempFile` and the `FileStream` constructor when the access is not allowed by the OS |
| 27 | `FileAccessException` | yes | `exists`, `delete` and the attributes (`size`, `created`, `modified`, `accessed`, `isReadOnly`) when the access is not allowed |
| 28 | `DirectoryNotEmptyException` | yes | `delete` of a directory that is not empty without `Recursive` |
| 29 | `FileNameAlreadyExistsException` | yes | `createDirectory` on an existing directory; `copy` and `move` with `None` when the target exists |
| 30 | `InvalidPathException` | no | `File(path)` with illegal characters; `getAllFiles` with an empty path; `createDirectory` with a missing parent folder; the `FileStream` constructor with an invalid path |
| 31 | `FullPathNotFoundException` | no | `writeAll`, `writeAllText` and the `FileStream` constructor with a missing parent folder |
| 32 | `SeekBeforeBeginException` | yes | `seek` to a position before the start of the stream |
| 33 | `FileAlreadyExistsException` | no | `writeAll`, `writeAllText` in `CreateOnly` on an existing file; the `FileStream` constructor with `CreateIfMissing` on an existing file |
| 34 | `FileAccessToADirectoryException` | no | the `FileStream` constructor when the path is a directory |
| 35 | `ReadOnlyItemCannotBeWrittenException` | no | the `FileStream` constructor opening a read-only item for write |
| 36 | `ConflictingSharingException` | no | the `FileStream` constructor when another stream or process has the file open in an incompatible mode |
| 37 | `InvalidStreamModesException` | no | the stream constructor with an invalid combination of modes (write off with a truncate or append open mode, both read and write off) |
| 38 | `NoMoreResourcesException` | no | too many open files or other resource limits; disk full |
| 39 | `AccessToAClosedStreamException` | no | `read`, `write`, `readByte`, `writeByte`, `seek` and `flush` on a closed stream |
| 40 | `StreamModeNotAllowedException` | no | `read` when the read mode is `None`, `write` when the write mode is `None` |
| 41 | `SeekNotSupportedByStreamException` | no | `seek` on a stream with `canSeek` false |
| 42 | `InvalidBufferRangeException` | no | `read`, `write` with `start` or `length` out of the bounds of the buffer, or `readAtLeast` or `writeAtLeast` larger than `length` |
| 43 | `GenericIOErrorException` | no | an I/O error while reading, writing or flushing (hardware failure, dropped network connection) |
| 44 | `FileRemovedWhileOpenException` | no | the file was deleted or moved while the stream was open |
| 45 | `StreamCloseFailureException` | no | an error while closing (for example the final flush fails) |

Silent fallbacks: `exists` and `delete` give `false`; `size` gives `0`; the dates give a null `date`; `isReadOnly` gives `false`; `createDirectory` gives the existing directory; the skipped files of `copy` and `move` are not copied; a timeout of a stream read gives `0`; `seek` before the start gives position `0`.

Not an error: a timeout of a read (silent), the end of the stream (`null`), and a second `close()`. Exceptions for memory and text streams: later.

### 17.6.7 Examples

```
alias Root.IO as fs

File directory = File.createDirectory("/d:/data/reports")
string filePath = File.combinePaths("/d:/data/reports", "report.txt")
File.delete(filePath)
File report = File(filePath)
report.writeAllText("Done")
report.writeAllText("More", String.StringEncoding.UTF8, fs.FileWriteMode.AppendOnly)

u64 fileCount = 0
u64 directoryCount = 0
for File item in File.getAllFiles("/d:/data", recursive: false, itemType: fs.FileSystemObjectsType.Both, match: "*.txt") {
  if item.isDirectory {
    directoryCount++
  } else {
    fileCount++
  }
}

File tempFile = File.getTempFile() atEnd of tempFile { tempFile.delete() }

// buffer is an arr<byte>
FileStream input = FileStream("/d:/data/in.bin", fs.StreamReadWriteAccessMode.Shared, fs.StreamReadWriteAccessMode.None, fs.StreamOpenMode.OpenExisting)
if input != null {
  uns count = input.read(buffer) ?? 0
  uns newPosition = input.seek(-16, fs.StreamSeekOrigin.End)
  input is atEnd.     // closes the stream now (12.4); otherwise it closes at the end of the scope
}
```

### 17.6.8 Streams

Status: the base class and `FileStream` are defined. Memory, console and text streams, network streams and the async syntax are not.

#### 17.6.8.1 Classes

- `Stream` is the base class. It offers only the basics: close, flush, seek (if supported), read and write.
- `FileStream : Stream` accesses the contents of a file. `MemoryStream` and `ConsoleStream` are other subclasses (later).
- `TextStream` is a trait applicable to any stream; it adds the specific text methods (later).
- All streams are binary; helpers convert them to text.
- Sync and async are both supported. The async version is real async: a callback is called when the operation completes. Its syntax is defined in the rest of the language specification (not for now).
- Sources considered: files, memory, console (standard input and output). Network (sockets, websockets) later: websockets are message based, so they may need a different shape.

#### 17.6.8.2 Opening and closing

- There is no `open` in the base class: the constructor of each stream type opens the stream, as in `FileStream stream = FileStream(path, readMode, writeMode, openMode)`.
- `File.open(readMode, writeMode, openMode)` is a shortcut for `FileStream(path, readMode, writeMode, openMode)`.
- If the constructor fails it returns `null` (objects are nullable by default) and raises the exception that describes the problem (17.6.6).
- A stream follows the Rust memory model: it is closed when its owner is lost. `stream is atEnd.` (12.4) or `close()` closes it earlier; the stream is then marked as closed, so a stream is never closed twice and a second `close()` does nothing.
- A closed stream cannot be reopened. Using it raises `AccessToAClosedStreamException`.
- Example: `FileStream stream = FileStream(path, readMode, writeMode, openMode)`; the stream is closed at the end of the scope, or earlier with `stream is atEnd.` or `stream.close()`.

#### 17.6.8.3 Constructor and modes

`FileStream(string path, fs.StreamReadWriteAccessMode readMode, fs.StreamReadWriteAccessMode writeMode, fs.StreamOpenMode openMode)`

- `readMode`, `writeMode`: `None`, `Exclusive` or `Shared` (17.6.4). They are separate parameters of the same enum type.
- `openMode`: what to do when the file is missing or already exists (table in 17.6.4).
- The three modes are shared by all the stream types and defined once with the base class. Every other constructor parameter belongs to the stream type: `path` exists only in `FileStream`.
- The modes are exposed as read-only constants of the object, with the name of the parameter in upper case: `READ_MODE`, `WRITE_MODE`, `OPEN_MODE`. They cannot be changed once the stream is open.
- Invalid combinations (write off with `CreateOrTruncate`, `TruncateExisting`, `CreateOrAppend` or `AppendExisting`; read and write both off) raise `InvalidStreamModesException`.

#### 17.6.8.4 Base class members

| Definition | Description |
|---|---|
| `bool canRead` | True if the stream can be read |
| `bool canWrite` | True if the stream can be written |
| `bool canSeek` | True if the stream supports seeking |
| `bool hasLength` | True if the stream can tell its length |
| `uns position` | Current position, always available; on a stream that cannot seek it grows as new data arrives |
| `uns length` | Length of the stream; `0` if the stream cannot provide it (see `hasLength`) |
| `uns? read(arr<byte> buffer, uns start = 0, uns length = <rest of the buffer>, uns readAtLeast = 0)` | Reads into the buffer; returns the number of bytes read, `0` if nothing is available, `null` at the end of the stream |
| `uns write(arr<byte> buffer, uns start = 0, uns length = <rest of the buffer>, uns writeAtLeast = 0)` | Writes from the buffer; returns the number of bytes written |
| `byte? readByte()` | Reads one byte; `null` at the end of the stream |
| `bool writeByte(byte value)` | Writes one byte; true if the byte was written |
| `uns seek(int position, fs.StreamSeekOrigin origin)` | Moves the position (only if `canSeek`) and returns the new absolute position |
| `flush(bool forcePhysical = false)` | Writes any buffered data to the destination (the OS, for a file); with `forcePhysical` the OS is also asked to write it to the physical device |
| `close()` | Closes the stream |

`uns` and `int` are the target-dependent unsigned and signed sizes (64 bits on x64, 32 on x32, 16 on 8-bit machines). For now they are used everywhere; before 0.1.0 they will be split by use (17.6.9). Async versions of `read` and `write` exist (for example `readAsync`); names and callback syntax come with the language async syntax.

#### 17.6.8.5 Behavior of read, write, seek and flush

- `read`, `write`: the buffer can be a whole array (it is used from the beginning for its full length) or a view of it, such as `(&)buffer[10..20]`. `start` and `length` select a part and are relative to the buffer passed: if a view is passed, the view is the buffer. Example: `read((&)buffer[10..20], start: 2, length: 4)` fills the elements 12 to 15 of `buffer`.
- `readAtLeast`: the minimum number of bytes to read before returning. The default `0` means do not wait: `read` returns what is available now, possibly nothing.
- `writeAtLeast`: the minimum number of bytes to write before returning. The default `0` means write what you can, with no limitation; the returned value tells how much was written.
- A timeout of a read raises a silent exception and `read` returns `0`.
- `seek` to a position before the start raises the silent `SeekBeforeBeginException` and the position becomes `0`, which is returned. `seek` to a position past the end is allowed, as in C# and Rust: reads return the end of the stream and a write extends the file.
- `flush()` pushes the library buffers to the OS, as in C# and Rust. `flush(forcePhysical: true)` also asks the OS to write the data to the physical device (C# `Flush(true)`, Rust `sync_all`).

### 17.6.9 Open items

- Memory streams, console as streams (standard input and output), text streams (`TextStream` trait), network streams: later
- Async: names and callback syntax for `read` and `write` (language async syntax): not for now
- Streams: exact meaning of `Exclusive` and `Shared` (for example, with an exclusive write can others still read?); other invalid mode combinations (for example creating a file with the write mode `None`)
- Streams: silent or not for each stream exception (for now all not silent); exceptions of the memory and text streams
- Before 0.1.0: split `int` and `uns` by use (calculation, memory offsets, file offsets); parser and compiler support for `uns` in normal code (not in `dfoot`)
- Before 0.1.0: regular expressions in `match`
- Normalization details: drive letters on other OS, network paths
- The full list of exceptions of the File manager and their final names

---

# 18 RESERVED

# 19 Compiler

- `program.df` > `main.df` > `lib.df`: the first one found is the root (executable, or library for `lib.df`); a skipped root file gives a level 1 warning. `dfcompile -forcelib folder/` forces a library.


## 19.1 Compiler Directives

- A directive starts with `#` in the first column, then the keyword, then optional text that cannot contain `#`. Indented lines are not directives.
- `#region` / `#endregion`: fold the code between them, no other effect. Regions nest and pair like braces. Unmatched → level 3 warning.
- `#region parts #2#` is a compile error (`#` in the text).
- Conditional compilation (`#if` family): open.


## 19.2 Toolchain

- **dfparse** `[--tokens] file.df`: .NET 10, hand-written lexer and recursive-descent parser, prints the syntax tree.
- **dfcompile** (planned): parse → type check → emit C → clang; minimal or zero libc, calling the OS directly.
- **Native-size option**: forces the native size of integers and reals [Ref: Number literals].

### 19.3 Compiler Warnings

Level 1 = most important, level 3 = least.

| Level | Warning | Section |
|---|---|---|
| 1 | Skipped root file | 19 |
| 1 | Unlisted local `const` auto-copied into a lambda | 13.3 |
| 1 | Jump loop between switch cases | 10.5 |
| 1 | Variable read before assignment | 1.5 |
| 1 | Array element read before fill or assignment | 6.7 |
| 1 | `Root.Class` written in a class header | 11.1 |
| 1 | Equality without hash on a key of a hash collection | 11.1.3 |
| 1 | `->` on a variable or field of a basic type | 12.4 |
| 2 | Cast out of range saturates | 3.5 |
| 2 | Upward type check | 11.1 |
| 2 | Type name not PascalCase | 1.1 |
| 2 | `catch { }` without type or variable | 15.1 |
| 2 | Char compared with an integer | 2.5 |
| 2 | `equals` and operator `==` both defined | 11.1.3 |
| 3 | Unmatched `#region` / `#endregion` | 19.1 |
| 3 | `const` name not UPPER_SNAKE_CASE | 1.1 |
| 3 | Folder ignored (invalid name) | 16 |
| 3 | Generic call chosen over comparisons | 11.3.5 |
| 3 | Primary parameter redeclared with an initializer | 11.1.1 |
| 3 | Unreachable branch of a generic check | 11.1.3 |

---




# 99. Open items

- Variadic parameters: position in the parameter list, passing an existing collection
- Locale details
- Date formatting week rules
- Conditional compilation (`#if` family)
- Panic exit codes (all -1000 for now)
- Whether standard messages keep the word "Exception"
- Class destructor that throws (15.x)
- Lexing of reals ending in a dot (`0.`): a real needs digits on both sides of the dot
- Binding in generic checks (`T is i32`: type of `other` inside the branch)
- Stdlib: strings, files, time, collections extras (queue/stack)
- Bitfields: whether `set`, `clear`, `setAs` and `toggle` modify the value in place or return a new value; nullability of bitfields (3.1.1, 7.1)
- `.sizeof` for all types (whether it counts the items of a collection)
- Functional collection members (`filter`, `map`, `reduce`)
- Order of enum keys in sorted collections (declaration order or value)
- `tryGet`: return value
- Writing a character by index into a string (`text[3] = 'x'`)
- DateTime library: import path, timezone database, parsing for `CalendarDate` / `ZonedDateTime`
- Import paths of the standard library parts (`import stdrnd`)
- Pointers: automatic bound for `&` on `arr` / `vec` / `list`; bound methods on unbounded pointers
- `addr` in `dfoot` (fixed memory maps ok, runtime addresses problematic)
- Safe / unsafe logic
- Function pointers (objects); C function tables (COM vtables, Vulkan dispatch tables)
- C interop: declaring external functions, struct layout, calling conventions, callbacks, strings (UTF-8 / UTF-16), ownership across the boundary, errors, unions, bit flags, varargs
- Deferred: async/await, locks/atomics, tasks, SIMD, EF syntax, Vulkan, shaders

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
