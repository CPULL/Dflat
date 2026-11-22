using System.Text;
using dfparser;

namespace dfcompiler;

// Task handles, the leftovers of spec 20.9 and 20.2: Task<...> fields and function results, t = ... checked against what t says, the type
// switch on a Task, and a wait with no timeout anywhere that the compiler can see.
public sealed partial class CodeGen {
  readonly HashSet<VarInfo> typedTaskVars = new();        // Task<...> t: what it says is checked on every t = ...
  readonly HashSet<VarInfo> noTimeoutVars = new();        // made on its line with no timeout (Task, Thread, Signal, Signal.group)

  // the Task<...> written in the declaration of a field (a field, or a primary parameter used as a field)
  TaskTypeSpec? FieldTaskSpec(FieldInfo field) =>
    field.Type is "Task" or "Thread" ? FindTaskSpec(field.At) : null;

  TaskTypeSpec? FindTaskSpec(Node node) {
    foreach (var child in node.Children) {
      if (taskTypeSpecs.TryGetValue(child, out var spec)) {
        return spec;
      }
      if (child.Kind is "Type" or "ResolvedType" or "Nullable" && FindTaskSpec(child) is TaskTypeSpec inner) {
        return inner;
      }
    }
    return null;
  }

  // fun Task<MyTask> make(): what the result says
  TaskTypeSpec? ResultTaskSpec(FunSig f) =>
    f.Ret is "Task" or "Thread" && f.Node.Children.FirstOrDefault(child => child.Kind == "Returns") is { Children.Count: 1 } rets
      && taskTypeSpecs.TryGetValue(rets.Children[0], out var spec) ? spec : null;

  // a field read by its name (inside the class) or through an object, a call of a function or a method: what its Task<...> says
  TaskHandle? TaskHandleOfMember(Node node, Scope scope) {
    if (node.Kind == "Name" && scope.Find(node.Text!) == null && currentClass != null && FindField(currentClass, node.Text!) is FieldInfo own
        && FieldTaskSpec(own) is TaskTypeSpec ownSpec) {
      return ResolveTaskSpec(ownSpec);
    }
    if (node.Kind == "Member" && node.Children.Count == 1 && node.Children[0].Kind is "Name" or "This" or "Member") {
      if (!StartsFromValue(node.Children[0], scope)) {
        return null;   // a class or a namespace: Root.Tasks.Native.Start(...)
      }
      var receiver = ExprOf(node.Children[0], scope);
      if (IsClass(receiver.Type) && classes.TryGetValue(receiver.Type, out var cls) && FindField(cls, node.Text!) is FieldInfo field
          && FieldTaskSpec(field) is TaskTypeSpec fieldSpec) {
        return ResolveTaskSpec(fieldSpec);
      }
      return null;
    }
    if (node.Kind == "Call" && node.Children[0] is { Kind: "Name" } callee && scope.Find(callee.Text!) == null && funs.TryGetValue(callee.Text!, out var named)) {
      var specs = named.Where(f => f.Class == null).Select(ResultTaskSpec).ToList();
      if (specs.Count > 0 && specs.All(spec => spec != null)) {
        var handles = specs.Select(spec => ResolveTaskSpec(spec!)).Distinct().ToList();
        return handles.Count == 1 ? handles[0] : null;
      }
      return null;
    }
    if (node.Kind == "Call" && node.Children[0] is { Kind: "Member" } method && method.Children.Count == 1 && StartsFromValue(method.Children[0], scope)) {
      var receiver = ExprOf(method.Children[0], scope);
      if (IsClass(receiver.Type) && classes.TryGetValue(receiver.Type, out var cls)) {
        var specs = LookupMethods(cls, method.Text!).Select(ResultTaskSpec).ToList();
        if (specs.Count > 0 && specs.All(spec => spec != null)) {
          var handles = specs.Select(spec => ResolveTaskSpec(spec!)).Distinct().ToList();
          return handles.Count == 1 ? handles[0] : null;
        }
      }
    }
    return null;
  }

  // a.b.c starts from a variable, this, a field or a global (not a class or a namespace)
  bool StartsFromValue(Node node, Scope scope) {
    while (node.Kind == "Member" && node.Children.Count == 1) {
      node = node.Children[0];
    }
    return node.Kind == "This" || node.Kind == "Name" && (scope.Find(node.Text!) != null || globalConsts.ContainsKey(node.Text!) || globals.ContainsKey(node.Text!)
      || currentClass != null && FindField(currentClass, node.Text!) != null);
  }

  // t = value: a Task<...> variable or field checks the value; a variable that knew its task from its line learns it again, or forgets
  void NoteTaskAssign(Node s, Scope scope) {
    if (!TasksMode || s.Kind != "Assign" || s.Text != "=") {
      return;
    }
    var target = s.Children[0];
    var value = s.Children[1];
    if (target.Kind == "Name" && scope.Find(target.Text!) is VarInfo variable && variable.Type is "Task" or "Thread") {
      noTimeoutVars.Remove(variable);
      if (typedTaskVars.Contains(variable)) {
        if (value.Kind != "Null") {
          CheckTaskHandleValue(taskHandles[variable], TaskHandleOf(value, scope), value);
        }
        return;
      }
      if (TaskHandleOf(value, scope) is TaskHandle known) {
        taskHandles[variable] = known;
      } else {
        taskHandles.Remove(variable);
      }
      return;
    }
    if (target.Kind is "Name" or "Member" && value.Kind != "Null" && TaskHandleOfMember(target, scope) is TaskHandle wanted) {
      CheckTaskHandleValue(wanted, TaskHandleOf(value, scope), value);   // a Task<...> field
    }
  }

  // t is ? { Task<MyTask> a: ..., Task<*, i32> b: ..., default: ... } (spec 20.9): chosen at run time by the class and the results
  Expr TaskTypeSwitch(Node n, Expr subject, Scope scope, string? expected) {
    string held = NewTemp();
    string result = NewTemp();
    string? type = expected;
    var arms = new List<(string Condition, string Code)>();
    string? fallback = null;
    foreach (var arm in n.Children.Skip(1)) {
      var labels = arm.Children[0].Children;
      bool isDefault = labels.Any(label => label.Kind == "Default");
      if (isDefault && labels.Count != 1) {
        throw Error("default stands alone in its case", arm);
      }
      if (!isDefault && (labels.Count != 1 || !taskTypeSpecs.TryGetValue(labels[0], out _))) {
        throw Error($"a case of the type switch of a {subject.Type} is one {subject.Type}<...>: Task<MyTask> a: ... (spec 20.9)", arm);
      }
      if (isDefault && arm.Text != null) {
        throw Error($"'{arm.Text}': default binds no name", arm);
      }
      var armScope = scope;
      string bind = "";
      string condition = "true";
      if (!isDefault) {
        var spec = taskTypeSpecs[labels[0]];
        var handle = ResolveTaskSpec(spec);
        string cls = handle.Cls == null ? "NULL" : $"&{handle.Cls.Descriptor}";
        string results = spec.Results.Count == 0 ? "NULL" : $"\"{TaskResultText(handle.Ret)}\"";
        condition = $"{held} != NULL && df_task_is({held}->m_handle, {cls}, {results})";
        if (arm.Text != null) {
          CheckNotShadowing(arm.Text, scope, arm);
          armScope = new Scope(scope);
          string cname = $"v_{arm.Text}_{++tempCounter}";
          var bound = new VarInfo(subject.Type, cname, false);
          armScope.Vars[arm.Text] = bound;
          taskHandles[bound] = handle;
          bind = $"{CType(subject.Type)} {cname} = {held}; ";
        }
      }
      var value = ExprOf(arm.Children[1], armScope, type);
      type ??= value.Type;
      string armCode = type == "void" ? $"{value.Code};" : $"{result} = {Convert(value, type, arm)};";
      if (type == "string" && !value.Fresh) {
        armCode = $"{result} = df_str_dup({Convert(value, type, arm)});";
      }
      if (isDefault) {
        fallback = $"{{ {armCode} }}";
      } else {
        arms.Add((condition, $"{{ {bind}{armCode} }}"));
      }
    }
    if (fallback == null) {
      throw Error($"the type switch of a {subject.Type} needs a 'default': a {subject.Type} can be of any class (spec 20.9)", n);
    }
    var code = new StringBuilder($"({{ {CType(subject.Type)} {held} = {subject.Code}; ");
    if (type != "void") {
      code.Append($"{CType(type!)} {result} = {ZeroValue(type!)}; ");
    }
    foreach (var (condition, armCode) in arms) {
      code.Append($"if ({condition}) {armCode} else ");
    }
    code.Append(fallback);
    code.Append(type == "void" ? " })" : $" {result}; }})");
    return new Expr(code.ToString(), type!, type == "string");
  }

  // Task(MyTask, ...), Thread(f, ...), Signal(...), Signal.group(...), and .start(...) on them: true when no timeout is written in any
  static bool MadeWithoutTimeout(Node node) {
    if (node.Kind == "Paren") {
      return MadeWithoutTimeout(node.Children[0]);
    }
    if (node.Kind != "Call") {
      return false;
    }
    bool hasTimeout = node.Children[1].Children.Any(arg => arg is { Kind: "Named", Text: "timeout" });
    if (hasTimeout) {
      return false;
    }
    if (node.Children[0] is { Kind: "Member", Text: "start" } start) {
      return start.Children[0].Kind == "Call" && MadeWithoutTimeout(start.Children[0]);
    }
    return node.Children[0] is { Kind: "Name", Text: "Task" or "Thread" or "Signal" }
      || node.Children[0] is { Kind: "Member", Text: "group" } group && group.Children[0] is { Kind: "Name", Text: "Signal" };
  }

  // x.start(timeout: ...) or x = ... anywhere in the code around: the timeout may come from there
  static bool GetsTimeoutLater(Node code, string name) {
    if (code.Kind == "Call" && code.Children[0] is { Kind: "Member", Text: "start" } start && start.Children[0] is { Kind: "Name" } held && held.Text == name
        && code.Children[1].Children.Any(arg => arg is { Kind: "Named", Text: "timeout" } || arg.Kind != "Named")) {
      return true;
    }
    if (code.Kind == "Assign" && code.Children[0] is { Kind: "Name" } assigned && assigned.Text == name) {
      return true;
    }
    return code.Children.Any(child => GetsTimeoutLater(child, name));
  }

  // after a declaration: a Task, Thread, Signal or SignalGroup made on its line with no timeout
  void NoteNoTimeout(Node decl, Scope scope) {
    if (!TasksMode || !scope.Vars.TryGetValue(decl.Text!, out var variable) || variable.Type is not ("Task" or "Thread" or "Signal" or "SignalGroup")) {
      return;
    }
    if (decl.Children.FirstOrDefault(c => c.Kind == "Init")?.Children[0] is Node init && MadeWithoutTimeout(init)) {
      noTimeoutVars.Add(variable);
    }
  }

  // wait x with no timeout: an error when nothing gives one, where the compiler sees it (spec 20.1, 20.2)
  void CheckWaitHasTimeout(Node target, Scope scope, Node at) {
    bool none = MadeWithoutTimeout(target)
      || target.Kind == "Name" && scope.Find(target.Text!) is VarInfo variable && noTimeoutVars.Contains(variable)
         && !GetsTimeoutLater(currentFun?.Node ?? ProgramNode!, target.Text!);
    if (none) {
      throw Error("this wait has no timeout: give one to the wait, to start or to the constructor (spec 20.1, 20.2)", at);
    }
  }
}
