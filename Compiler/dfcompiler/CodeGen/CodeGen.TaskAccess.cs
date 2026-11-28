using dfparser;

namespace dfcompiler;

// What the code of a task sees (spec 20.12) and threads started by threads (spec 20.1).
//
// The code of a task: the methods of every class given to Task(...) (and of its parents; the constructor runs on the creator's side, so
// it is not task code), the functions they call, at any depth, and the functions they start as threads. Of the top-level variables of
// the program and the static fields of the classes, that code may use only constants, refcounted objects, weak references, mutex
// variables and globals: any other one is a compile error. While compiling, each function notes what it calls and which top-level
// variables and static fields it uses; the check runs once everything is compiled. Followed too: the lambdas a function writes; a call of
// a function value, which may run any lambda of that function type; a call of a method, which may run the methods that replace it in
// the subclasses and in the classes of a trait (chosen when the program runs); the accessors of properties (they are calls).
//
// A thread cannot start another thread: Thread(...) written in a function that the program runs as a thread is a compile error; elsewhere
// (a handle passed to the thread) start() raises ThreadStartedByAThreadException.
public sealed partial class CodeGen {
  readonly Dictionary<FunSig, HashSet<FunSig>> funCalls = new();               // a function -> the functions it calls or starts as threads
  readonly Dictionary<FunSig, List<(string What, Node At)>> funSharedUses = new();   // a function -> the top-level state it may not share
  readonly List<(FunSig Fun, FunSig Started, Node At)> threadCreates = new();  // Thread(f, ...) written in a function
  readonly HashSet<FunSig> threadFuns = new();                                 // the functions run as threads
  readonly HashSet<string> threadValueTypes = new();                           // Thread(value): any lambda of these types runs as a thread
  readonly List<(FunSig Fun, Node At)> threadValueCreates = new();             // Thread(value, ...) written in a function
  readonly HashSet<string> sharedFileVars = new();                             // the top-level variables that are refcounted or weak
  readonly Dictionary<FunSig, HashSet<string>> funValueCalls = new();          // a function -> the function types it calls through a value
  readonly Dictionary<string, List<FunSig>> lambdasByType = new();             // a function type -> the lambdas of that type

  void NoteLambda(FunSig lambda, string fnType) {
    if (!TasksMode) {
      return;
    }
    NoteCall(lambda);
    if (!lambdasByType.TryGetValue(fnType, out var list)) {
      lambdasByType[fnType] = list = new List<FunSig>();
    }
    list.Add(lambda);
  }

  void NoteFunValueCall(string fnType) {
    if (!TasksMode || currentFun == null) {
      return;
    }
    if (!funValueCalls.TryGetValue(currentFun, out var types)) {
      funValueCalls[currentFun] = types = new HashSet<string>();
    }
    types.Add(fnType);
  }

  // the functions a call of f may run: the ones it calls (and their replacements), the lambdas of the function types it calls through a
  // value, the replacements of f itself
  List<FunSig> Reached(FunSig f) {
    var reached = new List<FunSig>();
    if (funCalls.TryGetValue(f, out var calls)) {
      reached.AddRange(calls);
      reached.AddRange(calls.SelectMany(Replacements));
    }
    if (funValueCalls.TryGetValue(f, out var valueTypes)) {
      reached.AddRange(valueTypes.SelectMany(type => lambdasByType.TryGetValue(type, out var lambdas) ? lambdas : new List<FunSig>()));
    }
    reached.AddRange(Replacements(f));
    return reached;
  }

  // a function of a compiled library whose interface says it uses what a task cannot see (taskunsafe, spec 15.8, 20.12)
  static bool ImportedTaskUnsafe(FunSig f) => f.Node.Children.Any(child => child.Kind == "ImportedTaskUnsafe");

  // the functions that use, at any depth, what the code of a task cannot see: written in the interface of a library (taskunsafe), so that
  // a program whose task calls one of them gets the error
  HashSet<FunSig> TaskUnsafeFuns() {
    var unsafeFuns = new HashSet<FunSig>(funSharedUses.Keys);
    var all = funCalls.Keys.Concat(funValueCalls.Keys).Concat(funSharedUses.Keys).Distinct().ToList();
    bool changed = true;
    while (changed) {
      changed = false;
      foreach (var f in all.Where(f => !unsafeFuns.Contains(f))) {
        if (Reached(f).Any(callee => unsafeFuns.Contains(callee) || ImportedTaskUnsafe(callee))) {
          unsafeFuns.Add(f);
          changed = true;
        }
      }
    }
    return unsafeFuns;
  }

  HashSet<FunSig>? taskUnsafeCache;

  // the methods a call of f may run instead of it: the ones of the same name in the classes below the class of f (its subclasses, or the
  // classes of a trait), chosen when the program runs
  IEnumerable<FunSig> Replacements(FunSig f) {
    if (f.Class == null || f.IsStatic || f.IsCtor || f.IsInit || f.IsStaticInit) {
      yield break;
    }
    string simple = f.Name.Split('.')[^1];
    foreach (var cls in classes.Values.Where(cls => cls != f.Class && IsSubtype(cls.Name, f.Class.Name))) {
      if (funs.TryGetValue(cls.Name + "." + simple, out var overloads)) {
        foreach (var replacement in overloads.Where(other => other.Class == cls)) {
          yield return replacement;
        }
      }
    }
  }

  void NoteCall(FunSig callee) {
    if (TasksMode && currentFun != null && currentFun != callee) {
      if (!funCalls.TryGetValue(currentFun, out var calls)) {
        funCalls[currentFun] = calls = new HashSet<FunSig>();
      }
      calls.Add(callee);
    }
  }

  void NoteSharedUse(string what, Node at) {
    if (currentFun == null || currentFun.IsStaticInit || LibraryCode) {
      return;
    }
    if (!funSharedUses.TryGetValue(currentFun, out var uses)) {
      funSharedUses[currentFun] = uses = new List<(string, Node)>();
    }
    uses.Add((what, at));
  }

  // refcounted and weak values are safe to share (spec 20.12)
  static bool SharedType(string type) =>
    IsCountedType(type) || IsWeakType(type) || type.StartsWith("(@") || type.StartsWith("(_");

  // a top-level variable read or written by name: constants and mutex variables are safe; globals never get here
  void NoteTopVarUse(string name, Node at) {
    if (!TasksMode || !fileVars.ContainsKey(name) && !libraryVars.Contains(name) || topMutexes.ContainsKey(name) || sharedFileVars.Contains(name)
        || SharedType(globalConsts[name].Type)) {
      return;
    }
    NoteSharedUse($"the top-level variable '{name}'", at);
  }

  void NoteStaticUse(FieldInfo field, Node at) {
    if (!TasksMode || field.IsConst || field.IsCounted || field.IsWeak || SharedType(field.Type)) {
      return;
    }
    NoteSharedUse($"the static field {DisplayName(field.Owner.Name)}.{field.Name}", at);
  }

  // Thread(f, ...) with a function value, or a lambda written in the call
  void NoteThreadValue(string fnType, Node at) {
    threadValueTypes.Add(fnType);
    if (currentFun != null) {
      threadValueCreates.Add((currentFun, at));
    }
  }

  void NoteThreadCreate(FunSig started, Node at) {
    threadFuns.Add(started);
    if (currentFun != null) {
      threadCreates.Add((currentFun, started, at));
    }
    NoteCall(started);   // a thread started by task code is task code
  }

  // once every function is compiled
  void CheckTaskAccess() {
    foreach (string fnType in threadValueTypes) {
      foreach (var lambda in lambdasByType.GetValueOrDefault(fnType) ?? new List<FunSig>()) {
        threadFuns.Add(lambda);   // a lambda that may run as a thread (spec 20.1)
      }
    }
    foreach (var (fun, at) in threadValueCreates) {
      if (threadFuns.Contains(fun)) {
        throw Error($"a thread cannot start another thread: {(fun.LambdaCtx != null ? "this lambda" : fun.Name)} runs as a thread (spec 20.1)", at);
      }
    }
    foreach (var (fun, started, at) in threadCreates) {
      if (threadFuns.Contains(fun)) {
        throw Error($"a thread cannot start another thread: {(fun.LambdaCtx != null ? "this lambda" : fun.Name)} runs as a thread (spec 20.1)", at);
      }
    }
    foreach (var (fun, what, at) in sharedTaskWaits) {
      if (threadFuns.Contains(fun)) {
        throw Error($"'{what}' is a task of the parent: a thread cannot wait on it ({fun.Name} runs as a thread, spec 20.1)", at);
      }
    }
    if (taskClasses.Count == 0) {
      return;
    }
    var lineage = new HashSet<ClassInfo>();
    foreach (var cls in taskClasses) {
      for (var c = cls; c != null; c = c.Parent) {
        lineage.Add(c);
      }
    }
    // from each method of a task class, the functions reached; the first one that uses what a task cannot see is the error
    var from = new Dictionary<FunSig, FunSig>();
    var queue = new Queue<FunSig>();
    foreach (var f in funCalls.Keys.Concat(funSharedUses.Keys).Concat(funValueCalls.Keys).Distinct()
        .Where(f => f.Class != null && lineage.Contains(f.Class) && !f.IsCtor && !f.IsInit && !f.IsStatic && !f.IsStaticInit)) {
      from[f] = f;
      queue.Enqueue(f);
    }
    while (queue.Count > 0) {
      var f = queue.Dequeue();
      if (funSharedUses.TryGetValue(f, out var uses)) {
        var (what, at) = uses[0];
        var root = f;
        while (from[root] != root) {
          root = from[root];
        }
        string Shown(FunSig fun) => fun.Name.StartsWith("lambda_") ? $"a lambda at line {fun.Node.Line}" : fun.Name;
        string through = root == f ? "" : $", reached from {Shown(root)}";
        throw Error($"{what} cannot be used by the code of a task ({Shown(f)}{through}): a task sees only constants, refcounted objects, "
          + "weak references, mutex variables and globals (spec 20.12)", at);
      }
      if (ImportedTaskUnsafe(f)) {
        var root = f;
        while (from[root] != root) {
          root = from[root];
        }
        throw Error($"{f.Name} of a compiled library uses top-level variables or static fields of its library, which the code of a task "
          + $"cannot see (reached from {(root.Name.StartsWith("lambda_") ? $"a lambda at line {root.Node.Line}" : root.Name)}, spec 20.12)", root.Node);
      }
      foreach (var callee in Reached(f).Where(callee => !from.ContainsKey(callee))) {
        from[callee] = f;
        queue.Enqueue(callee);
      }
    }
  }
}
