using System.Text;
using dfparser;

namespace dfcompiler;

// Mutex (spec 20.13): mutex <type> <name> = <value> timeout <milliseconds>. The variable is a plain variable of its type, with a lock of its
// own (df_mtx in the runtime, recursive: the thread that holds it takes it again at once). It is read and written only inside enforce name
// { }, which takes the lock: the timeout of the enforce, else the one of the mutex; lockfail, default and the skip of the block are the ones
// of every lock of enforce (7.2.2). An enforce m written inside enforce m is a compile error.
public sealed partial class CodeGen {
  readonly Dictionary<Node, Node> mutexDecls = new(ReferenceEqualityComparer.Instance);   // a mutex declaration -> its timeout
  readonly Dictionary<string, string> mutexLocks = new();   // the C name of a mutex variable -> its lock
  readonly HashSet<string> activeMutexes = new();           // the locks of the enforce blocks around the code being compiled
  readonly Dictionary<Node, string> mutexDeclLocks = new(ReferenceEqualityComparer.Instance);   // a mutex declaration -> its lock
  readonly HashSet<Node> mutexStaticTimeouts = new(ReferenceEqualityComparer.Instance);   // a mutex whose timeout is a number: in the C of its lock
  readonly Dictionary<string, string> topMutexes = new();   // a mutex written at the top level of the program (functions see it) -> its lock

  // before the code: a mutex declaration keeps its timeout aside; the declaration itself is a plain one
  void LowerMutexDecls(Node node, bool top = true) {
    foreach (var child in node.Children) {
      LowerMutexDecls(child, false);
      if (top && mutexDeclLocks.TryGetValue(child, out var topLock)) {
        if (!mutexStaticTimeouts.Contains(child)) {
          throw Error("the timeout of a mutex at the top level of the program is a number of milliseconds: timeout 1000 (spec 20.13)", mutexDecls[child]);
        }
        topMutexes[child.Text!] = topLock;   // the functions are compiled before the top-level code: they know it by its name
      }
    }
    if (node.Kind != "Decl") {
      return;
    }
    var mods = node.Children.FirstOrDefault(c => c.Kind == "Mods");
    var timeout = node.Children.FirstOrDefault(c => c.Kind == "Timeout");
    bool mutex = mods?.Text?.Split(' ').Contains("mutex") == true;
    if (!mutex) {
      if (timeout != null) {
        throw Error("a timeout in a declaration is for a mutex: mutex i32 counter = 0 timeout 1000 (spec 20.13)", timeout);
      }
      return;
    }
    if (!TasksMode) {
      throw Error("a mutex is part of Root.Tasks: it needs import Root.Tasks (spec 20.0, 20.13)", node);
    }
    if (timeout == null) {
      throw Error("a mutex needs its timeout, in milliseconds above 0: mutex i32 counter = 0 timeout 1000 (spec 20.13)", node);
    }
    if (!node.Children.Any(c => c.Kind == "Init")) {
      throw Error("a mutex needs its value: mutex i32 counter = 0 timeout 1000 (spec 20.13)", node);
    }
    if (timeout.Children[0] is { Kind: "Int" } literal && long.TryParse(literal.Text, out long ms) && ms <= 0) {
      throw Error("the timeout of a mutex is above 0; enforce m timeout 0 { } tries once (spec 20.13)", timeout);
    }
    var others = mods!.Text!.Split(' ').Where(mod => mod != "mutex").ToList();
    if (others.Contains("const")) {
      throw Error("a mutex is changed under its lock: it cannot be const (spec 20.13)", node);
    }
    if (others.Count == 0) {
      node.Children.Remove(mods);
    } else {
      mods.Text = string.Join(" ", others);
    }
    node.Children.Remove(timeout);
    mutexDecls[node] = timeout;
    string lockName = "df_mtx" + mutexDeclLocks.Count;
    mutexDeclLocks[node] = lockName;
    long staticMs = 0;
    if (timeout.Children[0] is { Kind: "Int" } number && long.TryParse(number.Text, out staticMs)) {
      mutexStaticTimeouts.Add(node);
    }
    typeDefs.Append($"static df_mtx {lockName} = {{ {staticMs}, 0, 0 }};   /* the lock of the mutex {node.Text} (spec 20.13) */\n");
  }

  // mutex T name = v timeout ms: the variable, then its lock with the timeout
  void EmitMutexDecl(Node s, Node timeout, StringBuilder sb, Scope scope, string pad) {
    var ms = ExprOf(timeout.Children[0], scope, "i64");
    if (!IsInt(ms.Type)) {
      throw Error($"the timeout of a mutex is a whole number of milliseconds, not {DisplayName(ms.Type)} (spec 20.13)", timeout);
    }
    EmitDecl(s, sb, scope, pad);
    string cname = scope.Vars.TryGetValue(s.Text!, out var variable) ? variable.CName
      : globalConsts.TryGetValue(s.Text!, out var global) ? global.CName
      : throw Error($"'{s.Text}' cannot be a mutex here", s);
    string lockName = mutexDeclLocks[s];
    mutexLocks[cname] = lockName;
    if (!mutexStaticTimeouts.Contains(s)) {
      sb.Append($"{pad}{lockName}.timeout = (int64_t)({ms.Code});\n");   // a timeout computed when the declaration runs
    }
  }

  // the lock of a mutex variable named in an enforce; null when the name is not a mutex
  string? MutexLockOf(Node name, Scope scope) {
    if (scope.Find(name.Text!) is VarInfo variable) {
      return mutexLocks.TryGetValue(variable.CName, out var localLock) ? localLock : null;
    }
    return globalConsts.ContainsKey(name.Text!) && topMutexes.TryGetValue(name.Text!, out var topLock) ? topLock : null;
  }

  // a mutex is read and written only inside its enforce (spec 20.13)
  void CheckMutexUse(string cname, string name, Node at, bool top = false) {
    if (mutexDeclLocks.Keys.Any(decl => decl.Line == at.Line && decl.Text == name)) {
      return;   // the declaration gives the mutex its first value (a top-level one is set by the start of the program)
    }
    if ((mutexLocks.TryGetValue(cname, out var lockName) || top && topMutexes.TryGetValue(name, out lockName)) && !activeMutexes.Contains(lockName)) {
      throw Error($"'{name}' is a mutex: it is used only inside enforce {name} {{ }} (spec 20.13)", at);
    }
  }
}
