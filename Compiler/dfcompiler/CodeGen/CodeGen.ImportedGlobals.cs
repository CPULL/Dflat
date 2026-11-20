using System.Text;
using dfparser;

namespace dfcompiler;

// The public globals and variables of a compiled library (spec 15.8, 16, 18.1), from its .dfi: C variables of the library, declared extern.
// A variable is read and written by the program as a variable of a library (a new value ends the old one); the library creates and ends it.
// A global is the library's object: the program calls its methods through the exported symbols, which take the lock of the global in the
// library, so the program and the library use the same lock. A field of the global read or written by the program would not take it: not
// supported yet.
public sealed partial class CodeGen {
  readonly Dictionary<string, (string CName, string Type)> importedGlobals = new();

  void DeclareImportedVars(Node program) {
    foreach (var decl in program.Children.Where(statement => statement.Kind == "Decl" && statement.Children.Any(child => child.Kind == "Imported")).ToList()) {
      string name = decl.Text!;
      string symbol = decl.Children.First(child => child.Kind == "Imported").Text!;
      string type = ResolveDeclType(decl, null);
      if (globals.ContainsKey(name) || globalConsts.ContainsKey(name) || importedGlobals.ContainsKey(name)) {
        throw Error($"'{name}' is already declared in this scope", decl);
      }
      if (IsGlobalDecl(decl)) {
        importedGlobals[name] = (symbol, type);
      } else {
        globalConsts[name] = (symbol, type);
        libraryVars.Add(name);   // written as a field: a new value ends the old one (spec 16)
      }
      classGlobals.Append($"extern {CType(type)} {symbol};   /* of a compiled library (spec 15.8) */\n");
      program.Children.Remove(decl);
    }
    if (importedGlobals.Count > 0) {
      CheckImportedGlobalUses(program, new List<Node>(), new HashSet<string>());
    }
  }

  // g.m(...) only: the method takes the lock in the library; g.f, ->g, &g, @g ... are not for a global of a library
  void CheckImportedGlobalUses(Node node, List<Node> ancestors, HashSet<string> hidden) {
    if (node.Kind is "Class" or "Struct" or "Fun" or "Lambda" or "Constructor" or "Trait") {
      hidden = new HashSet<string>(hidden);
      CollectHidingNames(node, hidden, node.Kind is "Class" or "Struct" or "Trait");
    }
    if (node.Kind == "Name" && importedGlobals.ContainsKey(node.Text!) && !hidden.Contains(node.Text!) && ancestors.Count > 0) {
      var parent = ancestors[^1];
      if (parent.Kind is "Member" or "SafeMember" && parent.Children[0] == node
          && !(ancestors.Count >= 2 && ancestors[^2] is { Kind: "Call" } call && call.Children[0] == parent)) {
        throw NotSupported($"a field of '{node.Text}', a global of a compiled library: call a method of its class (it takes the lock of the global)", parent);
      }
      if (parent.Kind is "Gift" or "WritableAddressOf" or "Counted" or "WeakOf" or "Finalize" or "Done" or "WritablePointerOf") {
        throw Error($"'{node.Text}' is a global of a library: it is never moved, replaced or ended by the program (spec 18.1)", parent);
      }
    }
    ancestors.Add(node);
    foreach (var child in node.Children) {
      CheckImportedGlobalUses(child, ancestors, hidden);
    }
    ancestors.RemoveAt(ancestors.Count - 1);
  }

  // the compiled libraries start when the program starts, before its globals, statics and code, in the order of include=; they end after
  // the program has ended, in the reverse order (spec 15.8). An error that leaves a start or an end is a panic
  // First the ids: the program numbers the classes and the exceptions of each library after its own (spec 15.8), before any library starts
  string LinkedStartCode() {
    if (LibraryBuild != null || LinkedLibraries.Count == 0) {
      return "";
    }
    var sb = new StringBuilder();
    if (LinkedLibraries.Any(library => library.Ids != null)) {
      long first = Math.Max(256L + classes.Count, customExceptionIds.Count == 0 ? 0 : customExceptionIds.Values.Max() + 1L);
      sb.Append($"  {{\n    uint64_t df_next_id = UINT64_C({first});\n");
      foreach (var library in LinkedLibraries.Where(library => library.Ids != null)) {
        sb.Append($"    {{ extern uint64_t {library.Ids}(uint64_t); df_next_id = {library.Ids}(df_next_id); }}\n");
      }
      sb.Append("  }\n");
    }
    foreach (var library in LinkedLibraries.Where(library => library.Start != null)) {
      sb.Append(LibraryCall(library.Start!.Value));
    }
    return sb.ToString();
  }

  string LinkedEndCode() => LibraryBuild != null ? "" : string.Concat(LinkedLibraries.Reverse().Where(library => library.End != null)
    .Select(library => LibraryCall(library.End!.Value)));

  static string LibraryCall((string Symbol, bool Error) call) {
    string prototype = call.Error ? $"df_exception* {call.Symbol}(void)" : $"void {call.Symbol}(void)";
    return call.Error
      ? $"  {{ extern {prototype}; df_exception* error = {call.Symbol}(); if (error) {{ df_panic_unhandled(error); }} }}\n"
      : $"  {{ extern {prototype}; {call.Symbol}(); }}\n";
  }

  // vec<string> in the stdlib list of a .dfi: the program defines the copy for the library (spec 15.8), even when it does not use it
  void LinkedGenericCopies() {
    foreach (string part in LinkedLibraries.SelectMany(library => library.Stdlib).Where(part => part.Contains('<')).Distinct()) {
      var decl = new Parser(new Lexer($"{part} df_copy", 1).Tokenize()).ParseProgram().Children[0];
      ResolveType(decl.Children.First(child => child.Kind == "Type"));
    }
  }
}
