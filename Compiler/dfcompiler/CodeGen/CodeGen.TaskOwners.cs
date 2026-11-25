using dfparser;

namespace dfcompiler;

// Who can wait on a task (spec 20.1, 20.2.6): its owner, or the one that borrows the handle with &. The rules the compiler sees are here; the
// runtime (df_task_may_wait) raises WaitingOnANotOwnedTaskException for the others.
public sealed partial class CodeGen {
  // Task(MyTask, ...).start(timeout: t): the named argument that carries the start to the creation, where the handle is made
  const string StartArgument = "start$";

  readonly HashSet<string> enforcedNames = new();                                        // the variables of the enforce blocks around
  readonly Dictionary<VarInfo, Node> taskAliases = new();                                // if t is Task<MyTask> mt: mt -> t
  readonly List<(FunSig Fun, string What, Node At)> sharedTaskWaits = new();             // waits on a task that is not of the function
  readonly Dictionary<string, (VarInfo Task, Frame Frame, int Waits)> lentTasks = new(); // a task lent with & to a thread -> when
  int taskWaits;

  int hoistTyping;
  bool HoistTyping => hoistTyping > 0;   // only the type of a declaration is wanted

  // Task(MyTask).start() makes one handle, already started: start() on a variable lends it (spec 20.1)
  void LowerTaskStarts(Node node) {
    for (int index = 0; index < node.Children.Count; index++) {
      var child = node.Children[index];
      if (child.Kind == "Call" && child.Children[0] is { Kind: "Member", Text: "start" } start) {
        var made = start.Children[0];
        while (made.Kind == "Paren" && made.Children.Count == 1) {
          made = made.Children[0];
        }
        if (made.Kind == "Call" && made.Children[0] is { Kind: "Name", Text: "Task" or "Thread" } && made.Children[1].Children.Count > 0) {
          var begin = Synthetic("Named", child, StartArgument);
          begin.Add(child.Children[1]);   // the arguments of start
          made.Children[1].Add(begin);
          node.Children[index] = made;
          child = made;
        }
      }
      LowerTaskStarts(child);
    }
  }

  // the new handle, started when .start(...) was written on it
  Expr Started(Expr made, Node? started, Scope scope) {
    if (started == null) {
      return made;
    }
    string held = NewTemp();
    var receiver = Synthetic("CValue", started, held).Add(Synthetic("Type", started, made.Type));
    var call = ExprOf(Synthetic("Call", started).Add(Synthetic("Member", started, "start").Add(receiver)).Add(started.Children[0]), scope);
    return new Expr($"({{ {CType(made.Type)} {held} = {made.Code}; (void)({call.Code}); {held}; }})", made.Type, true);
  }

  // t!: a handle has one owner
  void CheckTaskCopy(Node n, Scope scope) {
    if (TasksMode && !LibraryCode && n.Children[0] is { Kind: "Name" or "Member" or "Index" } && PlaceType(n.Children[0], scope) is "Task" or "Thread") {
      throw Error("the handle of a task cannot be copied: it has one owner (spec 20.1)", n);
    }
  }

  // the handle of a thread never leaves its task
  void CheckThreadLeaves(Node given, Scope scope) {
    var value = given.Kind == "Named" ? given.Children[0] : given;
    while (value.Kind is "Gift" or "WritableAddressOf" or "CountedOf" or "WeakOf" && value.Children.Count > 0) {
      value = value.Children[0];
    }
    if (value.Kind is "Name" or "Member" or "Index" or "Call" && ExprOf(value, scope).Type == "Thread") {
      throw Error("the handle of a thread never leaves its task: only the parent waits on it (spec 20.1)", given);
    }
  }

  // wait on a Task or a Thread: the code has the handle as its owner, or with &
  void CheckCanWait(Node target, Expr waited, Scope scope) {
    if (HoistTyping) {
      return;
    }
    taskWaits++;
    if (LibraryCode || waited.Fresh) {
      return;   // wait Task(X).start() timeout n
    }
    var root = target;
    while (root.Kind is "Member" or "Index" or "Paren" && root.Children.Count > 0) {
      root = root.Children[0];
    }
    string kind = waited.Type == "Thread" ? "thread" : "task";
    if (root.Kind == "Name" && scope.Find(root.Text!) is VarInfo held) {
      if (taskAliases.TryGetValue(held, out var source)) {
        CheckCanWait(source, waited, scope);   // if t is Task<MyTask> mt: mt is t
        return;
      }
      if (target.Kind == "Name" && (held.IsCounted || held.IsWeak)) {
        throw Error($"a counted or weak reference cannot be waited on: only the owner of the {kind} waits (spec 20.2.6)", target);
      }
      if (held.ReadOnlyRef || held.IsParam && !held.RefSlot && !held.Own) {
        throw Error($"'{root.Text}' only lends its {kind}: it is lent with & to be waited on (&{DisplayName(waited.Type)} {root.Text}, spec 20.2.6)", target);
      }
      return;
    }
    if (root.Kind == "Name" && GlobalNamed(root, scope) is GlobalInfo global) {
      if (!enforcedNames.Contains(root.Text!)) {
        throw Error($"'{global.Name}' is a global: it is waited on inside enforce {root.Text} {{ }} (spec 20.2.6)", target);
      }
      return;
    }
    if (currentFun != null && waited.Type == "Task") {
      sharedTaskWaits.Add((currentFun, target.Text ?? "t", target));   // a field, a top-level variable: not for a thread (CheckTaskAccess)
    }
  }

  // how the code has the handle, for the runtime
  string TaskHeld(Node target, Expr waited, Scope scope) {
    if (waited.Type is not ("Task" or "Thread")) {
      return "0";
    }
    if (waited.Fresh) {
      return "DF_HELD_OWNED";
    }
    if (target.Kind == "Name" && scope.Find(target.Text!) is VarInfo held) {
      if (taskAliases.TryGetValue(held, out var source)) {
        return TaskHeld(source, waited, scope);
      }
      if (held.IsParam && held.RefSlot) {
        return "DF_HELD_LENT";
      }
      if (held.Own) {
        return "DF_HELD_OWNED";
      }
    }
    return "DF_HELD_ELSEWHERE";
  }

  // Thread(f, &t): a second lend with no wait at all in between, in the same block or in one inside it
  void NoteTaskLent(Node arg, Scope scope) {
    var target = arg.Kind == "WritableAddressOf" ? arg.Children[0] : arg;
    if (target.Kind != "Name" || scope.Find(target.Text!) is not VarInfo task || typingOnly || HoistTyping || frames.Count == 0) {
      return;
    }
    if (lentTasks.TryGetValue(target.Text!, out var lent) && lent.Task == task && lent.Waits == taskWaits) {
      for (var frame = frames.Peek(); frame != null; frame = frame.Parent) {
        if (frame == lent.Frame) {
          throw Error($"'{target.Text}' is lent already: a task has one borrower at a time (spec 20.2.6)", arg);
        }
      }
    }
    lentTasks[target.Text!] = (task, frames.Peek(), taskWaits);
  }
}
