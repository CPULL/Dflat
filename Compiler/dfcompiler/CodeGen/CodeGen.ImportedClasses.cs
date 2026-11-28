using System.Text.RegularExpressions;

namespace dfcompiler;

// The classes of a compiled library (spec 15.8): the program knows them from the .dfi. Its C struct is the layout the library was compiled
// with (layout-<target>:), the record (df_cls_...) is the library's, declared extern; the methods and the constructors are the exported
// symbols of the library. An object of such a class ends, prints, hashes and clones through its record, as any object does, so the program
// writes none of these functions for it. A method is called by its symbol: the exported function of the library calls the method the way
// the library does, so a subclass of the library that replaces it is reached too.
public sealed partial class CodeGen {
  // the constructors of the .dfi: public Point(i32 x, i32 y) -> symbol, read as static functions named dfiCtor (Dfi.cs)
  void BuildImportedConstructors(ClassInfo cls) {
    var inits = new List<FunSig>();
    var ctors = new List<FunSig>();
    funs[cls.Name + InitsSuffix] = inits;
    funs[cls.Name] = ctors;
    var body = cls.Node.Children.First(child => child.Kind == "Block");
    foreach (var member in body.Children.Where(member => member.Kind == "Fun" && member.Children.Any(child => child.Kind == "ImportedCtor"))) {
      var sig = RegisterFun(member, null, cls)!;
      funs[sig.Name].Remove(sig);
      if (funs[sig.Name].Count == 0) {
        funs.Remove(sig.Name);
      }
      ctors.Add(new FunSig(cls.Name, cls.Name, sig.Params, member, null) {
        Class = cls,
        IsCtor = true,
        Visibility = Vis.Public,
        ImportedSymbol = member.Children.First(child => child.Kind == "Imported").Text
      });
    }
    cls.Ctor = ctors.FirstOrDefault();
    cls.Init = null;
  }

  // the C struct of the layout: the public fields with the C types of the program, the other fields as the library wrote them (a pointer to
  // a class the program does not know becomes void*: same size); the record of the library, declared
  void EmitImportedStruct(ClassInfo cls) {
    if (cls.Fields.FirstOrDefault(field => field.IsStatic) is FieldInfo shared) {
      throw NotSupported("a static field of a class of a compiled library", shared.At);   // it is not exported: the program would hold its own copy
    }
    var known = AllInstanceFields(cls).ToDictionary(field => field.CName);
    var parts = new List<string>();
    foreach (string part in cls.ImportedLayout!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var name = Regex.Match(part, @"([A-Za-z_][A-Za-z0-9_]*)\s*$").Groups[1].Value;
      if (known.TryGetValue(name, out var field)) {
        parts.Add($"{(field.IsCounted || field.IsWeak ? "df_rc*" : CType(field.Type))} {name}");
      } else {
        if (Regex.IsMatch(part, @"\bdf_setv\b")) {
          SetValueCType();   // a hidden field of a set type (spec 11.3.6.1): the program defines the C type of set values too
        }
        parts.Add(Regex.Replace(part, @"\b(struct\s+)?c_[A-Za-z0-9_]+\s*\*", "void* "));
      }
    }
    typeDefs.Append($"struct {cls.StructName} {{\n{string.Concat(parts.Select(part => "  " + part + ";\n"))}}};\n");
    typeDefs.Append($"extern const df_class {cls.Descriptor};   /* the record of a class of a compiled library (spec 15.8) */\n\n");
    if (cls.IsException) {
      typeDefs.Append($"extern df_descriptor {cls.ExcDescriptor};   /* its descriptor: the id is given at program start (spec 15.8) */\n");
    }
  }
}
