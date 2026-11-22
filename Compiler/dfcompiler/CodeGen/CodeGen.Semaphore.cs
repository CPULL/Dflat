using System.Text;
using dfparser;

namespace dfcompiler;

// Semaphore (spec 20.14): semaphore name [= init] [timeout t]. A counter of permits (df_sem in the runtime), 1 when no value is written;
// 0 is a compile error (nobody could ever take it). It is used with enforce: enforce name { } takes one permit for the block, enforce
// name.take(n) { } takes n; they come back when the block ends. A busy semaphore follows the rules of every lock of enforce (7.2.2): the
// timeout of the enforce, else the one of the semaphore, else one try; then lockfail, default or the skip of the block. Read by its name
// it is an uns, the permits free now; it is never assigned.
public sealed partial class CodeGen {
  readonly Dictionary<string, string> semaphoreReads = new();   // the code that reads a semaphore -> its lock (df_semN)
  readonly Dictionary<string, string> topSemaphores = new();    // a semaphore at the top level of the program (functions see it) -> its lock
  readonly HashSet<Node> semaphoreDone = new(ReferenceEqualityComparer.Instance);

  // the lock of a semaphore declaration: its start value and its timeout; null timeout code when it is computed when the line runs
  (string Lock, string? ComputedTimeout) DeclareSemaphore(Node s, Scope? scope, bool top) {
    if (!TasksMode) {
      throw Error("a semaphore is part of Root.Tasks: it needs import Root.Tasks (spec 20.0, 20.14)", s);
    }
    long permits = 1;
    if (s.Children.FirstOrDefault(c => c.Kind == "Init") is Node init) {
      if (init.Children[0] is not { Kind: "Int" } count || !long.TryParse(count.Text, out permits)) {
        throw Error("the permits of a semaphore are a whole number: semaphore pool = 12 (spec 20.14)", init);
      }
      if (permits == 0) {
        throw Error("a semaphore with 0 permits can never be taken (a deadlock): give it 1 or more (spec 20.14)", init);
      }
    }
    long ms = 0;
    string? computed = null;
    if (s.Children.FirstOrDefault(c => c.Kind == "Timeout") is Node timeout) {
      if (timeout.Children[0] is { Kind: "Int" } literal && long.TryParse(literal.Text, out ms)) {
      } else if (top) {
        throw Error("the timeout of a semaphore at the top level of the program is a number of milliseconds: timeout 1500 (spec 20.14)", timeout);
      } else {
        var wait = ExprOf(timeout.Children[0], scope!, "lapse");
        if (!IsNumeric(wait.Type) && wait.Type != "lapse") {
          throw Error($"the timeout of a semaphore is milliseconds or a lapse, not {DisplayName(wait.Type)} (spec 20.14)", timeout);
        }
        computed = Convert(wait, "lapse", timeout);
      }
    }
    string lockName = "df_sem" + semaphoreReads.Count;
    typeDefs.Append($"static df_sem {lockName} = {{ {ms}, {permits} }};   /* the semaphore {s.Text} (spec 20.14) */\n");
    semaphoreReads[$"((df_uns)DF_ATOMIC_LOAD({lockName}.permits))"] = lockName;
    return (lockName, computed);
  }

  // the top-level semaphores: known by name before the functions are compiled
  void DeclareTopSemaphores(Node program) {
    foreach (var s in program.Children.Where(c => c.Kind == "Semaphore" && c.Line < LibraryLine)) {
      string name = s.Text!;
      if (globalConsts.ContainsKey(name) || globals.ContainsKey(name) || topSemaphores.ContainsKey(name)) {
        throw Error($"'{name}' is already declared in this scope", s);
      }
      var (lockName, _) = DeclareSemaphore(s, null, true);
      topSemaphores[name] = lockName;
      globalConsts[name] = ($"((df_uns)DF_ATOMIC_LOAD({lockName}.permits))", NativeUns);   // read: the permits free now
      semaphoreDone.Add(s);
    }
  }

  // a semaphore declared in a function or a block
  void EmitSemaphore(Node s, StringBuilder sb, Scope scope, string pad) {
    if (semaphoreDone.Contains(s)) {
      return;   // a top-level one: made at program start
    }
    if (scope.Vars.ContainsKey(s.Text!)) {
      throw Error($"'{s.Text}' is already declared in this scope", s);
    }
    var (lockName, computed) = DeclareSemaphore(s, scope, false);
    if (computed != null) {
      sb.Append($"{pad}{lockName}.timeout = df_task_lapse_ms({computed}) < 0 ? 0 : df_task_lapse_ms({computed});\n");
    }
    scope.Vars[s.Text!] = new VarInfo(NativeUns, $"((df_uns)DF_ATOMIC_LOAD({lockName}.permits))", true);
  }

  // the lock of a semaphore named in an enforce; null when the name is not one
  string? SemaphoreLockOf(Node name, Scope scope) {
    if (scope.Find(name.Text!) is VarInfo variable) {
      return semaphoreReads.TryGetValue(variable.CName, out var local) ? local : null;
    }
    return topSemaphores.TryGetValue(name.Text!, out var top) ? top : null;
  }

  // enforce pool { } / enforce pool.take(n) { }: the check that takes the permits and the code that gives them back
  (string Flag, string Check, string Release) SemaphoreLock(Node name, string semLock, Scope scope) {
    string flag = "df_lk" + NewTemp();
    string count = "df_sc" + NewTemp();
    fn!.Decls.Append($"  bool {flag} = false;\n  int64_t {count} = 1;\n  (void){flag};\n  (void){count};\n");
    string ms = "-1";
    if (name.Children.FirstOrDefault(child => child.Kind == "Timeout") is Node timeout) {
      var wait = ExprOf(timeout.Children[0], scope, "i64");
      if (!IsInt(wait.Type)) {
        throw Error($"timeout takes a whole number of milliseconds, not {wait.Type}", timeout);
      }
      ms = wait.Code;
    }
    string countCode = "1";
    if (name.Children.FirstOrDefault(child => child.Kind == "Take") is Node take) {
      if (take.Children[0] is { Kind: "Int" } literal && long.TryParse(literal.Text, out long n) && n <= 0) {
        throw Error("take(n) takes 1 or more permits (spec 20.14)", take);
      }
      var taken = ExprOf(take.Children[0], scope, "i64");
      if (!IsInt(taken.Type)) {
        throw Error($"take(n) takes a whole number of permits, not {DisplayName(taken.Type)} (spec 20.14)", take);
      }
      countCode = taken.Code;
    }
    return (flag, $"({count} = (int64_t)({countCode}), {flag} = df_sem_take(&{semLock}, {count}, (int64_t)({ms})))", $"df_sem_give(&{semLock}, {count})");
  }
}
