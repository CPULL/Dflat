using dfparser;

namespace dfcompiler;

// What the code of a task sees (spec 20.12) and threads started by threads (spec 20.1).
//
// The code of a task: the methods of every class given to Task(...) (and of its parents; the constructor runs on the creator's side, so
// it is not task code), the functions they call, at any depth, and the functions they start as threads. Of the top-level variables of
// the program and the static fields of the classes, that code may use only constants, refcounted objects, weak references, mutex
// variables and globals: any other one is a compile error. While compiling, each function notes what it calls and which top-level
// variables and static fields it uses; the check runs once everything is compiled. A function reached only through a lambda or a
// function value is not followed.
//
// A thread cannot start another thread: Thread(...) written in a function that the program runs as a thread is a compile error; elsewhere
// (a handle passed to the thread) start() raises ThreadStartedByAThreadException.
public sealed partial class CodeGen {
  readonly Dictionary<FunSig, HashSet<FunSig>> funCalls = new();               // a function -> the functions it calls or starts as threads
  readonly Dictionary<FunSig, List<(string What, Node At)>> funSharedUses = new();   // a function -> the top-level state it may not share
  readonly List<(FunSig Fun, FunSig Started, Node At)> threadCreates = new();  // Thread(f, ...) written in a function
  readonly HashSet<FunSig> threadFuns = new();                                 // the functions run as threads
  readonly HashSet<string> sharedFileVars = new();                             // the top-level variables that are refcounted or weak

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

  void NoteThreadCreate(FunSig started, Node at) {
    threadFuns.Add(started);
    if (currentFun != null) {
      threadCreates.Add((currentFun, started, at));
    }
    NoteCall(started);   // a thread started by task code is task code
  }

  // once every function is compiled
  void CheckTaskAccess() {
    foreach (var (fun, started, at) in threadCreates) {
      if (threadFuns.Contains(fun)) {
        throw Error($"a thread cannot start another thread: {fun.Name} runs as a thread (spec 20.1)", at);
      }
    }
    if (taskClasses.Count == 0 || funSharedUses.Count == 0) {
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
    foreach (var f in funCalls.Keys.Concat(funSharedUses.Keys).Distinct()
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
        string through = root == f ? "" : $", reached from {root.Name}";
        throw Error($"{what} cannot be used by the code of a task ({f.Name}{through}): a task sees only constants, refcounted objects, "
          + "weak references, mutex variables and globals (spec 20.12)", at);
      }
      if (funCalls.TryGetValue(f, out var calls)) {
        foreach (var callee in calls.Where(callee => !from.ContainsKey(callee))) {
          from[callee] = f;
          queue.Enqueue(callee);
        }
      }
    }
  }
}
