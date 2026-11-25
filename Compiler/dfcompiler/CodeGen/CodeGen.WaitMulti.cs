using System.Text;
using dfparser;

namespace dfcompiler;

// wait (spec 20.2). Each wait statement has a C df_waitset (RuntimeTasks), made at its first run and ended with the scope of the wait by a
// hidden atEnd (LowerWaitMulti): the scope that holds the wait, or the one that holds its loop, because what arrives between two turns of
// the loop is kept. The sets of a runnable share its registrations. Each run gives the waitables of that moment to the set, waits, and runs
// the block of the branch that won. The wait that gives a value (WaitOne) has a set too; a waitable that is not a variable (a call) is
// registered only while it is waited for.
public sealed partial class CodeGen {
  int waitSets;
  readonly HashSet<(FunctionContext Fun, string Name)> waitSetDecls = new();

  // a branch that is compiled: its set, its struct when it is on an event, the C names of the item, the slot that won, the group member
  sealed record WaitBranchCode(string Set, Node Branch, string? Data, string Item, string Won, string Member, string Id, int FirstSlot, string? Value);
  readonly Dictionary<Node, WaitBranchCode> waitBranches = new(ReferenceEqualityComparer.Instance);

  // the name of the set of a wait: a child, so that it stays when the node is rewritten
  static string? WaitSetName(Node wait) => wait.Children.FirstOrDefault(c => c.Kind == "WaitSet")?.Text;

  static bool HasWaitSet(Node wait) =>
    wait.Kind is "WaitMulti" or "WaitOn" || wait.Kind == "Wait" && wait.Children.Count > 0 && wait.Children[0].Kind is "Name" or "Member" or "Index" or "This";

  // gives each wait its set and adds its end to the scope of the wait
  void LowerWaitMulti(Node node, List<Node> path) {
    path.Add(node);
    for (int index = 0; index < node.Children.Count; index++) {
      var child = node.Children[index];
      if (HasWaitSet(child) && WaitSetName(child) == null) {
        path.Add(child);
        int at = path.Count - 2;   // the node that holds the wait
        while (at >= 0 && path[at].Kind is not ("Lambda" or "Fun") && (path[at].Kind is not ("Block" or "Program")
            || at > 0 && path[at - 1].Kind is "While" or "For" or "DoWhile")) {
          at--;
        }
        // the initial value of a field, a default value, the body of a lambda that is an expression: no scope of their own
        bool scoped = at >= 0 && path[at].Kind is "Block" or "Program" && !(at > 0 && path[at - 1].Kind is "Class" or "Trait" or "Struct" or "Enum" or "Handle");
        if (scoped) {
          string name = "df_ws" + ++waitSets;
          child.Add(Synthetic("WaitSet", child, name));
          var holder = path[at];
          int place = holder.Children.IndexOf(path[at + 1]);
          holder.Children.Insert(place, Synthetic("AtEnd", child, "drop").Add(Synthetic("Block", child).Add(Synthetic("WaitSetEnd", child, name))));
          if (holder == node) {
            index++;
          }
        }
        path.RemoveAt(path.Count - 1);
      }
      LowerWaitMulti(child, path);
      index = Math.Max(index, node.Children.IndexOf(child));   // an end added before the child, for a wait inside it
    }
    path.RemoveAt(path.Count - 1);
  }

  void DeclareWaitSet(string name) {
    if (waitSetDecls.Add((fn!, name))) {
      fn!.Decls.Append($"  df_waitset* {name} = NULL;\n  (void){name};\n");
    }
  }

  void EmitWaitSetEnd(Node s, StringBuilder sb, string pad) {
    DeclareWaitSet(s.Text!);
    sb.Append($"{pad}df_ws_end(&{s.Text});\n");
  }

  // gives the waitable of a slot to its set
  string WaitTargetCode(string set, int slot, int branch, Expr waited, string outCode, string hasFrom, string from, string? keep, string taskHeld) {
    string held = NewTemp();
    string code = $"{{ {CType(waited.Type)} {held} = {TempObject(waited).Code}; bool {held}k = {held} != NULL && !DF_IS_DEAD({held}); ";   // null: ignored (spec 20.2.3)
    string handle = $"({held}k ? {held}->m_handle : 0)";
    if (keep != null) {
      code += $"{keep} = {held}; ";
    }
    if (waited.Type is "Signal" or "Token") {
      return code + $"df_ws_set_signal({set}, {slot}, {branch}, {handle}); }}";
    }
    if (waited.Type == "Event") {
      return code + $"df_ws_set_event({set}, {slot}, {branch}, {handle}, {hasFrom}, {from}); }}";
    }
    if (waited.Type == "SignalGroup") {
      string handles = NewTemp();
      string index = NewTemp();
      string count = $"({held}k ? (size_t){held}->m_members.len : 0)";
      return code + $"uint64_t* {handles} = (uint64_t*)malloc(({count} + 1) * sizeof(uint64_t)); if (!{handles}) df_out_of_memory(); "
        + $"for (size_t {index} = 0; {index} < {count}; {index}++) {{ {handles}[{index}] = ({held}->m_members.p[{index}])->m_handle; }} "
        + $"df_ws_set_signals({set}, {slot}, {branch}, {handles}, {count}); free({handles}); }}";
    }
    return code + $"df_ws_set_task({set}, {slot}, {branch}, {handle}, {outCode}, {taskHeld}); }}";
  }

  // the C statements of a wait on one waitable that gives a value, or nothing to run: outCode is where a task puts its value; fired gets the
  // handle of the signal of a group; note gets the silent note (the caller uses it), otherwise the silent exceptions are given
  string WaitOne(Node n, Expr waited, string outCode, Node timeoutNode, string? exceptions, string? fired, Scope scope, string? note = null) {
    string? kept = WaitSetName(n);
    string set = kept ?? "df_wtmp" + NewTemp();
    DeclareWaitSet(set);
    string won = NewTemp();
    string member = NewTemp();
    string item = NewTemp();
    string error = NewTemp();
    string timeout = Convert(ExprOf(timeoutNode, scope, "lapse"), "lapse", timeoutNode);
    string call = Serving($"df_ws_wait({set}, {timeout}, &{won}, &{member}, &{item}, true, false)");
    var code = new StringBuilder($"if ({set} == NULL) {set} = df_ws_new(1, 1); {WaitTargetCode(set, 0, 0, waited, outCode, "false", "0", null, TaskHeld(n.Children[0], waited, scope))} ");
    code.Append($"int32_t {won}; int64_t {member}; df_ev_item* {item}; df_exception* {error} = {(note != null ? call : WaitCall(call, n, exceptions!))}; ");
    if (note != null) {
      code.Append($"{note} = df_silent_desc; df_silent_desc = NULL; ");
    }
    if (fired != null) {
      code.Append($"{fired} = df_ws_fired({set}, {won}, {member}); ");
    }
    if (kept == null) {
      code.Append($"df_ws_end(&{set}); ");
    }
    code.Append($"if ({error}) {{ df_err = {error}; df_err_thrown = false; goto {Handler}; }} ");
    return code.ToString();
  }

  // wait [on] x [from y] [as v] [{ ... }] timeout t [{ ... }] (spec 20.2.1): a wait with one branch
  void EmitWaitOn(Node s, StringBuilder sb, Scope scope, int indent) {
    if (!TasksMode) {
      throw Error("wait on is part of Root.Tasks: it needs import Root.Tasks (spec 20.0)", s);
    }
    var target = s.Children[0];
    if (s.Text == null && ExprOf(target, scope).Type == "Event") {
      throw Error("a wait on an event names its data: wait on resize as size { ... } (spec 20.2.1)", s);
    }
    var multi = Synthetic("WaitMulti", s);
    var branch = Synthetic("WaitBranch", s, s.Text).Add(target);
    if (s.Children.FirstOrDefault(c => c.Kind == "From") is Node from) {
      branch.Add(from);
    }
    branch.Text = s.Text;
    branch.Add(s.Children.Skip(1).FirstOrDefault(c => c.Kind == "Block") ?? Synthetic("Block", s));
    multi.Add(branch);
    multi.Children.AddRange(s.Children.Where(c => c.Kind is "Timeout" or "TimeoutBlock" or "WaitSet"));
    EmitWaitMulti(multi, sb, scope, indent, true);
  }

  // a wait with clauses (spec 20.2.7): the wait, then the clauses with the rules of the ones of enforce (7.2.3). catch E on x takes what makes
  // the waitable x fail: its slot is the failed one; the exception of a block is of no waitable
  void EmitWaitClauses(Node s, StringBuilder sb, Scope scope, int indent) {
    if (!TasksMode) {
      throw Error("wait is part of Root.Tasks: it needs import Root.Tasks (spec 20.0)", s);
    }
    var clauses = s.Children.Where(c => c.Kind is "Catch" or "AtEnd" or "EnforceDefault").ToList();
    var wait = Synthetic(s.Kind, s, s.Text);
    wait.Text = s.Text;
    wait.Children.AddRange(s.Children.Where(c => !clauses.Contains(c)));
    string set = WaitSetName(s) ?? throw Error("a wait with blocks needs a scope: it is written in a block", s);
    DeclareWaitSet(set);
    var targets = s.Kind == "WaitOn" ? new List<Node> { s.Children[0] }
      : s.Children.Where(c => c.Kind == "WaitBranch").SelectMany(b => b.Children.Where(c => c.Kind is not ("Block" or "From"))).ToList();
    foreach (var clause in clauses.Where(c => c.Kind == "Catch")) {
      if (clause.Children.FirstOrDefault(c => c.Kind == "On") is not Node on) {
        continue;
      }
      var slots = new List<string>();
      foreach (var name in on.Children) {
        var at = targets.Select((target, slot) => (target, slot)).Where(t => t.target.Kind == "Name" && t.target.Text == name.Text).ToList();
        if (at.Count == 0) {
          throw Error($"'{name.Text}' is not a waitable of this wait (spec 20.2.7)", name);
        }
        slots.AddRange(at.Select(t => $"{set}->failed == {t.slot}"));
      }
      catchOnCondition[clause] = $"({set} != NULL && ({string.Join(" || ", slots)}))";
    }
    var block = Synthetic("Block", s).Add(wait);
    if (clauses.Any(c => c.Kind == "AtEnd")) {
      CheckEnforceExits(block, 0, 0);   // return is the only way out of a block that has an atEnd after it (7.2.4)
    }
    var holder = Synthetic("Enforce", s);
    holder.Children.AddRange(clauses);
    EmitBlock(EnforceClausesBlock(holder, block), sb, scope, indent);
  }

  void EmitWaitMulti(Node s, StringBuilder sb, Scope scope, int indent, bool single = false) {
    if (!TasksMode) {
      throw Error("a wait on several waitables is part of Root.Tasks: it needs import Root.Tasks (spec 20.0)", s);
    }
    string pad = Pad(indent);
    string set = WaitSetName(s) ?? throw Error("a wait with blocks needs a scope: it is written in a block", s);
    DeclareWaitSet(set);
    var branches = s.Children.Where(c => c.Kind == "WaitBranch").ToList();
    var timeouts = s.Children.Where(c => c.Kind == "Timeout").ToList();
    RequireWaitTimeout(timeouts.FirstOrDefault(), s);
    if (timeouts.Count > 1) {
      throw Error("a wait has one timeout (spec 20.2.8)", timeouts[1]);
    }
    string id = NewTemp();
    string won = "df_won" + id;
    string member = "df_wmember" + id;
    string item = "df_witem" + id;
    fn!.Decls.Append($"  int32_t {won} = -1;\n  int64_t {member} = 0;\n  df_ev_item* {item} = NULL;\n  (void){won};\n  (void){member};\n  (void){item};\n");
    static List<Node> Targets(Node branch) => branch.Children.Where(c => c.Kind is not ("Block" or "From")).ToList();
    int slots = branches.Sum(branch => Targets(branch).Count);
    sb.Append($"{pad}if ({set} == NULL) {set} = df_ws_new({branches.Count}, {slots});\n");
    int slot = 0;
    var bodies = new List<(Node Body, int First, int Count)>();
    for (int b = 0; b < branches.Count; b++) {
      var branch = branches[b];
      var targets = Targets(branch);
      var fromNode = branch.Children.FirstOrDefault(c => c.Kind == "From");
      int first = slot;
      string? data = null;
      string? value = null;
      foreach (var target in targets) {
        var waited = ExprOf(target, scope);
        if (waited.Type is not ("Task" or "Thread" or "Signal" or "SignalGroup" or "Token" or "Event")) {
          throw Error($"wait takes a task, a thread, a signal, a signal group, a token or an event, not {DisplayName(waited.Type)} (spec 20.2.2)", target);
        }
        if (targets.Count > 1 && waited.Type != "Signal") {
          throw Error($"a branch on several waitables takes signals only, not {DisplayName(waited.Type)} (spec 20.2.1)", target);
        }
        if (fromNode != null && waited.Type != "Event") {
          throw Error("from filters the senders of an event (spec 20.4.6)", fromNode);
        }
        string? keep = null;
        string outCode = "NULL, 0";
        string hasFrom = "false";
        string from = "0";
        if (waited.Type == "Token" && branch.Text != null) {
          throw Error("a token gives no value: on a token takes no as (spec 20.3.2)", branch);
        }
        if (waited.Type == "Signal") {
          if (targets.Count == 1 && branch.Text != null) {
            throw Error("a signal has no data: on a signal takes no as (spec 20.3)", branch);
          }
          if (branch.Text != null) {
            keep = $"df_wsig{id}_{slot}";
            fn!.Decls.Append($"  {CType("Signal")} {keep} = NULL;\n  (void){keep};\n");
          }
        } else if (waited.Type == "Event") {
          data = EventDataOf(target, scope) ?? throw Error("this Event does not say the struct of its data: Event<Size> (spec 20.4.2)", target);
          if (fromNode != null) {
            string eventData = data;
            var senderField = structs[data].Fields.FirstOrDefault(f => senderFields.ContainsKey((eventData, f.Name)))
              ?? throw Error($"{DisplayName(data)} has no Sender<...> field: from has nothing to compare (spec 20.4.6)", fromNode);
            from = SenderIdCode(fromNode.Children[0], senderFields[(data, senderField.Name)], scope);
            hasFrom = "true";
          }
        } else if (waited.Type is "Task" or "Thread") {
          CheckCanWait(target, waited, scope);   // spec 20.2.6
          string ret = TaskHandleOf(target, scope)?.Ret ?? "void";
          if (ret != "void") {
            value = $"df_wval{id}_{slot}";
            fn!.Decls.Append($"  {CType(ret)} {value} = {ZeroValue(ret)};\n  (void){value};\n");
            outCode = $"&{value}, sizeof({value})";
          }
        }
        sb.Append(pad).Append(WaitTargetCode(set, slot, b, waited, outCode, hasFrom, from, keep, TaskHeld(target, waited, scope))).Append('\n');
        slot++;
      }
      var body = Synthetic("WaitBranchBody", branch, branch.Text);
      waitBranches[body] = new WaitBranchCode(set, branch, data, item, won, member, id, first, value);
      bodies.Add((body, first, targets.Count));
    }
    string timeoutCode = Convert(ExprOf(timeouts[0].Children[0], scope, "lapse"), "lapse", timeouts[0].Children[0]);
    string error = NewTemp();
    sb.Append($"{pad}{{ df_exception* {error} = {Serving($"df_ws_wait({set}, {timeoutCode}, &{won}, &{member}, &{item}, {(single ? "true" : "false")}, true)")}; df_silent_desc = NULL; "
      + $"if ({error}) {{ df_err = {error}; df_err_thrown = false; goto {Handler}; }} }}\n");
    // the block of the branch that won, else the one of the timeout
    Node? chain = s.Children.FirstOrDefault(c => c.Kind == "TimeoutBlock")?.Children[0];
    for (int b = bodies.Count - 1; b >= 0; b--) {
      var (body, first, count) = bodies[b];
      var test = Synthetic("CValue", s, $"({won} >= {first} && {won} < {first + count})").Add(Synthetic("Type", s, "bool"));
      var choice = Synthetic("If", s).Add(test).Add(Synthetic("Block", body).Add(body));
      if (chain != null) {
        choice.Add(Synthetic("Else", s).Add(chain));
      }
      chain = choice;
    }
    EmitIf(chain!, sb, scope, indent, false);
  }

  // the block of a branch, with what the waitable gave; the branch is free again when it ends, in any way (a hidden atEnd)
  void EmitWaitBranchBody(Node s, StringBuilder sb, Scope scope, int indent) {
    var code = waitBranches[s];
    var branch = code.Branch;
    var block = branch.Children.First(c => c.Kind == "Block");
    var target = branch.Children[0];
    string waitedType = ExprOf(target, scope).Type;
    var inner = new Scope(scope);
    var wrapped = Synthetic("Block", block);
    if (code.Data != null && s.Text != null) {
      CheckNotShadowing(s.Text, scope, s);
      var bound = new VarInfo(code.Data, $"(*({CType(code.Data)}*){code.Item}->data)", true) { ReadOnlyStruct = true };
      inner.Vars[s.Text] = bound;
      eventItems[bound] = code.Item;
    } else if (waitedType == "Signal" && s.Text != null) {
      // on s1, s2 as s: the signal that fired
      CheckNotShadowing(s.Text, scope, s);
      int count = branch.Children.Count(c => c.Kind is not ("Block" or "From"));
      string pick = $"df_wsig{code.Id}_{code.FirstSlot + count - 1}";
      for (int i = count - 2; i >= 0; i--) {
        pick = $"({code.Won} == {code.FirstSlot + i} ? df_wsig{code.Id}_{code.FirstSlot + i} : {pick})";
      }
      string fired = "df_wfired" + NewTemp();
      fn!.Decls.Append($"  {CType("Signal")} {fired} = NULL;\n  (void){fired};\n");
      sb.Append($"{Pad(indent)}{fired} = {pick};\n");
      inner.Vars[s.Text] = new VarInfo("Signal", fired, true);
    } else if (waitedType == "SignalGroup" && s.Text != null) {
      var typed = SignalOfHandle($"df_ws_fired({code.Set}, {code.Won}, {code.Member})", s);
      wrapped.Add(Synthetic("Decl", s, s.Text).Add(Synthetic("Type", s, "var")).Add(Synthetic("Init", s).Add(typed)));
    } else if (waitedType is "Task" or "Thread") {
      var taken = Synthetic("Wait", s).Add(target).Add(Synthetic("WaitTaken", s, code.Value ?? "((void)0)"));
      if (s.Text != null) {
        wrapped.Add(Synthetic("Decl", s, s.Text).Add(Synthetic("Type", s, "var")).Add(Synthetic("Init", s).Add(taken)));
      } else if (code.Value != null) {
        wrapped.Add(Synthetic("ExprStmt", s).Add(taken));   // the value is not named: it ends here
      }
    }
    string item = code.Data != null ? code.Item : "NULL";
    var done = Synthetic("ExprStmt", s).Add(Synthetic("CValue", s, $"df_ws_done({code.Set}, {item})").Add(Synthetic("Type", s, "void")));
    wrapped.Children.AddRange(block.Children);
    wrapped.Add(Synthetic("AtEnd", block, "drop").Add(Synthetic("Block", block).Add(done)));
    EmitBlock(wrapped, sb, inner, indent);
  }
}
