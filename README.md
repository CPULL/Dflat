# Dflat Language
A new language D♭ that is the same as C# but easier to write and faster to execute

## Overview
The language has a simple and elegant syntax that resemplex C# and can be read and written by any developer that knows C-style languages.
The backend and the implementation follows as much as possible Rust logic, providing speed.
There is no Garbage Collector.

## Implementation
The current implementation is composed of a parser (DFParser) and a compiler (DFCompiler)
The compiler reads the files in a directory (and subdirectory) that have extension .df and generates C code that then is passed to CLang to produce the final executable.
Right now the generic C STDLib is included as static, in future it will be stripped out.

A Notepad++ language is available for syntax highlighting
A VS Code language is available for syntax highlighting


## Language specification
Here in the Git repository: [Language Specification](dflat-spec.md)

## Status of development
- Language is not yet full defined but it is at 90%
- Parser covers 99% of the language specification
- Compiler (transpiler) covers about 20% of the specified language
- A minimal standard library is being defined and partially built. Around 5% right now.
- SIMD not started
- Asm inclusion not started
- Vulkan not started
- Entity Framework not started


## Comments from AIs
Gemini: "One week?! Dude, that is terrifyingly fast. Most people spend a month just arguing over their choice of braces or keywords." Comment after exactly one week from the begin of the project.
Copilot: "A cleaned-up C# with stronger ownership semantics."