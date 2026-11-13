using dfparser;

namespace dfcompiler;

// Names qualified by their namespace (spec 16, 1.6.1). Runs on the files before they are merged into one program:
// - a top-level declaration without a keyword is seen only by its own namespace, `internal` also by the sub-namespaces,
//   `public` by everyone (unqualified only where its namespace is imported, or from a sub-namespace);
// - an unqualified name is looked for in the own namespace, then in the enclosing ones (nearest first), then in the imported
//   ones; the own namespace wins, the same name in two imported namespaces asks for the qualified name;
// - a fully qualified name (a.b.c.Dog, a.b.c.make(), through an alias of the namespace too) needs no import;
// - a name declared in more than one namespace gets a hidden name of its own (Dog_ns_a_b_c), so the merged program has no clash;
//   the messages and the printing show the real name (SourceMap.ShowNames, SourceMap.PrintName).
sealed partial class SourceLoader {
  enum NsVis { Namespace, Internal, Public }

  sealed class NsDecl {
    public NsDecl(string name, string ns, NsVis vis, Node node) {
      Name = name;
      Ns = ns;
      Vis = vis;
      Node = node;
      Internal = name;
    }

    public string Name { get; }
    public string Ns { get; }
    public NsVis Vis { get; }
    public Node Node { get; }
    public string Internal { get; set; }
    public string Shown => Ns == "" ? Name : Ns + "." + Name;
  }

  readonly Dictionary<string, List<NsDecl>> nsDecls = new();
  readonly HashSet<string> knownNamespaces = new();
  readonly Dictionary<string, string> nsAliases = new(StringComparer.OrdinalIgnoreCase);   // alias name -> namespace (lower case)
  readonly Dictionary<string, List<Node>> typeDecls = new();
  readonly HashSet<Node> resolvedNodes = new();                                // class and trait nodes by name, for the members

  static readonly HashSet<string> BindingKinds = new() { "Decl", "Param", "Var", "Catch", "Is", "Arm", "Bind", "Target", "Field", "FieldGroup" };

  // ---------------------------------------------------------------- qualified names that bring in their namespace

  void LoadQualified(bool multiFile) {
    for (bool added = true; added;) {
      added = false;
      var loaded = order.Select(file => file.Namespace).ToHashSet();
      var aliases = CollectAliases();
      foreach (var file in order.ToList()) {
        foreach (var (segments, line) in QualifiedCandidates(file.Tree)) {
          var path = ExpandAlias(segments, aliases);
          for (int k = path.Count - 1; k >= 1; k--) {
            string ns = string.Join(".", path.Take(k)).ToLowerInvariant();
            if (ns == "root" || ns.StartsWith("root.") || loaded.Contains(ns) || !roots.Any(root => root.Namespace != "root" && (root.Namespace == "" || ns == root.Namespace || ns.StartsWith(root.Namespace + ".")))) {
              continue;
            }
            var files = Resolve(ns, line, multiFile, quiet: true);
            if (files == null) {
              continue;
            }
            foreach (var found in files) {
              Visit(found, multiFile);
            }
            loaded.Add(ns);
            added = true;
            break;
          }
        }
      }
    }
  }

  Dictionary<string, string> CollectAliases() {
    var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var file in order) {
      foreach (var alias in file.Tree.Children.Where(statement => statement.Kind == "Alias")) {
        if (alias.Text != null && alias.Children.Count == 1 && alias.Children[0] is { Kind: "Type", Children.Count: 0, Text: string target }) {
          aliases[alias.Text] = target;
        }
      }
    }
    return aliases;
  }

  static List<string> ExpandAlias(List<string> segments, Dictionary<string, string> aliases) {
    if (segments.Count > 1 && aliases.TryGetValue(segments[0], out var target)) {
      return target.Split('.').Concat(segments.Skip(1)).ToList();
    }
    return segments;
  }

  // the dotted names of a tree: types (a.b.Dog) and chains of members on a name (a.b.make)
  static IEnumerable<(List<string> Segments, int Line)> QualifiedCandidates(Node node) {
    if (node.Kind == "Type" && node.Text != null && node.Text.Contains('.')) {
      yield return (node.Text.Split('.').ToList(), node.Line);
    }
    if (node.Kind == "Member" && ChainSegments(node) is { } chain) {
      yield return (chain.Select(part => part.Text!).ToList(), node.Line);
    }
    foreach (var child in node.Children) {
      foreach (var found in QualifiedCandidates(child)) {
        yield return found;
      }
    }
  }

  // a.b.c as Member(c, Member(b, Name a)): the nodes from the name outwards, or null when the chain does not start with a name
  static List<Node>? ChainSegments(Node member) {
    var parts = new List<Node>();
    var node = member;
    while (node.Kind == "Member" && node.Children.Count == 1) {
      parts.Add(node);
      node = node.Children[0];
    }
    if (node.Kind != "Name" || node.Text == null) {
      return null;
    }
    parts.Add(node);
    parts.Reverse();
    return parts;
  }

  // ---------------------------------------------------------------- the declarations

  void ResolveNames() {
    foreach (var file in order) {
      knownNamespaces.Add(file.Namespace);
      SourceMap.SetNamespace(file.Index, file.Namespace);
    }
    foreach (var (alias, target) in CollectAliases()) {
      if (knownNamespaces.Contains(target.ToLowerInvariant())) {
        nsAliases[alias] = target.ToLowerInvariant();
      }
    }
    foreach (var file in order) {
      foreach (var statement in file.Tree.Children) {
        if (TopLevelName(statement) is string name) {
          if (!nsDecls.TryGetValue(name, out var list)) {
            nsDecls[name] = list = new List<NsDecl>();
          }
          list.Add(new NsDecl(name, file.Namespace, VisibilityOf(statement), statement));
        }
        if (statement.Kind is "Class" or "Trait" && statement.Text != null) {
          if (!typeDecls.TryGetValue(statement.Text, out var types)) {
            typeDecls[statement.Text] = types = new List<Node>();
          }
          types.Add(statement);
        }
      }
    }
    // a name declared in more than one namespace: each one but the one of the program without a namespace gets its own name
    foreach (var list in nsDecls.Values) {
      if (list.Select(decl => decl.Ns).Distinct().Count() < 2) {
        continue;
      }
      foreach (var decl in list.Where(decl => decl.Ns != "")) {
        decl.Internal = decl.Name + "_ns_" + decl.Ns.Replace('.', '_');
        SourceMap.AddName(decl.Internal, decl.Shown, decl.Name);
      }
    }
    foreach (var decl in nsDecls.Values.SelectMany(list => list)) {
      SourceMap.AddDecl(decl.Internal, decl.Ns);
    }
    CheckImports();
    foreach (var file in order) {
      foreach (var statement in file.Tree.Children.ToList()) {
        if (statement.Kind is "Namespace" or "NamespaceLocal" or "Import") {
          continue;
        }
        if (statement.Kind == "Alias" && statement.Text != null && nsAliases.ContainsKey(statement.Text)) {
          file.Tree.Children.Remove(statement);   // an alias of a namespace is only for the names written with it
          continue;
        }
        var scope = new NameScope(file, statement, null);
        if (file == rootFile && TopLevelName(statement) == null && statement.Kind is not ("Alias" or "Class" or "Trait" or "Struct" or "Enum" or "GenericSet")) {
          scope.Bound.UnionWith(RootBindings());   // the top-level code of the root file: its variables
        }
        Walk(statement, scope);
        Rename(statement);
      }
    }
  }

  // every import names a namespace or one visible declaration of a namespace (spec 16.2); a namespace and a declaration of its parent
  // namespace cannot have the same name, so a path is never both
  void CheckImports() {
    foreach (string known in knownNamespaces.Where(known => known.Contains('.'))) {
      int dot = known.LastIndexOf('.');
      string parent = known[..dot];
      string last = known[(dot + 1)..];
      var clash = nsDecls.Where(pair => string.Equals(pair.Key, last, StringComparison.OrdinalIgnoreCase)).SelectMany(pair => pair.Value).FirstOrDefault(decl => decl.Ns == parent);
      if (clash != null) {
        throw new CompileException($"'{clash.Name}' has the name of the namespace {known}: a declaration and a namespace inside the same namespace need different names (spec 16.2)", clash.Node.Line);
      }
    }
    foreach (var file in order) {
      foreach (var import in file.Tree.Children.Where(statement => statement.Kind == "Import" && statement.Text != null)) {
        string path = import.Text!.ToLowerInvariant();
        if (path == "root" || path.StartsWith("root.") || knownNamespaces.Contains(path)) {
          continue;   // the stdlib is checked by its own imports (StdSingleImports)
        }
        int dot = path.LastIndexOf('.');
        string parent = dot < 0 ? "" : path[..dot];
        string name = dot < 0 ? path : import.Text![(dot + 1)..];
        var decl = dot < 0 || !knownNamespaces.Contains(parent) || !nsDecls.TryGetValue(name, out var list) ? null : list.FirstOrDefault(d => d.Ns == parent);
        if (decl == null) {
          throw new CompileException($"cannot find '{import.Text}': an import names a namespace or one declaration of a namespace (spec 16.2)", import.Line);
        }
        bool visible = decl.Vis == NsVis.Public || decl.Vis != NsVis.Namespace && Encloses(decl.Ns, file.Namespace) || decl.Ns == file.Namespace;
        if (!visible) {
          throw new CompileException($"'{decl.Name}' of {decl.Ns} is not public: it cannot be imported (spec 16.1, 16.2)", import.Line);
        }
      }
    }
  }

  // the variables of the top-level code of the root file
  HashSet<string> RootBindings() {
    var names = new HashSet<string>();
    foreach (var statement in rootFile!.Tree.Children) {
      if (TopLevelName(statement) == null && statement.Kind is not ("Alias" or "Class" or "Trait" or "Struct" or "Enum" or "GenericSet" or "Fun")) {
        CollectBindings(statement, names);
      }
    }
    return names;
  }

  static string? TopLevelName(Node statement) {
    if (statement.Text == null) {
      return null;
    }
    if (statement.Kind is "Class" or "Trait" or "Enum" or "Struct" or "GenericSet") {
      return statement.Text;
    }
    if (statement.Kind == "Fun" && statement.Text != "main") {
      return statement.Text;
    }
    if (statement.Kind == "Decl") {
      var mods = statement.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ') ?? Array.Empty<string>();
      return mods.Contains("const") && !mods.Contains("global") ? statement.Text : null;
    }
    return null;
  }

  static NsVis VisibilityOf(Node statement) {
    var mods = statement.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ') ?? Array.Empty<string>();
    return mods.Contains("public") || mods.Contains("unrestricted") ? NsVis.Public : mods.Contains("internal") ? NsVis.Internal : NsVis.Namespace;
  }

  // the hidden names of a declaration: the class itself, its constructors and the chains to itself
  void Rename(Node statement) {
    if (TopLevelName(statement) is not string name || !nsDecls.TryGetValue(name, out var list)) {
      return;
    }
    var decl = list.FirstOrDefault(d => d.Node == statement);
    if (decl == null || decl.Internal == decl.Name) {
      return;
    }
    statement.Text = decl.Internal;
    if (statement.Kind is "Class" or "Trait" or "Struct") {
      RenameConstructors(statement, name, decl.Internal);
    }
  }

  static void RenameConstructors(Node node, string name, string internalName) {
    foreach (var child in node.Children) {
      if (child.Kind is "Class" or "Trait" or "Struct") {
        continue;
      }
      if (child.Kind == "Constructor" && child.Text == name) {
        child.Text = internalName;
      }
      if (child.Kind == "Chain") {
        foreach (var call in child.Children.Where(call => call.Kind == "Call" && call.Text == name)) {
          call.Text = internalName;
        }
      }
      RenameConstructors(child, name, internalName);
    }
  }

  // ---------------------------------------------------------------- the scopes

  sealed class NameScope {
    public NameScope(SourceFile file, Node owner, NameScope? outer) {
      File = file;
      Outer = outer;
      Owner = owner;
    }

    public SourceFile File { get; }
    public NameScope? Outer { get; }
    public Node Owner { get; }
    public HashSet<string> Bound { get; } = new();

    public bool IsBound(string name) => Bound.Contains(name) || Outer != null && Outer.IsBound(name);
  }

  static void CollectBindings(Node node, HashSet<string> names) {
    if (BindingKinds.Contains(node.Kind) && node.Text != null) {
      names.Add(node.Text);
    }
    foreach (var child in node.Children) {
      if (child.Kind is "Class" or "Trait") {
        continue;
      }
      CollectBindings(child, names);
    }
  }

  // the members of a class or trait and of its parents and traits (by name: a member hides a top-level name inside the class)
  void CollectMembers(Node type, HashSet<string> names, HashSet<Node> seen) {
    if (!seen.Add(type)) {
      return;
    }
    foreach (var child in type.Children) {
      if (child.Kind == "GenericParams") {
        names.UnionWith(child.Children.Where(param => param.Text != null).Select(param => param.Text!));
      }
      if (child.Kind == "PrimaryCtor") {
        names.UnionWith(child.Children.Where(param => param.Kind == "Param" && param.Text != null).Select(param => param.Text!));
      }
      if (child.Kind == "Block") {
        foreach (var member in child.Children) {
          if (member.Text != null && member.Kind is "Decl" or "Fun" or "FieldGroup" or "GenericSet" or "Enum" or "Struct") {
            names.Add(member.Text);
          }
          if (member.Kind == "FieldGroup") {
            names.UnionWith(member.Children.Where(field => field.Text != null).Select(field => field.Text!));
          }
        }
      }
      if (child.Kind == "Bases") {
        foreach (var baseType in child.Children.Where(b => b.Text != null)) {
          string simple = baseType.Text!.Split('.').Last();
          if (typeDecls.TryGetValue(simple, out var parents)) {
            foreach (var parent in parents) {
              CollectMembers(parent, names, seen);
            }
          }
        }
      }
    }
  }

  // ---------------------------------------------------------------- the walk

  void Walk(Node node, NameScope scope) {
    switch (node.Kind) {
      case "Class" or "Trait" or "Struct": {
        var inner = new NameScope(scope.File, node, scope);
        CollectMembers(node, inner.Bound, new HashSet<Node>());
        if (node.Kind == "Struct") {
          inner.Bound.UnionWith(node.Children.Where(field => field.Text != null && field.Kind is "Field" or "Fun").Select(field => field.Text!));
        }
        foreach (var child in node.Children) {
          Walk(child, inner);
        }
        return;
      }
      case "Fun" or "Constructor" or "Lambda" or "Getter" or "Setter": {
        var inner = new NameScope(scope.File, node, scope);
        CollectBindings(node, inner.Bound);
        foreach (var child in node.Children) {
          if (child.Kind == "GenericParams") {
            inner.Bound.UnionWith(child.Children.Where(param => param.Text != null).Select(param => param.Text!));
          }
        }
        if (node.Kind is "Getter" or "Setter") {
          inner.Bound.Add("field");
          inner.Bound.Add("value");
        }
        foreach (var child in node.Children) {
          Walk(child, inner);
        }
        return;
      }
      case "Type":
        ResolveType(node, scope);
        break;
      case "Name":
        if (node.Text != null && !resolvedNodes.Contains(node) && !scope.IsBound(node.Text)) {
          if (nsDecls.ContainsKey(node.Text)) {
            node.Text = Unqualified(node.Text, scope, node.Line).Internal;
          } else {
            StdlibName(node, scope);
          }
        }
        return;
      case "Member":
        if (ResolveChain(node, scope)) {
          return;
        }
        break;
      case "Decl":
        TypedConstruction(node, scope);
        break;
      case "Chain":
        foreach (var call in node.Children.Where(call => call.Kind == "Call" && call.Text != null && !scope.IsBound(call.Text) && nsDecls.ContainsKey(call.Text))) {
          call.Text = Unqualified(call.Text!, scope, call.Line).Internal;
        }
        break;
    }
    foreach (var child in node.Children) {
      Walk(child, scope);
    }
  }

  void ResolveType(Node type, NameScope scope) {
    string? text = type.Text;
    if (text == null || text.StartsWith("Root.", StringComparison.OrdinalIgnoreCase)) {
      return;
    }
    if (text.Contains('.')) {
      var segments = text.Split('.').ToList();
      if (Qualified(segments, scope, type.Line) is { } found) {
        type.Text = found.Internal;
      } else if (!nsAliases.ContainsKey(segments[0])) {
        StdlibType(type, scope);
      }
      return;
    }
    if (!scope.IsBound(text) && !nsDecls.ContainsKey(text)) {
      StdlibType(type, scope);
      return;
    }
    if (!scope.IsBound(text) && nsDecls.ContainsKey(text)) {
      type.Text = Unqualified(text, scope, type.Line).Internal;
    }
  }

  // ---------------------------------------------------------------- the standard library (spec 16.1)

  // namespace of the stdlib (lower case) -> the names directly inside it -> the namespace as written (Root.Console)
  static readonly Dictionary<string, Dictionary<string, string>> StdChildren = BuildStdChildren();

  static Dictionary<string, Dictionary<string, string>> BuildStdChildren() {
    var children = new Dictionary<string, Dictionary<string, string>>();
    foreach (string path in CodeGen.StdlibPaths()) {
      var segments = path.Split('.');
      for (int i = 1; i < segments.Length; i++) {
        string ns = string.Join(".", segments.Take(i));
        if (!children.TryGetValue(ns.ToLowerInvariant(), out var names)) {
          children[ns.ToLowerInvariant()] = names = new Dictionary<string, string>();
        }
        names[segments[i]] = ns;
      }
    }
    return children;
  }

  // the namespaces of the stdlib; Console, Math, Random, ExecProcess ... are classes of Root, so importing them gives one name (spec 16.2)
  static readonly HashSet<string> StdNamespaces = new() { "root", "root.io" };

  // the stdlib namespaces a file imports: import Root, import Root.IO ...
  static List<string> StdImports(SourceFile file) =>
    file.Tree.Children.Where(statement => statement.Kind == "Import" && statement.Text != null && StdNamespaces.Contains(statement.Text.ToLowerInvariant()))
      .Select(statement => statement.Text!.ToLowerInvariant()).Distinct().ToList();

  // the single names of the stdlib a file imports (import Root.Console): the name and the namespace that declares it
  static List<(string Name, string Ns)> StdSingleImports(SourceFile file) {
    var found = new List<(string, string)>();
    foreach (var statement in file.Tree.Children.Where(statement => statement.Kind == "Import" && statement.Text != null)) {
      string path = statement.Text!;
      if (!path.StartsWith("Root.", StringComparison.OrdinalIgnoreCase) || StdNamespaces.Contains(path.ToLowerInvariant())) {
        continue;
      }
      int dot = path.LastIndexOf('.');
      string parent = path[..dot].ToLowerInvariant();
      string name = path[(dot + 1)..];
      if (!StdNamespaces.Contains(parent) || !StdChildren.TryGetValue(parent, out var names) || !names.TryGetValue(name, out var written)) {
        throw new CompileException($"'{path}' is not a namespace or a name of the standard library: import Root, Root.IO, or one name of them (Root.Console, Root.IO.File)", statement.Line);
      }
      found.Add((name, written));
    }
    return found;
  }

  // the stdlib namespace a bare name comes from in this file, or null; without the import, a name of the stdlib is an error
  string? StdlibNamespace(string name, NameScope scope, int line) {
    if (nsAliases.ContainsKey(name) || name == "Root") {
      return null;
    }
    var found = StdImports(scope.File).Where(ns => StdChildren[ns].ContainsKey(name)).Select(ns => StdChildren[ns][name])
      .Concat(StdSingleImports(scope.File).Where(single => single.Name == name).Select(single => single.Ns)).Distinct().ToList();
    if (found.Count > 1) {
      throw new CompileException($"'{name}' is in the imported namespaces {string.Join(" and ", found)}: write which one ({string.Join(" or ", found.Select(ns => ns + "." + name))})", line);
    }
    if (found.Count == 1) {
      return found[0];
    }
    // not imported: the full name is needed (the types of Root.IO and Root.ExecProcess were bare before spec 16.1)
    foreach (var (ns, names) in StdChildren) {
      if (names.TryGetValue(name, out var written) && (ns == "root" || char.IsUpper(name[0]))) {
        throw new CompileException($"'{name}' is in the standard library: write {written}.{name} or import {written}", line);
      }
    }
    return null;
  }

  // Console.log with import Root becomes Root.Console.log; File with import Root.IO stays File (the types of the libraries are known bare)
  void StdlibName(Node name, NameScope scope) {
    if (StdlibNamespace(name.Text!, scope, name.Line) is not string ns) {
      return;
    }
    if (ns is "Root.IO" or "Root.ExecProcess" && char.IsUpper(name.Text![0])) {
      return;
    }
    var token = new Token(TokenKind.Ident, name.Text!, name.Line, 0);
    Node inner = new Node("Name", token, "Root");
    foreach (string segment in ns.Split('.').Skip(1)) {
      inner = new Node("Member", token, segment).Add(inner);
    }
    name.Kind = "Member";
    name.Children.Clear();
    name.Children.Add(inner);
  }

  void StdlibType(Node type, NameScope scope) {
    string first = type.Text!.Split('.')[0];
    if (scope.IsBound(first) || nsDecls.ContainsKey(first) || StdlibNamespace(first, scope, type.Line) is not string ns) {
      return;
    }
    if (ns is "Root.IO" or "Root.ExecProcess" && char.IsUpper(first[0])) {
      return;
    }
    type.Text = ns + "." + type.Text;
  }

  // a.b.c.make(3), a.b.c.Color.Red, cp.Dog(): the longest start that is a namespace followed by one of its names becomes that name
  bool ResolveChain(Node member, NameScope scope) {
    var parts = ChainSegments(member);
    if (parts == null || scope.IsBound(parts[0].Text!)) {
      return false;
    }
    var segments = parts.Select(part => part.Text!).ToList();
    for (int k = segments.Count - 1; k >= 1; k--) {
      if (Qualified(segments.Take(k + 1).ToList(), scope, member.Line, quiet: true) is { } found) {
        var target = parts[k];
        target.Kind = "Name";
        target.Text = found.Internal;
        target.Children.Clear();
        return true;
      }
    }
    return false;
  }

  // Dog d = Dog() with Dog written a.b.c.Dog: the bare constructor takes the namespace of the declared type (spec 16)
  void TypedConstruction(Node decl, NameScope scope) {
    var type = decl.Children.FirstOrDefault(child => child.Kind == "Type");
    var init = decl.Children.FirstOrDefault(child => child.Kind == "Init");
    if (type?.Text == null || !type.Text.Contains('.') || init == null || init.Children.Count != 1) {
      return;
    }
    var segments = type.Text.Split('.').ToList();
    if (Qualified(segments, scope, type.Line, quiet: true) is not { } found) {
      return;
    }
    var expr = init.Children[0];
    while (expr.Kind is "CountedOf" or "Gift" or "Copy" && expr.Children.Count == 1) {
      expr = expr.Children[0];
    }
    if (expr.Kind == "Call" && expr.Children.Count > 0 && expr.Children[0] is { Kind: "Name" } callee && callee.Text == found.Name && !scope.IsBound(found.Name)) {
      callee.Text = found.Internal;
      resolvedNodes.Add(callee);   // the walk leaves it as it is
    }
  }

  // ---------------------------------------------------------------- the rules

  static bool Encloses(string outer, string inner) => outer == "" || inner.StartsWith(outer + ".");

  HashSet<string> ImportsOf(SourceFile file) =>
    file.Tree.Children.Where(statement => statement.Kind == "Import" && statement.Text != null).Select(statement => statement.Text!.ToLowerInvariant()).ToHashSet();

  NsDecl Unqualified(string name, NameScope scope, int line) {
    var list = nsDecls[name];
    string here = scope.File.Namespace;
    var own = list.FirstOrDefault(decl => decl.Ns == here);
    if (own != null) {
      return own;
    }
    // the enclosing namespaces, nearest first
    var enclosing = list.Where(decl => decl.Vis != NsVis.Namespace && Encloses(decl.Ns, here)).OrderByDescending(decl => decl.Ns.Length).FirstOrDefault();
    if (enclosing != null) {
      return enclosing;
    }
    var imports = ImportsOf(scope.File);
    var imported = list.Where(decl => decl.Vis == NsVis.Public && (imports.Contains(decl.Ns) || imports.Contains(decl.Ns + "." + name.ToLowerInvariant()))).ToList();
    var namespaces = imported.Select(decl => decl.Ns).Distinct().ToList();
    if (namespaces.Count > 1) {
      throw new CompileException($"'{name}' is declared in the imported namespaces {string.Join(" and ", namespaces.Select(ns => $"'{ns}'"))}: "
        + $"write which one ({string.Join(" or ", namespaces.Select(ns => ns + "." + name))})", line);
    }
    if (imported.Count > 0) {
      return imported[0];
    }
    throw new CompileException(NotVisible(list[0], here, imports.Contains(list[0].Ns), qualified: false), line);
  }

  // a.b.c.Dog: the namespace (or an alias of it) and the name; null when the start is not a namespace of the program
  NsDecl? Qualified(List<string> segments, NameScope scope, int line, bool quiet = false) {
    if (segments.Count < 2) {
      return null;
    }
    var path = segments.Take(segments.Count - 1).ToList();
    string ns = nsAliases.TryGetValue(path[0], out var aliased) ? string.Join(".", new[] { aliased }.Concat(path.Skip(1))) : string.Join(".", path);
    ns = ns.ToLowerInvariant();
    string name = segments[^1];
    if (!knownNamespaces.Contains(ns) || !nsDecls.TryGetValue(name, out var list)) {
      return null;
    }
    var decl = list.FirstOrDefault(d => d.Ns == ns);
    if (decl == null) {
      if (quiet) {
        return null;
      }
      throw new CompileException($"the namespace '{ns}' has no '{name}'", line);
    }
    string here = scope.File.Namespace;
    bool visible = decl.Ns == here || decl.Vis == NsVis.Public || decl.Vis == NsVis.Internal && Encloses(decl.Ns, here);
    if (!visible) {
      throw new CompileException(NotVisible(decl, here, true, qualified: true), line);
    }
    return decl;
  }

  static string NotVisible(NsDecl decl, string here, bool imported, bool qualified) {
    string where = here == "" ? "the program" : $"the namespace '{here}'";
    string what = $"'{decl.Name}' of the namespace '{decl.Ns}'";
    return decl.Vis switch {
      NsVis.Namespace => $"{what} has no visibility keyword: only its own namespace sees it (mark it internal or public to use it from {where})",
      NsVis.Internal => $"{what} is internal: only its namespace and the sub-namespaces see it (mark it public to use it from {where})",
      _ => qualified || imported ? $"{what} is not visible from {where}"
        : $"{what} is public: import {decl.Ns} or write {decl.Ns}.{decl.Name}",
    };
  }
}
