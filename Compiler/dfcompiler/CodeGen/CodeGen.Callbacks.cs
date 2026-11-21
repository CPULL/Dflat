using System.Text;
using dfparser;

namespace dfcompiler;

// Callbacks (spec 22): a pointer to a Dflat function that C calls. The .dflib declares the type of the callback with its calling
// convention, alias [stdcall] fun i64(Hwnd, u32) as WndProc (the library convention when none is written); the program makes the
// pointer with pointer.to(onKey(i32, *char)), naming the function and the types of its parameters, which choose among the overloads.
//
// Inside the compiler the pointed type is "[conv]fun(R)(P1,P2)" (the C type df_cbfn, one byte, so the pointer struct of spec 12.1 can hold
// it): the pointer is a pointer<[conv]fun(R)(P)>, with the cursor on a small C function written here, df_cbN, which has the convention and
// the C types of the callback and calls the Dflat function. Only plain functions and static methods; their parameters and result are plain
// C values (numbers, bool, addr, handles, pointers, struct<fixed> / <compact>). C cannot receive an exception: a function that can let a
// non-silent exception out is a compile error, a silent one stops in the wrapper as at any caller (spec 15.4).
public sealed partial class CodeGen {
  // how the runtime goes into this unit (spec 15.8, RuntimeLinkage.cs)
  // a library build declares the runtime and the stdlib of the program it is linked with (spec 15.8)
  // a program that links compiled libraries defines it for them (exported)
  public RuntimeLinkageMode RuntimeMode { get; set; } = LibraryBuild != null ? RuntimeLinkageMode.Imported
    : RuntimeLinkage.Forced ?? (LinkedLibraries.Count > 0 ? RuntimeLinkageMode.Exported : RuntimeLinkageMode.Static);

  // the compiled Dflat libraries the program links (spec 15.8, Dfi.cs): the runtime chunks and the stdlib parts they need are in the program
  internal static IReadOnlyList<CompiledLibrary> LinkedLibraries { get; set; } = new List<CompiledLibrary>();

  // the multitasking runtime (spec 20.0): the program imports Root.Tasks or links a library built with it. The runtime chunk "tasks"
  // defines DF_TASKS in the generated C, and a library build lists it in its interface
  public static bool TasksMode { get; set; }

  // the stdlib written in Dflat goes with the runtime (spec 15.8): its functions (the copies of its generics included) and the records of
  // its classes are defined in the program and only declared in a compiled library. Its lambdas and pure helpers (printers, vtables, array
  // helpers) stay in each unit: their names are numbered in the unit, or they hold no state
  readonly HashSet<string> stdlibSymbols = new();

  readonly HashSet<string> stdlibNames = new();
  readonly HashSet<string> stdlibEnums = new();   // the enums of the stdlib preludes   // the classes, traits and functions of the stdlib preludes written with ordinary lines

  bool IsStdlibClass(ClassInfo cls) => cls.Node.Line >= LibraryLine || stdlibNames.Contains(cls.Name);

  bool IsSharedStdlib(FunSig f) => f.LambdaCtx == null && !f.IsExtern
    && (f.Node.Line >= LibraryLine || (f.Class != null ? IsStdlibClass(f.Class) : stdlibNames.Contains(f.Name)));

  string StdlibLinkage(string code) =>
    RuntimeMode == RuntimeLinkageMode.Static ? code : RuntimeLinkage.Apply(code, RuntimeMode, stdlibSymbols.Contains);

  readonly Dictionary<string, string> callbackConventions = new();   // a callback type of a .dflib → its calling convention
  readonly Dictionary<string, int> callbackIds = new();               // a pointed type [conv]fun(...) → its number (C names)
  readonly Dictionary<(string Fun, string Pointee), string> callbackWrapperNames = new();
  readonly StringBuilder callbackWrappers = new();                    // after every prototype: they call the Dflat functions
  readonly HashSet<FunSig> nonSilentLeakers = new();                  // the functions a non-silent exception can leave (CodeGen.Leaks.cs)
  int callbackSignatureDepth;                                         // resolving the types of a callback: *arr<char> is *char there

  static bool IsCallbackPointee(string pointed) => pointed.StartsWith('[') && pointed.Contains("]fun(");
  static bool IsCallbackPointer(string type) => IsPointer(type) && IsCallbackPointee(PointedType(type));

  static (string Convention, string Fun) CallbackParts(string pointee) {
    int close = pointee.IndexOf(']');
    return (pointee[1..close], pointee[(close + 1)..]);
  }

  // alias [stdcall] fun i32(i32) as Name: a function type; in a .dflib it is also the type of a callback, with its convention
  void RegisterFunAlias(Node a, Node funType) {
    var native = a.Children.FirstOrDefault(child => child.Kind == "Native");
    if (native != null) {
      callbackSignatureDepth++;
    }
    string fun;
    try {
      fun = ResolveFunType(funType);
    } finally {
      if (native != null) {
        callbackSignatureDepth--;
      }
    }
    typeAliases[a.Text!] = fun;
    if (native != null) {
      callbackConventions[a.Text!] = native.Text ?? "cdecl";
      callbackAliasNodes.Add((fun, a));   // checked once the structs are known (CheckCallbackAliases)
    }
  }

  readonly List<(string Fun, Node At)> callbackAliasNodes = new();

  void CheckCallbackAliases() {
    foreach (var (fun, at) in callbackAliasNodes) {
      CheckCallbackSignature(fun, at);
    }
  }

  // pointer<WndProc>, pointer<fun i32(i32)>: the pointed type of a callback pointer; null when the type argument is no function type
  string? CallbackPointee(Node pointedNode, Node at, bool writable) {
    string? pointee = null;
    string? shown = null;
    if (pointedNode is { Kind: "Type", Children.Count: 0 } named && StripLibraryQualifier(named.Text!) is string name && typeAliases.TryGetValue(name, out var aliased) && IsFunType(aliased)) {
      pointee = $"[{callbackConventions.GetValueOrDefault(name, "cdecl")}]{aliased}";
      shown = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
    } else if (pointedNode.Kind == "FunType") {
      callbackSignatureDepth++;
      try {
        string fun = ResolveFunType(pointedNode);
        pointee = $"[cdecl]{fun}";
        shown = FunDisplay(fun);
      } finally {
        callbackSignatureDepth--;
      }
    }
    if (pointee == null) {
      return null;
    }
    if (writable) {
      throw Error("a callback pointer is not writable: pointer<F>, not pointer<&F> (spec 22)", at);
    }
    CheckCallbackSignature(CallbackParts(pointee).Fun, at);
    NoteCallbackDisplay(pointee, shown!);
    return pointee;
  }

  void NoteCallbackDisplay(string pointee, string shown) {
    displayNames.TryAdd(pointee, shown);
    displayNames.TryAdd(PointerType(pointee, false), $"pointer<{shown}>");
  }

  int CallbackId(string pointee) {
    if (!callbackIds.TryGetValue(pointee, out int id)) {
      id = callbackIds.Count;
      callbackIds[pointee] = id;
    }
    return id;
  }

  // what C can give to a callback and take back from it (spec 22)
  bool IsCallbackValue(string type) =>
    IsNumeric(type) || type is "bool" or "addr" || IsHandle(type) || IsStruct(type) && structs[type].Layout != null;

  void CheckCallbackSignature(string fun, Node at) {
    var (ret, ps) = FunParts(fun);
    foreach (string p in ps) {
      if (p.StartsWith("->") || p.StartsWith('&') || IsCountedType(p) || IsWeakType(p)) {
        throw Error($"the parameters of a callback are plain C values, not {DisplayName(p)} (spec 22)", at);
      }
      if (IsPointer(p) && !IsCallbackPointer(p)) {
        continue;
      }
      if (!IsCallbackValue(p)) {
        throw Error(p == "char"
          ? "a callback takes no char: a C char is an i8 or a u8, a C text a *char (spec 22)"
          : $"a callback takes numbers, bool, addr, handles, pointers and struct<fixed> / <compact>, not {DisplayName(p)} (spec 22)", at);
      }
    }
    if (ret != "void" && !IsCallbackValue(ret)) {
      throw Error($"a callback returns a number, bool, addr, a handle or a struct<fixed> / <compact>, not {DisplayName(ret)} (spec 22)", at);
    }
  }

  // the calling convention in a C declaration: nothing for cdecl (the C default), otherwise a macro that is empty where the keyword means
  // nothing: only 32-bit x86 has several conventions, vectorcall exists on x64 Windows too (spec 22)
  string ConventionKeyword(string? convention) {
    if (convention is null or "cdecl") {
      return "";
    }
    if (conventionsDefined.Add(convention)) {
      string condition = convention == "vectorcall" ? "defined(_WIN32) && (defined(__i386__) || defined(__x86_64__))" : "defined(__i386__)";
      typeDefs.Append($"#if {condition}\n#define DF_CC_{convention} __attribute__(({convention}))\n#else\n#define DF_CC_{convention}\n#endif\n");
    }
    return $"DF_CC_{convention} ";
  }

  // the C type of a parameter of a callback, as C sees it
  string CallbackCParam(string type) {
    if (IsPointer(type)) {
      return (IsWritablePointer(type) ? "" : "const ") + CType(PointedType(type)) + "*";
    }
    return CType(type);
  }

  // typedef R (conv *df_cbtN)(P...): the C function pointer of a callback type
  string CallbackFnType(string pointee) {
    string name = $"df_cbt{CallbackId(pointee)}";
    if (definedTypes.Add(name)) {
      var (convention, fun) = CallbackParts(pointee);
      var (ret, ps) = FunParts(fun);
      string keyword = ConventionKeyword(convention);
      string list = ps.Count == 0 ? "void" : string.Join(", ", ps.Select(CallbackCParam));
      typeDefs.Append($"typedef {(ret == "void" ? "void" : CType(ret))} ({keyword}*{name})({list});\n");
    }
    return name;
  }

  // pointer.to(onKey(i32, *char)): the callback pointer; expected (a pointer<WndProc> parameter or variable) gives the convention
  Expr FunRefPointer(Node funRef, Scope scope, string? expected) {
    string name = funRef.Text!;
    string shownName = name;
    if (scope.Find(name) != null) {
      throw Error($"'{name}' is a variable: a callback is a plain function or a static method; lambdas come later (spec 22)", funRef);
    }
    var typeNodes = funRef.Children.First(child => child.Kind == "ParamTypes").Children;
    var types = new List<string>();
    callbackSignatureDepth++;
    try {
      foreach (var typeNode in typeNodes) {
        if (typeNode.Kind == "ModeType") {
          throw Error($"the parameters of a callback are plain C values: pointer.to({shownName}(T, ...)) names their types alone (spec 22)", typeNode);
        }
        types.Add(ResolveType(typeNode));
      }
    } finally {
      callbackSignatureDepth--;
    }
    string written = $"{shownName}({string.Join(", ", types.Select(DisplayName))})";
    if (genericFuns.ContainsKey(name)) {
      throw Error($"'{name}' is generic: a callback is one function, not a generic one (spec 22)", funRef);
    }
    if (!funs.TryGetValue(name, out var overloads)) {
      throw Error($"unknown function '{name}' in pointer.to({written}) (spec 22)", funRef);
    }
    var f = overloads.FirstOrDefault(candidate => candidate.Params.Count == types.Count && candidate.Params.Zip(types).All(pair => pair.First.Type == pair.Second));
    if (f == null) {
      throw Error($"no function {written}: the types in pointer.to(...) are the types of its parameters, in order (spec 22)", funRef);
    }
    if (f.IsExtern) {
      throw Error($"'{name}' is a native: it is already a C function; a callback is a Dflat function (spec 22)", funRef);
    }
    if (f.Class != null && !f.IsStatic || f.Struct != null && !f.IsStatic || f.LambdaCtx != null) {
      throw Error($"'{name}' is a method: a callback is a plain function or a static method (spec 22)", funRef);
    }
    foreach (var p in f.Params) {
      if (p.Ref == 2 || p.IsWritable || p.IsWeak || p.IsVariadic || p.IsParent || p.IsGift && Giftable(p.Type)) {
        throw Error($"'{p.Name}' of {written} is not a plain parameter: C gives a callback plain values (spec 22)", funRef);
      }
    }
    if (nonSilentLeakers.Contains(f)) {
      throw Error($"{written} can let an exception out, and C cannot receive it: catch it inside the function to use it as a callback (spec 22)", funRef);
    }
    string fun = MakeFunType(f.Ret, types);
    CheckCallbackSignature(fun, funRef);
    string convention = "cdecl";
    if (expected != null && IsCallbackPointer(expected) && CallbackParts(PointedType(expected)) is var wanted && wanted.Fun == fun) {
      convention = wanted.Convention;
    }
    string pointee = $"[{convention}]{fun}";
    NoteCallbackDisplay(pointee, FunDisplay(fun));
    string type = PointerType(pointee, false);
    string wrapper = CallbackWrapper(f, pointee);
    string cType = CType(type);
    return new Expr($"(({cType}){{ (df_cbfn*)(void*){wrapper}, (df_cbfn*)(void*){wrapper}, 1, NULL }})", type);
  }

  // the C function C calls: the convention and the C types of the callback, a call of the Dflat function inside
  string CallbackWrapper(FunSig f, string pointee) {
    if (callbackWrapperNames.TryGetValue((f.CName, pointee), out var known)) {
      return known;
    }
    string name = $"df_cb{callbackWrapperNames.Count}";
    callbackWrapperNames[(f.CName, pointee)] = name;
    var (convention, fun) = CallbackParts(pointee);
    var (ret, ps) = FunParts(fun);
    string keyword = ConventionKeyword(convention);
    var cParams = new List<string>();
    var args = new List<string>();
    for (int i = 0; i < ps.Count; i++) {
      string t = ps[i];
      cParams.Add($"{CallbackCParam(t)} a{i}");
      if (IsPointer(t)) {
        string elemC = CType(PointedType(t));
        args.Add($"(({CType(t)}){{ ({elemC}*)a{i}, ({elemC}*)a{i}, -1, NULL }})");   // unbounded: it comes from C (spec 12.1)
      } else if (IsByPointer(f.Params[i])) {
        args.Add($"&a{i}");
      } else {
        args.Add($"a{i}");
      }
    }
    string cRet = ret == "void" ? "void" : CType(ret);
    string call = $"{f.CName}({string.Join(", ", args)})";
    var sb = new StringBuilder();
    sb.Append($"static {cRet} {keyword}{name}({(cParams.Count == 0 ? "void" : string.Join(", ", cParams))}) {{\n");
    if (PairReturn(f)) {
      // only a silent exception can come out (the others are a compile error): it stops here, as at any caller (spec 15.4)
      if (ret == "void") {
        sb.Append($"  df_exception* e = {call};\n  if (e) {{ df_exc_release(e); }}\n");
      } else {
        sb.Append($"  {ResultPair(cRet)} r = {call};\n  if (r.err) {{ df_exc_release(r.err); }}\n  return r.value;\n");
      }
    } else if (ret == "void") {
      sb.Append($"  {call};\n");
    } else {
      sb.Append($"  return {call};\n");
    }
    sb.Append("}\n");
    callbackWrappers.Append(sb);
    return name;
  }
}
