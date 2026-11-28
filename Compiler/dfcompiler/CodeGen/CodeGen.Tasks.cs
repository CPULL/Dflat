using System.Text;
using dfparser;

namespace dfcompiler;

// Tasks (spec 20.1, Root.Tasks). The class Task is written in Dflat (TasksPrelude) over the natives of Root.Tasks.Native (RuntimeTasks):
// a Task object holds one reference to a C task (df_task), which owns the object of the task and runs its taskRun() on a thread.
//
// Task(MyTask, 123, "blah", timeout: 5000, linked: false) is compiled here: the object is built on the side of the creator, as any
// constructor call (its exception goes to the creator, spec 20.1), then given to a new C task, and the Task object of the prelude is
// made with the handle of that task. Each class used as a task gets a C function, df_taskrun_<class>, that the thread calls: it calls
// taskRun() of the object and gives back the exception that left it.
public sealed partial class CodeGen {
  const string TasksPrelude = """
enum RunStatus { Ready, Running, Completed, Failed, Killed }
enum RunKind { Process, Task, Thread }

public class Task(u64 handle) {
  public fun &Task start(lapse timeout = null) {
    i32 started = Root.Tasks.Native.Start(handle, timeout)
    if started == 1 then throw TaskAlreadyStartedException("The task was already started")
    if started == 2 then throw NoMoreResourcesException("The system cannot start a new thread")
    return this
  }
  public fun RunStatus get_status() { (RunStatus)Root.Tasks.Native.Status(handle) }
  public fun RunKind get_kind() { RunKind.Task }
  public fun string get_name() { Root.Tasks.Native.Name(handle) }
  public fun bool get_linked() { Root.Tasks.Native.Linked(handle) }
  public fun date get_startTime() { Root.Tasks.Native.StartTime(handle) }
  unrestricted fun bool kill(lapse timeout) { Root.Tasks.Native.Kill(handle, timeout) }
} atEnd {
  Root.Tasks.Native.Release(handle)
}
""";

  // Root.Tasks.Task, Root.Tasks.Thread<i32>, Root.Tasks.Signal ... written in full as a type, and Root.Tasks.RunStatus.Running,
  // Root.Tasks.Token.register(...) in the code: the name of Root.Tasks (spec 16.1)
  static void LowerTasksTypeNames(Node node) {
    if (node.Kind == "Type" && node.Text is string text && text.StartsWith("Root.Tasks.") && TasksLibraryNames.Contains(text["Root.Tasks.".Length..])) {
      node.Text = text["Root.Tasks.".Length..];
    }
    for (int index = 0; index < node.Children.Count; index++) {
      var child = node.Children[index];
      if (child is { Kind: "Member", Text: string name, Children: [{ Kind: "Member", Text: "Tasks", Children: [{ Kind: "Name", Text: "Root" }] }] }
          && TasksLibraryNames.Contains(name)) {
        node.Children[index] = Synthetic("Name", child, name);
        continue;
      }
      LowerTasksTypeNames(child);
    }
  }

  // the names of Root.Tasks (spec 16.1): bare with import Root.Tasks
  static readonly string[] TasksLibraryNames = { "Task", "Thread", "RunStatus", "RunKind", "Signal", "SignalMode", "KnownSignals", "SignalGroup", "Token", "Event", "EventMode", "EventQueueFullMode" };

  // Root.Tasks.Native: the C side of the class Task (RuntimeTasks)
  static void AddTasksStdlib(Action<string, StdFun> add) {
    var tasks = new[] { "tasks" };
    void Native(string name, string ret, string cname, params string[] ps) {
      add("Root.Tasks.Native." + name, new StdFun(ps, ret, cname, false, 0, "", null, tasks));
    }
    Native("Start", "i32", "df_task_start", "u64", "lapse");
    Native("Retain", "u64", "df_task_retain", "u64");
    Native("Release", "void", "df_task_release", "u64");
    Native("Status", "i32", "df_task_status", "u64");
    Native("Name", "string", "df_task_name", "u64");
    Native("Linked", "bool", "df_task_linked", "u64");
    Native("StartTime", "date", "df_task_start_time", "u64");
    Native("Kill", "bool", "df_task_kill", "u64", "lapse");
  }

  // the classes used as tasks: each one gets its df_taskrun_<class> (TaskRunFunctions)
  readonly List<ClassInfo> taskClasses = new();

  // Task(MyTask, args..., timeout: t, linked: l) (spec 20.1); null when the call is not one (the prelude makes Task(handle))
  Expr? TaskCreate(Node n, Scope scope) {
    if (TaskMethodTarget(n, scope) is { } postedCall) {
      return TaskMethodCall(n, postedCall.Receiver, postedCall.Cls, false, "0", scope);   // ticker.stop(): posted through the handle (spec 20.1)
    }
    if (!TasksMode || n.Children[0] is not { Kind: "Name", Text: "Task" } || scope.Find("Task") != null || !classes.ContainsKey("Task")) {
      return null;
    }
    var args = n.Children[1].Children;
    if (args.Count > 0 && args[0].Kind == "CValue") {
      return null;   // the Task object made with the handle of a new task (below)
    }
    if (LibraryCode || currentClass?.Name == "Task") {
      return null;   // the prelude: Task(Root.Tasks.Native.Retain(handle))
    }
    if (args.Count == 0 || args[0] is not { Kind: "Name" } first || scope.Find(first.Text!) != null
        || !classes.TryGetValue(first.Text!, out var cls) || cls.IsTrait) {
      throw Error("Task() takes the class of the task first, then the arguments of its constructor: Task(MyTask, 123) (spec 20.1)", n);
    }
    var run = TaskRunOf(cls)
      ?? throw Error($"{DisplayName(cls.Name)} has no taskRun(): a class becomes a task with its function taskRun() (spec 20.1)", n);
    if (run.Ret == "Thread") {
      throw Error("the handle of a thread never leaves its task: taskRun() does not give it (spec 20.1)", n);
    }
    Node? timeout = null;
    Node? linked = null;
    Node? started = null;
    var ctorArgs = Synthetic("Args", n);
    foreach (var arg in args.Skip(1)) {
      if (arg is { Kind: "Named", Text: "timeout" }) {
        timeout = arg.Children[0];   // the named arguments of Task() are its own, never of the constructor of the class (spec 20.1)
      } else if (arg is { Kind: "Named", Text: "linked" }) {
        linked = arg.Children[0];
      } else if (arg is { Kind: "Named", Text: StartArgument }) {
        started = arg;
      } else {
        ctorArgs.Add(arg);
      }
    }
    foreach (var given in ctorArgs.Children) {
      CheckThreadLeaves(given, scope);   // a thread never leaves its task (spec 20.1)
    }
    // the object is built here, on the side of the creator: the exception of its constructor goes to the creator (spec 20.1)
    var obj = ExprOf(Synthetic("Call", n).Add(Synthetic("Name", n, cls.Name)).Add(ctorArgs), scope);
    string timeoutCode = timeout == null ? "0" : Convert(ExprOf(timeout, scope, "lapse"), "lapse", timeout);
    string linkedCode = linked == null ? "true" : Convert(ExprOf(linked, scope, "bool"), "bool", linked);
    if (!taskClasses.Contains(cls)) {
      taskClasses.Add(cls);
    }
    string handle = $"df_task_new((void*)({obj.Code}), {TaskRunName(cls)}, \"{DisplayName(cls.Name)}\", \"{TaskResultText(run.Ret)}\", {timeoutCode}, {linkedCode}, "
      + (run.Ret == "void" ? "0, NULL)" : $"sizeof({CType(run.Ret)}), {TaskDropName(cls)})");
    var made = Synthetic("Call", n).Add(Synthetic("Name", n, "Task"))
      .Add(Synthetic("Args", n).Add(Synthetic("CValue", n, handle).Add(Synthetic("Type", n, "u64"))));
    return Started(ExprOf(made, scope), started, scope);
  }

  FunSig? TaskRunOf(ClassInfo cls) =>
    LookupMethods(cls, "taskRun").FirstOrDefault(f => !f.IsStatic && f.Params.Count == 0);

  static string TaskRunName(ClassInfo cls) => "df_taskrun_" + StableName(cls.Name);
  static string TaskDropName(ClassInfo cls) => "df_taskdrop_" + StableName(cls.Name);

  // the C functions the threads call: taskRun() of the object; its last value is handed out (return val, or the zero value when
  // taskRun() ends without a return, spec 20.1) and the exception that left it is given back (a silent one does not end the task as
  // Failed: no catch around it, spec 15.4). With each one the function that ends a value nobody collected
  void TaskRunFunctions(StringBuilder protos, StringBuilder bodies) {
    protos.Append(threadProtos);   // the threads (CodeGen.Threads.cs)
    bodies.Append(threadCode);
    foreach (string record in taskCallRecords.Values) {
      protos.Append($"static void {record}_invoke(void* obj, df_task_call* c);\nstatic void {record}_end(df_task_call* c);\n");
    }
    bodies.Append(taskCallCode);
    foreach (var cls in taskClasses) {
      var run = TaskRunOf(cls)!;
      string head = $"static df_exception* {TaskRunName(cls)}(void* obj)";
      string dropHead = $"static void {TaskDropName(cls)}(void* p)";
      protos.Append(head).Append(";\n").Append(dropHead).Append(";\n");
      string call = $"{run.CName}(({CType(run.Class!.Name)})obj)";
      var body = new StringBuilder(head).Append(" {\n");
      bool hasValue = run.Ret != "void";
      if (PairReturn(run)) {
        body.Append(hasValue
          ? $"  {RetCType(run)} result = {call};\n  df_exception* err = result.err;\n  {CType(run.Ret)} value = result.value;\n"
          : $"  df_exception* err = {call};\n");
      } else {
        body.Append(hasValue ? $"  {CType(run.Ret)} value = {call};\n" : $"  {call};\n");
        body.Append("  df_exception* err = df_pending;\n  df_pending = NULL;\n");
      }
      body.Append("  if (err && err->descriptor->silent) {\n    df_exc_release(err);\n    err = NULL;\n  }\n");
      if (hasValue) {
        body.Append("  if (err == NULL) {\n    df_task_give(df_task_current, &value, DF_GIVE_RETURN);\n  }\n");
      }
      body.Append("  return err;\n}\n");
      bodies.Append(body);
      bodies.Append(dropHead).Append(" {\n");
      if (hasValue) {
        bodies.Append($"  {CType(run.Ret)}* v = ({CType(run.Ret)}*)p;\n  {DropValueCode(run.Ret, "(*v)")}\n");
      } else {
        bodies.Append("  (void)p;\n");
      }
      bodies.Append("}\n");
    }
  }

  // the end of a value that is owned (a value of a task nobody collected)
  string DropValueCode(string type, string place) {
    if (type == "string") {
      return $"df_str_free({place});";
    }
    if (IsMulti(type) || IsTuple(type)) {
      var elems = TupleElems(type);
      return string.Concat(elems.Select((elem, index) => DropValueCode(elem.Type, $"{place}.e{index}") + " "));
    }
    return TuplePartDrop(type, place);
  }

  // ---------------------------------------------------------------- the Task handles (spec 20.9)

  // what a Task variable knows of its task: the class (its methods, and taskRun), and what taskRun() gives. A Task made on its line by
  // Task(MyTask, ...) knows both (spec 20.9: nothing is written); Task<MyTask>, Task<MyTask, i32>, Task<*, i32> say it; a plain Task
  // parameter or field knows nothing
  readonly record struct TaskHandle(ClassInfo? Cls, string Ret);

  readonly Dictionary<VarInfo, TaskHandle> taskHandles = new();

  // the top-level Task and Thread variables, by name: the functions reach them by name, not through their scope (spec 14.1, 20.9)
  readonly Dictionary<string, TaskHandle> fileTaskHandles = new();
  readonly Dictionary<string, Node> fileTaskTypeNodes = new();   // the type written on a top-level Task or Thread variable (DeclareFileVars)

  // before the code: what each top-level Task or Thread variable holds. Task<...> says it; a plain one knows its task when its declaration and
  // every assignment of the program (top level and functions) give a task of the same class with the same results; otherwise it knows
  // nothing, as a local that is given different tasks
  void PlanFileTaskHandles(Node program) {
    if (!TasksMode) {
      return;
    }
    var empty = new Scope(null);
    var assigned = new Dictionary<string, List<Node>>();
    void Walk(Node node) {
      if (node.Kind == "Assign" && node.Text == "=" && node.Children.Count == 2 && node.Children[0].Kind == "Name") {
        string name = node.Children[0].Text!;
        if (!assigned.TryGetValue(name, out var values)) {
          assigned[name] = values = new List<Node>();
        }
        values.Add(node.Children[1]);
      }
      foreach (var child in node.Children) {
        Walk(child);
      }
    }
    Walk(program);
    // the top-level variables the functions use: their declarations are assignments by now (DeclareFileVars), the line that gave the
    // value is one of the assignments
    foreach (var (name, typeNode) in fileTaskTypeNodes) {
      if (taskTypeSpecs.TryGetValue(typeNode, out var spec)) {
        fileTaskHandles[name] = ResolveTaskSpec(spec);
        continue;
      }
      var values = assigned.TryGetValue(name, out var given) ? given : new List<Node>();
      var handles = new List<TaskHandle?>();
      foreach (var value in values.Where(value => value.Kind != "Null")) {
        TaskHandle? handle;
        try {
          handle = TaskHandleOf(value, empty);
        } catch (CompileException) {
          handle = null;   // reported where the value is compiled
        }
        handles.Add(handle);
      }
      if (handles.Count > 0 && handles.All(handle => handle != null) && handles.Distinct().Count() == 1) {
        fileTaskHandles[name] = handles[0]!.Value;
      }
    }
  }

  // Task<...> as written: the class (null for *) and the result types; the type node keeps only Task (one C type for every handle)
  sealed record TaskTypeSpec(string? ClassName, List<Node> Results, Node At);
  readonly Dictionary<Node, TaskTypeSpec> taskTypeSpecs = new(ReferenceEqualityComparer.Instance);
  readonly Dictionary<(Node Fun, string Param), Node> taskParamTypes = new();

  // before the code: every Task<...> type becomes Task, its arguments are kept aside (TaskTypeSpec)
  void LowerTaskHandleTypes(Node node) {
    if (node.Kind == "Type" && node.Text == "Thread" && node.Children.Count > 0) {
      // Thread<u64>, Thread<i32, string>: what the function gives (spec 20.1)
      if (node.Children.Any(child => child.Kind == "AnyClass")) {
        throw Error("a thread has no class: Thread<...> takes only what the function gives, Thread<u64> (spec 20.1)", node);
      }
      taskTypeSpecs[node] = new TaskTypeSpec(null, node.Children.ToList(), node);
      node.Children.Clear();
      return;
    }
    if (node.Kind == "Type" && node.Text == "Task" && node.Children.Count > 0) {
      var first = node.Children[0];
      string? className = first.Kind switch {
        "AnyClass" => null,
        "Type" when first.Children.Count == 0 => first.Text,
        _ => throw Error("Task<...> starts with the class of the task, or * for any class: Task<MyTask>, Task<*, i32> (spec 20.9)", node)
      };
      if (className == null && node.Children.Count == 1) {
        throw Error("Task<*> says nothing: write Task, or the results after the star, Task<*, i32> (spec 20.9)", node);
      }
      taskTypeSpecs[node] = new TaskTypeSpec(className, node.Children.Skip(1).ToList(), node);
      node.Children.Clear();
      return;
    }
    if (node.Kind == "AnyClass") {
      throw Error("* stands for any class only as the first argument of Task<*, ...> (spec 20.9)", node);
    }
    foreach (var child in node.Children) {
      LowerTaskHandleTypes(child);
    }
  }

  // what taskRun() gives, as the program writes it (the check of Task<*, i32> at run time)
  string TaskResultText(string ret) => IsMulti(ret) ? string.Join(", ", TupleElems(ret).Select(elem => DisplayName(elem.Type))) : DisplayName(ret);

  TaskHandle ResolveTaskSpec(TaskTypeSpec spec) {
    ClassInfo? cls = null;
    if (spec.ClassName != null) {
      if (!classes.TryGetValue(spec.ClassName, out cls) || cls.IsTrait) {
        throw Error($"'{spec.ClassName}' is not a class: Task<...> starts with the class of the task (spec 20.9)", spec.At);
      }
      if (TaskRunOf(cls) == null) {
        throw Error($"{DisplayName(cls.Name)} has no taskRun(): it is not a task (spec 20.1)", spec.At);
      }
    }
    var results = spec.Results.Select(ResolveType).ToList();
    string ret = results.Count == 0 ? TaskRunOf(cls!)!.Ret
      : results.Count == 1 ? results[0]
      : "*" + MakeTuple(results.Select(type => (type, (string?)null)));
    if (cls != null && results.Count > 0 && TaskRunOf(cls)!.Ret != ret) {
      throw Error($"the taskRun() of {DisplayName(cls.Name)} gives {TaskResultText(TaskRunOf(cls)!.Ret)}, not {TaskResultText(ret)} (spec 20.9)", spec.At);
    }
    return new TaskHandle(cls, ret);
  }

  // the class of the task an expression makes: Task(MyTask, ...), Task(MyTask, ...).start(...); null when it is not seen here
  ClassInfo? TaskClassOfNode(Node node, Scope scope) {
    if (node.Kind == "Paren") {
      return TaskClassOfNode(node.Children[0], scope);
    }
    if (node.Kind != "Call") {
      return null;
    }
    if (node.Children[0] is { Kind: "Member", Text: "start" } start) {
      return TaskClassOfNode(start.Children[0], scope);
    }
    if (node.Children[0] is { Kind: "Name", Text: "Task" } && scope.Find("Task") == null && node.Children[1].Children is [{ Kind: "Name" } first, ..]
        && scope.Find(first.Text!) == null && classes.TryGetValue(first.Text!, out var cls) && TaskRunOf(cls) != null) {
      return cls;
    }
    return null;
  }

  // what an expression of type Task knows of its task: made here, or a variable that knows; null when nothing is known
  TaskHandle? TaskHandleOf(Node node, Scope scope) {
    while (node.Kind is "WritableAddressOf" or "Gift" or "Paren" && node.Children.Count == 1) {
      node = node.Children[0];   // &t, ->t
    }
    if (node.Kind == "Call" && node.Children[0] is { Kind: "Member", Text: "start" } started && started.Children[0].Kind is "Name" or "Member") {
      return TaskHandleOf(started.Children[0], scope);   // a.start(): the handle itself (spec 20.1)
    }
    if (TaskClassOfNode(node, scope) is ClassInfo made) {
      return new TaskHandle(made, TaskRunOf(made)!.Ret);
    }
    if (ThreadResultOfNode(node, scope) is string threadResult) {
      return new TaskHandle(null, threadResult);   // Thread(runThis, ...): what the function gives
    }
    if (node.Kind == "Name" && scope.Find(node.Text!) is VarInfo variable && taskHandles.TryGetValue(variable, out var known)) {
      return known;
    }
    if (node.Kind == "Name" && scope.Find(node.Text!) is null or { IsLocal: false } && fileVars.ContainsKey(node.Text!) && IsFileVarHere(node.Text!, node)
        && fileTaskHandles.TryGetValue(node.Text!, out var topLevel)) {
      return topLevel;   // a top-level Task named in a function, or at top level once it forgot (spec 20.9)
    }
    return TaskHandleOfMember(node, scope);   // a Task<...> field, a function or a method that gives a Task<...> (CodeGen.TaskHandles.cs)
  }

  bool IsClassOrChild(ClassInfo cls, ClassInfo of) {
    for (var c = cls; c != null; c = c.Parent) {
      if (c == of) {
        return true;
      }
    }
    return false;
  }

  // a value for a Task<...>: it must be a task of that class (or a child), or give those results (spec 20.9)
  void CheckTaskHandleValue(TaskHandle wanted, TaskHandle? given, Node at) {
    if (given is not TaskHandle value) {
      throw Error("this Task does not say what its task is: check it with if t is Task<...> name { } (spec 20.9)", at);
    }
    if (wanted.Cls != null && (value.Cls == null || !IsClassOrChild(value.Cls, wanted.Cls))) {
      throw Error($"this is not a task of {DisplayName(wanted.Cls.Name)} (spec 20.9)", at);
    }
    if (wanted.Ret != value.Ret) {
      throw Error($"this gives {TaskResultText(value.Ret)}, not {TaskResultText(wanted.Ret)} (spec 20.9)", at);
    }
  }

  // after a declaration: what the Task variable knows (its Task<...> type, or the task made on its line)
  void NoteTaskVar(Node decl, Scope scope) {
    if (!TasksMode || !scope.Vars.TryGetValue(decl.Text!, out var variable) || variable.Type is not ("Task" or "Thread")) {
      return;
    }
    var init = decl.Children.FirstOrDefault(c => c.Kind == "Init")?.Children[0];
    var given = init == null ? null : TaskHandleOf(init, scope);
    if (decl.Children.FirstOrDefault(c => c.Kind == "Type") is Node typeNode && taskTypeSpecs.TryGetValue(typeNode, out var spec)) {
      var wanted = ResolveTaskSpec(spec);
      if (init != null && init.Kind != "Null") {
        CheckTaskHandleValue(wanted, given, init);
      }
      taskHandles[variable] = wanted;
      typedTaskVars.Add(variable);   // t = ... is checked (CodeGen.TaskHandles.cs)
      return;
    }
    if (given is TaskHandle known) {
      taskHandles[variable] = known;
    }
  }

  void NoteHoistedTask(Node decl, Scope scope) {
    if (!TasksMode || !scope.Vars.TryGetValue(decl.Text!, out var variable) || variable.Type is not ("Task" or "Thread")) {
      return;
    }
    string? saved = handlerOverride;
    handlerOverride ??= "df_hoist_typing";
    hoistTyping++;
    try {
      NoteTaskVar(decl, scope);
    } catch (CompileException) {
      // reported when the declaration is compiled
    } finally {
      hoistTyping--;
      handlerOverride = saved;
    }
  }

  // the parameters of a function: a Task<...> parameter knows what its task is (spec 20.9)
  void NoteTaskParams(FunSig f, Scope scope) {
    if (!TasksMode) {
      return;
    }
    foreach (var p in f.Params) {
      if (taskParamTypes.TryGetValue((f.Node, p.Name), out var typeNode) && taskTypeSpecs.TryGetValue(typeNode, out var spec)
          && scope.Vars.TryGetValue(p.Name, out var variable)) {
        taskHandles[variable] = ResolveTaskSpec(spec);
        typedTaskVars.Add(variable);
      }
    }
  }

  // f(t) with a parameter Task<...>: the argument must fit (spec 20.9)
  void CheckTaskArgument(FunSig f, ParamSig p, Node arg, Scope scope) {
    if (TasksMode && !HoistTyping && p.Type is "Task" or "Thread" && taskParamTypes.TryGetValue((f.Node, p.Name), out var typeNode) && taskTypeSpecs.TryGetValue(typeNode, out var spec)) {
      CheckTaskHandleValue(ResolveTaskSpec(spec), TaskHandleOf(arg, scope), arg);
    }
  }

  // if t is Task<MyTask> mt { }, if t is Task<*, i32> n { } (spec 20.9): the same handle, seen with what it knows
  (string Condition, Scope BlockScope)? TaskIsBinding(Node n, Scope scope) {
    if (!TasksMode || n.Children.Count < 2 || !taskTypeSpecs.TryGetValue(n.Children[1], out var spec)) {
      return null;
    }
    var subject = ExprOf(n.Children[0], scope);
    if (subject.Type != "Task") {
      throw Error($"only a Task is checked against Task<...>, not {DisplayName(subject.Type)}", n);
    }
    var handle = ResolveTaskSpec(spec);
    CheckNotShadowing(n.Text!, scope, n);
    tempCounter++;
    string cname = $"v_{n.Text}_{tempCounter}";
    fn!.Decls.Append($"  {CType("Task")} {cname} = NULL;\n");
    string temp = NewTemp();
    string cls = handle.Cls == null ? "NULL" : $"&{handle.Cls.Descriptor}";
    string result = spec.Results.Count == 0 ? "NULL" : $"\"{TaskResultText(handle.Ret)}\"";
    string condition = $"({{ {CType("Task")} {temp} = {subject.Code}; bool ok_{temp} = {temp} != NULL && df_task_is({temp}->m_handle, {cls}, {result}); "
      + $"if (ok_{temp}) {{ {cname} = {temp}; }} ok_{temp}; }})";
    var inner = new Scope(scope);
    var bound = new VarInfo("Task", cname, false);
    inner.Vars[n.Text!] = bound;
    taskHandles[bound] = handle;
    taskAliases[bound] = n.Children[0];
    return (condition, inner);
  }

  // a loop of taskRun() that never ends and has no checkpoint: its calls are never served (spec 20.8, level 1)
  void WarnLoopWithoutCheckpoint(Node s) {
    if (!InTaskRunBody || s.Kind != "While" || s.Children.Count == 0 || s.Children[0] is not { Kind: "Bool", Text: "true" }) {
      return;
    }
    if (!s.Children.Skip(1).Any(HasCheckpoint)) {
      Warn("this loop of taskRun() never ends and has no checkpoint (a wait, an enforce, a return): the calls through the handle are never served (spec 20.8)", s, 1);
    }
  }

  // a wait written in taskRun(): the calls through the handle are served while it waits (spec 20.8)
  string Serving(string call) => InTaskRunBody ? $"DF_SERVING(self, {call})" : call;

  static bool HasCheckpoint(Node node) =>
    node.Kind is "Wait" or "WaitOn" or "WaitMulti" or "Return" or "ReturnHold" or "ReturnPost" or "Enforce" || IsTimeWaitCall(node)
    || node.Kind is not ("Lambda" or "Fun") && node.Children.Any(HasCheckpoint);

  // Root.Time.wait(t), Time.wait(t): a timed wait (spec 20.8); seen by its shape, before the names are resolved
  static bool IsTimeWaitCall(Node node) =>
    node.Kind == "Call" && node.Children[0] is { Kind: "Member", Text: "wait" } member
    && member.Children[0] is { Kind: "Name", Text: "Time" } or { Kind: "Member", Text: "Time" };
  // ---------------------------------------------------------------- wait (spec 20.2)

  // the timeout block of a wait that gives a value (spec 20.2.8): one value stays in the wait (WaitDefault), several lines run after it
  // (WaitNote). Lowered before the enforce expressions
  void LowerWaitDefaults(Node node) {
    for (int index = 0; index < node.Children.Count; index++) {
      var child = node.Children[index];
      if (child is { Kind: "EnforceExpr" } && child.Children.Count > 0 && child.Children[0].Kind == "Wait") {
        throw Error("enforce wait ... default is replaced by the timeout block: wait t timeout 1000 { 123 } (spec 20.2.8)", child);
      }
      if (child is { Kind: "Decl" } && child.Children.FirstOrDefault(c => c.Kind == "Init") is { Children: [{ Kind: "Wait" } declWait] } declInit
          && declWait.Children.FirstOrDefault(c => c.Kind == "TimeoutBlock") is Node longBlock && longBlock.Children[0].Children is not [{ Kind: "ExprStmt" }]) {
        // i32 r = wait t timeout 500 { log("late"); return -1 } (spec 20.2.8, 7.2.6): r takes what the wait gives, then the timeout block runs
        // when the wait gave nothing (its C flag, WaitNote) and gives r its value with return
        node.Children.Insert(index + 1, LateWaitBlock(declWait, longBlock, new List<string> { child.Text! }));
      }
      if (child is { Kind: "Assign", Children: [{ Kind: "Name" } assigned, { Kind: "Wait" } assignWait] } && child.Text is null or "="
          && assignWait.Children.FirstOrDefault(c => c.Kind == "TimeoutBlock") is Node assignBlock && assignBlock.Children[0].Children is not [{ Kind: "ExprStmt" }]) {
        // r = wait t timeout 500 { several lines }: as for a declaration (spec 20.2.8)
        node.Children.Insert(index + 1, LateWaitBlock(assignWait, assignBlock, new List<string> { assigned.Text! }));
      }
      if (child is { Kind: "MultiAssign" } && child.Children.Count == 2 && child.Children[1].Children is [{ Kind: "Wait" } multiWait]
          && multiWait.Children.FirstOrDefault(c => c.Kind == "TimeoutBlock") is Node multiBlock
          && child.Children[0].Children.All(target => target.Kind is "Name" or "Decl")) {
        // i32 a, string b = wait t timeout 500 { ...; return 1, "x" }; a, b = wait ... (spec 20.2.8): several values
        node.Children.Insert(index + 1, LateWaitBlock(multiWait, multiBlock, child.Children[0].Children.Select(target => target.Text!).ToList()));
      }
      if (child is { Kind: "ExprStmt", Children: [{ Kind: "Wait" } statementWait] } && statementWait.Children.Any(c => c.Kind == "TimeoutBlock")) {
        // wait t timeout 50 { ... } as a statement (spec 20.2.1): the statement form, with no block for the waitable
        var waitOn = Synthetic("WaitOn", statementWait);
        waitOn.Children.AddRange(statementWait.Children);
        node.Children[index] = waitOn;
        child = waitOn;
      }
      if (child is { Kind: "Wait" } && child.Children.FirstOrDefault(c => c.Kind == "TimeoutBlock") is Node timeoutBlock) {
        // var res = wait t timeout 500 { 0 } (spec 20.2.8, 7.2.6): the value of the timeout block when the wait gives nothing
        var body = timeoutBlock.Children[0];
        if (body.Children is not [{ Kind: "ExprStmt" } value]) {
          throw Error("the timeout block of a wait that gives a value is one value for now: timeout 500 { 0 } (spec 20.2.8)", body);
        }
        child.Children.Remove(timeoutBlock);
        child.Add(Synthetic("WaitDefault", body).Add(value.Children[0]));
      }
      LowerWaitDefaults(child);
    }
  }

  // a wait always writes its timeout (spec 20.2.8); the waitables have none of their own
  void RequireWaitTimeout(Node? timeoutNode, Node at) {
    if (timeoutNode == null) {
      throw Error("a wait writes its timeout: wait x timeout 500 (spec 20.2.8)", at);
    }
  }

  // the timeout block runs after the wait, when the wait gave nothing (its C flag): if (note) { block }
  Node LateWaitBlock(Node wait, Node timeoutBlock, List<string> targets) {
    string note = $"df_wnote{++enforceTemps}";
    wait.Children.RemoveAll(c => c == timeoutBlock);
    wait.Add(Synthetic("WaitNote", timeoutBlock, note));
    var lateBlock = WaitValueBlock(timeoutBlock.Children[0], targets);
    foreach (string target in targets) {
      NoteLateValues(lateBlock, target);
    }
    return Synthetic("If", timeoutBlock).Add(Synthetic("CValue", timeoutBlock, note).Add(Synthetic("Type", timeoutBlock, "bool"))).Add(lateBlock);
  }

  // the timeout block that gives the values: return v (or return a, b) assigns the targets and leaves the block, also from a loop inside it
  // (break to a label after the block runs the ends of the scopes it leaves); a block that is one expression assigns it
  Node WaitValueBlock(Node block, List<string> targets, string? oneValue = null) {
    if (block.Children is [{ Kind: "ExprStmt" } single]) {
      return Synthetic("Block", block).Add(WaitAssign(targets, single.Children, single));
    }
    string label = $"df_wlate{++enforceTemps}";
    return Synthetic("Block", block).Add(RewriteWaitReturns(block, targets, label, oneValue)).Add(Synthetic("Label", block, label));
  }

  Node RewriteWaitReturns(Node block, List<string> targets, string label, string? oneValue = null) {
    var result = Synthetic(block.Kind, block, block.Text);
    foreach (var child in block.Children) {
      if (child.Kind == "Return") {
        if (child.Children.Count != targets.Count) {
          throw Error(targets.Count == 1
            ? oneValue ?? "the timeout block gives the value of the wait with return value (spec 20.2.8)"
            : $"the timeout block gives the {targets.Count} values of the wait: return {string.Join(", ", targets)} (spec 20.2.8)", child);
        }
        result.Add(WaitAssign(targets, child.Children, child));
        result.Add(Synthetic("Break", child, label));
        continue;
      }
      if (child.Kind is "Fun" or "Lambda") {
        result.Add(child);
        continue;
      }
      result.Add(RewriteWaitReturns(child, targets, label, oneValue));
    }
    return result;
  }

  // target = value, or a, b = v1, v2
  Node WaitAssign(List<string> targets, List<Node> values, Node at) {
    if (targets.Count == 1 && values.Count == 1) {
      return AssignTo(targets[0], values[0]);
    }
    var names = Synthetic("Targets", at);
    targets.ForEach(target => names.Add(Synthetic("Name", at, target)));
    var given = Synthetic("Values", at);
    values.ForEach(value => given.Add(value));
    return Synthetic("MultiAssign", at).Add(names).Add(given);
  }

  readonly HashSet<Node> lateValues = new(ReferenceEqualityComparer.Instance);   // the names assigned by the return of a timeout block

  void NoteLateValues(Node node, string target) {
    if (node.Kind == "Assign" && node.Children[0] is { Kind: "Name" } name && name.Text == target) {
      lateValues.Add(name);
    }
    node.Children.ForEach(child => NoteLateValues(child, target));
  }

  // the C flag of a wait whose timeout block runs after it, true when the wait gave nothing; null for any other wait (spec 20.2.8)
  string? WaitNoteFlag(Node wait) {
    if (wait.Children.FirstOrDefault(c => c.Kind == "WaitNote")?.Text is not string note) {
      return null;
    }
    if (paramKeeps.Add((fn!, note))) {
      fn!.Decls.Append($"  bool {note} = false;\n  (void){note};\n");
    }
    return note;
  }

  // the call of a wait: the silent exceptions it can leave (none: ""). A timeout is none of them, its note goes away
  string WaitCall(string call, Node n, string exceptions) {
    string temp = NewTemp();
    if (exceptions == "") {
      return $"({{ df_exception* {temp} = {call}; df_silent_desc = NULL; {temp}; }})";
    }
    return SilentCall($"({{ df_exception* {temp} = {call}; if (df_silent_desc == &df_note_late) {{ df_silent_desc = NULL; }} {temp}; }})", "Exception", exceptions, n);
  }

  // T x = wait t, var x = wait t: a value is wanted; a plain Task (a parameter, a field) does not know the result of its task (spec 20.9)
  void CheckWaitGivesValue(Node decl, Scope scope) {
    if (!TasksMode || decl.Children.FirstOrDefault(c => c.Kind == "Init") is not { Children: [{ Kind: "Wait" } waitNode] }
        || waitNode.Children.Any(c => c.Kind == "WaitDefault")) {
      return;
    }
    var target = waitNode.Children[0];
    if (target.Kind == "Call" && target.Children[0] is { Kind: "Member" } called && !TaskOwnMembers.Contains(called.Text!)) {
      return;   // wait t.getCount(): checked by TaskMethodCall
    }
    var waited = ExprOf(target, scope);
    if (waited.Type is "Task" or "Thread" && TaskHandleOf(target, scope) is not TaskHandle) {
      throw Error("this Task does not say what its task gives: wait on it gives no value (spec 20.9)", waitNode);
    }
    if (waited.Type is "Task" or "Thread" && TaskHandleOf(target, scope) is TaskHandle { Ret: "void" }) {
      throw Error("this task returns nothing: wait on it gives no value", waitNode);
    }
  }

  // wait task [timeout t]: the next value of the task; wait 250: a timed wait
  Expr TaskWait(Node n, Scope scope) {
    if (n.Children.Count == 0) {
      // a bare wait (spec 20.8): a checkpoint of taskRun(), it does not stop
      return new Expr(InTaskRunBody ? CheckRaise("df_task_checkpoint((void*)self)", "void") : "((void)0)", "void");
    }
    var target = n.Children[0];
    var timeoutNode = n.Children.FirstOrDefault(c => c.Kind == "Timeout")?.Children[0];
    if (TaskMethodTarget(target, scope) is { } waitedCall) {
      RequireWaitTimeout(timeoutNode, n);
      // wait t.getCount() timeout 500: a call through the handle that gives a value (spec 20.1)
      string callTimeout = timeoutNode == null ? "0" : Convert(ExprOf(timeoutNode, scope, "lapse"), "lapse", timeoutNode);
      if (n.Children.FirstOrDefault(c => c.Kind == "WaitDefault") is Node callDefault) {
        // wait t.getCount() timeout 500 { 0 }: the value of the timeout block when the call gives nothing (spec 20.2.8)
        string nothing = NewTemp();
        var made = TaskMethodCall(target, waitedCall.Receiver, waitedCall.Cls, true, callTimeout, scope, nothing);
        string value = NewTemp();
        var other = ExprOf(callDefault.Children[0], scope, made.Type);
        string otherCode = Convert(other, made.Type, callDefault);
        return made with { Code = $"({{ bool {nothing} = false; {CType(made.Type)} {value} = {made.Code}; "
          + $"if ({nothing}) {{ {value} = {(made.Type == "string" ? StrOwned(other, otherCode) : otherCode)}; }} {value}; }})" };
      }
      return TaskMethodCall(target, waitedCall.Receiver, waitedCall.Cls, true, callTimeout, scope, WaitNoteFlag(n));
    }
    var waited = ExprOf(target, scope);
    if (!(IsNumeric(waited.Type) || waited.Type == "lapse" || n.Children.Any(c => c.Kind == "WaitTaken"))) {
      RequireWaitTimeout(timeoutNode, n);
    }
    if (IsNumeric(waited.Type) || waited.Type == "lapse") {
      if (timeoutNode != null) {
        throw Error("wait 250 waits a time: it has no timeout (spec 20.8)", n);
      }
      string lapse = Convert(waited, "lapse", target);
      if (InTaskRunBody) {
        return new Expr(CheckRaise($"df_task_pause({lapse}, (void*)self)", "void"), "void");   // the calls through the handle are served meanwhile (spec 20.8)
      }
      return new Expr(TasksMode ? CheckRaise($"df_task_pause({lapse}, NULL)", "void") : $"df_time_sleep({lapse})", "void");   // wait 250, wait lapse(10) (spec 20.8); the cancel signal of a runnable (20.10)
    }
    if (waited.Type is "Signal" or "Token") {
      return SignalWait(n, waited, timeoutNode, scope);   // spec 20.3 (CodeGen.Signals.cs)
    }
    if (waited.Type == "SignalGroup") {
      return SignalGroupWait(n, waited, timeoutNode!, scope);   // spec 20.3 (CodeGen.Signals.cs)
    }
    if (waited.Type is not ("Task" or "Thread")) {
      throw Error($"wait takes a task, a thread, a signal, a token or a time, not {DisplayName(waited.Type)} (spec 20.2)", target);
    }
    if (!n.Children.Any(c => c.Kind == "WaitTaken")) {
      CheckCanWait(target, waited, scope);   // spec 20.2.6
    }
    string ret = TaskHandleOf(target, scope)?.Ret ?? "void";
    if (n.Children.FirstOrDefault(c => c.Kind == "WaitTaken") is Node taken) {
      return new Expr(taken.Text!, ret, ret == "string" || IsClass(ret) || IsArr(ret));   // a branch of a wait on several waitables: the value is there
    }
    if (HoistTyping) {
      if (ret == "void") {
        throw Error(TaskHandleOf(target, scope) == null ? "this Task does not say what its task gives: wait on it gives no value (spec 20.9)"
          : "this task returns nothing: wait on it gives no value", n);
      }
      return new Expr("0", ret, ret == "string" || IsClass(ret) || IsArr(ret));   // only the type is wanted
    }
    string slot = NewTemp();
    string outCode = ret == "void" ? "NULL, 0" : $"&{slot}, sizeof({slot})";
    bool fresh = ret == "string" || IsClass(ret) || IsArr(ret);
    if (n.Children.FirstOrDefault(c => c.Kind == "WaitDefault") is Node defaultNode) {
      // enforce wait ... default { v }: v when the wait gives nothing
      if (ret == "void") {
        throw Error("enforce wait gives a value: this task gives none", n);
      }
      var fallback = ExprOf(defaultNode.Children[0], scope, ret);
      string note = NewTemp();
      string waitCode = WaitOne(n, waited, outCode, timeoutNode!, null, null, scope, note);
      return new Expr($"({{ {CType(ret)} {slot} = {ZeroValue(ret)}; const df_descriptor* {note} = NULL; {waitCode} "
        + $"if ({note}) {{ {slot} = {(ret == "string" ? StrOwned(fallback, Convert(fallback, ret, defaultNode)) : Convert(fallback, ret, defaultNode))}; }} {slot}; }})", ret, fresh);
    }
    if (WaitNoteFlag(n) is string flag) {
      if (ret == "void") {
        throw Error("this wait gives no value: wait x timeout 500 { ... } is a statement (spec 20.2.8)", n);
      }
      string silent = NewTemp();
      string noted = WaitOne(n, waited, outCode, timeoutNode!, null, null, scope, silent);
      return new Expr($"({{ {CType(ret)} {slot} = {ZeroValue(ret)}; const df_descriptor* {silent} = NULL; {flag} = false; {noted} {flag} = {silent} != NULL; {slot}; }})", ret, fresh);
    }
    string waitCall = WaitOne(n, waited, outCode, timeoutNode!, "TaskAlreadyTerminatedException,TaskWasNotStartedException", null, scope);
    if (ret == "void") {
      return new Expr($"({{ {waitCall} }})", "void");
    }
    return new Expr($"({{ {CType(ret)} {slot} = {ZeroValue(ret)}; {waitCall} {slot}; }})", ret, fresh);
  }

  // ---------------------------------------------------------------- calls through the handle (spec 20.1)

  // the code is the body of a taskRun() (not a lambda in it): its checkpoints serve the calls through the handle (spec 20.8)
  bool InTaskRunBody => TasksMode && currentFun is { Class: not null, IsStatic: false, LambdaCtx: null } run
    && run.Name.Split('.')[^1] == "taskRun" && run.Params.Count == 0;

  static bool ContainsWait(Node node) =>
    node.Kind is "Wait" or "WaitOn" or "WaitMulti" || node.Kind is not ("Lambda" or "Fun") && node.Children.Any(ContainsWait);

  // before a statement of taskRun() that is a checkpoint (a return, an enforce, a wait in it): the calls waiting are served
  void EmitCheckpoint(Node s, StringBuilder sb, string pad) {
    if (InTaskRunBody && (s.Kind is "Return" or "Enforce" || s.Kind != "Wait" && ContainsWait(s))) {
      sb.Append(pad).Append(CheckRaise("df_task_checkpoint((void*)self)", "void")).Append(";\n");
    }
  }

  // t.count: the fields of the task are never reached through the handle, public ones included (spec 20.1)
  void CheckTaskFieldAccess(Node member, Scope scope) {
    if (!TasksMode || member.Kind != "Member" || TaskOwnMembers.Contains(member.Text!) || member.Children[0] is not { Kind: "Name" } held
        || scope.Find(held.Text!) == null && !globalConsts.ContainsKey(held.Text!)) {
      return;
    }
    var receiver = ExprOf(member.Children[0], scope);
    if (receiver.Type == "Task" && TaskHandleOf(member.Children[0], scope)?.Cls is ClassInfo cls && FindField(cls, member.Text!) != null) {
      throw Error($"'{member.Text}' is a field of {DisplayName(cls.Name)}: fields are never reached through the handle, only methods (spec 20.1)", member);
    }
  }

  // the members of the class Task itself (TasksPrelude): not calls through the handle
  static readonly HashSet<string> TaskOwnMembers = new() { "start", "signal", "kill", "status", "kind", "name", "linked", "startTime", "get_status", "get_kind", "get_name", "get_linked", "get_startTime" };

  // a method name of a class that runs as a task (for the exceptions a call can let out, CodeGen.Leaks.cs)
  bool IsTaskMethodName(string name) => TasksMode && !TaskOwnMembers.Contains(name)
    && classes.Values.Any(cls => TaskRunOf(cls) != null && LookupMethods(cls, name).Count > 0);

  // t.method(...) where t is a Task that knows its class; null when the call is not one
  (Node Receiver, ClassInfo Cls)? TaskMethodTarget(Node call, Scope scope) {
    if (!TasksMode || call.Kind != "Call" || call.Children[0] is not { Kind: "Member" } member || TaskOwnMembers.Contains(member.Text!)) {
      return null;
    }
    var receiver = member.Children[0];
    ClassInfo? cls = TaskClassOfNode(receiver, scope);
    if (cls == null && receiver.Kind == "Name" && (scope.Find(receiver.Text!) != null || globalConsts.ContainsKey(receiver.Text!))) {
      var named = ExprOf(receiver, scope);
      if (named.Type != "Task") {
        return null;
      }
      cls = TaskHandleOf(receiver, scope)?.Cls;
      if (cls == null) {
        throw Error($"this Task does not say the class of its task: '{member.Text}' cannot be called through it (spec 20.9)", call);
      }
    }
    if (cls == null && receiver.Kind is "Name" or "Member" or "Call") {
      cls = TaskHandleOfMember(receiver, scope)?.Cls;   // a Task<MyTask> field, or a function that gives one (spec 20.9)
    }
    return cls == null ? null : (receiver, cls);
  }

  readonly Dictionary<FunSig, string> taskCallRecords = new();   // a method called through the handle -> its record (df_tcr_N)
  readonly StringBuilder taskCallCode = new();                   // the invoke and end functions of the records (TaskRunFunctions)

  bool TaskCallValueType(string type) =>
    type == "string" || IsNumeric(type) || type is "bool" or "char" or "lapse" or "date" or "time" or "clock" || IsEnum(type)
    || IsStruct(type) && !StructHoldsObjects(type);

  // what a call through the handle gives back (spec 20.12): values, and what the method makes or gives (its owner is the caller); several
  // results of those
  bool TaskCallResultType(FunSig f) {
    if (IsMulti(f.Ret)) {
      return TupleElems(f.Ret).All(elem => TaskCallValueType(elem.Type));
    }
    if (TaskCallValueType(f.Ret)) {
      return true;
    }
    if (IsCountedType(f.Ret) || IsWeakType(f.Ret) || IsFunType(f.Ret) || f.RetWritable) {
      return false;
    }
    if (TaskCallLentResult(f)) {
      return true;   // a lend of the memory of the task: allowed, with the level 2 warning of the call (spec 20.12)
    }
    if (f.Writes.Count > 0) {
      return false;
    }
    return IsClass(f.Ret) ? f.ReturnsOwned : IsArr(f.Ret) || IsTuple(f.Ret) || IsStruct(f.Ret);
  }

  // a result that lends what the task holds (spec 13.1.1, 20.12): fun &i32 scoreRef(), fun Pet first(), fun &Pet edit(), fun arr<i32> items()
  bool TaskCallLentResult(FunSig f) => !IsMulti(f.Ret)
    && (IsValueLend(f.Ret) || IsClass(f.Ret) && !f.ReturnsOwned || IsArr(f.Ret) && !f.Gives.Contains(0));

  // wait t.first() timeout n where every method of that name in the classes run as tasks lends its result: the local borrows it
  bool WaitedLentCall(Node wait) {
    if (wait.Children.Count == 0 || wait.Children[0] is not { Kind: "Call" } call || call.Children[0] is not { Kind: "Member" } member) {
      return false;
    }
    var methods = classes.Values.Where(cls => TaskRunOf(cls) != null).SelectMany(cls => LookupMethods(cls, member.Text!)).Where(m => !m.IsStatic).ToList();
    return methods.Count > 0 && methods.All(TaskCallLentResult);
  }

  // ... and every one of them lends it read/write (fun &Pet edit())
  bool WaitedWritableCall(Node wait) => WaitedLentCall(wait) && wait.Children[0].Children[0] is { Kind: "Member" } member
    && classes.Values.Where(cls => TaskRunOf(cls) != null).SelectMany(cls => LookupMethods(cls, member.Text!)).Where(m => !m.IsStatic).All(m => m.Writes.Contains(0));

  // the name the warning of a lent result shows: what the method returns when it is a name or a field, otherwise the method
  static string LentResultName(FunSig f) {
    string method = f.Name.Split('.')[^1] + "()";
    var block = f.Node.Children.FirstOrDefault(child => child.Kind == "Block");
    if (block == null || block.Children.Count == 0) {
      return method;
    }
    var last = block.Children[^1];
    var value = last.Kind is "Return" or "ExprStmt" && last.Children.Count == 1 ? last.Children[0] : null;
    return value switch {
      { Kind: "Name" } => value.Text!,
      { Kind: "Member", Children: [{ Kind: "Name", Text: "this" }, ..] } => value.Text!,
      _ => method
    };
  }

  // a parameter of a call through the handle whose value the record owns (an object, an array, a collection, a struct or tuple of them)
  bool TaskCallOwnedParam(ParamSig p) => !TaskCallValueType(p.Type) && !TaskCallUsage(p);

  // @T and %T: the record holds the usage or the weak reference the argument took for the call; the method releases it, or the end of the
  // record when the call never ran (spec 12.7, 20.12)
  bool TaskCallUsage(ParamSig p) => IsCountedType(p.Type) || IsWeakType(p.Type);

  // the record of a method called through the handle: its arguments and its result, and the functions that run and end it
  string TaskCallRecord(FunSig f, Node at) {
    if (taskCallRecords.TryGetValue(f, out var known)) {
      return known;
    }
    if (f.Ret == "Thread" || f.Params.Any(p => p.Type == "Thread")) {
      throw Error("the handle of a thread never leaves its task (spec 20.1)", at);
    }
    foreach (var p in f.Params) {
      if ((IsCountedType(p.Type) || IsWeakType(p.Type)) && (p.IsWritable || p.Ref == 2)) {
        throw Error($"'{p.Name}' of {f.Name.Split('.')[^1]}: a call through the handle does not lend a counted or weak reference read/write (&@T, &%T) yet (spec 20.12)", at);
      }
    }
    if (f.Ret != "void" && !TaskCallResultType(f)) {
      throw Error($"{f.Name.Split('.')[^1]} gives {DisplayName(f.Ret)}: a call through the handle gives back values and the objects, arrays and collections the method makes or gives (->), not lends (spec 20.12)", at);
    }
    string name = "df_tcr" + taskCallRecords.Count;
    taskCallRecords[f] = name;
    var fields = new StringBuilder("typedef struct {\n  df_task_call head;\n");
    for (int index = 0; index < f.Params.Count; index++) {
      var p = f.Params[index];
      // & : the place of the caller (spec 20.12); a plain object or array: the value, owned by the record (o) when it is new, lent otherwise
      fields.Append(p.Ref == 2 ? $"  {CType(p.Type)}* a{index};\n" : $"  {CType(p.Type)} a{index};\n");
      if (TaskCallOwnedParam(p) && !p.IsGift && p.Ref != 2) {
        fields.Append($"  bool o{index};\n");
      }
    }
    if (f.Ret != "void") {
      fields.Append($"  {CType(f.Ret)} result;\n  bool taken;\n");
    }
    fields.Append($"}} {name};\n\n");
    typeDefs.Append(fields);
    // invoke: the method on the object of the task, with the arguments of the record
    string args = string.Concat(Enumerable.Range(0, f.Params.Count).Select(index => f.Params[index].IsGift && TaskCallOwnedParam(f.Params[index]) || TaskCallUsage(f.Params[index]) ? $", g{index}"
      : f.Params[index].Ref == 2 ? $", r->a{index}" : IsByPointer(f.Params[index]) ? $", &r->a{index}" : $", r->a{index}"));
    string call = $"{f.CName}(({CType(f.Class!.Name)})obj{args})";
    var invoke = new StringBuilder($"static void {name}_invoke(void* obj, df_task_call* c) {{\n  {name}* r = ({name}*)c;\n");
    for (int index = 0; index < f.Params.Count; index++) {
      if (f.Params[index].IsGift && TaskCallOwnedParam(f.Params[index]) || TaskCallUsage(f.Params[index])) {
        // ->T, @T, %T: the method owns it now (it releases a usage at its end); the record keeps nothing to end (spec 20.12)
        invoke.Append($"  {CType(f.Params[index].Type)} g{index} = r->a{index};\n  r->a{index} = {ZeroValue(f.Params[index].Type)};\n");
      }
    }
    if (PairReturn(f)) {
      invoke.Append(f.Ret == "void" ? $"  r->head.error = {call};\n" : $"  {RetCType(f)} res = {call};\n  r->head.error = res.err;\n  r->result = res.value;\n");
    } else {
      invoke.Append(f.Ret == "void" ? $"  {call};\n" : $"  r->result = {call};\n");
      invoke.Append("  if (df_pending) {\n    r->head.error = df_pending;\n    df_pending = NULL;\n  }\n");
    }
    invoke.Append("}\n");
    var end = new StringBuilder($"static void {name}_end(df_task_call* c) {{\n  {name}* r = ({name}*)c;\n  (void)r;\n");
    for (int index = 0; index < f.Params.Count; index++) {
      if (f.Params[index].Type == "string" && f.Params[index].Ref != 2) {
        end.Append($"  df_str_free(r->a{index});\n");
      } else if (IsFunType(f.Params[index].Type) && f.Params[index].Ref != 2) {
        end.Append($"  df_fn_release(r->a{index});\n");   // the usage the record took (a gift was moved out by the call)
      } else if (IsCountedType(f.Params[index].Type)) {
        end.Append($"  if (r->a{index} != NULL) {{\n    (void)df_rc_release(r->a{index});\n  }}\n");   // a call never served
      } else if (IsWeakType(f.Params[index].Type)) {
        end.Append($"  if (r->a{index} != NULL) {{\n    df_rc_unweak(r->a{index});\n  }}\n");
      } else if (TaskCallOwnedParam(f.Params[index]) && f.Params[index].IsGift) {
        end.Append($"  {DropValueCode(f.Params[index].Type, $"r->a{index}")}\n");   // a gift never served
      } else if (TaskCallOwnedParam(f.Params[index]) && f.Params[index].Ref != 2) {
        end.Append($"  if (r->o{index}) {{\n    {DropValueCode(f.Params[index].Type, $"r->a{index}")}\n  }}\n");   // a new value, not a lend
      }
    }
    if (f.Ret == "string") {
      end.Append("  if (!r->taken) {\n    df_str_free(r->result);\n  }\n");
    } else if (f.Ret != "void" && !TaskCallLentResult(f) && (!TaskCallValueType(f.Ret) || IsMulti(f.Ret))) {
      end.Append($"  if (!r->taken) {{\n    {DropValueCode(f.Ret, "r->result")}\n  }}\n");   // a result nobody collected (spec 20.1)
    }
    end.Append("}\n");
    taskCallCode.Append(invoke).Append(end);
    return name;
  }

  // t.method(args) through the handle (spec 20.1): posted when the method returns nothing; waited for with wait t.method() timeout n
  Expr TaskMethodCall(Node call, Node receiverNode, ClassInfo cls, bool waited, string timeoutCode, Scope scope, string? nothing = null) {
    var member = call.Children[0];
    string name = member.Text!;
    var args = call.Children[1].Children;
    var candidates = LookupMethods(cls, name).Where(f => !f.IsStatic).ToList();
    if (candidates.Count == 0) {
      throw Error(FindField(cls, name) != null
        ? $"'{name}' is a field of {DisplayName(cls.Name)}: fields are never reached through the handle, only methods and properties (spec 20.1)"
        : $"{DisplayName(cls.Name)} has no function '{name}'", call);
    }
    var f = ResolveOverload(candidates, args, scope, call);
    if (f.Visibility == Vis.Private && !f.IsUnrestricted) {
      throw Error($"'{name}' is private in {DisplayName(cls.Name)}: through the handle the public, unrestricted and internal methods are called (spec 20.1)", call);
    }
    if (f.Visibility == Vis.Internal) {
      CheckAccess(f.Visibility, f.Class!, name, call);   // internal: the namespace of the class and its sub-namespaces (spec 1.6.1, 20.1)
    }
    if (!waited && f.Ret != "void") {
      throw Error($"{name}() gives a value: it is waited for, wait {DisplayName(receiverNode.Text ?? "t")}.{name}() timeout n (spec 20.1)", call);
    }
    if (waited && f.Ret == "void") {
      throw Error($"{name}() returns nothing: it is posted, without wait (spec 20.1)", call);
    }
    string record = TaskCallRecord(f, call);
    var receiver = ExprOf(receiverNode, scope);
    string handle = $"({TempObject(receiver).Code})->m_handle";
    string rec = NewTemp();
    var code = new StringBuilder($"({{ {record}* {rec} = ({record}*)df_task_call_new(sizeof({record}), {record}_invoke, {record}_end, {(waited ? "true" : "false")}); ");
    var named = args.Where(a => a.Kind == "Named").ToDictionary(a => a.Text!, a => a.Children[0]);
    var positional = args.Where(a => a.Kind != "Named").ToList();
    Node?[]? slots = null;
    if (f.Params.Any(p => p.IsVariadic)) {
      (slots, string? problem) = MapArgs(f, args);   // T name...: the extra arguments in one List node (spec 13.4)
      if (slots == null) {
        throw Error(problem!, call);
      }
    }
    bool lends = false;
    for (int index = 0; index < f.Params.Count; index++) {
      var p = f.Params[index];
      Node? argNode = slots != null ? slots[index] : index < positional.Count ? positional[index] : named.GetValueOrDefault(p.Name);
      string value;
      if (p.IsVariadic && argNode != null) {
        // T name...: values are copied into an array of the record; objects are lent, so the call is waited for (spec 13.4, 20.12)
        string lent = VariadicLend(p, argNode, scope) ?? ArgCode(p, argNode, scope, f, index);
        if (TaskCallValueType(ArrElem(p.Type))) {
          value = $"{ArrCloneHelper(p.Type, argNode)}({lent})";
          code.Append($"{rec}->o{index} = true; ");
        } else {
          if (!waited) {
            throw Error($"'{p.Name}...' of {name} lends its objects: a posted call does not lend (spec 20.12)", argNode);
          }
          value = lent;
          code.Append($"{rec}->o{index} = false; ");
          lends = true;
        }
      } else if (IsFunType(p.Type) && p.Ref != 2 && argNode != null) {
        // a function value runs inside the task: it cannot borrow variables of the caller (spec 20.12); the record holds a usage of it
        string fnCode = p.IsGift ? ArgCode(p, argNode, scope, f, index) : $"df_fn_retain({Convert(ExprOf(argNode, scope, p.Type), p.Type, argNode)})";
        if (FnMayBorrow(argNode, scope)) {
          throw Error($"this function value borrows variables of the caller, and '{p.Name}' of {name} runs it inside the task: capture them with x! or ->x (spec 20.12)", argNode);
        }
        value = fnCode;
        if (!p.IsGift) {
          code.Append($"{rec}->o{index} = true; ");
        }
      } else if (TaskCallUsage(p) && argNode != null) {
        value = ArgCode(p, argNode, scope, f, index);   // @x, %x: one more usage or weak reference, carried by the record (spec 12.7)
      } else if (argNode == null) {
        if (p.Default == null) {
          throw Error($"missing argument '{p.Name}' of {name}", call);
        }
        var fallback = ExprOf(p.Default, scope, p.Type);
        value = Convert(fallback, p.Type, call);
        if (TaskCallOwnedParam(p) && !p.IsGift && p.Ref != 2) {
          code.Append($"{rec}->o{index} = true; ");   // a default value: the record ends it
        }
        if (p.Type == "string") {
          value = $"df_str_dup({value})";
        }
      } else if (TaskCallOwnedParam(p) && p.IsGift) {
        value = ArgCode(p, argNode, scope, f, index);   // ->x: given to the record, then to the method (spec 20.12)
      } else if (p.Ref == 2) {
        // &x: the method works on the place of the caller, who waits for the end of the call (spec 20.12)
        if (!waited) {
          throw Error(f.Ret == "void"
            ? $"'{p.Name}' of {name} is lent read/write (&): a posted call does not lend; the method needs a result to be waited for (spec 20.12)"
            : $"'{p.Name}' of {name} is lent read/write (&): a call that lends is waited for: wait t.{name}(...) timeout n (spec 20.12)", argNode);
        }
        value = ArgCode(p, argNode, scope, f, index);
        lends = true;
      } else {
        var given = ExprOf(argNode, scope, p.Type);
        value = Convert(given, p.Type, argNode);
        if (TaskCallOwnedParam(p)) {
          if (!given.Fresh && !waited) {
            string clone = $"send a clone ({(argNode.Kind == "Name" ? argNode.Text : "x")}!) or a new value";
            throw Error(f.Ret == "void"
              ? $"'{p.Name}' of {name} is lent (plain): a posted call does not lend: {clone} (spec 20.12)"
              : $"'{p.Name}' of {name} is lent (plain): a call that lends is waited for (wait t.{name}(...) timeout n), or {clone} (spec 20.12)", argNode);
          }
          code.Append($"{rec}->o{index} = {(given.Fresh ? "true" : "false")}; ");   // a new value: the record ends it; otherwise a lend
          lends |= !given.Fresh;
        }
        if (p.Type == "string" && !given.Fresh) {
          value = $"df_str_dup({value})";   // the record owns its strings: the caller goes on (spec 20.12)
        }
      }
      code.Append($"{rec}->a{index} = {value}; ");
    }
    if (lends) {
      code.Append($"{rec}->head.lends = true; ");   // the caller waits for the end once it has started (spec 20.12, temporary)
    }
    string refuse = $"if (!df_task_post_call({handle}, &{rec}->head)) {{ df_task_call_release(&{rec}->head); {(waited ? $"df_task_call_release(&{rec}->head); " : "")}"
      + $"df_raise(&df_desc_CalledANotRunningTaskException, {fn!.NameConst}); {RaiseJump} }} ";
    code.Append(refuse);
    if (!waited) {
      code.Append("})");
      return new Expr(code.ToString(), "void");
    }
    if (TaskCallLentResult(f)) {
      Warn($"The variable {LentResultName(f)} is lent outside the task, it can be modified. Be sure you take your precautions to avoid conflicts.", call, 2);
    }
    string got = NewTemp();
    string error = NewTemp();
    string result = NewTemp();
    string waitCall = SilentCall(Serving($"df_task_wait_call({handle}, &{rec}->head, {timeoutCode}, &{got})"), "Exception",
      "TaskAlreadyTerminatedException", call);
    code.Append($"bool {got}; df_exception* {error} = {waitCall}; {CType(f.Ret)} {result} = {(IsValueLend(f.Ret) ? "NULL" : ZeroValue(f.Ret))}; ");
    if (nothing != null) {
      code.Append($"{nothing} = !{got}; ");
    }
    code.Append($"if ({got}) {{ {result} = {rec}->result; {rec}->taken = true; }} df_task_call_release(&{rec}->head); ");
    code.Append($"if ({error}) {{ df_err = {error}; df_err_thrown = false; goto {Handler}; }} ");
    if (IsValueLend(f.Ret)) {
      // &T of a value: the place in the task; a call that did not run gives the zero value (spec 13.1.1, 20.12)
      string value = f.Ret[1..];
      string zero = NewTemp();
      return new Expr($"(*{code}static DF_TLS {CType(value)} {zero}; {zero} = {ZeroValue(value)}; {result} != NULL ? {result} : &{zero}; }}))", value, false);
    }
    code.Append($"{result}; }})");
    if (TaskCallLentResult(f)) {
      return new Expr(code.ToString(), f.Ret, false);   // a borrow of what the task holds: the caller never frees it
    }
    return new Expr(code.ToString(), f.Ret, f.Ret == "string" || !TaskCallValueType(f.Ret) && !IsMulti(f.Ret));
  }

  // ---------------------------------------------------------------- return! and return? (spec 20.1)

  readonly Dictionary<string, string> threadDrops = new();

  // the function that ends a value of this type that nobody collected (return! / return? of a lambda)
  string ThreadDropOf(string type) {
    if (threadDrops.TryGetValue(type, out var known)) {
      return known;
    }
    string name = "df_thrdrop" + threadDrops.Count;
    threadDrops[type] = name;
    // the lambdas are compiled after TaskRunFunctions: written with the helpers of the lambdas, before every body
    lambdaHelpers.Append($"static void {name}(void* p) {{\n  {CType(type)}* v = ({CType(type)}*)p;\n  (void)v;\n  {DropValueCode(type, "(*v)")}\n}}\n");
    return name;
  }

  void EmitTaskGive(Node s, StringBuilder sb, Scope scope, string pad) {
    bool hold = s.Kind == "ReturnHold";
    string written = hold ? "return!" : "return?";
    // in taskRun() of a task, or in a plain function that runs as a thread (spec 20.1)
    bool inTask = currentFun is { Class: not null, IsStatic: false } && currentFun.Name.Split('.')[^1] == "taskRun";
    bool inThread = currentFun is { Struct: null } && !inTask;   // a function, a method or a lambda run by Thread() (spec 20.1)
    if (!inTask && !inThread) {
      throw Error($"{written} hands out a value of a task or a thread: it is written in taskRun() or in a function run by Thread() (spec 20.1)", s);
    }
    bool inLambda = currentFun is { LambdaCtx: not null };
    if (currentRet == "void") {
      throw Error($"{written} hands out a value, and this {(inTask ? "taskRun()" : "function")} returns nothing", s);
    }
    if (!TasksMode) {
      throw Error($"{written} needs import Root.Tasks (spec 20.0)", s);
    }
    string drop;
    string give;
    string mode = hold ? "DF_GIVE_HOLD" : "DF_GIVE_POST";
    if (inTask) {
      var cls = currentFun!.Class!;
      if (!taskClasses.Contains(cls)) {
        taskClasses.Add(cls);   // its drop function (TaskRunFunctions)
      }
      drop = TaskDropName(cls);
      give = $"df_task_give_from((void*)self, &{{0}}, {mode})";
    } else if (inLambda) {
      // a lambda: run as a thread when the thread was made from it; anywhere else the value is ended and the exception raised (spec 20.1)
      drop = ThreadDropOf(currentRet!);
      give = $"df_thread_give_from((void*){currentFun!.CName}, &{{0}}, {mode})";
    } else {
      drop = ThreadRecord(currentFun!, s) + "_drop";
      give = $"df_thread_give_from((void*){currentFun!.CName}, &{{0}}, {mode})";
    }
    givingValue = true;
    string value;
    try {
      value = ReturnValueCode(s, scope, sb, pad) ?? ZeroValue(currentRet!);
    } finally {
      givingValue = false;
    }
    string held = NewTemp();
    sb.Append($"{pad}{{\n{pad}  {CType(currentRet!)} {held} = {value};\n");
    sb.Append($"{pad}  if (!{string.Format(give, held)}) {{\n");
    sb.Append($"{pad}    {drop}(&{held});\n");
    sb.Append($"{pad}    df_raise(&df_desc_ReturnProvidedOutsideATaskException, {fn!.NameConst});\n{pad}    {RaiseJump}\n{pad}  }}\n{pad}}}\n");
    sb.Append($"{pad}if (df_pending) {RaiseJump}\n");   // a return is a checkpoint: the cancel signal (spec 20.10)
  }

}
