using dfparser;

namespace dfcompiler;

// Lends (spec 12, 13.1.1): a local that borrows an object, a string or an array keeps what it borrows from locked until its last use, and a
// plain result lends only from what outlives the function (this, a parameter, static data). Checked inside one function, by statement order.
public sealed partial class CodeGen {
  // every declaration of a block and the statements that follow it in the same block
  readonly Dictionary<Node, List<Node>> followingStatements = new();

  void RecordFollowingStatements(List<Node> statements) {
    for (int index = 0; index < statements.Count; index++) {
      if (statements[index].Kind is "Decl" or "Assign") {
        followingStatements[statements[index]] = statements.Skip(index + 1).ToList();
      }
      if (statements[index].Kind == "MultiAssign") {
        // bool here, Dog dog = m.tryGet(k): each new variable locks what it borrows from, as a declaration does
        foreach (var target in statements[index].Children[0].Children.Where(target => target.Kind == "Decl")) {
          followingStatements[target] = statements.Skip(index + 1).ToList();
        }
      }
    }
  }

  // a.b.c for a name or a chain of fields; null for anything else
  static string? LendPath(Node node) => node.Kind switch {
    "Name" => node.Text,
    "Member" when node.Children.Count > 0 && LendPath(node.Children[0]) is string root => root + "." + node.Text,
    "Paren" => LendPath(node.Children[0]),
    _ => null
  };

  // what a borrowed value comes from: the place itself, the collection of an item, or the receiver and the lent arguments of a call
  List<string> LenderPaths(Node value, Scope scope) {
    var paths = new List<string>();
    switch (value.Kind) {
      case "Name" or "Member" or "Paren":
        if (LendPath(value) is string path) {
          paths.Add(path);
        }
        break;
      case "Index":
        paths.AddRange(LenderPaths(value.Children[0], scope));
        break;
      case "Call":
        if (value.Children[0] is { Kind: "Member" } callee) {
          paths.AddRange(LenderPaths(callee.Children[0], scope));
        }
        if (value.Children.Count > 1) {
          foreach (var raw in value.Children[1].Children) {
            var arg = raw.Kind == "Named" && raw.Children.Count > 0 ? raw.Children[^1] : raw;
            if (arg.Kind is "Name" or "Member" && LendPath(arg) is string argPath && PlaceType(arg, scope) is string argType
                && (IsClass(argType) || argType == "string" || IsArr(argType))) {
              paths.Add(argPath);
            }
          }
        }
        break;
    }
    return paths.Distinct().ToList();
  }

  // the value of a declaration is borrowed (a place, or a call with a plain result), not a new one
  bool DeclBorrows(Node init, string type, Scope scope) => init.Kind switch {
    "Name" or "Member" or "Index" => true,
    "Call" => !IsNewResult(init, type, scope),
    _ => false
  };

  // Dog d = k.leader(): until the last use of d, k (and k.boss) cannot be replaced, released, moved or handed out with & (spec 12). Strings
  // keep their identity when changed in place and a borrow sees the change (spec 4.1, 4.4): they are not locked here
  void CheckBorrowLock(Node decl, string type, Node? init, VarInfo variable, Scope scope) {
    if (LibraryCode || init == null || !(IsClass(type) || type == "string" || IsArr(type)) || !DeclBorrows(init, type, scope)) {
      return;
    }
    var lenders = LenderPaths(init, scope);
    if (lenders.Select(path => path.Split('.')[0]).FirstOrDefault(root => LocalLender(root, scope)) is string local) {
      variable.LentFromLocal = scope.Find(local)?.LentFromLocal ?? local;
    }
    if (type == "string" || lenders.Count == 0 || !followingStatements.TryGetValue(decl, out var after)) {
      return;
    }
    // Dog f = pack[0]: replacing an item of pack ends the object f borrows; a view or an array item of plain values is not ended (spec 6.4)
    bool items = init.Kind == "Index" && !(init.Children.Count > 1 && init.Children[1].Kind == "Range") && IsClass(type);
    LockLenders(decl, after, lenders, items, IsClass(type), scope);   // the calls that can change a lender (spec 13.1.1)
  }

  // until the last use of the borrowing variable, its lenders stay as they are (spec 12): not assigned, ended, moved, passed as &, their
  // fields not written; when an item of a collection is borrowed (Dog d = pack[0], m.tryGet(k)) also no member that changes the collection,
  // which could end the item (calls on other objects are not checked yet, spec 99)
  void LockLenders(Node decl, List<Node> after, List<string> lenders, bool items, bool objects, Scope scope) {
    int last = -1;
    for (int index = 0; index < after.Count; index++) {
      if (Mentions(after[index], decl.Text!)) {
        last = index;
      }
    }
    for (int index = 0; index < last; index++) {
      if (FindRelease(after[index], lenders, items) is (Node at, string path)) {
        throw Error($"'{path}' is lent to '{decl.Text}' until its last use: until then it and its fields cannot be assigned, ended, moved or passed as & (spec 12)", at);
      }
      if (objects && FindChangingCall(after[index], lenders, items, scope) is (Node call, string changed, string member)) {
        throw Error($"'{changed}' is lent to '{decl.Text}' until its last use: until then only its unrestricted methods can be called, not '{member}' (spec 13.1.1)", call);
      }
    }
  }

  // Dog d = k.leader(): a local that borrows an object is read-only, also when the source lends read/write; &Dog d = ... borrows read/write
  // and needs a source that lends read/write (spec 13.1.1)
  void SetBorrowAccess(Node decl, string type, Node? init, VarInfo variable, Scope scope) {
    if (LibraryCode || init == null || !IsClass(type) || variable.Own || variable.IsCounted || variable.IsWeak || !DeclBorrows(init, type, scope)) {
      if (decl.Children.Any(child => child.Kind == "ReadWrite") && !variable.IsCounted && !variable.IsWeak) {
        throw Error($"&{DisplayName(type)} {decl.Text}: a read/write borrow needs an object that is lent (a place or a &T result), not a new value", decl);
      }
      return;
    }
    bool readWrite = decl.Children.Any(child => child.Kind == "ReadWrite");
    if (readWrite && ReadOnlySource(init, scope) is string why) {
      throw Error($"&{DisplayName(type)} {decl.Text}: {why}; write {DisplayName(type)} {decl.Text} (spec 13.1.1)", decl);
    }
    variable.ReadOnlyRef = !readWrite;
  }

  // why a source cannot be borrowed read/write, or null when it can: a &T result, or a place the code can write
  string? ReadOnlySource(Node init, Scope scope) {
    // a lend from a read/write counted object only while an enforce holds its lock (spec 13.1.1, 12.7)
    var lender = init.Kind == "Call" && init.Children[0] is { Kind: "Member" } lendCall ? lendCall.Children[0] : init;
    while (lender.Kind is "Member" or "Index" or "Paren" && lender.Children.Count > 0 && CountedRef(lender, scope) == null) {
      lender = lender.Children[0];
    }
    if (!LibraryCode && CountedRef(lender, scope) is CountedRefInfo counted && IsWritableRef(counted) && !heldLocks.Contains(counted.Code)) {
      return $"a lend from the &@{counted.Type} '{counted.Name}' is valid only inside enforce {counted.Name} {{ }}, which holds its lock";
    }
    if (init.Kind == "Call") {
      return CalleesOf(init) is { Count: > 0 } callees && callees.All(f => f.Writes.Contains(0)) ? null : "this value is lent read-only";
    }
    var root = init;
    while (root.Kind is "Member" or "Index" or "Paren" && root.Children.Count > 0) {
      root = root.Children[0];
    }
    if (root is { Kind: "Name", Text: "this" } || root.Kind == "Name" && scope.Find(root.Text!) == null && currentClass != null && FindField(currentClass, root.Text!) != null) {
      return InUnrestrictedMethod ? "an unrestricted method does not change its object" : null;
    }
    if (root.Kind == "Name" && scope.Find(root.Text!) is VarInfo held) {
      if (held.ReadOnlyRef || held.IsParam && !held.RefSlot && !held.Own) {
        return $"'{root.Text}' is read-only here";
      }
      return null;
    }
    return "this is a value, not a place that can be lent read/write";
  }

  // bool here, Dog dog = m.tryGet(k): a new variable of several results that receives a lent object borrows it from the receiver of the call
  void CheckLentTarget(Node target, Node call, VarInfo variable, Scope scope) {
    if (LibraryCode || !followingStatements.TryGetValue(target, out var after)) {
      return;
    }
    var lenders = LenderPaths(call, scope);
    if (lenders.Select(path => path.Split('.')[0]).FirstOrDefault(root => LocalLender(root, scope)) is string local) {
      variable.LentFromLocal = scope.Find(local)?.LentFromLocal ?? local;
    }
    if (lenders.Count > 0) {
      LockLenders(target, after, lenders, true, true, scope);   // tryGet lends an item of the map
    }
  }

  // the first call of the statement that can change one of the lenders: lender.member(...) where member is not unrestricted (a class) or
  // changes the collection (spec 1.6.2: the members of the collections that do not change them)
  (Node, string, string)? FindChangingCall(Node node, List<string> lenders, bool items, Scope scope) {
    if (node.Kind is "Fun" or "Lambda") {
      return null;
    }
    if (node is { Kind: "Call" } && node.Children[0] is { Kind: "Member" } callee && LendPath(callee.Children[0]) is string path
        && lenders.FirstOrDefault(lender => path == lender || path.StartsWith(lender + ".")) is string lender && callee.Text is string member) {
      string? type = null;
      try {
        type = PlaceType(callee.Children[0], scope);
      } catch (CompileException) {
      }
      bool collection = type != null && (IsArr(type) || IsVec(type) || IsList(type) || IsMap(type) || IsSet(type) || IsView(type));
      // a collection: while one of its items is borrowed, its changing members; another object: every method that is not unrestricted
      bool changes = type != null && (collection
        ? items && !ReadOnlyCollectionMembers.Contains(member) && !member.StartsWith("get_")
        : IsClass(type) && LookupMethods(ObjInfo(type), member).Any(f => !f.IsUnrestricted && !f.IsStatic));
      if (changes) {
        return (node, lender, member);
      }
    }
    foreach (var child in node.Children) {
      if (FindChangingCall(child, lenders, items, scope) is { } found) {
        return found;
      }
    }
    return null;
  }

  // the first place of the statement that replaces, releases, moves or hands out one of the lenders (or what holds it), or writes one of its
  // fields: while a borrow is alive its lender is frozen, as in Rust (spec 12)
  static (Node, string)? FindRelease(Node node, List<string> lenders, bool items) {
    if (node.Kind is "Fun" or "Lambda") {
      return null;
    }
    string? Touched(Node target) {
      var place = target.Kind == "Index" ? target.Children[0] : target;
      if (LendPath(place) is not string path) {
        return null;
      }
      // k.boss = x, k.dogs[0] = x, k.count++ while something borrows from k: a field of the lender is written
      if (lenders.FirstOrDefault(lender => path.StartsWith(lender + ".")) is string owner) {
        return owner;
      }
      if (target.Kind == "Index" && !items) {
        return null;   // pack[1] = x while a view of pack is alive: allowed (spec 6.4)
      }
      return lenders.FirstOrDefault(lender => lender == path || lender.StartsWith(path + "."));
    }
    switch (node.Kind) {
      case "Assign" when node.Children.Count > 0 && Touched(node.Children[0]) is string assigned:
        return (node, assigned);
      case "Finalize" or "Gift" or "WritableAddressOf" or "PreInc" or "PreDec" or "PostInc" or "PostDec"
        when node.Children.Count > 0 && Touched(node.Children[0]) is string released:
        return (node, released);
      case "MultiAssign" when node.Children[0].Children.FirstOrDefault(target => target.Kind != "Decl" && Touched(target) != null) is Node multi:
        return (node, Touched(multi)!);
    }
    foreach (var child in node.Children) {
      if (FindRelease(child, lenders, items) is (Node, string) found) {
        return found;
      }
    }
    return null;
  }

  // fun() f = (; &a) { ... }: while the lambda lives the outer code cannot use a, until the last use of f or of a local that shares the
  // lambda (fun() g = f) (spec 13.3.4). Checked inside one block, by statement order, as the lends above
  void CheckCaptureLock(Node decl, Node? init) {
    if (LibraryCode || init is not { Kind: "Lambda" } lambda || !followingStatements.TryGetValue(decl, out var after)) {
      return;
    }
    var captured = lambda.Children.FirstOrDefault(child => child.Kind == "Captures")?.Children
      .Where(capture => capture.Kind == "WritableCapture").Select(capture => capture.Text!).ToList() ?? new List<string>();
    if (captured.Count == 0) {
      return;
    }
    var holders = new HashSet<string> { decl.Text! };
    int last = -1;
    for (int index = 0; index < after.Count; index++) {
      if (after[index] is { Kind: "Decl" } shared && shared.Children.FirstOrDefault(child => child.Kind == "Init")?.Children[0] is { Kind: "Name" } source
          && holders.Contains(source.Text!)) {
        holders.Add(shared.Text!);   // fun() g = f: the same lambda
      }
      if (holders.Any(holder => Mentions(after[index], holder))) {
        last = index;
      }
    }
    for (int index = 0; index < last; index++) {
      if (captured.FirstOrDefault(name => UsesName(after[index], name)) is string used) {
        throw Error($"'{used}' is captured with & by the lambda '{decl.Text}': it cannot be used until the last use of the lambda (spec 13.3.4)", after[index]);
      }
    }
  }

  // the name is read, written or captured by a lambda in the statement
  static bool UsesName(Node node, string name) =>
    node.Kind is "Name" or "WritableCapture" or "CopyCapture" or "MoveCapture" or "CountedCapture" or "WeakCapture" && node.Text == name
    || node.Children.Any(child => UsesName(child, name));

  // a local of the function (not a parameter, not this, not a global) that owns its value, or borrows from such a local
  bool LocalLender(string root, Scope scope) =>
    root != "this" && scope.Find(root) is VarInfo held && held.IsLocal
    && (held.Own || held.OwnsArray || held.OwnFlag != null && !held.ReadOnlyString || held.LentFromLocal != null);

  // return x with a plain result: it lends, so x must outlive the function (spec 13.1.1)
  void CheckLentResult(Node value, string type, Scope scope) {
    if (LibraryCode || !(IsClass(type) || type == "string" || IsArr(type))) {
      return;
    }
    var paths = value.Kind == "Call" ? LenderPaths(value, scope)
      : LendPath(value.Kind == "Index" ? value.Children[0] : value) is string path ? new List<string> { path } : new List<string>();
    if (paths.Select(lender => lender.Split('.')[0]).FirstOrDefault(root => LocalLender(root, scope)) is string root) {
      string local = scope.Find(root)?.LentFromLocal ?? root;
      throw Error($"this result lends from '{local}', which ends with the function: give the caller its own value with fun ->{DisplayName(type)} (spec 13.1.1)", value);
    }
  }

  // return x with a &T result: the caller writes x in place, so x is something this function can write: this and its fields, a & parameter,
  // or the &T result of another call; never a global (spec 13.1.1, 18.3)
  void CheckWritableLend(Node value, string type, Scope scope) {
    if (LibraryCode) {
      return;
    }
    if (InUnrestrictedMethod) {
      throw Error("an unrestricted method does not change its object: it cannot lend it read/write (a & result) (spec 1.6.2)", value);
    }
    // return g.field: a part of a global (its read may have become a copy, LowerGlobalAccesses)
    var lent = value.Kind == "Copy" ? value.Children[0] : value;
    if (LendPath(lent.Kind == "Index" ? lent.Children[0] : lent) is string globalPath
        && GlobalNamed(Synthetic("Name", value, globalPath.Split('.')[0]), scope) is GlobalInfo lentGlobal) {
      throw Error($"a & result cannot lend global '{lentGlobal.Name}': return a copy, or give the value with -> (spec 18.3)", value);
    }
    if (value.Kind == "Call") {
      if (CalleesOf(value) is { Count: > 0 } callees && callees.All(f => f.Writes.Contains(0))) {
        return;
      }
      throw Error("a & result lends what the function can write: this result is lent read-only by the call; return the place, or give a value with ->", value);
    }
    var place = value.Kind == "Index" ? value.Children[0] : value;
    if (LendPath(place) is not string path) {
      throw Error("a & result lends a place the function can write: this, a field or a & parameter", value);
    }
    string root = path.Split('.')[0];
    if (root != "this" && GlobalNamed(Synthetic("Name", value, root), scope) is GlobalInfo global) {
      throw Error($"a & result cannot lend global '{global.Name}': return a copy, or give the value with -> (spec 18.3)", value);
    }
    if (root != "this" && scope.Find(root) is VarInfo held) {
      if (held.IsConst) {
        throw Error($"'{root}' is const: a & result cannot lend it (spec 13.1.1)", value);
      }
      if (held.IsParam && !held.RefSlot) {
        throw Error($"'{root}' is lent read-only to this function: a & result lends only what the function can write (this, its fields, a & parameter) (spec 13.1.1)", value);
      }
    }
  }
}
