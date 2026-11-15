using dfparser;

namespace dfcompiler;

// Pointers (spec 12.1), step 1: pointer<T> / *T and pointer<&T> / &*T to numbers, bool, char, enums, structs without strings or objects,
// and other pointers. A pointer is a C struct { T* cur; T* base; int64_t count; }: the cursor, and the bounds kept for the checks
// (count < 0: unbounded). The null pointer has cur == NULL. Read-only and writable pointers share the C type.
// Not built yet: arrays and vecs (pointer.to(buff), *a[i]), the methods (add, inRange ...), casts, addr, static data (*"1.2.3"),
// bounds written in the type (*i32{count}), unsafe, pointers in fields, collections and tuples, pointers to and from C.
public sealed partial class CodeGen {
  static bool IsPointer(string t) => t.StartsWith("pointer<") && t.EndsWith('>');
  static bool IsWritablePointer(string t) => t.StartsWith("pointer<&");
  static string PointedType(string t) => IsWritablePointer(t) ? t[9..^1] : t[8..^1];
  static string PointerType(string pointed, bool writable) => $"pointer<{(writable ? "&" : "")}{pointed}>";

  // inside the type argument of pointer<...>: a pointer there is not inside a collection or a tuple
  int pointerTypeNests;

  // the variable each pointer variable points into: a pointer cannot outlive it (spec 12.1, 12)
  readonly Dictionary<VarInfo, VarInfo> pointerSources = new();

  string PointerCType(string type) {
    string pointed = PointedType(type);
    string name = "df_ptr_" + PointerMangle(pointed);
    if (definedTypes.Add(name)) {
      string elemC = CType(pointed);
      typeDefs.Append($"typedef struct {{\n  {elemC}* cur;\n  {elemC}* base;\n  int64_t count;\n  df_uns* lock;\n}} {name};\n\n");
    }
    return name;
  }

  string PointerMangle(string pointed) => IsPointer(pointed) ? "p_" + PointerMangle(PointedType(pointed)) : Mangle(pointed);

  // *T, &*T, pointer<T>, pointer<&T>: the pointed type must be a plain value
  string ResolvePointerType(Node n, Node pointedNode, bool writable, bool ownDepth) {
    if (nestedTypeDepth - (ownDepth ? 1 : 0) - pointerTypeNests > 0) {
      throw NotSupported("pointers inside collections, tuples and other generic types", n);
    }
    if (ownDepth) {
      pointerTypeNests++;
    }
    string pointed;
    try {
      pointed = ResolveType(pointedNode);
    } finally {
      if (ownDepth) {
        pointerTypeNests--;
      }
    }
    CheckPointee(pointed, n);
    return PointerType(pointed, writable);
  }

  void CheckPointee(string pointed, Node at) {
    if (pointed == "string") {
      return;   // only static data makes one: *"1.2.3" (spec 12.1)
    }
    if (IsArr(pointed) || IsVec(pointed) || IsList(pointed)) {
      throw NotSupported("pointers to collections (pointer.to(buff), *a[i], next steps of spec 12.1)", at);
    }
    if (IsNumeric(pointed) || pointed is "bool" or "char" || IsEnum(pointed) || IsPointer(pointed)) {
      return;
    }
    if (IsStruct(pointed)) {
      if (StructHoldsObjects(pointed)) {
        throw NotSupported($"a pointer to a struct that holds strings or objects ({pointed})", at);
      }
      return;
    }
    throw NotSupported($"a pointer to {DisplayName(pointed)} (spec 12.1: numbers, bool, char, enums and structs for now)", at);
  }

  // pointer p = pointer.to(x): the type comes from the value
  string InferPointerDecl(Node decl, Node? init, Scope? scope) {
    if (init == null || scope == null) {
      throw Error("'pointer' without a type argument takes its type from its value: pointer p = pointer.to(x) (spec 12.1)", decl);
    }
    string type = ExprOf(init.Children[0], scope).Type;
    if (!IsPointer(type)) {
      throw Error($"'pointer' takes its type from a pointer value, not from {DisplayName(type)} (spec 12.1)", decl);
    }
    return type;
  }

  // ---------------------------------------------------------------- making pointers

  // pointer.to(x), pointer.to(&x): null when the call is not one
  Expr? PointerToCall(Node n, Scope scope) {
    if (n.Children[0] is not { Kind: "Member", Text: "to" } member || member.Children[0] is not { Kind: "Name", Text: "pointer" } || scope.Find("pointer") != null) {
      return null;
    }
    var args = n.Children[1].Children;
    if (args.Count != 1 || args[0].Kind == "Named") {
      throw Error("pointer.to takes one value: pointer.to(x), or pointer.to(&x) for a writable pointer (spec 12.1)", n);
    }
    var arg = args[0];
    return arg.Kind == "WritableAddressOf" ? PointerOf(arg.Children[0], true, scope, n) : PointerOf(arg, false, scope, n);
  }

  // *x, &*x, pointer.to(x): a pointer to the place, bounded to one item
  Expr PointerOf(Node place, bool writable, Scope scope, Node at, string? expectedPointee = null) {
    var root = PlaceRoot(place);
    bool literal = place.Kind is "Int" or "Real" or "String" or "Char" or "Bool" || place.Kind == "Neg" && place.Children[0].Kind is "Int" or "Real";
    if (literal || root != null && scope.Find(root.Text!) is { IsConst: true } || IsGlobalConst(root, scope)) {
      if (writable) {
        throw Error("&* on a const or a literal: a const cannot be written through a pointer (spec 12.1)", at);
      }
      return StaticPointer(place, literal, scope, at, expectedPointee);   // *"1.2.3", *LIMIT: static data (spec 12.1)
    }
    if (place.Kind == "Name" && scope.Find(place.Text!) is { Type: "string" or "string?" }) {
      throw NotSupported("a pointer to a string variable (only static data: *\"text\")", at);
    }
    if (root != null && scope.Find(root.Text!) != null && PointerToCollection(place, writable, scope, at) is Expr items) {
      return items;   // pointer.to(buff), *a[i], pointer.to(v) (spec 12.1)
    }
    if (place.Kind == "Index") {
      throw NotSupported("a pointer to an item of this kind (*a[i] works on arr and vec, spec 12.1)", at);
    }
    if (root == null || scope.Find(root.Text!) == null) {
      throw Error("a pointer is made from a variable or a field of a struct variable: *x, &*x, pointer.to(s.pos) (spec 12.1)", at);
    }
    var variable = scope.Find(root.Text!)!;
    if (IsClass(variable.Type) || variable.IsCounted || variable.IsWeak) {
      throw NotSupported("a pointer to a field of an object", at);
    }
    if (writable && (variable.IsParam && !variable.CName.StartsWith("(*") || variable.ReadOnlyStruct || variable.ReadOnlyRef || variable.LoopVar)) {
      throw Error($"'{root.Text}' is read-only here: &* needs a place that can be written (spec 12.1, 13.1)", at);
    }
    // a writable pointer needs a place that can be written; a read-only one only reads it
    var target = writable ? LValue(place, scope) : ExprOf(place, scope) is var read ? new LValueInfo(read.Code, read.Type, false) : default;
    CheckPointee(target.Type, at);
    ReadCheck(variable, root.Text!, place);
    string cType = PointerCType(PointerType(target.Type, writable));
    return new Expr($"(({cType}){{ &({target.Code}), &({target.Code}), 1, NULL }})", PointerType(target.Type, writable));
  }

  // the value of a pointer variable being declared or assigned: a pointer to a vec made there counts on the vec (spec 12.1, 6.4)
  Node? lockingPointerInit;
  const string LockMark = "/*lock*/";

  // pointer.to(buff), *buff[i], pointer.to(v), *v[i]: the whole array as bounds, the cursor on the item; null when the place is no collection
  Expr? PointerToCollection(Node place, bool writable, Scope scope, Node at) {
    bool item = place.Kind == "Index";
    var collNode = item ? place.Children[0] : place;
    var indexNode = item ? place.Children[1] : null;
    if (indexNode is { Kind: "Empty" or "Range" }) {
      throw Error("a pointer to an item takes one index: *a[i] (spec 12.1)", at);
    }
    var coll = ExprOf(collNode, scope);
    if (IsList(coll.Type)) {
      throw item ? NotSupported("a pointer to the item of a list node", at)
        : Error("pointer.to on a list is a compile error: its items are not in one block; take a pointer to one item (spec 12.1)", at);
    }
    bool isArr = IsArr(coll.Type);
    if (!isArr && !IsVec(coll.Type)) {
      return null;
    }
    if (writable && ReadOnlyVariable(collNode, scope) is not null) {
      throw Error($"'{PlaceRoot(collNode)!.Text}' is read-only here: &* needs a collection that can be written (spec 12.1, 13.1)", at);
    }
    string elem = isArr ? ArrElem(coll.Type) : VecElem(coll.Type);
    if (elem is "string" or "string?") {
      throw NotSupported("a pointer to strings (spec 12.1: numbers, bool, char, enums and structs for now; *\"text\" for static data)", at);
    }
    CheckPointee(elem, at);
    string type = PointerType(elem, writable);
    string cType = PointerCType(type);
    string fnName = fn!.NameConst;
    string index = indexNode == null ? "0" : IndexValue(ExprOf(indexNode, scope, "u64"), indexNode);
    string held = NewTemp();
    string at0 = NewTemp();
    string result = NewTemp();
    string Check(string len) => indexNode == null ? "" : $"if (!df_idx_ok({len}, {at0}, {fnName})) {RaiseJump} ";
    if (isArr) {
      string arrCode = $"({{ {CType(coll.Type)} {held} = {coll.Code}; {cType} {result} = {{0}}; df_uns {at0} = {index}; {Check($"({held}.p ? {held}.len : 0)")}"
        + $"if ({held}.p) {{ {result} = ({cType}){{ {held}.p + {at0}, {held}.p, (int64_t){held}.len, NULL }}; }} {result}; }})";
      return new Expr(arrCode, type);
    }
    bool locks = lockingPointerInit == at;
    string items = $"{held}->{VecField(coll.Type, "items")}.p";
    string count = $"{held}->{VecField(coll.Type, "count")}";
    string counter = $"{held}->{VecField(coll.Type, "iteratorCounter")}";
    string vecCode = $"({{ {CType(coll.Type)} {held} = {coll.Code}; {cType} {result} = {{0}}; df_uns {at0} = {index}; "
      + $"if ({held}) {{ {Check(count)}{result} = ({cType}){{ {items} + {at0}, {items}, (int64_t){count}, {(locks ? $"&{counter}" : "NULL")} }}; {(locks ? $"{counter}++; " : "")}}} {result}; }})";
    return new Expr((locks ? LockMark : "") + vecCode, type);
  }

  // a copy of a pointer never holds the count of its vec: only the variable that made it releases it
  string PointerCopy(Expr e) {
    if (e.Code.StartsWith(LockMark) || e.Type == "null") {
      return e.Code;
    }
    string cType = CType(e.Type);
    return WithTemp(e, p => $"(({cType}){{ {p}.cur, {p}.base, {p}.count, NULL }})");
  }

  // p = q: the old value of p gives back its count
  string PointerAssign(LValueInfo target, string value) {
    string temp = NewTemp();
    return $"({{ {CType(target.Type)} {temp} = {value}; if ({target.Code}.lock) {{ (*{target.Code}.lock)--; }} {target.Code} = {temp}; }})";
  }

  // the end of a pointer variable (spec 12.1, 6.4)
  void EmitPointerDrop(Node s, System.Text.StringBuilder sb, Scope scope, string pad) {
    if (scope.Find(s.Text!) is VarInfo held && IsPointer(held.Type)) {
      sb.Append($"{pad}if ({held.CName}.lock) {{ (*{held.CName}.lock)--; }}\n");
    }
  }

  static bool IsPointerDecl(Node decl) =>
    decl.Kind == "Decl" && decl.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind)) is { } typeNode
    && (typeNode.Kind is "Pointer" or "WritablePointer" || typeNode is { Kind: "Type", Text: "pointer" });

  static Node PointerDropNode(string name, Node at) =>
    Synthetic("AtEnd", at, "drop").Add(Synthetic("Block", at).Add(Synthetic("PtrDrop", at, name)));

  bool IsGlobalConst(Node? root, Scope scope) => root != null && scope.Find(root.Text!) == null && globalConsts.ContainsKey(root.Text!);

  // the variable at the root of a place: x of x, s.pos.x, (s).pos
  static Node? PlaceRoot(Node place) {
    return place.Kind switch {
      "Name" => place,
      "Member" or "Paren" or "Index" => PlaceRoot(place.Children[0]),
      _ => null
    };
  }

  // ---------------------------------------------------------------- using pointers

  bool IsPointerNode(Node n, Scope scope) {
    try {
      return IsPointer(ExprOf(n, scope).Type);
    } catch (CompileException) {
      return false;
    }
  }

  // the address of item i (p[] is item 0), checked: NullPointerException, IndexOutOfRangeException (spec 12.1)
  string PointerItemAddress(Expr pointer, Node indexNode, Scope scope, Node at) {
    string pointed = PointedType(pointer.Type);
    string elemC = CType(pointed);
    var index = indexNode.Kind == "Empty" ? new Expr("0", "i64") : ExprOf(indexNode, scope, "i64");
    if (!IsInt(index.Type)) {
      throw Error($"the index of a pointer is an integer, not {DisplayName(index.Type)}", indexNode);
    }
    string p = pointer.Code;
    string prefix = "";
    if (!SimpleCode.IsMatch(p)) {
      p = NewTemp();
      prefix = $"{CType(pointer.Type)} {p} = {pointer.Code}; ";
    }
    string temp = NewTemp();
    return $"({{ {prefix}{elemC}* {temp} = ({elemC}*)df_ptr_at({p}.cur, {p}.base, {p}.count, (int64_t)({index.Code}), sizeof({elemC}), {fn!.NameConst}); if (!{temp}) {RaiseJump} {temp}; }})";
  }

  // a constant index on a pointer whose bounds are known: checked here (spec 12.1)
  void CheckConstPointerIndex(Node pointerNode, Node indexNode, Scope scope) {
    if (pointerNode.Kind != "Name" || scope.Find(pointerNode.Text!) is not VarInfo variable || variable.PointerCount is not long count) {
      return;
    }
    if (indexNode.Kind != "Empty" && IntLiteralValue(indexNode) is System.Numerics.BigInteger value && (value < 0 || value >= count)) {
      throw Error($"index {value} is outside the pointer '{pointerNode.Text}', which has {count} item{(count == 1 ? "" : "s")} (spec 12.1)", indexNode);
    }
  }

  // p[i], p[]: the value of the item
  Expr? PointerItemRead(Node n, Scope scope) {
    var pointer = ExprOf(n.Children[0], scope);
    if (!IsPointer(pointer.Type)) {
      return null;
    }
    CheckConstPointerIndex(n.Children[0], n.Children[1], scope);
    return new Expr($"(*{PointerItemAddress(pointer, n.Children[1], scope, n)})", PointedType(pointer.Type));
  }

  // p[i] = v, p[].x = v: the place of the item, through a writable pointer only
  LValueInfo? PointerItemLValue(Node target, Scope scope) {
    if (target.Kind != "Index") {
      return null;
    }
    var pointer = ExprOf(target.Children[0], scope);
    if (!IsPointer(pointer.Type)) {
      return null;
    }
    if (!IsWritablePointer(pointer.Type)) {
      throw Error($"{(target.Children[0].Kind == "Name" ? $"'{target.Children[0].Text}'" : "this pointer")} is a read-only pointer ({DisplayName(pointer.Type)}): write through a &*T, made with &*x or pointer.to(&x) (spec 12.1)", target);
    }
    CheckConstPointerIndex(target.Children[0], target.Children[1], scope);
    return new LValueInfo($"(*{PointerItemAddress(pointer, target.Children[1], scope, target)})", PointedType(pointer.Type), false);
  }

  // p == q, p == null: the same address; no other operator works on pointers (spec 12.1)
  Expr? PointerCombine(string op, Expr l, Expr r, Node at) {
    if (!IsPointer(l.Type) && !IsPointer(r.Type)) {
      return null;
    }
    if (op is not ("==" or "!=")) {
      throw Error(op is "+" or "-"
        ? "no pointer arithmetic: move the cursor with p.add(steps) and read with p[i] (spec 12.1)"
        : $"the operator {op} does not work on pointers: only == and != (spec 12.1)", at);
    }
    string Cur(Expr e) => e.Type == "null" ? "NULL" : IsPointer(e.Type) ? $"(void*)({e.Code}).cur"
      : throw Error($"cannot compare {DisplayName(l.Type)} and {DisplayName(r.Type)}", at);
    if (IsPointer(l.Type) && IsPointer(r.Type) && PointedType(l.Type) != PointedType(r.Type)) {
      throw Error($"cannot compare {DisplayName(l.Type)} and {DisplayName(r.Type)}: they point to different types", at);
    }
    return new Expr($"({Cur(l)} {op} {Cur(r)})", "bool");
  }

  // ---------------------------------------------------------------- lifetimes: a pointer does not outlive what it points into

  // the variable whose memory the value points into, when the compiler knows it
  VarInfo? PointerSource(Node value, Scope scope) {
    switch (value.Kind) {
      case "AddressOf":
      case "WritablePointerOf":
        return PlaceRoot(value.Children[0]) is Node root && scope.Find(root.Text!) is { IsConst: false } pointed ? pointed : null;   // a const is static data
      case "Cast":
        return IsPointerTypeNode(value.Children[0]) ? PointerSource(value.Children[1], scope) : null;   // (*u8)p points where p points
      case "Paren":
        return PointerSource(value.Children[0], scope);
      case "Name":
        return scope.Find(value.Text!) is VarInfo variable && pointerSources.TryGetValue(variable, out var source) ? source : null;
      case "Call": {
        var args = value.Children[1].Children;
        if (value.Children[0] is { Kind: "Member", Text: "to" } member && member.Children[0] is { Kind: "Name", Text: "pointer" } && scope.Find("pointer") == null && args.Count == 1) {
          var arg = args[0].Kind == "WritableAddressOf" ? args[0].Children[0] : args[0];
          return PlaceRoot(arg) is Node argRoot ? scope.Find(argRoot.Text!) : null;
        }
        // a result can point into what its pointer arguments point into: the innermost of them
        VarInfo? inner = null;
        foreach (var arg in args) {
          var argValue = arg.Kind == "Named" ? arg.Children[0] : arg;
          if (PointerSource(argValue, scope) is VarInfo candidate && (inner == null || ScopeDepth(candidate, scope) > ScopeDepth(inner, scope))) {
            inner = candidate;
          }
        }
        return inner;
      }
      default:
        return null;
    }
  }

  // how deep the scope of the variable is, seen from here (0: the outermost)
  static int ScopeDepth(VarInfo variable, Scope scope) {
    var chain = new List<Scope>();
    for (var s = scope; s != null; s = s.Parent) {
      chain.Add(s);
    }
    chain.Reverse();
    return chain.FindIndex(s => s.Vars.ContainsValue(variable));
  }

  // pointer p = *x
  void NotePointerDecl(Node decl, VarInfo variable, Node? init, Scope scope, string name) {
    if (init == null || !IsPointer(variable.Type)) {
      return;
    }
    if (PointerSource(init, scope) is VarInfo source) {
      pointerSources[variable] = source;
    }
    Node? place = null;
    if (init.Kind is "AddressOf" or "WritablePointerOf") {
      place = init.Children[0];
    } else if (init.Kind == "Call" && PointerToCallNode(init, scope) && init.Children[1].Children.Count == 1) {
      var arg = init.Children[1].Children[0];
      place = arg.Kind == "WritableAddressOf" ? arg.Children[0] : arg;
    }
    if (place == null) {
      return;
    }
    var collNode = place.Kind == "Index" ? place.Children[0] : place;
    string placeType = ExprOf(collNode, scope).Type;
    if (IsArr(placeType) || IsVec(placeType)) {
      // the array or the vec stays as it is until the last use of the pointer: not assigned, moved, ended or passed as & (spec 12.1, 12);
      // the size of a vec is checked when it runs (its count of open iterators)
      if (!LibraryCode && LendPath(collNode) is string path && followingStatements.TryGetValue(decl, out var after)) {
        LockLenders(decl, after, new List<string> { path }, false, false, scope);
      }
      return;
    }
    if (place.Kind != "Index" && !IsVec(placeType) && !AssignedAgain(name)) {
      variable.PointerCount = 1;   // made from one value and never assigned again: its bounds are known (spec 12.1)
    }
  }

  bool PointerToCallNode(Node n, Scope scope) =>
    n.Children[0] is { Kind: "Member", Text: "to" } member && member.Children[0] is { Kind: "Name", Text: "pointer" } && scope.Find("pointer") == null;

  // p = *x: p must not live longer than x
  void CheckPointerAssign(LValueInfo target, Node targetNode, Node value, Scope scope, Node at) {
    if (!IsPointer(target.Type)) {
      return;
    }
    if (target.Variable == null) {
      throw NotSupported("a pointer stored outside a variable", at);
    }
    if (PointerSource(value, scope) is not VarInfo source) {
      pointerSources.Remove(target.Variable);
      return;
    }
    if (ScopeDepth(target.Variable, scope) < ScopeDepth(source, scope)) {
      throw Error($"'{targetNode.Text}' would point into '{SourceName(source, scope)}' after its block ends: a pointer cannot outlive what it points to (spec 12.1)", at);
    }
    pointerSources[target.Variable] = source;
  }

  // return p: not into a local or a parameter of the function
  void CheckPointerResult(Node value, string type, Scope scope, Node at) {
    if (!IsPointer(type) || PointerSource(value, scope) is not VarInfo source) {
      return;
    }
    if (source.IsLocal || source.IsParam) {
      throw Error($"the result points into '{SourceName(source, scope)}', which ends with the function: a pointer cannot outlive what it points to (spec 12.1)", at);
    }
  }

  static string SourceName(VarInfo source, Scope scope) {
    for (var s = scope; s != null; s = s.Parent) {
      foreach (var pair in s.Vars) {
        if (pair.Value == source) {
          return pair.Key;
        }
      }
    }
    return source.CName;
  }

  void CheckNoPointerField(string type, Node at) {
    if (IsPointer(type)) {
      throw NotSupported("pointer fields (pointers in struct<fixed> come with the C interop, spec 12.1)", at);
    }
  }

  // *i32, &*Point, **i32: the name toStr prints (spec 12.1)
  string PointerDisplay(string type) {
    string pointed = PointedType(type);
    return (IsWritablePointer(type) ? "&*" : "*") + (IsPointer(pointed) ? PointerDisplay(pointed) : DisplayName(pointed));
  }

  string PointerStr(Expr e) {
    string elemC = CType(PointedType(e.Type));
    return WithTemp(e, p => $"df_ptr_str({CQuoted(CodePoints(PointerDisplay(e.Type)), out _)}, {p}.cur, {p}.base, {p}.count, sizeof({elemC}))");
  }

  // ---------------------------------------------------------------- methods (spec 12.1) and Root.Alignment (spec 12.2)

  static readonly HashSet<string> PointerMethods = new() {
    "add", "addRaw", "inRange", "inRangeRaw", "getOffset", "getOffsetRaw", "getAddress", "getAddressRaw", "isAligned"
  };

  // the alignment of the architecture: the size of the native integer (spec 12.2)
  static int ArchAlignment => NativeInt == "i64" ? 8 : 4;
  const int CacheAlignment = 64;   // the default of the cache line (spec 12.2); the compiler option is not built yet

  // p.add(steps), p.getOffset() ...: null when the call is not a method of a pointer
  Expr? PointerMethodCall(Node n, Scope scope) {
    if (n.Children[0] is not { Kind: "Member" } member || !PointerMethods.Contains(member.Text!) || !IsPointerNode(member.Children[0], scope)) {
      return null;
    }
    var args = n.Children[1].Children;
    string name = member.Text!;
    var pointer = ExprOf(member.Children[0], scope);
    string elemC = CType(PointedType(pointer.Type));
    bool raw = name.EndsWith("Raw");
    string unit = raw ? "1" : $"(int64_t)sizeof({elemC})";
    string fnName = fn!.NameConst;
    string nullRaise = $"{{ df_raise(&df_desc_NullPointerException, {fnName}); {RaiseJump} }}";

    string Amount(int count) {
      if (args.Count != count || args.Any(arg => arg.Kind == "Named")) {
        throw Error(count == 0 ? $"{name}() takes no argument (spec 12.1)" : $"{name} takes one amount: {name}({(raw ? "bytes" : "steps")}) (spec 12.1)", n);
      }
      var amount = ExprOf(args[0], scope, "i64");
      if (!IsInt(amount.Type)) {
        throw Error($"the amount of {name} is an integer, not {DisplayName(amount.Type)}", args[0]);
      }
      return $"((int64_t)({amount.Code}) * {unit})";
    }

    string held = NewTemp();
    string Hold(string body) => $"({{ {CType(pointer.Type)} {held} = {pointer.Code}; {body}; }})";
    switch (name) {
      case "add":
      case "addRaw": {
        // the cursor moves in place: the variable of the pointer is written (a plain parameter is read-only)
        string delta = Amount(1);
        var place = LValue(member.Children[0], scope);
        string at = NewTemp();
        string moved = NewTemp();
        return new Expr($"({{ {CType(pointer.Type)}* {at} = &({place.Code}); void* {moved} = df_ptr_move({at}->cur, {at}->base, {at}->count, {delta}, sizeof({elemC}), {fnName}); "
          + $"if (!{moved}) {RaiseJump} {at}->cur = ({elemC}*){moved}; }})", "void");
      }
      case "inRange":
      case "inRangeRaw":
        return new Expr(Hold($"df_ptr_in_range({held}.cur, {held}.base, {held}.count, {Amount(1)}, sizeof({elemC}))"), "bool");
      case "getOffset":
      case "getOffsetRaw": {
        if (args.Count == 0) {
          return new Expr(Hold($"if (!{held}.cur) {nullRaise} (int64_t)((char*){held}.cur - (char*){held}.base) / {unit}"), "i64");
        }
        if (args.Count != 1 || args[0].Kind == "Named") {
          throw Error($"{name} takes nothing, or the pointer to measure from: {name}(other) (spec 12.1)", n);
        }
        var other = ExprOf(args[0], scope);
        string otherCur;
        if (IsPointer(other.Type)) {
          if (!raw && PointedType(other.Type) != PointedType(pointer.Type)) {
            throw Error($"getOffset counts steps of {DisplayName(PointedType(pointer.Type))}: the other pointer must point to the same type; use getOffsetRaw for bytes (spec 12.1)", args[0]);
          }
          otherCur = $"(void*)({other.Code}).cur";
        } else if (other.Type == "addr" && raw) {
          otherCur = $"(void*)({other.Code})";
        } else {
          throw Error($"{name} measures from {(raw ? "a pointer or an addr" : "a pointer")}, not from {DisplayName(other.Type)} (spec 12.1)", args[0]);
        }
        string from = NewTemp();
        return new Expr(Hold($"void* {from} = {otherCur}; if (!{held}.cur || !{from}) {nullRaise} (int64_t)((char*){held}.cur - (char*){from}) / {unit}"), "i64");
      }
      case "getAddress":
      case "getAddressRaw":
        if (args.Count == 0 && !raw) {
          return new Expr(Hold($"(void*){held}.cur"), "addr");
        }
        return new Expr(Hold($"({held}.cur ? (void*)((char*){held}.cur + {Amount(1)}) : (void*)0)"), "addr");
      default: {
        // isAligned(), isAligned(bytes)
        string bytes = args.Count == 0 ? ArchAlignment.ToString() : AlignmentArg(args, n, scope);
        return new Expr(Hold($"df_ptr_aligned({held}.cur, {bytes})"), "bool");
      }
    }
  }

  string AlignmentArg(List<Node> args, Node n, Scope scope) {
    if (args.Count != 1 || args[0].Kind == "Named") {
      throw Error("isAligned takes nothing, or the alignment in bytes: isAligned(16) (spec 12.1)", n);
    }
    var bytes = ExprOf(args[0], scope, "u64");
    if (!IsInt(bytes.Type)) {
      throw Error($"the alignment is a number of bytes, not {DisplayName(bytes.Type)}", args[0]);
    }
    return $"(uint64_t)({bytes.Code})";
  }

  // Root.Alignment.arch, Root.Alignment.cache (spec 12.2)
  static Expr? AlignmentValue(string path) => path switch {
    "Root.Alignment.arch" => new Expr($"((uint64_t){ArchAlignment}ULL)", "u64"),
    "Root.Alignment.cache" => new Expr($"((uint64_t){CacheAlignment}ULL)", "u64"),
    _ => null
  };

  // Root.Alignment.type<T>(), isAligned(p), isAligned<T>(p), isAlignedCache(p) (spec 12.2)
  Expr? AlignmentCall(Node n, Scope scope) {
    var callee = n.Children[0];
    var named = callee.Kind == "GenericRef" ? callee.Children[0] : callee;
    if (named.Kind != "Member" || Path(named) is not string path || !path.StartsWith("Root.Alignment.") || scope.Find(path.Split('.')[0]) != null) {
      return null;
    }
    string member = path["Root.Alignment.".Length..];
    var args = n.Children[1].Children;
    string? typeArg = null;
    if (callee.Kind == "GenericRef") {
      if (callee.Children.Count != 2) {
        throw Error($"Alignment.{member} takes one type: Alignment.{member}<T> (spec 12.2)", n);
      }
      typeArg = ResolveType(callee.Children[1]);
    }
    if (member == "type") {
      if (typeArg == null || args.Count != 0) {
        throw Error("Alignment.type<T>() takes the type and no argument (spec 12.2)", n);
      }
      return new Expr($"((uint64_t)_Alignof({CType(typeArg)}))", "u64");
    }
    if (member is not ("isAligned" or "isAlignedCache")) {
      throw Error($"Root.Alignment has no function '{member}' (spec 12.2: type<T>(), arch, cache, isAligned, isAlignedCache)", n);
    }
    if (args.Count != 1 || args[0].Kind == "Named") {
      throw Error($"Alignment.{member} takes one pointer (spec 12.2)", n);
    }
    var pointer = ExprOf(args[0], scope);
    if (!IsPointer(pointer.Type)) {
      throw Error($"Alignment.{member} takes a pointer, not {DisplayName(pointer.Type)} (spec 12.2)", args[0]);
    }
    if (member == "isAlignedCache" && typeArg != null) {
      throw Error("Alignment.isAlignedCache takes no type (spec 12.2)", n);
    }
    string bytes = member == "isAlignedCache" ? CacheAlignment.ToString() : typeArg != null ? $"(uint64_t)_Alignof({CType(typeArg)})" : ArchAlignment.ToString();
    return new Expr($"df_ptr_aligned(({pointer.Code}).cur, {bytes})", "bool");
  }

  // ---------------------------------------------------------------- step 4: static data, casts, addr, bounds, unsafe (spec 12.1)

  static bool IsPointerTypeNode(Node n) => n.Kind is "Pointer" or "WritablePointer" || n is { Kind: "Type", Text: "pointer" };

  int unsafeDepth;
  int staticDataCount;

  static bool HasUnsafeMod(Node? node) => node?.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ').Contains("unsafe") == true;

  // inside unsafe { }, an unsafe function or a method of an unsafe class (spec 12.1)
  bool InUnsafe => unsafeDepth > 0 || HasUnsafeMod(currentFun?.Node) || HasUnsafeMod(currentClass?.Node);

  // a class marked unsafe can be used only from unsafe code (spec 12.1)
  void CheckUnsafeClassUse(ClassInfo? cls, Node at) {
    if (cls != null && HasUnsafeMod(cls.Node) && currentFun != null && !InUnsafe && !LibraryCode) {
      throw Error($"'{cls.Name}' is an unsafe class: it can be used only from unsafe code (unsafe {{ }}, an unsafe function or class) (spec 12.1)", at);
    }
  }

  // *"1.2.3", *5, *LIMIT: a read-only pointer to data that lives for the whole program
  Expr StaticPointer(Node place, bool literal, Scope scope, Node at, string? expectedPointee) {
    string? cName = null;
    string type;
    if (literal) {
      var value = ExprOf(place, scope, expectedPointee);
      type = value.Type;
      CheckPointee(type, at);
      cName = $"df_sd{staticDataCount++}";
      literalCode.Append($"static {CType(type)} {cName} = {value.Code};\n");
    } else if (scope.Find(PlaceRoot(place)!.Text!) is VarInfo local) {
      if (place.Kind != "Name" || local.ConstValue is not System.Numerics.BigInteger constValue) {
        throw NotSupported("a pointer to a const that is not an integer literal (write the literal, or a top-level const)", at);
      }
      type = local.Type;
      cName = $"df_sd{staticDataCount++}";
      literalCode.Append($"static {CType(type)} {cName} = {IntConstCode(constValue, Normalize(type))};\n");
    } else {
      var global = ExprOf(place, scope);   // a top-level const: a C global set at program start
      type = global.Type;
      CheckPointee(type, at);
      cName = global.Code;
    }
    string pointerType = PointerType(type, false);
    string cType = PointerCType(pointerType);
    return new Expr($"(({cType}){{ &{cName}, &{cName}, 1, NULL }})", pointerType);
  }

  // (*i16)p, (&*u8)w, (pointer<u64>)p: the same memory read as another type; the count is the bytes divided by the new size (spec 12.1)
  Expr PointerCast(Expr e, string to, Node n) {
    if (!IsPointer(to) || !IsPointer(e.Type)) {
      throw Error(IsPointer(e.Type) ? $"a pointer becomes {DisplayName(to)} only with a transmute: [{DisplayName(to)}]p (spec 12.1, 3.5)"
        : $"{DisplayName(e.Type)} becomes a pointer only with a transmute, in unsafe code: [{DisplayName(to)}]x (spec 12.1)", n);
    }
    if (IsWritablePointer(to) && !IsWritablePointer(e.Type)) {
      throw Error($"a read-only pointer ({DisplayName(e.Type)}) cannot be cast to a writable one ({DisplayName(to)}) (spec 12.1)", n);
    }
    string fromC = CType(PointedType(e.Type));
    string toC = CType(PointedType(to));
    string cType = CType(to);
    return new Expr(WithTemp(e, p =>
      $"(({cType}){{ ({toC}*){p}.cur, ({toC}*){p}.base, {p}.count < 0 ? -1 : ({p}.count * (int64_t)sizeof({fromC})) / (int64_t)sizeof({toC}), NULL }})"), to);
  }

  // [addr]p, [u64]p: the address of the cursor; [*i32]a, [*i32]0xA0000: an unbounded pointer, only in unsafe code (spec 12.1)
  Expr? PointerTransmute(string to, Expr e, Node n) {
    if (IsPointer(e.Type)) {
      if (to == "addr") {
        return new Expr($"((void*)({e.Code}).cur)", "addr");
      }
      if (to is "u64" or "i64") {
        return new Expr($"(({CType(to)})(uintptr_t)({e.Code}).cur)", to);
      }
      throw Error($"a pointer transmutes to addr or to a 64-bit integer, not to {DisplayName(to)} (spec 12.1)", n);
    }
    if (!IsPointer(to)) {
      return null;
    }
    if (e.Type != "addr" && !IsInt(e.Type)) {
      throw Error($"a pointer is made by a transmute only from an addr or an integer, not from {DisplayName(e.Type)} (spec 12.1)", n);
    }
    if (!InUnsafe) {
      throw Error($"[{DisplayName(to)}]x makes a pointer the compiler cannot check: only in unsafe code (unsafe {{ }}, an unsafe function or class) (spec 12.1)", n);
    }
    string elemC = CType(PointedType(to));
    string address = e.Type == "addr" ? $"(void*)({e.Code})" : $"(void*)(uintptr_t)({e.Code})";
    string temp = NewTemp();
    return new Expr($"({{ {elemC}* {temp} = ({elemC}*){address}; ({CType(to)}){{ {temp}, {temp}, -1, NULL }}; }})", to);
  }

  // *i32{count} p = ...: the pointer starts at its cursor with count items; in safe code they must be inside its bounds (spec 12.1)
  string PointerBound(string code, string type, Node boundNode, Scope scope) {
    var bound = ExprOf(boundNode, scope, "i64");
    if (!IsInt(bound.Type)) {
      throw Error($"the bound of a pointer is a number of items, not {DisplayName(bound.Type)} (spec 12.1)", boundNode);
    }
    string elemC = CType(PointedType(type));
    string held = NewTemp();
    string count = NewTemp();
    return $"({{ {CType(type)} {held} = {code}; int64_t {count} = (int64_t)({bound.Code}); "
      + $"if (!df_ptr_bound_ok({held}.cur, {held}.base, {held}.count, {count}, sizeof({elemC}), {(InUnsafe ? "false" : "true")}, {fn!.NameConst})) {RaiseJump} "
      + $"if ({held}.cur) {{ {held}.base = {held}.cur; {held}.count = {count}; }} {held}; }})";
  }

  static bool HasPointerBound(Node typeNode) =>
    typeNode.Kind is "Pointer" or "WritablePointer" && typeNode.Children.Count > 1 || typeNode.Children.Any(HasPointerBound);

  // ---------------------------------------------------------------- step 5: C interop (spec 12.1, 22)

  // a pointer field of a struct<fixed> or struct<compact>: the C pointer only, "cptr<T>" / "cptr<&T>" inside the compiler
  static bool IsCPtr(string t) => t.StartsWith("cptr<") && t.EndsWith('>');
  static string CPtrOf(string pointerType) => "cptr<" + pointerType["pointer<".Length..];
  static string PointerOfCPtr(string cptr) => "pointer<" + cptr["cptr<".Length..];

  string CPtrCType(string cptr) => CType(PointedType(PointerOfCPtr(cptr))) + "*";

  // the type of a field of a struct: a pointer only in a struct with a C layout, kept as the C pointer
  string StructFieldPointer(string type, string? layout, Node at) {
    if (!IsPointer(type)) {
      return type;
    }
    if (layout == null) {
      throw NotSupported("pointer fields outside a struct<fixed> or struct<compact> (spec 12.1)", at);
    }
    if (IsPointer(PointedType(type)) || PointedType(type) == "string") {
      throw NotSupported($"a field {DisplayName(type)} (a pointer field points to a plain value or a struct)", at);
    }
    return CPtrOf(type);
  }

  void CheckUnsafeCPointer(string what, Node at) {
    if (!InUnsafe && !LibraryCode) {
      throw Error($"{what}: a pointer that crosses into C has no bounds and no validity guarantee, so it is used only in unsafe code (unsafe {{ }}, an unsafe function or class) (spec 12.1)", at);
    }
  }

  // s.nextIn: an unbounded pointer (spec 12.1)
  Expr CPtrRead(string code, string cptr, Node at) {
    CheckUnsafeCPointer($"reading the pointer field '{at.Text}'", at);
    string type = PointerOfCPtr(cptr);
    string temp = NewTemp();
    return new Expr($"({{ {CPtrCType(cptr)} {temp} = {code}; ({CType(type)}){{ {temp}, {temp}, -1, NULL }}; }})", type);
  }

  // s.nextOut = p: the cursor of p; null is always allowed
  string CPtrWrite(Expr e, string cptr, Node at) {
    if (e.Type == "null") {
      return "NULL";
    }
    string type = PointerOfCPtr(cptr);
    if (!IsPointer(e.Type) || !Assignable(type, e.Type)) {
      throw Error(IsPointer(e.Type) && PointedType(e.Type) == PointedType(type)
        ? $"a read-only pointer ({DisplayName(e.Type)}) cannot become writable: make a writable one with &*x or pointer.to(&x) (spec 12.1)"
        : $"cannot convert {DisplayName(e.Type)} to {DisplayName(type)} (spec 12.1)", at);
    }
    CheckUnsafeCPointer("storing a pointer in a struct field", at);
    return $"(({CPtrCType(cptr)})({e.Code}).cur)";
  }

  string CPtrStr(Expr e) {
    string pointer = PointerOfCPtr(e.Type);
    string elemC = CType(PointedType(pointer));
    return WithTemp(e, p => $"df_ptr_str({CQuoted(CodePoints(PointerDisplay(pointer)), out _)}, (void*){p}, (void*){p}, -1, sizeof({elemC}))");
  }

  // extern fun f(*i32 p), extern fun *u8 g(): only the cursor crosses into C
  void CheckExternPointer(string type, Node at) {
    if (IsPointer(PointedType(type))) {
      throw NotSupported("a pointer to a pointer given to or returned by a C function", at);
    }
    if (PointedType(type) == "string") {
      throw NotSupported("a pointer to a string given to or returned by a C function", at);
    }
  }
}
