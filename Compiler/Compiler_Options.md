# Compiler options
Option names are not case sensitive. The arguments after `CL:`, file names and paths follow the rules of the operating system.

- `--output <file>` path of the executable. The default is: `build.<target>/<name>[.exe]` next to the source. It can be an actual file and it will become the executable, all folders that are missing will be created. Or a folder (it needs a trailing directory separator) then the folder will be used to output the executable (the name defined in the project, or the name of the project file, or the name of the source file)
- `--target <target>` win-x64, win-x32, linux-x64, linux-arm64, osx-x64, osx-arm64, or `CL:<clang arguments>`: the arguments are passed, AS-IS, to CLang. The default is: use the architecture of the machine running the compiler
- `--emit-c` generate only the C file (`.compilation/<name>.c`), no compilation of this file will occur.
- `--run` run the program after the build. It must be the very last option; everything written after `--` is given to the program as its arguments: `dfcompiler source.df --target linux-x64 --run -- first second`. It can be used only in a direct compilation, not in a `.dsprj` file.
- `--cc <path>` and environment variable `DFLAT_CC` the clang to use, by default is used the one specified on `PATH`
- `--version` print the compiler version and exits
- `--c-std <name>` the C language standard given to the C compiler: `c99`, `c11`, `c17` or `gnu11`. The default is `c11`. The generated C uses the statement-expression extension of clang and gcc, so the C compiler must be one of them.
- `--libc <name>` the C library to link: by default the library of the target; `none` for no C library (needs the runtime without the C library, not built yet); any other value is the name or the path of a specific library to link.
- `--COptLev <number>` optimization level to be used by the C compiler. The default is 2.
- `--COptMode <mode>` what the C compiler optimizes for: `size` (`-Os`) or `speed` (`-O<COptLev>`, the default).
- `--debug` and `--release` the value of the preprocessor variable `build`. The default is `--debug`. A debug build keeps the line and the stack trace of the exceptions (`Line`, `StackTrace`), a release build gives line 0 and a null stack trace (spec 15.2); for now the options only set the variable.
- `--suppresswarning <level>` suppress the warnings of the specified level and of the less important levels (1 is the most important, 3 the least): `--suppresswarning 2` suppresses the warnings of levels 2 and 3. In the future we will see how to suppress specific warnings.
- `--chaindepth<type> <number>` limit the number of copies for a specific type of collections for different types of objects. They are defaulted to 16 and can be any number between 1 and 256. Arrays are by default limited to 64. Valid types are `arr, list, map, hmap, tree, htree, set` (see the proposal below)
- Live silent exception classes: at most 64, because the observed mask is 64 bits (to be discussed, see the proposal below)
- `--native-int <bits>`, `--native-real <bits>`, `--native-addr <bits>` force the native size of the integers, of the reals and of the addresses to the specified number of bits. There is no option for `uns`: it always has the same size as `int`. For example `--native-int 32` forces the native size of the integers to be 32 bits. Sizes supported are 8, 16, 32, 64, 128, 256 (some may not be possible on specific target architectures, and a type may accept only some of them, for example a real has no 8 bits).
- `--cachealign <number>` the alignment for cache lines, in bytes. The default is 64 bytes.

## Other options to be checked
- `--lazy-statics` initialize the static properties of a class when they are first used. By default all of them are initialized at program start, in declaration order (spec 11.1.2).
- `--force-lib` build a folder as a library even when its root file would make it an executable. The root file is the first one found of `program.df`, `main.df`, `lib.df`, and `lib.df` makes a library (spec 19). Not built.
- `--no-exceptions` build for a target without exceptions, for example a C64: the `catch` blocks are ignored, a silent exception falls back to its silent behavior and a non-silent exception ends the program (spec 15.6). It could be a property of the target instead of an option.
- Split of `int` and `uns` by use (calculation, memory offsets, file offsets), before 0.1.0 (stdlib 6.9): this may mean several size options.

## Implied, not discussed as options yet
- Warnings (spec 19.3): showing only up to a level and making warnings errors. Suppressing is covered by `--suppresswarning`.
- Conditional compilation: the preprocessor of spec 19.1 (`##define`, `##if`, ...). The symbols are defined in the source with `##define`; the variables set by the compiler come from the options listed in "Preprocessor variables".
- Locale: an open item. The number formats have an `l` for locale, and the default is culture-invariant.
- DateTime library: import path and timezone database, "to be defined".
- Root file and root namespace: which file starts the program, and the name of the root namespace (`namespace local` is built from the folder path).

## Preprocessor variables
The variables of the preprocessor (spec 19.1) that are set by the compiler take their values from these options. `endian` comes from the cpu, `bits` from the target, and `ram` is 4gb until a custom architecture defines it (planned). The native sizes `int`, `uns` and `real` are 32, 32 and 64 bits until the options `--native-int` and `--native-real` are built. `version` is the language version (the version of the specification the compiler was built for).

| Variable | Option |
|---|---|
| `os`, `cpu`, `arch` | `--target` (the default is the machine running the compiler) |
| `int`, `uns`, `real` | `--native-int`, `--native-real` (`uns` has the size of `int`) |
| `build` | `--debug` or `--release` |
| `exceptions` | `--no-exceptions` (or the target) |
| `coptlev` | `--COptLev` |
| `coptmode` | `--COptMode` |
| `version` | the language version: `version.major`, `version.minor`, `version.build` (not an option; `--version` prints it next to the compiler version) |

## Compilation projects
A file with the extension `.dsprj` can have all the options listed, except `--run` and `--version`, and the key `CompilerVar` (below). The compiler will try to read it first in case we compile a folder or we pass explicitly the `.dsprj` file to compile.
The file is a normal text file with the options specified as: `option name = parameter`, the option name without the `--` and the values after an `=` (similar to old INI files)
```
output=../../FinalBuildFolder/
coptlev=2
name=MyAwesomeExecutable
target=linux-arm64
native-int=16
include=..\..\includes\a library I need\version 1.2.3\
```

### Claude proposals
**Limits of the generated copies (spec 11.3.12).** Replace the `--chaindepth<type>` line with three families of options, each between 1 and 256:
- `--max-chain-depth <n>`: nested instantiations (a generic that instantiates another that instantiates another). The default is 16.
- `--max-copies <n>`: different type combinations of one generic in the user code. The default is 16.
- `--max-copies-<collection> <n>`: the same for each collection of the library: `vec`, `list`, `map`, `hmap`, `set`, `hset` (`bmap` and `bset` are aliases of `map` and `set`). The default is 64.
- `arr` has no limit (spec 11.3.12), and `tree` and `htree` are not names of the specification. In the project file the keys are the same names without `--`.

**Live silent exception classes.** Keep it as an internal limit, not an option. Today the mask has 64 bits, so at most 64 silent exception classes can be handled by catches at the same time (a compile error says so). On a small target it could follow the native size of `int` (16 bits would allow 16 classes).

**Always passed to the C compiler, not options.** `-fwrapv` makes signed overflow wrap around, as Dflat defines it; without it C treats the overflow as undefined behavior and may optimize wrongly. The target flags (`--target=...`, `-lm`, `-D_CRT_SECURE_NO_WARNINGS`) come from `--target`. If a way to add arguments for the C compiler is needed, one passthrough option is enough: `--cflags "<arguments>"`.

**Preprocessor variables in the project file.** The key `CompilerVar` defines a variable for the preprocessor (spec 19.1): `CompilerVar=<name>=<value>`, one line per variable. The value is `true`, `false`, a number, or a text without spaces (`CompilerVar=myvar=bleach`). A variable defined this way cannot be redefined, neither by a later line nor by `##define` in the source, and the name cannot be a predefined variable or one of their values. There is no command line option for it.

**Project file (`.dsprj`) rules.**
1. Paths are relative to the folder of the `.dsprj` file.
2. Precedence, from the weakest to the strongest: default, `DFLAT_CC`, the `.dsprj`, the command line.
3. An option with several values (for example `include`) is written on repeated lines: `include=a` and then `include=b`.
4. A line that starts with `;` is a comment.
5. Keys used in the example but missing from the list of options: `name`, `include` and the file to compile (`source` or `root`).
6. A folder with several `.dsprj` files is an error, unless the compiler is given the file to use.
7. Keys are not case sensitive. Values and paths follow the rules of the operating system, and the arguments after `CL:` are passed as they are.
8. `--emit-c` can be used in a `.dsprj`; `--run` and `--version` cannot.
