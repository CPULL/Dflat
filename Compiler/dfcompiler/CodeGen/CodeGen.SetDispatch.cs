using System.Text;
using dfparser;

namespace dfcompiler;

// A set value given to a parameter of the same set (spec 11.3.6): the call reads the tag of the value and calls the copy of the function
// for that member. The call becomes a type switch on the value, with one call for each member of the set.
public sealed partial class CodeGen {
  // the dispatch of writeValue(node.value) with fun writeValue(JsonType value); null when no argument is a set value of its parameter
  Expr? SetDispatch(Node n, Scope scope) {
    if (LibraryCode || n.Line >= LibraryLine || fn == null || n.Children.Count < 2 || n.Children[0].Kind is not ("Name" or "Member")
        || SetParamTemplate(n.Children[0], scope) is not GenericFun template) {
      return null;
    }
    var args = n.Children[1].Children;
    foreach (var (pattern, argument) in PatternPairs(template, args)) {
      if (pattern is not { Kind: "Type", Children.Count: 0 } || template.Params.FirstOrDefault(p => p.Name == pattern.Text) is not TypeParam param
          || !param.Name.StartsWith('_') || param.Constraints is not [{ Text: string set }]
          || argument.Kind is "Gift" or "WritableAddressOf" or "Lambda" or "List" or "MapLit" or "EmptyNew" or "Null" || IsLiteralNode(argument)) {
        continue;
      }
      string type = ExprOf(argument, scope).Type;
      if (!IsSetValue(type) || type != set) {
        continue;
      }
      int at = args.FindIndex(arg => arg == argument || arg.Kind == "Named" && arg.Children.Contains(argument));
      if (at < 0) {
        continue;
      }
      // what runs before the value: the object, and the arguments written before it (spec 2.5)
      var built = new List<Node>();
      var before = new StringBuilder();
      try {
        if (n.Children[0] is { Kind: "Member", Children.Count: > 0 } member && HasEffects(member.Children[0])) {
          before.Append(Prebuild(member.Children[0], scope, built));
        }
        foreach (var arg in args.Take(at)) {
          before.Append(Prebuild(arg.Kind == "Named" ? arg.Children[0] : arg, scope, built));
        }
        string bound = $"__set{++tempCounter}";
        var dispatch = Synthetic("TypeSwitch", n, "dispatch").Add(argument);
        foreach (string memberType in SetValueMembers(type, n)) {
          var call = Synthetic("Call", n).Add(n.Children[0]);
          var callArgs = Synthetic("Args", n.Children[1]);
          for (int index = 0; index < args.Count; index++) {
            var value = index == at ? Synthetic("Name", argument, bound) : args[index];
            callArgs.Add(index == at && args[index].Kind == "Named" ? Synthetic("Named", args[index], args[index].Text).Add(value) : value);
          }
          call.Add(callArgs);
          dispatch.Add(Synthetic("Arm", n, bound).Add(Synthetic("Values", n).Add(Synthetic("ResolvedType", n, memberType))).Add(call));
        }
        var result = ExprOf(dispatch, scope);
        return before.Length == 0 ? result : result with { Code = $"({{ {before}{result.Code}; }})" };
      } finally {
        built.ForEach(node => prebuilt.Remove(node));
      }
    }
    return null;
  }

  // the function or the method that this call names, when one of its parameters has a set as its type
  GenericFun? SetParamTemplate(Node callee, Scope scope) {
    string name = callee.Text!;
    if (!genericMethods.Keys.Any(key => key.Name == name) && !genericFuns.Keys.Any(key => key == name || key.EndsWith("." + name))) {
      return null;
    }
    GenericFun? found = null;
    if (callee.Kind == "Name") {
      if (scope.Find(name) != null) {
        return null;
      }
      found = currentClass != null ? GenericMethodOf(currentClass, name) : currentStruct != null && genericMethods.TryGetValue((currentStruct.Name, name), out var own) ? own : null;
      if (found == null) {
        genericFuns.TryGetValue(name, out found);
      }
    } else if (IsValueReceiver(callee.Children[0], scope)) {
      string type = ExprOf(callee.Children[0], scope).Type;
      if (IsClass(type)) {
        found = GenericMethodOf(ObjInfo(type), name);
      } else if (IsStruct(type)) {
        genericMethods.TryGetValue((type, name), out found);
      }
    } else if (Path(callee) is string path) {
      if (!genericFuns.TryGetValue(path, out found)) {
        string owner = path[..^(name.Length + 1)];
        found = classes.TryGetValue(owner, out var cls) ? GenericMethodOf(cls, name) : genericMethods.TryGetValue((owner, name), out var ofStruct) ? ofStruct : null;
      }
    }
    return found != null && found.Params.Any(p => p.Name.StartsWith('_')) ? found : null;
  }
}
