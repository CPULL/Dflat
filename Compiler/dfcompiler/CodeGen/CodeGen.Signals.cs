using dfparser;

namespace dfcompiler;

// Signals (spec 20.3, Root.Tasks): a bare go, identified by a name or by a number, with a mode set when it is made. The class Signal is
// written in Dflat (SignalsPrelude) over the natives of Root.Tasks.Native; the registry, the queues of the waiters and the counters are
// in the runtime (RuntimeTasks: df_signal). wait s [timeout t] is compiled here (SignalWait), called by TaskWait.
public sealed partial class CodeGen {
  const string SignalsPrelude = """
enum SignalMode { Broadcast, Exclusive, Single, Stable, ExclusiveSafe }
enum KnownSignals { Cancel: 666 }

public class Signal(u64 handle, lapse timeout, bool made) {
  public Signal(string name, lapse timeout = null) : Signal(SignalMode.Single, name, timeout) { }
  public Signal(uns number, lapse timeout = null) : Signal(SignalMode.Single, number, timeout) { }
  public Signal(SignalMode mode, string name, lapse timeout = null) : Signal(Root.Tasks.Native.SignalGet((i32)mode, false, 0, name, false), timeout, true) {
    Signal.check(handle)
  }
  public Signal(SignalMode mode, uns number, lapse timeout = null) : Signal(Root.Tasks.Native.SignalGet((i32)mode, true, (u64)number, "", false), timeout, true) {
    Signal.check(handle)
  }
  public Signal(KnownSignals known, lapse timeout = null) : Signal(Root.Tasks.Native.SignalGet((i32)SignalMode.ExclusiveSafe, true, (u64)known, "", true), timeout, true) { }

  static fun check(u64 made) {
    if made == 0 then throw SignalModeMismatchException("This signal exists with another mode")
    if made == 1 then throw ReservedSignalException("The signal numbers below 4096 are reserved: KnownSignals names them")
  }

  unrestricted fun send() { Root.Tasks.Native.SignalSend(handle) }
  public static fun send(string name) { Signal(name).send() }
  public static fun send(uns number) { Signal(number).send() }
  public static fun send(SignalMode mode, string name) { Signal(mode, name).send() }
  public static fun send(SignalMode mode, uns number) { Signal(mode, number).send() }

  public static fun Signal tryGet(SignalMode mode, string name) {
    u64 found = Root.Tasks.Native.SignalFind((i32)mode, false, 0, name)
    if found == 0 then return null
    return Signal(found, null, true)
  }
  public static fun Signal tryGet(SignalMode mode, uns number) {
    u64 found = Root.Tasks.Native.SignalFind((i32)mode, true, (u64)number, "")
    if found == 0 then return null
    return Signal(found, null, true)
  }

  unrestricted fun SignalMode get_mode() { (SignalMode)Root.Tasks.Native.SigMode(handle) }
  unrestricted fun string get_name() { Root.Tasks.Native.SigName(handle) }
  unrestricted fun uns? get_number() {
    if Root.Tasks.Native.SigByNumber(handle) then return (uns)Root.Tasks.Native.SigNumber(handle)
    return null
  }
}

public struct SignalType { uns? number, string name }

public class SignalGroup(->arr<Signal> members, lapse timeout) {
  unrestricted fun SignalType typeAt(i64 index) {
    if index < 0 then return SignalType()
    if members[index].number == null then return SignalType(name: members[index].name)
    return SignalType(number: members[index].number)
  }
}
""";

  static void AddSignalsStdlib(Action<string, StdFun> add) {
    var tasks = new[] { "tasks" };
    void Native(string name, string ret, string cname, params string[] ps) {
      add("Root.Tasks.Native." + name, new StdFun(ps, ret, cname, false, 0, "", null, tasks));
    }
    Native("SignalGet", "u64", "df_signal_get", "i32", "bool", "u64", "string", "bool");
    Native("SignalFind", "u64", "df_signal_find", "i32", "bool", "u64", "string");
    Native("SignalSend", "void", "df_signal_send", "u64");
    Native("SigMode", "i32", "df_signal_mode", "u64");
    Native("SigName", "string", "df_signal_name", "u64");
    Native("SigNumber", "u64", "df_signal_number", "u64");
    Native("SigByNumber", "bool", "df_signal_by_number", "u64");
  }

  // Signal.group([mode,] 1, 35678, "bump", ..., timeout: t) (spec 20.3): a SignalGroup of the signals with those numbers and names,
  // all of the mode (Single when it is not written). null when the call is not one
  Expr? SignalGroupCreate(Node n, Scope scope) {
    if (!TasksMode || n.Children[0] is not { Kind: "Member", Text: "group" } member || member.Children[0] is not { Kind: "Name", Text: "Signal" }
        || scope.Find("Signal") != null || !classes.ContainsKey("SignalGroup")) {
      return null;
    }
    var args = n.Children[1].Children;
    Node? mode = null;
    Node? timeout = null;
    var items = new List<Node>();
    foreach (var arg in args) {
      if (arg is { Kind: "Named", Text: "timeout" }) {
        timeout = arg.Children[0];
      } else if (arg.Kind == "Named") {
        throw Error($"Signal.group takes the named argument timeout only, not {arg.Text} (spec 20.3)", arg);
      } else if (mode == null && items.Count == 0 && ExprOf(arg, scope).Type == "SignalMode") {
        mode = arg;
      } else {
        items.Add(arg);
      }
    }
    if (items.Count == 0) {
      throw Error("Signal.group takes the numbers and names of its signals: Signal.group(1, 35678, \"bump\") (spec 20.3)", n);
    }
    var list = Synthetic("List", n);
    foreach (var item in items) {
      var type = ExprOf(item, scope).Type;
      if (type != "string" && !IsInt(type) && type != "KnownSignals") {
        throw Error($"a member of Signal.group is a number or a name, not {DisplayName(type)} (spec 20.3)", item);
      }
      var made = Synthetic("Args", item);
      if (mode != null && type != "KnownSignals") {
        made.Add(mode);
      }
      made.Add(item);
      list.Add(Synthetic("Call", item).Add(Synthetic("Name", item, "Signal")).Add(made));
    }
    var groupArgs = Synthetic("Args", n).Add(list).Add(timeout ?? Synthetic("Null", n));
    return ExprOf(Synthetic("Call", n).Add(Synthetic("Name", n, "SignalGroup")).Add(groupArgs), scope);
  }

  // wait g [timeout t] on a SignalGroup (spec 20.3): the SignalType of the member that fired first; zero (no number, no name) on timeout
  Expr SignalGroupWait(Node n, Node target, Expr waited, Node? timeoutNode, Scope scope) {
    if (n.Children.Any(c => c.Kind == "WaitDefault")) {
      throw Error("enforce wait default is not defined on a SignalGroup yet", n);
    }
    string timeoutCode = timeoutNode == null ? "0" : Convert(ExprOf(timeoutNode, scope, "lapse"), "lapse", timeoutNode);
    var held = TempObject(waited);
    string members = NewTemp();
    string handles = NewTemp();
    string won = NewTemp();
    string error = NewTemp();
    string index = NewTemp();
    string call = Serving($"df_signal_wait_any({handles}, (size_t){members}.len, {timeoutCode}, ({held.Code})->m_timeout, &{won})");
    string waitCode = $"({{ {CType("arr<Signal>")} {members} = ({held.Code})->m_members; uint64_t* {handles} = (uint64_t*)malloc(({members}.len ? {members}.len : 1) * sizeof(uint64_t)); "
      + $"if (!{handles}) df_out_of_memory(); for (df_uns {index} = 0; {index} < {members}.len; {index}++) {{ {handles}[{index}] = ({members}.p[{index}])->m_handle; }} "
      + $"int64_t {won} = -1; df_exception* {error} = {SilentCall(call, "Exception", "WaitTimeoutException", n)}; free({handles}); "
      + $"if ({error}) {{ df_err = {error}; df_err_thrown = false; goto {Handler}; }} {won}; }})";
    var typed = Synthetic("Call", n).Add(Synthetic("Member", n, "typeAt").Add(target))
      .Add(Synthetic("Args", n).Add(Synthetic("CValue", n, waitCode).Add(Synthetic("Type", n, "i64"))));
    return ExprOf(typed, scope);
  }

  // t.signal(s) on a Task or a Thread (spec 20.3.1, 20.9): s for that runnable only. null when the call is not one
  Expr? RunnableSignalCall(Node n, Scope scope) {
    if (!TasksMode || n.Children[0] is not { Kind: "Member", Text: "signal" } member || !classes.ContainsKey("Signal")) {
      return null;
    }
    var receiver = ExprOf(member.Children[0], scope);
    if (receiver.Type is not ("Task" or "Thread")) {
      return null;
    }
    var args = n.Children[1].Children;
    if (args.Count != 1 || args[0].Kind == "Named") {
      throw Error("signal() takes the signal to send: t.signal(Signal(\"myapp.reload\")) (spec 20.3.1)", n);
    }
    var sent = ExprOf(args[0], scope);
    if (sent.Type != "Signal") {
      throw Error($"signal() sends a Signal, not {DisplayName(sent.Type)} (spec 20.3.1)", args[0]);
    }
    var runnable = TempObject(receiver);
    var signal = TempObject(sent);
    return new Expr($"df_signal_to(({runnable.Code})->m_handle, ({signal.Code})->m_handle)", "void");
  }

  // the end of the main program (spec 20.10): every runnable that has not ended gets the soft kill, in one window of 50 ms per level
  string TasksEndCode() => TasksMode ? "  df_tasks_end_main();\n" : "";

  // the very end of the program: the registry of the events is freed, with what is left in the queues (spec 20.4)
  string EventsEndCode() => TasksMode ? "  df_events_end();\n" : "";

  // wait s [timeout t] (spec 20.2, 20.3): a signal gives no value; the timeout of the wait, else the one of the signal
  Expr SignalWait(Node n, Expr waited, Node? timeoutNode, Scope scope) {
    if (n.Children.Any(c => c.Kind == "WaitDefault")) {
      throw Error("enforce wait gives a value: a signal gives none (spec 20.3)", n);
    }
    string timeoutCode = timeoutNode == null ? "0" : Convert(ExprOf(timeoutNode, scope, "lapse"), "lapse", timeoutNode);
    var held = TempObject(waited);
    string error = NewTemp();
    string call = Serving($"df_signal_wait(({held.Code})->m_handle, {timeoutCode}, ({held.Code})->m_timeout)");
    string waitCall = SilentCall(call, "Exception", "WaitTimeoutException", n);
    return new Expr($"({{ df_exception* {error} = {waitCall}; if ({error}) {{ df_err = {error}; df_err_thrown = false; goto {Handler}; }} }})", "void");
  }
}
