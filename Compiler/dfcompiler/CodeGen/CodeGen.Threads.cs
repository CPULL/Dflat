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
    if started == 3 then throw ThreadStartedByAThreadException("A thread cannot start another thread")
    return Thread(Root.Tasks.Native.Retain(handle))
  }
  public fun RunStatus get_status() { (RunStatus)Root.Tasks.Native.Status(handle) }
  public fun RunKind get_kind() { RunKind.Thread }
  public fun string get_name() { Root.Tasks.Native.Name(handle) }
  unrestricted fun bool kill(lapse timeout) { Root.Tasks.Native.Kill(handle, timeout) }
} atEnd {
  Root.Tasks.Native.Release(handle)
}
""";

  readonly Dictionary<FunSig, string> threadRecords = new();   // a function run as a thread -> its record (df_thrN)
  readonly StringBuilder threadCode = new();                   // the run, end and drop functions of the records (TaskRunFunctions)
  readonly StringBuilder threadProtos = new();

  // what Thread(x, args...) runs (spec 20.1): a top-level function (or a static method) by its name, a method of an object (bare inside
  // its class: the object is this; obj.method), or a function value (a variable, a field, a lambda). Null when the call is not Thread()
  sealed record ThreadTarget(FunSig? Fun, string? Self, Expr? Value, List<ParamSig> Params, string Ret);

  readonly Dictionary<Node, ThreadTarget> threadTargets = new(ReferenceEqualityComparer.Instance);   // a lambda is compiled once

  ThreadTarget? ThreadTargetOf(Node call, Scope scope) {
    if (threadTargets.TryGetValue(call, out var seen)) {
      return seen;
    }
    var found = FindThreadTarget(call, scope);
    if (found != null) {
      threadTargets[call] = found;
    }
    return found;
  }

  ThreadTarget? FindThreadTarget(Node call, Scope scope) {
    if (!TasksMode || call.Kind != "Call" || call.Children[0] is not { Kind: "Name", Text: "Thread" } || scope.Find("Thread") != null
        || call.Children[1].Children.Count == 0 || call.Children[1].Children[0].Kind is "Named" or "CValue") {
      return null;
    }
    var first = call.Children[1].Children[0];
    var args = call.Children[1].Children.Skip(1).Where(arg => arg is not { Kind: "Named", Text: "timeout" }).ToList();
    // a top-level function by its name
    if (first.Kind == "Name" && scope.Find(first.Text!) == null && funs.TryGetValue(first.Text!, out var named)) {
      var candidates = named.Where(f => f.Class == null && f.Struct == null && f.LambdaCtx == null).ToList();
      if (candidates.Count > 0) {
        var f = ResolveOverload(candidates, args, scope, call);
        return new ThreadTarget(f, null, null, f.Params, f.Ret);
      }
    }
    // a method of this class by its bare name: the object is this (a thread of the task works on its object, spec 20.1)
    if (first.Kind == "Name" && scope.Find(first.Text!) == null && currentClass != null && FindField(currentClass, first.Text!) is null
        && LookupMethods(currentClass, first.Text!) is { Count: > 0 } own) {
      var f = ResolveOverload(own, args, scope, call);
      if (!f.IsStatic && scope.Find("this") == null) {
        throw Error($"'{first.Text}' is a method of {DisplayName(currentClass.Name)}: a static function has no object to run it on", first);
      }
      return new ThreadTarget(f, f.IsStatic ? null : "self", null, f.Params, f.Ret);
    }
    // obj.method, Class.method (static)
    if (first.Kind == "Member" && first.Children.Count == 1) {
      if (first.Children[0] is { Kind: "Name" } owner && scope.Find(owner.Text!) == null && classes.TryGetValue(owner.Text!, out var staticOwner)
          && LookupMethods(staticOwner, first.Text!).Where(f => f.IsStatic).ToList() is { Count: > 0 } statics) {
        var f = ResolveOverload(statics, args, scope, call);
        return new ThreadTarget(f, null, null, f.Params, f.Ret);
      }
      if (StartsFromValue(first.Children[0], scope)) {
        var receiver = ExprOf(first.Children[0], scope);
        if (IsClass(receiver.Type) && classes.TryGetValue(receiver.Type, out var cls) && FindField(cls, first.Text!) is null
            && LookupMethods(cls, first.Text!).Where(f => !f.IsStatic).ToList() is { Count: > 0 } methods) {
          var f = ResolveOverload(methods, args, scope, call);
          CheckAccess(f.Visibility, f.Class!, first.Text!, first);
          return new ThreadTarget(f, receiver.Code, null, f.Params, f.Ret);
        }
      }
    }
    // a function value
    var value = ExprOf(first, scope);
    if (IsFunType(value.Type)) {
      var (ret, types) = FunParts(value.Type);
      var ps = types.Select((type, index) => ParamFromFunType($"argument {index + 1}", type)).ToList();
      return new ThreadTarget(null, null, value, ps, ret);
    }
    return null;
  }

  // the record of what a thread runs: the object or the function value, the arguments; the functions that run it, end it, and end a
  // result nobody collected. One record per function (and per function type for function values)
  readonly Dictionary<string, string> threadValueRecords = new();

  string ThreadRecord(FunSig f, Node at) => ThreadRecordOf(new ThreadTarget(f, f.Class != null && !f.IsStatic ? "self" : null, null, f.Params, f.Ret), at);

  string ThreadRecordOf(ThreadTarget target, Node at) {
    if (target.Fun is FunSig known && threadRecords.TryGetValue(known, out var made)) {
      return made;
    }
    string? valueKey = target.Fun == null ? target.Value!.Value.Type : null;
    if (valueKey != null && threadValueRecords.TryGetValue(valueKey, out var madeValue)) {
      return madeValue;
    }
    string what = target.Fun?.Name ?? "this function";
    foreach (var p in target.Params) {
      if (p.IsVariadic || IsFunType(p.Type)) {
        throw Error($"'{p.Name}' of {what}: a thread does not take variadic or function parameters yet (spec 20.1)", at);
      }
    }
    string name = "df_thr" + (threadRecords.Count + threadValueRecords.Count);
    if (target.Fun != null) {
      threadRecords[target.Fun] = name;
    } else {
      threadValueRecords[valueKey!] = name;
    }
    var ps = target.Params;
    bool method = target.Fun is { Class: not null, IsStatic: false };
    var fields = new StringBuilder("typedef struct {\n  char none;\n");
    if (method) {
      fields.Append($"  {CType(target.Fun!.Class!.Name)} self;\n");   // the object the method runs on (shared, spec 20.1)
    }
    if (target.Fun == null) {
      fields.Append("  df_fn fn;\n");   // the function value, with a usage of its context
    }
    for (int index = 0; index < ps.Count; index++) {
      fields.Append(ps[index].Ref == 2 ? $"  {ParamCType(ps[index])} a{index};\n" : $"  {ThreadArgCType(ps[index])} a{index};\n");
    }
    fields.Append($"}} {name};\n\n");
    typeDefs.Append(fields);
    threadProtos.Append($"static df_exception* {name}_run(void* p);\nstatic void {name}_end(void* p);\nstatic void {name}_drop(void* p);\n");
    var run = new StringBuilder($"static df_exception* {name}_run(void* p) {{\n  {name}* r = ({name}*)p;\n  (void)r;\n");
    var args = new List<string>();
    for (int index = 0; index < ps.Count; index++) {
      var p = ps[index];
      if (ThreadArgMoves(p)) {
        // given to the function: the record keeps nothing to end (a usage, a weak reference, ->T)
        run.Append($"  {ThreadArgCType(p)} g{index} = r->a{index};\n  r->a{index} = {ThreadArgZero(p)};\n");
        args.Add($"g{index}");
      } else {
        args.Add(p.Ref == 2 ? $"r->a{index}" : IsByPointer(p) ? $"&r->a{index}" : $"r->a{index}");
      }
    }
    string ret = target.Ret;
    bool hasValue = ret != "void";
    if (target.Fun != null) {
      var f = target.Fun;
      string call = $"{f.CName}({string.Join(", ", (method ? new[] { "r->self" } : Array.Empty<string>()).Concat(args))})";
      if (PairReturn(f)) {
        run.Append(hasValue
          ? $"  {RetCType(f)} result = {call};\n  df_exception* err = result.err;\n  {CType(ret)} value = result.value;\n"
          : $"  df_exception* err = {call};\n");
      } else {
        run.Append(hasValue ? $"  {CType(ret)} value = {call};\n" : $"  {call};\n");
        run.Append("  df_exception* err = df_pending;\n  df_pending = NULL;\n");
      }
    } else {
      // a lambda returns its exception with its result (spec 13.3)
      string call = LambdaCall("r->fn", ret, ps.Select(ParamCType), args);
      run.Append(hasValue
        ? $"  {ResultPair(CType(ret))} result = {call};\n  df_exception* err = result.err;\n  {CType(ret)} value = result.value;\n"
        : $"  df_exception* err = {call};\n");
    }
    run.Append("  if (err && err->descriptor->silent) {\n    df_exc_release(err);\n    err = NULL;\n  }\n");
    if (hasValue) {
      run.Append("  if (err == NULL) {\n    df_task_give(df_task_current, &value, DF_GIVE_RETURN);\n  }\n");
    }
    run.Append("  return err;\n}\n");
    // the record owns the strings it copied, what was never given to the function, and the usage of the function value
    var end = new StringBuilder($"static void {name}_end(void* p) {{\n  {name}* r = ({name}*)p;\n  (void)r;\n");
    for (int index = 0; index < ps.Count; index++) {
      var p = ps[index];
      if (p.Type == "string" && !p.IsGift && p.Ref != 2) {
        end.Append($"  df_str_free(r->a{index});\n");
      } else if (ThreadArgMoves(p)) {
        end.Append($"  if (r->a{index}) {{\n    {ThreadArgEnd(p, $"r->a{index}")}\n  }}\n");
      }
    }
    if (target.Fun == null) {
      end.Append("  df_fn_release(r->fn);\n");
    }
    end.Append("  free(r);\n}\n");
    var drop = new StringBuilder($"static void {name}_drop(void* p) {{\n");
    drop.Append(hasValue ? $"  {CType(ret)}* v = ({CType(ret)}*)p;\n  {DropValueCode(ret, "(*v)")}\n" : "  (void)p;\n");
    drop.Append("}\n");
    threadCode.Append(run).Append(end).Append(drop);
    return name;
  }

  // a counted (@T), weak (%T) or given object (->T): the record holds it until the function takes it
  bool ThreadArgMoves(ParamSig p) =>
    p.Ref != 2 && (IsCountedType(p.Type) || IsWeakType(p.Type) || p.IsWeak || p.IsGift && (IsClass(p.Type) || IsArr(p.Type)));

  string ThreadArgCType(ParamSig p) => IsCountedType(p.Type) || IsWeakType(p.Type) || p.IsWeak ? "df_rc*" : CType(p.Type);

  string ThreadArgZero(ParamSig p) => IsCountedType(p.Type) || IsWeakType(p.Type) || p.IsWeak ? "NULL" : ZeroValue(p.Type);

  string ThreadArgEnd(ParamSig p, string place) =>
    IsWeakType(p.Type) || p.IsWeak ? $"df_rc_unweak({place});" : IsCountedType(p.Type) ? $"df_rc_release({place});" : DropValueCode(p.Type, place);

  // Thread(x, args..., timeout: t) (spec 20.1); null when the call is not one (the prelude makes Thread(handle))
  Expr? ThreadCreate(Node n, Scope scope) {
    if (!TasksMode || n.Children[0] is not { Kind: "Name", Text: "Thread" } || scope.Find("Thread") != null || !classes.ContainsKey("Thread")) {
      return null;
    }
    var args = n.Children[1].Children;
    if (args.Count > 0 && args[0].Kind == "CValue" || LibraryCode || currentClass?.Name == "Thread") {
      return null;
    }
    var target = ThreadTargetOf(n, scope)
      ?? throw Error("Thread() takes a function first (a function, a method or a function value), then its arguments: Thread(runThis, 567) (spec 20.1)", n);
    string record = ThreadRecordOf(target, n);
    if (target.Fun != null) {
      NoteThreadCreate(target.Fun, n);   // a thread written in a thread, and what a task reaches (CodeGen.TaskAccess.cs)
    }
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
    if (target.Fun == null && given.Any(arg => arg.Kind == "Named")) {
      throw Error("a function value takes its arguments by position", n);
    }
    var named = given.Where(a => a.Kind == "Named").ToDictionary(a => a.Text!, a => a.Children[0]);
    var positional = given.Where(a => a.Kind != "Named").ToList();
    var ps = target.Params;
    if (target.Fun == null && positional.Count != ps.Count) {
      throw Error($"this function takes {ps.Count} argument{(ps.Count == 1 ? "" : "s")}, not {positional.Count} ({FunDisplay(target.Value!.Value.Type)})", n);
    }
    string rec = NewTemp();
    var code = new StringBuilder($"({{ {record}* {rec} = ({record}*)df_alloc(sizeof({record})); memset({rec}, 0, sizeof({record})); ");
    if (target.Self != null) {
      code.Append($"{rec}->self = {target.Self}; ");
    }
    if (target.Fun == null) {
      code.Append($"{rec}->fn = df_fn_retain({target.Value!.Value.Code}); ");
    }
    for (int index = 0; index < ps.Count; index++) {
      var p = ps[index];
      Node? argNode = index < positional.Count ? positional[index] : named.GetValueOrDefault(p.Name);
      if (argNode == null && p.Default == null) {
        throw Error($"missing argument '{p.Name}' of {target.Fun?.Name ?? "the function"}", n);
      }
      string converted;
      if (p.Ref == 2 || ThreadArgMoves(p)) {
        // &x: the place of the caller (a thread shares the memory of its task, spec 20.1, 20.12); @x, %x, ->x: held by the record
        converted = ArgCode(p, argNode ?? p.Default!, scope, target.Fun, index);
      } else {
        var value = ExprOf(argNode ?? p.Default!, scope, p.Type);
        converted = Convert(value, p.Type, argNode ?? p.Default!);
        if (p.Type == "string" && !value.Fresh) {
          converted = $"df_str_dup({converted})";   // the record owns its strings: the creator goes on (spec 20.12)
        }
      }
      code.Append($"{rec}->a{index} = {converted}; ");
    }
    string timeoutCode = timeout == null ? "0" : Convert(ExprOf(timeout, scope, "lapse"), "lapse", timeout);
    string size = target.Ret == "void" ? "0" : $"sizeof({CType(target.Ret)})";
    string fnPointer = target.Fun != null ? $"(void*){target.Fun.CName}" : $"{rec}->fn.code";
    string shown = target.Fun?.Name ?? "lambda";
    code.Append($"df_thread_new({rec}, {record}_end, {record}_run, {fnPointer}, \"{shown}\", \"{TaskResultText(target.Ret)}\", {timeoutCode}, {size}, {record}_drop); }})");
    var made = Synthetic("Call", n).Add(Synthetic("Name", n, "Thread"))
      .Add(Synthetic("Args", n).Add(Synthetic("CValue", n, code.ToString()).Add(Synthetic("Type", n, "u64"))));
    return ExprOf(made, scope);
  }

  // the old name: the function of Thread(f, ...) when it is a function or a method
  FunSig? ThreadFunOf(Node call, Scope scope) => ThreadTargetOf(call, scope)?.Fun;

  // Thread(f, ...) or Thread(f, ...).start(): what the thread gives; null when it is not seen here
  string? ThreadResultOfNode(Node node, Scope scope) {
    if (node.Kind == "Paren") {
      return ThreadResultOfNode(node.Children[0], scope);
    }
    if (node.Kind == "Call" && node.Children[0] is { Kind: "Member", Text: "start" } start) {
      return ThreadResultOfNode(start.Children[0], scope);
    }
    return ThreadTargetOf(node, scope)?.Ret;
  }
}
