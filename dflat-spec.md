# D♭ — Dflat Language Specification (draft)

Status: design in progress. Decisions below reflect the latest choices; earlier alternatives are omitted.

---

## 1. Overview

- **Name**: Dflat. Logo: **D♭**. Source extension: **`.df`** (lowercase).
- **Position**: between C# 12 and Rust. Short, clean, modern C-style syntax; no legacy C# compatibility; no Rust lifetime annotations or pedantic syntax.
- **Goals**: speed and simplicity — a C++ replacement.
- **Inspiration**: backend/semantics from Rust; frontend syntax from C# and Dart.
- **Planned built-ins**: native Entity Framework syntax, Vulkan integration (not yet designed).

---

## 2. Lexical rules

- **Comments**: `// line` and `/* block */`.
- **Statement end**: `;` ends a statement; it is optional before a newline. Several statements on one line need `;`. Exact newline rules will be fixed in the grammar.
- **Identifiers**: cannot start with `_` (underscores inside names are fine: `my_var`).
- **Type names**: always lowercase for built-ins.
- **Naming convention** (not enforced): public names start with a capital letter, everything else lowercase.
- **Labels**: `#name#`.
- **Reserved**: `...` (future use).

---

## 3. Built-in types

### 3.1 Numbers

| Type | Description |
|---|---|
| `i8` `i16` `i32` `i64` `i128` `i256` | signed integers (`i128`/`i256` software-emulated for now) |
| `u8` `u16` `u32` `u64` `u128` `u256` | unsigned integers |
| `r16` | IEEE 754 binary16 (same as GPU half) |
| `r32` `r64` `r128` | IEEE floats (`r128` software-emulated) |
| `dec` | fixed-point, 32 + 32 bits |
| `byte` | alias of `u8` |

Numbers are **not nullable** unless declared `type?`.

### 3.2 Other scalars

| Type | Description |
|---|---|
| `bool` | true / false |
| `char` | 32-bit Unicode code point |
| `date` | 64-bit, milliseconds since 1 Jan 1970 UTC. Null = minimum value |
| `clock` | u64 monotonic counter, 0.1 ns resolution, from `GetClock()`. Measures intervals only. `GetClock()` never returns 0; null = 0 |
| `lapse` | difference between two dates or two clocks (see 3.3) |

### 3.3 `lapse`

Signed 64-bit. Raw value = `value × 8 + code` (3 low bits):

| Code (bits 2-1-0) | Meaning | Range |
|---|---|---|
| `000` | null | — |
| `010` | date lapse, milliseconds | ±36 million years |
| `011` | clock lapse, 0.1 ns | ±3.6 years |
| `100` | year lapse (date differences beyond ±36M years) | — |
| `111` | clock lapse overflow | — |
| `001` `101` `110` | free | — |

- `date − date` → date lapse; `clock − clock` → clock lapse.
- Mixing units (add/compare) converts to date lapse, rounding **away from zero** (±0.3 ms → ±1 ms).
- Null propagates.

### 3.4 Composite and collection types

| Type | Description |
|---|---|
| `vec count,type` | fixed-size vector, e.g. `vec 4,i16` (arrays are vectors) |
| tuple | `(i32, r128, string)` or with names `(i32 val, string name)`; **minimum 2 elements** |
| `string` | UTF-8 text (see 11) |
| `list` | growable sequence, counted B-tree, O(log n) insert/remove anywhere |
| `map` | key → value, sorted (B-tree) |
| `hmap` | key → value, hash map |
| `dfoot` | Dflat Fast Open Objects Tree (see 12) |

- **GUID** is not a native type: defined by the EF layer, platform-specific layout.
- **Indexes and lengths**: `u64` by default on all architectures; any `u` width may be declared.

---

## 4. Variables and constants

```
i32 a = 10
string name = "john"
const i32 limit = 100
```

- Declarations are **type-first**.
- **Scope**: C#-style. A variable belongs to its whole block but can't be used before its declaration; reading a possibly-unassigned variable is a compile error (definite assignment).
- **`const`**: read-only value and read-only pointer, a **global guarantee** — nobody can modify it while it's const.
- Out-of-bounds access on a const (compile-time or runtime defined) is a compile error.

### Multiple assignment

```
i32 id, string name = getUser()          // new variables
id, name = getUser()                     // existing variables
existing, _, _, i32 newVar = funcall()   // mixed; _ discards
```

- A name with a type declares a new variable; a bare name must already exist.
- The count must match the function's return count.

---

## 5. Nulls

- Numbers and other value types: not nullable unless declared `type?` (`i32?`).
- **Classes are nullable** by default.
- **Strings**: `""` and `null` are the same. A shared empty instance is used, so string methods never see a real null pointer; `s.len` is 0.
- `date` / `clock` nulls are sentinel values (see 3.2).
- **Null propagates** through arithmetic, like NaN.
- **Equality**: null equals only null.
- **Ordering**: null is less than any value and sorts first.
- Accessing a member of a null object raises a null-pointer exception.
- **No automatic de-nullification** and no `x!` force-unwrap.

### `enforce`

The only way to use a `type?` as non-nullable:

```
enforce a, b {
  // a and b are non-nullable here; writes go to the originals
} else {
  // at least one is null; still accessible as nullable
  if a == null { ... }
}
```

- Assigning `null` to an enforced variable inside the block is a compile error.

### Null-safe operators

- `obj?.Member`, `value ?? fallback`, `x ??= value`
- `i8 b = maybe ?? 0` unwraps into a non-nullable target.
- `x = x ?? 0` does **not** make `x` non-nullable afterwards.

---

## 6. Conversions

### 6.1 Cast `(type)` — keeps the value

- Out of range **saturates** to min/max (unsigned: 0/max), with a level-2 warning.
- Widening is value- and endian-safe.

### 6.2 Transmute `[type]` — keeps the bits

- Same bits read as another type (Rust `transmute` style).
- Across sizes: keeps the **low-order bits** when truncating, **zero-extends** when growing. Never errors.
- To `bool`: zero = false, non-zero = true.
- To `char`: an invalid Unicode value becomes `U+0000`.

```
i16 a = 257
i8 b = (i8)a     // 127 (saturated)
i8 c = [i8]a     // 1   (low byte of 0x0101)
```

### 6.3 Implicit conversions

- **Strict typing**: no implicit conversions except:
  - widening **within the same family** (`i8 → i32`, `u16 → u64`, `r32 → r64`)
  - **any type → bool** (rules below)
- **Literals** adapt to the expected type; a value that doesn't fit is a compile error.

### 6.4 Automatic conversion to bool

| Type | false when |
|---|---|
| integers, `byte` | 0 |
| `r` types | 0 (including −0.0) or NaN |
| `dec` | 0 |
| `char` | `\0` |
| `date`, `clock` | null |
| `lapse` | zero length (or null) |
| `string` | null or empty |
| `vec`, tuple | not convertible |

---

## 7. Operators

### 7.1 List

| Kind | Operators |
|---|---|
| arithmetic | `+ - * / %` |
| bitwise | `&` and, `|` or, `^` xor, `_` not (prefix: `_x`), `<< >>` |
| logical | `!` not, `&&`, `^^` (xor, no short-circuit), `||` |
| comparison | `== != < > <= >=` |
| assignment | `= += -= *= /= %= &= |= ^= <<= >>= ??=` |
| increment | `++a a++ --a a--` (expressions, **not** atomic) |
| ranges | `a..b` exclusive, `a..=b` inclusive, `a..` to end, `..b` from start |
| null-safe | `?.` `??` |

- **Assignments are not expressions** (`++`/`--` are increments, so they are).
- A lone `_` is the discard; `_` followed by an operand is bitwise NOT.
- `/` truncates toward zero; `%` takes the sign of the dividend.
- **Overflow**: wraps (truncates bits) unless a `catch` for `OverflowException` exists in the **same function**; then it raises.

### 7.2 Precedence (high → low, Rust style)

1. postfix: call, `.`, `?.`, index, `x++`, `x!`
2. unary: `! - + _ & ++x --x`, casts, transmutes
3. `* / %`
4. `+ -`
5. `<< >>`
6. `&`
7. `^`
8. `|`
9. comparisons
10. `&&`
11. `^^`
12. `||`
13. ranges `..` `..=`
14. `??`
15. switch expression `? { }`

---

## 8. Control flow

### 8.1 Conditionals and loops

```
if a > 0 {
} else if a < 0 {
} else {
}

while i < n {
}

{
  a--
} while a > 0            // do-while

for u8 i = 0..10 { }                  // counter, exclusive end
for u16 i = 0..=100 step i += 2 { }   // explicit step, any statement
for char c in "my string" { }         // values
for u64 key in list.keys { }          // keys
```

- Without `step`, the counter increments by 1.
- Loop and boundary checks: compile-time analysis for static values; runtime checks otherwise (can be disabled at compile time).

### 8.2 `switch` statement

```
switch val {
  1: this(), 2: that()
  3, 4, 5: { b = 1; b++ }
  default: other()
}
```

- No fall-through; `default` keyword.
- Case bodies: one statement, or braces for several.
- `jump` continues into another case; a jump loop between cases is a level-3 warning.

### 8.3 Switch expression `? { }`

```
bool res = val ? { 1: true, 2: true, 4: true, default: false }
string yn = ok ? { "yes", "no" }       // bool form: true value, false value
```

- Braces after `?` are always a switch expression. There is no classic `a ? b : c`.

### 8.4 Jumps and labels

```
#retry#
...
jump retry
```

- Labels are written `#name#`, used as `jump name`.
- Jumps may go anywhere in the same block or an outer block; **never into an inner block**.
- Blocks are **cycle blocks** (loops) or **statement blocks**.
- Virtual labels on the innermost cycle block: `jump begin` (≈ continue), `jump out` (≈ break).

---

## 9. Functions

### 9.1 Declaration

```
fun u64 fib(u64 n) {
  if n < 2 {
    return n
  }
  return fib(n - 1) + fib(n - 2)
}

fun log(string msg) { }                          // void: no type
fun i32, string getUser() { return 1, "john" }   // multiple return values
fun (i32 val, string name) getPair() { return (1, "name") }   // tuple return
fun u64 getVal() { c }                           // single expression: implicit return
```

- Multiple return values and tuple returns are **different** things.

### 9.2 Parameters

```
fun hello(string name = "john doe", i32 age) { }
hello("Mick", 24)
hello(27)              // matched by type when unambiguous
hello(age: 27)         // named arguments, required only when ambiguous
```

- Positional (C# style), defaults allowed in any position.

### 9.3 Passing modes

Markers are written **both in the definition and at the call site**.

| Form | Meaning |
|---|---|
| value types (numbers, bool, char, date, …) | by value |
| `&count` | value type by reference |
| objects (string, collections, classes) | by reference; callee may modify for the call's duration (mutable borrow) |
| `obj!` | copy, passed by value |
| `&obj!` | const reference (read-only) |

### 9.4 Lambdas and function types

```
fun string greet = (string name) { "hello " + name }   // lambda variable: types required
sort(list, (a, b) { a < b })                            // inline: types inferred, implicit return

fun callBack(i32 val, i32(string, u8) cb, fun(i32) done) {
  done(cb("a", 1))
}
```

- Function types: `i32(string, u8)` (with return), `fun(i32)` (void). `fun i32(string)` is also valid.
- With overloaded receivers, inline lambda parameter types must be written.

---

## 10. Errors

```
i32 r = 10 / b
catch ex {
  print(ex.message)
}

catch OverflowException ex { }

atEnd {
  cleanup()
}
```

- Any code can raise an exception; there is **no `try`**.
- `catch` handles everything raised **above it in its block**; the innermost matching catch fires.
- Unhandled exceptions propagate to the caller; at the top level the program ends with the message.
- `atEnd { }` runs when the block exits (like `defer`).
- Exception types: C#-like, simplified (to be defined).

---

## 11. Strings

- UTF-8; `char` is a 32-bit code point.
- `.len` = character count; `.sizeof` = bytes.

### Layout

- **Small strings**: fixed 64-byte form, 1-byte length, up to 62 bytes of UTF-8.
- **Large strings**:
  - 32-byte header: char count, finalized byte length, sizeof, block count
  - content bytes
  - 8-byte tail: `0` = end, otherwise pointer to a chain of 4 KB blocks
- Appends go to blocks without moving existing text; changes at the start (e.g. trim) switch to block mode.
- A compaction method (name TBD) merges blocks into the compact form.
- A compact string with tail `0` is already a valid C string.
- `.len`, `.sizeof` and the C-string pointer are O(1).

---

## 12. Collections

### 12.1 Types

- `vec count,type` — fixed size, contiguous
- `list` — growable, counted B-tree
- `map` — sorted (B-tree); `hmap` — hash
- `string` — collection of chars
- `dfoot` — see 12.5

### 12.2 Literals

```
vec 4,i16 v = {1, 2, 3, 4}
map m = { one: 1, two: 2, three: 3 }     // map<string, i32>
map n = { 1: "one", 2: "two" }           // map<i32, string>
```

- Braces after `=`, in arguments, or after `return` are collection literals (after `?` they are a switch expression).
- **Map keys are always literals**; bare names are string keys. Values may be any expression.
- All keys in a `map` share one type; the map type is inferred from the literal.

### 12.3 Members

| Member | Applies to | Description |
|---|---|---|
| `.len` | all | count / length |
| `.sizeof` | all types | bytes to store the object itself (shallow; references count as pointer size) |
| `c[k]` | all | read by index/key |
| `c[k] = v` | all | set / alter |
| `add(value)` | non-keyed | append |
| `add(collection)` | non-keyed | append all |
| `has(value)` | all | bool |
| `hasKey(key)` | all | bool |
| `indexOf(value)` | list, vec | position or null |
| `keyOf(value)` | maps, others | key or null |
| `remove(value)` | all except vec | remove |
| `removeAt(pos / key / range)` | all except vec | e.g. `removeAt(2..)` |
| `tryGet(key, &out[, default])` | maps | bool |
| `c[a..b]` | all | sub-collection, same type |
| `.keys` | all | keys (positions when none are defined) |

- Sub-collections are **shallow copies**: new container, same objects (value elements are copied).
- `m[key]` with a missing key: null (nullable values), zero value (non-nullable), or an exception when a matching `catch` exists in the function.
- Positional access is only for lists and vectors; maps use keys.

### 12.4 Zero values

Numbers 0, bool false, char `\0`, date/clock/lapse null, structs all fields zeroed.

### 12.5 `dfoot` — Dflat Fast Open Objects Tree

- Recursive, ordered tree for structured data (JSON-like, **not** JSON-compatible).
- **Keys** stored as *macrotype + value*:
  - signed number (negative values), unsigned number (non-negative values), string, bool, date
  - `r` types and `dec` cannot be keys
  - `1` and `"1"` are different keys; `(u8)5` and `(i32)5` are the same key
- **Values**: any Dflat type, including classes (details TBD).

```
{ 1: "first number", a1: "string", false: "boolean", "false": "string" }
```

---

## 13. Enums

```
public enum Color Colors {
  Red: Color(1, 0, 0), Green: Color(0, 1, 0), Blue: Color(0, 0, 1)
  , fun Colors Closest(Color c) {
    return Colors.Red
  }
}

enum Status { Active, Inactive, Pending: 10 }     // i32, auto-increment
```

- `enum [ValueType] Name { }`; value type defaults to `i32`.
- Values: auto-increment integers or any compile-time constant (structs allowed, no dynamic classes).
- Aliases (same value, two names) allowed; comparison is **by value**.
- Methods are **static only**: `Colors.Closest(c)`.

---

## 14. Classes

```
public class Px(i32 a, i32 b) {
  u64 c = [u64]a << 32 | [u64]b

  public Px() : Px(0, 0)
  public Px(i32 b) : Px(b, b)
  public Px(u64 a, char b) : Px([i32]a, (i32)a) {
    c = [u64]b
  }

  public fun u64 GetVal() { c }
}

Px p = Px(5)          // no "new"
```

- Classes are **reference types**, nullable by default.
- **Primary constructor** in the header; its parameters become fields automatically.
- **Secondary constructors** must chain `: ClassName(...)`; the primary runs first, then the body, which may reassign fields (redundant primary assignments are optimized away).
- `param.x` reaches a parameter shadowed by a field.
- Constructor bodies are optional.

### Visibility

| Keyword | Visible to |
|---|---|
| (none) | private |
| `public` | everyone |
| `internal` | its package (namespace) and all sub-packages |

---

## 15. Program structure

- Code lives in classes; classes live in namespaces (packages with sub-packages).
- **Top-level statements** only in `program.df` or `main.df` (one per program); `args` is available; `return n` sets the exit code.
- Without top-level code: `fun i32 main(... args) { return 0 }`.
- **Imports**: `import Name`.
- **Standard library**: static class `Root`, used as `Root.Print(...)` or after `import Root`. Minimal contents: console, conversions, math, time, files, program args/exit.

---

## 16. Toolchain

- **dfparse** (`dfparse [--tokens] file.df`): .NET 10 console app, hand-written lexer and recursive-descent parser, prints the syntax tree.
- **Compiler plan**:
  1. lexer + parser
  2. semantic pass (names, types, literal and cast rules)
  3. emit C
  4. compile and link with clang
- **Runtime**: minimal or zero libc (`-ffreestanding -nostdlib`), calling the OS directly (Windows: `kernel32` — `WriteFile`, `ReadFile`, `CreateFileW`, `VirtualAlloc`, `ExitProcess`), plus its own `memcpy`/`memset`/`memmove`/`memcmp`.
- **Pending parser changes**: `vec count,type` order; reject `()`; open-ended ranges.

---

## 17. Open items

- Generics (type syntax for `list<T>`, `map<K,V>`)
- Interfaces / traits, inheritance
- Namespace declaration syntax
- Class-body statements vs constructor body
- Lambda captures
- Overloading rules
- Resource lifecycle / lifetime inference, shared ownership (objects in several collections)
- `atEnd` details (order, on exception)
- Exception types and `throw`
- `r` → integer cast with NaN / infinity
- Statement-termination grammar
- Keyword casing style
- `jump` syntax for switch cases
- String compaction method name
- `dfoot` with classes as values
- Collection extras: insert, clear, sets, queue/stack, sort, iteration while modifying, list literal vs vec literal
- Pointers
- `Root` contents in detail
- Deferred: async/await, locks/atomics, tasks, SIMD, EF syntax, Vulkan, shaders
