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
  public fun ->Task start(lapse timeout = null) {
    i32 started = Root.Tasks.Native.Start(handle, timeout)
    if started == 1 then throw TaskAlreadyStartedException("The task was already started")
    if started == 2 then throw NoMoreResourcesException("The system cannot start a new thread")
    return Task(Root.Tasks.Native.Retain(handle))
  }
  public fun RunStatus get_status() { (RunStatus)Root.Tasks.Native.Status(handle) }
  public fun RunKind get_kind() { RunKind.Task }
  public fun string get_name() { Root.Tasks.Native.Name(handle) }
  public fun bool get_linked() { Root.Tasks.Native.Linked(handle) }
} atEnd {
  Root.Tasks.Native.Release(handle)
}
""";

  // the names of Root.Tasks (spec 16.1): bare with import Root.Tasks
  static readonly string[] TasksLibraryNames = { "Task", "Thread", "RunStatus", "RunKind" };

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
    Node? timeout = null;
    Node? linked = null;
    var ctorArgs = Synthetic("Args", n);
    foreach (var arg in args.Skip(1)) {
      if (arg is { Kind: "Named", Text: "timeout" }) {
        timeout = arg.Children[0];   // the named arguments of Task() are its own, never of the constructor of the class (spec 20.1)
      } else if (arg is { Kind: "Named", Text: "linked" }) {
        linked = arg.Children[0];
      } else {
        ctorArgs.Add(arg);
      }
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
    return ExprOf(made, scope);
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
    if (TaskClassOfNode(node, scope) is ClassInfo made) {
      return new TaskHandle(made, TaskRunOf(made)!.Ret);
    }
    if (ThreadResultOfNode(node, scope) is string threadResult) {
      return new TaskHandle(null, threadResult);   // Thread(runThis, ...): what the function gives
    }
    if (node.Kind == "Name" && scope.Find(node.Text!) is VarInfo variable && taskHandles.TryGetValue(variable, out var known)) {
      return known;
    }
    return null;
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
      if (init != null) {
        CheckTaskHandleValue(wanted, given, init);
      }
      taskHandles[variable] = wanted;
      return;
    }
    if (given is TaskHandle known) {
      taskHandles[variable] = known;
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
      }
    }
  }

  // f(t) with a parameter Task<...>: the argument must fit (spec 20.9)
  void CheckTaskArgument(FunSig f, ParamSig p, Node arg, Scope scope) {
    if (TasksMode && p.Type is "Task" or "Thread" && taskParamTypes.TryGetValue((f.Node, p.Name), out var typeNode) && taskTypeSpecs.TryGetValue(typeNode, out var spec)) {
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

  static bool HasCheckpoint(Node node) =>
    node.Kind is "Wait" or "Return" or "ReturnHold" or "ReturnPost" or "Enforce" || node.Kind is not ("Lambda" or "Fun") && node.Children.Any(HasCheckpoint);
  // ---------------------------------------------------------------- wait (spec 20.2)

  // enforce wait t timeout 1000 default { 123 } (spec 20.2, 7.2.6): the default is the value when the wait gives nothing (a timeout, a
  // task that ended or never started). Lowered before the other enforce expressions: the wait keeps its default as WaitDefault
  void LowerWaitDefaults(Node node) {
    for (int index = 0; index < node.Children.Count; index++) {
      var child = node.Children[index];
      if (child is { Kind: "EnforceExpr" } && child.Children.Count > 0 && child.Children[0].Kind == "Wait") {
        var clauses = child.Children.Skip(1).ToList();
        if (clauses.Count != 1 || clauses[0].Kind != "EnforceDefault") {
          throw Error("enforce wait takes one clause, default { value }: the value when the wait gives nothing (spec 20.2)", child);
        }
        var block = clauses[0].Children.First(c => c.Kind == "Block");
        if (block.Children is not [{ Kind: "ExprStmt" } single]) {
          throw Error("the default of enforce wait is one value: default { 123 } (spec 20.2)", block);
        }
        var waited = child.Children[0];
        waited.Add(Synthetic("WaitDefault", block).Add(single.Children[0]));
        node.Children[index] = waited;
        child = waited;
      }
      LowerWaitDefaults(child);
    }
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
      // wait t.getCount() timeout 500: a call through the handle that gives a value (spec 20.1)
      string callTimeout = timeoutNode == null ? "0" : Convert(ExprOf(timeoutNode, scope, "lapse"), "lapse", timeoutNode);
      return TaskMethodCall(target, waitedCall.Receiver, waitedCall.Cls, true, callTimeout, scope);
    }
    var waited = ExprOf(target, scope);
    if (IsNumeric(waited.Type) || waited.Type == "lapse") {
      if (timeoutNode != null) {
        throw Error("wait 250 waits a time: it has no timeout (spec 20.8)", n);
      }
      string lapse = Convert(waited, "lapse", target);
      if (InTaskRunBody) {
        return new Expr(CheckRaise($"df_task_pause({lapse}, (void*)self)", "void"), "void");   // the calls through the handle are served meanwhile (spec 20.8)
      }
      return new Expr(TasksMode ? $"df_task_pause({lapse}, NULL)" : $"df_time_sleep({lapse})", "void");   // wait 250, wait lapse(10) (spec 20.8)
    }
    if (waited.Type is not ("Task" or "Thread")) {
      throw Error($"wait takes a task, a thread or a time, not {DisplayName(waited.Type)} (spec 20.2)", target);
    }
    string ret = TaskHandleOf(target, scope)?.Ret ?? "void";
    string timeoutCode = timeoutNode == null ? "0" : Convert(ExprOf(timeoutNode, scope, "lapse"), "lapse", timeoutNode);
    // the handle: a Task made here lives until the end of the statement
    string handle = $"({TempObject(waited).Code})->m_handle";
    string slot = NewTemp();
    string error = NewTemp();
    string call = $"df_task_wait({handle}, {(ret == "void" ? "NULL, 0" : $"&{slot}, sizeof({slot})")}, {timeoutCode})";
    string jump = $"if ({error}) {{ df_err = {error}; df_err_thrown = false; goto {Handler}; }}";
    bool fresh = ret == "string" || IsClass(ret) || IsArr(ret);
    if (n.Children.FirstOrDefault(c => c.Kind == "WaitDefault") is Node defaultNode) {
      // enforce wait ... default { v }: v when the wait gives nothing
      if (ret == "void") {
        throw Error("enforce wait gives a value: this task gives none", n);
      }
      var fallback = ExprOf(defaultNode.Children[0], scope, ret);
      string note = NewTemp();
      return new Expr($"({{ {CType(ret)} {slot}; df_exception* {error} = {call}; const df_descriptor* {note} = df_silent_desc; df_silent_desc = NULL; "
        + $"{jump} if ({note}) {{ {slot} = {Convert(fallback, ret, defaultNode)}; }} {slot}; }})", ret, fresh);
    }
    string waitCall = SilentCall(call, "Exception", "WaitTimeoutException,TaskAlreadyTerminatedException,TaskWasNotStartedException", n);
    if (ret == "void") {
      return new Expr($"({{ df_exception* {error} = {waitCall}; {jump} }})", "void");
    }
    return new Expr($"({{ {CType(ret)} {slot}; df_exception* {error} = {waitCall}; {jump} {slot}; }})", ret, fresh);
  }

  // ---------------------------------------------------------------- calls through the handle (spec 20.1)

  // the code is the body of a taskRun() (not a lambda in it): its checkpoints serve the calls through the handle (spec 20.8)
  bool InTaskRunBody => TasksMode && currentFun is { Class: not null, IsStatic: false, LambdaCtx: null } run
    && run.Name.Split('.')[^1] == "taskRun" && run.Params.Count == 0;

  static bool ContainsWait(Node node) =>
    node.Kind == "Wait" || node.Kind is not ("Lambda" or "Fun") && node.Children.Any(ContainsWait);

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
  static readonly HashSet<string> TaskOwnMembers = new() { "start", "status", "kind", "name", "linked", "get_status", "get_kind", "get_name", "get_linked" };

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
    return cls == null ? null : (receiver, cls);
  }

  readonly Dictionary<FunSig, string> taskCallRecords = new();   // a method called through the handle -> its record (df_tcr_N)
  readonly StringBuilder taskCallCode = new();                   // the invoke and end functions of the records (TaskRunFunctions)

  bool TaskCallValueType(string type) =>
    type == "string" || IsNumeric(type) || type is "bool" or "char" or "lapse" or "date" or "time" or "clock" || IsEnum(type)
    || IsStruct(type) && !StructHoldsObjects(type);

  // the record of a method called through the handle: its arguments and its result, and the functions that run and end it
  string TaskCallRecord(FunSig f, Node at) {
    if (taskCallRecords.TryGetValue(f, out var known)) {
      return known;
    }
    foreach (var p in f.Params) {
      if (!TaskCallValueType(p.Type) || p.Ref == 2 || p.IsGift || p.IsVariadic) {
        throw Error($"'{p.Name}' of {f.Name.Split('.')[^1]}: a call through the handle passes numbers, strings, enums and structs of them for now (spec 20.1)", at);
      }
    }
    if (f.Ret != "void" && (!TaskCallValueType(f.Ret) || IsMulti(f.Ret))) {
      throw Error($"{f.Name.Split('.')[^1]} gives {DisplayName(f.Ret)}: a call through the handle gives back numbers, strings, enums and structs of them for now (spec 20.1)", at);
    }
    string name = "df_tcr" + taskCallRecords.Count;
    taskCallRecords[f] = name;
    var fields = new StringBuilder("typedef struct {\n  df_task_call head;\n");
    for (int index = 0; index < f.Params.Count; index++) {
      fields.Append($"  {CType(f.Params[index].Type)} a{index};\n");
    }
    if (f.Ret != "void") {
      fields.Append($"  {CType(f.Ret)} result;\n  bool taken;\n");
    }
    fields.Append($"}} {name};\n\n");
    typeDefs.Append(fields);
    // invoke: the method on the object of the task, with the arguments of the record
    string args = string.Concat(Enumerable.Range(0, f.Params.Count).Select(index => IsByPointer(f.Params[index]) ? $", &r->a{index}" : $", r->a{index}"));
    string call = $"{f.CName}(({CType(f.Class!.Name)})obj{args})";
    var invoke = new StringBuilder($"static void {name}_invoke(void* obj, df_task_call* c) {{\n  {name}* r = ({name}*)c;\n");
    if (PairReturn(f)) {
      invoke.Append(f.Ret == "void" ? $"  r->head.error = {call};\n" : $"  {RetCType(f)} res = {call};\n  r->head.error = res.err;\n  r->result = res.value;\n");
    } else {
      invoke.Append(f.Ret == "void" ? $"  {call};\n" : $"  r->result = {call};\n");
      invoke.Append("  if (df_pending) {\n    r->head.error = df_pending;\n    df_pending = NULL;\n  }\n");
    }
    invoke.Append("}\n");
    var end = new StringBuilder($"static void {name}_end(df_task_call* c) {{\n  {name}* r = ({name}*)c;\n  (void)r;\n");
    for (int index = 0; index < f.Params.Count; index++) {
      if (f.Params[index].Type == "string") {
        end.Append($"  df_str_free(r->a{index});\n");
      }
    }
    if (f.Ret == "string") {
      end.Append("  if (!r->taken) {\n    df_str_free(r->result);\n  }\n");
    }
    end.Append("}\n");
    taskCallCode.Append(invoke).Append(end);
    return name;
  }

  // t.method(args) through the handle (spec 20.1): posted when the method returns nothing; waited for with wait t.method() timeout n
  Expr TaskMethodCall(Node call, Node receiverNode, ClassInfo cls, bool waited, string timeoutCode, Scope scope) {
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
    for (int index = 0; index < f.Params.Count; index++) {
      var p = f.Params[index];
      Node? argNode = index < positional.Count ? positional[index] : named.GetValueOrDefault(p.Name);
      string value;
      if (argNode == null) {
        if (p.Default == null) {
          throw Error($"missing argument '{p.Name}' of {name}", call);
        }
        var fallback = ExprOf(p.Default, scope, p.Type);
        value = Convert(fallback, p.Type, call);
        if (p.Type == "string") {
          value = $"df_str_dup({value})";
        }
      } else {
        var given = ExprOf(argNode, scope, p.Type);
        value = Convert(given, p.Type, argNode);
        if (p.Type == "string" && !given.Fresh) {
          value = $"df_str_dup({value})";   // the record owns its strings: the caller goes on (spec 20.12)
        }
      }
      code.Append($"{rec}->a{index} = {value}; ");
    }
    string refuse = $"if (!df_task_post_call({handle}, &{rec}->head)) {{ df_task_call_release(&{rec}->head); {(waited ? $"df_task_call_release(&{rec}->head); " : "")}"
      + $"df_raise(&df_desc_CalledANotRunningTaskException, {fn!.NameConst}); {RaiseJump} }} ";
    code.Append(refuse);
    if (!waited) {
      code.Append("})");
      return new Expr(code.ToString(), "void");
    }
    string got = NewTemp();
    string error = NewTemp();
    string result = NewTemp();
    string waitCall = SilentCall($"df_task_wait_call({handle}, &{rec}->head, {timeoutCode}, &{got})", "Exception",
      "WaitTimeoutException,TaskAlreadyTerminatedException", call);
    code.Append($"bool {got}; df_exception* {error} = {waitCall}; {CType(f.Ret)} {result} = {ZeroValue(f.Ret)}; ");
    code.Append($"if ({got}) {{ {result} = {rec}->result; {rec}->taken = true; }} df_task_call_release(&{rec}->head); ");
    code.Append($"if ({error}) {{ df_err = {error}; df_err_thrown = false; goto {Handler}; }} {result}; }})");
    return new Expr(code.ToString(), f.Ret, f.Ret == "string");
  }

  // ---------------------------------------------------------------- return! and return? (spec 20.1)

  void EmitTaskGive(Node s, StringBuilder sb, Scope scope, string pad) {
    bool hold = s.Kind == "ReturnHold";
    string written = hold ? "return!" : "return?";
    // in taskRun() of a task, or in a plain function that runs as a thread (spec 20.1)
    bool inTask = currentFun is { Class: not null, IsStatic: false } && currentFun.Name.Split('.')[^1] == "taskRun";
    bool inThread = currentFun is { Class: null, Struct: null, LambdaCtx: null };
    if (!inTask && !inThread) {
      throw Error($"{written} hands out a value of a task or a thread: it is written in taskRun() or in a function run by Thread() (spec 20.1)", s);
    }
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
    } else {
      drop = ThreadRecord(currentFun!, s) + "_drop";
      give = $"df_thread_give_from((void*){currentFun!.CName}, &{{0}}, {mode})";
    }
    string value = ReturnValueCode(s, scope, sb, pad) ?? ZeroValue(currentRet!);
    string held = NewTemp();
    sb.Append($"{pad}{{\n{pad}  {CType(currentRet!)} {held} = {value};\n");
    sb.Append($"{pad}  if (!{string.Format(give, held)}) {{\n");
    sb.Append($"{pad}    {drop}(&{held});\n");
    sb.Append($"{pad}    df_raise(&df_desc_ReturnOutsideATaskException, {fn!.NameConst});\n{pad}    {RaiseJump}\n{pad}  }}\n{pad}}}\n");
  }

}
