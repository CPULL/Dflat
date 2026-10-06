namespace dfparse;

// Hand-written recursive descent parser for a Dflat subset.
public sealed class Parser {
  static readonly HashSet<string> BuiltinTypes = new() {
    "i8", "i16", "i32", "i64", "i128", "i256",
    "u8", "u16", "u32", "u64", "u128", "u256",
    "r16", "r32", "r64", "r128",
    "dec", "bool", "char", "byte", "date", "clock", "lapse", "string", "vec"
  };

  // ">>=" is handled separately (lexed as '>' '>=')
  static readonly HashSet<string> AssignOps = new() {
    "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", "??="
  };

  static readonly HashSet<string> Modifiers = new() {
    "public", "internal", "private", "const"
  };

  // Rust-style precedence, lowest first. ".." and "??" sit below these.
  static readonly string[][] Levels = {
    new[] { "||" },
    new[] { "^^" },
    new[] { "&&" },
    new[] { "==", "!=", "<", "<=", ">", ">=" },
    new[] { "|" },
    new[] { "^" },
    new[] { "&" },
    new[] { "<<", ">>" },
    new[] { "+", "-" },
    new[] { "*", "/", "%" }
  };

  readonly List<Token> toks;
  int pos;
  bool noBrace; // true while parsing if/while/for/switch headers: '{' starts the block

  public Parser(List<Token> tokens) {
    toks = tokens;
  }

  // ---------------------------------------------------------------- helpers

  Token Peek => toks[pos];

  Token PeekAt(int n) {
    return toks[Math.Min(pos + n, toks.Count - 1)];
  }

  Token Next() {
    var t = toks[pos];
    if (pos < toks.Count - 1) {
      pos++;
    }
    return t;
  }

  bool IsOp(string s) => Peek.Kind == TokenKind.Op && Peek.Text == s;
  bool IsKw(string s) => Peek.Kind == TokenKind.Keyword && Peek.Text == s;
  bool IsNewline => Peek.Kind == TokenKind.Newline;
  bool AtEnd => Peek.Kind == TokenKind.Eof;

  bool AcceptOp(string s) {
    if (!IsOp(s)) {
      return false;
    }
    Next();
    return true;
  }

  Token ExpectOp(string s) {
    if (!IsOp(s)) {
      throw Error($"expected '{s}'");
    }
    return Next();
  }

  Token ExpectIdent() {
    if (Peek.Kind != TokenKind.Ident) {
      throw Error("expected identifier");
    }
    return Next();
  }

  Token ExpectMember() {
    if (Peek.Kind != TokenKind.Ident && Peek.Kind != TokenKind.Int) {
      throw Error("expected member name");
    }
    return Next();
  }

  void SkipNewlines() {
    while (IsNewline) {
      Next();
    }
  }

  void SkipTerminators() {
    while (IsNewline || IsOp(";")) {
      Next();
    }
  }

  void SkipSeparators() {
    while (IsNewline || IsOp(";") || IsOp(",")) {
      Next();
    }
  }

  DfException Error(string msg) {
    return new DfException($"{msg}, found {Describe(Peek)}", Peek.Line, Peek.Col);
  }

  static DfException ErrorAt(string msg, Token t) {
    return new DfException(msg, t.Line, t.Col);
  }

  static string Describe(Token t) {
    return t.Kind switch {
      TokenKind.Newline => "end of line",
      TokenKind.Eof => "end of file",
      _ => $"'{t.Text}'"
    };
  }

  // '>' immediately followed by '>' or '>=' forms ">>" or ">>="
  string? PeekCompoundGt() {
    if (!IsOp(">")) {
      return null;
    }
    var n = PeekAt(1);
    if (n.Kind != TokenKind.Op || n.Line != Peek.Line || n.Col != Peek.Col + 1) {
      return null;
    }
    if (n.Text == ">") {
      return ">>";
    }
    if (n.Text == ">=") {
      return ">>=";
    }
    return null;
  }

  static bool StartsOperand(Token t) {
    switch (t.Kind) {
      case TokenKind.Ident:
      case TokenKind.Int:
      case TokenKind.Real:
      case TokenKind.String:
      case TokenKind.Char:
        return true;
      case TokenKind.Keyword:
        return t.Text is "true" or "false" or "null";
      case TokenKind.Op:
        return t.Text is "(" or "[" or "!" or "-" or "+" or "_" or "&" or "++" or "--";
      default:
        return false;
    }
  }

  // After "(UserType)": stricter, so "(a)-b" stays a subtraction
  static bool StartsCastOperand(Token t) {
    switch (t.Kind) {
      case TokenKind.Ident:
      case TokenKind.Int:
      case TokenKind.Real:
      case TokenKind.String:
      case TokenKind.Char:
        return true;
      case TokenKind.Keyword:
        return t.Text is "true" or "false" or "null";
      case TokenKind.Op:
        return t.Text == "(";
      default:
        return false;
    }
  }

  // ---------------------------------------------------------------- program and statements

  public Node ParseProgram() {
    var root = new Node("Program", Peek);
    SkipTerminators();
    while (!AtEnd) {
      root.Add(ParseStatement());
      SkipTerminators();
    }
    return root;
  }

  List<Token> ParseModifiers() {
    var mods = new List<Token>();
    while (Peek.Kind == TokenKind.Keyword && Modifiers.Contains(Peek.Text)) {
      mods.Add(Next());
    }
    return mods;
  }

  static Node WithMods(Node node, List<Token> mods) {
    if (mods.Count > 0) {
      node.Children.Insert(0, new Node("Mods", mods[0], string.Join(" ", mods.Select(m => m.Text))));
    }
    return node;
  }

  void EndStatement(bool caseBody) {
    if (IsNewline || IsOp(";") || IsOp("}") || AtEnd) {
      return;
    }
    if (caseBody && IsOp(",")) {
      return;
    }
    throw Error("expected end of statement");
  }

  Node ParseStatement(string? className = null, bool caseBody = false) {
    var mods = ParseModifiers();
    if (IsKw("fun")) {
      return WithMods(ParseFun(), mods);
    }
    if (IsKw("class")) {
      return WithMods(ParseClass(), mods);
    }
    if (IsKw("enum")) {
      return WithMods(ParseEnum(), mods);
    }
    if (className != null && Peek.Kind == TokenKind.Ident && Peek.Text == className && PeekAt(1).Is("(")) {
      return WithMods(ParseConstructor(), mods);
    }
    if (mods.Count > 0) {
      var decl = ParseSimple(!caseBody);
      if (decl.Kind != "Decl") {
        throw ErrorAt("modifiers can only be applied to declarations", mods[0]);
      }
      EndStatement(caseBody);
      return WithMods(decl, mods);
    }
    if (IsOp("{")) {
      return ParseBlockStatement();
    }
    if (Peek.Kind == TokenKind.Label) {
      var l = Next();
      return new Node("Label", l, l.Text);
    }
    if (Peek.Kind == TokenKind.Keyword) {
      switch (Peek.Text) {
        case "if":
          return ParseIf();
        case "while":
          return ParseWhile();
        case "for":
          return ParseFor();
        case "switch":
          return ParseSwitch();
        case "jump":
          return ParseJump(caseBody);
        case "return":
          return ParseReturn(caseBody);
        case "catch":
          return ParseCatch();
        case "atEnd":
          return ParseAtEnd();
        case "enforce":
          return ParseEnforce();
      }
    }
    var simple = ParseSimple(!caseBody);
    EndStatement(caseBody);
    return simple;
  }

  Node ParseBlock(string? className = null) {
    SkipNewlines();
    var open = ExpectOp("{");
    var block = new Node("Block", open);
    bool saved = noBrace;
    noBrace = false;
    SkipTerminators();
    while (!IsOp("}")) {
      if (AtEnd) {
        throw ErrorAt("unclosed '{'", open);
      }
      block.Add(ParseStatement(className));
      SkipTerminators();
    }
    Next();
    noBrace = saved;
    return block;
  }

  // { ... } or { ... } while cond
  Node ParseBlockStatement() {
    var block = ParseBlock();
    if (IsKw("while")) {
      var w = Next();
      var node = new Node("DoWhile", w).Add(block).Add(ParseCondition());
      EndStatement(false);
      return node;
    }
    return block;
  }

  Node ParseCondition() {
    bool saved = noBrace;
    noBrace = true;
    var e = ParseExpr();
    noBrace = saved;
    return e;
  }

  bool AcceptElse() {
    int save = pos;
    SkipNewlines();
    if (IsKw("else")) {
      Next();
      return true;
    }
    pos = save;
    return false;
  }

  Node ParseIf() {
    var t = Next();
    var node = new Node("If", t).Add(ParseCondition()).Add(ParseBlock());
    if (AcceptElse()) {
      var els = new Node("Else", t);
      els.Add(IsKw("if") ? ParseIf() : ParseBlock());
      node.Add(els);
    }
    return node;
  }

  Node ParseWhile() {
    var t = Next();
    return new Node("While", t).Add(ParseCondition()).Add(ParseBlock());
  }

  // for u8 i = 0..10 step i += 2 { }   |   for char c in s { }   |   for key in list.keys { }
  Node ParseFor() {
    var t = Next();
    var node = new Node("For", t);
    bool saved = noBrace;
    noBrace = true;
    if (Peek.Kind == TokenKind.Ident && PeekAt(1).Is("in")) {
      var n = Next();
      node.Add(new Node("Var", n, n.Text));
    } else {
      var type = ParseType();
      var n = ExpectIdent();
      node.Add(new Node("Var", n, n.Text).Add(type));
    }
    if (IsKw("in")) {
      var tin = Next();
      node.Add(new Node("In", tin).Add(ParseExpr()));
    } else {
      var eq = ExpectOp("=");
      node.Add(new Node("From", eq).Add(ParseExpr()));
      if (IsKw("step")) {
        var st = Next();
        node.Add(new Node("Step", st).Add(ParseSimple(false)));
      }
    }
    noBrace = saved;
    node.Add(ParseBlock());
    return node;
  }

  // switch val { 1: a, 2: b \n 3, 4, 5: { ... } \n default: c }
  Node ParseSwitch() {
    var t = Next();
    var node = new Node("Switch", t).Add(ParseCondition());
    SkipNewlines();
    var open = ExpectOp("{");
    bool saved = noBrace;
    noBrace = false;
    SkipSeparators();
    while (!IsOp("}")) {
      if (AtEnd) {
        throw ErrorAt("unclosed switch", open);
      }
      var c = new Node("Case", Peek);
      if (IsKw("default")) {
        c.Add(new Node("Default", Next()));
      } else {
        c.Add(ParseExpr());
        while (AcceptOp(",")) {
          SkipNewlines();
          c.Add(ParseExpr());
        }
      }
      ExpectOp(":");
      if (IsOp("{")) {
        c.Add(ParseBlock());
      } else {
        c.Add(ParseStatement(null, true));
      }
      node.Add(c);
      SkipSeparators();
    }
    Next();
    noBrace = saved;
    return node;
  }

  Node ParseJump(bool caseBody) {
    var t = Next();
    var target = ExpectIdent();
    EndStatement(caseBody);
    return new Node("Jump", t, target.Text);
  }

  Node ParseReturn(bool caseBody) {
    var t = Next();
    var node = new Node("Return", t);
    bool empty = IsNewline || IsOp(";") || IsOp("}") || AtEnd || (caseBody && IsOp(","));
    if (!empty) {
      node.Add(ParseExpr());
      while (!caseBody && AcceptOp(",")) {
        node.Add(ParseExpr());
      }
    }
    EndStatement(caseBody);
    return node;
  }

  // catch { }   |   catch ex { }   |   catch OverflowException ex { }
  Node ParseCatch() {
    var t = Next();
    var node = new Node("Catch", t);
    if (Peek.Kind == TokenKind.Ident) {
      var first = Next();
      if (Peek.Kind == TokenKind.Ident) {
        var name = Next();
        node.Text = name.Text;
        node.Add(new Node("Type", first, first.Text));
      } else {
        node.Text = first.Text;
      }
    }
    node.Add(ParseBlock());
    return node;
  }

  Node ParseAtEnd() {
    var t = Next();
    return new Node("AtEnd", t).Add(ParseBlock());
  }

  // enforce a, b { } else { }
  Node ParseEnforce() {
    var t = Next();
    var node = new Node("Enforce", t);
    do {
      var n = ExpectIdent();
      node.Add(new Node("Name", n, n.Text));
    } while (AcceptOp(","));
    node.Add(ParseBlock());
    if (AcceptElse()) {
      node.Add(new Node("Else", t).Add(ParseBlock()));
    }
    return node;
  }

  // Declaration, multiple assignment, assignment or expression
  Node ParseSimple(bool allowMulti) {
    var start = Peek;
    var first = ParseTarget();

    if (allowMulti && IsOp(",")) {
      var targets = new Node("Targets", start).Add(first);
      while (AcceptOp(",")) {
        targets.Add(ParseTarget());
      }
      var eq = ExpectOp("=");
      SkipNewlines();
      return new Node("MultiAssign", eq).Add(targets).Add(ParseExpr());
    }

    if (first.Kind == "Decl") {
      if (AcceptOp("=")) {
        SkipNewlines();
        first.Add(ParseExpr());
      }
      return first;
    }

    string? op = null;
    if (PeekCompoundGt() == ">>=") {
      op = ">>=";
    } else if (Peek.Kind == TokenKind.Op && AssignOps.Contains(Peek.Text)) {
      op = Peek.Text;
    }
    if (op != null) {
      var opTok = Next();
      if (op == ">>=") {
        Next();
      }
      SkipNewlines();
      return new Node("Assign", opTok, op).Add(first).Add(ParseExpr());
    }

    if (first.Kind == "Discard") {
      throw ErrorAt("'_' alone is only valid as an assignment target", start);
    }
    return new Node("ExprStmt", start).Add(first);
  }

  // One target: "_" (discard), "Type name" (new variable), or an expression
  Node ParseTarget() {
    if (IsOp("_") && !StartsOperand(PeekAt(1))) {
      return new Node("Discard", Next());
    }
    var decl = TryParseDecl();
    if (decl != null) {
      return decl;
    }
    return ParseExpr();
  }

  // Speculative: "Type name" followed by '=', ',', or end of statement
  Node? TryParseDecl() {
    if (Peek.Kind != TokenKind.Ident && !IsOp("(")) {
      return null;
    }
    int save = pos;
    try {
      var type = ParseType();
      if (Peek.Kind == TokenKind.Ident) {
        var name = Peek;
        var after = PeekAt(1);
        bool ends = after.Kind == TokenKind.Newline || after.Kind == TokenKind.Eof
          || after.Is("=") || after.Is(",") || after.Is(";") || after.Is("}");
        if (ends) {
          Next();
          return new Node("Decl", name, name.Text).Add(type);
        }
      }
    } catch (DfException) {
      // not a declaration
    }
    pos = save;
    return null;
  }

  // ---------------------------------------------------------------- types

  Node ParseType(bool allowCallback = true) {
    var start = Peek;
    Node type;
    if (IsKw("fun")) {
      // fun(i32, string)  or  fun i32(string)
      Next();
      type = new Node("FunType", start);
      if (!IsOp("(")) {
        type.Add(new Node("Returns", Peek).Add(ParseType(false)));
      }
      type.Add(ParseTypeList());
    } else if (IsOp("(")) {
      type = ParseTupleType();
    } else {
      var name = ExpectIdent();
      if (name.Text == "vec") {
        type = new Node("VecType", name).Add(ParseType(false));
        ExpectOp(",");
        if (Peek.Kind != TokenKind.Int) {
          throw Error("expected vector length");
        }
        type.Text = Next().Text;
      } else {
        type = new Node("Type", name, name.Text);
        if (IsOp("<")) {
          Next();
          do {
            type.Add(ParseType());
          } while (AcceptOp(","));
          ExpectOp(">");
        }
      }
      // i32(string, u8): callback with return type
      if (allowCallback && IsOp("(")) {
        type = new Node("FunType", start).Add(new Node("Returns", start).Add(type)).Add(ParseTypeList());
      }
    }
    if (IsOp("?") && !PeekAt(1).Is("{")) {
      var q = Next();
      type = new Node("Nullable", q).Add(type);
    }
    return type;
  }

  Node ParseTypeList() {
    var open = ExpectOp("(");
    var list = new Node("ParamTypes", open);
    if (!IsOp(")")) {
      do {
        list.Add(ParseType());
      } while (AcceptOp(","));
    }
    ExpectOp(")");
    return list;
  }

  // (i32, r128, string)  or  (i32 val, string name)
  Node ParseTupleType() {
    var open = ExpectOp("(");
    var tuple = new Node("TupleType", open);
    do {
      var fieldType = ParseType();
      if (Peek.Kind == TokenKind.Ident) {
        var n = Next();
        tuple.Add(new Node("Field", n, n.Text).Add(fieldType));
      } else {
        tuple.Add(new Node("Field", open).Add(fieldType));
      }
    } while (AcceptOp(","));
    ExpectOp(")");
    return tuple;
  }

  // ---------------------------------------------------------------- declarations

  // fun [type[, type...]] name(params) { }   |   fun [type] name = lambda
  Node ParseFun() {
    var t = Next();
    var rets = new Node("Returns", t);
    while (!(Peek.Kind == TokenKind.Ident && (PeekAt(1).Is("(") || PeekAt(1).Is("=")))) {
      rets.Add(ParseType(false));
      if (!AcceptOp(",")) {
        break;
      }
    }
    var name = ExpectIdent();
    var node = new Node("Fun", name, name.Text);
    if (rets.Children.Count > 0) {
      node.Add(rets);
    }
    if (AcceptOp("=")) {
      node.Kind = "FunVar";
      node.Add(ParseExpr());
      return node;
    }
    node.Add(ParseParams());
    node.Add(ParseBlock());
    return node;
  }

  // (type name [= default], &type name, type name!, ...)
  Node ParseParams() {
    var open = ExpectOp("(");
    var ps = new Node("Params", open);
    SkipNewlines();
    if (!IsOp(")")) {
      do {
        SkipNewlines();
        bool byRef = AcceptOp("&");
        var type = ParseType();
        var name = ExpectIdent();
        var p = new Node(byRef ? "RefParam" : "Param", name, name.Text).Add(type);
        if (AcceptOp("!")) {
          p.Kind = "CopyParam";
        }
        if (IsOp("=")) {
          var eq = Next();
          p.Add(new Node("Default", eq).Add(ParseExpr()));
        }
        ps.Add(p);
        SkipNewlines();
      } while (AcceptOp(","));
    }
    ExpectOp(")");
    return ps;
  }

  // class Name(primary params) { members }
  Node ParseClass() {
    Next();
    var name = ExpectIdent();
    var node = new Node("Class", name, name.Text);
    if (IsOp("(")) {
      var ps = ParseParams();
      ps.Kind = "PrimaryCtor";
      node.Add(ps);
    }
    node.Add(ParseBlock(name.Text));
    return node;
  }

  // Name(params) [: Name(args)] [{ body }]
  Node ParseConstructor() {
    var name = Next();
    var node = new Node("Constructor", name, name.Text).Add(ParseParams());
    if (IsOp(":")) {
      var colon = Next();
      var target = ExpectIdent();
      node.Add(new Node("Chain", colon, target.Text).Add(ParseArgs()));
    }
    int save = pos;
    SkipNewlines();
    if (IsOp("{")) {
      node.Add(ParseBlock());
    } else {
      pos = save;
    }
    return node;
  }

  // enum [ValueType] Name { A, B: value, ..., fun ... }
  Node ParseEnum() {
    var t = Next();
    Node? valueType = null;
    bool nameOnly = Peek.Kind == TokenKind.Ident && (PeekAt(1).Is("{") || PeekAt(1).Kind == TokenKind.Newline);
    if (!nameOnly) {
      valueType = ParseType(false);
    }
    var name = ExpectIdent();
    var node = new Node("Enum", name, name.Text);
    if (valueType != null) {
      node.Add(new Node("ValueType", t).Add(valueType));
    }
    SkipNewlines();
    var open = ExpectOp("{");
    SkipSeparators();
    while (!IsOp("}")) {
      if (AtEnd) {
        throw ErrorAt("unclosed enum", open);
      }
      var mods = ParseModifiers();
      if (IsKw("fun")) {
        node.Add(WithMods(ParseFun(), mods));
      } else {
        var n = ExpectIdent();
        var entry = new Node("Entry", n, n.Text);
        if (AcceptOp(":")) {
          entry.Add(ParseExpr());
        }
        node.Add(entry);
      }
      SkipSeparators();
    }
    Next();
    return node;
  }

  // ---------------------------------------------------------------- expressions

  Node ParseExpr() {
    return ParseSwitchExpr();
  }

  // subject ? { 1: a, 2, 3: b, default: c }   |   cond ? { whenTrue, whenFalse }
  Node ParseSwitchExpr() {
    var subject = ParseCoalesce();
    if (!(IsOp("?") && PeekAt(1).Is("{"))) {
      return subject;
    }
    var q = Next();
    var open = Next();
    var sw = new Node("SwitchExpr", q).Add(subject);
    bool saved = noBrace;
    noBrace = false;
    var pending = new List<Node>();
    SkipNewlines();
    while (!IsOp("}")) {
      if (AtEnd) {
        throw ErrorAt("unclosed '{'", open);
      }
      var value = IsKw("default") ? new Node("Default", Next()) : ParseExpr();
      SkipNewlines();
      if (IsOp(":")) {
        var colon = Next();
        SkipNewlines();
        var labels = new Node("Values", colon);
        foreach (var p in pending) {
          labels.Add(p);
        }
        labels.Add(value);
        pending.Clear();
        sw.Add(new Node("Arm", colon).Add(labels).Add(ParseExpr()));
        SkipNewlines();
      } else {
        pending.Add(value);
      }
      if (!AcceptOp(",")) {
        SkipNewlines();
        if (!IsOp("}")) {
          throw Error("expected ',' or '}'");
        }
      }
      SkipNewlines();
    }
    Next();
    noBrace = saved;
    if (pending.Count > 0) {
      if (sw.Children.Count > 1) {
        throw ErrorAt("switch expression mixes labeled and unlabeled values", q);
      }
      if (pending.Count != 2) {
        throw ErrorAt("bool switch expression needs exactly 2 values: { whenTrue, whenFalse }", q);
      }
      sw.Kind = "BoolSwitchExpr";
      sw.Add(pending[0]).Add(pending[1]);
    }
    return sw;
  }

  Node ParseCoalesce() {
    var left = ParseRange();
    if (IsOp("??")) {
      var op = Next();
      SkipNewlines();
      return new Node("Binary", op, "??").Add(left).Add(ParseCoalesce());
    }
    return left;
  }

  Node ParseRange() {
    var left = ParseBinary(0);
    if (IsOp("..") || IsOp("..=")) {
      var op = Next();
      return new Node("Range", op, op.Text).Add(left).Add(ParseBinary(0));
    }
    if (IsOp("...")) {
      throw Error("'...' is reserved");
    }
    return left;
  }

  Node ParseBinary(int level) {
    if (level == Levels.Length) {
      return ParseUnary();
    }
    var left = ParseBinary(level + 1);
    while (true) {
      string? op = MatchBinaryOp(Levels[level]);
      if (op == null) {
        return left;
      }
      var tok = Next();
      if (op == ">>") {
        Next();
      }
      SkipNewlines();
      left = new Node("Binary", tok, op).Add(left).Add(ParseBinary(level + 1));
    }
  }

  string? MatchBinaryOp(string[] ops) {
    if (Peek.Kind != TokenKind.Op) {
      return null;
    }
    string? gt = PeekCompoundGt();
    if (gt != null) {
      return gt == ">>" && ops.Contains(">>") ? ">>" : null;
    }
    return ops.Contains(Peek.Text) ? Peek.Text : null;
  }

  Node ParseUnary() {
    var t = Peek;
    if (t.Kind == TokenKind.Op) {
      switch (t.Text) {
        case "!":
          Next();
          return new Node("Not", t).Add(ParseUnary());
        case "-":
          Next();
          return new Node("Neg", t).Add(ParseUnary());
        case "+":
          Next();
          return new Node("Plus", t).Add(ParseUnary());
        case "++":
          Next();
          return new Node("PreInc", t).Add(ParseUnary());
        case "--":
          Next();
          return new Node("PreDec", t).Add(ParseUnary());
        case "&":
          Next();
          return new Node("Ref", t).Add(ParseUnary());
        case "_":
          Next();
          if (StartsOperand(Peek)) {
            return new Node("BitNot", t).Add(ParseUnary());
          }
          return new Node("Discard", t);
        case "[":
          Next();
          var target = ParseType();
          ExpectOp("]");
          return new Node("Transmute", t).Add(target).Add(ParseUnary());
        case "(":
          var cast = TryParseCast();
          if (cast != null) {
            return cast;
          }
          break;
      }
    }
    return ParsePostfix(ParsePrimary());
  }

  // (type)expr
  Node? TryParseCast() {
    int save = pos;
    var open = Next();
    Node? type = null;
    bool isCast = false;
    if (Peek.Kind == TokenKind.Ident) {
      bool builtin = BuiltinTypes.Contains(Peek.Text);
      try {
        type = ParseType(false);
        if (IsOp(")")) {
          var after = PeekAt(1);
          isCast = builtin ? StartsOperand(after) : StartsCastOperand(after);
        }
      } catch (DfException) {
        isCast = false;
      }
    }
    if (!isCast || type == null) {
      pos = save;
      return null;
    }
    Next();
    return new Node("Cast", open).Add(type).Add(ParseUnary());
  }

  Node ParsePostfix(Node e) {
    while (true) {
      var t = Peek;
      // method chain continued on the next line: "\n.where(...)"
      if (IsNewline && (PeekAt(1).Is(".") || PeekAt(1).Is("?."))) {
        Next();
        continue;
      }
      if (t.Kind != TokenKind.Op) {
        return e;
      }
      switch (t.Text) {
        case "(":
          e = new Node("Call", t).Add(e).Add(ParseArgs());
          break;
        case ".":
        case "?.":
          Next();
          var m = ExpectMember();
          e = new Node(t.Text == "." ? "Member" : "SafeMember", m, m.Text).Add(e);
          break;
        case "[":
          Next();
          bool saved = noBrace;
          noBrace = false;
          SkipNewlines();
          var idx = ParseExpr();
          SkipNewlines();
          ExpectOp("]");
          noBrace = saved;
          e = new Node("Index", t).Add(e).Add(idx);
          break;
        case "++":
          Next();
          e = new Node("PostInc", t).Add(e);
          break;
        case "--":
          Next();
          e = new Node("PostDec", t).Add(e);
          break;
        case "!":
          Next();
          e = new Node("Copy", t).Add(e);
          break;
        default:
          return e;
      }
    }
  }

  Node ParsePrimary() {
    var t = Peek;
    switch (t.Kind) {
      case TokenKind.Int:
        Next();
        return new Node("Int", t, t.Text);
      case TokenKind.Real:
        Next();
        return new Node("Real", t, t.Text);
      case TokenKind.String:
        Next();
        return new Node("String", t, t.Text);
      case TokenKind.Char:
        Next();
        return new Node("Char", t, t.Text);
      case TokenKind.Ident:
        Next();
        return new Node("Name", t, t.Text);
      case TokenKind.Keyword:
        if (t.Text is "true" or "false") {
          Next();
          return new Node("Bool", t, t.Text);
        }
        if (t.Text == "null") {
          Next();
          return new Node("Null", t);
        }
        break;
      case TokenKind.Op:
        if (t.Text == "(") {
          return ParseParen();
        }
        if (t.Text == "{" && !noBrace) {
          return ParseCollection();
        }
        if (t.Text == "...") {
          throw Error("'...' is reserved");
        }
        break;
    }
    throw Error("expected expression");
  }

  int FindMatchingParen(int start) {
    int depth = 0;
    for (int i = start; i < toks.Count; i++) {
      var t = toks[i];
      if (t.Kind == TokenKind.Eof) {
        return -1;
      }
      if (t.Kind != TokenKind.Op) {
        continue;
      }
      if (t.Text == "(") {
        depth++;
      } else if (t.Text == ")") {
        depth--;
        if (depth == 0) {
          return i;
        }
      }
    }
    return -1;
  }

  // (expr)  |  (a, b, ...) tuple  |  (params) { body } lambda
  Node ParseParen() {
    if (!noBrace) {
      int close = FindMatchingParen(pos);
      if (close >= 0) {
        int after = close + 1;
        while (toks[after].Kind == TokenKind.Newline) {
          after++;
        }
        if (toks[after].Is("{")) {
          return ParseLambda();
        }
      }
    }
    var open = Next();
    bool saved = noBrace;
    noBrace = false;
    SkipNewlines();
    var tuple = new Node("Tuple", open);
    if (IsOp(")")) {
      Next();
      noBrace = saved;
      return tuple;
    }
    var first = ParseExpr();
    SkipNewlines();
    if (!IsOp(",")) {
      ExpectOp(")");
      noBrace = saved;
      return new Node("Paren", open).Add(first);
    }
    tuple.Add(first);
    while (AcceptOp(",")) {
      SkipNewlines();
      tuple.Add(ParseExpr());
      SkipNewlines();
    }
    ExpectOp(")");
    noBrace = saved;
    return tuple;
  }

  // (a, b) { a < b }   |   (string name) { ... }
  Node ParseLambda() {
    var open = ExpectOp("(");
    var ps = new Node("Params", open);
    SkipNewlines();
    if (!IsOp(")")) {
      do {
        SkipNewlines();
        var after = PeekAt(1);
        bool untyped = Peek.Kind == TokenKind.Ident
          && (after.Is(",") || after.Is(")") || after.Kind == TokenKind.Newline);
        if (untyped) {
          var n = Next();
          ps.Add(new Node("Param", n, n.Text));
        } else {
          var type = ParseType();
          var n = ExpectIdent();
          ps.Add(new Node("Param", n, n.Text).Add(type));
        }
        SkipNewlines();
      } while (AcceptOp(","));
    }
    ExpectOp(")");
    return new Node("Lambda", open).Add(ps).Add(ParseBlock());
  }

  // (a, b, name: c)
  Node ParseArgs() {
    var open = ExpectOp("(");
    var args = new Node("Args", open);
    bool saved = noBrace;
    noBrace = false;
    SkipNewlines();
    if (!IsOp(")")) {
      do {
        SkipNewlines();
        if (Peek.Kind == TokenKind.Ident && PeekAt(1).Is(":")) {
          var n = Next();
          Next();
          SkipNewlines();
          args.Add(new Node("Named", n, n.Text).Add(ParseExpr()));
        } else {
          args.Add(ParseExpr());
        }
        SkipNewlines();
      } while (AcceptOp(","));
    }
    ExpectOp(")");
    noBrace = saved;
    return args;
  }

  // { a, b, c }
  Node ParseCollection() {
    var open = Next();
    var list = new Node("List", open);
    SkipNewlines();
    while (!IsOp("}")) {
      if (AtEnd) {
        throw ErrorAt("unclosed '{'", open);
      }
      list.Add(ParseExpr());
      SkipNewlines();
      if (!AcceptOp(",")) {
        break;
      }
      SkipNewlines();
    }
    ExpectOp("}");
    return list;
  }
}
