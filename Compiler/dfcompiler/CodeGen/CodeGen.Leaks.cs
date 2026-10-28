using dfparser;

namespace dfcompiler;

// Which exceptions can LEAVE a function (see "EXCEPTIONS: THE MODEL" in CompilerImplementation.txt). A function that no non-silent
// exception can leave is pure or safe: it returns only its result and its callers do not check it. An exception that is fully handled
// inside the function (every point that can raise it is covered by a catch that handles it, and the catch bodies throw nothing) does
// not leave it. Silent exceptions are not part of this analysis yet: they are raised only where a catch of the same function handles them.
public sealed partial class CodeGen {
  const int AnyException = 10;   // the id of Exception: "some non-silent exception, we do not know which"
  const int SilentLeak = -3;     // in the set of what leaves a function: it returns a silent exception (it never goes on to a caller's caller)

  // the catches that cover a point of the code: one list of catch targets for every enclosing block that has catches
  // after the statement (null = catch all, -2 = the SilentException trait, otherwise an exception id)
  sealed class Cover {
    public static readonly Cover None = new(new List<List<int?>>());

    public Cover(List<List<int?>> levels) {
      Levels = levels;
    }

    public List<List<int?>> Levels { get; }

    public Cover With(List<int?> level) {
      var more = new List<List<int?>>(Levels) { level };
      return new Cover(more);
    }
  }

  sealed class FunFlow {
    public List<(int Id, Cover Cover)> Raises { get; } = new();
    // throws of silent exceptions (-1: a catch variable that may hold any silent exception)
    public List<(int Id, Cover Cover)> Silent { get; } = new();
    public List<(HashSet<string> Names, Cover Cover)> Calls { get; } = new();
  }

  sealed class FlowContext {
    public FlowContext(FunFlow flow, Cover cover, bool inCatchBody, Dictionary<string, int>? catchVars = null) {
      Flow = flow;
      Cover = cover;
      InCatchBody = inCatchBody;
      CatchVars = catchVars ?? new Dictionary<string, int>();
    }

    // the variables of the catches we are inside, with the exception they hold (-2: a silent one)
    public Dictionary<string, int> CatchVars { get; }

    public FunFlow Flow { get; }
    public Cover Cover { get; }
    public bool InCatchBody { get; }     // inside a catch body only a throw leaves; any other error is a panic (see EmitCatch)
  }

  // the parent of an exception id (null for Exception itself)
  int? ParentExceptionId(int id) {
    if (id == AnyException) {
      return null;
    }
    var name = customExceptionIds.FirstOrDefault(pair => pair.Value == id).Key;
    if (name != null && classes.TryGetValue(name, out var cls) && cls.Parent != null && customExceptionIds.TryGetValue(cls.Parent.Name, out int parent)) {
      return parent;
    }
    return AnyException;
  }

  // does a catch for this target handle that exception? The root Exception handles silent exceptions too; the SilentException trait
  // (-2) handles only the silent ones.
  bool CatchHandles(int? target, int exception, bool silent = false) {
    if (target == null) {
      return true;
    }
    if (target == -2) {
      return silent;
    }
    if (silent && target == AnyException) {
      return false;   // catch Exception (and the bare catch ex) do not handle silent exceptions (spec 15.1, 15.4, 15.8.2)
    }
    for (int? current = exception; current != null; current = ParentExceptionId(current.Value)) {
      if (current == target) {
        return true;
      }
    }
    return false;
  }

  bool Covered(int exception, Cover cover, bool silent = false) {
    return cover.Levels.Any(level => level.Any(target => CatchHandles(target, exception, silent)));
  }

  bool IsSilentId(int id) {
    if (SilentStandard.Contains(id)) {
      return true;
    }
    var name = customExceptionIds.FirstOrDefault(pair => pair.Value == id).Key;
    return name != null && classes.TryGetValue(name, out var cls) && IsSilentClass(cls);
  }

  // ids that a throw statement can raise; none for Panic; the silent ones are recorded apart (they leave a function only when a
  // caller observes them, see ComputeSilentEmitters)
  void FlowThrow(Node node, FlowContext ctx) {
    var value = node.Children[0];
    int id = AnyException;
    if (value.Kind == "Name" && ctx.CatchVars.TryGetValue(value.Text!, out int held)) {
      if (held == -2) {
        ctx.Flow.Silent.Add((-1, ctx.Cover));   // the variable of a catch SilentException holds a silent exception
        return;
      }
      if (IsSilentId(held)) {
        ctx.Flow.Silent.Add((held, ctx.Cover));
        return;
      }
      if (held == AnyException) {
        ctx.Flow.Silent.Add((-1, ctx.Cover));   // catch Exception e, catch all: it may hold a silent exception too
      }
      id = held;
    } else if (value.Kind == "Call") {
      string? path = Path(value.Children[0]);
      if (path == "Panic") {
        return;
      }
      if (path != null) {
        string resolved = ResolveCatchName(path);
        int? known = ExceptionId(resolved);
        if (known is int found && found >= 0) {
          if (IsSilentId(found)) {
            ctx.Flow.Silent.Add((found, ctx.Cover));
            return;
          }
          id = found;
        }
      }
    }
    ctx.Flow.Raises.Add((id, ctx.Cover));
  }

  void FlowNode(Node node, FlowContext ctx) {
    switch (node.Kind) {
      case "Block":
        FlowList(node.Children, ctx);
        return;
      case "Catch":
        return;   // handled by the list that holds it
      case "Throw":
        FlowThrow(node, ctx);
        break;
      case "Binary":
        if (!ctx.InCatchBody && node.Text is "/" or "%") {
          ctx.Flow.Raises.Add((StandardExceptions["DivisionByZeroException"], ctx.Cover));
        } else if (!ctx.InCatchBody && node.Text is "==" or "!=" && hasObjectEquality) {
          ctx.Flow.Raises.Add((AnyException, ctx.Cover));
        }
        break;
      case "Assign":
        if (!ctx.InCatchBody && node.Text is "/=" or "%=") {
          ctx.Flow.Raises.Add((StandardExceptions["DivisionByZeroException"], ctx.Cover));
        } else if (!ctx.InCatchBody && node.Text is "+=" or "-=" && FlowTargetIsCollection(node.Children[0], currentFlowFun)) {
          // c += x calls add, c -= x calls removeAt on a map and remove on a set (CollectionOperator)
          ctx.Flow.Calls.Add((node.Text == "+=" ? new HashSet<string> { "?.add" } : new HashSet<string> { "?.removeAt", "?.remove" }, ctx.Cover));
        }
        break;
      case "Index":
        if (!ctx.InCatchBody && node.Children.Count == 2 && node.Children[0].Kind != "GenericRef" && node.Children[1].Kind != "Empty") {
          ctx.Flow.Raises.Add((StandardExceptions["IndexOutOfRangeException"], ctx.Cover));
        }
        break;
      case "Member":
        if (!ctx.InCatchBody && MemberMayRaise(node)) {
          ctx.Flow.Raises.Add((StandardExceptions["NullPointerException"], ctx.Cover));
        }
        break;
      case "Call":
        if (!ctx.InCatchBody) {
          var names = new HashSet<string>();
          CollectCallNames(node, names);
          ctx.Flow.Calls.Add((names, ctx.Cover));
        }
        break;
    }
    foreach (var child in node.Children) {
      FlowNode(child, ctx);
    }
  }

  // the function names a call can reach (the same candidates as ContainsRaise)
  void CollectCallNames(Node call, HashSet<string> called) {
    var callee = call.Children[0];
    var prefixes = new List<string>();
    if (currentFlowFun?.EnumName != null) {
      prefixes.Add(currentFlowFun.EnumName);
    }
    for (var c = currentFlowFun?.Class; c != null; c = c.Parent) {
      prefixes.Add(c.Name);
    }
    if (callee.Kind == "Name") {
      called.Add(callee.Text!);
      called.Add("?." + callee.Text);
      foreach (string prefix in prefixes) {
        called.Add(prefix + "." + callee.Text);
      }
    } else if (callee.Kind == "GenericRef" && callee.Children[0] is { Kind: "Name" } genericCallee) {
      called.Add(genericCallee.Text!);
    } else if (Path(callee) is string path) {
      called.Add(path);
    }
    if (callee.Kind == "Member") {
      called.Add("?." + callee.Text);
    }
  }

  FunSig? currentFlowFun;

  // statements of one block: a statement is covered by the catches written after it in the same block; the catch bodies and the
  // atEnd blocks are not covered by those catches (an exception from them goes to the enclosing level)
  void FlowList(List<Node> statements, FlowContext ctx) {
    var targets = new List<(int Index, int? Target)>();
    for (int index = 0; index < statements.Count; index++) {
      if (statements[index].Kind == "Catch") {
        targets.Add((index, CatchTarget(statements[index]).Id));
      }
    }
    for (int index = 0; index < statements.Count; index++) {
      var statement = statements[index];
      if (statement.Kind == "Catch") {
        var body = statement.Children.FirstOrDefault(child => child.Kind == "Block");
        if (body != null) {
          var (target, variable) = CatchTarget(statement);
          var vars = new Dictionary<string, int>(ctx.CatchVars);
          if (variable != null) {
            vars[variable] = target ?? AnyException;
          }
          FlowList(body.Children, new FlowContext(ctx.Flow, ctx.Cover, true, vars));
        }
        continue;
      }
      if (statement.Kind == "AtEnd") {
        // an atEnd block has no error part: what it raises is a panic, not something that leaves the function
        foreach (var child in statement.Children) {
          FlowNode(child, new FlowContext(new FunFlow(), Cover.None, true, ctx.CatchVars));
        }
        continue;
      }
      var after = targets.Where(entry => entry.Index > index).Select(entry => entry.Target).ToList();
      FlowNode(statement, after.Count == 0 ? ctx : new FlowContext(ctx.Flow, ctx.Cover.With(after), ctx.InCatchBody, ctx.CatchVars));
    }
  }

  // the exceptions each function lets out; failingFuns becomes the functions that let some out
  void FindFailingFunctions() {
    var allFuns = funs.Values.SelectMany(overloads => overloads).ToList();
    var flows = new Dictionary<FunSig, FunFlow>();
    foreach (var fun in allFuns) {
      var flow = new FunFlow();
      currentFlowFun = fun;
      if (!fun.IsAbstract && !fun.IsExtern) {
        FlowNode(fun.Node, new FlowContext(flow, Cover.None, false));
      }
      flows[fun] = flow;
    }
    currentFlowFun = null;

    var leaks = allFuns.ToDictionary(fun => fun, _ => new HashSet<int>());
    IEnumerable<FunSig> Resolve(string name) {
      if (name.StartsWith("?.")) {
        return allFuns.Where(other => other.Class != null && other.Name.EndsWith("." + name[2..]));
      }
      if (funs.TryGetValue(name, out var overloads)) {
        return overloads;
      }
      return Array.Empty<FunSig>();
    }

    bool changed = true;
    while (changed) {
      changed = false;
      foreach (var fun in allFuns) {
        var set = leaks[fun];
        void Add(int exception) {
          if (set.Add(exception)) {
            changed = true;
          }
        }
        var flow = flows[fun];
        foreach (var (id, cover) in flow.Raises) {
          if (!Covered(id, cover)) {
            Add(id);
          }
        }
        // a silent throw that no catch of the function handles leaves it, like any throw (the caller decides what to do with it)
        if (fun.CanEmitSilent) {
          foreach (var (id, cover) in flow.Silent) {
            if (id == -1 || !Covered(id, cover, true)) {
              Add(SilentLeak);
            }
          }
        }
        foreach (var (names, cover) in flow.Calls) {
          foreach (string name in names) {
            // a generic function, or the constructor of a generic class: copies are made later, so anything can come out
            if (genericFuns.ContainsKey(name) || (genericClasses.ContainsKey(name))) {
              if (!Covered(AnyException, cover)) {
                Add(AnyException);
              }
              continue;
            }
            foreach (var callee in Resolve(name)) {
              foreach (int exception in leaks[callee].Where(leaked => leaked != SilentLeak).ToList()) {
                if (!Covered(exception, cover)) {
                  Add(exception);
                }
              }
            }
          }
        }
        // a constructor runs its init function; an init function runs the init function of its parent
        if (fun.IsCtor && fun.Class!.Init != null) {
          foreach (int exception in leaks[fun.Class.Init].Where(leaked => leaked != SilentLeak).ToList()) {
            Add(exception);
          }
        }
        if (fun.IsInit && fun.Class!.Parent?.Init != null) {
          foreach (int exception in leaks[fun.Class.Parent.Init].Where(leaked => leaked != SilentLeak).ToList()) {
            Add(exception);
          }
        }
      }
      // functions of classes and traits with the same name and parameters can be called through the same pointer (virtual slots,
      // trait functions): they must all return their exception or none: they share what they let out
      foreach (var group in allFuns.Where(f => f.Class != null && !f.IsStatic && !f.IsCtor && !f.IsInit && !f.IsDtor && !f.IsAccessor)
          .GroupBy(f => (f.Name[(f.Name.LastIndexOf('.') + 1)..], string.Join(",", f.Params.Select(p => p.Type))))
          .Concat(allFuns.Where(f => f.Class != null && !f.IsStatic && !f.IsCtor && !f.IsInit && !f.IsDtor && f.Slot >= 0 && f.Root.GroupSize > 0)
            .GroupBy(f => (f.Root.Name, "slot")))) {
        var shared = new HashSet<int>(group.SelectMany(member => leaks[member]));
        foreach (var member in group) {
          foreach (int exception in shared) {
            if (leaks[member].Add(exception)) {
              changed = true;
            }
          }
        }
      }
    }
    failingFuns.Clear();
    foreach (var fun in allFuns) {
      if (leaks[fun].Count > 0 && !fun.IsDtor && !fun.IsAccessor) {   // a destructor (an atEnd of a class) and an accessor: what they raise is a panic
        failingFuns.Add(fun);
      }
    }
    ComputeSilentEmitters(allFuns, flows);
  }

  // ---------------------------------------------------------------- silent exceptions (stage 2)

  public Node? ProgramNode { get; set; }

  // the bit of the exception being thrown in the observed mask (0 for a kind nobody handles)
  string ObservedBitTable() {
    if (silentBit.Count == 0) {
      return "";
    }
    var sb = new System.Text.StringBuilder("static uint64_t df_obs_bit(const df_exception* e) {\n  switch (e->id) {\n");
    foreach (var (id, bit) in silentBit) {
      sb.Append($"    case {id}: return UINT64_C({1UL << bit});\n");
    }
    sb.Append("  }\n  return 0;\n}\n\n");
    return sb.ToString();
  }

  readonly Dictionary<int, int> silentBit = new();      // live silent exception id → its bit in the observed mask

  // The silent exceptions that some catch of the program handles are LIVE: each gets a bit. A call site builds the mask of the kinds the
  // catches around it handle (ObservedMask) and ignores any other silent exception that the callee returned (SilentIgnored).
  void ComputeSilentEmitters(List<FunSig> allFuns, Dictionary<FunSig, FunFlow> flows) {
    silentBit.Clear();
    var targets = new List<int?>();
    void Collect(Node node) {
      if (node.Kind == "Catch") {
        targets.Add(CatchTarget(node).Id);
      }
      foreach (var child in node.Children) {
        Collect(child);
      }
    }
    if (ProgramNode != null) {
      Collect(ProgramNode);
    }
    var kinds = SilentStandard.Concat(customExceptionIds.Where(pair => IsSilentId(pair.Value)).Select(pair => pair.Value)).Distinct().OrderBy(id => id);
    foreach (int id in kinds) {
      if (targets.Any(target => CatchHandles(target, id, true))) {
        if (silentBit.Count == 64) {
          throw Error("more than 64 silent exception classes are handled by catches: the observed mask has 64 bits", ProgramNode!);
        }
        silentBit[id] = silentBit.Count;
      }
    }
  }
}
