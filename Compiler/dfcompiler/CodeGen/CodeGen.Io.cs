namespace dfcompiler;

// Root.IO.File (spec 17.6.3): written in Dflat, added to the program when it mentions File (AddPrelude), on top of the native
// functions of Root.IO.Native (CodeGen.Stdlib, RuntimeIO). The natives leave the id of a failure in the error register;
// File.check reads it after every call: it throws the exceptions that are not silent and gives back the id of a silent one.
// A silent exception reaches only the function that calls the one that raises it, so the public functions throw the silent
// ones themselves (a helper cannot throw them for its caller), and the wrappers catch and throw them again (EXCEPTIONS: THE MODEL).
// The read-only properties are the get_ functions: "f.size" calls get_size() (MemberValue).
public sealed partial class CodeGen {
  const string IoPrelude = """
public class File(string path) {
  string full = File.fixPath(path)

  public static fun string fixPath(string path) {
    string result = Root.IO.Native.Normalize(path)
    File.check(path)
    return result
  }

  // throws the exceptions that are not silent; gives back the id of a silent one (27, 28, 29, 32) or 0
  public static fun u64 check(string path) {
    u64 code = Root.IO.Native.Error()
    if code == 0 then return 0
    if code == 27 || code == 28 || code == 29 || code == 32 then return code
    if code == 25 then throw FileNotFoundException("File or folder not found: " + path)
    if code == 26 then throw InvalidFileAccessException("Access not allowed: " + path)
    if code == 30 then throw InvalidPathException("Invalid path: " + path)
    if code == 31 then throw FullPathNotFoundException("The parent folder does not exist: " + path)
    if code == 33 then throw FileAlreadyExistsException("The file already exists: " + path)
    if code == 34 then throw FileAccessToADirectoryException("The path is a folder: " + path)
    if code == 35 then throw ReadOnlyItemCannotBeWrittenException("The item is read-only: " + path)
    if code == 36 then throw ConflictingSharingException("The file is in use: " + path)
    if code == 37 then throw InvalidStreamModesException("Invalid combination of stream modes: " + path)
    if code == 38 then throw NoMoreResourcesException("No more resources (disk full or too many open files): " + path)
    if code == 45 then throw StreamCloseFailureException("Closing the stream failed: " + path)
    throw GenericIOErrorException("I/O error: " + path)
    return 0
  }

  // properties (read-only)
  public fun string get_path() { full }
  public fun string get_name() { Root.IO.Native.Name(full) }
  public fun string get_extension() { Root.IO.Native.Extension(full) }
  public fun File get_parent() {
    string up = Root.IO.Native.ParentPath(full)
    if up == "" then return null
    return File(up)
  }
  public fun bool get_isDirectory() { Root.IO.Native.Kind(full) == 2 }
  public fun bool get_isFile() { Root.IO.Native.Kind(full) == 1 }
  public fun u64 get_size() {
    u64 result = Root.IO.Native.Size(full)
    u64 silent = File.check(full)
    if silent == 27 then throw FileAccessException("Cannot access: " + full)
    return result
  }
  public fun date get_created() {
    date result = Root.IO.Native.Created(full)
    u64 silent = File.check(full)
    if silent == 27 then throw FileAccessException("Cannot access: " + full)
    return result
  }
  public fun date get_modified() {
    date result = Root.IO.Native.Modified(full)
    u64 silent = File.check(full)
    if silent == 27 then throw FileAccessException("Cannot access: " + full)
    return result
  }
  public fun date get_accessed() {
    date result = Root.IO.Native.Accessed(full)
    u64 silent = File.check(full)
    if silent == 27 then throw FileAccessException("Cannot access: " + full)
    return result
  }
  public fun bool get_isReadOnly() {
    bool result = Root.IO.Native.ReadOnly(full)
    u64 silent = File.check(full)
    if silent == 27 then throw FileAccessException("Cannot access: " + full)
    return result
  }

  // static methods
  public static fun bool exists(string path) {
    string p = File.fixPath(path)
    bool result = Root.IO.Native.Exists(p)
    File.check(p)
    return result
  }
  public static fun bool delete(string path, Root.IO.DeleteOptions options = Root.IO.DeleteOptions.None) {
    string p = File.fixPath(path)
    bool result = Root.IO.Native.Delete(p, (i32)options)
    u64 silent = File.check(p)
    if silent == 27 then throw FileAccessException("Cannot access: " + p)
    if silent == 28 then throw DirectoryNotEmptyException("The folder is not empty: " + p)
    return result
  }
  public static fun File copy(string from, string to, Root.IO.OverwriteOptions options = Root.IO.OverwriteOptions.None) {
    string source = File.fixPath(from)
    string target = File.fixPath(to)
    bool done = Root.IO.Native.Copy(source, target, options == Root.IO.OverwriteOptions.Overwrite)
    u64 silent = File.check(target)
    if silent == 29 then throw FileNameAlreadyExistsException("The name already exists: " + target)
    if done then return File(target)
    return null
  }
  public static fun File move(string from, string to, Root.IO.OverwriteOptions options = Root.IO.OverwriteOptions.None) {
    string source = File.fixPath(from)
    string target = File.fixPath(to)
    bool done = Root.IO.Native.Move(source, target, options == Root.IO.OverwriteOptions.Overwrite)
    u64 silent = File.check(target)
    if silent == 29 then throw FileNameAlreadyExistsException("The name already exists: " + target)
    if done then return File(target)
    return null
  }
  public static fun File createDirectory(string path) {
    string p = File.fixPath(path)
    Root.IO.Native.MakeDir(p)
    u64 silent = File.check(p)
    if silent == 29 then throw FileNameAlreadyExistsException("The name already exists: " + p)
    return File(p)
  }
  public static fun arr<File> getAllFiles(string path, bool recursive = false, Root.IO.FileSystemObjectsType itemType = Root.IO.FileSystemObjectsType.Files, string match = "*") {
    string p = File.fixPath(path)
    arr<string> names = Root.IO.Native.List(p, recursive, (i32)itemType, match)
    File.check(p)
    arr<File> files = arr<File>[names.len]
    for u64 index = 0..names.len {
      files[index] = File(names[index])
    }
    return files
  }
  public static fun File getTempFile() { File(Root.IO.Native.TempName()) }
  public static fun string getTempFileName() { Root.IO.Native.TempName() }
  public static fun string getTempDir() { Root.IO.Native.TempDir() }
  public static fun string combinePaths(string path1, string path2) {
    Root.IO.Native.Combine(File.fixPath(path1), File.fixPath(path2))
  }

  // Root.IO.getCurrentDir, setCurrentDir, getFullPath, getRelativePath (stdlib 6.2): the calls are sent here (LibraryStaticPath)
  public static fun File getCurrentDir() {
    string path = Root.IO.Native.CurrentDir()
    File.check(path)
    return File(path)
  }
  public static fun setCurrentDir(string path) {
    string p = File.fixPath(path)
    Root.IO.Native.SetCurrentDir(p)
    File.check(p)
  }
  public static fun setCurrentDir(File directory) {
    File.setCurrentDir(directory.path)
  }
  public static fun string getFullPath(string path) {
    string result = Root.IO.Native.FullPath(File.fixPath(path))
    File.check(path)
    return result
  }
  public static fun string getRelativePath(string from, string to) {
    string result = Root.IO.Native.RelativePath(File.fixPath(from), File.fixPath(to))
    File.check(to)
    return result
  }

  //OPEN-STATIC

  // object methods
  //OPEN-OBJECT
  public fun bool exists() { File.exists(full) }
  // the wrappers pass the silent exceptions of the static functions on, whatever their class: they catch them and throw them again
  public fun bool delete(Root.IO.DeleteOptions options = Root.IO.DeleteOptions.None) {
    return File.delete(full, options)
    catch SilentException e { throw e }
  }
  public fun File copyTo(string to, Root.IO.OverwriteOptions options = Root.IO.OverwriteOptions.None) {
    return File.copy(full, to, options)
    catch SilentException e { throw e }
  }
  public fun File moveTo(string to, Root.IO.OverwriteOptions options = Root.IO.OverwriteOptions.None) {
    return File.move(full, to, options)
    catch SilentException e { throw e }
  }
  public fun File createDirectory() {
    return File.createDirectory(full)
    catch SilentException e { throw e }
  }
  public fun arr<File> getAllFiles(bool recursive = false, Root.IO.FileSystemObjectsType itemType = Root.IO.FileSystemObjectsType.Files, string match = "*") {
    File.getAllFiles(full, recursive, itemType, match)
  }
  public fun arr<u8> readAll() {
    arr<u8> bytes = Root.IO.Native.ReadAll(full)
    File.check(full)
    return bytes
  }
  public fun string readAllText(String.StringEncoding encoding = String.StringEncoding.UTF8) {
    string text = Root.IO.Native.ReadAllText(full, (i32)encoding)
    File.check(full)
    return text
  }
  public fun writeAll(arr<u8> buffer, Root.IO.FileWriteMode mode = Root.IO.FileWriteMode.CreateReplace) {
    Root.IO.Native.WriteAll(full, buffer, (i32)mode)
    File.check(full)
  }
  public fun writeAllText(string text, String.StringEncoding encoding = String.StringEncoding.UTF8, Root.IO.FileWriteMode mode = Root.IO.FileWriteMode.CreateReplace) {
    Root.IO.Native.WriteAllText(full, text, (i32)encoding, (i32)mode)
    File.check(full)
  }
}
""";
}
