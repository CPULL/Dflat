**Standard library**
Version 0.0.10 2026/10/25 by CPU
- Type members live on the types: `d.year`, `d.addDays(3)`, `s.split(",")`, `c.isDigit`, `list.sort()`.

# 1 Program

| Definition | Example | Description |
|---|---|---|
| `arr<string> getArgs()` | `arr<string> args = Root.getArgs()` | Returns the command-line arguments; index 0 is the program name (as in C and Rust). Returns an empty array when there are no arguments and no program name (e.g. micro-controllers). Same object as the `args` parameter of `main` [14.1](dflat-spec.md#141-entry-point) |
| `exit(int code)` | `Root.exit(2)` | Ends the program at once with the exit code. The console output is flushed; the `atEnd` blocks and the destructors of the objects still alive do not run (as C `exit` and Rust `process::exit`) |

# 2 Console
Access to stdinput and stdoutput and stderror

| Definition | Example | Description |
|---|---|---|
| `log(any value)` | `Root.Console.log("Hello")` | Converts the object to string if needed, prints on stdout appending a newline |
| `logError(any value)` | `Root.Console.logError("Hello")` | Converts the object to string if needed, prints on stderr appending a newline |
| `print(any value)` | `Root.Console.print("Hello")` | Converts the object to string if needed, prints on stdout |
| `char? read()` | `char? c = Root.Console.read()` | Does not wait: returns the character of a key already pressed, or `null` when no key is waiting. No echo and no Enter needed. Details below |
| `string readLine()` | `string line = Root.Console.readLine()` | Reads a line from stdin and returns it, the line is not stripped of the newline character, if any. Returns "" at end of input |
| `clear()` | `Root.Console.clear()` | Clears the console and puts the cursor at the top-left corner, so the program can write a full "screen" again. Details below |

- `read()`:
  - Keys without a character (arrows, function keys) give `null`. Enter gives `'\n'` on every OS. A full input system (all the keys, key up and down) is a separate library, later.
  - When the input is not a console (redirected from a file or a pipe), `read()` returns the next character of the input, and `null` at the end of the input.
  - `readLine()` waits for Enter and echoes the text as usual, also after calls of `read()`.
- `clear()`:
  - Works the same on every OS: the runtime uses the method of the OS (ANSI sequences on Linux and macOS, the console API on Windows), so the program needs no `##if`.
  - Does nothing when the output is not a console (redirected to a file or a pipe) or on a target without a console.
  - Clears what is on the screen; whether the scrollback of the terminal is also cleared depends on the OS.

# 3 Date and Time
Mostly used to get current date and time and to measure elapsed at nanosecond scale

| Definition | Example | Description |
|---|---|---|
| `date now()` | `date d = Root.Time.now()` | Gets the current time in UTC. A `date` prints as ISO 8601, UTC, to the millisecond: `2026-10-11T14:03:22.123Z` |
| `clock clock()` | `Root.Time.clock()` | Returns a u64 monotonic counter with a resolution of 0.1 ns; `clock()` never returns 0 |
| `sleep(lapse duration)` | `Root.Time.sleep(250)` | Stops the program for at least `duration`. The OS decides the precision: the pause can be a little longer, never shorter. A zero, negative or null lapse returns at once. A number is a lapse in milliseconds [3.4](dflat-spec.md#34-lapse): `sleep(250)` is 250 ms, `sleep(0.5)` is half a millisecond |

- `date`, `time`, `clock` and `lapse` are already nullable (`type?` is a compile error on them) and a variable of these types without a value starts as null. A `clock` prints as its number, a `lapse` as its number in its own unit, and null as `null`.
- `time` [3.3](dflat-spec.md#33-date-and-time) is the time of day, millisecond resolution; parsing in section 9.
- Calendar dates and zoned date-times are classes, `CalendarDate` and `ZonedDateTime`, in an optional DateTime library: available only when imported, so programs that don't need them stay small. Import path, timezone database and parsing: to be defined with the library. There is no local date-time type: a generic zone covers wall-clock values.
- `date - date` and `clock - clock` give a `lapse`. Two values of the same type can be compared; a null date or clock sorts first.

# 4 Math
Standard mathematical functions. `Root.Math` operates on `r64` while `Root.Math32` operates on `r32` (similar to `Mathf` of C#)

| Name | Definition | Description |
|---|---|---|
| `sin` | `r64 sin(r64 v)` | Computes the trigonometric sine of an angle in radians. |
| `cos` | `r64 cos(r64 v)` | Computes the trigonometric cosine of an angle in radians. |
| `tan` | `r64 tan(r64 v)` | Computes the trigonometric tangent of an angle in radians. |
| `asin` | `r64 asin(r64 v)` | Computes the arcsine (inverse sine) in radians, returning a value between $-\pi/2$ and $\pi/2$. |
| `acos` | `r64 acos(r64 v)` | Computes the arccosine (inverse cosine) in radians, returning a value between $0$ and $\pi$. |
| `atan` | `r64 atan(r64 v)` | Computes the arctangent (inverse tangent) in radians, returning a value between $-\pi/2$ and $\pi/2$. |
| `atan2` | `r64 atan2(r64 y, r64 x)` | Computes the 4-quadrant arctangent of $y / x$ using the signs of both arguments to determine the correct quadrant. |
| `sinh` | `r64 sinh(r64 v)` | Computes the hyperbolic sine of a value. |
| `cosh` | `r64 cosh(r64 v)` | Computes the hyperbolic cosine of a value. |
| `tanh` | `r64 tanh(r64 v)` | Computes the hyperbolic tangent of a value. |
| `sqrt` | `r64 sqrt(r64 v)` | Computes the non-negative square root of a value. |
| `cbrt` | `r64 cbrt(r64 v)` | Computes the cube root of a value. |
| `pow` | `r64 pow(r64 v, r64 e)` | Raises a base value to a specified exponent power. |
| `exp` | `r64 exp(r64 v)` | Computes $e$ raised to the power of the given value ($e^v$). |
| `ln` | `r64 ln(r64 v)` | Computes the natural logarithm (base $e$) of a value. |
| `log10` | `r64 log10(r64 v)` | Computes the common logarithm (base 10) of a value. |
| `log(x, base)` | `r64 log(r64 x, r64 base)` | Computes the logarithm of $x$ with respect to a custom base. |
| `abs` | `r64 abs(r64 v)` | Returns the absolute (non-negative) value of a number. |
| `sign` | `r64 sign(r64 v)` | Returns the sign of a number ($1$ for positive, $-1$ for negative, $0$ for zero). |
| `min` | `r64 min(r64 v...)` | Returns the smallest of two or more values (at least two); NaN if any is NaN. |
| `max` | `r64 max(r64 v...)` | Returns the largest of two or more values (at least two); NaN if any is NaN. |
| `clamp` | `r64 clamp(r64 v, r64 min, r64 max)` | Constrains a value to lie between the specified minimum and maximum bounds; NaN stays NaN; `min` greater than `max` is undefined. |
| `floor` | `r64 floor(r64 v)` | Rounds a value down to the largest integer less than or equal to the number. |
| `ceil` | `r64 ceil(r64 v)` | Rounds a value up to the smallest integer greater than or equal to the number. |
| `round` | `r64 round(r64 v)` | Rounds a value to the nearest integer, halves away from zero. |
| `trunc` | `r64 trunc(r64 v)` | Truncates the fractional part of a number, rounding toward zero. |
| `hypot` | `r64 hypot(r64 v...)` | Computes the square root of the sum of squares of its arguments (Euclidean distance function). |
| `fmod` | `r64 fmod(r64 v, r64 d)` | Computes the floating-point remainder of division. |
| `lerp(a, b, t)` | `r64 lerp(r64 a, r64 b, r64 t)` | Performs linear interpolation between $a$ and $b$ using parameter $t$ ($0.0$ to $1.0$). |
| `inverseLerp(a, b, v)` | `r64 inverseLerp(r64 a, r64 b, r64 v)` | Finds the interpolation factor $t$ for value $v$ between range $a$ and $b$. |
| `PI` | `r64 PI` | Mathematical constant Pi ($\pi \approx 3.14159$). |
| `PI_HALF` | `r64 PI_HALF` | Mathematical constant Pi divided by two ($\pi/2 \approx 1.57079$). |
| `PI2` | `r64 PI2` | Mathematical constant Pi multiplied by two ($\pi*2 \approx 6.28318$). |
| `E` | `r64 E` | Mathematical constant Euler's number ($e \approx 2.71828$). |
| `SQRT2` | `r64 SQRT2` | Mathematical constant square root of two ($\sqrt{2} \approx 1.41421$). |
| `R2D` | `r64 R2D` | Conversion factor from radians to degrees ($180 / \pi$). |
| `D2R` | `r64 D2R` | Conversion factor from degrees to radians ($\pi / 180$). |

- Math functions never raise exceptions: `sqrt(-1)` is NaN, `ln(0)` is `-∞`.
- `abs`, `sign`, `min`, `max` and `clamp` also exist for every integer type (`abs` and `sign` for signed ones only) and return that type; the argument types pick the version (13.2 rules). `Math32` has the real versions only.

# 5 Random
A simple random number generator is included in the standard library. The algorithm is fixed (xoshiro256\*\*, seeded with splitmix64), so the same seed gives the same sequence on every target. Without a call to `seed` it starts from the current time.

| Name | Definition | Description |
|---|---|---|
| `seed` | `Root.Random.seed(u64)` | Initialize the seed of the generator with the provided value |
| `int` | `i64 int(i64 min, i64 max)` | Returns a random integer between the minimum number and the maximum number (both included, no bias); the two are swapped if `min` is greater than `max` |
| `realRange` | `r64 realRange(r64 min, r64 max)` | Returns a random real number between the minimum number and the maximum number (max excluded) |
| `real` | `r64 real()` | Returns a real value in the range [0, 1] (1 is included) |
| `shuffle` | `shuffle(collection)` | Randomize the order of elements in an ordered collection. It is valid for arrays, vectors, lists, strings |

---

# 6 IO - Files, Directories and Streams

The `File` manager class and the `Stream` base class with `FileStream` are specified. Memory, console and text streams, network streams and the async syntax are not [6.9](#69-open-items).

## 6.1 Scope and general decisions

- `Root.IO` holds the basics of file system access (the equivalent of C# `System.IO`): files, directories, paths and streams. In the examples `alias Root.IO as fs` is used.
- `File` is the file manager class: it works at the level of a file manager (like Norton Commander) on files and directories. `Stream` accesses the contents of a file.
- All streams are binary. Helpers (UTF converters) transform them to text.
- `Stream` is the base class. The stream types (`FileStream`, `MemoryStream`, `ConsoleStream`) are subclasses, because one object cannot be a file, a memory and a console stream at the same time. `TextStream` is a trait that can be applied to any stream to add the text methods (6.8).
- Memory streams are available on every target. Other parts of `Root.IO` can be unavailable on specific targets.
- `Root.Console` is a special case: a shortcut to read and write. The console (standard input, standard output) is also reachable as streams (to be defined).
- Big errors raise exceptions, but not every failure raises one, to keep async code manageable: many cases use silent exceptions (see [15.4](dflat-spec.md#154-silent-exceptions) and [6.6](#66-exceptions)).

## 6.2 Paths

- Paths are normalized: `/` is the root and the separator on every OS, and a drive letter is the first folder: `/d:/folder/subfolder/file.txt`.
- Paths written in the form of any OS are accepted and normalized (following C#). `.` and `..` are accepted as generic paths, as everywhere.
- A path with illegal characters raises `InvalidPathException` (not silent) when the `File` is created.
- `File.combinePaths(path1, path2)` joins exactly two paths and returns a `string`.
- Drive letters on other OS and network paths: later.

Functions of `Root.IO` for paths and the current directory:

| Definition | Description |
|---|---|
| `File getCurrentDir()` | The current directory of the program, as a `File` |
| `setCurrentDir(File directory)`, `setCurrentDir(string path)` | Changes the current directory of the program; relative paths are resolved from it. A missing directory raises `FileNotFoundException`, a path that is a file raises `InvalidPathException`, a directory that cannot be entered raises `InvalidFileAccessException` |
| `string getFullPath(string path)` | The absolute normalized path: a relative path is resolved from the current directory, `.` and `..` are removed. The item does not need to exist |
| `string getRelativePath(string from, string to)` | The path that goes from the directory `from` to `to` (`../lib/x.df`), both made absolute first; `.` when they are the same; the absolute `to` when the two are on different drives |

## 6.3 The `File` class

`File(string path)` creates a `File` object that refers to a file or a directory. It opens nothing and does not check the path: a path that does not exist is valid, and its `exists()` returns `false`.

A folder listing returns a list of single `File` objects; `isDirectory` tells whether an item is a file or a directory.

### 6.3.1 Properties (read-only)

| Property | Type | Description |
|---|---|---|
| `path` | `string` | Normalized path |
| `name` | `string` | Name of the file or directory |
| `extension` | `string` | Extension of the name, without the dot |
| `parent` | `File` | Containing directory |
| `isDirectory` | `bool` | True for a directory |
| `isFile` | `bool` | True for a file |
| `size` | `uns` | Size in bytes |
| `created` | `date` | Creation date |
| `modified` | `date` | Last modification date |
| `accessed` | `date` | Last access date |
| `isReadOnly` | `bool` | True if the item is read-only (the only attribute in the standard library) |

- If the item cannot be accessed, `size`, `created`, `modified`, `accessed` and `isReadOnly` raise the silent `FileAccessException` and give `0`, a null `date` and `false`.
- For an item that does not exist they give the default values.

### 6.3.2 Static methods

| Definition | Description |
|---|---|
| `Root.IO.ExistResult exists(string path)` | Returns if the file exists or not, and if you can check for it |
| `bool delete(string path, fs.DeleteOptions options = fs.DeleteOptions.None)` | Deletes a file or a directory; true if it was deleted, false if it was not |
| `File copy(string from, string to, fs.OverwriteOptions options = fs.OverwriteOptions.None)` | Copies a file or a directory to the new location and returns the `File` at the new location |
| `File move(string from, string to, fs.OverwriteOptions options = fs.OverwriteOptions.None)` | Moves a file or a directory to the new location and returns the `File` at the new location |
| `File createDirectory(string path)` | Creates the directory and returns it |
| `arr<File> getAllFiles(string path, bool recursive = false, fs.FileSystemObjectsType itemType = fs.FileSystemObjectsType.Files, string match = "*")` | Lists the items of a directory; `path` has no default and an empty path raises `InvalidPathException` |
| `File getTempFile()` | A `File` for a temporary file |
| `string getTempFileName()` | The fully qualified name and path of a temporary file; it does not create the file |
| `string getTempDir()` | The path of the temporary directory |
| `string combinePaths(string path1, string path2)` | Joins two paths |
| `FileStream open(string path, fs.StreamReadWriteAccessMode readMode, fs.StreamReadWriteAccessMode writeMode, fs.StreamOpenMode openMode)` | Shortcut for `FileStream(path, readMode, writeMode, openMode)` [6.8.3](#683-constructor-and-modes) |

### 6.3.3 Object methods

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
| `FileStream open(fs.StreamReadWriteAccessMode readMode, fs.StreamReadWriteAccessMode writeMode, fs.StreamOpenMode openMode)` | Shortcut for `FileStream(path, readMode, writeMode, openMode)` on this file [6.8.3](#683-constructor-and-modes) |

`readAll`, `readAllText`, `writeAll` and `writeAllText` exist only on the `File` object, not as static methods. They are a simplification: they ignore sharing and read only. Streams [6.8](#68-streams) give the finer control.

## 6.4 Enums

All the enums are in `Root.IO`.

| Enum | Values |
|---|---|
| `fs.DeleteOptions` | bitfield [3.1.1](dflat-spec.md#311-bitfields) with `Recursive` and `ForceDelete`; `None` and `All` are built in; flags combine with `\|` (`Recursive \| ForceDelete`) |
| `fs.OverwriteOptions` | `None`, `Overwrite` (used by `copy`, `copyTo`, `move` and `moveTo`) |
| `fs.FileSystemObjectsType` | `Files`, `Dirs`, `Both` |
| `fs.FileWriteMode` | `CreateOnly`, `ReplaceOnly`, `CreateReplace`, `AppendOnly`, `CreateAppend` (used by `writeAll` and `writeAllText`) |
| `fs.StreamReadWriteAccessMode` | `None`, `Exclusive`, `Shared` (used for both `readMode` and `writeMode` of a stream) |
| `fs.StreamOpenMode` | `OpenExisting`, `CreateOrOpen`, `CreateIfMissing`, `CreateOrTruncate`, `TruncateExisting`, `CreateOrAppend`, `AppendExisting` |
| `fs.StreamSeekOrigin` | `Begin`, `CurrentPosition`, `End` |
| `fs.ExistResult` | `None`, `IsThere`, `YouCantAccesIt`, `CannottellYou` |

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

Stream access modes (`fs.StreamReadWriteAccessMode`): `None` means the stream does not read (or does not write). `Exclusive` means the stream takes exclusive access for that operation. `Shared` means other streams can use the file for that operation too (exact meaning to be confirmed, [6.9](#69-open-items)).

## 6.5 Behavior

- `delete`: a directory that is not empty is deleted only with the `Recursive` flag; without it the silent `DirectoryNotEmptyException` is raised and `delete` returns `false`. A missing item returns `false`. `ForceDelete` tries its best to delete an item that is locked for some reason (for example used by another `File` object); the result depends on the OS.
- `copy`, `move`: a directory is always copied or moved with everything inside it. If the target directory already exists, the contents are merged (as pasting a folder over a folder on Windows). With `Overwrite` files of the same name are replaced; with `None` those files are skipped and the silent `FileNameAlreadyExistsException` is raised. When the file itself is skipped, `copy` and `move` return nothing (a null `File`). A missing source raises `FileNotFoundException` (not silent).
- `createDirectory`: it never creates missing parent folders; a missing parent raises `InvalidPathException`. If the directory already exists, the silent `FileNameAlreadyExistsException` is raised and the existing directory is returned as a `File`, so the code goes on without failing (a `catch` can still handle the case).
- `getAllFiles`: returns an `arr<File>` in no defined order. A path that does not exist raises `FileNotFoundException`, not an empty result. `recursive` includes the subdirectories; `itemType` selects files, directories or both.
- `match`: only the wildcards `*` and `?` are special; everything else is static text; case is ignored. It applies to the names of files only. Regular expressions: delayed until 0.1.0.
- `readAll`, `readAllText`: a missing file raises `FileNotFoundException` (not silent).
- `writeAll`, `writeAllText`: the write mode decides what happens with an existing or a missing file (table in 6.4). A missing parent folder raises `FullPathNotFoundException` (not silent).
- Temporary files: nothing is deleted automatically (the OS usually handles temporary files); whether `getTempFile` creates the file on disk depends on the OS. Use `atEnd` when the file has to be removed, for example `File tempFile = File.getTempFile() atEnd of tempFile { tempFile.delete() }` [14.2](dflat-spec.md#142-atend).

## 6.6 Exceptions

The IDs continue the table in [15.3](dflat-spec.md#153-standard-exceptions). For the stream exceptions (33 to 45) whether they are silent is still to be confirmed case by case; for now they are not silent.

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

## 6.7 Examples

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

## 6.8 Streams

Status: the base class and `FileStream` are defined. Memory, console and text streams, network streams and the async syntax are not.

### 6.8.1 Stream Classes

- `Stream` is the base class. It offers only the basics: close, flush, seek (if supported), read and write.
- `FileStream : Stream` accesses the contents of a file. `MemoryStream` and `ConsoleStream` are other subclasses (later).
- `TextStream` is a trait applicable to any stream; it adds the specific text methods (later).
- All streams are binary; helpers convert them to text.
- Sync and async are both supported. The async version is real async: a callback is called when the operation completes. Its syntax is defined in the rest of the language specification (not for now).
- Sources considered: files, memory, console (standard input and output). Network (sockets, websockets) later: websockets are message based, so they may need a different shape.

### 6.8.2 Opening and closing

- There is no `open` in the base class: the constructor of each stream type opens the stream, as in `FileStream stream = FileStream(path, readMode, writeMode, openMode)`.
- `File.open(readMode, writeMode, openMode)` is a shortcut for `FileStream(path, readMode, writeMode, openMode)`.
- If the constructor fails it returns `null` (objects are nullable by default) and raises the exception that describes the problem (6.6).
- A stream follows the Rust memory model: it is closed when its owner is lost. `stream is atEnd.` [12.4](dflat-spec.md#124-moving-fields-and-finalizing-variables) or `close()` closes it earlier; the stream is then marked as closed, so a stream is never closed twice and a second `close()` does nothing.
- A closed stream cannot be reopened. Using it raises `AccessToAClosedStreamException`.
- Example: `FileStream stream = FileStream(path, readMode, writeMode, openMode)`; the stream is closed at the end of the scope, or earlier with `stream is atEnd.` or `stream.close()`.

### 6.8.3 Constructor and modes

`FileStream(string path, fs.StreamReadWriteAccessMode readMode, fs.StreamReadWriteAccessMode writeMode, fs.StreamOpenMode openMode)`

- `readMode`, `writeMode`: `None`, `Exclusive` or `Shared` [6.4](#64-enums). They are separate parameters of the same enum type.
- `openMode`: what to do when the file is missing or already exists (table in [6.4](#64-enums)).
- The three modes are shared by all the stream types and defined once with the base class. Every other constructor parameter belongs to the stream type: `path` exists only in `FileStream`.
- The modes are exposed as read-only constants of the object, with the same name of the parameter: `readMode`, `writeMode`, `openMode`. They cannot be changed once the stream is open.
- Invalid combinations (write off with `CreateOrTruncate`, `TruncateExisting`, `CreateOrAppend` or `AppendExisting`; read and write both off) raise `InvalidStreamModesException`.

### 6.8.4 Base class members

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

`uns` and `int` are the target-dependent unsigned and signed sizes (64 bits on x64, 32 on x32, 16 on 8-bit machines). For now they are used everywhere; before 0.1.0 they will be split by use [6.9](#69-open-items). Async versions of `read` and `write` exist (for example `readAsync`); names and callback syntax come with the language async syntax.

### 6.8.5 Behavior of read, write, seek and flush

- `read`, `write`: the buffer can be a whole array (it is used from the beginning for its full length) or a view of it, such as `(&)buffer[10..20]`. `start` and `length` select a part and are relative to the buffer passed: if a view is passed, the view is the buffer. Example: `read((&)buffer[10..20], start: 2, length: 4)` fills the elements 12 to 15 of `buffer`.
- `readAtLeast`: the minimum number of bytes to read before returning. The default `0` means do not wait: `read` returns what is available now, possibly nothing.
- `writeAtLeast`: the minimum number of bytes to write before returning. The default `0` means write what you can, with no limitation; the returned value tells how much was written.
- A timeout of a read raises a silent exception and `read` returns `0`.
- `seek` to a position before the start raises the silent `SeekBeforeBeginException` and the position becomes `0`, which is returned. `seek` to a position past the end is allowed, as in C# and Rust: reads return the end of the stream and a write extends the file.
- `flush()` pushes the library buffers to the OS, as in C# and Rust. `flush(forcePhysical: true)` also asks the OS to write the data to the physical device (C# `Flush(true)`, Rust `sync_all`).

## 6.9 Open items

- Memory streams, console as streams (standard input and output), text streams (`TextStream` trait), network streams: later
- Async: names and callback syntax for `read` and `write` (language async syntax): not for now
- Streams: exact meaning of `Exclusive` and `Shared` (for example, with an exclusive write can others still read?); other invalid mode combinations (for example creating a file with the write mode `None`)
- Streams: silent or not for each stream exception (for now all not silent); exceptions of the memory and text streams
- Before 0.1.0: split `int` and `uns` by use (calculation, memory offsets, file offsets); parser and compiler support for `uns` in normal code (not in `dfoot`)
- Before 0.1.0: regular expressions in `match`
- Normalization details: drive letters on other OS, network paths
- The full list of exceptions of the File manager and their final names

# 7 Running programs

`Root.ExecProcess` runs other programs (executables) and reads the environment of the program.

| Definition | Description |
|---|---|
| `RunResult run(string program, arr<string> args = [], string workDir = "", string input = "", lapse timeout = null)` | Starts the program, writes `input` to its standard input, waits for it to end and captures its standard output and standard error |
| `int exec(string program, arr<string> args = [], string workDir = "")` | Starts the program on the same console (its input and output are the ones of this program, nothing is captured), waits for it to end and returns its exit code |
| `string getEnv(string name)` | The value of an environment variable; `""` when it does not exist. On Windows the name is not case sensitive |

`RunResult` (a class, `Root.ExecProcess.RunResult`), read-only fields:

| Field | Type | Description |
|---|---|---|
| `exitCode` | `int` | The exit code of the program; `-1` when it did not start or was stopped by the timeout |
| `output` | `string` | What the program wrote on its standard output |
| `errors` | `string` | What the program wrote on its standard error |
| `timedOut` | `bool` | True when the timeout stopped the program |
| `error` | `Exception` | Why the program could not start; `null` when it started |

- `program` is a name or a path. A name without a folder is searched in the folders of the `PATH` environment variable; on Windows `.exe` is added when the name has no extension.
- The arguments are passed one by one, exactly as written: there is no shell, so spaces, quotes, `*` and `$` in an argument are not interpreted. A program that needs a shell runs it: `run("sh", ["-c", "ls *.df"])`, `run("cmd", ["/c", "dir"])`.
- `workDir` is the current directory of the program; `""` is the current directory of this program.
- `output`, `errors` and `input` are UTF-8 text. Text in other encodings is taken as it comes: invalid UTF-8 is not an error, it can only show wrong characters. Most of the outputs are ASCII.
- A non-zero exit code is not an error: it is only `exitCode`. On Linux and macOS a program ended by a signal gives `128 +` the signal number (as the shells do).
- `timeout`: `null` waits without limit. When the time is over the program is killed, `timedOut` is true, and `output` and `errors` keep what was written until then. A number is a lapse in milliseconds: `timeout: 5000`.
- Failures to start: `run` never raises; it puts the exception in `error` (`FileNotFoundException` when the program is not found, `InvalidFileAccessException` when it cannot be run, `FullPathNotFoundException` when `workDir` does not exist, `GenericIOErrorException` for the rest). `exec` raises the same exceptions, not silent.
- Both functions wait for the end of the program, so it never outlives the call.

## 7.1 Later

- A `Process` object that starts a program and goes on without waiting, with `stdin`, `stdout` and `stderr` as streams (it needs the console and pipe streams, 6.8), `isRunning`, `exitCode`, `wait(timeout)`, `kill()`.
- When a `Process` object ends while its program still runs, the named parameter `endBehavior` of its constructor decides what happens: `enum Root.ExecProcess.ProcessEndBehavior { Wait, Kill, Detach }`, `Kill` by default. `Wait` waits for the program to end, `Kill` stops it, `Detach` leaves it running on its own.
- An environment for the started program (a parameter of `run` and `exec`); it needs `map`.
- `tryGetVar(name)`, if a program needs to tell a missing variable from an empty one.

---