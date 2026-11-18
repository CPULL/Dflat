using System.Numerics;
using System.Text;
using dfparser;

namespace dfcompiler;

// Variadic natives (spec 22): native i32 printf(*char format, CArg args...) takes, for its C '...', the arguments one by one, each of a
// type of the set CArg. C needs the types at each call, so each shape of a call (the types of its extra arguments) gets its own wrapper,
// which calls the C function with plain C arguments (clang applies the C rules: float to double, small integers to int). A mapping that
// gives its own variadic parameter on to a native (fun i32 n print(string format, CArg args...) -> n = printf(format, args)) gets a copy
// for each shape too, where args is the list of its arguments.
public sealed partial class CodeGen {
  readonly Dictionary<string, FunSig> variadicShapes = new();   // "<function> <types>" -> the copy for that shape
  readonly StringBuilder variadicWrappers = new();

  // a native, or a mapping that gives its variadic parameter on to a native
  static bool IsCVariadic(FunSig f) =>
    f.Params.Count > 0 && f.Params[^1].IsVariadic && (f.IsExtern || f.Node.Children.Any(child => child.Kind == "CVariadicForward"));

  // the types a C variadic part accepts: the members of its set, or its one type
  List<string> CVariadicTypes(ParamSig param, Node at) {
    string elem = ArrElem(param.Type);
    return IsSetName(elem) ? SetMembers(elem).Select(Normalize).ToList() : new List<string> { Normalize(elem) };
  }

  // the declaration: each accepted type is a number, bool, addr or string
  void CheckCVariadicParam(ParamSig param, Node at) {
    foreach (string type in CVariadicTypes(param, at)) {
      if (!(IsNumeric(type) || type is "bool" or "addr" or "string")) {
        throw Error($"the '...' of a native takes numbers, bool, addr and string, not {DisplayName(type)} (spec 22)", at);
      }
    }
  }

  // a call of a C variadic function: the copy for the shape of its extra arguments, called with them one by one
  Expr? CVariadicCall(FunSig f, List<Node> args, Scope scope, Node at, string? self) {
    if (!IsCVariadic(f) || f.CVariadicFixed != null) {
      return null;
    }
    var (slots, problem) = MapArgs(f, args);
    if (slots == null) {
      throw Error(problem!, at);
    }
    var variadic = f.Params[^1];
    var extra = slots[^1] is { Kind: "List" } list ? list.Children.ToList() : new List<Node>();
    if (slots[^1] != null && slots[^1]!.Kind != "List") {
      throw Error($"the '...' of {f.Name} takes the values one by one, not a collection (spec 22)", slots[^1]!);
    }
    var accepted = CVariadicTypes(variadic, at);
    var types = new List<string>();
    foreach (var item in extra) {
      string type = CVariadicType(item, accepted, scope);
      if (IsCollectionType(type)) {
        throw Error($"the '...' of {f.Name} takes the values one by one, not a collection ({DisplayName(type)}) (spec 22)", item);
      }
      if (!accepted.Contains(type)) {
        throw Error($"the '...' of {f.Name} takes {string.Join(", ", accepted.Select(DisplayName))}, not {DisplayName(type)} (spec 22)", item);
      }
      types.Add(type);
    }
    var shape = CVariadicShape(f, types, at);
    var call = new List<Node>();
    for (int index = 0; index < f.Params.Count - 1; index++) {
      if (slots[index] != null) {
        call.Add(Synthetic("Named", slots[index]!, f.Params[index].Name).Add(slots[index]!));
      }
    }
    for (int index = 0; index < extra.Count; index++) {
      call.Add(Synthetic("Named", extra[index], $"{variadic.Name}__{index}").Add(extra[index]));
    }
    return UserCall(shape, call, scope, at, self);
  }

  // an integer literal is an i32 when it fits (a C int), else an i64; a real literal is an r64 (a C double)
  string CVariadicType(Node item, List<string> accepted, Scope scope) {
    var literal = item.Kind == "Neg" ? item.Children[0] : item;
    if (literal.Kind == "Int" && IntLiteralValue(item) is BigInteger value) {
      return value >= int.MinValue && value <= int.MaxValue && accepted.Contains("i32") ? "i32" : "i64";
    }
    if (literal.Kind == "Real") {
      return "r64";
    }
    return Normalize(ExprOf(item, scope).Type);
  }

  // the copy of f for these types: the variadic parameter becomes one parameter per argument (name__0, name__1, ...)
  FunSig CVariadicShape(FunSig f, List<string> types, Node at) {
    string key = f.Name + " " + string.Join(",", types);
    if (variadicShapes.TryGetValue(key, out var known)) {
      return known;
    }
    var variadic = f.Params[^1];
    var copy = CloneNode(f.Node);
    copy.Text = $"{f.Name}__v{variadicShapes.Count}";
    copy.Children.RemoveAll(child => child.Kind == "CVariadicForward");
    var ps = copy.Children.First(child => child.Kind == "Params");
    ps.Children.RemoveAt(ps.Children.Count - 1);
    for (int index = 0; index < types.Count; index++) {
      ps.Add(Synthetic("Param", at, $"{variadic.Name}__{index}").Add(Synthetic("ResolvedType", at, types[index])));
    }
    if (!f.IsExtern) {
      ExpandVariadicUses(copy.Children.First(child => child.Kind == "Block"), variadic.Name, types.Count);
    }
    var sig = RegisterFun(copy, null)!;
    sig.CVariadicFixed = f.Params.Count - 1;
    variadicShapes[key] = sig;
    if (f.IsExtern) {
      variadicWrappers.Append(ExternDeclarations(sig));   // only the wrapper: the prototype with '...' is the one of f
    } else {
      sig.Gives.UnionWith(f.Gives);   // the template's results: a new value is given (InferGivenResults ran on it)
      failingFuns.Add(sig);
      Classify(sig, new Dictionary<FunSig, int>());
      pendingInstances.Add(sig);
    }
    return sig;
  }

  // inside the copy of a mapping: f(..., args) gives the arguments one by one; args cannot be used in any other way
  void ExpandVariadicUses(Node node, string name, int count) {
    for (int index = 0; index < node.Children.Count; index++) {
      var child = node.Children[index];
      if (node.Kind == "Args" && child.Kind == "Name" && child.Text == name) {
        node.Children.RemoveAt(index);
        for (int item = 0; item < count; item++) {
          node.Children.Insert(index + item, Synthetic("Name", child, $"{name}__{item}"));
        }
        index += count - 1;
        continue;
      }
      if (child.Kind == "Name" && child.Text == name) {
        throw Error($"'{name}...' of a mapping is only given on to a native, f(..., {name}) (spec 22)", child);
      }
      ExpandVariadicUses(child, name, count);
    }
  }

  // the type of an expression, or null when it does not compile (only to choose a path: the code is made again by the chosen one)
  string? TryExprType(Node n, Scope scope) {
    try {
      return ExprOf(n, scope).Type;
    } catch (CompileException) {
      return null;
    }
  }
}
