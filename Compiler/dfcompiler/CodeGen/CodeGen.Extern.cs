using System.Text;
using dfparser;

namespace dfcompiler;

// Extern C functions (spec 15.9): extern fun addr gfx_create() is a function of a C library. The program calls the wrapper dfx_<name>,
// which takes the Dflat values and calls the C function: a string becomes a const char* (UTF-8, ended by a 0, valid during the call), an
// arr<T> becomes a T* and a size_t length. The other types go as they are; addr is a void*.
public sealed partial class CodeGen {
  bool IsExternValueType(string type) => IsNumeric(type) || type is "bool" or "addr" || IsStruct(type) && structs[type].Layout != null;

  void CheckExtern(Node funNode, ClassInfo? cls, string? enumName, string ret, List<ParamSig> ps, bool noBody) {
    if (cls != null || enumName != null) {
      throw Error("an extern function is declared at the top level, not in a class or an enum", funNode);
    }
    if (!noBody) {
      throw Error("an extern function has no body: the C library gives it", funNode);
    }
    if (ret != "void" && !IsExternValueType(ret)) {
      throw Error(IsStruct(ret)
        ? $"a struct given to C needs a layout: struct<fixed> {ret} or struct<compact> {ret} (spec 11.1.5.6)"
        : $"an extern function returns a number, bool, addr or a struct<fixed> / <compact>, not {ret} (spec 15.9)", funNode);
    }
    var paramsNode = funNode.Children.First(child => child.Kind == "Params");
    foreach (var (param, node) in ps.Zip(paramsNode.Children)) {
      if (node.Kind != "Param" || param.IsWeak) {
        throw Error($"the parameters of an extern function are plain values: '{param.Name}'", node);
      }
      bool ok = IsExternValueType(param.Type) || param.Type == "string"
        || IsArr(param.Type) && (IsNumeric(ArrElem(param.Type)) || ArrElem(param.Type) == "bool");
      if (IsStruct(param.Type) && structs[param.Type].Layout == null) {
        throw Error($"a struct given to C needs a layout: struct<fixed> {param.Type} or struct<compact> {param.Type} (spec 11.1.5.6)", node);
      }
      if (!ok) {
        throw Error($"'{param.Name}' is {param.Type}: an extern function takes numbers, bool, addr, string and arr of numbers (spec 15.9)", node);
      }
    }
  }

  // string(addr) borrows a C text (UTF-8 ended by a 0) and never frees it; string(->addr) takes it and frees it with free() when the string
  // ends or changes (spec 4.4.6, 15.9). The result is a string?: a null addr gives null, and string s = string(p) takes "" for it. ->x empties the variable or field x (it no longer owns the text); ->f() takes the result
  Expr StringFromAddr(List<Node> args, Scope scope, Node at) {
    if (args.Count != 1 || args[0].Kind == "Named") {
      throw Error("string(...) takes one addr: string(addr) borrows a C text, string(->addr) takes it (spec 4.4.6)", at);
    }
    var arg = args[0];
    bool owned = arg.Kind == "Gift";
    var operand = owned ? arg.Children[0] : arg;
    var value = ExprOf(operand, scope);
    if (value.Type is not ("addr" or "null")) {
      throw Error($"string(...) takes an addr, not {value.Type}: string(addr) borrows a C text, string(->addr) takes it (spec 4.4.6)", at);
    }
    if (value.Type == "null") {
      return new Expr("((df_opt_string){0})", "string?", true);
    }
    string code = value.Code;
    if (owned && operand.Kind is "Name" or "Member" or "Index") {
      var target = LValue(operand, scope);
      if (target.IsConst) {
        throw Error("a const cannot be moved", operand);
      }
      string slot = NewTemp();
      string taken = NewTemp();
      code = $"({{ void** {slot} = &({target.Code}); void* {taken} = *{slot}; *{slot} = NULL; {taken}; }})";
    }
    return new Expr($"df_str_from_c_opt({code}, {(owned ? "true" : "false")})", "string?", true);   // a null addr is null (spec 7.1)
  }

  // the prototype of the C function and the wrapper the program calls
  string ExternDeclarations(FunSig f) {
    string ret = f.Ret == "void" ? "void" : CType(f.Ret);
    var cParams = new List<string>();
    foreach (var p in f.Params) {
      if (p.Pointer != 0) {
        cParams.Add((p.Pointer == 1 ? "const " : "") + CType(p.Type) + "*");
      } else if (p.Type == "string") {
        cParams.Add("const char*");
      } else if (IsArr(p.Type)) {
        cParams.Add(CType(ArrElem(p.Type)) + "*");
        cParams.Add("size_t");
      } else {
        cParams.Add(CType(p.Type));
      }
    }
    var sb = new StringBuilder();
    sb.Append($"extern {ret} {f.Name}({(cParams.Count == 0 ? "void" : string.Join(", ", cParams))});\n");
    string wrapperParams = f.Params.Count == 0 ? "void" : string.Join(", ", f.Params.Select(p =>
      p.Pointer != 0 ? $"{(p.Pointer == 1 ? "const " : "")}{CType(p.Type)}* v_{p.Name}" : $"{CType(p.Type)} v_{p.Name}"));
    sb.Append($"static {ret} {f.CName}({wrapperParams}) {{\n");
    var args = new List<string>();
    var frees = new List<string>();
    foreach (var p in f.Params) {
      if (p.Type == "string") {
        sb.Append($"  char* k_{p.Name}; df_sv s_{p.Name} = df_view_keep(v_{p.Name}, &k_{p.Name});\n");
        args.Add($"s_{p.Name}.p");
        frees.Add($"  free(k_{p.Name});\n");
      } else if (IsArr(p.Type)) {
        args.Add($"v_{p.Name}.p");
        args.Add($"(size_t)v_{p.Name}.len");
      } else {
        args.Add($"v_{p.Name}");
      }
    }
    string call = $"{f.Name}({string.Join(", ", args)})";
    if (ret == "void") {
      sb.Append($"  {call};\n");
      frees.ForEach(free => sb.Append(free));
    } else {
      sb.Append($"  {ret} r = {call};\n");
      frees.ForEach(free => sb.Append(free));
      sb.Append("  return r;\n");
    }
    sb.Append("}\n");
    return sb.ToString();
  }
}
