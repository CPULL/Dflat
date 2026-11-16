using System.Text;
using dfparser;

namespace dfcompiler;

// Extern C functions (spec 15.9): extern fun addr gfx_create() is a function of a C library. The program calls the wrapper dfx_<name>,
// which takes the Dflat values and calls the C function: a string becomes a const char* (UTF-8, ended by a 0, valid during the call), an
// arr<T> becomes a T* and a size_t length. The other types go as they are; addr is a void*.
public sealed partial class CodeGen {
  bool IsExternValueType(string type) => IsNumeric(type) || type is "bool" or "addr" || IsStruct(type) && structs[type].Layout != null || IsPointer(type);   // a pointer: its cursor (spec 12.1)

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
  // ends or changes; string(addr!) copies it at once and never frees it (spec 4.4.6, 15.9). The result is a string?: a null addr gives null,
  // and string s = string(p) takes "" for it. ->x empties the variable or field x (it no longer owns the text); ->f() takes the result.
  // A StringEncoding (UTF8 by default) can follow: any encoding but UTF8 reads the text in that encoding and copies it (taken: freed at once)
  Expr StringFromAddr(List<Node> args, Scope scope, Node at) {
    const string forms = "string(addr) borrows a C text, string(->addr) takes it, string(addr!) copies it; a StringEncoding can follow (spec 4.4.6)";
    if (args.Count is < 1 or > 2 || args[0].Kind == "Named" || args.Count == 2 && args[1].Kind == "Named" && args[1].Text != "encoding") {
      throw Error($"string(...) takes one addr and an optional StringEncoding: {forms}", at);
    }
    var arg = args[0];
    bool owned = arg.Kind == "Gift";
    bool copied = arg.Kind == "Copy";
    var operand = owned || copied ? arg.Children[0] : arg;
    var value = ExprOf(operand, scope);
    if (IsPointer(value.Type) && PointedType(value.Type) is "char" or "u8" or "i8" && !owned) {
      value = new Expr($"((void*)({value.Code}).cur)", "addr");   // a C text a native gave as *char (spec 22): its address
    }
    if (value.Type is not ("addr" or "null")) {
      throw Error($"string(...) takes an addr, not {value.Type}: {forms}", at);
    }
    string? encoding = null;
    if (args.Count == 2) {
      var encNode = args[1].Kind == "Named" ? args[1].Children[0] : args[1];
      var enc = ExprOf(encNode, scope);
      if (enc.Type != "StringEncoding") {
        throw Error($"the second argument of string(...) is a StringEncoding, not {enc.Type}: {forms}", args[1]);
      }
      encoding = enc.Code;
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
    if (encoding != null || copied) {
      NeedChunk("text");   // df_str_from_c_enc (RuntimeText.cs)
      return new Expr($"df_str_from_c_enc({code}, (int32_t)({encoding ?? "0"}), {(owned ? 1 : copied ? 2 : 0)})", "string?", true);
    }
    return new Expr($"df_str_from_c_opt({code}, {(owned ? "true" : "false")})", "string?", true);   // a null addr is null (spec 7.1)
  }

  readonly HashSet<string> conventionsDefined = new();   // the DF_CC_ macros already written

  // the prototype of the C function and the wrapper the program calls
  string ExternDeclarations(FunSig f) {
    foreach (var pointerType in f.Params.Where(p => p.Pointer == 0 && IsPointer(p.Type)).Select(p => p.Type).Append(f.Ret).Where(IsPointer)) {
      CheckExternPointer(pointerType, f.Node);   // spec 12.1
    }
    string ret = f.Ret == "void" ? "void" : CType(f.Ret);
    string cRet = IsPointer(f.Ret) ? CType(PointedType(f.Ret)) + "*" : ret;   // a C pointer; the program gets it unbounded
    var cParams = new List<string>();
    foreach (var p in f.Params) {
      if (IsPointer(p.Type)) {
        cParams.Add((IsWritablePointer(p.Type) ? "" : "const ") + CType(PointedType(p.Type)) + "*");   // only the cursor crosses (spec 12.1)
      } else if (p.Pointer != 0) {
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
    string symbol = f.CSymbol ?? f.Name;
    string convention = f.Convention is null or "cdecl" ? "" : $"DF_CC_{f.Convention} ";   // the calling convention of a native (spec 22)
    if (convention != "" && conventionsDefined.Add(f.Convention!)) {
      // only 32-bit x86 has several conventions; vectorcall exists on x64 Windows too. Elsewhere the keyword means nothing (spec 22)
      string condition = f.Convention == "vectorcall" ? "defined(_WIN32) && (defined(__i386__) || defined(__x86_64__))" : "defined(__i386__)";
      sb.Append($"#if {condition}\n#define DF_CC_{f.Convention} __attribute__(({f.Convention}))\n#else\n#define DF_CC_{f.Convention}\n#endif\n");
    }
    sb.Append($"extern {cRet} {convention}{symbol}({(cParams.Count == 0 ? "void" : string.Join(", ", cParams))});\n");
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
      } else if (IsPointer(p.Type)) {
        args.Add($"v_{p.Name}.cur");
      } else {
        args.Add($"v_{p.Name}");
      }
    }
    string call = $"{symbol}({string.Join(", ", args)})";
    if (ret == "void") {
      sb.Append($"  {call};\n");
      frees.ForEach(free => sb.Append(free));
    } else if (IsPointer(f.Ret)) {
      sb.Append($"  {cRet} r = {call};\n");
      frees.ForEach(free => sb.Append(free));
      sb.Append($"  return ({ret}){{ r, r, -1, NULL }};\n");   // unbounded (spec 12.1)
    } else {
      sb.Append($"  {ret} r = {call};\n");
      frees.ForEach(free => sb.Append(free));
      sb.Append("  return r;\n");
    }
    sb.Append("}\n");
    return sb.ToString();
  }
}
