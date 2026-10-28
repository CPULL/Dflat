using System.Text;
using dfparser;

namespace dfcompiler;

// .hash (spec 6.2.1): a uns for every value. Natives up to uns are their own value widened with zeros, reals their normalized bits, strings
// rapidhash V3 (RuntimeHash.cs), tuples $ over the components, null the constant "null".hash. An object hashes through its class descriptor
// (df_hash_obj): the function is the override of get_hash() (written with override, Root.Class offers it) or, without one, a default generated
// for the class itself: "Root.Class".hash $ the fields in memory order, an unstable field giving the hash of its type name plus its field name.
// Collections (the default ones, their iterators and views, and the classes with the trait Collection or Iterator) hash as their type name.
// The names known at compile time are hashed by the compiler (DfHash, Hashing.cs).
public sealed partial class CodeGen {
  bool hashUsed;   // the program uses .hash: every class gets its hash function

  static int UnsBits => Bits(NativeUns);

  string HashConst(string text) => DfHash.CConst(DfHash.Str(text, UnsBits), UnsBits);

  static readonly HashSet<string> IoLibraryNames = new() {
    "File", "Stream", "FileStream", "TextStream", "DeleteOptions", "OverwriteOptions", "FileSystemObjectsType", "FileWriteMode",
    "StreamReadWriteAccessMode", "StreamOpenMode", "StreamSeekOrigin"
  };

  // the fully qualified name of a type for its hash (spec 6.2.1): natives and the default collections without namespace, classes and enums with
  // it (Root. when the program does not give one: namespaces do not qualify names yet), generic arguments in <> separated by ',' with no spaces
  string QualifiedTypeName(string type) {
    if (IsNullable(type)) {
      return QualifiedTypeName(BaseOf(type)) + "?";
    }
    if (IsTuple(type)) {
      return "(tuple," + string.Join(",", TupleElems(type).Select(elem => QualifiedTypeName(elem.Type))) + ")";
    }
    if (IsArr(type)) {
      return $"arr<{QualifiedTypeName(ArrElem(type))}>";
    }
    if (classInstanceArgs.TryGetValue(type, out var instance)) {
      string args = string.Join(",", instance.Args.Select(QualifiedTypeName));
      string template = instance.Template.Node.Line >= LibraryLine ? instance.Template.Name : "Root." + instance.Template.Name;
      return $"{template}<{args}>";
    }
    if (type == "StringEncoding") {
      return "String.StringEncoding";
    }
    if (IoLibraryNames.Contains(type)) {
      return "Root.IO." + type;
    }
    if (type == "RunResult") {
      return "Root.ExecProcess.RunResult";
    }
    if (IsClass(type) || IsEnum(type)) {
      return "Root." + type;
    }
    return type;
  }

  // a collection for the hash: a copy of a library generic (vec, list, map, their iterators and views) or a class with the trait Collection or Iterator
  bool IsCollectionLike(ClassInfo cls) {
    if (classInstanceArgs.TryGetValue(cls.Name, out var instance) && instance.Template.Node.Line >= LibraryLine) {
      return true;
    }
    return AllTraits(cls).Any(trait => trait.Name is "Collection" or "Iterator"
      || (classInstanceArgs.TryGetValue(trait.Name, out var copy) && copy.Template.Name is "Collection" or "IndexIterator"));
  }

  // the override of get_hash() that a class uses: its own or the nearest one of its parents
  FunSig? HashOverride(ClassInfo cls) {
    for (var c = cls; c != null; c = c.Parent) {
      if (funs.TryGetValue(c.Name + ".get_hash", out var list) && list.FirstOrDefault(f => f.Params.Count == 0 && !f.IsStatic) is FunSig found) {
        return found;
      }
    }
    return null;
  }

  // x.hash
  Expr HashOf(Expr e, Node at) {
    hashUsed = true;
    NeedChunk("hash");
    return new Expr(HashCode(e, at), NativeUns);
  }

  string HashCode(Expr e, Node at) {
    string type = e.Type;
    if (type == "null") {
      return "DF_HASH_NULL";
    }
    if (type == "string") {
      return $"df_hash_str({StrRead(e)})";
    }
    if (IsNullable(type)) {
      return WithTemp(e, temp => $"({temp}.has ? {HashCode(new Expr(temp + ".val", BaseOf(type)), at)} : DF_HASH_NULL)");
    }
    if (IsTimeType(type)) {
      string bits = type == "time" ? "uint32_t" : "uint64_t";
      return WithTemp(e, temp => $"({temp} == {NullCode(type)} ? DF_HASH_NULL : {WidenedHash($"({bits})({temp})", type == "time" ? 32 : 64)})");
    }
    if (enums.TryGetValue(type, out var info)) {
      return HashCode(new Expr(e.Code, info.ValueType), at);
    }
    if (type is "bool" or "char") {
      return $"((df_uns)({e.Code}))";
    }
    if (IsInt(type)) {
      int bits = Bits(type);
      return WidenedHash($"({PrimitiveCType("u" + bits)})({e.Code})", bits);
    }
    if (type == "r32") {
      return $"df_hash_r32({e.Code})";
    }
    if (type == "r64") {
      return $"df_hash_r64({e.Code})";
    }
    if (IsTuple(type)) {
      var elems = TupleElems(type);
      return WithTemp(e, temp => {
        string h = HashCode(new Expr($"{temp}.e0", elems[0].Type), at);
        for (int index = 1; index < elems.Count; index++) {
          h = $"df_hash_comb({h}, {HashCode(new Expr($"{temp}.e{index}", elems[index].Type), at)})";
        }
        return h;
      });
    }
    if (IsArr(type)) {
      string name = HashConst(QualifiedTypeName(type));
      return SimpleCode.IsMatch(e.Code) ? name : $"((void)({e.Code}), {name})";
    }
    if (IsClass(type)) {
      return $"df_hash_obj({e.Code})";
    }
    throw NotSupported($"The hash of {DisplayName(type)}", at);
  }

  // an unsigned value of this many bits: itself, or its two halves combined when it is wider than uns
  static string WidenedHash(string unsignedCode, int bits) => bits > UnsBits ? $"df_hash_w64({unsignedCode})" : $"((df_uns){unsignedCode})";

  // ---------------------------------------------------------------- stable fields

  // a field is stable when its hash follows its content (spec 6.2.1): natives, strings, tuples of stable values, owned objects whose class
  // overrides get_hash() or whose members are all stable; collections, iterators, views and references are not
  bool IsStableType(string type, HashSet<ClassInfo> visiting) {
    if (IsNullable(type)) {
      return IsStableType(BaseOf(type), visiting);
    }
    if (IsTuple(type)) {
      return TupleElems(type).All(elem => IsStableType(elem.Type, visiting));
    }
    if (IsArr(type) || IsMulti(type) || type == "Exception") {
      return false;   // (an exception is never owned by a field; multi-type values have no hash yet)
    }
    if (IsTraitType(type)) {
      return true;   // the class is known only at run time: its own hash function decides
    }
    if (classes.TryGetValue(type, out var cls)) {
      if (HashOverride(cls) != null) {
        return true;
      }
      if (IsCollectionLike(cls)) {
        return false;
      }
      if (!visiting.Add(cls)) {
        return true;   // a class that holds its own type: the recursion ends at a null field at run time
      }
      bool stable = AllInstanceFields(cls).Where(field => !field.Hidden && !field.IsParentRef && !field.IsWeak).All(field => IsStableType(field.Type, visiting));
      visiting.Remove(cls);
      return stable;
    }
    return true;
  }

  // ---------------------------------------------------------------- the hash function of a class

  // df_hash_<class>: the override of get_hash(), the type name of a collection, or the default of the class
  void EmitHashFunction(ClassInfo cls, StringBuilder sb) {
    sb.Append($"static df_uns df_hash_{cls.Name}(const void* p) {{\n");
    var custom = HashOverride(cls);
    if (custom != null) {
      string call = $"{custom.CName}(({CType(custom.Class!.Name)})p)";
      if (PairReturn(custom)) {
        sb.Append($"  {ResultPair(CType(custom.Ret))} r = {call};\n  if (r.err) {{\n    df_panic_unhandled(r.err);\n  }}\n  return (df_uns)r.value;\n}}\n\n");
      } else {
        sb.Append($"  return (df_uns){call};\n}}\n\n");
      }
      return;
    }
    if (IsCollectionLike(cls)) {
      sb.Append($"  (void)p;\n  return {HashConst(QualifiedTypeName(cls.Name))};\n}}\n\n");
      return;
    }
    sb.Append($"  const {cls.StructName}* o = (const {cls.StructName}*)p;\n  (void)o;\n");
    sb.Append($"  df_uns h = {HashConst(QualifiedTypeName(cls.Name))};\n");
    foreach (var field in AllInstanceFields(cls).Where(field => !field.Hidden)) {
      string part = field.IsParentRef || field.IsWeak || !IsStableType(field.Type, new HashSet<ClassInfo>())
        ? HashConst(QualifiedTypeName(field.Type) + field.Name)
        : HashCode(new Expr($"o->{field.CName}", field.Type), cls.Node);
      sb.Append($"  h = df_hash_comb(h, {part});\n");
    }
    sb.Append("  return h;\n}\n\n");
  }
}
