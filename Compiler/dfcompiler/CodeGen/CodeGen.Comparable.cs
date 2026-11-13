using System.Text;
using dfparser;

namespace dfcompiler;

// Order of values (spec 6.2.2) and the trait Comparable (spec 11.2.1). A class that has the trait (Comparable alone is Comparable<itself>; T can
// be the class, a parent class or one of its traits) replaces int compareTo(T value), which also receives null. Its descriptor gets a C function
// (df_class.cmp) that finds the most specific compareTo that applies to the other object and calls it; df_obj_cmp (Runtime.cs) asks both objects
// and takes the most specific one (the left one on a tie), otherwise the default order: null first, then the bare class names, then the
// addresses. sort and sortUnstable use it for objects, and item by item for tuples, structs and nullable values; < <= > >= on objects need
// Comparable on one side. The keys of map and set keep the order by hash, unless the key class is Comparable (CodeGen.Map.cs).
// An exception raised by a compareTo inside a sort is kept in df_cmp_err and raised after the sort; inside a map or set it is dropped.
public sealed partial class CodeGen {
  // the instances of Comparable<T> a class implements (its own, its parents', its traits')
  List<(ClassInfo Trait, string Target)> ComparableTraits(ClassInfo cls) =>
    AllTraits(cls).Where(trait => classInstanceArgs.TryGetValue(trait.Name, out var instance) && instance.Template.Name == "Comparable")
      .Select(trait => (trait, classInstanceArgs[trait.Name].Args[0])).ToList();

  // the class has the trait Comparable (spec 11.2.1): it can be compared with < <= > >= and its compareTo orders it
  bool IsComparableClass(string type) => classes.TryGetValue(type, out var cls) && !cls.IsTrait && ComparableTraits(cls).Count > 0;

  // T of Comparable<T> is the class itself, one of its parent classes or one of its traits
  void CheckComparableTargets() {
    foreach (var cls in classes.Values.Where(c => !c.IsTrait && c.Node.Line < LibraryLine)) {
      foreach (var trait in cls.Traits) {
        if (!classInstanceArgs.TryGetValue(trait.Name, out var instance) || instance.Template.Name != "Comparable") {
          continue;
        }
        string target = instance.Args[0];
        bool ok = false;
        for (var c = cls; c != null && !ok; c = c.Parent) {
          ok = c.Name == target;
        }
        ok |= AllTraits(cls).Any(t => t.Name == target);
        if (!ok) {
          throw Error($"Comparable<{DisplayName(target)}>: {DisplayName(cls.Name)} can be compared with itself, one of its parent classes or one of its traits (spec 11.2.1)", cls.Node);
        }
      }
    }
  }

  // the name of the class without its namespace, for the default order (spec 6.2.2); non-ASCII bytes are written as C escapes
  string BareClassName(ClassInfo cls) {
    string shown = SourceMap.PrintName(DisplayName(cls.Name));
    int generic = shown.IndexOf('<');
    string head = generic < 0 ? shown : shown[..generic];
    int dot = head.LastIndexOf('.');
    string bare = (dot < 0 ? head : head[(dot + 1)..]) + (generic < 0 ? "" : shown[generic..]);
    var sb = new StringBuilder();
    foreach (byte b in Encoding.UTF8.GetBytes(bare)) {
      sb.Append(b < 128 && b != '"' && b != '\\' ? ((char)b).ToString() : $"\\{System.Convert.ToString(b, 8).PadLeft(3, '0')}");
    }
    return sb.ToString();
  }

  // df_class.cmp of a class: the compareTo methods that apply, the most specific first (a parent class deeper in the tree is more specific; a
  // trait is the least specific); "NULL" when the class is not Comparable
  string EmitCompareFunction(ClassInfo cls, StringBuilder sb) {
    var candidates = new List<(string Test, int Depth, FunSig Method)>();
    foreach (var (_, target) in ComparableTraits(cls)) {
      var method = LookupMethods(cls, "compareTo").FirstOrDefault(m => !m.IsStatic && !m.Class!.IsTrait && m.Params.Count == 1 && m.Params[0].Type == target);
      if (method == null) {
        continue;
      }
      if (classes.TryGetValue(target, out var targetClass) && !targetClass.IsTrait) {
        int depth = 1;
        for (var c = targetClass.Parent; c != null; c = c.Parent) {
          depth++;
        }
        candidates.Add(($"df_is(other, &{targetClass.Descriptor})", depth, method));
      } else {
        candidates.Add(($"df_has_trait(other, &df_trait_{target})", 0, method));
      }
    }
    if (candidates.Count == 0) {
      return "NULL";
    }
    string name = "df_cmpc_" + cls.Name;
    sb.Append($"static const df_class {cls.Descriptor};\n");
    sb.Append($"static int {name}(const void* self, const void* other, int* depth, bool run) {{\n");
    foreach (var (test, depth, method) in candidates.OrderByDescending(candidate => candidate.Depth)) {
      string declarer = CType(method.Class!.Name);
      string call = $"{method.CName}(({declarer})(void*)self, ({ParamCType(method.Params[0])})(void*)other)";
      sb.Append($"  if (other == NULL || {test}) {{\n    *depth = {depth};\n    if (!run) {{\n      return 0;\n    }}\n");
      if (PairReturn(method)) {
        sb.Append($"    {RetCType(method)} p = {call};\n");
        sb.Append("    if (p.err != NULL) {\n      if (df_cmp_err == NULL) {\n        df_cmp_err = p.err;\n      } else {\n        df_exc_release(p.err);\n      }\n      return 0;\n    }\n");
        sb.Append("    return (p.value > 0) - (p.value < 0);\n  }\n");
      } else {
        sb.Append($"    {CType(method.Ret)} r = {call};\n    return (r > 0) - (r < 0);\n  }}\n");
      }
    }
    sb.Append("  *depth = -1;\n  return 0;\n}\n\n");
    return name;
  }

  // the C that raises the exception a compareTo left during a comparison (sort, < on objects)
  string CompareRaise() => $"if (df_cmp_err != NULL) {{ df_err = df_cmp_err; df_cmp_err = NULL; df_err_thrown = false; goto {Handler}; }} ";

  // the items of this type are compared through objects (a compareTo can raise)
  bool OrderUsesObjects(string type) {
    if (IsNullable(type)) {
      return OrderUsesObjects(BaseOf(type));
    }
    if (IsTuple(type)) {
      return TupleElems(type).Any(elem => OrderUsesObjects(elem.Type));
    }
    if (IsStruct(type)) {
      return structs[type].Fields.Any(field => OrderUsesObjects(field.Type));
    }
    return IsClass(type) && !IsArr(type);
  }

  // the C comparer (int f(const void*, const void*)) of the items of a sort: the order of spec 6.2.2; null when the type has none
  string? CompareFunction(string elem) {
    if (enums.TryGetValue(elem, out var info)) {
      return CompareFunction(info.ValueType);
    }
    string? simple = elem switch {
      "i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64" or "r32" or "r64" => "df_cmp_" + elem,
      "bool" => "df_cmp_bool",
      "char" => "df_cmp_u32",
      "string" => "df_cmp_str",
      "string1k" => "df_cmp_s1k",
      "date" or "lapse" => "df_cmp_i64",
      "time" => "df_cmp_i32",
      "clock" => "df_cmp_u64",
      _ => null
    };
    if (simple != null) {
      return simple;
    }
    if (BoxInner(elem) is string counted) {
      return CountedCompareFunction(elem, counted);   // sort() of list<@T>, vec<@T>, arr<@T> (spec 12.7, 6.2.2)
    }
    if (IsClass(elem) && !IsArr(elem) && !IsCountedType(elem) && !IsWeakType(elem) && !boxInner.ContainsKey(elem)) {
      return "df_cmp_objp";
    }
    if (!(IsNullable(elem) || IsTuple(elem) || IsStruct(elem))) {
      return null;
    }
    if (CompareCode(elem, "x", "y") is not string body) {
      return null;
    }
    string name = "df_cmpg_" + System.Text.RegularExpressions.Regex.Replace(Mangle(elem), @"[^A-Za-z0-9_]", "_");
    if (arrayHelpers.Add(name)) {
      arrayCode.Append($"static int {name}(const void* a, const void* b) {{\n  const {CType(elem)}* x = (const {CType(elem)}*)a;\n  const {CType(elem)}* y = (const {CType(elem)}*)b;\n  return {body};\n}}\n\n");
    }
    return name;
  }

  // the comparer of the items of a collection of counted objects or values: the boxes are opened and what they hold is compared with the
  // default order of its type (objects: compareTo of a Comparable class, else type name and address; a null box is first)
  string? CountedCompareFunction(string box, string counted) {
    string name = "df_cmprc_" + System.Text.RegularExpressions.Regex.Replace(Mangle(box), @"[^A-Za-z0-9_]", "_");
    if (arrayHelpers.Contains(name)) {
      return name;
    }
    string item = CType(box);
    string field = BoxField(box);
    string body;
    if (IsCountedValueType(counted)) {
      string? inner = CompareFunction(counted);
      if (inner == null) {
        return null;
      }
      string Read(string p) => CellRead($"(*({item}*){p} != NULL ? (*({item}*){p})->{field} : NULL)", counted).Replace(MapBorrowMark, "");
      body = $"  {CType(counted)} x = {Read("a")};\n  {CType(counted)} y = {Read("b")};\n  return {inner}(&x, &y);\n";
    } else {
      string obj = CType(counted);
      string Read(string p) => $"(*({item}*){p} != NULL ? (void*)DF_RC_OBJ({obj}, (*({item}*){p})->{field}) : NULL)";
      body = $"  return df_obj_cmp({Read("a")}, {Read("b")});\n";
    }
    arrayHelpers.Add(name);
    arrayCode.Append($"static int {name}(const void* a, const void* b) {{\n{body}}}\n\n");
    return name;
  }

  // the order of two values held at the pointers x and y (C expressions): an int; null when the type has no order
  string? CompareCode(string type, string x, string y) {
    if (IsNullable(type)) {
      string? inner = CompareCode(BaseOf(type), $"(&({x})->val)", $"(&({y})->val)");
      return inner == null ? null
        : $"(({x})->has != ({y})->has ? (({x})->has ? 1 : -1) : !({x})->has ? 0 : {inner})";   // null first (spec 6.2.2)
    }
    if (IsTuple(type)) {
      var parts = new List<string>();
      var elems = TupleElems(type);
      for (int index = 0; index < elems.Count; index++) {
        if (CompareCode(elems[index].Type, $"(&({x})->e{index})", $"(&({y})->e{index})") is not string part) {
          return null;
        }
        parts.Add(part);
      }
      return Chain(parts);
    }
    if (IsStruct(type)) {
      var parts = new List<string>();
      foreach (var field in structs[type].Fields) {
        if (field.Buffer != null || CompareCode(field.Type, $"(&({x})->f_{field.Name})", $"(&({y})->f_{field.Name})") is not string part) {
          return null;
        }
        parts.Add(part);
      }
      return Chain(parts);
    }
    return CompareFunction(type) is string function ? $"{function}((const void*)({x}), (const void*)({y}))" : null;

    // item by item, left to right: the first that differs decides
    string Chain(List<string> parts) {
      if (parts.Count == 0) {
        return "0";
      }
      var sb = new StringBuilder("({ int c = ").Append(parts[0]).Append("; ");
      foreach (var part in parts.Skip(1)) {
        sb.Append($"if (c == 0) {{ c = {part}; }} ");
      }
      return sb.Append("c; })").ToString();
    }
  }

  // < <= > >= on objects (spec 11.2.1): only when one side has the trait Comparable; the order of df_obj_cmp
  Expr? CompareObjects(string op, Expr l, Expr r, Node at) {
    if (op is not ("<" or "<=" or ">" or ">=") || !(IsObjectOperand(l.Type) || IsObjectOperand(r.Type))) {
      return null;
    }
    if (!IsComparableClass(l.Type) && !IsComparableClass(r.Type)) {
      string shown = IsObjectOperand(l.Type) ? l.Type : r.Type;
      throw Error($"the class {DisplayName(shown)} does not define how to be compared with other objects, consider implementing the Comparable trait for it (spec 11.2.1)", at);
    }
    foreach (var side in new[] { l, r }) {
      if (!IsObjectOperand(side.Type) && side.Type != "null") {
        throw Error($"cannot compare {DisplayName(l.Type)} and {DisplayName(r.Type)}", at);
      }
    }
    l = TempObject(l);
    r = TempObject(r);
    string order = NewTemp();
    string left = l.Type == "null" ? "NULL" : $"(const void*)({l.Code})";
    string right = r.Type == "null" ? "NULL" : $"(const void*)({r.Code})";
    return new Expr($"({{ int {order} = df_obj_cmp({left}, {right}); {CompareRaise()}{order} {op} 0; }})", "bool");
  }

  bool IsObjectOperand(string type) => IsClass(type) && !IsArr(type) && !IsCountedType(type) && !IsWeakType(type);
}
