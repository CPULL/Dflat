using System.Text;
using dfparser;

namespace dfcompiler;

// Globals (spec 18): global Cache cache = Cache(123) in the root file. A C static that owns its object, created in declaration order
// at program start and destroyed in reverse order at the end. Part 1: declaration, life cycle and the rules of use (18.1-18.3);
// the locks, the rank checks and the lambda rule come later.
public sealed partial class CodeGen {
  sealed record GlobalInfo(string Name, string CName, string Type, int Rank, Node Decl) {
    public string? Library => SourceMap.LibraryOf(Decl.Line);   // a global of a library: visible only inside it (spec 18.1)
  }

  readonly Dictionary<string, GlobalInfo> globals = new();
  readonly List<GlobalInfo> globalOrder = new();
  FunSig? globalsInit;

  static bool IsGlobalDecl(Node node) => node.Kind == "Decl" && ModsOf(node).Contains("global");

  // the top-level globals of the root file: registered, their initializers moved to the function __globals, run at program start
  void DeclareGlobals(Node program) {
    var decls = program.Children.Where(IsGlobalDecl).ToList();
    if (decls.Count == 0) {
      return;
    }
    var at = decls[0];
    var block = Synthetic("Block", at);
    allGlobalNames.UnionWith(decls.Select(decl => decl.Text!));
    foreach (var decl in decls) {
      string name = decl.Text!;
      if (decl.Line >= SourceMap.Stride && !SourceMap.IsLibraryRoot(decl.Line)) {
        throw Error("a global can be declared only in the root file (program.df, main.df or lib.df) (spec 18.1)", decl);
      }
      if (ModsOf(decl).Length > 1) {
        throw Error("a global takes no other modifier: global <Class> <name> = <Class>(...) (spec 18.1)", decl);
      }
      if (globals.ContainsKey(name) || globalConsts.ContainsKey(name)) {
        throw Error($"'{name}' is already declared in this scope", decl);
      }
      var typeNode = decl.Children.FirstOrDefault(child => child.Kind == "Type");
      if (typeNode == null || typeNode.Text == "var") {
        throw Error("a global must be an object of a class: global <Class> <name> = <Class>(...) (spec 18.1)", decl);
      }
      var init = decl.Children.FirstOrDefault(child => child.Kind == "Init")
        ?? throw Error("a global needs its initializer: global <Class> <name> = <Class>(...) (spec 18.1)", decl);
      if (decl.Children.Any(child => child.Kind is "Counted" or "Weak" or "Nullable")) {
        throw Error("a global must be an object of a class: global <Class> <name> = <Class>(...) (spec 18.1)", decl);
      }
      string type = ResolveDeclType(decl, null);
      if (!classes.ContainsKey(type) || IsNullable(type)) {
        throw Error("a global must be an object of a class: global <Class> <name> = <Class>(...) (spec 18.1)", decl);
      }
      var info = new GlobalInfo(name, $"gl_{name}", type, globalOrder.Count, decl);
      CheckInitializerOrder(init, info);
      globals[name] = info;
      globalOrder.Add(info);
      classGlobals.Append($"static {CType(type)} {info.CName};\n");
      block.Add(Synthetic("Assign", decl, "=").Add(Synthetic("Name", decl, name)).Add(init.Children[0]));
      program.Children.Remove(decl);
    }
    var node = Synthetic("Fun", at, "__globals").Add(Synthetic("Params", at)).Add(block);
    globalsInit = new FunSig("__globals", "void", new List<ParamSig>(), node, null) { IsStaticInit = true };
    funs[globalsInit.Name] = new List<FunSig> { globalsInit };
    FindLockedMethods();
    classGlobals.Append(GlobalsRuntimeCode());
  }

  // ---------------------------------------------------------------- locks (spec 18.4, 18.5)

  // the methods that can run on a global: the methods of its class, of its parents and the ones that answer its traits. Each one
  // becomes a wrapper that takes the lock of its object when the object is a global (a plain object has no lock), runs the body
  // (f_X_m__body) and releases the lock: the arguments are evaluated before the lock, and an exception passes through the return
  readonly HashSet<FunSig> lockedMethods = new();

  void FindLockedMethods() {
    var owners = new HashSet<ClassInfo>();
    var answers = new HashSet<FunSig>();
    foreach (var global in globalOrder) {
      for (var cls = classes[global.Type]; cls != null; cls = cls.Parent) {
        owners.Add(cls);
        CheckNoFunctionValues(cls, global);
        foreach (var impl in cls.Impls) {
          answers.UnionWith(impl.Methods);
          CheckNoFunctionValues(impl.Trait, global);
        }
      }
    }
    foreach (var f in funs.Values.SelectMany(list => list).Where(f => f.Class != null && owners.Contains(f.Class)).Concat(answers)) {
      if (!f.IsStatic && !f.IsCtor && !f.IsInit && !f.IsDtor && !f.IsAccessor && !f.IsAbstract && !f.IsStaticInit && !f.IsExtern
          && f.Struct == null && f.LambdaCtx == null && f.Class != null) {
        lockedMethods.Add(f);
      }
    }
  }

  // spec 18.6: a class used by a global (its own, its parents, its traits) has no lambdas and no function values: no fields, parameters,
  // results, locals or calls of function types. What a lambda does cannot be known at compile time
  void CheckNoFunctionValues(ClassInfo cls, GlobalInfo global) {
    foreach (var field in cls.Fields.Where(field => IsFunType(field.Type))) {
      throw Error($"class '{cls.Name}' is used by global '{global.Name}' and cannot have lambdas or function values (spec 18.6)", field.At);
    }
    if (FirstFunctionValue(cls.Node) is Node found) {
      throw Error($"class '{cls.Name}' is used by global '{global.Name}' and cannot have lambdas or function values (spec 18.6)", found);
    }
  }

  static Node? FirstFunctionValue(Node node) {
    if (node.Kind is "Lambda" or "FunType") {
      return node;
    }
    foreach (var child in node.Children) {
      if (FirstFunctionValue(child) is Node found) {
        return found;
      }
    }
    return null;
  }

  // read lock or write lock (spec 18.4): an unrestricted method does not change its object (spec 1.6.2), so it reads, unless what it can
  // reach calls back into a global of its class to write it (an object it owns, a function, another class), or calls a lambda: then it
  // writes. The read lock of a global cannot become a write lock (ReadLockBecameWriteLockException stays a safety net)
  readonly Dictionary<FunSig, bool> lockWrites = new();

  bool LockWrites(FunSig f) {
    if (!f.IsUnrestricted) {
      return true;
    }
    if (!lockWrites.TryGetValue(f, out bool writes)) {
      var own = globalOrder.Where(global => IsSubtype(global.Type, f.Class!.Name)).Select(global => global.Name).ToHashSet();
      writes = own.Count > 0 && ReachesGlobalWrite(f, own, new HashSet<FunSig>());
      lockWrites[f] = writes;
    }
    return writes;
  }

  bool ReachesGlobalWrite(FunSig f, HashSet<string> own, HashSet<FunSig> seen) {
    if (!seen.Add(f) || f.IsExtern || f.IsAbstract && !f.Node.Children.Any(child => child.Kind == "Block")) {
      return false;
    }
    return NodeReachesGlobalWrite(f.Node, f, own, seen);
  }

  bool NodeReachesGlobalWrite(Node node, FunSig context, HashSet<string> own, HashSet<FunSig> seen) {
    if (node.Kind == "Lambda") {
      return true;   // a lambda can do anything
    }
    if (globalWrites.TryGetValue(node, out var written) && own.Contains(written.Name)) {
      return true;   // g.f = v on a global of the class
    }
    if (node.Kind == "Call" && node.Children.Count > 0) {
      var callee = node.Children[0];
      if (callee.Kind is "Lambda" or "Call" or "Paren") {
        return true;   // a function value
      }
      if (callee is { Kind: "Member" } member && member.Children.Count == 1 && member.Children[0] is { Kind: "Name" } receiver
          && globals.TryGetValue(receiver.Text!, out var called)) {
        var methods = LookupMethods(ObjInfo(called.Type), member.Text!);
        if (own.Contains(called.Name) && methods.Any(method => !method.IsUnrestricted)) {
          return true;   // g.write() on a global of the class
        }
        if (methods.Any(method => ReachesGlobalWrite(method, own, seen))) {
          return true;
        }
      } else if (callee.Kind == "Member") {
        // a method of another object: every method of that name in the program (its class is not known here)
        string suffix = "." + callee.Text;
        if (funs.Values.SelectMany(list => list).Where(f => f.Name.EndsWith(suffix)).ToList().Any(f => ReachesGlobalWrite(f, own, seen))) {
          return true;
        }
      } else if (callee.Kind == "Name") {
        string name = callee.Text!;
        var targets = context.Class != null ? LookupMethods(context.Class, name) : new List<FunSig>();
        if (targets.Count == 0 && funs.TryGetValue(name, out var free)) {
          targets = free;
        }
        if (targets.Count == 0 && !classes.ContainsKey(name) && !Stdlib.ContainsKey(name) && !genericFuns.ContainsKey(name)
            && !genericClasses.ContainsKey(name) && !traits.ContainsKey(name)) {
          return true;   // f() of a function value
        }
        if (targets.ToList().Any(f => ReachesGlobalWrite(f, own, seen))) {
          return true;
        }
      }
    }
    foreach (var child in node.Children) {
      if (NodeReachesGlobalWrite(child, context, own, seen)) {
        return true;
      }
    }
    return false;
  }

  // the C of the locks: a lock per global with its rank, the list of the locks this thread holds (one list: no threads yet)
  string GlobalsRuntimeCode() {
    var sb = new StringBuilder();
    sb.Append("/* globals (spec 18): a read/write lock per global, the locks held by the thread (one list: no threads yet) */\n");
    sb.Append("typedef struct { int32_t rank; int32_t readers; bool writer; } df_glock;\n");
    sb.Append("typedef struct { df_glock* lock; bool write; bool taken; } df_gl_hold;\n");
    sb.Append("static df_gl_hold* df_gl_held = NULL;\nstatic size_t df_gl_depth = 0;\nstatic size_t df_gl_cap = 0;\n");
    sb.Append("typedef struct { void** object; df_glock lock; } df_gl_entry;\n");
    sb.Append($"static df_gl_entry df_gl_table[{globalOrder.Count}] = {{");
    sb.Append(string.Join(",", globalOrder.Select(global => $" {{ (void**)&{global.CName}, {{ {global.Rank}, 0, false }} }}")));
    sb.Append(" };\n");
    sb.Append(@"static df_glock* df_gl_of(void* self) {
  if (!self) {
    return NULL;
  }
  for (size_t i = 0; i < sizeof(df_gl_table) / sizeof(df_gl_table[0]); i++) {
    if (*df_gl_table[i].object == self) {
      return &df_gl_table[i].lock;
    }
  }
  return NULL;
}
static void df_gl_push(df_glock* lock, bool write, bool taken) {
  if (df_gl_depth == df_gl_cap) {
    df_gl_cap = df_gl_cap ? df_gl_cap * 2 : 16;
    df_gl_hold* grown = (df_gl_hold*)realloc(df_gl_held, df_gl_cap * sizeof(df_gl_hold));
    if (!grown) {
      df_out_of_memory();
    }
    df_gl_held = grown;
  }
  df_gl_held[df_gl_depth++] = (df_gl_hold){ lock, write, taken };
}
/* false: the exception is raised (df_pending) and nothing is taken */
static bool df_gl_enter(df_glock* lock, bool write, const char* function) {
  bool held = false;
  bool heldWrite = false;
  for (size_t i = 0; i < df_gl_depth; i++) {
    if (df_gl_held[i].lock == lock) {
      held = true;
      heldWrite = heldWrite || df_gl_held[i].write;
    }
  }
  if (held) {
    /* an internal call: no new lock; a read lock cannot become a write lock (spec 18.5) */
    if (write && !heldWrite) {
      df_raise(&df_desc_ReadLockBecameWriteLockException, function);
      return false;
    }
    df_gl_push(lock, heldWrite, false);
    return true;
  }
  for (size_t i = 0; i < df_gl_depth; i++) {
    if (df_gl_held[i].lock->rank <= lock->rank) {
      df_raise(&df_desc_CallingGlobalDefinedAfterException, function);
      return false;
    }
  }
  /* no threads yet: a lock this thread does not hold is free */
  if (write) {
    lock->writer = true;
  } else {
    lock->readers++;
  }
  df_gl_push(lock, write, true);
  return true;
}
static void df_gl_exit(void) {
  df_gl_hold hold = df_gl_held[--df_gl_depth];
  if (hold.taken) {
    if (hold.write) {
      hold.lock->writer = false;
    } else {
      hold.lock->readers--;
    }
  }
}
");
    return sb.ToString();
  }

  // the body of a locked method is emitted as f_X_m__body; f_X_m is the wrapper that takes the lock
  string LockedMethodCode(FunSig f, string emitted, StringBuilder protos) {
    string sig = Signature(f);
    string bodySig = sig.Replace($" {f.CName}(", $" {f.CName}__body(");
    protos.Append(bodySig).Append(";\n");
    int at = emitted.IndexOf(sig, StringComparison.Ordinal);
    string body = emitted[..at] + bodySig + emitted[(at + sig.Length)..];
    var args = new List<string> { "self" };
    args.AddRange(f.Params.Select(ParamName));
    string call = $"{f.CName}__body({string.Join(", ", args)})";
    string ret = RetCType(f);
    bool returnsNothing = ret == "void";
    string failure = PairReturn(f)
      ? (f.Ret == "void" ? "{ df_exception* error = df_pending; df_pending = NULL; return error; }"
        : $"{{ df_exception* error = df_pending; df_pending = NULL; return ({ResultPair(CType(f.Ret))}){{ {ZeroValue(f.Ret)}, error }}; }}")
      : returnsNothing ? "{ return; }" : $"{{ return {ZeroValue(f.Ret)}; }}";   // the exception stays in df_pending
    var sb = new StringBuilder(body);
    sb.Append(sig).Append(" {\n");
    sb.Append("  df_glock* lock = df_gl_of((void*)self);\n");
    sb.Append(returnsNothing ? $"  if (!lock) {{\n    {call};\n    return;\n  }}\n" : $"  if (!lock) {{\n    return {call};\n  }}\n");
    sb.Append($"  if (!df_gl_enter(lock, {(LockWrites(f) ? "true" : "false")}, \"{f.Name}\")) {failure}\n");
    sb.Append(returnsNothing ? $"  {call};\n  df_gl_exit();\n}}\n" : $"  {ret} result = {call};\n  df_gl_exit();\n  return result;\n}}\n");
    return sb.ToString();
  }

  // an initializer uses only the globals declared before it: they already exist (spec 18.2)
  void CheckInitializerOrder(Node node, GlobalInfo own) {
    if (node.Kind == "Name") {
      if (node.Text == own.Name) {
        throw Error($"the initializer of global '{own.Name}' uses '{own.Name}' itself, which does not exist yet (spec 18.2)", node);
      }
      if (!globals.ContainsKey(node.Text!) && allGlobalNames.Contains(node.Text!)) {
        throw Error($"the initializer of global '{own.Name}' uses global '{node.Text}', which is declared after it: globals are created in declaration order (spec 18.2)", node);
      }
    }
    foreach (var child in node.Children) {
      CheckInitializerOrder(child, own);
    }
  }

  // all the names of the globals of the program, also the ones not registered yet (for the order check of the initializers)
  readonly HashSet<string> allGlobalNames = new();

  // C code at program start: the globals in declaration order (after the consts and the statics, which they may use)
  string GlobalsInitCode() {
    if (globalsInit == null) {
      return "";
    }
    return PairReturn(globalsInit)
      ? $"  {{\n    df_exception* error = {globalsInit.CName}();\n    if (error) {{\n      df_panic_unhandled(error);\n    }}\n  }}\n"
      : $"  {globalsInit.CName}();\n";
  }

  // C code at program end: the globals in the reverse order, before the statics end (their atEnd may use them)
  string GlobalsEndCode() {
    var sb = new StringBuilder();
    for (int index = globalOrder.Count - 1; index >= 0; index--) {
      var global = globalOrder[index];
      sb.Append($"  {{ void* gone = (void*){global.CName}; {global.CName} = NULL; df_drop(gone); }}\n");
    }
    return sb.ToString();
  }

  // the global a name means here, or null (a local, a parameter or a field with the same name hides it)
  GlobalInfo? GlobalNamed(Node n, Scope scope) {
    if (n.Kind != "Name" || !globals.TryGetValue(n.Text!, out var global)) {
      return null;
    }
    if (scope.Find(n.Text!) != null || currentClass != null && FindField(currentClass, n.Text!) != null) {
      return null;
    }
    return global;
  }

  // x = ..., ->x, x is atEnd. on a global: only __globals sets it (spec 18.3)
  LValueInfo GlobalLValue(GlobalInfo global, Node target) {
    if (currentFun != null && currentFun == globalsInit) {
      return new LValueInfo(global.CName, global.Type, false, null, true, false, true);
    }
    throw Error($"global '{global.Name}' cannot be assigned, moved or ended: it always holds the object it was created with (spec 18.3)", target);
  }

  // a call on a global from outside (spec 18.3): a & result is an error; a lent result must be copied (g.m()!); the automatic copy
  // under the lock comes with the locks
  void CheckGlobalCall(Node call, Scope scope) {
    if (call.Children.Count == 0 || call.Children[0] is not { Kind: "Member" } member || member.Children.Count != 1
        || GlobalNamed(member.Children[0], scope) is not GlobalInfo global) {
      return;
    }
    var methods = LookupMethods(ObjInfo(global.Type), member.Text!);
    if (methods.Count == 0) {
      return;
    }
    if (methods.Any(method => method.Writes.Count > 0)) {
      throw Error($"'{member.Text}' lends its result with &, which cannot come out of global '{global.Name}': call a method that returns a copy "
        + "or gives the value with -> (spec 18.3)", call);
    }
  }

  bool LendsResult(FunSig method) => !method.ReturnsOwned && !method.Gives.Contains(0) && LendsHeapValue(method.Ret);

  // ---------------------------------------------------------------- accesses from outside (spec 18.3)

  readonly Dictionary<Node, (GlobalInfo Global, bool Write)> globalReads = new();   // an expression under the lock: g.f, g.f.h, g.f.m(), a copy of them
  readonly Dictionary<Node, GlobalInfo> globalWrites = new();   // a statement run under the write lock: g.f = v, g.f += v, g.f++
  readonly HashSet<Node> wrappingGlobal = new();

  // after ClassifyReturns: a lent result of a method called on a global becomes a copy (g.m() is g.m()!); the field reads and writes of
  // a global are marked for their lock; a direct field that holds a string, an object, a collection or an array is copied under the lock
  void LowerGlobalAccesses(Node program) {
    if (globals.Count == 0) {
      return;
    }
    LowerAccesses(program, new List<Node>(), new HashSet<string>());
    foreach (var assign in globalsInit!.Node.Children.First(child => child.Kind == "Block").Children) {
      LowerAccesses(assign.Children[1], new List<Node> { assign }, new HashSet<string>());
    }
  }

  void LowerAccesses(Node node, List<Node> ancestors, HashSet<string> hidden) {
    if (node.Kind is "Class" or "Struct" or "Fun" or "Lambda" or "Constructor" or "Trait") {
      hidden = new HashSet<string>(hidden);
      CollectHidingNames(node, hidden, node.Kind is "Class" or "Struct" or "Trait");
    }
    if (node.Kind == "Name" && globals.TryGetValue(node.Text!, out var global) && !hidden.Contains(node.Text!)
        && ancestors.Count >= 2 && ancestors[^1] is { Kind: "Member" } hop && hop.Children[0] == node) {
      LowerAccess(hop, global, ancestors);
    }
    ancestors.Add(node);
    for (int index = 0; index < node.Children.Count; index++) {
      LowerAccesses(node.Children[index], ancestors, hidden);
    }
    ancestors.RemoveAt(ancestors.Count - 1);
  }

  // ancestors ends with hop (g.x); the node before it holds hop
  void LowerAccess(Node hop, GlobalInfo global, List<Node> ancestors) {
    CheckCallOrder(global, ancestors, hop);
    int at = ancestors.Count - 2;   // the parent of hop
    var parent = ancestors[at];
    if (parent.Kind == "Call" && parent.Children[0] == hop) {
      // g.m(...): the wrapper of the method takes the lock; a lent result is copied
      var methods = LookupMethods(ObjInfo(global.Type), hop.Text!);
      if (methods.Count > 0 && methods.Any(LendsResult) && at >= 1 && ancestors[at - 1].Kind != "Copy") {
        Replace(ancestors[at - 1], parent, Synthetic("Copy", parent, "global").Add(parent));
        cloneUsed = true;   // the clone functions are emitted (UsesClone ran before this pass)
      }
      return;
    }
    // a field and what follows it: g.f, g.f.h, g.f.m(), g.f[i], g.f.m().h ... The lock covers the whole chain: a write lock when one of
    // its calls can change what it reaches (or its type is not known here), a read lock otherwise
    var top = hop;
    string? type = FieldTypeOf(global.Type, hop.Text!);
    bool writes = false;
    while (at >= 0) {
      var next = ancestors[at];
      if (next is { Kind: "Member" } && next.Children[0] == top) {
        if (at >= 1 && ancestors[at - 1] is { Kind: "Call" } call && call.Children[0] == next) {
          writes |= !ReadOnlyMember(type, next.Text!);
          type = MemberCallType(type, next.Text!);
          top = call;
          at -= 2;
          continue;
        }
        type = type == null ? null : FieldTypeOf(type, next.Text!);
        top = next;
        at--;
        continue;
      }
      if (next is { Kind: "Index" } && next.Children[0] == top && next.Children.Count == 2 && next.Children[1].Kind != "Range") {
        type = type != null && (IsArr(type) || IsVec(type) || IsList(type)) ? IndexCollectionElem(type) : null;
        top = next;
        at--;
        continue;
      }
      break;
    }
    var holder = at >= 0 ? ancestors[at] : null;
    if (holder == null) {
      return;
    }
    if (holder.Kind is "SafeMember" or "Index" && holder.Children[0] == top) {
      throw NotSupported($"this use of a field of global '{global.Name}' (?., a range): write a method of its class for now (spec 18.3)", holder);
    }
    if (holder.Kind == "Assign" && holder.Children[0] == top) {
      globalWrites[holder] = global;   // g.f = v, g.f += v, g.items[0] = v: the statement under the write lock
      return;
    }
    if (holder.Kind == "ExprStmt") {
      globalReads[top] = (global, writes);   // g.items.add(1) alone
      return;
    }
    if (holder.Kind is "PreInc" or "PreDec" or "PostInc" or "PostDec") {
      if (at < 1 || ancestors[at - 1].Kind != "ExprStmt") {
        throw NotSupported($"++ and -- on a field of global '{global.Name}' inside an expression: write it as a statement (spec 18.3)", holder);
      }
      globalWrites[ancestors[at - 1]] = global;
      return;
    }
    if (holder.Kind is "WritableAddressOf" or "Gift" or "Counted" or "WeakOf" or "Targets" or "Finalize" or "Done" or "WritablePointerOf") {
      throw NotSupported($"this use of a field of global '{global.Name}' (&, ->, @, %, several targets, is atEnd.): write a method of its class "
        + "for now (spec 18.3)", holder);
    }
    if (holder.Kind is "Return" or "ExprStmt" && ancestors.LastOrDefault(node => node.Kind == "Fun") is Node funNode
        && funs.Values.SelectMany(list => list).FirstOrDefault(f => f.Node == funNode) is { Writes.Count: > 0 }) {
      throw Error($"a & result cannot lend global '{global.Name}': return a copy, or give the value with -> (spec 18.3)", top);
    }
    if (holder.Kind == "Copy") {
      globalReads[holder] = (global, writes);
      return;
    }
    if (type == null || LendsHeapValue(type)) {
      // string s = g.name, Dog d = g.dogs[0]: a copy, made under the lock (a value of a type not known here is copied only when it lends,
      // see the Copy "global" of ExprOf)
      var copy = Synthetic("Copy", top, "global").Add(top);
      Replace(holder, top, copy);
      cloneUsed = true;
      globalReads[copy] = (global, writes);
      return;
    }
    globalReads[top] = (global, writes);
  }

  // spec 18.5: the code of a class that calls a global ranked above every global of that class can never run on one of them: a compile
  // error. (The check at run time stays: the lock is taken by the called method, for every caller)
  void CheckCallOrder(GlobalInfo callee, List<Node> ancestors, Node at) {
    var owner = ancestors.LastOrDefault(node => node.Kind is "Class" or "Trait");
    if (owner?.Text == null) {
      return;
    }
    var callers = globalOrder.Where(global => IsSubtype(global.Type, owner.Text)).ToList();
    if (callers.Count == 0 || !callers.All(caller => caller != callee && callee.Rank > caller.Rank)) {
      return;
    }
    throw callers.Count == 1
      ? Error($"global '{callers[0].Name}' calls global '{callee.Name}', which is declared after it: a global can call only globals declared before it (spec 18.5)", at)
      : Error($"globals '{string.Join("', '", callers.Select(caller => caller.Name))}' call global '{callee.Name}', which is declared after them: a global can call "
        + "only globals declared before it (spec 18.5)", at);
  }

  // ---------------------------------------------------------------- values evaluated before the lock

  // g.items.add(h.get()), g.f = h.get(): what can call another global is evaluated before the lock of g is taken, as the arguments of a
  // method call are (spec 18.5). The value is kept in a C temporary and its node answers with it (ExprOf)
  readonly Dictionary<Node, Expr> prebuilt = new();

  static bool ContainsCall(Node node) => node.Kind is "Call" or "CtorChain" || node.Kind != "Lambda" && node.Children.Any(ContainsCall);

  string Prebuild(Node value, Scope scope, List<Node> built) {
    if (value.Kind is "Gift" or "WritableAddressOf" or "WritablePointerOf" or "Counted" or "WeakOf" or "Lambda" || !ContainsCall(value) || prebuilt.ContainsKey(value)) {
      return "";
    }
    var e = ExprOf(value, scope);
    if (e.Type == "void") {
      return "";
    }
    string temp = NewTemp();
    prebuilt[value] = e with { Code = temp };
    built.Add(value);
    return $"{CType(e.Type)} {temp} = {e.Code}; ";
  }

  // the arguments of the calls and the indexes along a chain on a global (g.f.m(a), g.f[i])
  string PrebuildChain(Node node, Scope scope, List<Node> built) {
    var sb = new StringBuilder();
    while (true) {
      if (node.Kind == "Copy" && node.Children.Count == 1) {
        node = node.Children[0];
      } else if (node.Kind == "Call" && node.Children.Count > 0) {
        foreach (var args in node.Children.Skip(1).Where(child => child.Kind == "Args")) {
          foreach (var arg in args.Children) {
            sb.Append(Prebuild(arg.Kind == "Named" && arg.Children.Count > 0 ? arg.Children[^1] : arg, scope, built));
          }
        }
        node = node.Children[0];
      } else if (node.Kind == "Index" && node.Children.Count == 2) {
        sb.Append(Prebuild(node.Children[1], scope, built));
        node = node.Children[0];
      } else if (node.Kind is "Member" or "Paren" && node.Children.Count == 1) {
        node = node.Children[0];
      } else {
        return sb.ToString();
      }
    }
  }

  void ForgetPrebuilt(List<Node> built) {
    foreach (var node in built) {
      prebuilt.Remove(node);
    }
  }

  // a member call that does not change what it is called on: the read-only members of the collections, the unrestricted methods
  bool ReadOnlyMember(string? type, string name) {
    if (type == null) {
      return false;
    }
    if (IsCollectionType(type) || IsArr(type)) {
      return ReadOnlyCollectionMembers.Contains(name);
    }
    if (IsClass(type)) {
      var methods = LookupMethods(ObjInfo(type), name);
      return methods.Count > 0 && methods.All(method => method.IsUnrestricted);
    }
    return false;
  }

  string? MemberCallType(string? type, string name) {
    if (type == null || !IsClass(type)) {
      return null;
    }
    var methods = LookupMethods(ObjInfo(type), name);
    return methods.Count == 1 ? methods[0].Ret : null;
  }

  string? FieldTypeOf(string type, string name) =>
    classes.TryGetValue(type, out var cls) && FindField(cls, name) is FieldInfo field ? field.Type : null;

  static void Replace(Node holder, Node old, Node with) {
    int index = holder.Children.IndexOf(old);
    if (index >= 0) {
      holder.Children[index] = with;
    }
  }

  static string LockOf(GlobalInfo global) => $"(&df_gl_table[{global.Rank}].lock)";


  // an expression under the lock of a global: the lock is released on the normal way out and before an exception goes on
  Expr GlobalReadExpr(Node n, GlobalInfo global, bool write, Scope scope, string? expected) {
    var built = new List<Node>();
    try {
      string before = PrebuildChain(n, scope, built);   // the arguments, before the lock (an exception there has nothing to release)
      string release = "gl_rel_" + NewTemp();
      string outer = Handler;
      string enter = $"if (!df_gl_enter({LockOf(global)}, {(write ? "true" : "false")}, {fn!.NameConst})) {RaiseJump}";
      string? saved = handlerOverride;
      handlerOverride = release;
      Expr e;
      try {
        e = ExprOf(n, scope, expected);
      } finally {
        handlerOverride = saved;
      }
      string escape = $"if (0) {{ {release}: df_gl_exit(); goto {outer}; }}";
      if (e.Type == "void") {
        return e with { Code = $"({{ {before}{enter} {escape} {e.Code}; df_gl_exit(); }})" };
      }
      string temp = NewTemp();
      return e with { Code = $"({{ {before}{enter} {escape} {CType(e.Type)} {temp} = {e.Code}; df_gl_exit(); {temp}; }})" };
    } finally {
      ForgetPrebuilt(built);
    }
  }

  // a statement under the write lock of a global (g.f = v, g.f++)
  void EmitGlobalWrite(Node s, GlobalInfo global, StringBuilder sb, Scope scope, int indent) {
    string pad = Pad(indent);
    var built = new List<Node>();
    // g.f = v, g.f[i] += v, g.f++: the value and the indexes first, then the lock (spec 18.5)
    var target = s.Kind == "Assign" ? s.Children[0] : s.Kind == "ExprStmt" && s.Children[0].Children.Count > 0 ? s.Children[0].Children[0] : null;
    string before = target != null ? PrebuildChain(target, scope, built) : "";
    if (s.Kind == "Assign" && s.Children.Count > 1) {
      before += Prebuild(s.Children[1], scope, built);
    }
    sb.Append(pad).Append("{\n");
    if (before.Length > 0) {
      sb.Append(pad).Append("  ").Append(before).Append('\n');
    }
    string release = "gl_rel_" + NewTemp();
    string outer = Handler;
    sb.Append(pad).Append($"  if (!df_gl_enter({LockOf(global)}, true, {fn!.NameConst})) {RaiseJump}\n");
    string? saved = handlerOverride;
    handlerOverride = release;
    try {
      sb.Append(pad).Append("  {\n");
      EmitStmt(s, sb, scope, indent + 2);
      sb.Append(pad).Append("  }\n");
    } finally {
      handlerOverride = saved;
      ForgetPrebuilt(built);
    }
    sb.Append(pad).Append("  df_gl_exit();\n");
    sb.Append(pad).Append($"  if (0) {{\n{pad}    {release}:\n{pad}    df_gl_exit();\n{pad}    goto {outer};\n{pad}  }}\n");
    sb.Append(pad).Append("}\n");
  }

  bool LendsHeapValue(string type) =>
    type is "string" or "string?" || IsClass(type) || IsCollectionType(type) || IsArr(type) || IsTuple(type);

  // the uses of the globals in the whole program (spec 18.1, 18.3): a global is only called, read or written through its members, or
  // copied with !; a global declared anywhere but the top level is an error
  void CheckGlobalUses(Node program) {
    if (globals.Count == 0) {
      CheckNestedGlobalDecls(program);
      return;
    }
    CheckUses(program, null, -1, new HashSet<string>());
    foreach (var assign in globalsInit!.Node.Children.First(child => child.Kind == "Block").Children) {
      CheckUses(assign.Children[1], assign, 1, new HashSet<string>());   // global B b = B(a): the initializers follow the same rules
    }
  }

  void CheckNestedGlobalDecls(Node node) {
    foreach (var child in node.Children) {
      if (IsGlobalDecl(child)) {
        throw Error("a global can be declared only at the top level of the root file (program.df, main.df or lib.df) (spec 18.1)", child);
      }
      CheckNestedGlobalDecls(child);
    }
  }

  void CheckUses(Node node, Node? parent, int position, HashSet<string> hidden) {
    if (IsGlobalDecl(node)) {
      throw Error("a global can be declared only at the top level of the root file (program.df, main.df or lib.df) (spec 18.1)", node);
    }
    if (node.Kind is "Class" or "Struct" or "Fun" or "Lambda" or "Constructor" or "Trait") {
      hidden = new HashSet<string>(hidden);
      CollectHidingNames(node, hidden, node.Kind is "Class" or "Struct" or "Trait");
    }
    if (node.Kind == "Name" && globals.ContainsKey(node.Text!) && !hidden.Contains(node.Text!) && parent != null) {
      CheckGlobalVisible(globals[node.Text!], node);
      CheckGlobalUse(node, parent, position);
    }
    for (int index = 0; index < node.Children.Count; index++) {
      CheckUses(node.Children[index], node, index, hidden);
    }
  }

  // the names that hide a global inside a class (its fields and primary parameters) or a function (its parameters and locals)
  static void CollectHidingNames(Node node, HashSet<string> names, bool members) {
    foreach (var child in node.Children) {
      if (members) {
        // class A(i32 start) { i32 n ... }: the primary parameters and the fields (direct children of the body), not the methods
        if (child.Kind is "Block" or "PrimaryCtor" or "FieldGroup") {
          CollectHidingNames(child, names, true);
        } else if (child.Kind is "Decl" || child.Kind.EndsWith("Param")) {
          if (child.Text != null) {
            names.Add(child.Text);
          }
        }
        continue;
      }
      if (child.Kind is "Class" or "Struct" or "Fun" or "Lambda" or "Trait" or "Constructor") {
        continue;   // their own names, collected when the walk enters them
      }
      if (child.Kind is "Decl" or "Var" || child.Kind.EndsWith("Param")) {
        if (child.Text != null) {
          names.Add(child.Text);
        }
      }
      CollectHidingNames(child, names, false);
    }
  }

  // the globals of a library are used only by the code of that library, and a library cannot name the globals of the program (spec 18.1)
  void CheckGlobalVisible(GlobalInfo global, Node use) {
    string? here = SourceMap.LibraryOf(use.Line);
    if (global.Library == here) {
      return;
    }
    throw global.Library != null
      ? Error($"global '{global.Name}' belongs to library '{global.Library}' and is not visible outside it (spec 18.1)", use)
      : Error($"global '{global.Name}' belongs to the program and is not visible in library '{here}' (spec 18.1)", use);
  }

  void CheckGlobalUse(Node name, Node parent, int position) {
    string what = name.Text!;
    if (parent.Kind is "Member" or "SafeMember" && position == 0 || parent.Kind == "Copy") {
      return;   // g.m(), g.field, g.field = v, g!
    }
    if (parent.Kind == "Assign" && position == 0 || parent.Kind is "Targets" or "Finalize" or "Done") {
      throw Error($"global '{what}' cannot be assigned, moved or ended: it always holds the object it was created with (spec 18.3)", name);
    }
    throw Error($"global '{what}' cannot be lent, given or shared: call its methods, or copy it with {what}! (spec 18.3)", name);
  }
}
