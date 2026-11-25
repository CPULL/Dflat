using dfparser;

namespace dfcompiler;

// Signals (spec 20.3, Root.Tasks): a bare go, identified by a name or by a number, with a mode set when it is made. The class Signal is
// written in Dflat (SignalsPrelude) over the natives of Root.Tasks.Native; the registry, the queues of the waiters and the counters are
// in the runtime (RuntimeTasks: df_signal). wait s timeout t is compiled here (SignalWait), called by TaskWait.
public sealed partial class CodeGen {
  const string SignalsPrelude = """
enum SignalMode { Broadcast, Exclusive, Single, ExclusiveSafe }
enum KnownSignals { Cancel: 666 }

public class Signal(u64 handle, bool made) {
  public Signal(string name) : Signal(SignalMode.Single, name) { }
  public Signal(uns number) : Signal(SignalMode.Single, number) { }
  public Signal(SignalMode mode, string name) : Signal(Root.Tasks.Native.SignalGet((i32)mode, false, 0, name, false), true) {
    Signal.check(handle)
  }
  public Signal(SignalMode mode, uns number) : Signal(Root.Tasks.Native.SignalGet((i32)mode, true, (u64)number, "", false), true) {
    Signal.check(handle)
  }
  public Signal(KnownSignals known) : Signal(Root.Tasks.Native.SignalGet((i32)SignalMode.ExclusiveSafe, true, (u64)known, "", true), true) { }

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
    return Signal(found, true)
  }
  public static fun Signal tryGet(SignalMode mode, uns number) {
    u64 found = Root.Tasks.Native.SignalFind((i32)mode, true, (u64)number, "")
    if found == 0 then return null
    return Signal(found, true)
  }

  unrestricted fun SignalMode get_mode() { (SignalMode)Root.Tasks.Native.SigMode(handle) }
  unrestricted fun string get_name() { Root.Tasks.Native.SigName(handle) }
  unrestricted fun uns? get_number() {
    if Root.Tasks.Native.SigByNumber(handle) then return (uns)Root.Tasks.Native.SigNumber(handle)
    return null
  }
}

public class SignalGroup(->arr<Signal> members) { }

public class Token(u64 handle) {
  public Token(string name) : Token(Root.Tasks.Native.TokenFind(name)) { }
  public static fun ->Token register(string name) {
    u64 made = Root.Tasks.Native.TokenRegister(name)
    if made == 0 then throw TokenAlreadyRegisteredException("This token is registered already")
    return Token(made)
  }
  unrestricted fun produce(uns n = 1) { Root.Tasks.Native.TokenProduce(handle, (u64)n) }
  unrestricted fun uns get_count() { (uns)Root.Tasks.Native.TokenCount(handle) }
  unrestricted fun string get_name() { Root.Tasks.Native.SigName(handle) }
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
    Native("TokenRegister", "u64", "df_token_register", "string");
    Native("TokenFind", "u64", "df_token_find", "string");
    Native("TokenProduce", "void", "df_token_produce", "u64", "u64");
    Native("TokenCount", "u64", "df_token_count", "u64");
  }

  // Signal.group([mode,] 1, 35678, "bump", ...) (spec 20.3): a SignalGroup of the signals with those numbers and names,
  // all of the mode (Single when it is not written). null when the call is not one
  // Token(name) and Token.register(name) (spec 20.3.2): the name gets the namespace of its file, as the one of an event (20.4.1); then they
  // are the ones of the prelude. null when the call is not one
  Expr? TokenMaker(Node n, Scope scope) {
    if (!TasksMode || LibraryCode || !classes.ContainsKey("Token") || scope.Find("Token") != null
        || n.Children[0] is not ({ Kind: "Name", Text: "Token" } or { Kind: "Member", Text: "register", Children: [{ Kind: "Name", Text: "Token" }] })
        || n.Children[1].Children is not [{ Kind: not "CValue" } nameNode]) {
      return null;
    }
    if (nameNode.Kind == "Named" || ExprOf(nameNode, scope).Type != "string") {
      return null;   // the errors of the call itself
    }
    fn!.UsesTmp = true;
    var full = Synthetic("CValue", nameNode, $"df_tmp({EventNameCode(nameNode, scope)})").Add(Synthetic("Type", nameNode, "string"));
    return ExprOf(Synthetic("Call", n).Add(n.Children[0]).Add(Synthetic("Args", n).Add(full)), scope);
  }

  // SignalGroup(s1, s2) (spec 20.3): the group of those signals, which it knows by their handles
  Expr? SignalGroupCreate(Node n, Scope scope) {
    if (!TasksMode || scope.Find("Signal") != null || !classes.ContainsKey("SignalGroup")) {
      return null;
    }
    if (n.Children[0] is { Kind: "Name", Text: "SignalGroup" } && scope.Find("SignalGroup") == null
        && n.Children[1].Children is not [{ Kind: "List" }]) {
      var given = n.Children[1].Children;
      if (given.Count == 0) {
        throw Error("SignalGroup takes its signals: SignalGroup(s1, s2) (spec 20.3)", n);
      }
      var copies = Synthetic("List", n);
      foreach (var arg in given) {
        var signal = ExprOf(arg, scope);
        if (arg.Kind == "Named" || signal.Type != "Signal") {
          throw Error($"SignalGroup takes signals, not {DisplayName(signal.Type)} (spec 20.3)", arg);
        }
        string held = NewTemp();
        copies.Add(SignalOfHandle($"({{ {CType("Signal")} {held} = {TempObject(signal).Code}; {held} != NULL && !DF_IS_DEAD({held}) ? {held}->m_handle : 0; }})", arg));
      }
      return ExprOf(Synthetic("Call", n).Add(Synthetic("Name", n, "SignalGroup")).Add(Synthetic("Args", n).Add(copies)), scope);
    }
    if (n.Children[0] is not { Kind: "Member", Text: "group" } member || member.Children[0] is not { Kind: "Name", Text: "Signal" }) {
      return null;
    }
    var args = n.Children[1].Children;
    Node? mode = null;
    var items = new List<Node>();
    foreach (var arg in args) {
      if (arg is { Kind: "Named", Text: "timeout" }) {
        throw Error("a signal group has no timeout of its own: the timeout is the one of the wait (spec 20.2.8)", arg);
      } else if (arg.Kind == "Named") {
        throw Error($"Signal.group takes no named arguments, not {arg.Text} (spec 20.3)", arg);
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
    var groupArgs = Synthetic("Args", n).Add(list);
    return ExprOf(Synthetic("Call", n).Add(Synthetic("Name", n, "SignalGroup")).Add(groupArgs), scope);
  }

  // a new Signal object of the signal with that C handle
  Node SignalOfHandle(string handle, Node at) =>
    Synthetic("Call", at).Add(Synthetic("Name", at, "Signal")).Add(Synthetic("Args", at)
      .Add(Synthetic("CValue", at, handle).Add(Synthetic("Type", at, "u64")))
      .Add(Synthetic("CValue", at, "true").Add(Synthetic("Type", at, "bool"))));

  // another object for the same signal, null for a null one
  Expr SignalCopy(Expr signal, Node at) {
    Scope scope = new(null);
    string held = NewTemp();
    string copy = NewTemp();
    var made = ExprOf(SignalOfHandle($"{held}->m_handle", at), scope);
    return new Expr($"({{ {CType("Signal")} {held} = {signal.Code}; {CType("Signal")} {copy} = NULL; if ({held} != NULL) {{ {copy} = {made.Code}; }} {copy}; }})", "Signal", true);
  }

  // wait g timeout t on a SignalGroup (spec 20.3): the Signal that fired first; null when the time runs out
  Expr SignalGroupWait(Node n, Expr waited, Node timeoutNode, Scope scope) {
    string fallback = "";
    if (n.Children.FirstOrDefault(c => c.Kind == "WaitDefault") is Node defaultNode && defaultNode.Children[0].Kind != "Null") {
      // timeout 500 { s1 }: that signal when nothing fired (spec 20.2.8)
      var other = ExprOf(defaultNode.Children[0], scope, "Signal");
      if (other.Type != "Signal") {
        throw Error($"a wait on a SignalGroup gives a Signal, not {DisplayName(other.Type)} (spec 20.3)", defaultNode);
      }
      string held = NewTemp();
      var copy = ExprOf(SignalOfHandle($"{held}->m_handle", n), scope);
      fallback = $" else {{ {CType("Signal")} {held} = {TempObject(other).Code}; if ({held} != NULL) {{ {{0}} = {copy.Code}; }} }}";
    }
    string fired = "df_wsigh" + NewTemp();
    string result = NewTemp();
    string code = WaitOne(n, waited, "NULL, 0", timeoutNode, "", fired, scope);
    var made = ExprOf(SignalOfHandle(fired, n), scope);
    string noted = WaitNoteFlag(n) is string flag ? $"{flag} = !{fired}; " : "";
    return new Expr($"({{ uint64_t {fired} = 0; {code} {noted}{CType("Signal")} {result} = NULL; if ({fired}) {{ {result} = {made.Code}; }}{fallback.Replace("{0}", result)} {result}; }})", "Signal", true);
  }

  // s1 == s2 (spec 20.3): the signals are compared, not the objects
  Expr? SignalEquality(string op, Expr l, Expr r) {
    if (!TasksMode || op is not ("==" or "!=") || l.Type != "Signal" || r.Type != "Signal") {
      return null;
    }
    string a = NewTemp();
    string b = NewTemp();
    string type = CType("Signal");
    return new Expr($"({{ {type} {a} = {TempObject(l).Code}; {type} {b} = {TempObject(r).Code}; "
      + $"({a} ? {a}->m_handle : 0) {op} ({b} ? {b}->m_handle : 0); }})", "bool");
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

  // wait s timeout t (spec 20.2, 20.3): a signal gives no value
  Expr SignalWait(Node n, Expr waited, Node? timeoutNode, Scope scope) {
    if (n.Children.Any(c => c.Kind is "WaitDefault" or "WaitNote")) {
      throw Error("the timeout block of a wait on a signal or a token gives no value: they give none (spec 20.3)", n);
    }
    return new Expr($"({{ {WaitOne(n, waited, "NULL, 0", timeoutNode!, "", null, scope)} }})", "void");
  }
}
