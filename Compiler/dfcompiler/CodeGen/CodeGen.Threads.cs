using System.Text;
using dfparser;

namespace dfcompiler;

// Threads (spec 20.1): a function that runs on its own OS thread. Thread(runThis, 567, timeout: 5000) takes the arguments now (in a record
// of the call: strings copied, the rest as they are: a thread shares the memory of its task, spec 20.12) and start() runs the function.
// In the runtime a thread is a task (RuntimeTasks) whose object is the record: wait, the three forms of return, the timeouts and the
// statuses are the ones of the tasks. The class Thread is written in Dflat (ThreadPrelude), as Task.
public sealed partial class CodeGen {
  const string ThreadPrelude = """
public class Thread(u64 handle) {
  public fun ->Thread start(lapse timeout = null) {
    i32 started = Root.Tasks.Native.Start(handle, timeout)
    if started == 1 then throw TaskAlreadyStartedException("The thread was already started")
    if started == 2 then throw NoMoreResourcesException("The system cannot start a new thread")
    return Thread(Root.Tasks.Native.Retain(handle))
  }
  public fun RunStatus get_status() { (RunStatus)Root.Tasks.Native.Status(handle) }
  public fun RunKind get_kind() { RunKind.Thread }
  public fun string get_name() { Root.Tasks.Native.Name(handle) }
} atEnd {
  Root.Tasks.Native.Release(handle)
}
""";

  readonly Dictionary<FunSig, string> threadRecords = new();   // a function run as a thread -> its record (df_thrN)
  readonly StringBuilder threadCode = new();                   // the run, end and drop functions of the records (TaskRunFunctions)
  readonly StringBuilder threadProtos = new();

  // the function named in Thread(f, args...): the top-level functions of that name, chosen by the arguments
  FunSig? ThreadFunOf(Node call, Scope scope) {
    if (!TasksMode || call.Kind != "Call" || call.Children[0] is not { Kind: "Name", Text: "Thread" } || scope.Find("Thread") != null
        || call.Children[1].Children is not [{ Kind: "Name" } first, ..] || scope.Find(first.Text!) != null
        || !funs.TryGetValue(first.Text!, out var named)) {
      return null;
    }
    var candidates = named.Where(f => f.Class == null && f.Struct == null && f.LambdaCtx == null).ToList();
    if (candidates.Count == 0) {
      return null;
    }
    var args = call.Children[1].Children.Skip(1).Where(arg => arg is not { Kind: "Named", Text: "timeout" }).ToList();
    return ResolveOverload(candidates, args, scope, call);
  }

  // the record of a function run as a thread: its arguments; the functions that run it, end it, and end a result nobody collected
  string ThreadRecord(FunSig f, Node at) {
    if (threadRecords.TryGetValue(f, out var known)) {
      return known;
    }
    foreach (var p in f.Params) {
      if (p.Ref == 2 || p.IsVariadic || IsCountedType(p.Type) || IsWeakType(p.Type)) {
        throw Error($"'{p.Name}' of {f.Name}: a thread takes plain and given (->) parameters for now (spec 20.1)", at);
      }
    }
    string name = "df_thr" + threadRecords.Count;
    threadRecords[f] = name;
    var fields = new StringBuilder("typedef struct {\n  char none;\n");
    for (int index = 0; index < f.Params.Count; index++) {
      fields.Append($"  {CType(f.Params[index].Type)} a{index};\n");
    }
    fields.Append($"}} {name};\n\n");
    typeDefs.Append(fields);
    threadProtos.Append($"static df_exception* {name}_run(void* p);\nstatic void {name}_end(void* p);\nstatic void {name}_drop(void* p);\n");
    string args = string.Join(", ", Enumerable.Range(0, f.Params.Count).Select(index => IsByPointer(f.Params[index]) ? $"&r->a{index}" : $"r->a{index}"));
    string call = $"{f.CName}({args})";
    bool hasValue = f.Ret != "void";
    var run = new StringBuilder($"static df_exception* {name}_run(void* p) {{\n  {name}* r = ({name}*)p;\n  (void)r;\n");
    if (PairReturn(f)) {
      run.Append(hasValue
        ? $"  {RetCType(f)} result = {call};\n  df_exception* err = result.err;\n  {CType(f.Ret)} value = result.value;\n"
        : $"  df_exception* err = {call};\n");
    } else {
      run.Append(hasValue ? $"  {CType(f.Ret)} value = {call};\n" : $"  {call};\n");
      run.Append("  df_exception* err = df_pending;\n  df_pending = NULL;\n");
    }
    run.Append("  if (err && err->descriptor->silent) {\n    df_exc_release(err);\n    err = NULL;\n  }\n");
    if (hasValue) {
      run.Append("  if (err == NULL) {\n    df_task_give(df_task_current, &value, DF_GIVE_RETURN);\n  }\n");
    }
    run.Append("  return err;\n}\n");
    // the record owns the strings it copied and what the function takes with ->
    var end = new StringBuilder($"static void {name}_end(void* p) {{\n  {name}* r = ({name}*)p;\n  (void)r;\n");
    for (int index = 0; index < f.Params.Count; index++) {
      var p = f.Params[index];
      if (p.Type == "string" && !p.IsGift) {
        end.Append($"  df_str_free(r->a{index});\n");
      }
    }
    end.Append("  free(r);\n}\n");
    var drop = new StringBuilder($"static void {name}_drop(void* p) {{\n");
    drop.Append(hasValue ? $"  {CType(f.Ret)}* v = ({CType(f.Ret)}*)p;\n  {DropValueCode(f.Ret, "(*v)")}\n" : "  (void)p;\n");
    drop.Append("}\n");
    threadCode.Append(run).Append(end).Append(drop);
    return name;
  }

  // Thread(f, args..., timeout: t) (spec 20.1); null when the call is not one (the prelude makes Thread(handle))
  Expr? ThreadCreate(Node n, Scope scope) {
    if (!TasksMode || n.Children[0] is not { Kind: "Name", Text: "Thread" } || scope.Find("Thread") != null || !classes.ContainsKey("Thread")) {
      return null;
    }
    var args = n.Children[1].Children;
    if (args.Count > 0 && args[0].Kind == "CValue" || LibraryCode || currentClass?.Name == "Thread") {
      return null;
    }
    var f = ThreadFunOf(n, scope)
      ?? throw Error("Thread() takes a function first, then its arguments: Thread(runThis, 567) (spec 20.1)", n);
    string record = ThreadRecord(f, n);
    Node? timeout = null;
    var given = new List<Node>();
    foreach (var arg in args.Skip(1)) {
      if (arg is { Kind: "Named", Text: "timeout" }) {
        timeout = arg.Children[0];
      } else if (arg is { Kind: "Named", Text: "linked" }) {
        throw Error("a thread always belongs to the task that starts it: linked is for Task() (spec 20.1)", arg);
      } else {
        given.Add(arg);
      }
    }
    var named = given.Where(a => a.Kind == "Named").ToDictionary(a => a.Text!, a => a.Children[0]);
    var positional = given.Where(a => a.Kind != "Named").ToList();
    string rec = NewTemp();
    var code = new StringBuilder($"({{ {record}* {rec} = ({record}*)df_alloc(sizeof({record})); memset({rec}, 0, sizeof({record})); ");
    for (int index = 0; index < f.Params.Count; index++) {
      var p = f.Params[index];
      Node? argNode = index < positional.Count ? positional[index] : named.GetValueOrDefault(p.Name);
      Expr value;
      if (argNode == null) {
        value = ExprOf(p.Default ?? throw Error($"missing argument '{p.Name}' of {f.Name}", n), scope, p.Type);
        argNode = p.Default;
      } else {
        value = ExprOf(argNode, scope, p.Type);
      }
      string converted = Convert(value, p.Type, argNode);
      if (p.Type == "string" && !value.Fresh) {
        converted = $"df_str_dup({converted})";   // the record owns its strings: the creator goes on (spec 20.12)
      }
      code.Append($"{rec}->a{index} = {converted}; ");
    }
    string timeoutCode = timeout == null ? "0" : Convert(ExprOf(timeout, scope, "lapse"), "lapse", timeout);
    string size = f.Ret == "void" ? "0" : $"sizeof({CType(f.Ret)})";
    code.Append($"df_thread_new({rec}, {record}_end, {record}_run, (void*){f.CName}, \"{f.Name}\", \"{TaskResultText(f.Ret)}\", {timeoutCode}, {size}, {record}_drop); }})");
    var made = Synthetic("Call", n).Add(Synthetic("Name", n, "Thread"))
      .Add(Synthetic("Args", n).Add(Synthetic("CValue", n, code.ToString()).Add(Synthetic("Type", n, "u64"))));
    return ExprOf(made, scope);
  }

  // Thread(f, ...) or Thread(f, ...).start(): what the thread gives; null when it is not seen here
  string? ThreadResultOfNode(Node node, Scope scope) {
    if (node.Kind == "Paren") {
      return ThreadResultOfNode(node.Children[0], scope);
    }
    if (node.Kind == "Call" && node.Children[0] is { Kind: "Member", Text: "start" } start) {
      return ThreadResultOfNode(start.Children[0], scope);
    }
    return ThreadFunOf(node, scope)?.Ret;
  }
}
