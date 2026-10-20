using System.Globalization;
using System.Numerics;
using System.Text;
using dfparser;

namespace dfcompiler;

public sealed class CompileException : Exception {
  public CompileException(string message, int line) : base(message) {
    Line = line;
  }

  public int Line { get; }
}

sealed class FunSig {
  public FunSig(string name, string ret, List<ParamSig> ps, Node node, string? enumName) {
    Name = name;
    Ret = ret;
    Params = ps;
    Node = node;
    EnumName = enumName;
    Root = this;
  }

  public string Name { get; }
  public int OverloadIndex { get; set; }
  public string CName => "f_" + Name.Replace('.', '_').Replace("==", "op_eq") + (OverloadIndex > 0 ? "_" + OverloadIndex : "");
  public string Ret { get; }
  public List<ParamSig> Params { get; }
  public Node Node { get; }
  public string? EnumName { get; }

  // classes
  public ClassInfo? Class { get; set; }
  public bool IsStatic { get; set; }
  public bool IsOverride { get; set; }
  public bool IsCtor { get; set; }
  public bool IsInit { get; set; }
  public bool IsStaticInit { get; set; }
  public Vis Visibility { get; set; } = Vis.Public;
  public int Slot { get; set; } = -1;
  public bool ReturnsOwned { get; set; }      // returns new or moved objects (false: borrowed ones)
  public bool ReturnsOwnedSet { get; set; }
  public bool IsDtor { get; set; }            // the atEnd of a class
  // can a silent throw leave this function? Constructors, init functions, destructors, == operators and the entry point never return
  // a silent exception (spec 15.7.6): there it is seen only by a catch of the same function and otherwise does nothing
  public bool CanEmitSilent => !IsCtor && !IsInit && !IsStaticInit && !IsDtor && !IsAbstract && GenericT == null && !Name.EndsWith(".==") && Name != "main";
  public bool IsAbstract { get; set; }   // a trait function without a body
  public int TSlot { get; set; } = -1;   // position among the functions of its trait
  public string? GenericT { get; set; } // a copy of equals(T other) made for this T
  public FunSig Root { get; set; }
  public int GroupSize { get; set; }   // on a root: how many functions override it
  public int InstanceDepth { get; set; }      // a copy of a generic function: how deep the chain of copies is
  public string InstanceChain { get; set; } = "";
}

sealed class ParamSig {
  public ParamSig(string name, string type, Node? def) {
    Name = name;
    Type = type;
    Default = def;
  }

  public string Name { get; }
  public string Type { get; }
  public Node? Default { get; }
  public bool IsVariadic { get; init; }   // T name...: an arr<T> filled from the extra arguments
  public bool IsGift { get; init; }       // ->T name: the function owns the object it is given
  public int Ref { get; init; }           // 0 a copy, 1 &T name (read-only reference), 2 (&)T name (writable reference, the call writes (&)x)
}

sealed class VarInfo {
  public VarInfo(string type, string cname, bool isConst) {
    Type = type;
    CName = cname;
    IsConst = isConst;
  }

  public string Type { get; }
  public string CName { get; }
  public bool IsConst { get; }
  public bool Own { get; set; }       // the variable owns its object: it is destroyed when the scope ends
  public bool IsWeak { get; set; }    // a weak reference: used only through enforce
  public long? KnownLength { get; set; }   // an array that starts from a literal or arr<T>[n] and is never assigned again: its length is known
}

sealed class Scope {
  public Scope(Scope? parent) {
    Parent = parent;
  }

  public Scope? Parent { get; }
  public Dictionary<string, VarInfo> Vars { get; } = new();

  public VarInfo? Find(string name) {
    for (var s = this; s != null; s = s.Parent) {
      if (s.Vars.TryGetValue(name, out var v)) {
        return v;
      }
    }
    return null;
  }
}

// Type strings: "i32", "string", "Status" (enum), "i32?" (nullable),
// "(i32 id,string name)" (tuple), "*(i32,string)" (multiple return values), "null" (the null literal)
// Fresh: the value is a new object or a moved one (its owner is whoever receives it)
readonly record struct Expr(string Code, string Type, bool Fresh = false);

// Type checks a Dflat syntax tree (first subset) and emits C.
// Split in partial files: CodeGen.Types.cs, CodeGen.Switch.cs, CodeGen.Nulls.cs, CodeGen.Tuples.cs
public sealed partial class CodeGen {
  static readonly HashSet<string> SignedTypes = new() { "i8", "i16", "i32", "i64" };
  static readonly HashSet<string> UnsignedTypes = new() { "u8", "u16", "u32", "u64" };
  static readonly HashSet<string> RealTypes = new() { "r32", "r64" };
  static readonly HashSet<string> TypeNodeKinds = new() {
    "Type", "ResolvedType", "Nullable", "TupleType", "FunType", "Pointer", "WritablePointer", "Volatile", "Specific", "Any"
  };

  static readonly Dictionary<string, string> MathConsts = new() {
    ["PI"] = "3.141592653589793",
    ["PI_HALF"] = "1.5707963267948966",
    ["PI2"] = "6.283185307179586",
    ["E"] = "2.718281828459045",
    ["SQRT2"] = "1.4142135623730951",
    ["R2D"] = "57.29577951308232",
    ["D2R"] = "0.017453292519943295"
  };

  readonly Dictionary<string, List<FunSig>> funs = new();   // name → overloads
  readonly Dictionary<string, string> nameAliases = new();
  readonly Dictionary<string, string> typeAliases = new();
  readonly List<string> warnings = new();
  string? currentRet;
  int loopDepth;
  bool specWarned;
  int tempCounter;

  public IReadOnlyList<string> Warnings => warnings;

  static CompileException Error(string msg, Node at) {
    return new CompileException(msg, at.Line);
  }

  // level 1 = most important, 3 = least (spec 19.1); 0 = tool notice without a level
  void Warn(string msg, Node at, int level = 0) {
    if (at.Line >= LibraryLine) {
      return;   // code of the compiler's own libraries (vec): not the program's business
    }
    string tag = level > 0 ? $"warning L{level}" : "warning";
    warnings.Add($"({at.Line}): {tag}: {msg}");
  }

  string NewTemp() {
    tempCounter++;
    return "t_" + tempCounter;
  }

  // a node built by the compiler (not from source), reported at the line of 'at'
  static Node Synthetic(string kind, Node at, string? text = null) {
    return new Node(kind, new Token(TokenKind.Ident, text ?? "", at.Line, 0), text);
  }

  // ---------------------------------------------------------------- entry

  public string Generate(Node program) {
    CheckTopLevelFeatures(program);
    AddPrelude(program);
    ProgramNode = program;
    definedTypes.Add("df_arr_string");   // defined by the runtime
    definedTypes.Add("df_arr_u8");
    foreach (var statement in program.Children) {
      if (statement.Kind == "Enum") {
        DeclareEnum(statement);
      }
    }
    RegisterGenerics(program);
    DeclareClasses(program);
    foreach (var statement in program.Children) {
      if (statement.Kind == "Alias") {
        RegisterAlias(statement);
      }
    }
    foreach (var statement in program.Children) {
      if (statement.Kind == "Enum") {
        RegisterEnum(statement);
      }
    }
    RegisterClasses();
    CheckClassInstances();
    foreach (var statement in program.Children) {
      if (statement.Kind == "Fun") {
        if (IsGenericFunNode(statement)) {
          RegisterGenericFun(statement);
        } else {
          RegisterFun(statement, null);
        }
      }
    }

    ClassifyReturns();
    FindFailingFunctions();

    var protos = new StringBuilder();
    var bodies = new StringBuilder();
    // (a snapshot: compiling a body can make copies of generic functions, which are added to funs)
    foreach (var fun in funs.Values.SelectMany(overloads => overloads).Where(fun => !fun.IsAbstract).ToList()) {
      protos.Append(Signature(fun)).Append(";\n");
      bodies.Append(fun.IsCtor ? EmitConstructor(fun) : EmitFun(fun)).Append('\n');
    }

    // a main function is the entry point (spec 14.1); otherwise the top-level statements are
    var topLevel = program.Children.Where(statement =>
      statement.Kind is not ("Fun" or "Alias" or "Enum" or "Class" or "Trait" or "GenericSet" or "Namespace" or "NamespaceLocal")).ToList();
    if (funs.TryGetValue("main", out var mains)) {
      if (topLevel.Count > 0) {
        throw Error("a program starts either from top-level code or from a main function, not both", topLevel[0]);
      }
      return Assemble(protos, bodies, EntryPoint(mains));
    }

    // top-level statements: main(); an exception reaching the top level is a panic
    fn = NewFunctionContext("main()", "i32", true);
    assign = new AssignState();
    currentRet = null;
    var statements = program.Children.Where(statement => statement.Kind is not ("Fun" or "Alias" or "Enum" or "Class" or "Trait" or "GenericSet")).ToList();
    var root = PrepareFrame(statements, new Scope(null), FrameKind.Function);
    root.OuterHandler = fn.PanicLabel;
    var mainBody = new StringBuilder();
    EmitHoists(root, mainBody, "  ");
    EmitFrameBody(root, mainBody, 1);
    var main = new StringBuilder();
    main.Append("int main(int argc, char** argv) {\n");
    main.Append("  df_init();\n  df_set_args(argc, argv);\n");
    main.Append(StaticInitCode());
    main.Append(fn.Decls);
    if (fn.UsesRetVar) {
      main.Append($"  int32_t {fn.RetVar} = 0;\n");
    }
    main.Append(mainBody);
    main.Append("  return 0;\n");
    main.Append($"  {fn.PanicLabel}: df_panic_unhandled(df_err);\n");
    main.Append("  return 0;\n}\n");
    fn = null;
    return Assemble(protos, bodies, main);
  }

  string Assemble(StringBuilder protos, StringBuilder bodies, StringBuilder main) {
    // the copies of equals the program needs (found while compiling everything else), then the class tables
    while (pendingInstances.Count > 0) {
      var instance = pendingInstances[0];
      pendingInstances.RemoveAt(0);
      protos.Append(Signature(instance)).Append(";\n");
      bodies.Append(EmitFun(instance)).Append('\n');
    }
    EmitClassTables();
    var output = new StringBuilder();
    output.Append("/* generated by dfcompiler */\n");
    output.Append(Runtime.Source).Append('\n');
    output.Append(OptionalRuntime());
    output.Append("/* ---- program ---- */\n\n");
    output.Append(typeDefs);
    output.Append(ObservedBitTable());
    output.Append(functionNames).Append('\n');
    output.Append(enumCode);
    output.Append(arrayCode);
    output.Append(classGlobals).Append('\n');
    output.Append(protos).Append('\n');
    output.Append(classCode);
    output.Append(bodies);
    output.Append(main);
    return output.ToString();
  }

  // the C main that calls the Dflat main function (spec 14.1): main(), main(arr<string> args), main(string args...)
  StringBuilder EntryPoint(List<FunSig> mains) {
    if (mains.Count > 1) {
      throw Error("more than one main function", mains[1].Node);
    }
    var entry = mains[0];
    if (entry.Params.Count > 1 || (entry.Params.Count == 1 && entry.Params[0].Type != "arr<string>")) {
      throw Error("main takes no parameters, or arr<string> args, or string args...", entry.Node);
    }
    if (entry.Ret is not ("void" or "i32")) {
      throw Error("main returns int or nothing", entry.Node);
    }
    var main = new StringBuilder();
    main.Append("int main(int argc, char** argv) {\n");
    main.Append("  df_init();\n  df_set_args(argc, argv);\n");
    main.Append(StaticInitCode());
    var entryArgs = new List<string>();
    if (entry.Params.Count == 1) {
      entryArgs.Add("df_args");
    }
    string call = $"{entry.CName}({string.Join(", ", entryArgs)})";
    if (PairReturn(entry)) {
      // main returns its exit code with its exception: an exception nobody handled is a panic
      if (entry.Ret == "void") {
        main.Append($"  df_exception* error = {call};\n");
      } else {
        main.Append($"  {ResultPair("int32_t")} result = {call};\n  df_exception* error = result.err;\n  int32_t code = result.value;\n");
      }
      main.Append("  if (error) {\n    df_panic_unhandled(error);\n  }\n");
    } else if (entry.Ret == "void") {
      main.Append($"  {call};\n");
    } else {
      main.Append($"  int32_t code = {call};\n");
    }
    main.Append(entry.Ret == "void" ? "  return 0;\n}\n" : "  return (int)code;\n}\n");
    return main;
  }

  void RegisterAlias(Node a) {
    var target = a.Children[0];
    if (target.Kind != "Type" || target.Children.Count > 0) {
      throw Error("alias target must be a plain name or type", a);
    }
    string t = target.Text!;
    if (IsKnownType(t)) {
      typeAliases[a.Text!] = Normalize(t);
    } else {
      nameAliases[a.Text!] = t;
    }
  }

  // enumName: set for static methods declared inside an enum
  FunSig? RegisterFun(Node funNode, string? enumName, ClassInfo? cls = null) {
    string name = cls != null ? cls.Name + "." + funNode.Text : enumName == null ? funNode.Text! : enumName + "." + funNode.Text;
    if (cls == null && enumName == null && classes.ContainsKey(name)) {
      throw Error($"a function cannot be named like the class '{name}'", funNode);
    }
    foreach (var child in funNode.Children) {
      if (child.Kind is "GenericParams" or "Where") {
        throw NotSupported("Generics", child);
      }
    }
    string ret = "void";
    var rets = funNode.Children.FirstOrDefault(child => child.Kind == "Returns");
    if (rets != null) {
      if (rets.Children.Count == 1) {
        // the exit code of main is an int (native size is not supported yet: i32)
        bool isMainInt = enumName == null && funNode.Text == "main" && rets.Children[0] is { Kind: "Type", Text: "int" };
        ret = isMainInt ? "i32" : ResolveType(rets.Children[0]);
      } else {
        ret = "*" + MakeTuple(rets.Children.Select(typeNode => (ResolveType(typeNode), (string?)null)));
      }
    }
    var ps = new List<ParamSig>();
    var paramsNode = funNode.Children.First(child => child.Kind == "Params");
    foreach (var p in paramsNode.Children) {
      if (ParamFeatures.TryGetValue(p.Kind, out var feature)) {
        throw NotSupported(feature, p);
      }
      if (p.Kind == "VariadicParam" && p != paramsNode.Children[^1]) {
        throw Error("a variadic parameter (T name...) must be the last one", p);
      }
      if (p.Children.Any(child => child.Kind == "Volatile")) {
        throw NotSupported("Pointers", p);
      }
      if (ps.Any(existing => existing.Name == p.Text)) {
        throw Error($"duplicate parameter '{p.Text}'", p);
      }
      var def = p.Children.FirstOrDefault(child => child.Kind == "Default");
      string paramType = ResolveType(p.Children[0]);
      bool variadic = p.Kind == "VariadicParam";
      int reference = p.Kind == "RefParam" ? 1 : p.Kind == "WritableRefParam" ? 2 : 0;
      if (reference == 2 && def != null) {
        throw Error($"the writable reference '{p.Text}' cannot have a default value: it needs a variable", p);
      }
      ps.Add(new ParamSig(p.Text!, variadic ? "arr<" + paramType + ">" : paramType, def?.Children[0]) {
        IsVariadic = variadic,
        IsGift = p.Kind == "GiftParam" && IsClass(paramType),
        Ref = reference
      });
    }
    if (funNode.Children.Any(child => child.Kind == "NoBody") && cls?.IsTrait != true) {
      throw Error("functions without a body are only valid in traits", funNode);
    }
    if (!funs.TryGetValue(name, out var overloads)) {
      overloads = new List<FunSig>();
      funs[name] = overloads;
    }
    // overloads differ by number, order or types of parameters; the return type alone doesn't count
    if (overloads.Any(existing => existing.Params.Select(param => param.Type).SequenceEqual(ps.Select(param => param.Type)))) {
      throw Error($"function '{name}' with these parameter types is already defined", funNode);
    }
    var fun = new FunSig(name, ret, ps, funNode, enumName) { OverloadIndex = overloads.Count };
    overloads.Add(fun);
    return fun;
  }

  // references to objects are the object pointer itself; every other type is passed by the address of the variable
  bool IsByPointer(ParamSig p) => p.Ref != 0 && !IsClass(p.Type);
  string ParamCType(ParamSig p) => IsByPointer(p) ? (p.Ref == 1 ? "const " : "") + CType(p.Type) + "*" : CType(p.Type);
  string ParamName(ParamSig p) => (IsByPointer(p) ? "p_" : "v_") + p.Name;

  string Signature(FunSig f) {
    var list = f.Params.Select(p => $"{ParamCType(p)} {ParamName(p)}").ToList();
    if (f.Class != null && !f.IsStatic && !f.IsCtor) {
      list.Insert(0, $"{CType(f.Class.Name)} self");
    }
    string ps = list.Count == 0 ? "void" : string.Join(", ", list);
    return $"static {RetCType(f)} {f.CName}({ps})";
  }

  string EmitFun(FunSig f) {
    fn = NewFunctionContext(FunHeader(f), f.Ret, false);
    fn.PairOf = PairReturn(f) ? f : null;
    assign = new AssignState();
    currentRet = f.Ret;
    currentEnum = f.EnumName;
    currentClass = f.Class;
    currentFun = f;
    currentGenericT = f.GenericT;
    initializing = f.IsInit || f.IsStaticInit;
    var scope = new Scope(null);
    if (f.Class != null && !f.IsStatic) {
      scope.Vars["this"] = new VarInfo(f.Class.Name, "self", true);
    }
    foreach (var p in f.Params) {
      // a reference parameter is a pointer in C; the body reads and writes through it, a read-only one cannot be assigned
      scope.Vars[p.Name] = IsByPointer(p)
        ? new VarInfo(p.Type, $"(*{ParamName(p)})", p.Ref == 1)
        : new VarInfo(p.Type, "v_" + p.Name, false) { Own = p.IsGift };
    }
    var block = f.Node.Children.First(c => c.Kind == "Block");
    // the atEnd after the function body is its final cleanup; a parameter taken with -> ends with the function
    var statements = new List<Node>(block.Children);
    var trailing = f.Node.Children.FirstOrDefault(c => c.Kind == "AtEnd" && c.Text == "trailing");
    if (trailing != null) {
      statements.Insert(0, Synthetic("AtEnd", trailing).Add(trailing.Children[0]));
    }
    foreach (var p in f.Params.Where(p => p.IsGift)) {
      statements.Insert(0, DropNode(p.Name, f.Node));
    }
    var root = PrepareFrame(statements, scope, FrameKind.Function);
    root.OuterHandler = fn.FailLabel;
    var body = new StringBuilder();
    // single-expression body returns its value
    bool singleValue = f.Ret != "void" && !IsMulti(f.Ret);
    if (singleValue && statements.Count == 1 && statements[0].Kind == "ExprStmt") {
      frames.Push(root);
      root.CurrentIndex = 0;
      var e = Convert(ExprOf(block.Children[0].Children[0], scope, f.Ret), f.Ret, block.Children[0]);
      body.Append(fn.PairOf != null ? $"  return {PairValue(f, e)};\n" : $"  return {e};\n");
      frames.Pop();
    } else {
      if (f.IsInit) {
        body.Append(InitPrologue(f.Class!));
      }
      EmitHoists(root, body, "  ");
      EmitFrameBody(root, body, 1);
      if (f.GenericT != null && !assign.Unreachable) {
        throw Error($"every path of equals must return a value (checked for T = {f.GenericT})", f.Node);
      }
    }

    var sb = new StringBuilder();
    sb.Append(Signature(f)).Append(" {\n");
    sb.Append(fn.Decls);
    if (fn.UsesRetVar) {
      sb.Append($"  {CType(f.Ret)} {fn.RetVar} = {ZeroValue(f.Ret)};\n");
    }
    sb.Append(body);
    // an exception leaving the function is returned with it. A destructor leaves it for the runtime that called it; a function that
    // returns only its result cannot let one out (the analysis said so): reaching here is an internal error, a panic.
    string failValue = f.Ret == "void" ? "" : " " + ZeroValue(f.Ret);
    if (fn.PairOf != null) {
      sb.Append($"  {fn.FailLabel}: return {PairError(f)};\n");
    } else {
      sb.Append($"  return{failValue};\n  {fn.FailLabel}: df_panic_unhandled(df_err);\n");
    }
    sb.Append($"  {fn.PanicLabel}: df_panic_unhandled(df_err);\n");
    if (fn.PairOf != null) {
      sb.Append($"  return {PairError(f)};\n");
    } else if (f.Ret != "void") {
      sb.Append($"  return{failValue};\n");
    }
    sb.Append("}\n");
    currentRet = null;
    currentEnum = null;
    currentClass = null;
    currentFun = null;
    currentGenericT = null;
    initializing = false;
    fn = null;
    return sb.ToString();
  }

  // ---------------------------------------------------------------- statements

  static string Pad(int indent) => new(' ', indent * 2);

  void EmitStmt(Node s, StringBuilder sb, Scope scope, int indent) {
    string pad = Pad(indent);
    if (s.Children.Any(child => child.Kind == "AtEnd" && child.Text == "trailing")) {
      throw NotSupported("atEnd after a statement (14.2, form 3): only after a function or a class", s);
    }
    switch (s.Kind) {
      case "Namespace":
      case "NamespaceLocal":
        sb.Append($"{pad}/* {s.Kind} {s.Text} */\n");
        return;

      case "Decl":
        EmitDecl(s, sb, scope, pad);
        return;

      case "Assign":
        sb.Append(pad).Append(AssignCode(s, scope)).Append(";\n");
        return;

      case "MultiAssign":
        EmitMultiAssign(s, sb, scope, pad);
        return;

      case "ExprStmt": {
        if (s.Children[0].Kind == "Finalize") {
          EmitFinalize(s.Children[0], sb, scope, pad);
          return;
        }
        var e = ExprOf(s.Children[0], scope);
        string k = s.Children[0].Kind;
        if (k == "Call" && e.Fresh && IsClass(e.Type)) {
          // a new object nobody keeps ends here
          sb.Append(pad).Append(CheckRaise($"df_drop((void*)({e.Code}))", "void")).Append(";\n");
        } else if (k is "Call" or "PostInc" or "PostDec" or "PreInc" or "PreDec") {
          // a result nobody keeps: say so to the C compiler (a call that checks an exception is a statement expression)
          sb.Append(pad).Append(e.Type == "void" || IsMulti(e.Type) ? e.Code : $"(void)({e.Code})").Append(";\n");
        } else {
          Warn("expression result is not used", s);
          sb.Append(pad).Append($"(void)({e.Code});\n");
        }
        return;
      }

      case "Block":
        sb.Append(pad).Append("{\n");
        EmitBlock(s, sb, scope, indent + 1);
        sb.Append(pad).Append("}\n");
        return;

      case "If":
        if (TypeBranch(s, sb, scope, indent)) {
          return;
        }
        EmitIf(s, sb, scope, indent, false);
        return;

      case "IfThen": {
        if (TypeBranch(s, sb, scope, indent)) {
          return;
        }
        var thenScope = scope;
        string condition;
        if (s.Children[0].Kind == "Is" && s.Children[0].Text != null) {
          (condition, thenScope) = IsBinding(s.Children[0], scope);   // if x is Circle c then ...
        } else {
          condition = BoolOf(ExprOf(s.Children[0], scope), s);
        }
        sb.Append($"{pad}if ({condition}) {{\n");
        var before = assign.Clone();
        EmitStatementAsBlock(s.Children[1], sb, thenScope, indent + 1);
        assign = AssignState.Merge(new[] { assign, before });
        sb.Append(pad).Append("}\n");
        return;
      }

      case "While":
        EmitWhile(s, sb, scope, indent, false);
        return;

      case "DoWhile":
        EmitWhile(s, sb, scope, indent, true);
        return;

      case "For":
        EmitFor(s, sb, scope, indent);
        return;

      case "Switch":
        EmitSwitch(s, sb, scope, indent);
        return;

      case "Enforce":
        EmitEnforce(s, sb, scope, indent);
        return;

      case "Break":
        if (s.Text != null) {
          EmitJumpToLabel(s.Text, sb, indent, s);
        } else {
          if (loopDepth == 0) {
            throw Error("'break' outside a loop", s);
          }
          EmitUnwindTo(FindLoopFrame(s, "break"), sb, indent, s);
          sb.Append(pad).Append("break;\n");
        }
        assign.Unreachable = true;
        return;

      case "Continue":
        if (loopDepth == 0) {
          throw Error("'continue' outside a loop", s);
        }
        EmitUnwindTo(FindLoopFrame(s, "continue"), sb, indent, s);
        sb.Append(pad).Append("continue;\n");
        assign.Unreachable = true;
        return;

      case "Label":
        sb.Append($"{pad}{frames.Peek().CLabel(s.Text!)}: ;\n");
        assign.Unreachable = false; // reachable from jumps
        return;

      case "Jump":
        EmitJump(s, sb, indent);
        assign.Unreachable = true;
        return;

      case "Return":
        EmitReturn(s, sb, scope, indent);
        assign.Unreachable = true;
        return;

      case "Catch":
      case "AtEnd":
        throw Error($"'{s.Kind}' must be written directly inside a block", s);

      case "Throw":
        if (EmitThrow(s, sb, scope, pad)) {
          assign.Unreachable = true;
        }
        return;

      case "Fun":
      case "Alias":
      case "Enum":
        throw Error($"'{s.Kind}' is only supported at the top level for now", s);

      default:
        if (NodeFeatures.TryGetValue(s.Kind, out var feature)) {
          throw NotSupported(feature, s);
        }
        throw Error($"'{s.Kind}' is not supported yet", s);
    }
  }

  void EmitDecl(Node s, StringBuilder sb, Scope scope, string pad) {
    var mods = s.Children.FirstOrDefault(c => c.Kind == "Mods");
    bool isConst = mods?.Text?.Split(' ').Contains("const") == true;
    if (s.Children.Any(c => c.Kind == "Size")) {
      throw NotSupported("Sized declarations (arr<T>[n] name)", s);
    }
    string type = ResolveDeclType(s, scope);
    if (scope.Vars.ContainsKey(s.Text!)) {
      throw Error($"'{s.Text}' is already declared in this scope", s);
    }
    CheckNotShadowing(s.Text!, scope, s);
    var init = s.Children.FirstOrDefault(c => c.Kind is "Init" or "WeakInit");
    string code;
    Expr? initValue = null;
    bool weak = init?.Kind == "WeakInit";
    if (init == null) {
      if (isConst) {
        throw Error("a const needs a value", s);
      }
      code = ZeroValue(type);
    } else {
      var value = ExprOf(init.Children[0], scope, type);
      if (weak) {
        if (!IsClass(type)) {
          throw Error("only objects have weak references", s);
        }
        if (value.Fresh) {
          throw Error("a weak reference needs an existing object: nobody would own a new one", s);
        }
      }
      initValue = value;
      code = Convert(value, type, init);
    }
    // an owner must receive an object nobody else owns (the same decision was made when the scope was prepared)
    bool owns = declOwner.TryGetValue(s, out bool guessed) && guessed;
    if (owns && IsClass(type) && initValue is Expr given && !(given.Fresh || given.Type == "null")) {
      throw Error($"'{s.Text}' would own an object that something else owns: write ->x to move it, or start from a new object", s);
    }
    VarInfo variable;
    if (frames.Peek().Hoisted.TryGetValue(s.Text!, out var hoisted)) {
      // already created at the start of the scope (the scope has atEnd blocks)
      variable = hoisted;
      sb.Append($"{pad}{hoisted.CName} = {code};\n");
    } else {
      variable = new VarInfo(type, "v_" + s.Text, isConst);
      sb.Append($"{pad}{(isConst ? "const " : "")}{CType(type)} {variable.CName} = {code};\n");
    }
    variable.IsWeak = weak;
    if (init != null && !weak && IsArr(type) && ConstantArrayLength(init.Children[0]) is long length && !AssignedAgain(s.Text!)) {
      variable.KnownLength = length;
    }
    scope.Vars[s.Text!] = variable;
    if (init == null) {
      assign.Unassigned.Add(variable);
    } else {
      assign.Unassigned.Remove(variable);
    }
  }

  void EmitIf(Node s, StringBuilder sb, Scope scope, int indent, bool isElseIf) {
    string pad = Pad(indent);
    var thenScope = scope;
    string condition;
    if (s.Children[0].Kind == "Is" && s.Children[0].Text != null) {
      (condition, thenScope) = IsBinding(s.Children[0], scope);   // if v is Circle c { }
    } else {
      condition = BoolOf(ExprOf(s.Children[0], scope), s);
    }
    string head = $"if ({condition}) {{\n";
    sb.Append(isElseIf ? head : pad + head);
    var before = assign.Clone();
    EmitBlock(s.Children[1], sb, thenScope, indent + 1);
    var afterThen = assign;
    assign = before.Clone();
    sb.Append(pad).Append('}');
    if (s.Children.Count > 2) {
      var els = s.Children[2].Children[0];
      if (els.Kind == "If") {
        sb.Append(" else ");
        EmitIf(els, sb, scope, indent, true);
        assign = AssignState.Merge(new[] { afterThen, assign });
        return;
      }
      sb.Append(" else {\n");
      EmitBlock(els, sb, scope, indent + 1);
      sb.Append(pad).Append('}');
    }
    assign = AssignState.Merge(new[] { afterThen, assign });
    sb.Append('\n');
  }

  // while cond { } | { } while cond
  void EmitWhile(Node s, StringBuilder sb, Scope scope, int indent, bool doWhile) {
    string pad = Pad(indent);
    var block = doWhile ? s.Children[0] : s.Children[1];
    var condNode = doWhile ? s.Children[1] : s.Children[0];
    var body = PrepareFrame(LoopStatements(block.Children), scope, FrameKind.LoopBody);
    bool cleanup = body.AtEnds.Count > 0;
    int inner = indent;
    if (cleanup) {
      sb.Append($"{pad}{{\n");
      inner = indent + 1;
      EmitHoists(body, sb, Pad(inner));
    }
    string innerPad = Pad(inner);
    string Condition() {
      handlerOverride = cleanup ? body.LoopExcLabel : null;
      string code = BoolOf(ExprOf(condNode, scope), s);
      handlerOverride = null;
      return code;
    }
    var before = assign.Clone();
    sb.Append(doWhile ? $"{innerPad}do {{\n" : $"{innerPad}while ({Condition()}) {{\n");
    if (cleanup) {
      sb.Append($"{innerPad}  {body.RanFlag} = true;\n");
    }
    loopDepth++;
    EmitFrameBody(body, sb, inner + 1);
    loopDepth--;
    sb.Append(doWhile ? $"{innerPad}}} while ({Condition()});\n" : $"{innerPad}}}\n");
    // assignments inside the loop body don't count after the loop
    assign = before;
    if (cleanup) {
      EmitLoopTail(body, sb, inner);
      sb.Append($"{pad}}}\n");
    }
  }

  // for T i = start..end [step ...] { }: counts down when start > end
  void EmitFor(Node s, StringBuilder sb, Scope scope, int indent) {
    string pad = Pad(indent);
    var varNode = s.Children[0];
    var source = s.Children[1];
    var step = s.Children.FirstOrDefault(c => c.Kind == "Step");
    var block = s.Children.Last();

    if (source.Kind == "In" && source.Children[0].Kind != "Range") {
      EmitForEach(s, sb, scope, indent);
      return;
    }
    if (varNode.Children.Count == 0) {
      throw Error($"the 'for' variable '{varNode.Text}' needs a type", s);
    }
    var range = source.Children[0];
    if (range.Kind != "Range") {
      throw Error("only 'for' over ranges is supported yet", s);
    }
    if (range.Children[0].Kind == "Open" || range.Children[1].Kind == "Open") {
      throw Error("a 'for' range needs a start and an end", s);
    }
    string type = ResolveType(varNode.Children[0]);
    if (!IsInt(type)) {
      throw Error("a 'for' range variable must be an integer", s);
    }
    CheckNotShadowing(varNode.Text!, scope, s);
    string startCode = Convert(ExprOf(range.Children[0], scope, type), type, range);
    string endCode = Convert(ExprOf(range.Children[1], scope, type), type, range);

    var innerScope = new Scope(scope);
    string cname = "v_" + varNode.Text;
    innerScope.Vars[varNode.Text!] = new VarInfo(type, cname, false);
    var body = PrepareFrame(LoopStatements(block.Children), innerScope, FrameKind.LoopBody);
    bool cleanup = body.AtEnds.Count > 0;

    tempCounter++;
    string endVar = $"df_end{tempCounter}";
    string downVar = $"df_down{tempCounter}";
    string goVar = $"df_go{tempCounter}";
    bool inclusive = range.Text == "..=";
    string init = $"{cname} = {startCode}, {endVar} = {endCode}, {downVar} = ({cname} > {endVar})";
    string condition;
    string update;
    if (step == null) {
      // inclusive: stop after the end value; exclusive: stop on reaching it
      init += $", {goVar} = {(inclusive ? "1" : $"({cname} != {endVar})")}";
      condition = goVar;
      update = inclusive
        ? $"{goVar} = ({cname} != {endVar}), {goVar} ? ({downVar} ? {cname}-- : {cname}++) : 0"
        : $"{downVar} ? {cname}-- : {cname}++, {goVar} = ({cname} != {endVar})";
    } else {
      string cmpDown = inclusive ? ">=" : ">";
      string cmpUp = inclusive ? "<=" : "<";
      condition = $"({downVar} ? {cname} {cmpDown} {endVar} : {cname} {cmpUp} {endVar})";
      handlerOverride = cleanup ? body.LoopExcLabel : null;
      update = StepCode(step.Children[0], innerScope);
      handlerOverride = null;
    }
    string variables = step == null ? $"{cname}, {endVar}, {downVar}, {goVar}" : $"{cname}, {endVar}, {downVar}";

    int inner = indent;
    if (cleanup) {
      // the loop variable lives outside the loop: the atEnd blocks run after it
      sb.Append($"{pad}{{\n");
      inner = indent + 1;
      EmitHoists(body, sb, Pad(inner));
      sb.Append($"{Pad(inner)}{CType(type)} {variables};\n");
      sb.Append($"{Pad(inner)}for ({init}; {condition}; {update}) {{\n");
      sb.Append($"{Pad(inner)}  {body.RanFlag} = true;\n");
    } else {
      sb.Append($"{pad}for ({CType(type)} {init}; {condition}; {update}) {{\n");
    }
    var before = assign.Clone();
    loopDepth++;
    EmitFrameBody(body, sb, inner + 1);
    loopDepth--;
    assign = before;
    sb.Append(Pad(inner)).Append("}\n");
    if (cleanup) {
      EmitLoopTail(body, sb, inner);
      sb.Append($"{pad}}}\n");
    }
  }

  string StepCode(Node step, Scope scope) {
    if (step.Kind == "Assign") {
      return AssignCode(step, scope);
    }
    if (step.Kind == "ExprStmt") {
      return ExprOf(step.Children[0], scope).Code;
    }
    throw Error("unsupported 'step' expression", step);
  }

  void EmitReturn(Node s, StringBuilder sb, Scope scope, int indent) {
    string pad = Pad(indent);
    string? value = ReturnValueCode(s, scope, sb, pad);
    if (!ReturnPassesAtEnd()) {
      if (fn!.IsMain) {
        sb.Append($"{pad}return {value ?? "0"};\n");
      } else if (fn.PairOf != null) {
        sb.Append($"{pad}return {PairValue(fn.PairOf, value ?? "0")};\n");
      } else {
        sb.Append(value == null ? $"{pad}return;\n" : $"{pad}return {value};\n");
      }
      return;
    }
    // the value is computed first, then the atEnd blocks run
    if (ReturnIgnored()) {
      if (value != null) {
        sb.Append($"{pad}(void)({value});\n");
      }
    } else if (value != null || fn!.IsMain) {
      fn!.UsesRetVar = true;
      sb.Append($"{pad}{fn.RetVar} = {value ?? "0"};\n");
    }
    EmitReturnUnwind(sb, indent);
  }

  // checks the returned value against the function; null when there is none
  string? ReturnValueCode(Node s, Scope scope, StringBuilder sb, string pad) {
    if (fn!.IsMain) {
      if (s.Children.Count > 1) {
        throw Error("the program exit code is a single integer", s);
      }
      if (s.Children.Count == 0) {
        return null;
      }
      var e = ExprOf(s.Children[0], scope, "i32");
      if (!IsInt(e.Type)) {
        throw Error("the program exit code must be an integer", s);
      }
      return $"(int)({e.Code})";
    }
    if (currentRet == "void") {
      if (s.Children.Count > 0) {
        throw Error("this function returns nothing", s);
      }
      return null;
    }
    if (s.Children.Count == 0) {
      throw Error($"this function must return a {currentRet}", s);
    }
    if (IsMulti(currentRet!)) {
      return MultiReturnCode(s, scope, sb, pad);
    }
    if (s.Children.Count > 1) {
      throw Error("this function returns one value", s);
    }
    var returned = ExprOf(s.Children[0], scope, currentRet);
    returned = CheckReturnOwnership(returned, s.Children[0], scope, s);
    return Convert(returned, currentRet!, s);
  }

  // Something that can be written: a variable or a tuple field
  readonly record struct LValueInfo(string Code, string Type, bool IsConst, VarInfo? Variable = null, bool IsField = false);

  LValueInfo LValue(Node target, Scope scope) {
    if (target.Kind == "Name") {
      var variable = scope.Find(target.Text!);
      if (variable == null && currentClass != null && FindField(currentClass, target.Text!) is FieldInfo ownField) {
        var bare = BareField(target.Text!, scope, target)!.Value;
        return new LValueInfo(bare.Code, bare.Type, ownField.IsConst && !initializing, null, !ownField.IsStatic);
      }
      if (variable == null) {
        throw Error($"unknown variable '{target.Text}'", target);
      }
      return new LValueInfo(variable.CName, variable.Type, variable.IsConst, variable);
    }
    if (target.Kind == "Member") {
      if (ClassLValue(target, scope) is LValueInfo classTarget) {
        return classTarget;
      }
      var owner = LValue(target.Children[0], scope);
      if (!IsTuple(owner.Type)) {
        throw Error("only variables and tuple fields can be assigned for now", target);
      }
      int index = TupleFieldIndex(owner.Type, target.Text!, target);
      return new LValueInfo($"{owner.Code}.e{index}", TupleElems(owner.Type)[index].Type, owner.IsConst);
    }
    throw Error("only variables and tuple fields can be assigned for now", target);
  }

  string AssignCode(Node s, Scope scope) {
    var targetNode = s.Children[0];
    if (targetNode.Kind == "Index") {
      return IndexAssign(s, scope);
    }
    var target = LValue(targetNode, scope);
    if (target.IsConst) {
      throw Error($"'{targetNode.Text}' is const", s);
    }
    string op = s.Text!;
    if (op == "=") {
      var assigned = ExprOf(s.Children[1], scope, target.Type);
      if (IsClass(target.Type) && (target.Variable != null || target.IsField)) {
        return OwnedAssign(target, targetNode, assigned, s);
      }
      string value = Convert(assigned, target.Type, s);
      MarkAssigned(target.Variable);
      return $"{target.Code} = {value}";
    }
    if (op == "_=") {
      return WeakAssign(target, targetNode, s.Children[1], scope, s);
    }
    ReadCheck(target.Variable, targetNode.Text ?? "", targetNode);
    if (op == "??=") {
      string code = CoalesceAssignCode(target, s.Children[1], scope, s);
      MarkAssigned(target.Variable);
      return code;
    }
    string bin = op[..^1];
    var left = new Expr(target.Code, target.Type);
    var result = BinaryOp(bin, left, s.Children[1], scope, s);
    return $"{target.Code} = {Convert(result, target.Type, s)}";
  }

  // ---------------------------------------------------------------- expressions

  Expr ExprOf(Node n, Scope scope, string? expected = null) {
    switch (n.Kind) {
      case "Int":
        return IntLiteral(n, expected, false);
      case "Real":
        return RealLiteral(n.Text!, expected);
      case "String":
        return new Expr(CString(Unescape(n.Text![1..^1], n)), "string");
      case "Char": {
        var cps = Unescape(n.Text![1..^1], n);
        return new Expr($"((uint32_t){cps[0]}u)", "char");
      }
      case "Bool":
        return new Expr(n.Text!, "bool");
      case "Null":
        return new Expr("0", "null");
      case "Name": {
        var v = scope.Find(n.Text!);
        if (v != null) {
          if (v.IsWeak) {
            throw Error($"'{n.Text}' is a weak reference: use it through enforce {n.Text} {{ }} else {{ }}", n);
          }
          ReadCheck(v, n.Text!, n);
          return new Expr(v.CName, v.Type);
        }
        if (BareField(n.Text!, scope, n) is Expr field) {
          return field;
        }
        if (traits.TryGetValue(n.Text!, out var viewed) && scope.Find("this") != null && IsSubtype(scope.Find("this")!.Type, viewed.Name)) {
          return new Expr("self", viewed.Name);   // TraitName.member inside a class that implements it
        }
        if (n.Text == "parent" && currentClass?.Parent != null && scope.Find("this") != null) {
          return new Expr($"(({CType(currentClass.Parent.Name)})self)", currentClass.Parent.Name);
        }
        return BareEnumEntry(n.Text!) ?? throw Error($"unknown name '{n.Text}'", n);
      }
      case "Paren": {
        var e = ExprOf(n.Children[0], scope, expected);
        return new Expr($"({e.Code})", e.Type, e.Fresh);
      }
      case "Gift":
        return GiftExpr(n, scope);
      case "Tuple":
        return TupleLiteral(n, scope, expected);
      case "Interp":
        return Interpolated(n, scope);
      case "Binary":
        return Binary(n, scope, expected);
      case "Not":
        return new Expr($"(!{BoolOf(ExprOf(n.Children[0], scope), n)})", "bool");
      case "Neg": {
        if (n.Children[0].Kind == "Int") {
          return IntLiteral(n.Children[0], expected, true);
        }
        if (n.Children[0].Kind == "Real") {
          var r = RealLiteral(n.Children[0].Text!, expected);
          return new Expr($"(-{r.Code})", r.Type);
        }
        var e = ExprOf(n.Children[0], scope, expected);
        if (IsNullable(e.Type)) {
          return LiftUnary(e, value => Negate(value, n));
        }
        return Negate(e, n);
      }
      case "Plus":
        return ExprOf(n.Children[0], scope, expected);
      case "BitNot": {
        var e = ExprOf(n.Children[0], scope, expected);
        if (IsNullable(e.Type)) {
          return LiftUnary(e, value => BitNot(value, n));
        }
        return BitNot(e, n);
      }
      case "PostInc":
      case "PostDec":
      case "PreInc":
      case "PreDec":
        return IncDec(n, scope);
      case "Cast":
        return CastExpr(ExprOf(n.Children[1], scope), ResolveType(n.Children[0]), n);
      case "Transmute":
        return Transmute(n, scope);
      case "Call":
        return Call(n, scope);
      case "Member":
        return MemberValue(n, scope);
      case "SafeMember":
        return SafeAccess(n, n, scope);
      case "SwitchExpr":
        return SwitchExpr(n, scope, expected);
      case "TypeSwitch":
        return TypeSwitch(n, scope, expected);
      case "BoolSwitchExpr":
        return BoolSwitchExpr(n, scope, expected);
      case "IsNull":
        return new Expr(EqualsCode(ExprOf(n.Children[0], scope), new Expr("0", "null"), n), "bool");
      case "Is":
        return IsExpr(n, scope);
      case "List":
        if (expected != null && IsArr(expected)) {
          return ArrayLiteral(n, scope, expected);
        }
        if (expected != null && IsVec(expected)) {
          return VecLiteral(n, scope, expected);
        }
        throw Error("a list needs a declared type: arr<T> name = [...]", n);
      case "Index":
        if (n.Children[1].Kind == "Empty") {
          throw NotSupported("Pointers", n);
        }
        return IndexExpr(n, scope);
      default:
        if (NodeFeatures.TryGetValue(n.Kind, out var feature)) {
          throw NotSupported(feature, n);
        }
        throw Error($"'{n.Kind}' expressions are not supported yet", n);
    }
  }

  // a + b, a - b, a * b of integers with the overflow raised as a silent OverflowException (only used where a catch of the function handles it)
  string CheckedArithmetic(string builtin, string cType, string left, string right) {
    string result = NewTemp();
    return $"({{ {cType} {result}; if ({builtin}({left}, {right}, &{result})) {{ df_raise(&df_desc_OverflowException, {fn!.NameConst}); {RaiseJump} }} {result}; }})";
  }

  Expr Negate(Expr e, Node at) {
    if (!IsNumeric(e.Type)) {
      throw Error($"cannot negate {e.Type}", at);
    }
    if (SignedTypes.Contains(e.Type) && SilentCaught(StandardExceptions["OverflowException"])) {
      return new Expr(CheckedArithmetic("__builtin_sub_overflow", CType(e.Type), $"(({CType(e.Type)})0)", e.Code), e.Type);
    }
    return new Expr(IsInt(e.Type) ? $"(({CType(e.Type)})(-{e.Code}))" : $"(-{e.Code})", e.Type);
  }

  Expr BitNot(Expr e, Node at) {
    if (IsBitfield(e.Type)) {
      // flips only the defined bits
      return new Expr($"(({CType(e.Type)})(~{e.Code} & {IntConstCode(enums[e.Type].Mask, enums[e.Type].ValueType)}))", e.Type);
    }
    if (!IsInt(e.Type)) {
      throw Error("'_' (bitwise not) needs an integer", at);
    }
    return new Expr($"(({CType(e.Type)})~{e.Code})", e.Type);
  }

  Expr IncDec(Node n, Scope scope) {
    var targetNode = n.Children[0];
    var target = LValue(targetNode, scope);
    ReadCheck(target.Variable, targetNode.Text ?? "", targetNode);
    if (!IsNumeric(target.Type)) {
      throw Error($"++ and -- need a number, not {target.Type}", n);
    }
    if (target.IsConst) {
      throw Error($"'{targetNode.Text}' is const", n);
    }
    if (IsInt(target.Type) && SilentCaught(StandardExceptions["OverflowException"])) {
      // the step overflows: raise it; the variable wraps, as it does when nobody catches
      string builtin = n.Kind is "PostInc" or "PreInc" ? "__builtin_add_overflow" : "__builtin_sub_overflow";
      string type = CType(target.Type);
      string old = NewTemp();
      string result = n.Kind is "PostInc" or "PostDec" ? old : target.Code;
      return new Expr($"({{ {type} {old} = {target.Code}; if ({builtin}({old}, ({type})1, &{target.Code})) {{ df_raise(&df_desc_OverflowException, {fn!.NameConst}); {RaiseJump} }} {result}; }})", target.Type);
    }
    string code = n.Kind switch {
      "PostInc" => $"{target.Code}++",
      "PostDec" => $"{target.Code}--",
      "PreInc" => $"++{target.Code}",
      _ => $"--{target.Code}"
    };
    return new Expr(code, target.Type);
  }

  // ---------------------------------------------------------------- literals

  static bool IsUntypedLiteral(Node n) {
    if (n.Kind is "Paren" or "Neg") {
      return IsUntypedLiteral(n.Children[0]);
    }
    if (n.Kind == "Int") {
      string t = n.Text!.ToLowerInvariant();
      return !t.StartsWith("0x") && !(t.StartsWith("0b") && t.Length > 2 && t[2] is '0' or '1')
        && char.IsDigit(t[^1]);
    }
    return n.Kind == "Real" && char.IsDigit(n.Text![^1]);
  }

  Expr IntLiteral(Node n, string? expected, bool negative) {
    expected = expected == null ? null : BaseOf(expected);
    string raw = n.Text!.Replace("_", "").ToLowerInvariant();
    BigInteger value;
    string type;

    bool isHex = raw.StartsWith("0x");
    bool isBin = raw.StartsWith("0b") && raw.Length > 2 && (raw[2] == '0' || raw[2] == '1');
    if (isHex || isBin) {
      bool unsigned = raw.EndsWith('u');
      string digits = raw[2..].TrimEnd('u');
      int bits;
      if (isHex) {
        value = BigInteger.Parse("0" + digits, NumberStyles.HexNumber);
        bits = digits.Length <= 2 ? 8 : digits.Length <= 4 ? 16 : digits.Length <= 8 ? 32 : digits.Length <= 16 ? 64 : 0;
      } else {
        value = BigInteger.Zero;
        foreach (char ch in digits) {
          value = value * 2 + (ch - '0');
        }
        bits = digits.Length <= 8 ? 8 : digits.Length <= 16 ? 16 : digits.Length <= 32 ? 32 : digits.Length <= 64 ? 64 : 0;
      }
      if (bits == 0) {
        throw Error("literals wider than 64 bits are not supported yet", n);
      }
      if (negative) {
        throw Error("hex and binary literals cannot be negated; they are raw bits", n);
      }
      type = (unsigned ? "u" : "i") + bits;
      string hex = value.ToString("X");
      return new Expr($"(({CType(type)})0x{hex}ULL)", type);
    }

    int end = raw.Length;
    while (end > 0 && char.IsLetter(raw[end - 1])) {
      end--;
    }
    string suffix = raw[end..];
    value = BigInteger.Parse(raw[..end], CultureInfo.InvariantCulture);
    if (negative) {
      value = -value;
    }

    if (suffix == "") {
      if (expected != null && RealTypes.Contains(expected)) {
        string lit = value.ToString(CultureInfo.InvariantCulture) + ".0";
        return new Expr(expected == "r32" ? $"({lit}f)" : $"({lit})", expected);
      }
      type = expected != null && IsInt(expected) ? expected : "i32";
    } else {
      type = suffix switch {
        "u" => "u32",
        "l" => "i64",
        "ul" or "lu" => "u64",
        "s" => "i16",
        "us" or "su" => "u16",
        "b" or "ub" or "bu" => "u8",
        _ => throw Error($"unknown suffix '{suffix}'", n)
      };
    }

    var (min, max) = IntRange(type);
    if (value < min || value > max) {
      throw Error($"{value} doesn't fit in {type}", n);
    }
    return new Expr(IntConstCode(value, type), type);
  }

  static (BigInteger, BigInteger) IntRange(string t) {
    int bits = Bits(t);
    if (SignedTypes.Contains(t)) {
      var half = BigInteger.One << (bits - 1);
      return (-half, half - 1);
    }
    return (BigInteger.Zero, (BigInteger.One << bits) - 1);
  }

  static Expr RealLiteral(string text, string? expected) {
    expected = expected == null ? null : BaseOf(expected);
    string raw = text.Replace("_", "");
    bool isFloat = raw.EndsWith('f') || raw.EndsWith('F');
    if (isFloat) {
      raw = raw[..^1];
    }
    double d = double.Parse(raw, CultureInfo.InvariantCulture);
    string type = isFloat || expected == "r32" ? "r32" : "r64";
    string lit = d.ToString("R", CultureInfo.InvariantCulture);
    if (!lit.Contains('.') && !lit.Contains('E')) {
      lit += ".0";
    }
    return new Expr(type == "r32" ? $"({lit}f)" : $"({lit})", type);
  }

  // Dflat escapes → list of code points
  List<int> Unescape(string raw, Node at) {
    var cps = new List<int>();
    int i = 0;
    while (i < raw.Length) {
      char c = raw[i];
      if (c != '\\') {
        int cp = char.ConvertToUtf32(raw, i);
        cps.Add(cp);
        i += char.IsSurrogatePair(raw, i) ? 2 : 1;
        continue;
      }
      char e = raw[i + 1];
      i += 2;
      switch (e) {
        case 'n': cps.Add(10); break;
        case 'r': cps.Add(13); break;
        case 't': cps.Add(9); break;
        case '0': cps.Add(0); break;
        case '\\': cps.Add('\\'); break;
        case '\'': cps.Add('\''); break;
        case '"': cps.Add('"'); break;
        case '{': cps.Add('{'); break;
        case '}': cps.Add('}'); break;
        case 'u':
        case '#': {
          int close = raw.IndexOf('}', i);
          string digits = raw[(i + 1)..close];
          cps.Add(e == 'u' ? int.Parse(digits, NumberStyles.HexNumber) : int.Parse(digits));
          i = close + 1;
          break;
        }
        default:
          throw Error($"unknown escape '\\{e}'", at);
      }
    }
    return cps;
  }

  static List<int> CodePoints(string text) {
    return text.EnumerateRunes().Select(rune => rune.Value).ToList();
  }

  static string CString(List<int> cps) {
    var bytes = new List<byte>();
    foreach (int cp in cps) {
      bytes.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(cp)));
    }
    var sb = new StringBuilder("df_str_lit(\"");
    foreach (byte b in bytes) {
      if (b >= 0x20 && b < 0x7F && b != '"' && b != '\\' && b != '?') {
        sb.Append((char)b);
      } else {
        sb.Append('\\').Append(System.Convert.ToString(b, 8).PadLeft(3, '0'));
      }
    }
    sb.Append($"\", {bytes.Count})");
    return sb.ToString();
  }

  // The holes are evaluated one after the other, left to right: C leaves the order of function arguments open, so with two or
  // more holes each one goes into a temporary first.
  Expr Interpolated(Node n, Scope scope) {
    string code = "df_str_lit(\"\", 0)";
    bool first = true;
    var pieces = new List<string>();
    var setup = new StringBuilder();
    bool ordered = n.Children.Count(part => part.Kind != "Text") > 1;
    foreach (var part in n.Children) {
      string piece;
      if (part.Kind == "Text") {
        piece = CString(Unescape(part.Text![1..^1], part));
      } else {
        var hole = ExprOf(part.Children[0], scope);
        var formatted = part.Children.Count > 1 ? FormattedHole(hole, part.Children[1], part) : null;
        if (part.Children.Count > 1 && formatted == null && !specWarned) {
          Warn("format specs apply to numbers, dates and times only; ignored for other values", part);
          specWarned = true;
        }
        piece = formatted?.Code ?? StrOf(hole, part);
        if (ordered) {
          string temp = NewTemp();
          setup.Append($"df_string {temp} = {piece}; ");
          piece = temp;
        }
      }
      pieces.Add(piece);
    }
    foreach (string piece in pieces) {
      code = first ? piece : $"df_str_concat({code}, {piece})";
      first = false;
    }
    return new Expr(ordered ? $"({{ {setup}{code}; }})" : code, "string");
  }

  // ---------------------------------------------------------------- operators

  Expr Binary(Node n, Scope scope, string? expected) {
    string op = n.Text!;
    if (op is "&&" or "||" or "^^") {
      string l = BoolOf(ExprOf(n.Children[0], scope), n);
      string r = BoolOf(ExprOf(n.Children[1], scope), n);
      return op == "^^" ? new Expr($"({l} != {r})", "bool") : new Expr($"({l} {op} {r})", "bool");
    }
    if (op == "??") {
      return Coalesce(n, scope, expected);
    }
    // an untyped literal adapts to the other side; two literals adapt to the expected type
    string? context = NumericContext(expected);
    if (IsUntypedLiteral(n.Children[0]) && !IsUntypedLiteral(n.Children[1])) {
      var right = ExprOf(n.Children[1], scope, context);
      var lit = ExprOf(n.Children[0], scope, NumericContext(right.Type));
      return Combine(op, lit, right, n);
    }
    var left = ExprOf(n.Children[0], scope, context);
    return BinaryOp(op, left, n.Children[1], scope, n);
  }

  Expr BinaryOp(string op, Expr left, Node rightNode, Scope scope, Node at) {
    var right = ExprOf(rightNode, scope, NumericContext(left.Type));
    return Combine(op, left, right, at);
  }

  Expr Combine(string op, Expr l, Expr r, Node at) {
    // string building
    if (op == "+" && (l.Type is "string" or "char" || r.Type is "string" or "char")) {
      return new Expr($"df_str_concat({StrOf(l, at)}, {StrOf(r, at)})", "string");
    }
    if (IsTimeType(l.Type) || IsTimeType(r.Type)) {
      return TimeOp(op, l, r, at);
    }
    if (op is "==" or "!=") {
      string equal = EqualsCode(l, r, at);
      return new Expr(op == "==" ? equal : $"(!{equal})", "bool");
    }
    bool withNull = IsNullable(l.Type) || IsNullable(r.Type) || l.Type == "null" || r.Type == "null";
    if (op is "<" or "<=" or ">" or ">=") {
      if (withNull) {
        return OrderNullable(op, l, r, at);
      }
      if (IsEnum(l.Type) || IsEnum(r.Type)) {
        if (l.Type != r.Type) {
          throw Error($"cannot compare {l.Type} and {r.Type}", at);
        }
        return new Expr($"({l.Code} {op} {r.Code})", "bool");
      }
      if (l.Type == "char" && r.Type == "char") {
        return new Expr($"({l.Code} {op} {r.Code})", "bool");   // by code point ('a'..='z')
      }
    } else if (withNull) {
      return LiftBinary(op, l, r, at);
    }
    if (IsTuple(l.Type) || IsTuple(r.Type)) {
      throw Error($"operator '{op}' doesn't apply to tuples", at);
    }
    if (IsBitfield(l.Type) || IsBitfield(r.Type)) {
      if (op is not ("|" or "&" or "^")) {
        throw Error($"operator '{op}' doesn't apply to a bitfield; combine flags with | & ^ or cast to an integer", at);
      }
      if (l.Type != r.Type) {
        throw Error($"cannot apply '{op}' to {l.Type} and {r.Type}", at);
      }
      return new Expr($"(({CType(l.Type)})({l.Code} {op} {r.Code}))", l.Type);
    }
    if (op is "<<" or ">>") {
      if (!IsInt(l.Type) || !IsInt(r.Type)) {
        throw Error("shifts need integers", at);
      }
      return new Expr($"(({CType(l.Type)})({l.Code} {op} {r.Code}))", l.Type);
    }

    string t = Unify(l.Type, r.Type, op, at);
    string lc = l.Type == t ? l.Code : $"(({CType(t)}){l.Code})";
    string rc = r.Type == t ? r.Code : $"(({CType(t)}){r.Code})";

    switch (op) {
      case "<":
      case "<=":
      case ">":
      case ">=":
        return new Expr($"({lc} {op} {rc})", "bool");
      case "+":
      case "-":
      case "*":
        if (IsInt(t) && SilentCaught(StandardExceptions["OverflowException"])) {
          // a catch of this function handles the overflow: raise it (without one the result wraps and nothing is raised, spec 15.4)
          string builtin = op == "+" ? "__builtin_add_overflow" : op == "-" ? "__builtin_sub_overflow" : "__builtin_mul_overflow";
          return new Expr(CheckedArithmetic(builtin, CType(t), lc, rc), t);
        }
        return IsInt(t)
          ? new Expr($"(({CType(t)})({lc} {op} {rc}))", t)
          : new Expr($"({lc} {op} {rc})", t);
      case "/":
        return IsInt(t)
          ? new Expr(CheckRaise($"df_div_{t}({lc}, {rc}, {fn!.NameConst})", t), t)
          : new Expr($"({lc} / {rc})", t);
      case "%":
        return IsInt(t)
          ? new Expr(CheckRaise($"df_mod_{t}({lc}, {rc}, {fn!.NameConst})", t), t)
          : new Expr(t == "r32" ? $"fmodf({lc}, {rc})" : $"fmod({lc}, {rc})", t);
      case "&":
      case "|":
      case "^":
        if (!IsInt(t)) {
          throw Error($"'{op}' needs integers", at);
        }
        return new Expr($"(({CType(t)})({lc} {op} {rc}))", t);
      default:
        throw Error($"operator '{op}' is not supported yet", at);
    }
  }

  static string Unify(string a, string b, string op, Node at) {
    if (a == b && IsNumeric(a)) {
      return a;
    }
    if (IsNumeric(a) && IsNumeric(b) && Family(a) == Family(b)) {
      return Bits(a) >= Bits(b) ? a : b;
    }
    // unsigned widens to a larger signed type
    if (Family(a) == "unsigned" && Family(b) == "signed" && Bits(a) < Bits(b)) {
      return b;
    }
    if (Family(b) == "unsigned" && Family(a) == "signed" && Bits(b) < Bits(a)) {
      return a;
    }
    throw new CompileException($"cannot apply '{op}' to {a} and {b}; use a cast", at.Line);
  }

  Expr CastExpr(Expr e, string to, Node n) {
    if (e.Type == to) {
      return e;
    }
    if (e.Type == "null") {
      return new Expr(Convert(e, to, n), to);
    }
    // null stays null: (i32)maybe gives an i32?
    if (IsNullable(to)) {
      string toBase = BaseOf(to);
      if (IsNullable(e.Type)) {
        return LiftUnary(e, value => CastExpr(value, toBase, n));
      }
      var inner = CastExpr(e, toBase, n);
      return new Expr(Convert(inner, to, n), to);
    }
    if (IsNullable(e.Type)) {
      return LiftUnary(e, value => CastExpr(value, to, n));
    }
    if (to == "time" && IsInt(e.Type)) {
      return CastToTime(e, n);
    }
    if (enums.TryGetValue(to, out var target)) {
      var inner = CastExpr(e, target.ValueType, n);
      // a bitfield never holds undefined bits
      return new Expr(target.Bitfield ? $"(({CType(to)})({inner.Code} & {IntConstCode(target.Mask, target.ValueType)}))" : inner.Code, to);
    }
    if (enums.TryGetValue(e.Type, out var source)) {
      return CastExpr(new Expr(e.Code, source.ValueType), to, n);
    }
    if (IsTuple(to) || IsTuple(e.Type)) {
      if (IsTuple(to) && IsTuple(e.Type) && Assignable(to, e.Type)) {
        return new Expr(Convert(e, to, n), to);
      }
      throw Error($"cannot cast {e.Type} to {to}", n);
    }
    if (to == "string") {
      return new Expr(StrOf(e, n), "string");
    }
    if (to == "bool") {
      return new Expr(BoolOf(e, n), "bool");
    }
    if (IsInt(to)) {
      string src = SignedTypes.Contains(e.Type) ? $"_i((int64_t)({e.Code}))"
        : e.Type is "date" or "time" ? $"_i((int64_t)({e.Code}))"
        : UnsignedTypes.Contains(e.Type) || e.Type is "char" ? $"_u((uint64_t)({e.Code}))"
        : RealTypes.Contains(e.Type) ? $"_r((double)({e.Code}))"
        : e.Type == "bool" ? $"_u((uint64_t)({e.Code}))"
        : throw Error($"cannot cast {e.Type} to {to}", n);
      if (SilentCaught(StandardExceptions["OutOfRangeCastException"])) {
        // a catch of this function handles a value that does not fit: it is raised (the cast still saturates)
        return new Expr(SilentCall($"df_satc_{to}{src}", to, "OutOfRangeCastException", n), to);
      }
      return new Expr($"df_sat_{to}{src}", to);
    }
    if (RealTypes.Contains(to)) {
      if (!IsNumeric(e.Type)) {
        throw Error($"cannot cast {e.Type} to {to}", n);
      }
      return new Expr($"(({CType(to)})({e.Code}))", to);
    }
    if (to == "char" && IsInt(e.Type)) {
      return new Expr($"((uint32_t)({e.Code}))", "char");
    }
    throw Error($"cannot cast {e.Type} to {to}", n);
  }

  // [type]x: same bits, low bits kept or zero-extended
  Expr Transmute(Node n, Scope scope) {
    string to = ResolveType(n.Children[0]);
    var e = ExprOf(n.Children[1], scope);
    if (to == "bool") {
      // 0, null, "", NaN and '\0' are false, anything else is true
      return new Expr(BoolOf(e, n), "bool");
    }
    string targetType = to;
    if (enums.TryGetValue(to, out var target)) {
      targetType = target.ValueType;
    }
    string sourceType = enums.TryGetValue(e.Type, out var source) ? source.ValueType : e.Type;
    bool fromInt = IsInt(sourceType) || sourceType is "char" or "bool";
    if (fromInt && (IsInt(targetType) || targetType is "char")) {
      int bits = sourceType is "char" ? 32 : sourceType is "bool" ? 8 : Bits(sourceType);
      string converted = $"(({CType(targetType)})(uint{bits}_t)({e.Code}))";
      if (enums.TryGetValue(to, out var bitfield) && bitfield.Bitfield) {
        converted = $"(({CType(targetType)})({converted} & {IntConstCode(bitfield.Mask, bitfield.ValueType)}))";
      }
      return new Expr(converted, to);
    }
    if (fromInt && targetType == "bool") {
      return new Expr($"(({e.Code}) != 0)", "bool");
    }
    throw Error($"transmute from {e.Type} to {to} is not supported yet", n);
  }

  // ---------------------------------------------------------------- calls and members

  // a.b.c → "a.b.c" with the first segment resolved through aliases
  string? Path(Node n) {
    if (n.Kind == "GenericRef") {
      return GenericRefClass(n);   // Stack<i32>.create(): the copy of the generic class
    }
    if (n.Kind == "Name") {
      if (nameAliases.TryGetValue(n.Text!, out var name)) {
        return name;
      }
      return typeAliases.TryGetValue(n.Text!, out var type) && IsEnum(type) ? type : n.Text;
    }
    if (n.Kind == "Member") {
      var p = Path(n.Children[0]);
      if (p == "String" && n.Text == "StringEncoding") {
        return "StringEncoding";   // String.StringEncoding.UTF16
      }
      if (p == "Root.IO" && IsEnum(n.Text!)) {
        return n.Text;             // fs.StreamOpenMode.OpenExisting with alias Root.IO as fs
      }
      return p == null ? null : p + "." + n.Text;
    }
    return null;
  }

  Expr Call(Node n, Scope scope) {
    var callee = n.Children[0];
    var args = n.Children[1].Children;
    if (callee.Kind == "GenericRef") {
      if (callee.Children[0] is not { Kind: "Name" } genericName) {
        throw NotSupported("Generics", callee);
      }
      if (genericFuns.TryGetValue(genericName.Text!, out var explicitFun)) {
        return UserCall(InstantiateForCall(explicitFun, callee.Children.Skip(1).ToList(), args, scope, n), args, scope, n);
      }
      if (GenericRefClass(callee, args, scope) is string instanceName) {
        // Stack<i32>(args): the constructor of the copy
        return Call(Synthetic("Call", n).Add(Synthetic("Name", n, instanceName)).Add(n.Children[1]), scope);
      }
      throw Error($"'{genericName.Text}' is not a generic function or class", callee);
    }
    if (callee.Kind == "SafeMember") {
      return SafeAccess(callee, n, scope);
    }

    // parent.name(): the parent's version of an overridden function
    if (callee.Kind == "Member" && callee.Children[0] is { Kind: "Name", Text: "parent" } && scope.Find("parent") == null) {
      return ParentCall(callee.Text!, args, scope, n);
    }

    // TraitName.name(): the trait's own function, inside a class that implements it
    if (callee.Kind == "Member" && callee.Children[0] is { Kind: "Name" } traitName && traits.TryGetValue(traitName.Text!, out var qualified)
        && scope.Find(traitName.Text!) == null && scope.Find("this") != null) {
      return TraitDirectCall(qualified, callee.Text!, args, scope, n);
    }

    // x.toStr() and x.toStr(format)
    if (callee.Kind == "Member" && callee.Text == "toStr" && args.Count <= 1) {
      var value = ExprOf(callee.Children[0], scope);
      return args.Count == 0 ? new Expr(StrOf(value, n), "string") : FormatCall(value, args, scope, n);
    }

    // a function of a value: "text".Trim(), c.IsDigit(), d.addDays(1), numbers.has(3)
    if (callee.Kind == "Member" && IsValueReceiver(callee.Children[0], scope)) {
      var receiver = ExprOf(callee.Children[0], scope);
      return TryReceiverCall(receiver, callee.Text!, args, scope, n)
        ?? throw Error($"{receiver.Type} has no function '{callee.Text}'", n);
    }

    string? path = Path(callee);
    if (path != null && scope.Find(path.Split('.')[0]) == null) {
      if (path == "Root.Random.shuffle") {
        return ShuffleCall(args, scope, n);
      }
      if (path.EndsWith(".tryParse") && TryParseCall(path, args, scope, n) is Expr tried) {
        return tried;
      }
      if (Stdlib.TryGetValue(path, out var stdOverloads)) {
        return StdCall(path, stdOverloads, args, scope, n);
      }
      if (callee.Kind == "Name" && BareMethodCall(callee.Text!, args, scope, n) is Expr bare) {
        return bare;
      }
      if (callee.Kind == "Member" && StaticCall(path, args, scope, n) is Expr staticCall) {
        return staticCall;
      }
      if (callee.Kind == "Name" && genericFuns.TryGetValue(path, out var inferredFun)) {
        return UserCall(InstantiateForCall(inferredFun, new List<Node>(), args, scope, n), args, scope, n);
      }
      bool enumMethod = callee.Kind == "Member" && IsEnum(path.Split('.')[0]);
      if ((callee.Kind == "Name" || enumMethod) && funs.TryGetValue(path, out var overloads)) {
        return UserCall(ResolveOverload(overloads, args, scope, n), args, scope, n);
      }
      // inside an enum method, sibling methods without the enum name
      if (callee.Kind == "Name" && currentEnum != null && funs.TryGetValue(currentEnum + "." + path, out var siblings)) {
        return UserCall(ResolveOverload(siblings, args, scope, n), args, scope, n);
      }
    }
    throw Error($"unknown function '{path ?? callee.Kind}'", n);
  }

  // positional arguments in order, named ones anywhere, defaults for the rest
  Expr UserCall(FunSig f, List<Node> args, Scope scope, Node at) {
    var (slots, problem) = MapArgs(f, args);
    if (slots == null) {
      throw Error(problem!, at);
    }
    var codes = new List<string>();
    for (int index = 0; index < f.Params.Count; index++) {
      var param = f.Params[index];
      Node arg = slots[index] ?? param.Default!;
      // a single array given to a variadic parameter is passed as it is
      if (param.IsVariadic && arg.Kind == "List" && arg.Children.Count == 1
          && ExprOf(arg.Children[0], scope).Type == param.Type) {
        arg = arg.Children[0];
      }
      codes.Add(ArgCode(param, arg, scope));
    }
    string call = $"{f.CName}({string.Join(", ", codes)})";
    return new Expr(UserCallCode(f, call), f.Ret, IsClass(f.Ret) && (f.IsCtor || f.ReturnsOwned));
  }

  Expr MemberValue(Node n, Scope scope) {
    string? path = Path(n);
    if (path != null && scope.Find(path.Split('.')[0]) == null) {
      string name = path[(path.LastIndexOf('.') + 1)..];
      if (path.StartsWith("Root.Math.") && MathConsts.TryGetValue(name, out var v)) {
        return new Expr($"({v})", "r64");
      }
      if (path.StartsWith("Root.Math32.") && MathConsts.TryGetValue(name, out var v32)) {
        return new Expr($"({v32}f)", "r32");
      }
      string[] parts = path.Split('.');
      if (parts.Length == 2 && classes.TryGetValue(parts[0], out var staticOwner)) {
        return StaticField(staticOwner, parts[1], n);
      }
      if (parts.Length == 2 && IsEnum(parts[0])) {
        return EnumEntryExpr(parts[0], parts[1]) ?? throw Error($"'{parts[0]}' has no entry '{parts[1]}'", n);
      }
    }
    var obj = ExprOf(n.Children[0], scope);
    if (IsNullable(obj.Type)) {
      throw Error($"'{n.Text}': the {obj.Type} may be null; use '?.'", n);
    }
    if (IsTuple(obj.Type)) {
      int index = TupleFieldIndex(obj.Type, n.Text!, n);
      return new Expr($"({obj.Code}).e{index}", TupleElems(obj.Type)[index].Type);
    }
    if (obj.Type == "Exception") {
      return ExceptionMember(obj, n.Text!, n);
    }
    if (IsClass(obj.Type)) {
      // a read-only property of a library class: x.size calls its get_size()
      var owner = ObjInfo(obj.Type);
      // (a private field of the same name does not hide the property from outside the class: readMode of a stream)
      var named = FindField(owner, n.Text!);
      bool reachable = named != null && (named.Visibility != Vis.Private
        || (currentClass != null && (currentClass == named.Owner || IsSubclass(currentClass.Name, named.Owner.Name))));
      if (!reachable && LookupMethods(owner, "get_" + n.Text).Count > 0) {
        return ObjectCall(obj, "get_" + n.Text, new List<Node>(), scope, n) ?? throw Error("no property", n);
      }
      return TraitView(obj, n.Text!, n) ?? ObjectField(obj, n.Text!, n);   // x.TraitName, x.field
    }
    if (obj.Type == "string" && n.Text == "len") {
      return new Expr($"df_str_chars({obj.Code})", "u64");
    }
    if (IsArr(obj.Type) && n.Text == "len") {
      return new Expr($"(({obj.Code}).len)", "u64");
    }
    if (DateTimeProperty(obj, n.Text!, n) is Expr property) {
      return property;
    }
    if (obj.Type == "string" && n.Text == "sizeof") {
      return new Expr($"(({obj.Code}).len)", "u64");
    }
    throw Error($"member '{n.Text}' of {obj.Type} is not supported yet", n);
  }
}
