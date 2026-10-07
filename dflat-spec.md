# Dflat Language
A new language D♭ that is the same as C# but easier to write and faster to execute

**Version** 0.0.2  2026/10/07 by CPU

# D♭ — Dflat Language Specification (draft)

Status: design in progress. Decisions below reflect the latest choices; earlier alternatives are omitted.

---

## 1. Overview

- **Name**: Dflat. Logo: **D♭**. Source extension: **`.df`**.
- **Position**: between C# 12 and Rust. Short, clean, modern C-style syntax; no legacy C# compatibility; no Rust lifetime annotations or pedantic syntax.
- **Goals**: speed and simplicity, a C++ replacement. Also targets very small devices (micro-controllers, C64-class machines).
- **Inspiration**: semantics and memory model from Rust; syntax from C# and Dart.
- **Planned**: native Entity Framework syntax, Vulkan integration (not yet designed).

---

## 2. Lexical rules

- **Comments**: `// line` and `/* block */`.
- **Statement end**: JavaScript rules. A line break ends a statement when the statement can end there; `;` is needed only for several statements on one line.
- **Identifiers**: cannot start with `_` (underscores inside names are fine).
- **Naming convention** (not enforced): namespaces lowercase; public classes, methods and members PascalCase; everything else camelCase. Built-in types are lowercase.
- **Labels**: `#name#`.
- **Reserved**: `...`.

### 2.1 Number literals

| Literal | Type |
|---|---|
| `123` | `i32` |
| `123u` | `u32` |
| `123l` | `i64` |
| `123ul` | `u64` |
| `123s` / `123us` | `i16` / `u16` |
| `123b` | `byte` (`u8`) |
| `1.5` | `r64` |
| `1.5f`, `2f` | `r32` |
| `6.022e23` | `r64` |
| `0x1F` | hex; size from digit count (2 → 8 bit, 4 → 16, 8 → 32, 16 → 64), signed; `u` suffix for unsigned |
| `0b1010` | binary, same rules as hex |
| `1_000_000` | `_` digit separator |

- Suffixes are case-insensitive.
- Literals adapt to the declared type; a value that doesn't fit is a compile error.
- `0x00000000DEADBEEF` is a 64-bit value; hex and binary always describe raw bits.

### 2.2 Characters and strings

- Chars: `'a'`. Strings: `"text"`.
- Escapes: `\n \r \t \\ \' \" \0`, code points `\u{1F600}` (hex) and `\#{123}` (decimal).
- An invalid code point in a literal (surrogate, above `0x10FFFF`) is a compile error.
- Interpolated strings: `$"Hello {name}, total {x:D,02}"`; `\{` and `\}` for literal braces (see 14).

---

## 3. Built-in types

### 3.1 Numbers

| Type | Description |
|---|---|
| `i8` `i16` `i32` `i64` `i128` `i256` | signed integers (`i128`/`i256` software-emulated for now) |
| `u8` `u16` `u32` `u64` `u128` `u256` | unsigned integers |
| `r16` | IEEE 754 binary16 (GPU half) |
| `r32` `r64` `r128` | IEEE floats (`r128` software-emulated) |
| `dec` | fixed-point, 32 + 32 bits |
| `byte` | alias of `u8` |
| `int` `uns` `real` | target-dependent aliases, mapped by compiler options or conditional compilation; for small targets; not allowed in `dfoot` |

Numbers are **not nullable** unless declared `type?`.

### 3.2 Other scalars

| Type | Description |
|---|---|
| `bool` | true / false |
| `char` | 32-bit Unicode code point |
| `date` | 64-bit, ms since 1 Jan 1970 UTC; null = minimum value |
| `clock` | u64 monotonic counter, 0.1 ns, from `GetClock()`; intervals only; `GetClock()` never returns 0; null = 0 |
| `lapse` | difference between two dates or two clocks (3.3) |

### 3.3 `lapse`

Signed 64-bit; raw value = `value × 8 + code`.

| Code (bits 2-1-0) | Meaning | Range |
|---|---|---|
| `000` | null | — |
| `010` | date lapse, ms | ±36 million years |
| `011` | clock lapse, 0.1 ns | ±3.6 years |
| `100` | year lapse | beyond ±36M years |
| `111` | clock lapse overflow | — |

- `date − date` → date lapse; `clock − clock` → clock lapse.
- Mixed units convert to date lapse, rounding away from zero. Null propagates.
- Lapses format as numbers in their own unit.

### 3.4 Composite and collection types

| Type | Description |
|---|---|
| `vec<N, T>` | fixed size, contiguous (arrays are vectors) |
| `list<T>` | growable, counted B-tree, O(log n) insert/remove |
| `map<K, V>` | sorted (B-tree) |
| `hmap<K, V>` | hash map |
| tuple | `(i32, string)` or `(i32 val, string name)`; **at least 2 elements** |
| `string` | UTF-8 text (13) |
| `dfoot` | Dflat Fast Open Objects Tree (12.5) |

- GUID is defined by the EF layer, platform-specific.
- Lengths and default indexes are `u64` everywhere.

---

## 4. Declarations and namespaces

```
i32 a = 10
const i32 limit = 100
i32 id, string name = getUser()     // multiple return values
existing, _, i32 fresh = f()        // mix existing, discard, new
x, y = y, x                         // swap: right side evaluated first
```

- Type-first; C#-style scope and definite assignment.
- `const`: read-only value and pointer, global guarantee.
- A tuple or literal must be assigned, passed or returned; alone it's a compile error.
- Tuple destructuring on the left: open.

### 4.1 Namespaces, imports, aliases

```
namespace clinic.planning         // one per file, no braces
namespace local                   // name from the folder path
import stdrnd
alias r Root
alias byte8 u8
```

- `program.df` > `main.df` > `lib.df`: the first one found is the root (executable, or library for `lib.df`); a skipped root file gives a level 1 warning. `dfcompile -forcelib folder/` forces a library.
- The root file declares its namespace explicitly; `local` = root namespace + folder path.
- Aliases are pure aliases (interchangeable with the original).

---

## 5. Nulls

- Value types: nullable only as `type?`. Classes are nullable.
- Strings: `""` and null are the same.
- Null propagates in arithmetic; equals only null; sorts first.
- Null object access raises `NullPointerException`; no `x!` force-unwrap.
- `enforce a, b { } else { }`: the only unwrap for `type?` (and for weak references, 15).
- `?.` `??` `??=`; `i8 b = maybe ?? 0` unwraps into a non-nullable target.
- `a == null` may call an overloaded `==`; `a is null` always checks the reference.

---

## 6. Conversions

- **Cast `(type)`**: keeps the value; out of range saturates (level 2 warning). From `r`: +∞ → max, −∞ → min (0 for unsigned), NaN → 0.
- **Transmute `[type]`**: same bits; truncates low bits or zero-extends. `[bool]`: 0 = false. `[char]`: invalid → U+0000.
- **Implicit**: only same-family widening (`i8 → i64`, `r32 → r64`) and any type → bool.
- **To bool**: numbers 0 / NaN = false; `char` `\0`; `date` / `clock` null; `lapse` zero or null; `string` null/empty; `vec` and tuples not convertible.

---

## 7. Operators

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
| ownership | `->x` move, `x!` copy, `&x` by reference |

- Assignments are not expressions (`++`/`--` are).
- `+` evaluates left to right; with a string or char operand it concatenates: `1 + 2 + "a"` = `"3a"`, `'a' + 'b'` = `"ab"`. Char codes need a transmute: `[u32]'a' + [u32]'b'`.
- Overflow wraps unless a `catch` for `OverflowException` is in the same function.
- Division by zero and similar errors are always checked explicitly (no CPU traps).
- Precedence (Rust): postfix, unary, `* / %`, `+ -`, shifts, `&`, `^`, `|`, comparisons and `is`, `&&`, `^^`, `||`, ranges, `??`, `? { }`.
- Operator overloading: allowed for custom classes, not for built-in types. Overloading `==` on a map key class requires a matching hash.

---

## 8. Control flow

```
if a > 0 {
} else {
}
if a > 0 then jump done           // single statement, same line, no else

while i < n { }
{ a-- } while a > 0

for u8 i = 0..10 { }
for u16 i = 0..=100 step i += 2 { }
for char c in "text" { }
for u64 k in list.keys { }

break                             // innermost loop
break Found                       // to label #Found#
continue                          // no label
jump #retry#                      // to a label
```

- `break` / `continue` outside a loop: compile error.
- Jumps go to the same or an outer block, never into an inner one.

### 8.1 Switch

```
switch val {
  1: one(), 2: two()
  3, 4, 5: { b = 1; b++ }
  Red: jump 3                     // jump to another case by value
  default: jump #out#             // explicit label
}
bool res = val ? { 1: true, default: false }
string yn = ok ? { "yes", "no" }
```

- No fall-through; `default`; a jump loop between cases is a level 3 warning.
- Inside a switch, `jump name` targets a case; `jump #name#` always targets a label.

---

## 9. Functions

```
fun u64 fib(u64 n) { ... }
fun log(string msg) { }                        // void
fun i32, string getUser() { return 1, "a" }    // multiple values
fun (i32 v, string n) getPair() { return (1, "a") }   // tuple
fun u64 getVal() { c }                         // single expression returns
```

### 9.1 Parameters

| Definition | Call | Meaning |
|---|---|---|
| `i32 x` | `f(x)` | value type, copied |
| `&i32 x` | `f(&x)` | value type by reference |
| `Item o` | `f(o)` | object borrowed, callee may modify |
| `&Item o!` | `f(&o!)` | object borrowed read-only |
| `Item o!` | `f(o!)` | object copied |
| `->Item o` | `f(->o)` | ownership moves to the callee |

- Defaults anywhere; named arguments when needed (`hello(age: 27)`).

### 9.2 Overloading

- Literals: the native-size signed type wins (`i64` on 64-bit), even over an exact `i32`.
- Variables: exact type, then smallest same-family widening; other families are not candidates; still tied → compile error.

### 9.3 Lambdas

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

---

## 10. Errors

### 10.1 Syntax

```
i32 r = 10 / b
catch DivisionByZeroException ex { }
catch ex { }
throw Exception("message")
throw MyException(123, "wow")
throw Panic(3)
atEnd { cleanup() }
```

- No `try`; a `catch` handles everything raised above it in its block; innermost matching catch wins; unhandled → caller; top level → program ends with the message.

### 10.2 Types

- `root.Exception` is the base; standard ones are subclasses in `root`: `NullPointerException`, `OverflowException`, `InvalidCastException`, `DivisionByZeroException`, `OutOfMemoryException` (list grows during development).
- Subclasses only when they carry extra fields.
- `ChangingRefCountTypeException`: switching an object between the two counting kinds.
- `Panic(code)`: critical exit, not an exception; not catchable, no cleanup, exits with `code`.

### 10.3 Implementation

- Rust/Swift style: a failing function returns normally with an **error register** holding 0 or a pointer to an error record; the caller jumps to its `catch` or propagates. Functions that can't fail skip the check.
- Error record: `{ id, descriptor*, message, subclass fields }`. Descriptor: `{ id, name, parent* }`.
- IDs: 0–255 reserved for `root`, otherwise a hash of the full name. Exact catch = one compare; base-class catch walks the parents.
- Release builds keep function, type and message; debug builds add the stack trace.
- External libraries raise the base types with a short message; richer errors via an exported mapping function.

### 10.4 `atEnd`

- Registers code for the enclosing scope; runs on every exit (end, `return`, `break`, jumps, exceptions), not on `Panic`.
- Several `atEnd` run in reverse order; owned objects are freed in reverse order too.
- An exception inside `atEnd` while another is propagating → `Panic`, printing both (details later).

---

## 11. Enums

```
public enum Color Colors {
  Red: Color(1, 0, 0), Green: Color(0, 1, 0)
  , fun Colors Closest(Color c) { return Colors.Red }
}
enum Status { Active, Inactive, Pending: 10 }
```

- `enum [ValueType] Name`; `i32` by default; values are constants (structs allowed); aliases allowed; compared by value; methods static only.

---

## 12. Collections

### 12.1 Literals

```
list<i32> l = [1, 2, 3]
vec v = ["a", "b", "c"]                 // vec<3, string>
map m = { one: 1, two: 2 }              // map<string, i32>
map n = { 1: "one", 2: "two" }          // map<i32, string>
```

- Types are inferred from literals; integers default to `i32`, mixed `[1, 2.5]` → `r64`; other mixes are errors; empty literals need a declared type.
- Map keys are literals (bare names are strings); values may be any expression.

### 12.2 Members

| Member | Description |
|---|---|
| `.len` | count / length |
| `.sizeof` | bytes to store the object itself |
| `c[k]`, `c[k] = v` | read / write |
| `c[a..b]`, `c[..b]`, `c[a..]` | sub-collection, same type (shallow) |
| `add(v)`, `add(collection)` | append |
| `has(v)`, `hasKey(k)` | bool |
| `indexOf(v)` | lists and vectors |
| `keyOf(v)` | maps and others |
| `remove(v)`, `removeAt(k / range)` | not on vectors |
| `tryGet(k, &out[, default])` | maps |
| `.keys` | keys or positions |

- Missing map key: null, zero value, or exception if a matching catch exists.
- Collections own their elements (15).

### 12.3 `dfoot`

- Recursive ordered tree, JSON-like, not JSON-compatible.
- Keys: macrotype + value (signed number, unsigned number, string, bool, date); `r`, `dec`, `int`/`uns`/`real` not allowed.
- Values: any Dflat type, classes included (details later).

---

## 13. Strings

- UTF-8; `.len` = chars, `.sizeof` = bytes.
- Small strings: 64 bytes, 1-byte length, up to 62 bytes.
- Large strings: 32-byte header (char count, byte length, sizeof, block count), content, 8-byte tail (0 or pointer to 4 KB blocks); a compact string is a valid C string.
- Every type has `toStr()`; class default: `Px { a: Py{}, b: 3, c: BTree{}, … }` (first 3 fields, one level).

---

## 14. Formatting

Used in `$"{expr:spec}"` and `toStr("spec")`. Options in any order; letters act as separators; reserved letters as fill/separator chars are a compile error.

### 14.1 Numbers (case-insensitive except `h` / `H`)

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
| `e` | scientific (to define) |

- Hex / binary show raw bits (no negative hex). Width overflow prints the full number.
- Special values: `NaN`, `∞`, `-∞`, `null` (`INF` / `-INF` on targets without Unicode).
- Default for reals: as C# (shortest round-trip). Example: `12345.toStr("f0w10")` → `0000012345`.

### 14.2 Strings

| Option | Meaning |
|---|---|
| `w<n>` / `wc<n>` | width in characters / display columns |
| `f<char>` | fill |
| `a<` `a^` `a>` | alignment |
| `t<'…'` `t>'…'` | truncate keeping start / end, with marker (counted in the width) |
| `cU` `cL` `cS` `cT` | upper, lower, sentence (`Hello world`), title (`Hello World`) |
| `q` | quoted and escaped |
| `s` `sl` `sr` | trim both / left / right |

### 14.3 Dates

- C# custom pattern letters: `d f F g h H k m M s t y z`.
- Additions: `w` week, `q` quarter, `i` ISO week, `r` ISO year, `e` day of year (3 wide), `j` day of week; a doubled letter pads with a space.
- Alignment: `a<20`, `a^20`, `a>20`; no truncation.
- Locale names and first day of week: later.

---

## 15. Memory and ownership

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

- Single owner; freed at the end of the owner's scope.
- Function parameters borrow; returning moves ownership to the caller.
- Collections own their elements: adding moves in, removing moves out, reading borrows.
- Switching an object between the two counting kinds → `ChangingRefCountTypeException`.
- Weak references are used through `enforce w { } else { }`; inside the block the object is kept alive.
- `.asRefCount` / `.asSharedRefCount` / `.isRC` are built-in operations, not methods.

---

## 16. Classes and traits

```
public class Circle(r64 radius) : root.Class, Shape {
  public Circle() : Circle(1.0), Named("circle")
  public override fun r64 Area() { r.Math.Pi * radius * radius }
}

trait Named {
  string name
  Named(string n) { name = n }
  fun string Label()                     // no body: must be implemented
  fun string Hello() { $"Hi {Label()}" }
}
trait Shape : Named { fun r64 Area() }
```

- No `new`: `Circle c = Circle(2.0)`.
- Primary constructor parameters become fields; secondary constructors chain `: Class(...)`, plus trait constructors.
- Single parent class; any number of traits; base of everything: `root.Class`.
- Construction order: parent, traits in listed order, class body.
- `override` mandatory; `parent.Method()` reaches the overridden version.
- Traits: properties, methods with or without body, constructor when they have properties; one parent trait; a supertrait inherited twice exists once.
- Same member in two traits: `TraitA.member` (inside), `px.TraitA.member` (outside), unqualified → compile error.
- Visibility: private by default, `public`, `internal` (namespace + sub-namespaces). A subclass sees everything when the parent's source is available, public only for compiled libraries (at its own risk). No `sealed`.
- Type checks: `if v is Circle c { }` (reference, `c` scoped to the block), `if v! is Circle c { }` (copy); upward checks are a warning.
- Trait-typed values: the compiler chooses static or dynamic dispatch; force with `impl Shape` / `dyn Shape`.

---

## 17. Standard library (first sketch)

```
alias r Root
r.GetClock()        r.Now()
r.Console.Log  .LogError  .GetInput  .GetChar
r.Math (= Math64, r64)    r.Math32 (r32 results)
  Sin Cos Tan Asin Acos Atan Atan2(y, x) Sinh Cosh Tanh
  Sqrt Cbrt Pow Exp Ln Log10 Log(x, base)
  Abs Sign Min Max Clamp (also integers)
  Floor Ceil Round Trunc Hypot Fmod Lerp(a, b, t) InverseLerp(a, b, v)
  constants: Pi PiHalf E Sqrt2 R2D D2R
```

- Type members live on the types: `d.Year`, `d.AddDays(3)`, `s.Split(",")`, `c.IsDigit`, `list.Sort()`.
- Random: `import stdrnd`; `Rnd rnd = Rnd([u64]r.Now())`; `Int(min, max)`, `RealRange(min, max)` (max excluded), `Real()` in [0, 1], `Seed(u64)`, `Shuffle(c)` for lists, vectors, strings. Generator: later.
- To add: strings, chars, parsing, files, paths, environment, time.

---

## 18. Toolchain

- **dfparse** `[--tokens] file.df`: .NET 10, hand-written lexer and recursive-descent parser, prints the syntax tree.
- **dfcompile** (planned): parse → type check → emit C → clang; minimal or zero libc, calling the OS directly.

---

## 19. Open items

- Tuple destructuring on the left
- Scientific number format `e`
- Locale details
- Date formatting week rules
- Conditional compilation syntax
- Generics (user-defined)
- Exception list, `Panic` exit codes for `atEnd` failures
- Stdlib: strings, files, time, collections extras (insert, clear, sets, queue/stack)
- Pointers
- Deferred: async/await, locks/atomics, tasks, SIMD, EF syntax, Vulkan, shaders
