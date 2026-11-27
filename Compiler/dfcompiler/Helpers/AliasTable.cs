using dfparser;

namespace dfcompiler;

// The aliases of the program (spec 16): an alias is seen only in the file that declares it, anywhere in that file. The file of a
// name is found from its line (SourceMap). An alias of a function type (a callback type, spec 22) is a type declaration: it is seen
// everywhere, as the other declarations, so that the program uses the callback types of an import lib.
sealed class AliasTable {
  readonly Dictionary<(int File, string Name), string> perFile = new();
  readonly Dictionary<string, string> everywhere = new();

  static int FileOf(int line) => line / SourceMap.Stride;

  public void Set(string name, string value, int line) => perFile[(FileOf(line), name)] = value;

  public void SetEverywhere(string name, string value) => everywhere[name] = value;

  public bool TryGetValue(string name, int line, out string value) {
    if (perFile.TryGetValue((FileOf(line), name), out var found) || everywhere.TryGetValue(name, out found)) {
      value = found;
      return true;
    }
    value = "";
    return false;
  }

  public bool TryGetValue(string name, Node at, out string value) => TryGetValue(name, at.Line, out value);

  public bool ContainsKey(string name, int line) => TryGetValue(name, line, out _);

  public bool ContainsKey(string name, Node at) => TryGetValue(name, at.Line, out _);
}
