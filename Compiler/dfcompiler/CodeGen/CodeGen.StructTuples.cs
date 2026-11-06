using dfparser;

namespace dfcompiler;

// Field groups, tuples and hash (spec 11.1.5.8, 11.1.5.9), step 7. A struct with two or more fields converts to a tuple with the same
// number of items, each field convertible to its item, in order: in a declaration or an assignment of a tuple, as the result of a
// function that returns a tuple or several values, and on the left of a, b = s. A field group takes a struct with the same structure
// (s.pos = Vec2(1.0, 2.0)) field by field in order. A struct converted keeps no buffers and no objects (strings are copied, or moved
// out of a new struct). .hash is the hash of the tuple of its fields (a buffer adds its items).
public sealed partial class CodeGen {
  // the fields of a struct that go into a tuple, in order
  List<StructField> StructTupleFields(string type, Node at) {
    var fields = structs[type].Fields;
    if (fields.Count < 2) {
      throw Error($"the struct '{type}' has one field: a tuple has at least 2 items (spec 3.2)", at);
    }
    foreach (var field in fields) {
      if (field.Buffer != null || IsClass(field.Type) || StructHoldsObjects(field.Type)) {
        throw NotSupported($"converting to a tuple a struct with {(field.Buffer != null ? "a buffer" : "an object")} ('{field.Name}' of {type})", at);
      }
    }
    return fields;
  }

  bool StructToTupleAssignable(string from, string to) {
    var fields = structs[from].Fields;
    var items = TupleElems(to);
    return fields.Count >= 2 && fields.Count == items.Count
      && fields.Zip(items).All(pair => pair.First.Buffer == null && Assignable(pair.Second.Type, pair.First.Type));
  }

  // ({ S s = value; (Tuple){ s.f_a, s.f_b }; }): each field converted to its item; a string item gets a copy unless the struct is new
  string StructToTupleCode(Expr e, string to, Node at) {
    var fields = StructTupleFields(e.Type, at);
    var items = TupleElems(to);
    if (fields.Count != items.Count) {
      throw Error($"the struct '{e.Type}' has {fields.Count} fields, the tuple {items.Count} items", at);
    }
    string held = NewTemp();
    var parts = new List<string>();
    for (int index = 0; index < fields.Count; index++) {
      var item = new Expr(StructFieldRead(held, fields[index]), fields[index].Type, e.Fresh);
      string converted = Convert(item, items[index].Type, at);
      parts.Add(items[index].Type is "string" or "string?" ? StrOwnedAs(item, items[index].Type, converted) : converted);
    }
    return $"({{ {CType(e.Type)} {held} = {e.Code}; ({CType(to)}){{ {string.Join(", ", parts)} }}; }})";
  }

  // s.hash: the fields combined as the items of a tuple; a buffer adds each of its items
  string StructHashCode(Expr e, Node at) {
    var info = structs[e.Type];
    string held = NewTemp();
    string hash = NewTemp();
    var steps = new List<string>();
    foreach (var field in info.Fields) {
      if (field.Buffer is long length) {
        string index = NewTemp();
        string item = HashCode(new Expr($"{held}.f_{field.Name}[{index}]", ArrElem(field.Type)), at);
        steps.Add($"for (df_uns {index} = 0; {index} < {length}; {index}++) {{ {hash} = df_hash_comb({hash}, {item}); }}");
      } else {
        steps.Add($"{hash} = df_hash_comb({hash}, {HashCode(new Expr(StructFieldRead(held, field), field.Type), at)});");
      }
    }
    // the first field starts the hash, as for tuples
    var first = info.Fields[0];
    string start = first.Buffer == null ? HashCode(new Expr(StructFieldRead(held, first), first.Type), at) : "((df_uns)0)";
    var rest = first.Buffer == null ? steps.Skip(1) : steps;
    return $"({{ {info.CName} {held} = {e.Code}; df_uns {hash} = {start}; {string.Join(" ", rest)} {hash}; }})";
  }
}
