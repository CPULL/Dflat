using System.Text;
using dfparser;

namespace dfcompiler;

// How exceptions travel (see "EXCEPTIONS: THE MODEL" in CompilerImplementation.txt).
// A function that can let a (non-silent) exception leave it returns two things: its result and the exception (null when there is none).
// Inside a function the exception is the local df_err. The C runtime cannot return it that way: it leaves it in df_pending, and the code
// generated around the call moves it into df_err at once. Destructors (called by the runtime) also leave theirs in df_pending.
public sealed partial class CodeGen {
  readonly Dictionary<string, string> resultPairs = new();   // C type of the result → the struct that carries it with the exception

  // functions whose signature carries the exception: the others (pure and safe ones) return their result only
  bool PairReturn(FunSig f) => failingFuns.Contains(f) && !f.IsDtor && !f.IsAccessor;

  string ResultPair(string cType) {
    if (!resultPairs.TryGetValue(cType, out var name)) {
      name = "df_res" + resultPairs.Count;
      resultPairs[cType] = name;
      typeDefs.Append($"typedef struct {{\n  {cType} value;\n  df_exception* err;\n}} {name};\n\n");
    }
    return name;
  }

  // the C type a function returns
  string RetCType(FunSig f) {
    string cType = CType(f.Ret);
    if (!PairReturn(f)) {
      return cType;
    }
    return f.Ret == "void" ? "df_exception*" : ResultPair(cType);
  }

  // return the result with no exception
  string PairValue(FunSig f, string value) {
    return f.Ret == "void" ? "NULL" : $"({ResultPair(CType(f.Ret))}){{ {value}, NULL }}";
  }

  // return the exception held in the local df_err (the result is the zero value)
  string PairError(FunSig f) {
    return f.Ret == "void" ? "df_err" : $"({ResultPair(CType(f.Ret))}){{ {ZeroValue(f.Ret)}, df_err }}";
  }

  // a call of a Dflat function: look at the exception it returned and go to the handler of this scope when there is one. A silent
  // exception that no catch around the call handles is ignored here (spec 15.4): the call goes on with the value the function returned
  string PairCheck(string call, string type) {
    string temp = NewTemp();
    if (type == "void") {
      return $"({{ df_exception* {temp} = {call}; if ({temp}) {{ if ({SilentIgnored(temp)}) {{ df_exc_release({temp}); {temp} = NULL; }} else {{ df_err = {temp}; df_err_thrown = false; goto {Handler}; }} }} }})";
    }
    return $"({{ {ResultPair(CType(type))} {temp} = {call}; if ({temp}.err) {{ if ({SilentIgnored(temp + ".err")}) {{ df_exc_release({temp}.err); {temp}.err = NULL; }} else {{ df_err = {temp}.err; df_err_thrown = false; goto {Handler}; }} }} {temp}.value; }})";
  }

  // the code of a call to the user function f: checked the way its signature says
  string UserCallCode(FunSig f, string call) {
    if (PairReturn(f)) {
      return PairCheck(call, f.Ret);
    }
    return failingFuns.Contains(f) ? CheckRaise(call, f.Ret) : call;   // a destructor leaves its exception in df_pending
  }
}
