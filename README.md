# Dflat Language
[![Release](https://img.shields.io/github/v/release/CPULL/Dflat?include_prereleases)](https://github.com/CPULL/Dflat/releases)

A new language D♭ that is the same as C# but easier to write and faster to execute.

## Overview
**Dflat (D♭)** pairs the clean, expressive syntax of C# with the deterministic ownership and bare-metal speed of Rust, eliminating both garbage collection and complex lifetime annotations. 

Designed to bring the C# style where C++ is used today, it compiles to C and then to native code, and it is designed to scale from high-performance graphics down to microcontrollers. If you know any C-style language, you can already read Dflat.

## Implementation
The current toolchain consists of a parser (`dfparser`) and a transpiler/compiler (`dfcompiler`):
- Source files with the `.df` extension are parsed and lowered into efficient native C.
- The emitted C code is compiled via **Clang** to produce the final executable.
- The generic C standard library is currently statically linked; a minimal/zero-libc runtime is planned.

Syntax highlighting packages available:
- **VS Code** extension
- **Notepad++** language definition

## Language Specification
The complete, living language draft is tracked in the repository:  
👉 [Dflat Language Specification](dflat-spec.md)

## Status of Development
- **Language Design:** ~95% defined
- **Parser:** Covers 99% of the specification
- **Compiler:** Implements ~80% of the specified features
- **Standard Library (`Root`):** Minimal standard library operational (I/O, File, Streams, ExecProcess, Collections, Math, Time)
- **Vulkan:** Minimal integration library implemented
- **In Progress / Planned:** SIMD, Inline Assembly, Entity Framework syntax

## Why Dflat?
Dflat targets the sweet spot: the developer ergonomics and readability of C#, combined with the raw performance and deterministic safety of Rust—without pedantic lifetime annotations. 

Systems programming shouldn't require fighting a borrow checker's syntax or tumbling down a "pyramid of doom" of nested indentation.

## Why this Name?
D♭ (D flat) and C♯ (C sharp) are enharmonic equivalents—they are the exact same pitch on a keyboard.

The name is a quadruple pun:

1. **The Musical Joke:** A language that is "the same as C#, but with a different notation." Calling it D♭ is the ultimate music-theory way of saying: *It plays the exact same tune as C#, just written in a cleaner key signature.*
2. **The Alphabetical Twist:** In programming language history, C led to C++ and C#, while B led to C and C led to D. Instead of predictable names like "D++" or "D#", flattening D (D♭) brings you right back to C♯ on the piano.
3. **The "Flat" Engineering Metaphor:**
   - **Flat memory:** Cache-friendly, contiguous allocations without pointer-chasing GC spaghetti.
   - **Flat code:** No "pyramid of doom" indentation from nested `try` / `catch` / `finally` and `using` blocks.
   - **Flat learning curve:** Deterministic memory safety without the steep, jagged cliff of lifetime annotations.
4. **No one wants to play in C♯ major:**
   - **D♭ major** has 5 flats (B♭, E♭, A♭, D♭, G♭).
   - **C♯ major** has 7 sharps (F#, C#, G#, D#, A#, E#, B#)—meaning every single note in the scale has an accidental, including awkward notes like E# and B# (which sound like F and C).
   
   C# (the language) has accumulated its own "seven sharps": twenty-five years of legacy baggage, esoteric keywords, deep nesting pyramids, and runtime overhead. D♭ plays the exact same piece, but wipes the awkward accidentals off the page so you can just read and write clean music.

*(And Rust is C major).*

## Hello world

```csharp
Root.Console.log("Hello, world!")
```

## Differences from C#
- **No garbage collector:** Every object has a single owner and is freed when its owner goes out of scope; `->` explicitly moves ownership.
- **`atEnd` blocks:** Attached to classes, functions, or scopes, replacing finalizers, `using`, and `try/finally`.
- **No `new` keyword:** Construct objects directly: `Job job = Job("Write README", 3)`.
- **`fun` before return type:** Method signatures use `fun <type>`: `public fun string urgency() { ... }`; the last expression is the return value.
- **Native collections:** Built-in `vec`, `list`, `map` / `hmap`, and `set` / `hset` with literal syntax `[1, 2, 3]` and `["a": 1]`, replacing `List`, `Dictionary`, and `HashSet`.
- **Explicit-width scalars:** `i8`–`i64`, `u8`–`u64`, `r32`, `r64`, `dec`, plus architecture-sized `int` and `uns`.
- **Unified switch expression:** `x ? { 1: "one", default: "other" }`, which also replaces the ternary operator: `ok ? { "yes", "no" }`.
- **True native binaries:** Compiles down to optimized native C and machine code via Clang—no runtime VM, JIT, or CLR overhead.

## Example

```csharp
alias Root.Console as console

// Primary constructor with automatic field generation
class Job(string name, i32 priority) {
  public string name
  public i32 priority

  public fun string urgency() {
    // Switch expression with range matching
    priority ? { 1: "Critical", 2..=3: "Important", default: "Routine" }
  }
} atEnd {
  // Class destructor runs deterministically when dropped
  console.log($"[Drop] Job '{name}' freed from memory")
}

fun main() {
  // Growable contiguous vector owning its elements (no GC)
  vec<Job> queue = []
  queue.add(Job("Release compiler v0.1", 1))
  queue.add(Job("B-Tree memory tuning", 2))

  // Explicit ownership move (->) for existing objects
  Job extra = Job("Write README", 3)
  queue.add(->extra) // 'extra' is moved into the queue and becomes null

  // Sorted B+ tree map with literal syntax
  map summary = ["Critical": 0, "Important": 0, "Routine": 0]

  // Clean iteration with read-only borrowing
  for var job in queue {
    string level = job.urgency()
    console.log($"[{level}] {job.name}")
    summary[level] += 1
  }

  // Built-in collection formatting
  console.log($"Totals: {summary}")
} atEnd {
  // Function-level exit cleanup
  console.log("Finished: all memory and collections cleanly unwound.")
}
```