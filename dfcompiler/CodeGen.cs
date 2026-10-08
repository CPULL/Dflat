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
  }

  public string Name { get; }
  public string CName => "f_" + Name.Replace('.', '_');
  public string Ret { get; }
  public List<ParamSig> Params { get; }
  public Node Node { get; }
  public string? EnumName { get; }
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
readonly record struct Expr(string Code, string Type);

// Type checks a Dflat syntax tree (first subset) and emits C.
// Split in partial files: CodeGen.Types.cs, CodeGen.Switch.cs, CodeGen.Nulls.cs, CodeGen.Tuples.cs
public sealed partial class CodeGen {
  static readonly HashSet<string> SignedTypes = new() { "i8", "i16", "i32", "i64" };
  static readonly HashSet<string> UnsignedTypes = new() { "u8", "u16", "u32", "u64" };
  static readonly HashSet<string> RealTypes = new() { "r32", "r64" };
  static readonly HashSet<string> TypeNodeKinds = new() { "Type", "Nullable", "TupleType", "FunType", "Dyn", "Impl" };

  static readonly Dictionary<string, string> MathFuns = new() {
    ["Sqrt"] = "sqrt", ["Cbrt"] = "cbrt", ["Sin"] = "sin", ["Cos"] = "cos", ["Tan"] = "tan",
    ["Asin"] = "asin", ["Acos"] = "acos", ["Atan"] = "atan", ["Sinh"] = "sinh", ["Cosh"] = "cosh",
    ["Tanh"] = "tanh", ["Exp"] = "exp", ["Ln"] = "log", ["Log10"] = "log10", ["Floor"] = "floor",
    ["Ceil"] = "ceil", ["Round"] = "round", ["Trunc"] = "trunc", ["Abs"] = "fabs",
    ["Atan2"] = "atan2", ["Pow"] = "pow", ["Min"] = "fmin", ["Max"] = "fmax", ["Hypot"] = "hypot",
    ["Fmod"] = "fmod"
  };

  static readonly Dictionary<string, string> MathConsts = new() {
    ["Pi"] = "3.141592653589793",
    ["PiHalf"] = "1.5707963267948966",
    ["E"] = "2.718281828459045",
    ["Sqrt2"] = "1.4142135623730951",
    ["R2D"] = "57.29577951308232",
    ["D2R"] = "0.017453292519943295"
  };

  readonly Dictionary<string, FunSig> funs = new();
  readonly Dictionary<string, string> nameAliases = new();
  readonly Dictionary<string, string> typeAliases = new();
  readonly List<string> warnings = new();
  string? currentRet;
  bool inMain;
  int loopDepth;
  bool specWarned;
  int tempCounter;

  public IReadOnlyList<string> Warnings => warnings;

  static CompileException Error(string msg, Node at) {
    return new CompileException(msg, at.Line);
  }

  // level 1 = most important, 3 = least (spec 19.1); 0 = tool notice without a level
  void Warn(string msg, Node at, int level = 0) {
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
    foreach (var statement in program.Children) {
      if (statement.Kind == "Enum") {
        DeclareEnum(statement);
      }
    }
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
    foreach (var statement in program.Children) {
      if (statement.Kind == "Fun") {
        RegisterFun(statement, null);
      } else if (statement.Kind == "FunVar") {
        throw Error("lambda variables are not supported yet", statement);
      }
    }

    var protos = new StringBuilder();
    var bodies = new StringBuilder();
    foreach (var fun in funs.Values) {
      protos.Append(Signature(fun)).Append(";\n");
      bodies.Append(EmitFun(fun)).Append('\n');
    }

    var main = new StringBuilder();
    main.Append("int main(int argc, char** argv) {\n");
    main.Append("  (void)argc;\n  (void)argv;\n  df_init();\n");
    inMain = true;
    currentRet = null;
    var scope = new Scope(null);
    foreach (var statement in program.Children) {
      if (statement.Kind is "Fun" or "Alias" or "Enum") {
        continue;
      }
      EmitStmt(statement, main, scope, 1);
    }
    main.Append("  return 0;\n}\n");
    inMain = false;

    var output = new StringBuilder();
    output.Append("/* generated by dfcompiler */\n");
    output.Append(Runtime.Source).Append('\n');
    output.Append("/* ---- program ---- */\n\n");
    output.Append(typeDefs);
    output.Append(enumCode);
    output.Append(protos).Append('\n');
    output.Append(bodies);
    output.Append(main);
    return output.ToString();
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
  void RegisterFun(Node funNode, string? enumName) {
    string name = enumName == null ? funNode.Text! : enumName + "." + funNode.Text;
    if (funs.ContainsKey(name)) {
      throw Error($"function '{name}' already defined (overloading not supported yet)", funNode);
    }
    string ret = "void";
    var rets = funNode.Children.FirstOrDefault(child => child.Kind == "Returns");
    if (rets != null) {
      if (rets.Children.Count == 1) {
        ret = ResolveType(rets.Children[0]);
      } else {
        ret = "*" + MakeTuple(rets.Children.Select(typeNode => (ResolveType(typeNode), (string?)null)));
      }
    }
    var ps = new List<ParamSig>();
    var paramsNode = funNode.Children.First(child => child.Kind == "Params");
    foreach (var p in paramsNode.Children) {
      if (p.Kind != "Param") {
        throw Error($"{p.Kind} parameters are not supported yet", p);
      }
      var def = p.Children.FirstOrDefault(child => child.Kind == "Default");
      ps.Add(new ParamSig(p.Text!, ResolveType(p.Children[0]), def?.Children[0]));
    }
    if (funNode.Children.Any(child => child.Kind == "NoBody")) {
      throw Error("functions without a body are only valid in traits", funNode);
    }
    funs[name] = new FunSig(name, ret, ps, funNode, enumName);
  }

  string Signature(FunSig f) {
    var ps = f.Params.Count == 0
      ? "void"
      : string.Join(", ", f.Params.Select(p => $"{CType(p.Type)} v_{p.Name}"));
    return $"static {CType(f.Ret)} {f.CName}({ps})";
  }

  string EmitFun(FunSig f) {
    var sb = new StringBuilder();
    sb.Append(Signature(f)).Append(" {\n");
    currentRet = f.Ret;
    currentEnum = f.EnumName;
    var scope = new Scope(null);
    foreach (var p in f.Params) {
      scope.Vars[p.Name] = new VarInfo(p.Type, "v_" + p.Name, false);
    }
    var block = f.Node.Children.First(c => c.Kind == "Block");
    // single-expression body returns its value
    bool singleValue = f.Ret != "void" && !IsMulti(f.Ret);
    if (singleValue && block.Children.Count == 1 && block.Children[0].Kind == "ExprStmt") {
      var e = Convert(ExprOf(block.Children[0].Children[0], scope, f.Ret), f.Ret, block.Children[0]);
      sb.Append($"  return {e};\n");
    } else {
      foreach (var s in block.Children) {
        EmitStmt(s, sb, scope, 1);
      }
    }
    sb.Append("}\n");
    currentRet = null;
    currentEnum = null;
    return sb.ToString();
  }

  // ---------------------------------------------------------------- statements

  static string Pad(int indent) => new(' ', indent * 2);

  void EmitBlock(Node block, StringBuilder sb, Scope scope, int indent) {
    var inner = new Scope(scope);
    foreach (var s in block.Children) {
      EmitStmt(s, sb, inner, indent);
    }
  }

  void EmitStmt(Node s, StringBuilder sb, Scope scope, int indent) {
    string pad = Pad(indent);
    switch (s.Kind) {
      case "Namespace":
      case "NamespaceLocal":
      case "Import":
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
        var e = ExprOf(s.Children[0], scope);
        string k = s.Children[0].Kind;
        if (k is "Call" or "PostInc" or "PostDec" or "PreInc" or "PreDec") {
          sb.Append(pad).Append(e.Code).Append(";\n");
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
        EmitIf(s, sb, scope, indent, false);
        return;

      case "IfThen":
        sb.Append($"{pad}if ({BoolOf(ExprOf(s.Children[0], scope), s)}) {{\n");
        EmitStmt(s.Children[1], sb, new Scope(scope), indent + 1);
        sb.Append(pad).Append("}\n");
        return;

      case "While":
        sb.Append($"{pad}while ({BoolOf(ExprOf(s.Children[0], scope), s)}) {{\n");
        loopDepth++;
        EmitBlock(s.Children[1], sb, scope, indent + 1);
        loopDepth--;
        sb.Append(pad).Append("}\n");
        return;

      case "DoWhile":
        sb.Append(pad).Append("do {\n");
        loopDepth++;
        EmitBlock(s.Children[0], sb, scope, indent + 1);
        loopDepth--;
        sb.Append($"{pad}}} while ({BoolOf(ExprOf(s.Children[1], scope), s)});\n");
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
          sb.Append($"{pad}goto lbl_{s.Text};\n");
          return;
        }
        if (loopDepth == 0) {
          throw Error("'break' outside a loop", s);
        }
        sb.Append(pad).Append("break;\n");
        return;

      case "Continue":
        if (loopDepth == 0) {
          throw Error("'continue' outside a loop", s);
        }
        sb.Append(pad).Append("continue;\n");
        return;

      case "Label":
        sb.Append($"{pad}lbl_{s.Text}: ;\n");
        return;

      case "Jump":
        EmitJump(s, sb, pad);
        return;

      case "Return":
        EmitReturn(s, sb, scope, pad);
        return;

      case "Throw":
        EmitThrow(s, sb, scope, pad);
        return;

      case "Fun":
      case "Alias":
      case "Enum":
        throw Error($"'{s.Kind}' is only supported at the top level for now", s);

      default:
        throw Error($"'{s.Kind}' is not supported yet", s);
    }
  }

  void EmitDecl(Node s, StringBuilder sb, Scope scope, string pad) {
    var mods = s.Children.FirstOrDefault(c => c.Kind == "Mods");
    bool isConst = mods?.Text?.Split(' ').Contains("const") == true;
    var typeNode = s.Children.First(c => TypeNodeKinds.Contains(c.Kind));
    string type = ResolveType(typeNode);
    if (scope.Vars.ContainsKey(s.Text!)) {
      throw Error($"'{s.Text}' is already declared in this scope", s);
    }
    var init = s.Children.FirstOrDefault(c => c.Kind is "Init" or "WeakInit");
    string code;
    if (init == null) {
      if (isConst) {
        throw Error("a const needs a value", s);
      }
      code = ZeroValue(type);
    } else {
      if (init.Kind == "WeakInit") {
        throw Error("weak references are not supported yet", s);
      }
      code = Convert(ExprOf(init.Children[0], scope, type), type, init);
    }
    string cname = "v_" + s.Text;
    scope.Vars[s.Text!] = new VarInfo(type, cname, isConst);
    sb.Append($"{pad}{(isConst ? "const " : "")}{CType(type)} {cname} = {code};\n");
  }

  void EmitIf(Node s, StringBuilder sb, Scope scope, int indent, bool isElseIf) {
    string pad = Pad(indent);
    string head = $"if ({BoolOf(ExprOf(s.Children[0], scope), s)}) {{\n";
    sb.Append(isElseIf ? head : pad + head);
    EmitBlock(s.Children[1], sb, scope, indent + 1);
    sb.Append(pad).Append('}');
    if (s.Children.Count > 2) {
      var els = s.Children[2].Children[0];
      if (els.Kind == "If") {
        sb.Append(" else ");
        EmitIf(els, sb, scope, indent, true);
        return;
      }
      sb.Append(" else {\n");
      EmitBlock(els, sb, scope, indent + 1);
      sb.Append(pad).Append('}');
    }
    sb.Append('\n');
  }

  void EmitFor(Node s, StringBuilder sb, Scope scope, int indent) {
    string pad = Pad(indent);
    var varNode = s.Children[0];
    var source = s.Children[1];
    var step = s.Children.FirstOrDefault(c => c.Kind == "Step");
    var block = s.Children.Last();

    var range = source.Children[0];
    if (range.Kind != "Range") {
      throw Error("only 'for' over ranges is supported yet", s);
    }
    if (range.Children[0].Kind == "Open" || range.Children[1].Kind == "Open") {
      throw Error("a 'for' range needs a start and an end", s);
    }

    string type;
    Expr start;
    if (varNode.Children.Count > 0) {
      type = ResolveType(varNode.Children[0]);
      if (!IsInt(type)) {
        throw Error("a 'for' range variable must be an integer", s);
      }
      start = ExprOf(range.Children[0], scope, type);
    } else {
      start = ExprOf(range.Children[0], scope);
      type = start.Type;
    }
    string startCode = Convert(start, type, range);
    string endCode = Convert(ExprOf(range.Children[1], scope, type), type, range);

    var inner = new Scope(scope);
    string cname = "v_" + varNode.Text;
    inner.Vars[varNode.Text!] = new VarInfo(type, cname, false);
    string cmp = range.Text == "..=" ? "<=" : "<";
    string inc = step == null ? $"{cname}++" : StepCode(step.Children[0], inner);

    sb.Append($"{pad}for ({CType(type)} {cname} = {startCode}; {cname} {cmp} {endCode}; {inc}) {{\n");
    loopDepth++;
    EmitBlock(block, sb, inner, indent + 1);
    loopDepth--;
    sb.Append(pad).Append("}\n");
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

  void EmitReturn(Node s, StringBuilder sb, Scope scope, string pad) {
    if (inMain) {
      if (s.Children.Count > 1) {
        throw Error("the program exit code is a single integer", s);
      }
      if (s.Children.Count == 0) {
        sb.Append(pad).Append("return 0;\n");
        return;
      }
      var e = ExprOf(s.Children[0], scope, "i32");
      if (!IsInt(e.Type)) {
        throw Error("the program exit code must be an integer", s);
      }
      sb.Append($"{pad}return (int)({e.Code});\n");
      return;
    }
    if (currentRet == "void") {
      if (s.Children.Count > 0) {
        throw Error("this function returns nothing", s);
      }
      sb.Append(pad).Append("return;\n");
      return;
    }
    if (s.Children.Count == 0) {
      throw Error($"this function must return a {currentRet}", s);
    }
    if (IsMulti(currentRet!)) {
      EmitMultiReturn(s, sb, scope, pad);
      return;
    }
    if (s.Children.Count > 1) {
      throw Error("this function returns one value", s);
    }
    sb.Append($"{pad}return {Convert(ExprOf(s.Children[0], scope, currentRet), currentRet!, s)};\n");
  }

  // throw Panic(code)  |  throw SomeException("message") (uncaught: ends the program)
  void EmitThrow(Node s, StringBuilder sb, Scope scope, string pad) {
    var call = s.Children[0];
    if (call.Kind != "Call" || call.Children[0].Kind != "Name") {
      throw Error("throw needs an exception constructor, e.g. throw Exception(\"msg\")", s);
    }
    string name = call.Children[0].Text!;
    var args = call.Children[1].Children;
    if (name == "Panic") {
      if (args.Count != 1) {
        throw Error("Panic takes one exit code", s);
      }
      var code = ExprOf(args[0], scope, "i64");
      if (!IsInt(code.Type)) {
        throw Error("the Panic exit code must be an integer", s);
      }
      sb.Append($"{pad}df_panic((int64_t)({code.Code}));\n");
      return;
    }
    string msg = args.Count > 0 ? StrOf(ExprOf(args[0], scope), s) : "df_str_lit(\"\", 0)";
    sb.Append($"{pad}{{ df_string m_ = {msg}; char* z_ = (char*)df_alloc(m_.len + 1); ");
    sb.Append($"memcpy(z_, m_.p, m_.len); z_[m_.len] = 0; df_fail(\"{name}\", z_); }}\n");
  }

  // Something that can be written: a variable or a tuple field
  readonly record struct LValueInfo(string Code, string Type, bool IsConst);

  LValueInfo LValue(Node target, Scope scope) {
    if (target.Kind == "Name") {
      var variable = scope.Find(target.Text!) ?? throw Error($"unknown variable '{target.Text}'", target);
      return new LValueInfo(variable.CName, variable.Type, variable.IsConst);
    }
    if (target.Kind == "Member") {
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
    var target = LValue(targetNode, scope);
    if (target.IsConst) {
      throw Error($"'{targetNode.Text}' is const", s);
    }
    string op = s.Text!;
    if (op == "=") {
      return $"{target.Code} = {Convert(ExprOf(s.Children[1], scope, target.Type), target.Type, s)}";
    }
    if (op == "??=") {
      return CoalesceAssignCode(target, s.Children[1], scope, s);
    }
    if (op == "_=") {
      throw Error($"'{op}' is not supported yet", s);
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
          return new Expr(v.CName, v.Type);
        }
        return BareEnumEntry(n.Text!) ?? throw Error($"unknown name '{n.Text}'", n);
      }
      case "Paren": {
        var e = ExprOf(n.Children[0], scope, expected);
        return new Expr($"({e.Code})", e.Type);
      }
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
      case "BoolSwitchExpr":
        return BoolSwitchExpr(n, scope, expected);
      case "IsNull":
        return new Expr(EqualsCode(ExprOf(n.Children[0], scope), new Expr("0", "null"), n), "bool");
      case "Is":
        throw Error("type checks with 'is' need classes; not supported yet", n);
      default:
        throw Error($"'{n.Kind}' expressions are not supported yet", n);
    }
  }

  Expr Negate(Expr e, Node at) {
    if (!IsNumeric(e.Type)) {
      throw Error($"cannot negate {e.Type}", at);
    }
    return new Expr(IsInt(e.Type) ? $"(({CType(e.Type)})(-{e.Code}))" : $"(-{e.Code})", e.Type);
  }

  Expr BitNot(Expr e, Node at) {
    if (!IsInt(e.Type)) {
      throw Error("'_' (bitwise not) needs an integer", at);
    }
    return new Expr($"(({CType(e.Type)})~{e.Code})", e.Type);
  }

  Expr IncDec(Node n, Scope scope) {
    var targetNode = n.Children[0];
    var target = LValue(targetNode, scope);
    if (!IsNumeric(target.Type)) {
      throw Error($"++ and -- need a number, not {target.Type}", n);
    }
    if (target.IsConst) {
      throw Error($"'{targetNode.Text}' is const", n);
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

  Expr Interpolated(Node n, Scope scope) {
    string code = "df_str_lit(\"\", 0)";
    bool first = true;
    foreach (var part in n.Children) {
      string piece;
      if (part.Kind == "Text") {
        piece = CString(Unescape(part.Text![1..^1], part));
      } else {
        if (part.Children.Count > 1 && !specWarned) {
          Warn("format specs are ignored for now", part);
          specWarned = true;
        }
        piece = StrOf(ExprOf(part.Children[0], scope), part);
      }
      code = first ? piece : $"df_str_concat({code}, {piece})";
      first = false;
    }
    return new Expr(code, "string");
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
    } else if (withNull) {
      return LiftBinary(op, l, r, at);
    }
    if (IsTuple(l.Type) || IsTuple(r.Type)) {
      throw Error($"operator '{op}' doesn't apply to tuples", at);
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
        return IsInt(t)
          ? new Expr($"(({CType(t)})({lc} {op} {rc}))", t)
          : new Expr($"({lc} {op} {rc})", t);
      case "/":
        return IsInt(t)
          ? new Expr($"df_div_{t}({lc}, {rc})", t)
          : new Expr($"({lc} / {rc})", t);
      case "%":
        return IsInt(t)
          ? new Expr($"df_mod_{t}({lc}, {rc})", t)
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
    if (enums.TryGetValue(to, out var target)) {
      var inner = CastExpr(e, target.ValueType, n);
      return new Expr(inner.Code, to);
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
        : UnsignedTypes.Contains(e.Type) || e.Type is "char" ? $"_u((uint64_t)({e.Code}))"
        : RealTypes.Contains(e.Type) ? $"_r((double)({e.Code}))"
        : e.Type == "bool" ? $"_u((uint64_t)({e.Code}))"
        : throw Error($"cannot cast {e.Type} to {to}", n);
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
    string targetType = to;
    if (enums.TryGetValue(to, out var target)) {
      targetType = target.ValueType;
    }
    string sourceType = enums.TryGetValue(e.Type, out var source) ? source.ValueType : e.Type;
    bool fromInt = IsInt(sourceType) || sourceType is "char" or "bool";
    if (fromInt && (IsInt(targetType) || targetType is "char")) {
      int bits = sourceType is "char" ? 32 : sourceType is "bool" ? 8 : Bits(sourceType);
      return new Expr($"(({CType(targetType)})(uint{bits}_t)({e.Code}))", to);
    }
    if (fromInt && targetType == "bool") {
      return new Expr($"(({e.Code}) != 0)", "bool");
    }
    throw Error($"transmute from {e.Type} to {to} is not supported yet", n);
  }

  // ---------------------------------------------------------------- calls and members

  // a.b.c → "a.b.c" with the first segment resolved through aliases
  string? Path(Node n) {
    if (n.Kind == "Name") {
      if (nameAliases.TryGetValue(n.Text!, out var name)) {
        return name;
      }
      return typeAliases.TryGetValue(n.Text!, out var type) && IsEnum(type) ? type : n.Text;
    }
    if (n.Kind == "Member") {
      var p = Path(n.Children[0]);
      return p == null ? null : p + "." + n.Text;
    }
    return null;
  }

  Expr Call(Node n, Scope scope) {
    var callee = n.Children[0];
    var args = n.Children[1].Children;
    if (args.Any(a => a.Kind == "Named")) {
      throw Error("named arguments are not supported yet", n);
    }
    if (callee.Kind == "SafeMember") {
      return SafeAccess(callee, n, scope);
    }

    // x.toStr()
    if (callee.Kind == "Member" && callee.Text == "toStr" && args.Count == 0) {
      return new Expr(StrOf(ExprOf(callee.Children[0], scope), n), "string");
    }

    string? path = Path(callee);
    if (path != null && scope.Find(path.Split('.')[0]) == null) {
      switch (path) {
        case "Root.Console.Log":
        case "Root.Console.LogError":
          if (args.Count != 1) {
            throw Error($"{path} takes one argument", n);
          }
          string fn = path.EndsWith("Error") ? "df_console_log_error" : "df_console_log";
          return new Expr($"{fn}({StrOf(ExprOf(args[0], scope), n)})", "void");
      }
      if (path.StartsWith("Root.Math.") || path.StartsWith("Root.Math64.") || path.StartsWith("Root.Math32.")) {
        return MathCall(path, args, scope, n);
      }
      bool enumMethod = callee.Kind == "Member" && IsEnum(path.Split('.')[0]);
      if ((callee.Kind == "Name" || enumMethod) && funs.TryGetValue(path, out var f)) {
        return UserCall(f, args, scope, n);
      }
      // inside an enum method, sibling methods without the enum name
      if (callee.Kind == "Name" && currentEnum != null && funs.TryGetValue(currentEnum + "." + path, out var sibling)) {
        return UserCall(sibling, args, scope, n);
      }
    }
    throw Error($"unknown function '{path ?? callee.Kind}'", n);
  }

  Expr UserCall(FunSig f, List<Node> args, Scope scope, Node at) {
    if (args.Count > f.Params.Count) {
      throw Error($"'{f.Name}' takes {f.Params.Count} arguments", at);
    }
    var codes = new List<string>();
    for (int i = 0; i < f.Params.Count; i++) {
      var p = f.Params[i];
      Node? arg = i < args.Count ? args[i] : p.Default;
      if (arg == null) {
        throw Error($"missing argument '{p.Name}' for '{f.Name}'", at);
      }
      codes.Add(Convert(ExprOf(arg, scope, p.Type), p.Type, arg));
    }
    return new Expr($"{f.CName}({string.Join(", ", codes)})", f.Ret);
  }

  Expr MathCall(string path, List<Node> args, Scope scope, Node at) {
    bool is32 = path.StartsWith("Root.Math32.");
    string name = path[(path.LastIndexOf('.') + 1)..];
    string rt = is32 ? "r32" : "r64";
    if (name is "Lerp" or "InverseLerp" or "Clamp") {
      if (args.Count != 3) {
        throw Error($"{name} takes 3 arguments", at);
      }
      var a = Convert(ExprOf(args[0], scope, rt), rt, at);
      var b = Convert(ExprOf(args[1], scope, rt), rt, at);
      var c = Convert(ExprOf(args[2], scope, rt), rt, at);
      string code = name switch {
        "Lerp" => $"({a} + ({b} - {a}) * {c})",
        "InverseLerp" => $"(({c} - {a}) / ({b} - {a}))",
        _ => $"({(is32 ? "fminf(fmaxf" : "fmin(fmax")}({a}, {b}), {c}))"
      };
      return new Expr(code, rt);
    }
    if (!MathFuns.TryGetValue(name, out var cfun)) {
      throw Error($"unknown math function '{name}'", at);
    }
    var codes = args.Select(a => Convert(ExprOf(a, scope, rt), rt, a)).ToList();
    return new Expr($"{cfun}{(is32 ? "f" : "")}({string.Join(", ", codes)})", rt);
  }

  Expr MemberValue(Node n, Scope scope) {
    string? path = Path(n);
    if (path != null && scope.Find(path.Split('.')[0]) == null) {
      string name = path[(path.LastIndexOf('.') + 1)..];
      if ((path.StartsWith("Root.Math.") || path.StartsWith("Root.Math64.")) && MathConsts.TryGetValue(name, out var v)) {
        return new Expr($"({v})", "r64");
      }
      if (path.StartsWith("Root.Math32.") && MathConsts.TryGetValue(name, out var v32)) {
        return new Expr($"({v32}f)", "r32");
      }
      string[] parts = path.Split('.');
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
    if (obj.Type == "string" && n.Text == "len") {
      return new Expr($"df_str_chars({obj.Code})", "u64");
    }
    if (obj.Type == "string" && n.Text == "sizeof") {
      return new Expr($"(({obj.Code}).len)", "u64");
    }
    throw Error($"member '{n.Text}' of {obj.Type} is not supported yet", n);
  }
}
