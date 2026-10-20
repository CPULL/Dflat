using System.Text;
using dfparser;

namespace dfcompiler;

enum FrameKind {
  Function,
  Block,
  LoopBody,
  CatchBody,
  AtEndBody
}

// One block scope being compiled: its catches, its atEnd blocks, its labels and where its exceptions go.
sealed class Frame {
  public Frame(int id, FrameKind kind, Frame? parent, Scope parentScope) {
    Id = id;
    Kind = kind;
    Parent = parent;
    ParentScope = parentScope;
  }

  public int Id { get; }
  public FrameKind Kind { get; }
  public Frame? Parent { get; }
  public Scope ParentScope { get; }
  public Scope? StatementScope { get; set; }
  public List<Node> Statements { get; } = new();
  public List<Node> AtEnds { get; } = new();
  public List<(int Index, Node Node)> Catches { get; } = new();
  public HashSet<string> Labels { get; } = new();
  public Dictionary<string, VarInfo> Hoisted { get; } = new();
  public Scope? AtEndScope { get; set; }
  public int CurrentIndex { get; set; } = -1;
  public AssignState? StartAssign { get; set; }
  public string OuterHandler { get; set; } = "";
  public string? RanFlag { get; set; }
  public string? CaughtVar { get; set; }   // catch bodies: the C variable that holds the exception the catch owns (released when the body is left)

  // atEnd bodies: the scope they belong to, their position (1 = first written) and the exit kind
  public Frame? Owner { get; set; }
  public int AtEndIndex { get; set; }
  public bool ExceptionMode { get; set; }

  public string PendingVar => $"df_pend{Id}";
  public string ExcLabel(int step) => $"f{Id}_exc{step}";
  public string DispatchLabel(int ordinal) => $"f{Id}_dispatch{ordinal}";
  public string CatchLabel(int ordinal) => $"f{Id}_catch{ordinal}";
  public string LoopExcLabel => $"f{Id}_loopexc";
  public string CLabel(string name) => $"lbl_{name}_{Id}";

  // exception leaving the scope: run its atEnd blocks (exception mode), then the outer handler
  public string ExceptionExit => AtEnds.Count > 0 ? ExcLabel(AtEnds.Count) : OuterHandler;

  // where an exception raised at the current statement goes
  public string HandlerAt() {
    for (int ordinal = 0; ordinal < Catches.Count; ordinal++) {
      if (Catches[ordinal].Index > CurrentIndex) {
        return DispatchLabel(ordinal);
      }
    }
    return ExceptionExit;
  }
}

sealed class FunctionContext {
  public FunctionContext(int id, string header, string retType, bool isMain) {
    Id = id;
    Header = header;
    RetType = retType;
    IsMain = isMain;
    // every function has its own exception: the handlers of the scopes look at these
    Decls.Append("  df_exception* df_err = NULL;\n  bool df_err_thrown = false;\n  (void)df_err;\n  (void)df_err_thrown;\n");
  }

  // the function returns its result and its exception (see CodeGen.ExcFlow.cs); the function, for the name of the pair
  public FunSig? PairOf { get; set; }

  public int Id { get; }
  public string Header { get; }
  public string RetType { get; }
  public bool IsMain { get; }
  public string NameConst => $"df_fn{Id}";
  public string FailLabel => $"fn{Id}_fail";
  public string PanicLabel => $"fn{Id}_panic";
  public string RetVar => $"df_ret{Id}";
  public bool UsesRetVar { get; set; }
  public StringBuilder Decls { get; } = new();
}

// catch, throw, Exception, Panic, atEnd (spec 10).
public sealed partial class CodeGen {
  static readonly Dictionary<string, int> StandardExceptions = new() {
    ["Exception"] = 10,
    ["NullPointerException"] = 11,
    ["OverflowException"] = 12,
    ["InvalidCastException"] = 13,
    ["DivisionByZeroException"] = 14,
    ["OutOfMemoryException"] = 15,
    ["ChangingRefCountTypeException"] = 16,
    ["OutOfRangeCastException"] = 17,
    ["InvalidCharacterCodePointException"] = 18,
    ["IndexOutOfRangeException"] = 19,
    ["ModifyingProtectedSourceException"] = 20,
    // 21 to 23 are retired (spec 15.3): the collections use fallbacks without an exception
    ["InvalidDateTimeFormatException"] = 24,
    // Root.IO (spec 17.6.6)
    ["FileNotFoundException"] = 25,
    ["InvalidFileAccessException"] = 26,
    ["FileAccessException"] = 27,
    ["DirectoryNotEmptyException"] = 28,
    ["FileNameAlreadyExistsException"] = 29,
    ["InvalidPathException"] = 30,
    ["FullPathNotFoundException"] = 31,
    ["SeekBeforeBeginException"] = 32,
    ["FileAlreadyExistsException"] = 33,
    ["FileAccessToADirectoryException"] = 34,
    ["ReadOnlyItemCannotBeWrittenException"] = 35,
    ["ConflictingSharingException"] = 36,
    ["InvalidStreamModesException"] = 37,
    ["NoMoreResourcesException"] = 38,
    ["AccessToAClosedStreamException"] = 39,
    ["StreamModeNotAllowedException"] = 40,
    ["SeekNotSupportedByStreamException"] = 41,
    ["InvalidBufferRangeException"] = 42,
    ["GenericIOErrorException"] = 43,
    ["FileRemovedWhileOpenException"] = 44,
    ["StreamCloseFailureException"] = 45
  };

  readonly Stack<Frame> frames = new();
  readonly StringBuilder functionNames = new();
  readonly HashSet<FunSig> failingFuns = new();
  FunctionContext? fn;
  string? handlerOverride;

  // label an exception raised here jumps to
  string Handler => handlerOverride ?? frames.Peek().HandlerAt();

  // exception name (also root.X / Root.X) → its id: a standard exception, an exception class, or -2 for the
  // SilentException trait (every silent exception)
  int? ExceptionId(string name) {
    foreach (var prefix in new[] { "Root.", "root." }) {
      if (name.StartsWith(prefix)) {
        name = name[prefix.Length..];
      }
    }
    if (StandardExceptions.TryGetValue(name, out int id) || customExceptionIds.TryGetValue(name, out id)) {
      return id;
    }
    return name == "SilentException" ? -2 : null;
  }

  static string ExceptionName(int id) => StandardExceptions.First(pair => pair.Value == id).Key;

  // a member access on an object may find null: NullPointerException (only when the program has classes)
  bool MemberMayRaise(Node member) {
    if (classes.Count == 0) {
      return false;
    }
    int hops = 0;
    var current = member.Children[0];
    while (current.Kind == "Member") {
      hops++;
      current = current.Children[0];
    }
    if (current.Kind == "Name") {
      string root = current.Text!;
      if (hops == 0 && root is "this" or "parent") {
        return false;
      }
      bool staticRoot = root is "Root" or "root" || nameAliases.ContainsKey(root) || IsEnum(root);
      if (staticRoot) {
        return false;
      }
      if (classes.ContainsKey(root)) {
        return hops >= 1;
      }
      return true;
    }
    return current.Kind is not ("String" or "Interp" or "Int" or "Real" or "Char" or "Bool");
  }

  // a program with equals or an operator == in a class: any comparison may call it
  bool hasObjectEquality => classes.Values.Any(cls => cls.EqualsNode != null || funs.ContainsKey(cls.Name + ".=="));

  // after code of the runtime that may raise: it left the exception in df_pending; take it into df_err and go to the handler
  string CheckRaise(string code, string type) {
    string jump = $"{{ df_err = df_pending; df_pending = NULL; df_err_thrown = false; goto {Handler}; }}";
    if (type == "void") {
      return $"({{ {code}; if (df_pending) {jump} }})";
    }
    string temp = NewTemp();
    return $"({{ {CType(type)} {temp} = {code}; if (df_pending) {jump} {temp}; }})";
  }

  // "i32 divide(i32, i32)"
  static string FunHeader(FunSig f) {
    string ret = f.Ret == "void" ? "" : (IsMulti(f.Ret) ? string.Join(", ", TupleElems(f.Ret).Select(elem => elem.Type)) : f.Ret) + " ";
    return $"{ret}{f.Name}({string.Join(", ", f.Params.Select(p => p.Type))})";
  }

  FunctionContext NewFunctionContext(string header, string retType, bool isMain) {
    tempCounter++;
    var context = new FunctionContext(tempCounter, header, retType, isMain);
    var bytes = new List<int>(CodePoints(header));
    string literal = CString(bytes);
    // df_str_lit("...", n) → "..."
    string quoted = literal["df_str_lit(".Length..literal.LastIndexOf(',')];
    functionNames.Append($"static const char {context.NameConst}[] = {quoted};\n");
    return context;
  }

  // ---------------------------------------------------------------- frames

  Frame PrepareFrame(List<Node> statements, Scope parentScope, FrameKind kind) {
    statements = statements.Select(statement => statement.Kind == "AtEndOf" ? NormalizeAtEndOf(statement) : statement).ToList();
    tempCounter++;
    var frame = new Frame(tempCounter, kind, frames.Count > 0 ? frames.Peek() : null, parentScope);
    frame.Statements.AddRange(statements);
    for (int index = 0; index < statements.Count; index++) {
      var statement = statements[index];
      switch (statement.Kind) {
        case "AtEnd":
          frame.AtEnds.Add(statement);
          break;
        case "Catch":
          frame.Catches.Add((index, statement));
          break;
        case "Label":
          frame.Labels.Add(statement.Text!);
          break;
      }
    }
    if (frames.Count > 0 && kind is FrameKind.Block or FrameKind.LoopBody) {
      frame.OuterHandler = Handler;
    }
    // the objects of the scope end with it (after the atEnd blocks that were written)
    bool writtenAtEnds = frame.AtEnds.Any(atEnd => atEnd.Text != "drop");
    HashSet<Node>? ownerDecls = null;
    if (kind is not (FrameKind.AtEndBody or FrameKind.LoopBody)) {
      ownerDecls = AddImplicitDrops(frame, statements);
    }
    if (frame.AtEnds.Count > 0) {
      HoistDeclarations(frame, writtenAtEnds ? null : ownerDecls);
      fn!.Decls.Append($"  df_exception* {frame.PendingVar} = NULL;\n");
      if (kind == FrameKind.LoopBody) {
        frame.RanFlag = $"f{frame.Id}_ran";
      }
    }
    return frame;
  }

  // atEnd sees every variable of its scope, wherever declared: they are created at the start
  // of the scope with their default value
  void HoistDeclarations(Frame frame, HashSet<Node>? only) {
    // the type of 'var x = Foo(n)' needs the variables declared before it in this scope
    var typingScope = new Scope(frame.ParentScope);
    void Hoist(Node decl) {
      if (only != null && !only.Contains(decl)) {
        try {
          typingScope.Vars[decl.Text!] = new VarInfo(TypeForHoist(decl, typingScope), "", false);
        } catch (CompileException) {
          // a type that cannot be worked out yet: the name is simply not visible to the later declarations
        }
        return;
      }
      string name = decl.Text!;
      if (frame.Hoisted.ContainsKey(name)) {
        throw Error($"'{name}' is already declared in this scope", decl);
      }
      var mods = decl.Children.FirstOrDefault(child => child.Kind == "Mods");
      bool isConst = mods?.Text?.Split(' ').Contains("const") == true;
      string type = TypeForHoist(decl, typingScope);
      frame.Hoisted[name] = new VarInfo(type, $"v_{name}_{frame.Id}", isConst) {
        Own = declOwner.TryGetValue(decl, out bool owns) && owns,
        IsWeak = decl.Children.Any(child => child.Kind == "WeakInit")
      };
      typingScope.Vars[name] = frame.Hoisted[name];
    }
    foreach (var statement in frame.Statements) {
      if (statement.Kind == "Decl") {
        Hoist(statement);
      } else if (statement.Kind == "MultiAssign") {
        foreach (var target in statement.Children[0].Children.Where(target => target.Kind == "Decl")) {
          Hoist(target);
        }
      }
    }
    var atEndScope = new Scope(frame.ParentScope);
    foreach (var (name, variable) in frame.Hoisted) {
      atEndScope.Vars[name] = variable;
    }
    frame.AtEndScope = atEndScope;
  }

  // the type of a declaration while the scope is prepared: only the type is wanted, so the code the expression makes is dropped
  // (it needs a label for exceptions, and the frame that has them is not open yet)
  string TypeForHoist(Node decl, Scope scope) {
    string? saved = handlerOverride;
    handlerOverride ??= "df_hoist_typing";
    try {
      return ResolveDeclType(decl, scope);
    } finally {
      handlerOverride = saved;
    }
  }

  void EmitHoists(Frame frame, StringBuilder sb, string pad) {
    foreach (var variable in frame.Hoisted.Values) {
      sb.Append($"{pad}{CType(variable.Type)} {variable.CName} = {ZeroValue(variable.Type)};\n");
    }
    if (frame.RanFlag != null) {
      sb.Append($"{pad}bool {frame.RanFlag} = false;\n");
    }
  }

  // a block with its own scope (the caller writes the braces)
  void EmitBlock(Node block, StringBuilder sb, Scope scope, int indent) {
    var frame = PrepareFrame(block.Children, scope, FrameKind.Block);
    EmitHoists(frame, sb, Pad(indent));
    EmitFrameBody(frame, sb, indent);
  }

  // a single statement compiled as a block (if-then, case bodies)
  void EmitStatementAsBlock(Node statement, StringBuilder sb, Scope scope, int indent) {
    var block = statement.Kind == "Block" ? statement : Synthetic("Block", statement).Add(statement);
    EmitBlock(block, sb, scope, indent);
  }

  void EmitFrameBody(Frame frame, StringBuilder sb, int indent) {
    string pad = Pad(indent);
    frames.Push(frame);
    var scope = new Scope(frame.ParentScope);
    frame.StatementScope = scope;
    frame.StartAssign = assign.Clone();
    int catchOrdinal = 0;
    for (int index = 0; index < frame.Statements.Count; index++) {
      frame.CurrentIndex = index;
      var statement = frame.Statements[index];
      if (statement.Kind == "AtEnd") {
        continue; // compile-time: placed at every exit of the scope
      }
      if (statement.Kind == "Catch") {
        EmitCatch(frame, catchOrdinal, statement, sb, indent);
        catchOrdinal++;
        continue;
      }
      EmitStmt(statement, sb, scope, indent);
    }
    frame.CurrentIndex = frame.Statements.Count;

    bool isLoop = frame.Kind == FrameKind.LoopBody;
    if (!isLoop) {
      // normal end of the scope
      for (int step = frame.AtEnds.Count; step >= 1; step--) {
        EmitAtEnd(frame, step, false, sb, indent);
      }
    }
    bool hasTail = frame.Catches.Count > 0 || (!isLoop && frame.AtEnds.Count > 0);
    if (hasTail) {
      string done = $"f{frame.Id}_done";
      sb.Append($"{pad}goto {done};\n");
      EmitDispatch(frame, sb, pad);
      if (!isLoop) {
        EmitExceptionChain(frame, sb, indent);
      }
      sb.Append($"{pad}{done}: ;\n");
    }
    frames.Pop();
  }

  // ---------------------------------------------------------------- catch

  // the exception of a catch belongs to it and ends with it (spec 15.1): the variable cannot be stored, returned or moved out of the catch
  void CheckCatchVariableStays(Node node, string name) {
    bool IsVariable(Node n) => n.Kind == "Name" && n.Text == name;
    bool escapes = node.Kind switch {
      "Return" => node.Children.Any(child => IsVariable(child)),
      "Decl" => node.Children.FirstOrDefault(child => child.Kind is "Init" or "WeakInit") is { } init && IsVariable(init.Children[0]),
      "Assign" => node.Text == "=" && node.Children.Count == 2 && IsVariable(node.Children[1]),
      "Gift" => IsVariable(node.Children[0]),
      "List" or "Tuple" => node.Children.Any(child => IsVariable(child)),
      _ => false
    };
    if (escapes) {
      throw Error($"the exception '{name}' belongs to its catch and ends with it: it cannot be stored, returned or moved out; copy its Id and Message, or throw it", node);
    }
    foreach (var child in node.Children) {
      CheckCatchVariableStays(child, name);
    }
  }

  // (exception id or null for everything, variable name or null)
  (int? Id, string? Variable) CatchTarget(Node catchNode) {
    var typeNode = catchNode.Children.FirstOrDefault(child => child.Kind == "Type");
    string? name = catchNode.Text;
    if (typeNode != null) {
      string typeName = ResolveCatchName(typeNode.Text!);
      if (typeName == "all") {
        return (null, name);
      }
      return (ExceptionId(typeName) ?? throw UnknownException(typeName, catchNode), name);
    }
    if (name == null) {
      return (null, null);              // catch { }: same as catch all
    }
    if (name == "all") {
      return (null, null);              // catch all { }
    }
    string resolved = ResolveCatchName(name);
    if (ExceptionId(resolved) is int id) {
      return (id, null);                // catch DivisionByZeroException { }
    }
    // a catch starts with an exception type: there is no shorthand with the variable alone (spec 15.1)
    throw Error($"unknown exception type '{name}': a catch names an exception type, then an optional variable (catch Exception {name} gets any exception)", catchNode);
  }

  // first segment through name aliases: r.DivisionByZeroException → Root.DivisionByZeroException
  string ResolveCatchName(string name) {
    int dot = name.IndexOf('.');
    string first = dot < 0 ? name : name[..dot];
    if (nameAliases.TryGetValue(first, out var target)) {
      return target + (dot < 0 ? "" : name[dot..]);
    }
    return name;
  }

  static CompileException UnknownException(string name, Node at) {
    return Error($"unknown exception type '{name}'", at);
  }

  // the class named by a catch (catch MyException ex), when the exception is a class of the program
  ClassInfo? CatchClass(Node catchNode) {
    var typeNode = catchNode.Children.FirstOrDefault(child => child.Kind == "Type");
    if (typeNode == null) {
      return null;
    }
    string typeName = ResolveCatchName(typeNode.Text!);
    return classes.TryGetValue(typeName, out var cls) && cls.IsException ? cls : null;
  }

  void EmitCatch(Frame frame, int ordinal, Node catchNode, StringBuilder sb, int indent) {
    string pad = Pad(indent);
    var (id, variable) = CatchTarget(catchNode);
    if (catchNode.Text == null && !catchNode.Children.Any(child => child.Kind == "Type")) {
      Warn("'catch { }' catches everything, like 'catch all { }'", catchNode, 2);
    }
    string skip = $"f{frame.Id}_skip{ordinal}";
    string cbexit = $"f{frame.Id}_cbexit{ordinal}";
    string cbdone = $"f{frame.Id}_cbdone{ordinal}";
    sb.Append($"{pad}goto {skip};\n");
    sb.Append($"{pad}{frame.CatchLabel(ordinal)}: {{\n");
    tempCounter++;
    string caught = $"df_caught{tempCounter}";
    sb.Append($"{pad}  df_exception* {caught} = df_err;\n");
    var catchScope = new Scope(frame.StatementScope);
    // the catch runs from any point above it: assignments made there may be missing
    var normalFlow = assign;
    assign = frame.StartAssign!.Clone();
    if (variable != null) {
      CheckNotShadowing(variable, catchScope, catchNode);
      tempCounter++;
      string cname = $"v_{variable}_{tempCounter}";
      string variableType = CatchClass(catchNode)?.Name ?? "Exception";
      sb.Append($"{pad}  {CType(variableType)} {cname} = ({CType(variableType)}){caught};\n");
      catchScope.Vars[variable] = new VarInfo(variableType, cname, true);
      CheckCatchVariableStays(catchNode.Children.First(child => child.Kind == "Block"), variable);
    }
    sb.Append($"{pad}  df_err = NULL;\n");
    var block = catchNode.Children.First(child => child.Kind == "Block");
    var body = PrepareFrame(block.Children, catchScope, FrameKind.CatchBody);
    body.OuterHandler = cbexit;
    body.CaughtVar = caught;
    EmitHoists(body, sb, pad + "  ");
    EmitFrameBody(body, sb, indent + 1);
    sb.Append($"{pad}  df_exc_release({caught});\n");
    sb.Append($"{pad}  goto {cbdone};\n");
    // a throw goes to the enclosing level (later catches don't see it); any other error is a panic. The exception the catch owned is released
    // unless the body threw that same one again
    sb.Append($"{pad}  {cbexit}: if (df_err_thrown) {{ if (df_err != {caught}) {{ df_exc_release({caught}); }} goto {frame.ExceptionExit}; }}\n");
    sb.Append($"{pad}  goto {fn!.PanicLabel};\n");
    sb.Append($"{pad}  {cbdone}: ;\n");
    sb.Append($"{pad}}}\n");
    sb.Append($"{pad}{skip}: ;\n");
    assign = AssignState.Merge(new[] { normalFlow, assign });
  }

  // catches are checked in order; the first one that handles the type runs
  void EmitDispatch(Frame frame, StringBuilder sb, string pad) {
    for (int ordinal = 0; ordinal < frame.Catches.Count; ordinal++) {
      sb.Append($"{pad}{frame.DispatchLabel(ordinal)}:\n");
      bool caughtAll = false;
      for (int next = ordinal; next < frame.Catches.Count; next++) {
        var (id, _) = CatchTarget(frame.Catches[next].Node);
        if (id == null) {
          sb.Append($"{pad}  goto {frame.CatchLabel(next)};\n");
          caughtAll = true;
          break;
        }
        string test = id == -2 ? "df_err->descriptor->silent" : $"df_exc_is(df_err, {id})";
        sb.Append($"{pad}  if ({test}) goto {frame.CatchLabel(next)};\n");
      }
      if (!caughtAll) {
        sb.Append($"{pad}  goto {frame.ExceptionExit};\n");
      }
    }
  }

  // ---------------------------------------------------------------- atEnd

  // exception leaving the scope: atEnd blocks from last to first, then the outer handler.
  // Entering at step k (an atEnd that raised) runs only the earlier ones.
  void EmitExceptionChain(Frame frame, StringBuilder sb, int indent) {
    if (frame.AtEnds.Count == 0) {
      return;
    }
    string pad = Pad(indent);
    for (int step = frame.AtEnds.Count; step >= 1; step--) {
      sb.Append($"{pad}{frame.ExcLabel(step)}: if (df_err) {{ {frame.PendingVar} = df_err; df_err = NULL; }}\n");
      EmitAtEnd(frame, step, true, sb, indent);
    }
    // reached with an error of its own (an atEnd that failed at the normal end) it keeps that error
    sb.Append($"{pad}{frame.ExcLabel(0)}: if (df_err == NULL) {{ df_err = {frame.PendingVar}; }} {frame.PendingVar} = NULL;\n");
    sb.Append($"{pad}goto {frame.OuterHandler};\n");
  }

  // the atEnd body, inlined at an exit of its scope
  void EmitAtEnd(Frame owner, int step, bool exceptionMode, StringBuilder sb, int indent) {
    string pad = Pad(indent);
    var block = owner.AtEnds[step - 1].Children[0];
    int savedLoopDepth = loopDepth;
    var savedSwitches = switches.ToArray();
    string? savedOverride = handlerOverride;
    loopDepth = 0;
    switches.Clear();
    handlerOverride = null;
    var savedAssign = assign;
    assign = assign.Clone();
    atEndDepth++;

    var frame = PrepareFrame(block.Children, owner.AtEndScope!, FrameKind.AtEndBody);
    frame.Owner = owner;
    frame.AtEndIndex = step;
    frame.ExceptionMode = exceptionMode;
    // an atEnd block has no error part (spec 15.7.6): an exception that is not silent and is not handled inside it is a panic,
    // whether or not another exception is passing through
    frame.OuterHandler = fn!.PanicLabel;
    sb.Append($"{pad}{{\n");
    EmitHoists(frame, sb, pad + "  ");
    EmitFrameBody(frame, sb, indent + 1);
    sb.Append($"{pad}}}\n");

    atEndDepth--;
    assign = savedAssign;
    loopDepth = savedLoopDepth;
    foreach (var context in savedSwitches.Reverse()) {
      switches.Push(context);
    }
    handlerOverride = savedOverride;
  }

  // loop body scope: its atEnd blocks run once, after the loop, and only if the body ran
  void EmitLoopTail(Frame body, StringBuilder sb, int indent) {
    if (body.AtEnds.Count == 0) {
      return;
    }
    string pad = Pad(indent);
    sb.Append($"{pad}if ({body.RanFlag}) {{\n");
    for (int step = body.AtEnds.Count; step >= 1; step--) {
      EmitAtEnd(body, step, false, sb, indent + 1);
    }
    sb.Append($"{pad}}}\n");
    string done = $"f{body.Id}_loopdone";
    sb.Append($"{pad}goto {done};\n");
    // an error in the loop header after the body ran also ends the body scope
    sb.Append($"{pad}{body.LoopExcLabel}: if ({body.RanFlag}) goto {body.ExcLabel(body.AtEnds.Count)};\n");
    sb.Append($"{pad}goto {body.OuterHandler};\n");
    EmitExceptionChain(body, sb, indent);
    sb.Append($"{pad}{done}: ;\n");
  }

  // ---------------------------------------------------------------- leaving scopes

  // break, continue, jump: run the atEnd blocks of every scope left, up to (not including) stop
  void EmitUnwindTo(Frame? stop, StringBuilder sb, int indent, Node at) {
    for (var frame = frames.Peek(); frame != stop; frame = frame.Parent) {
      if (frame == null) {
        throw Error("internal error: scope not found", at);
      }
      if (frame.Kind == FrameKind.AtEndBody) {
        throw Error("'break' and 'jump' can only move inside the atEnd block", at);
      }
      for (int step = frame.AtEnds.Count; step >= 1; step--) {
        EmitAtEnd(frame, step, false, sb, indent);
      }
      if (frame.Kind == FrameKind.CatchBody && frame.CaughtVar != null) {
        sb.Append($"{Pad(indent)}df_exc_release({frame.CaughtVar});\n");   // leaving the catch: the exception it owned ends
      }
    }
  }

  Frame FindLoopFrame(Node at, string keyword) {
    for (var frame = frames.Peek(); frame != null; frame = frame.Parent) {
      if (frame.Kind == FrameKind.LoopBody) {
        return frame;
      }
      if (frame.Kind == FrameKind.AtEndBody) {
        break;
      }
    }
    throw Error($"'{keyword}' outside a loop", at);
  }

  Frame FindLabelFrame(string name, Node at) {
    for (var frame = frames.Peek(); frame != null; frame = frame.Parent) {
      if (frame.Labels.Contains(name)) {
        return frame;
      }
      if (frame.Kind == FrameKind.AtEndBody) {
        throw Error($"'jump' can only move inside the atEnd block; #{name}# is outside", at);
      }
    }
    throw Error($"label #{name}# not found in this block or an outer one", at);
  }

  void EmitJumpToLabel(string name, StringBuilder sb, int indent, Node at) {
    var target = FindLabelFrame(name, at);
    EmitUnwindTo(target, sb, indent, at);
    sb.Append($"{Pad(indent)}goto {target.CLabel(name)};\n");
  }

  // does a return from here pass any atEnd block?
  bool ReturnPassesAtEnd() {
    for (var frame = frames.Peek(); frame != null; frame = frame.Parent) {
      if (frame.AtEnds.Count > 0 || frame.Kind is FrameKind.AtEndBody or FrameKind.CatchBody) {
        return true;
      }
    }
    return false;
  }

  // inside an atEnd block run while an exception passes through: a return value is ignored
  bool ReturnIgnored() {
    for (var frame = frames.Peek(); frame != null; frame = frame.Kind == FrameKind.AtEndBody ? frame.Owner!.Parent : frame.Parent) {
      if (frame.Kind == FrameKind.AtEndBody) {
        return frame.ExceptionMode;
      }
    }
    return false;
  }

  // return: atEnd blocks of every scope up to the function, then return the stored value
  void EmitReturnUnwind(StringBuilder sb, int indent) {
    string pad = Pad(indent);
    var frame = frames.Peek();
    while (frame != null) {
      for (int step = frame.AtEnds.Count; step >= 1; step--) {
        EmitAtEnd(frame, step, false, sb, indent);
      }
      if (frame.Kind == FrameKind.CatchBody && frame.CaughtVar != null) {
        sb.Append($"{pad}df_exc_release({frame.CaughtVar});\n");
      }
      if (frame.Kind == FrameKind.AtEndBody) {
        var owner = frame.Owner!;
        if (frame.ExceptionMode) {
          // the exception keeps propagating through the earlier atEnd blocks
          sb.Append($"{pad}goto {owner.ExcLabel(frame.AtEndIndex - 1)};\n");
          return;
        }
        for (int step = frame.AtEndIndex - 1; step >= 1; step--) {
          EmitAtEnd(owner, step, false, sb, indent);
        }
        frame = owner.Parent;
        continue;
      }
      frame = frame.Parent;
    }
    if (fn!.IsMain) {
      sb.Append($"{pad}return (int){fn.RetVar};\n");
    } else if (fn.PairOf != null) {
      sb.Append($"{pad}return {PairValue(fn.PairOf, fn.RetVar)};\n");
    } else if (fn.RetType == "void") {
      sb.Append($"{pad}return;\n");
    } else {
      sb.Append($"{pad}return {fn.RetVar};\n");
    }
  }

  // ---------------------------------------------------------------- throw

  // throw Exception("message") | throw DivisionByZeroException() | throw ex | throw Panic(code)
  // true when the statement leaves: a silent exception that nothing can catch does nothing
  bool EmitThrow(Node s, StringBuilder sb, Scope scope, string pad) {
    var value = s.Children[0];
    if (value.Kind == "Name" && scope.Find(value.Text!) is VarInfo variable) {
      if (variable.Type != "Exception" && !IsExceptionClass(variable.Type)) {
        throw Error($"'{value.Text}' is not an exception", s);
      }
      string rethrow = $"{{ df_err = (df_exception*){variable.CName}; df_err_thrown = true; goto {Handler}; }}";
      bool mayBeSilent = variable.Type == "Exception" || (IsExceptionClass(variable.Type) && IsSilentClass(classes[variable.Type]));
      if (!mayBeSilent || (currentFun?.CanEmitSilent == true && atEndDepth == 0)) {
        sb.Append($"{pad}{rethrow}\n");
        return true;
      }
      // a silent exception cannot leave here (constructor, destructor, entry point): only a catch of this function sees it
      string silentTest = silentBit.Count > 0
        ? $"!((df_exception*){variable.CName})->descriptor->silent || ({ObservedMask()} & df_obs_bit((df_exception*){variable.CName}))"
        : $"!((df_exception*){variable.CName})->descriptor->silent";
      sb.Append($"{pad}if ({silentTest}) {rethrow}\n");
      return false;
    }
    if (value.Kind != "Call") {
      throw Error("throw needs an exception, e.g. throw Exception(\"message\")", s);
    }
    var args = value.Children[1].Children;
    string name = Path(value.Children[0]) ?? throw Error("throw needs an exception, e.g. throw Exception(\"message\")", s);
    if (classes.ContainsKey(name)) {
      return ThrowObject(s, value, name, sb, scope, pad);
    }
    if (args.Any(arg => arg.Kind == "Named")) {
      throw Error("exceptions take no named arguments yet", s);
    }
    if (name == "Panic") {
      if (args.Count != 1) {
        throw Error("Panic takes one exit code", s);
      }
      var code = ExprOf(args[0], scope, "i64");
      if (!IsInt(code.Type)) {
        throw Error("the Panic exit code must be an integer", s);
      }
      sb.Append($"{pad}df_panic((int64_t)({code.Code}));\n");
      return true;
    }
    int id = ExceptionId(name) ?? throw UnknownException(name, s);
    if (id < 0) {
      throw Error("SilentException is a trait: throw an exception class", s);
    }
    string? condition = SilentStandard.Contains(id) ? SilentThrowCondition(id) : "";
    if (condition == null) {
      return false;
    }
    if (args.Count > 1) {
      throw Error($"{ExceptionName(id)} takes only an optional message", s);
    }
    string message = args.Count == 1 ? Convert(ExprOf(args[0], scope, "string"), "string", s) : "df_str_lit(\"\", 0)";
    string temp = NewTemp();
    string raise = $"{{ df_string {temp} = {message}; "
      + $"df_err = df_new_exception(&df_desc_{ExceptionName(id)}, {temp}.p, {temp}.len, {fn!.NameConst}); "
      + $"df_err_thrown = true; goto {Handler}; }}";
    sb.Append(condition == "" ? $"{pad}{raise}\n" : $"{pad}if ({condition}) {raise}\n");
    return condition == "";
  }

  // ex.Id, ex.Message, ex.Function, ex.Line
  Expr ExceptionMember(Expr exception, string member, Node at) {
    return member switch {
      "Id" => new Expr($"(({exception.Code})->id)", "u64"),
      "Message" => new Expr($"df_str_dup_cstr(({exception.Code})->message)", "string"),
      "Function" => new Expr($"df_str_dup_cstr(({exception.Code})->function)", "string"),
      "Line" => new Expr($"(({exception.Code})->line)", "u64"),
      _ => throw Error($"member '{member}' of Exception is not supported yet", at)
    };
  }
}
