using System.Text;
using dfparser;

namespace dfcompiler;

// The changes hidden in calls (spec 4, strings behave as values): which fields and top-level variables with a text a call can change, found
// by name in everything it calls; and the copies that the borrowers and the parameters of a function take before such a statement.
public sealed partial class CodeGen {
  readonly Dictionary<string, List<Node>> bodiesByName = new();   // a function, a method, a property or a class -> its bodies
  HashSet<string>? textNames;                                     // the fields and the top-level variables that can hold a text, or an object
  readonly HashSet<string> importedNames = new();                 // functions without a body here: they can change anything
  readonly HashSet<string> funTypedNames = new();                 // the names declared with a function type: a call of one is the call of a value
  readonly Dictionary<string, (HashSet<string> Writes, bool Unknown)> callEffects = new();
  readonly HashSet<(FunctionContext Fun, string Name)> paramKeeps = new();

  static readonly HashSet<string> TextlessTypes = new() {
    "i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64", "int", "uns", "r32", "r64", "real", "bool", "char", "lapse", "date", "time", "clock"
  };

  void CollectBodies() {
    if (textNames != null) {
      return;
    }
    textNames = new HashSet<string>();
    void NoteName(Node decl) {
      if (decl.Text != null && !decl.Children.Any(c => c.Kind == "Type" && c.Text != null && TextlessTypes.Contains(c.Text))) {
        textNames.Add(decl.Text);
      }
    }
    void Body(string name, Node body) {
      if (!bodiesByName.TryGetValue(name, out var list)) {
        bodiesByName[name] = list = new List<Node>();
      }
      list.Add(body);
    }
    void Walk(Node node, Node? holder) {
      if (node.Line >= LibraryLine) {
        return;
      }
      switch (node.Kind) {
        case "Fun":
          if (node.Text != null && node.Children.Any(c => c.Kind == "Block")) {
            Body(node.Text, node);
          }
          return;
        case "Class" or "Trait" or "Struct":
          Body(node.Text!, node);
          foreach (var child in node.Children) {
            if (child.Kind is "Param" or "Field") {
              NoteName(child);
            }
            foreach (var param in child.Kind == "Params" ? child.Children : new List<Node>()) {
              NoteName(param);
            }
            Walk(child, node);
          }
          return;
        case "Decl" or "Field" or "Global" or "Mutex":
          NoteName(node);
          if (node.Text != null && node.Children.Any(c => c.Kind == "Accessors")) {
            Body(node.Text, node);   // a property: its setter runs at an assignment
          }
          return;
        case "Block" or "FieldGroup" when holder != null:
          foreach (var child in node.Children) {
            Walk(child, holder);
          }
          return;
      }
    }
    void FunTyped(Node node) {
      if (node.Kind is "Decl" or "Param" or "Field" && node.Text != null
          && node.Children.Any(c => c.Kind == "FunType" || c.Kind == "Init" && c.Children.Count > 0 && c.Children[0].Kind == "Lambda")) {
        funTypedNames.Add(node.Text);
      }
      node.Children.ForEach(FunTyped);
    }
    foreach (var child in programNode!.Children) {
      Walk(child, null);
    }
    textNames.UnionWith(fileVars.Keys);   // the top-level variables that functions use (spec 14.1)
    FunTyped(programNode);
    foreach (var (name, overloads) in funs) {
      if (overloads.Any(f => f.IsImported)) {
        importedNames.Add(name.Split('.')[^1]);
      }
      // the accessors (get_x, set_x) and the constructors are functions by now
      foreach (var f in overloads.Where(f => !f.IsImported && f.Node.Line < LibraryLine)) {
        string last = name.Split('.')[^1];
        if (!bodiesByName.TryGetValue(last, out var list) || !list.Contains(f.Node)) {
          Body(last, f.Node);
        }
      }
    }
  }

  // what a call of this name can change, in all that it calls
  (HashSet<string> Writes, bool Unknown) CallEffect(string name) {
    if (callEffects.TryGetValue(name, out var known)) {
      return known;
    }
    var writes = new HashSet<string>();
    bool unknown = false;
    var seen = new HashSet<string>();
    var queue = new Queue<string>();
    queue.Enqueue(name);
    while (queue.Count > 0) {
      string at = queue.Dequeue();
      if (!seen.Add(at)) {
        continue;
      }
      unknown |= importedNames.Contains(at);
      if (!bodiesByName.TryGetValue(at, out var bodies)) {
        continue;
      }
      foreach (var body in bodies) {
        // a class: its initial values and its constructors
        var parts = body.Kind is "Class" or "Trait" or "Struct"
          ? body.Children.SelectMany(c => c.Kind == "Block" ? c.Children : new List<Node> { c }).Where(c => c.Kind != "Fun" || c.Text == body.Text)
          : body.Children;
        foreach (var part in parts) {
          var paths = new HashSet<string>();
          CollectStrChanges(part, paths, null, true);
          writes.UnionWith(paths.Select(path => path.Split('.')[^1]).Where(textNames!.Contains));
          unknown |= CalledNames(part, queue.Enqueue, true);
        }
      }
    }
    return callEffects[name] = (writes, unknown);
  }

  // the names a node calls, and the properties it sets; true when it calls a function value
  bool CalledNames(Node node, Action<string> called, bool blocks) {
    bool unknown = false;
    if (node.Kind == "Call") {
      if (node.Children[0] is { Kind: "Name" or "Member", Text: string callee }) {
        called(callee);
        unknown = funTypedNames.Contains(callee) && !bodiesByName.ContainsKey(callee);
      } else {
        unknown = node.Children[0].Kind is "Call" or "Index";   // f()(), m["a"]()
      }
    } else if (node.Kind is "Assign" or "PreInc" or "PreDec" or "PostInc" or "PostDec" && node.Children[0] is { Kind: "Name" or "Member", Text: string set }
        && bodiesByName.ContainsKey("set_" + set)) {
      called("set_" + set);
    } else if (node is { Kind: "Name" or "Member", Text: string read } && bodiesByName.ContainsKey("get_" + read)) {
      called("get_" + read);
    }
    foreach (var child in node.Children) {
      if (blocks || child.Kind is not ("Block" or "Case" or "Catch" or "AtEnd" or "Lambda" or "Fun" or "WaitBranch" or "TimeoutBlock")) {
        unknown |= CalledNames(child, called, blocks);
      }
    }
    return unknown;
  }

  // before a statement that changes the source of borrowed texts: the borrowers used later take their own copy
  void EmitStrDetaches(Node s, StringBuilder sb, Scope scope, string pad) {
    if (LibraryCode || frames.Count == 0 || fn == null || s.Line >= LibraryLine) {
      return;
    }
    CollectBodies();
    var borrows = CurrentStrBorrows();
    var changed = new HashSet<string>();
    CollectStrChanges(s, changed, (receiver, member) => StrReceiverChanges(receiver, member, scope));
    // D e = d: a change through e is one of d
    foreach (string path in changed.ToList()) {
      string root = path.Split('.')[0];
      if (borrows.Lenders.TryGetValue(root, out var sources) && scope.Find(root) is VarInfo aliased && aliased.Type != "string") {
        changed.UnionWith(sources.Select(source => source + path[root.Length..]));
      }
    }
    // what the calls of the statement can change
    var writes = new HashSet<string>();
    int calls = 0;
    bool unknownCall = false;
    bool unknown = CalledNames(s, name => {
      var effect = CallEffect(name);
      calls += effect.Writes.Count > 0 || effect.Unknown ? 1 : 0;
      writes.UnionWith(effect.Writes);
      unknownCall |= effect.Unknown;
    }, false);
    calls += unknown ? 1 : 0;
    unknown |= unknownCall || InTaskRunBody && (ContainsWait(s) || s.Kind is "Return" or "Enforce");   // a checkpoint serves the calls through the handle
    var receivers = new HashSet<string>();
    if (calls > 0) {
      ChangingReceivers(s, receivers);
    }
    var captured = new HashSet<string>();
    LambdaNames(s, captured);
    if (changed.Count == 0 && captured.Count == 0 && writes.Count == 0 && !unknown) {
      return;
    }
    // a place reached through another name (&string r = a, for &string k in v): every borrower
    bool any = changed.Any(path => scope.Find(path.Split('.')[0]) is VarInfo held && (held.LendPointer != null || held.RefSlot && !held.IsParam));
    // a parameter can be the text of anything that is not a variable of this function only
    bool Outside(string path) {
      var held = scope.Find(path.Split('.')[0]);
      if (held is { IsLocal: true, IsParam: false } && (held.Own || held.OwnFlag != null || held.OwnsArray || held.OwnsStruct)) {
        return false;
      }
      return held is { IsParam: true } ? held.RefSlot || held.Type != "string" : textNames!.Contains(path.Split('.')[^1]);
    }
    bool outside = any || unknown || writes.Count > 0 || changed.Any(Outside);
    bool Touched(string lender) {
      if (changed.Any(path => StrTouches(path, lender))) {
        return true;
      }
      string root = lender.Split('.')[0];
      var variable = scope.Find(root);
      if (variable is { IsParam: true, Type: "string", RefSlot: false }) {
        return outside;   // b = p
      }
      if (variable is { RefSlot: true } && outside) {
        return true;
      }
      if (receivers.Contains(root)) {
        return true;   // v[0].rename()
      }
      if (variable is { IsLocal: true, IsParam: false } && !lender.Contains('.')) {
        return false;   // a local string or collection: only this function reaches it
      }
      return unknown || lender.Split('.').Any(writes.Contains);
    }
    // a text that the statement itself reads before the change: k.label = p, rename(p) alone
    bool now = !(calls == 0 && s.Kind == "Assign" && s.Text is null or "=" && (s.Children[0].Kind is "Member" or "Index" || s.Children[0].Kind == "Name" && scope.Find(s.Children[0].Text!) == null)
      || s.Kind == "ExprStmt" && s.Children[0].Kind == "Call" && calls == 1 && changed.Count == 0
        && s.Children[0].Children[0] is { Text: string outer } && CallEffect(outer) is var outerEffect && (outerEffect.Writes.Count > 0 || outerEffect.Unknown));
    foreach (var (name, lenders) in borrows.Lenders) {
      if (scope.Find(name) is not VarInfo { Type: "string", OwnFlag: string flag } variable || variable.IsConst || variable.ReadOnlyString && !variable.IsParam) {
        continue;
      }
      bool touched = any || lenders.Any(lender => lender != name && (variable.IsParam ? outside : changed.Any(path => path != name && StrTouches(path, lender)) || Touched(lender)));
      if (touched && changed.Count == 1 && changed.Contains(name) && writes.Count == 0 && !unknown) {
        touched = false;   // b = x, b += x: b itself
      }
      if (touched && StrUsedLater(name, now) || captured.Contains(name) && lenders.Count > 0) {   // a lambda keeps the text it captures
        sb.Append($"{pad}df_str_mine(&{variable.CName}, &{flag});\n");
      }
    }
    if (currentFun == null || !outside && captured.Count == 0) {
      return;
    }
    foreach (var p in currentFun.Params) {
      if (p.Type == "string" && !p.IsGift && !borrows.Lenders.ContainsKey(p.Name) && scope.Find(p.Name) is VarInfo { IsParam: true, RefSlot: false, OwnFlag: not null } param
          && (outside && StrUsedLater(p.Name, now) || captured.Contains(p.Name) && outside)) {
        string keep = "df_keep_" + param.CName;
        if (paramKeeps.Add((fn, keep))) {
          fn.Decls.Append($"  df_string {keep} __attribute__((cleanup(df_str_keep_end))) = NULL;\n");
        }
        sb.Append($"{pad}if ({keep} == NULL) {{ {keep} = df_str_dup({param.CName}); {param.CName} = {keep}; }}\n");
      }
    }
  }

  // the collections on whose items the statement calls a method that can change a text: v[0].rename()
  void ChangingReceivers(Node node, HashSet<string> roots, bool blocks = false) {
    if (node.Kind == "Call" && node.Children[0] is { Kind: "Member", Text: string called } member && member.Children[0].Kind == "Index"
        && CallEffect(called) is var effect && (effect.Writes.Count > 0 || effect.Unknown)) {
      var root = member.Children[0];
      while (root.Kind is "Member" or "Index" or "Paren" && root.Children.Count > 0) {
        root = root.Children[0];
      }
      if (root.Kind == "Name") {
        roots.Add(root.Text!);
      }
    }
    foreach (var child in node.Children) {
      if (blocks || child.Kind is not ("Block" or "Lambda" or "Fun")) {
        ChangingReceivers(child, roots, blocks);
      }
    }
  }

  // the names used in the lambdas of a statement (not in its blocks)
  static void LambdaNames(Node node, HashSet<string> names) {
    if (node.Kind == "Lambda") {
      void All(Node inner) {
        if (inner.Kind is "Name" or "CopyCapture" or "WritableCapture" && inner.Text != null) {
          names.Add(inner.Text);
        }
        inner.Children.ForEach(All);
      }
      All(node);
      return;
    }
    if (node.Kind is not ("Block" or "Case" or "Fun")) {
      node.Children.ForEach(child => LambdaNames(child, names));
    }
  }
}
