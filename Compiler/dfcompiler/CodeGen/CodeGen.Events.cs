using System.Text;
using dfparser;

namespace dfcompiler;

// Events (spec 20.4, Root.Tasks): a name, a struct of data, a sender that registers it and receivers that find it. The class Event is
// written in Dflat (EventsPrelude) over a reference to the runtime event (RuntimeTasks: df_event, df_evref); Event.register(...),
// Event(name), ev.send(data) and wait on ev [from x] as data { } timeout t are compiled here. Sender<T> (20.4.6) is a u64 field that holds
// the id of an object of T: the classes named in a Sender<T> get a hidden field df_oid, given at the creation of each object.
public sealed partial class CodeGen {
  const string EventsPrelude = """
enum EventMode { Volatile, Static }
enum EventQueueFullMode { DiscardNewest, DiscardOldest, BlockSender }

public class Event(u64 handle) {
  unrestricted fun string get_name() { Root.Tasks.Native.EvName(handle) }
} atEnd {
  Root.Tasks.Native.EvRelease(handle)
}
""";

  static void AddEventsStdlib(Action<string, StdFun> add) {
    var tasks = new[] { "tasks" };
    add("Root.Tasks.Native.EvName", new StdFun(new[] { "u64" }, "string", "df_event_name", false, 0, "", null, tasks));
    add("Root.Tasks.Native.EvRelease", new StdFun(new[] { "u64" }, "void", "df_event_release", false, 0, "", null, tasks));
  }

  static readonly string[] EventReservedNames = { "processed", "setProcessed", "stopPropagation" };

  readonly Dictionary<Node, Node> eventTypeSpecs = new(ReferenceEqualityComparer.Instance);   // Event<Size>: the type node -> Size
  readonly Dictionary<VarInfo, string> eventVars = new();                                     // an Event variable -> its struct
  readonly Dictionary<(Node Fun, string Param), Node> eventParamTypes = new();
  readonly Dictionary<(string Struct, string Field), string> senderFields = new();             // Sender<T> fields -> T
  readonly HashSet<string> senderClasses = new();
  readonly Dictionary<VarInfo, string> eventItems = new();                                    // the data of wait on -> its C item
  readonly HashSet<string> eventHelpers = new();

  // before the code: Event<Size> keeps Size aside; Sender<T> fields become u64 (the id); the classes named in them get their id field;
  // Event<T> x = Event.register(...) / Event(...) give T to the call
  void LowerEventTypes(Node program) {
    var structNames = program.Children.Where(c => c.Kind == "Struct").Select(c => c.Text!).ToHashSet();
    void Walk(Node node) {
      if (node.Kind == "Type" && node.Text == "Event" && node.Children.Count > 0) {
        if (node.Children.Count != 1 || node.Children[0] is not { Kind: "Type" } data) {
          throw Error("Event<...> takes the struct of its data: Event<Size> (spec 20.4.2)", node);
        }
        eventTypeSpecs[node] = data;
        node.Children.Clear();
        return;
      }
      if (node.Kind == "Struct") {
        Node? found = null;
        foreach (var member in node.Children.Where(member => member.Kind == "Field")) {
          if (member.Children.Count > 0 && member.Children[0] is { Kind: "Type", Text: "Sender" } sender) {
            if (sender.Children.Count != 1 || sender.Children[0] is not { Kind: "Type" } target) {
              throw Error("Sender<...> takes the class of the sender: Sender<Window> (spec 20.4.6)", sender);
            }
            if (found != null) {
              throw Error($"a struct has at most one Sender<...> field: {found.Text} and {member.Text} (spec 20.4.6)", member);
            }
            found = member;
            senderFields[(node.Text!, member.Text!)] = target.Text!;
            senderClasses.Add(target.Text!);
            sender.Text = "u64";   // the id of the sender
            sender.Children.Clear();
          }
        }
      }
      if (node.Kind == "Decl" && node.Children.FirstOrDefault(c => c.Kind == "Type" && c.Text == "Event") is Node declType && declType.Children.Count == 1
          && declType.Children[0] is { Kind: "Type" } declData
          && node.Children.FirstOrDefault(c => c.Kind == "Init")?.Children[0] is { Kind: "Call" } init && IsEventMaker(init, structNames)) {
        init.Children[1].Add(Synthetic("Named", init, "data").Add(Synthetic("Name", init, declData.Text)));   // Event<Size> x = ...: Size
      }
      foreach (var child in node.Children) {
        Walk(child);
      }
    }
    Walk(program);
    // the id of every object of a class named in a Sender<T> (spec 20.4.6)
    foreach (var cls in program.Children.Where(c => c.Kind == "Class" && senderClasses.Contains(c.Text!))) {
      var body = cls.Children.First(c => c.Kind == "Block");
      if (body.Children.Any(m => m.Kind == "Decl" && m.Text == "df_oid")) {
        continue;
      }
      var decl = Synthetic("Decl", cls, "df_oid").Add(Synthetic("Type", cls, "u64"))
        .Add(Synthetic("Init", cls).Add(Synthetic("CValue", cls, "df_oid_next()").Add(Synthetic("Type", cls, "u64"))));
      body.Children.Insert(0, decl);
    }
  }

  // Event.register(...) or Event(name, ...) without the struct written in it
  static bool IsEventMaker(Node call, HashSet<string> structNames) {
    var args = call.Children[1].Children;
    if (call.Children[0] is { Kind: "Member", Text: "register" } reg && reg.Children[0] is { Kind: "Name", Text: "Event" }) {
      return !(args.Count > 1 && args[1] is { Kind: "Name" } second && structNames.Contains(second.Text!)) && !args.Any(a => a is { Kind: "Named", Text: "data" });
    }
    return call.Children[0] is { Kind: "Name", Text: "Event" } && !args.Any(a => a is { Kind: "Named", Text: "data" } || a.Kind == "CValue");
  }

  // the struct of an event: it must be a struct of values and strings, without the names of the propagation (spec 20.4.2)
  string EventDataType(string name, Node at) {
    if (!structs.TryGetValue(name, out var info)) {
      throw Error($"the data of an event is a struct, not {DisplayName(name)} (spec 20.4.2)", at);
    }
    foreach (var field in info.Fields) {
      if (EventReservedNames.Contains(field.Name)) {
        throw Error($"{field.Name} is a reserved word for structures used as event data.", field.At);
      }
      CheckEventFieldType(field.Type, field.Name, field.At);
    }
    foreach (string reserved in EventReservedNames) {
      if (funs.TryGetValue($"{name}.{reserved}", out var methods)) {
        throw Error($"{reserved} is a reserved word for structures used as event data.", methods[0].Node);
      }
    }
    return name;
  }

  void CheckEventFieldType(string type, string field, Node at) {
    if (IsNumeric(type) || type is "bool" or "char" or "string" or "string?" or "lapse" or "date" or "time" or "clock" || IsEnum(type)) {
      return;
    }
    if (structs.TryGetValue(type, out var inner)) {
      foreach (var f in inner.Fields) {
        CheckEventFieldType(f.Type, f.Name, f.At);
      }
      return;
    }
    throw Error($"'{field}': the data of an event holds numbers, strings, enums, structs of them and Sender<T>, not {DisplayName(type)} (spec 20.4.2)", at);
  }

  // the C functions that copy the data into an event and end it
  (string Copy, string Drop) EventHelpers(string type) {
    var info = structs[type];
    string copy = "df_evcp_" + info.CName;
    string drop = "df_evdrop_" + info.CName;
    if (eventHelpers.Add(type)) {
      bool owns = StructHoldsObjects(type);
      string clone = owns ? StructCloneHelper(type) : "";   // first: the helpers write their own code
      string dropper = owns ? StructDropHelper(type) : "";
      structProtos.Append($"static void {copy}(void* d, const void* s);\nstatic void {drop}(void* p);\n");
      structCode.Append($"static void {copy}(void* d, const void* s) {{\n  *({info.CName}*)d = {(owns ? $"{clone}(*(const {info.CName}*)s)" : $"*(const {info.CName}*)s")};\n}}\n\n");
      structCode.Append($"static void {drop}(void* p) {{\n  {(owns ? $"{dropper}(({info.CName}*)p);" : "(void)p;")}\n}}\n\n");
    }
    return (copy, drop);
  }

  // the full name: a name without a dot gets the namespace of the file (spec 20.4.1)
  string EventNameCode(Node nameNode, Scope scope) {
    var name = ExprOf(nameNode, scope, "string");
    if (name.Type != "string") {
      throw Error($"the name of an event is a string, not {DisplayName(name.Type)} (spec 20.4.1)", nameNode);
    }
    string ns = SourceMap.NamespaceOf(nameNode.Line) ?? "";
    return $"df_event_full_name({name.Code}, \"{ns}\")";
  }

  // Event.register(name, [Size], [mode], [timeout], timeout:, keep:, fullMode:) and Event(name); null when the call is not one
  Expr? EventMaker(Node n, Scope scope) {
    if (!TasksMode || scope.Find("Event") != null || !classes.ContainsKey("Event")) {
      return null;
    }
    var args = n.Children[1].Children;
    bool register = n.Children[0] is { Kind: "Member", Text: "register" } reg && reg.Children[0] is { Kind: "Name", Text: "Event" };
    bool find = n.Children[0] is { Kind: "Name", Text: "Event" } && !(args.Count > 0 && args[0].Kind == "CValue");
    if (!register && !find) {
      return null;
    }
    var positional = args.Where(a => a.Kind != "Named").ToList();
    var named = args.Where(a => a.Kind == "Named").ToDictionary(a => a.Text!, a => a.Children[0]);
    if (positional.Count == 0) {
      throw Error("an event is named: Event.register(\"gfx.window.ui.resize\", timeout: 1000) (spec 20.4.3)", n);
    }
    string? data = named.TryGetValue("data", out var dataNode) ? dataNode.Text : null;
    int next = 1;
    if (register && positional.Count > 1 && positional[1] is { Kind: "Name" } second && structs.ContainsKey(second.Text!)) {
      data = second.Text;
      next = 2;
    }
    if (data == null) {
      throw Error(register
        ? "the struct of the event is not known: Event<Size> x = Event.register(...) or Event.register(name, Size, ...) (spec 20.4.3)"
        : "the struct of the event is not known: Event<Size> x = Event(name, ...) (spec 20.4.3)", n);
    }
    EventDataType(data, n);
    Node? mode = null;
    Node? timeout = named.GetValueOrDefault("timeout");
    foreach (var arg in positional.Skip(next)) {
      var type = ExprOf(arg, scope).Type;
      if (register && type == "EventMode" && mode == null) {
        mode = arg;
      } else if (timeout == null && (IsNumeric(type) || type == "lapse")) {
        timeout = arg;
      } else {
        throw Error($"this argument of {(register ? "Event.register" : "Event")} is not expected (spec 20.4.3)", arg);
      }
    }
    foreach (var key in named.Keys.Where(key => key is not ("data" or "timeout" or "keep" or "fullMode"))) {
      throw Error($"{(register ? "Event.register" : "Event")} has no argument '{key}' (spec 20.4.3)", named[key]);
    }
    if (find && (named.ContainsKey("keep") || named.ContainsKey("fullMode") || mode != null)) {
      throw Error("the mode, keep and fullMode of an event are given by its registration (spec 20.4.4)", n);
    }
    if (find && timeout != null) {
      throw Error("an event has no timeout for its receivers: the timeout is the one of the wait (spec 20.2.8, 20.4.3)", timeout);
    }
    bool blockSender = named.TryGetValue("fullMode", out var fullModeNode) && fullModeNode is { Kind: "Member", Text: "BlockSender" };
    if (register && timeout != null && !blockSender) {
      throw Error("the timeout of a registration is how long a BlockSender send waits for room: it is given only with fullMode: EventQueueFullMode.BlockSender (spec 20.4.3)", timeout);
    }
    if (register && timeout == null && blockSender) {
      throw Error("a BlockSender event gives how long a send waits for room: Event.register(name, ..., timeout: 1000) (spec 20.4.3)", n);
    }
    string timeoutCode = timeout == null ? "0" : Convert(ExprOf(timeout, scope, "lapse"), "lapse", timeout);
    string nameTemp = NewTemp();
    string result = NewTemp();
    string code;
    if (register) {
      var (copy, drop) = EventHelpers(data);
      string modeCode = mode == null ? "0" : $"(int32_t)({ExprOf(mode, scope, "EventMode").Code})";
      string keepCode = named.TryGetValue("keep", out var keep) ? $"(int64_t)({Convert(ExprOf(keep, scope, "i64"), "i64", keep)})" : "1";
      string fullCode = named.TryGetValue("fullMode", out var full) ? $"(int32_t)({ExprOf(full, scope, "EventQueueFullMode").Code})" : "1";
      code = $"({{ df_string {nameTemp} = {EventNameCode(positional[0], scope)}; uint64_t {result} = df_event_register({nameTemp}, \"{DisplayName(data)}\", "
        + $"sizeof({CType(data)}), {copy}, {drop}, {modeCode}, {timeoutCode}, {keepCode}, {fullCode}); df_str_free({nameTemp}); "
        + $"if (!{result}) {{ df_raise(&df_desc_DuplicateEventNameWithDataMismatchException, {fn!.NameConst}); {RaiseJump} }} {result}; }})";
    } else {
      code = $"({{ df_string {nameTemp} = {EventNameCode(positional[0], scope)}; uint64_t {result} = df_event_find({nameTemp}, \"{DisplayName(data)}\"); "
        + $"df_str_free({nameTemp}); {result}; }})";
    }
    var made = Synthetic("Call", n).Add(Synthetic("Name", n, "Event"))
      .Add(Synthetic("Args", n).Add(Synthetic("CValue", n, code).Add(Synthetic("Type", n, "u64"))));
    return ExprOf(made, scope);
  }

  // the struct of an Event expression: a variable, a parameter, a field, or the call that makes it; null when it is not known
  string? EventDataOf(Node node, Scope scope) {
    if (node.Kind == "Name" && scope.Find(node.Text!) is VarInfo variable) {
      return eventVars.TryGetValue(variable, out var data) ? data : null;
    }
    if (node.Kind == "Name" && currentClass != null && FindField(currentClass, node.Text!) is FieldInfo own) {
      return EventFieldData(own);
    }
    if (node.Kind == "Member" && node.Children.Count == 1 && StartsFromValue(node.Children[0], scope)) {
      var receiver = ExprOf(node.Children[0], scope);
      if (IsClass(receiver.Type) && classes.TryGetValue(receiver.Type, out var cls) && FindField(cls, node.Text!) is FieldInfo field) {
        return EventFieldData(field);
      }
    }
    if (node.Kind == "Call") {
      var args = node.Children[1].Children;
      if (args.FirstOrDefault(a => a is { Kind: "Named", Text: "data" }) is Node data) {
        return data.Children[0].Text;
      }
      if (node.Children[0] is { Kind: "Member", Text: "register" } && args.Count > 1 && args[1] is { Kind: "Name" } second && structs.ContainsKey(second.Text!)) {
        return second.Text;
      }
    }
    return null;
  }

  string? EventFieldData(FieldInfo field) {
    foreach (var child in field.At.Children) {
      if (eventTypeSpecs.TryGetValue(child, out var data)) {
        return data.Text;
      }
    }
    return null;
  }

  // after a declaration: what struct an Event variable carries
  void NoteEventVar(Node decl, Scope scope) {
    if (!TasksMode || !scope.Vars.TryGetValue(decl.Text!, out var variable) || variable.Type != "Event") {
      return;
    }
    if (decl.Children.FirstOrDefault(c => c.Kind == "Type") is Node typeNode && eventTypeSpecs.TryGetValue(typeNode, out var spec)) {
      eventVars[variable] = EventDataType(spec.Text!, spec);
      return;
    }
    if (decl.Children.FirstOrDefault(c => c.Kind == "Init")?.Children[0] is Node init && EventDataOf(init, scope) is string data) {
      eventVars[variable] = data;
    }
  }

  void NoteEventParams(FunSig f, Scope scope) {
    if (!TasksMode) {
      return;
    }
    foreach (var p in f.Params) {
      if (eventParamTypes.TryGetValue((f.Node, p.Name), out var typeNode) && eventTypeSpecs.TryGetValue(typeNode, out var spec)
          && scope.Vars.TryGetValue(p.Name, out var variable)) {
        eventVars[variable] = EventDataType(spec.Text!, spec);
      }
    }
  }

  // the id of a sender (spec 20.4.6): myid in a method of T, or an object of T
  string SenderIdCode(Node value, string senderClass, Scope scope) {
    if (value is { Kind: "Name", Text: "myid" } && scope.Find("myid") == null) {
      if (currentClass == null || scope.Find("this") == null || !IsSubtype(currentClass.Name, senderClass)) {
        throw Error($"myid is the id of the object a method of {DisplayName(senderClass)} runs on: here there is none (spec 20.4.6)", value);
      }
      return $"self->{FindField(currentClass, "df_oid")!.CName}";
    }
    var given = ExprOf(value, scope);
    if (given.Type == "null") {
      return "0";
    }
    if (!IsClass(given.Type) || !IsSubtype(given.Type, senderClass)) {
      throw Error($"a Sender<{DisplayName(senderClass)}> takes myid or an object of {DisplayName(senderClass)}, not {DisplayName(given.Type)} (spec 20.4.6)", value);
    }
    var held = TempObject(given);
    string temp = NewTemp();
    return $"({{ {CType(given.Type)} {temp} = {held.Code}; {temp} != NULL ? {temp}->{FindField(ObjInfo(given.Type), "df_oid")!.CName} : 0; }})";
  }

  // ev.send(data) (spec 20.4): skipped when the event is not registered; another struct raises the exception
  Expr? EventSend(Node n, Scope scope) {
    if (!TasksMode || n.Children[0] is not { Kind: "Member", Text: "send" } member || !classes.ContainsKey("Event")) {
      return null;
    }
    if (member.Children[0] is { Kind: "Name" } owner && scope.Find(owner.Text!) == null && currentClass?.Fields.Any(f => f.Name == owner.Text) != true) {
      return null;   // Signal.send(...) and other names
    }
    var receiver = ExprOf(member.Children[0], scope);
    if (receiver.Type != "Event") {
      return null;
    }
    string data = EventDataOf(member.Children[0], scope)
      ?? throw Error("this Event does not say the struct of its data: Event<Size> (spec 20.4.2)", n);
    var args = n.Children[1].Children;
    if (args.Count != 1 || args[0].Kind == "Named") {
      throw Error("send takes the data of the event: resize.send(Size(...)) (spec 20.4)", n);
    }
    var value = ExprOf(args[0], scope, data);
    if (value.Type != data) {
      throw Error($"this event carries {DisplayName(data)}, not {DisplayName(value.Type)} (spec 20.4.2)", args[0]);
    }
    var held = TempObject(receiver);
    string temp = NewTemp();
    string state = NewTemp();
    var senderField = structs[data].Fields.FirstOrDefault(f => senderFields.ContainsKey((data, f.Name)));
    string sender = senderField == null ? "0" : $"{temp}.f_{senderField.Name}";
    string drop = value.Fresh && StructHoldsObjects(data) ? StructDropCode(data, temp) : "";
    return new Expr($"({{ {CType(data)} {temp} = {Convert(value, data, args[0])}; int32_t {state} = df_event_send(({held.Code})->m_handle, &{temp}, {sender}); {drop} "
      + $"if ({state} == 2) {{ df_raise(&df_desc_DuplicateEventNameWithDataMismatchException, {fn!.NameConst}); {RaiseJump} }} }})", "void");
  }

  // data.processed, data.setProcessed(), data.stopPropagation() on the data of wait on (spec 20.4.5); null otherwise
  Expr? EventDataMember(Node n, Scope scope) {
    if (eventItems.Count == 0) {
      return null;
    }
    if (n.Kind == "Member" && n.Text == "processed" && n.Children[0] is { Kind: "Name" } held && scope.Find(held.Text!) is VarInfo v && eventItems.TryGetValue(v, out var item)) {
      return new Expr($"({item}->processed)", "bool");
    }
    if (n.Kind == "Call" && n.Children[0] is { Kind: "Member", Text: "setProcessed" or "stopPropagation" } call && call.Children[0] is { Kind: "Name" } owner
        && scope.Find(owner.Text!) is VarInfo bound && eventItems.TryGetValue(bound, out var it)) {
      if (n.Children[1].Children.Count != 0) {
        throw Error($"{call.Text}() takes no arguments (spec 20.4.5)", n);
      }
      return new Expr(call.Text == "setProcessed" ? $"((void)({it}->processed = true))" : $"((void)({it}->stopped = true))", "void");
    }
    return null;
  }
}
