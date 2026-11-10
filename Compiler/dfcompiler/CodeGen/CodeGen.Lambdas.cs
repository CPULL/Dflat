using System.Text;
using dfparser;

namespace dfcompiler;

// Lambdas and function types (spec 13.3).
//
// A function type is written fun(i32), string(char, char); in the compiler it is the type "fun(R)(P1,P2)". Its C value is a df_fn
// { code, ctx } of the runtime. Every lambda is compiled as a function of its own, f_lambda_N(void* df_ctx, params...), emitted after the
// other functions (pendingInstances). A lambda with captures has a context, a counted struct df_lctx_N that starts with df_lhead: each
// holder has a usage and the last one runs df_lend_N (it ends what the lambda owns) and frees it. A lambda without captures has no context.
//
// Captures (13.3.1): an unlisted outer variable is a read-only borrow, &a a writable one (the context keeps the address of the variable);
// a! a copy and ->a a move (the context owns the value). A lambda that borrows cannot outlive its variables: it can be called, passed to a
// call or kept in a new local, not assigned to an existing variable or returned (a strict form of 13.3.4 until borrow lifetimes are
// checked). A function-type parameter may borrow too.
//
// Inside a method the fields are captured by their bare names, like locals; a borrowed field borrows the object (13.3.1).
//
// Counted and weak captures keep a usage or a weak reference in the context; properties and the parent are reached through the object
// (p_self, LambdaClass). Not built yet: function types as map keys and set items, &a of string?, tuples, structs and function values.
//
// Exceptions: every lambda returns its exception, as a function that can fail does (a call through a function value cannot know which
// lambda it reaches), and each call checks it; a silent one is ignored by the caller unless a catch there handles it (spec 15.4).
public sealed partial class CodeGen {
  static bool IsFunType(string type) => type.StartsWith("fun(");

  static string MakeFunType(string ret, IEnumerable<string> ps) => $"fun({ret})({string.Join(",", ps)})";

  // fun(R)(P1,P2) → (R, [P1, P2])
  static (string Ret, List<string> Params) FunParts(string type) {
    int close = MatchingClose(type, 3);
    string ret = type[4..close];
    string inner = type[(close + 2)..^1];
    return (ret, SplitTopLevel(inner));
  }

  static int MatchingClose(string text, int open) {
    int depth = 0;
    for (int index = open; index < text.Length; index++) {
      if (text[index] is '(' or '<') {
        depth++;
      } else if (text[index] is ')' or '>') {
        depth--;
        if (depth == 0) {
          return index;
        }
      }
    }
    return text.Length - 1;
  }

  static List<string> SplitTopLevel(string text) {
    var parts = new List<string>();
    if (text.Length == 0) {
      return parts;
    }
    int depth = 0;
    int start = 0;
    for (int index = 0; index < text.Length; index++) {
      if (text[index] is '(' or '<') {
        depth++;
      } else if (text[index] is ')' or '>') {
        depth--;
      } else if (text[index] == ',' && depth == 0) {
        parts.Add(text[start..index]);
        start = index + 1;
      }
    }
    parts.Add(text[start..]);
    return parts;
  }

  // the way the program writes it: fun(i32), string(char, char)
  static string FunDisplay(string type) {
    var (ret, ps) = FunParts(type);
    string Shown(string p) {
      string marker = p.StartsWith("->") ? "->" : p.StartsWith("&") ? "&" : "";
      string rest = p[marker.Length..];
      return marker + (IsWeakType(rest) ? "%" + rest[1..] : IsFunType(rest) ? FunDisplay(rest) : rest);
    }
    string list = string.Join(", ", ps.Select(Shown));
    return ret == "void" ? $"fun({list})" : $"{(IsFunType(ret) ? FunDisplay(ret) : ret)}({list})";
  }

  // fun(i32), fun i32(string), i32(string) (spec 13.3.2)
  string ResolveFunType(Node n) {
    var returns = n.Children.FirstOrDefault(child => child.Kind == "Returns");
    string ret = returns == null ? "void" : ResolveType(returns.Children[0]);
    var list = n.Children.First(child => child.Kind == "ParamTypes");
    var ps = list.Children.Select(ParamTypeText).ToList();
    return MakeFunType(ret, ps);
  }

  // a parameter of a function type with its mode (spec 13.3.2): T, &T, ->T, @T, &@T, %T (written _T inside the compiler), &%T
  string ParamTypeText(Node typeNode) {
    if (typeNode.Kind != "ModeType") {
      return ResolveType(typeNode);
    }
    string type = ResolveType(typeNode.Children[0]);
    switch (typeNode.Text) {
      case "@" or "&@":
        return (typeNode.Text == "&@" ? "&" : "") + CountedTypeOf(type, typeNode, true);
      case "%" or "&%":
        if (!IsClass(type)) {
          throw Error("only objects have weak references", typeNode);
        }
        return (typeNode.Text == "&%" ? "&" : "") + "_" + type;
      case "->":
        return Giftable(type) ? "->" + type : type;   // ->i32: a basic value is a copy anyway (spec 13.1.1)
      default:
        return "&" + type;
    }
  }

  bool Giftable(string type) => IsClass(type) || type == "string" || IsArr(type) || StructHoldsObjects(type);

  // the same text for a parameter of a lambda
  static string ParamTypeText(ParamSig p) =>
    p.Ref == 2 ? "&" + p.Type : p.IsGift ? "->" + p.Type : (IsCountedType(p.Type) || IsWeakType(p.Type)) && p.IsWritable ? "&" + p.Type : p.Type;

  // the parameter a call through a function value passes to: its mode comes from the function type
  ParamSig ParamFromFunType(string name, string text) {
    if (text.StartsWith("->")) {
      return new ParamSig(name, text[2..], null) { IsGift = true };
    }
    if (text.StartsWith("&") && text.Length > 1 && (IsCountedType(text[1..]) || IsWeakType(text[1..]))) {
      return new ParamSig(name, text[1..], null) { IsWritable = true, IsWeak = IsWeakType(text[1..]) };
    }
    if (text.StartsWith("&")) {
      return new ParamSig(name, text[1..], null) { Ref = 2 };
    }
    return new ParamSig(name, text, null) { IsWeak = IsWeakType(text) };
  }

  // the parameters of a lambda, with their modes, as a function declares them (spec 13.3, 13.1)
  ParamSig LambdaParam(Node p) {
    string type = ResolveType(p.Children[0]);
    bool weak = p.Children.Any(child => child.Kind == "Weak");
    if (weak && !IsClass(type)) {
      throw Error("only objects have weak references", p);
    }
    if (p.Children.Any(child => child.Kind == "Counted")) {
      type = CountedTypeOf(type, p, true);
    }
    if (weak) {
      type = "_" + type;
    }
    return new ParamSig(p.Text!, type, null) {
      IsWritable = p.Children.Any(child => child.Kind == "ReadWrite"),
      IsGift = p.Kind == "GiftParam" && Giftable(type),
      Ref = p.Kind == "WritableRefParam" ? 2 : 0,
      IsWeak = weak
    };
  }

  // ---------------------------------------------------------------- lambda expressions

  int lambdaCounter;
  readonly HashSet<Node> borrowingLambdas = new();
  readonly HashSet<Node> writingLambdas = new();   // lambdas with a & capture: f! of one is a compile error (spec 13.3.3)

  // a capture kept by address (a borrow) can have any type; a copy or a move only the types handled below
  bool IsPlainValueType(string type) =>
    IsNumeric(type) || type is "bool" or "char" or "date" or "time" or "clock" or "lapse" or "addr" || IsEnum(type);

  Expr LambdaExpr(Node n, Scope scope, string? expected) {
    var paramsNode = n.Children.First(child => child.Kind == "Params");
    var capturesNode = n.Children.FirstOrDefault(child => child.Kind == "Captures");
    var block = n.Children.First(child => child.Kind == "Block");

    // parameters: their types are always written (13.3)
    var ps = new List<ParamSig>();
    foreach (var p in paramsNode.Children) {
      if (p.Children.Count == 0) {
        throw Error($"the type of the lambda parameter '{p.Text}' is written: (i32 {p.Text}) (spec 13.3)", p);
      }
      if (ps.Any(other => other.Name == p.Text)) {
        throw Error($"duplicate parameter '{p.Text}'", p);
      }
      ps.Add(LambdaParam(p));
    }
    string ret = "void";
    if (expected != null && IsFunType(expected)) {
      var (expectedRet, expectedParams) = FunParts(expected);
      if (expectedParams.Count != ps.Count || expectedParams.Zip(ps).Any(pair => pair.First != ParamTypeText(pair.Second))) {
        throw Error($"this lambda is {FunDisplay(MakeFunType(expectedRet, ps.Select(ParamTypeText)))}, but {FunDisplay(expected)} is expected", n);
      }
      ret = expectedRet;
    }

    // captures: the listed ones, then every other outer variable the body uses (a read-only borrow)
    var listed = new Dictionary<string, Node>();
    foreach (var capture in capturesNode?.Children ?? new List<Node>()) {
      string name = capture.Text!;
      if (capture.Kind == "Capture") {
        throw Error($"'{name}' is captured read-only without listing it: remove it from the list, or write &{name} to change it (spec 13.3.1)", capture);
      }
      if (listed.ContainsKey(name)) {
        throw Error($"'{name}' is listed twice", capture);
      }
      if (ps.Any(p => p.Name == name)) {
        throw Error($"'{name}' is a parameter of the lambda, not a capture", capture);
      }
      if (capture.Kind is "CountedCapture" or "WeakCapture") {
        CountedSource(capture, scope);   // @a, &@a, %a, &%a: a counted object, or a weak reference for %a (spec 13.3.1)
      } else if (LambdaClassMember(name, scope) is FieldInfo member) {
        CheckMemberCapture(member, capture);
      } else if (OuterOf(name, scope, capture) == null) {
        throw Error($"unknown variable '{name}'", capture);
      }
      listed[name] = capture;
    }
    var used = new List<string>();
    CollectNames(block, used);
    var captures = new List<(string Name, VarInfo Outer, string Kind, Node At)>();
    var counted = new List<(string Name, CountedRefInfo Source, Node At)>();
    var members = new List<(FieldInfo Field, string Kind, Node At)>();   // properties and the parent, reached through the object
    foreach (var (name, at) in listed) {
      if (at.Kind is "CountedCapture" or "WeakCapture") {
        counted.Add((name, CountedSource(at, scope), at));
        continue;
      }
      if (LambdaClassMember(name, scope) is FieldInfo listedMember) {
        members.Add((listedMember, at.Kind, at));
        continue;
      }
      captures.Add((name, OuterOf(name, scope, at)!, at.Kind, at));
    }
    foreach (string name in used.Distinct()) {
      if (listed.ContainsKey(name) || ps.Any(p => p.Name == name)) {
        continue;
      }
      if (name == "this" && scope.Find(name) != null) {
        throw Error("there is no this in a lambda: the fields are captured by their bare names (spec 13.3.1)", n);
      }
      var named = Synthetic("Name", n, name);
      if (CountedRef(named, scope) is CountedRefInfo shared) {
        throw Error($"'{name}' is counted and cannot be borrowed: list it as @{name}, or %{name} for a weak reference (spec 13.3.1)", n);
      }
      if (WeakRef(named, scope) is CountedRefInfo) {
        throw Error($"'{name}' is a weak reference: list it as %{name} (spec 13.3.1)", n);
      }
      if (LambdaClassMember(name, scope) is FieldInfo usedMember) {
        members.Add((usedMember, "Implicit", n));
        continue;
      }
      if (OuterOf(name, scope, n) is VarInfo outer) {
        captures.Add((name, outer, "Implicit", n));
      }
    }
    bool needsSelf = members.Any(member => !member.Field.IsStatic && member.Kind is "Implicit" or "WritableCapture");
    if (needsSelf && scope.Find("this") == null) {
      var first = members.First(member => !member.Field.IsStatic);
      throw Error($"'{first.Field.Name}' belongs to an object; a static function has no object", first.At);
    }

    int id = ++lambdaCounter;
    string ctxName = $"df_lctx_{id}";
    var funNode = Synthetic("Fun", n, $"lambda_{id}").Add(block);
    bool hasContext = captures.Count + counted.Count > 0 || needsSelf || members.Any(member => member.Kind == "CopyCapture");
    var lambda = new FunSig($"lambda_{id}", ret, ps, funNode, null) { LambdaCtx = hasContext ? ctxName : "df_lhead" };
    if (members.Any(member => member.Kind is "Implicit" or "WritableCapture")) {
      lambda.LambdaClass = currentClass;   // its body reaches the properties and the parent through the class (spec 13.3.1)
      lambda.LambdaSelf = needsSelf;
    }
    if (ret == "string" || IsClass(ret) || IsArr(ret)) {
      lambda.Gives.Add(0);   // the caller of a function value owns the new value it gets
      lambda.ReturnsOwned = true;
      lambda.ReturnsOwnedSet = true;
    }
    bool borrows = false;
    var fields = new StringBuilder();
    var inits = new StringBuilder();
    var ends = new StringBuilder();
    var clones = new StringBuilder();   // f! (spec 13.3.3): what the lambda owns is cloned; borrows are copied as they are
    if (needsSelf) {
      // the lambda borrows the object of the method: its properties and its parent are read (or, listed &, written) through it
      borrows = true;
      fields.Append("  void* p_self;\n");
      inits.Append("c_->p_self = (void*)self; ");
    }
    foreach (var (member, kind, at) in members) {
      string name = member.Name;
      if (kind == "Implicit") {
        lambda.LambdaReadOnlyMembers.Add(name);   // read-only, as any capture that is not listed (spec 13.3.1)
        continue;
      }
      if (kind != "CopyCapture") {
        continue;   // &prop: written through its accessors
      }
      // prop! and parent!: the value read now (through the getter) is copied into the lambda
      string type = member.Type;
      string cType = CType(type);
      string field = $"c->v_{name}";
      if (IsPlainValueType(type)) {
        var read = ExprOf(Synthetic("Name", at, name), scope, type);
        fields.Append($"  {cType} v_{name};\n");
        inits.Append($"c_->v_{name} = {Convert(read, type, at)}; ");
        lambda.LambdaVars.Add((name, new VarInfo(type, field, false)));
      } else if (type == "string") {
        var read = ExprOf(Synthetic("Name", at, name), scope, type);
        fields.Append($"  df_string v_{name};\n");
        inits.Append($"c_->v_{name} = {(read.Fresh ? read.Code : $"df_str_dup({read.Code})")}; ");
        ends.Append($"  df_str_free({field});\n");
        clones.Append($"  n->v_{name} = df_str_dup(c->v_{name});\n");
        lambda.LambdaVars.Add((name, new VarInfo(type, field, false) { RefSlot = true }));
      } else if (IsArr(type) || IsClass(type) && !IsTraitType(type)) {
        var copy = ExprOf(Synthetic("Copy", at).Add(Synthetic("Name", at, name)), scope);
        OwnedCapture(name, type, copy.Code, fields, inits, ends, clones, lambda, at);
      } else {
        throw NotSupported($"{name}! of a {type} property in a lambda (spec 13.3.1)", at);
      }
    }
    foreach (var (name, source, at) in counted) {
      // @a and &@a: a usage of the lambda (+1 now, -1 when it ends); %a and &%a: a weak reference (spec 13.3.1). A move ->a of a counted
      // object is below, with the other moves
      bool weak = at.Kind == "WeakCapture";
      bool writable = at.Children.Any(child => child.Kind == "ReadWrite");
      fields.Append($"  df_rc* v_{name};\n");
      inits.Append($"c_->v_{name} = {(weak ? "df_rc_weak" : "df_rc_use")}({source.Code}); ");
      ends.Append(weak ? $"  df_rc_unweak(c->v_{name});\n" : $"  df_rc_release(c->v_{name});\n");
      clones.Append($"  n->v_{name} = {(weak ? "df_rc_weak" : "df_rc_use")}(c->v_{name});\n");
      lambda.LambdaVars.Add((name, new VarInfo(source.Type, $"c->v_{name}", true) { IsCounted = !weak, IsWeak = weak, IsWritable = writable }));
    }
    foreach (var (name, outer, kind, at) in captures) {
      if (outer.IsCounted && kind == "MoveCapture") {
        // ->a of a counted object: its usage moves into the lambda, the count does not change (spec 13.3.1, 12.7)
        fields.Append($"  df_rc* v_{name};\n");
        inits.Append($"c_->v_{name} = {outer.CName}; {outer.CName} = NULL; ");
        ends.Append($"  df_rc_release(c->v_{name});\n");
        clones.Append($"  n->v_{name} = df_rc_use(c->v_{name});\n");
        lambda.LambdaVars.Add((name, new VarInfo(outer.Type, $"c->v_{name}", true) { IsCounted = true, IsWritable = outer.IsWritable }));
        continue;
      }
      if (outer.IsCounted || outer.IsWeak) {
        throw Error(outer.IsWeak ? $"'{name}' is a weak reference: list it as %{name} (spec 13.3.1)"
          : $"'{name}' is counted and cannot be borrowed or copied this way: list it as @{name}, %{name} or ->{name} (spec 13.3.1)", at);
      }
      string type = outer.Type;
      string cType = CType(type);
      switch (kind) {
        case "Implicit":
        case "WritableCapture": {
          bool readOnly = kind == "Implicit";
          if (!readOnly) {
            if (outer.IsConst) {
              throw Error($"'{name}' is const: it cannot be captured with &", at);
            }
            if (type == "string" || IsArr(type)) {
              // &s, &a: the lambda can replace the string or the array, as a & parameter can (spec 13.1): the variable must own it
              if (outer.ArrFlag != null) {
                throw Error($"'{name}' may only borrow its array here (it is assigned another array): &{name} needs the variable that owns it (spec 13.1, 6.8.1)", at);
              }
              string address = WritableAddress(Synthetic("Name", at, name), scope, type, at);
              borrows = true;
              fields.Append($"  void* p_{name};\n");
              inits.Append($"c_->p_{name} = (void*)({address}); ");
              lambda.LambdaVars.Add((name, new VarInfo(type, $"(*({cType}*)c->p_{name})", false) { RefSlot = true, BorrowsArray = IsArr(type) }));
              break;
            }
            if (!IsPlainValueType(type) && !IsClass(type)) {
              throw NotSupported($"&{name} of a {type} in a lambda (numbers, bool, char, dates, objects, strings and arrays are built)", at);
            }
          }
          borrows = true;
          fields.Append($"  void* p_{name};\n");
          inits.Append($"c_->p_{name} = (void*)&({outer.CName}); ");
          var inner = new VarInfo(type, $"(*({cType}*)c->p_{name})", readOnly || IsClass(type)) {
            CapturedReadOnly = readOnly,
            ReadOnlyRef = readOnly && (IsClass(type) || IsArr(type)),
            ReadOnlyString = type == "string",
            BorrowsArray = IsArr(type),
            FnBorrows = IsFunType(type)
          };
          lambda.LambdaVars.Add((name, inner));
          break;
        }
        case "CopyCapture": {
          string field = $"c->v_{name}";
          if (IsPlainValueType(type)) {
            fields.Append($"  {cType} v_{name};\n");
            inits.Append($"c_->v_{name} = {outer.CName}; ");
            lambda.LambdaVars.Add((name, new VarInfo(type, field, false)));
          } else if (type == "string") {
            fields.Append($"  df_string v_{name};\n");
            inits.Append($"c_->v_{name} = df_str_dup({outer.CName}); ");
            ends.Append($"  df_str_free({field});\n");
            clones.Append($"  n->v_{name} = df_str_dup(c->v_{name});\n");
            lambda.LambdaVars.Add((name, new VarInfo(type, field, false) { RefSlot = true }));
          } else if (type == "string?") {
            fields.Append($"  df_opt_string v_{name};\n");
            inits.Append($"c_->v_{name} = df_opt_str_dup({outer.CName}); ");
            ends.Append($"  df_str_free({field}.val);\n");
            clones.Append($"  n->v_{name} = df_opt_str_dup(c->v_{name});\n");
            lambda.LambdaVars.Add((name, new VarInfo(type, field, false)));
          } else if (IsFunType(type)) {
            fields.Append($"  df_fn v_{name};\n");
            inits.Append($"c_->v_{name} = df_fn_retain({outer.CName}); ");
            ends.Append($"  df_fn_release({field});\n");
            clones.Append($"  n->v_{name} = df_fn_clone(c->v_{name});\n");
            borrows |= outer.FnBorrows;
            lambda.LambdaVars.Add((name, new VarInfo(type, field, false) { FnBorrows = outer.FnBorrows, FnWrites = outer.FnWrites }));
          } else if (IsArr(type) || IsClass(type) && !IsTraitType(type)) {
            // a clone (12.6): the lambda owns it
            var copy = ExprOf(Synthetic("Copy", at).Add(Synthetic("Name", at, name)), scope);
            OwnedCapture(name, type, copy.Code, fields, inits, ends, clones, lambda, at);
          } else {
            throw NotSupported($"{name}! of a {type} in a lambda (spec 13.3.1)", at);
          }
          break;
        }
        case "MoveCapture": {
          string field = $"c->v_{name}";
          var moved = Synthetic("Gift", at).Add(Synthetic("Name", at, name));
          if (type == "string") {
            fields.Append($"  df_string v_{name};\n");
            inits.Append($"c_->v_{name} = {GiftExpr(moved, scope).Code}; ");
            ends.Append($"  df_str_free({field});\n");
            clones.Append($"  n->v_{name} = df_str_dup(c->v_{name});\n");
            lambda.LambdaVars.Add((name, new VarInfo(type, field, false) { RefSlot = true }));
          } else if (IsArr(type) || IsClass(type) && !IsTraitType(type)) {
            OwnedCapture(name, type, GiftExpr(moved, scope).Code, fields, inits, ends, clones, lambda, at);
          } else if (IsFunType(type)) {
            if (outer.IsParam) {
              throw Error($"'{name}' is a parameter: it only borrows its function, ->{name} cannot take it", at);
            }
            fields.Append($"  df_fn v_{name};\n");
            inits.Append($"c_->v_{name} = {outer.CName}; {outer.CName} = (df_fn){{0}}; ");
            ends.Append($"  df_fn_release({field});\n");
            clones.Append($"  n->v_{name} = df_fn_clone(c->v_{name});\n");
            borrows |= outer.FnBorrows;
            lambda.LambdaVars.Add((name, new VarInfo(type, field, false) { FnBorrows = outer.FnBorrows, FnWrites = outer.FnWrites }));
          } else {
            throw NotSupported($"->{name} of a {type} in a lambda (spec 13.3.1)", at);
          }
          break;
        }
      }
    }
    failingFuns.Add(lambda);   // it returns its exception (CodeGen.ExcFlow.cs)
    pendingInstances.Add(lambda);
    if (borrows) {
      borrowingLambdas.Add(n);
    }
    if (captures.Any(capture => capture.Kind == "WritableCapture" || capture.Kind is "CopyCapture" or "MoveCapture" && capture.Outer.FnWrites)
        || members.Any(member => member.Kind == "WritableCapture")) {
      writingLambdas.Add(n);
    }
    string fnType = MakeFunType(ret, ps.Select(ParamTypeText));
    if (!hasContext) {
      return new Expr($"((df_fn){{ (void*){lambda.CName}, NULL }})", fnType);
    }
    lambdaHelpers.Append($"typedef struct {{\n  df_lhead h;\n{fields}}} {ctxName};\n\n");
    lambdaHelpers.Append($"static void df_lend_{id}(void* p) {{\n  {ctxName}* c = ({ctxName}*)p;\n  (void)c;\n{ends}}}\n\n");
    lambdaHelpers.Append($"static void* df_lclone_{id}(const void* p) {{\n  const {ctxName}* c = (const {ctxName}*)p;\n  {ctxName}* n = ({ctxName}*)df_alloc(sizeof({ctxName}));\n  *n = *c;\n  n->h.rc = 1;\n{clones}  return n;\n}}\n\n");
    string code = $"({{ {ctxName}* c_ = ({ctxName}*)df_alloc(sizeof({ctxName})); c_->h.rc = 1; c_->h.end = df_lend_{id}; c_->h.clone = df_lclone_{id}; {inits}"
      + $"(df_fn){{ (void*){lambda.CName}, &c_->h }}; }})";
    return new Expr(code, fnType, true);
  }

  // the counted object (or, for %a, also a weak reference) a @a, &@a, %a or &%a capture takes; &@a and &%a need a read/write source
  CountedRefInfo CountedSource(Node capture, Scope scope) {
    string name = capture.Text!;
    var named = Synthetic("Name", capture, name);
    bool weak = capture.Kind == "WeakCapture";
    bool writable = capture.Children.Any(child => child.Kind == "ReadWrite");
    string form = (writable ? "&" : "") + (weak ? "%" : "@") + name;
    if (CountedRef(named, scope) is not CountedRefInfo source) {
      if (WeakRef(named, scope) is CountedRefInfo weakSource) {
        if (!weak) {
          throw Error($"'{name}' is a weak reference: {form} needs the counted object; list %{name}, or take a usage inside enforce {name} (spec 13.3.1, 12.7)", capture);
        }
        source = weakSource;
      } else if (OuterOf(name, scope, capture) == null && scope.Find(name) == null) {
        throw Error($"unknown variable '{name}'", capture);
      } else {
        throw Error($"'{name}' is not counted: {form} takes a counted object (@T); capture it as {name}, &{name}, {name}! or ->{name} (spec 13.3.1)", capture);
      }
    }
    CheckWritableSource(source, writable, capture);
    return source;
  }

  // what a captured name is outside the lambda: a variable, or a field of the class of the method (borrowing it borrows the object, 13.3.1).
  // A field is seen through a VarInfo whose C is the field itself. Null when the name is neither.
  VarInfo? OuterOf(string name, Scope scope, Node at) {
    if (scope.Find(name) is VarInfo variable) {
      return variable;
    }
    if (currentClass == null || FindField(currentClass, name, at) is not FieldInfo field) {
      return null;
    }
    if (field.IsCounted && !field.HasAccessors || field.IsWeak) {
      throw Error(field.IsWeak ? $"'{name}' is a weak reference: list it as %{name} (spec 13.3.1)"
        : $"'{name}' is counted and cannot be borrowed or copied this way: list it as @{name} or %{name} (spec 13.3.1)", at);
    }
    if (field.HasAccessors || field.IsParentRef) {
      return null;   // a property or the parent: reached through the object (LambdaClassMember)
    }
    var bare = BareField(name, scope, at, true)!.Value;
    return new VarInfo(field.Type, bare.Code, field.IsConst || field.IsReadOnly && !initializing);
  }

  // a property or the reference to the parent of the class of the method, used by a lambda by its bare name (spec 13.3.1)
  FieldInfo? LambdaClassMember(string name, Scope scope) =>
    scope.Find(name) == null && currentClass != null && FindField(currentClass, name) is FieldInfo field && (field.HasAccessors || field.IsParentRef) ? field : null;

  // the listed forms of a property or the parent: &prop writes through its accessors, prop! copies the value it reads now; the parent is
  // only read (unlisted) or copied, and nothing can be moved out of them
  void CheckMemberCapture(FieldInfo member, Node capture) {
    string name = member.Name;
    switch (capture.Kind) {
      case "MoveCapture":
        throw Error(member.IsParentRef ? $"'{name}' is the reference to the parent: ->{name} cannot take it (spec 12.5)"
          : $"'{name}' is a property: ->{name} cannot take its value; copy it with {name}! (spec 13.3.1)", capture);
      case "WritableCapture" when member.IsParentRef:
        throw Error($"'{name}' is the reference to the parent: only the constructor sets it, &{name} cannot be captured (spec 12.5)", capture);
      case "WritableCapture" when !member.CanWrite:
        throw Error($"'{name}' cannot be assigned: it has no W:, so &{name} cannot be captured", capture);
    }
  }

  // a capture the lambda owns (a clone or a move): an object (dropped) or an array (its items end with it) when the lambda ends
  void OwnedCapture(string name, string type, string value, StringBuilder fields, StringBuilder inits, StringBuilder ends, StringBuilder clones, FunSig lambda, Node at) {
    string cType = CType(type);
    if (IsArr(type)) {
      fields.Append($"  {cType} v_{name};\n");
      inits.Append($"c_->v_{name} = {value}; ");
      ends.Append($"  {ArrDropHelper(type)}(c->v_{name});\n");
      clones.Append($"  n->v_{name} = {ArrCloneHelper(type, at)}(c->v_{name});\n");
      lambda.LambdaVars.Add((name, new VarInfo(type, $"c->v_{name}", false) { OwnsArray = true }));
      return;
    }
    fields.Append($"  void* v_{name};\n");
    inits.Append($"c_->v_{name} = (void*)({value}); ");
    ends.Append($"  df_drop(c->v_{name});\n");
    cloneUsed = true;   // a clone of the lambda clones its object through the class
    clones.Append($"  n->v_{name} = df_clone_obj(c->v_{name});\n");
    lambda.LambdaVars.Add((name, new VarInfo(type, $"(*({cType}*)&c->v_{name})", false) { Own = true }));
  }

  // the names of variables a body uses (a nested lambda counts: it captures them through this one)
  static void CollectNames(Node node, List<string> names) {
    if (node.Kind == "Name" && node.Text != null) {
      names.Add(node.Text);
    }
    foreach (var child in node.Children) {
      CollectNames(child, names);
    }
  }

  // a write to a const, or to a variable a lambda captured without listing it
  static CompileException ConstWrite(LValueInfo target, Node targetNode, Node at) =>
    target.Variable is { CapturedReadOnly: true }
      ? Error($"'{targetNode.Text}' is captured read-only by the lambda: list it as &{targetNode.Text} to change it (spec 13.3.1)", at)
      : target.Variable is { IsConst: true } && target.Variable.CName.StartsWith("(*(c_")
        ? Error($"'{targetNode.Text}' is captured with & by the lambda: its fields can be changed, the variable cannot be replaced (spec 13.3.1)", at)
        : target.Variable is { ReadOnlyStruct: true } && targetNode.Text == "this"
          ? Error("an unrestricted method only reads its struct: it cannot assign this (spec 11.1.5.2)", at)
          : Error($"'{targetNode.Text}' is const", at);

  // ---------------------------------------------------------------- function values

  // the value may borrow (a lambda with a borrowed capture, a variable or parameter holding one): it cannot be stored past its variables
  bool FnMayBorrow(Node value, Scope scope) => value.Kind switch {
    "Lambda" => borrowingLambdas.Contains(value),
    "Name" => scope.Find(value.Text!)?.FnBorrows ?? false,
    "Paren" => FnMayBorrow(value.Children[0], scope),
    "Call" or "Null" => false,
    "Member" or "Index" => false,              // a field or an item keeps only values that do not borrow
    "Copy" => FnMayBorrow(value.Children[0], scope),
    _ => true
  };

  // the value may hold a & capture (a lambda written with one, or a variable that keeps one): f! of it is a compile error (spec 13.3.3)
  bool FnMayWrite(Node value, Scope scope) => value.Kind switch {
    "Lambda" => writingLambdas.Contains(value),
    "Name" => scope.Find(value.Text!)?.FnWrites ?? false,
    "Paren" => FnMayWrite(value.Children[0], scope),
    _ => false
  };

  // a usage of the function value for a variable or a result: a new lambda is taken as it is, an existing value gets one more usage
  static string FnOwned(Expr value) =>
    value.Type == "null" ? "((df_fn){0})" : value.Fresh ? value.Code : $"df_fn_retain({value.Code})";

  // a function-type local declared with its type: its usage ends with the scope
  static bool IsFunDecl(Node decl) =>
    decl.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind)) is Node typeNode
    && (typeNode.Kind == "FunType" || typeNode.Kind == "ResolvedType" && typeNode.Text!.StartsWith("fun("));   // also T gone = ->items[i] of a collection of function values

  string FnDeclCode(Node decl, Expr? initValue, string code) {
    if (!IsFunDecl(decl)) {
      throw Error($"write the function type of '{decl.Text}': a lambda is kept in a variable declared with it, as fun(i32) {decl.Text} = ... (spec 13.3.2)", decl);
    }
    return initValue is Expr value ? FnOwned(value) : code;
  }

  string FnAssign(LValueInfo target, Node targetNode, string op, Node valueNode, Scope scope, Node at) {
    if (op != "=") {
      throw Error($"'{op}' is not defined on a function value", at);
    }
    if (target.Variable == null && !target.IsField && !target.IsStatic) {
      throw NotSupported("a function value in this place (spec 13.3.2)", at);
    }
    if (target.Variable is { IsParam: true }) {
      throw Error($"'{targetNode.Text}' is a parameter: it is read-only", at);
    }
    var value = ExprOf(valueNode, scope, target.Type);
    string converted = Convert(value, target.Type, valueNode);
    if (FnMayBorrow(valueNode, scope)) {
      throw Error($"this function value borrows variables, so it cannot be kept in '{targetNode.Text}', which may outlive them: {(target.Variable == null ? "a field keeps only lambdas that own their captures: capture" : "declare a new variable for it, or capture")} with x! or ->x (spec 13.3.4)", at);
    }
    MarkAssigned(target.Variable);
    return $"df_fn_set(&{target.Code}, {FnOwned(value with { Code = converted })})";
  }

  string FnReturned(Expr value, Node expression, Scope scope, Node at) {
    if (FnMayBorrow(expression, scope)) {
      throw Error("this function value borrows variables of the function, so it cannot be returned: capture them with x! or ->x (spec 13.3.4)", at);
    }
    return FnOwned(value with { Code = Convert(value, currentRet!, at) });
  }

  // the argument of a function-type parameter: a borrow; a new lambda ends with the statement
  string FnArg(ParamSig param, Node arg, Scope scope) {
    if (param.IsGift || IsByPointer(param) || arg.Kind is "Gift" or "WritableAddressOf") {
      throw NotSupported("&, -> and the other markers on a function-type parameter (spec 13.3.2)", arg);
    }
    var value = ExprOf(arg, scope, param.Type);
    string converted = Convert(value, param.Type, arg);
    if (!value.Fresh) {
      return converted;
    }
    fn!.UsesTmp = true;
    return $"df_fn_tmp({converted})";
  }

  // f(args) where f is a function value: a null one raises UnassignedFunctionCallException (spec 13.3.2)
  Expr FnValueCall(Expr callee, List<Node> args, Scope scope, Node at) {
    var (ret, types) = FunParts(callee.Type);
    if (args.Any(arg => arg.Kind == "Named")) {
      throw Error("a function value takes its arguments by position", at);
    }
    if (args.Count != types.Count) {
      throw Error($"this function takes {types.Count} argument{(types.Count == 1 ? "" : "s")}, not {args.Count} ({FunDisplay(callee.Type)})", at);
    }
    var codes = new List<string>();
    for (int index = 0; index < args.Count; index++) {
      codes.Add(ArgCode(ParamFromFunType($"argument {index + 1}", types[index]), args[index], scope));
    }
    codes = ArgsInOrder(args.Cast<Node?>().ToList(), codes, out string prefix);
    string fnCode = callee.Code;
    if (callee.Fresh) {
      fn!.UsesTmp = true;
      fnCode = $"df_fn_tmp({fnCode})";
    }
    string temp = NewTemp();
    string call = PairCheck(LambdaCall(temp, ret, types.Select(type => ParamCType(ParamFromFunType("p", type))), codes), ret);
    string code = $"({{ {prefix}df_fn {temp} = {fnCode}; if (!{temp}.code) {{ df_raise(&df_desc_UnassignedFunctionCallException, {fn!.NameConst}); {RaiseJump} }} {call}; }})";
    bool fresh = ret == "string" || IsClass(ret) || IsArr(ret) || ret == "string?" || IsFunType(ret);
    return new Expr(code, ret, fresh);
  }

  // the C call of the lambda held in the df_fn 'held': it returns its result with its exception (a df_res pair, or the exception alone)
  string LambdaCall(string held, string ret, IEnumerable<string> cTypes, IEnumerable<string> args) {
    string retC = ret == "void" ? "df_exception*" : ResultPair(CType(ret));
    return $"(({retC} (*)({string.Join(", ", new[] { "void*" }.Concat(cTypes))})){held}.code)({string.Join(", ", new[] { $"(void*){held}.ctx" }.Concat(args))})";
  }

  // a call of a lambda whose exception, when it is not ignored (a silent one, spec 15.4), first runs 'cleanup' (what was built so far ends)
  string CheckedLambdaCall(string held, string ret, IEnumerable<string> cTypes, IEnumerable<string> args, string cleanup) {
    string temp = NewTemp();
    string call = LambdaCall(held, ret, cTypes, args);
    if (ret == "void") {
      return $"({{ df_exception* {temp} = {call}; if ({temp}) {{ if ({SilentIgnored(temp)}) {{ df_exc_release({temp}); }} "
        + $"else {{ {cleanup}df_err = {temp}; df_err_thrown = false; goto {Handler}; }} }} }})";
    }
    string err = temp + ".err";
    return $"({{ {ResultPair(CType(ret))} {temp} = {call}; if ({err}) {{ if ({SilentIgnored(err)}) {{ df_exc_release({err}); {err} = NULL; }} "
      + $"else {{ {cleanup}df_err = {err}; df_err_thrown = false; goto {Handler}; }} }} {temp}.value; }})";
  }

  // a call whose callee is a function value: a variable or parameter of a function type, a lambda, a call that returns one. Null otherwise.
  Expr? TryFnValueCall(Node callee, List<Node> args, Scope scope, Node at) {
    if (callee.Kind == "Name" && scope.Find(callee.Text!) is VarInfo variable && IsFunType(variable.Type)) {
      return FnValueCall(new Expr(variable.CName, variable.Type), args, scope, at);
    }
    // a field of a function type: onKey(5) inside a method of the class, obj.onKey(5), this.onKey(5) (spec 13.3.2)
    if (callee.Kind == "Name" && scope.Find(callee.Text!) == null && currentClass != null && FindField(currentClass, callee.Text!) is { } own
        && IsFunType(own.Type) && LookupMethods(currentClass, callee.Text!).Count == 0) {
      return FnValueCall(ExprOf(callee, scope), args, scope, at);
    }
    // an item of a tuple: t.f(3)
    if (callee.Kind == "Member" && Path(callee.Children[0]) is string tuplePath && scope.Find(tuplePath.Split('.')[0]) is VarInfo { } holder
        && IsTuple(holder.Type) && ExprOf(callee.Children[0], scope) is { } tuple && IsTuple(tuple.Type)
        && TupleElems(tuple.Type).Any(item => item.Name == callee.Text && IsFunType(item.Type))) {
      return FnValueCall(ExprOf(callee, scope), args, scope, at);
    }
    if (callee.Kind == "Member" && FunFieldNames.Contains(callee.Text!) && Path(callee.Children[0]) is string path
        && (scope.Find(path.Split('.')[0]) != null || currentClass != null && FindField(currentClass, path.Split('.')[0]) != null)) {
      var receiver = ExprOf(callee.Children[0], scope);
      if (IsClass(receiver.Type) && FindField(ObjInfo(receiver.Type), callee.Text!) is { } field && IsFunType(field.Type)
          && LookupMethods(ObjInfo(receiver.Type), callee.Text!).Count == 0) {
        return FnValueCall(ExprOf(callee, scope), args, scope, at);
      }
    }
    if (callee.Kind is "Lambda" or "Paren" or "Call" or "Index") {
      var value = ExprOf(callee, scope);
      if (!IsFunType(value.Type)) {
        throw Error($"this {value.Type} is not a function: it cannot be called", at);
      }
      return FnValueCall(value, args, scope, at);
    }
    return null;
  }

  // sort(f), filter(f), map<U>(f), reduce(x, f): the member calls the function it is given
  static bool FlowCallsGivenFunction(Node call) {
    var callee = call.Children[0].Kind == "GenericRef" ? call.Children[0].Children[0] : call.Children[0];
    return callee is { Kind: "Member", Text: "sort" or "sortUnstable" or "filter" or "map" or "reduce" }
      && call.Children[1].Children.Any(arg => arg.Kind is "Lambda" or "Name");
  }

  // the analysis of exceptions: a call through a function value may raise UnassignedFunctionCallException. The analysis has no types,
  // so any call of a name that is not a function, a class or a library function counts.
  bool FlowMayCallFunValue(Node callee) {
    if (callee.Kind is "Lambda" or "Paren" or "Call" or "Index") {
      return true;
    }
    if (callee.Kind == "Member" && FunFieldNames.Contains(callee.Text!)) {
      return true;   // obj.onKey(5): a field of a function type (the analysis has no types)
    }
    if (callee.Kind != "Name") {
      return false;
    }
    string name = callee.Text!;
    if (funs.ContainsKey(name) || genericFuns.ContainsKey(name) || classes.ContainsKey(name) || traits.ContainsKey(name) || Stdlib.ContainsKey(name)
        || name is "string" or "Panic" or "date" or "time" || IsEnum(name) || ExceptionId(name) != null || nameAliases.ContainsKey(name) || typeAliases.ContainsKey(name)) {
      return false;
    }
    return currentFlowFun?.Class is not ClassInfo cls || LookupMethods(cls, name).Count == 0;
  }

  // the names of the fields of a function type of every class: obj.name(...) may call a function value
  HashSet<string>? funFieldNames;
  HashSet<string> FunFieldNames => funFieldNames ??= classes.Values.SelectMany(cls => cls.Fields).Where(member => IsFunType(member.Type))
    .Select(member => member.Name).ToHashSet();

  // a collection keeps its items: a function value that borrows variables cannot go in (spec 13.3.4)
  void CheckStoredFunction(FunSig f, ParamSig param, Node arg, Scope scope) {
    if (IsFunType(param.Type) && f.Node.Line >= LibraryLine && !LibraryCode && FnMayBorrow(arg, scope)) {
      throw Error(BorrowedIntoStore("a collection"), arg);
    }
  }

  // a function value that borrows, stored where it may outlive what it borrows
  void CheckNotBorrowingFunction(string type, Node value, Scope scope, string where) {
    if (IsFunType(type) && !LibraryCode && FnMayBorrow(value, scope)) {
      throw Error(BorrowedIntoStore(where), value);
    }
  }

  static string BorrowedIntoStore(string where) =>
    $"this function value borrows variables, so {where} cannot keep it: it may outlive them; capture with x! or ->x (spec 13.3.4)";
}
