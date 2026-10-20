using System.Text;
using dfparser;

namespace dfcompiler;

// Vectors vec<T> (spec 6.8) and the collection traits (spec 6.12), first slice.
// vec<T> is a generic class written in Dflat (VecPrelude) on top of arr<T>: a growable block with a count. The compiler adds it to the program when the
// program mentions vec (AddPrelude) and makes the copy for each element type like for any generic class. The hot paths are not calls: c[i], c[i] = v,
// .len and the for loop are emitted as C that reads the fields of the object directly (VecIndex, VecIndexAssign, EmitForEach), and the members that
// exist on arr (has, indexOf, fill, reverse, sort, sortUnstable) work on a view of the live items (VecView) with the code of the arrays.
// Traits: Iterator (a marker), IndexIterator<Item> (a cursor and next()) and IndexCollection<Item> (the length and getIterator()).
// Not built yet: Collection<Item> (needs a generic parent trait), the other collections, views, ownership of the items (the items are not dropped).
public sealed partial class CodeGen {
  bool vecPrelude;
  const int LibraryLine = 900000;   // the lines of the Dflat code the compiler adds for its collections

  const string VecPrelude = """
trait Iterator { }

trait IndexIterator<Item> : Iterator {
  u64 cursor
  fun (Item value, bool isValid) next()
}

trait IndexCollection<Item> {
  fun u64 get_len()
  fun IndexIterator<Item> getIterator()
}

// the iterator holds the items and the count as they were when it was made (the vector cannot change while it is iterated, spec 6.4)
public class vecIterator<T>(arr<T> items, u64 count) : IndexIterator<T> {
  override fun (T value, bool isValid) next() {
    T none
    if cursor >= count {
      return (none, false)
    }
    T item = items[cursor]
    cursor += 1
    return (item, true)
  }
}

// the iterator of an arr<T>
public class arrIterator<T>(arr<T> items) : IndexIterator<T> {
  override fun (T value, bool isValid) next() {
    T none
    if cursor >= items.len {
      return (none, false)
    }
    T item = items[cursor]
    cursor += 1
    return (item, true)
  }
}

public class vec<T> : IndexCollection<T> {
  arr<T> items = arr<T>[0]
  u64 count = 0

  override fun u64 get_len() { count }
  public fun u64 get_capacity() { items.len }

  override fun IndexIterator<T> getIterator() {
    return vecIterator<T>(items, count)
  }

  fun grow(u64 needed) {
    u64 newCapacity = items.len * 2
    if newCapacity < 4 {
      newCapacity = 4
    }
    if newCapacity < needed {
      newCapacity = needed
    }
    resizeStorage(newCapacity)
  }

  fun resizeStorage(u64 newCapacity) {
    arr<T> bigger = arr<T>[newCapacity]
    u64 i = 0
    while i < count {
      bigger[i] = ->items[i]
      i += 1
    }
    items = bigger
  }

  public fun add(->T value) {
    if count == items.len {
      grow(count + 1)
    }
    items[count] = ->value
    count += 1
  }

  public fun insert(u64 index, ->T value) {
    if index > count {
      throw IndexOutOfRangeException("Index Out Of Range Exception")
    }
    if count == items.len {
      grow(count + 1)
    }
    u64 i = count
    while i > index {
      items[i] = ->items[i - 1]
      i -= 1
    }
    items[index] = ->value
    count += 1
  }

  public fun removeAt(u64 index) {
    if index >= count {
      throw IndexOutOfRangeException("Index Out Of Range Exception")
    }
    T gone = ->items[index]
    u64 i = index
    while i + 1 < count {
      items[i] = ->items[i + 1]
      i += 1
    }
    count -= 1
  }

  public fun bool remove(T value) {
    u64 i = 0
    while i < count {
      if items[i] == value {
        removeAt(i)
        return true
      }
      i += 1
    }
    return false
  }

  public fun u64 removeAll(T value) {
    u64 kept = 0
    u64 i = 0
    while i < count {
      if items[i] == value {
        T gone = ->items[i]
      } else {
        if kept != i {
          items[kept] = ->items[i]
        }
        kept += 1
      }
      i += 1
    }
    u64 removed = count - kept
    count = kept
    return removed
  }

  public fun clear() {
    u64 i = 0
    while i < count {
      T gone = ->items[i]
      i += 1
    }
    count = 0
  }

  public fun reserve(u64 extra) {
    if count + extra > items.len {
      resizeStorage(count + extra)
    }
  }

  public fun shrinkToFit() {
    if items.len > count {
      resizeStorage(count)
    }
  }

  public fun fillAtCapacity(T value) {
    u64 i = 0
    while i < items.len {
      items[i] = value
      i += 1
    }
    count = items.len
  }

  public fun resize(u64 newLength, T value) {
    if newLength > items.len {
      resizeStorage(newLength)
    }
    u64 i = count
    while i < newLength {
      items[i] = value
      i += 1
    }
    i = newLength
    while i < count {
      T gone = ->items[i]
      i += 1
    }
    count = newLength
  }

  public fun truncate(u64 length) {
    if length < count {
      u64 i = length
      while i < count {
        T gone = ->items[i]
        i += 1
      }
      count = length
    }
  }

  public fun swap(u64 first, u64 second) {
    if first >= count || second >= count {
      throw IndexOutOfRangeException("Index Out Of Range Exception")
    }
    T held = ->items[first]
    items[first] = ->items[second]
    items[second] = ->held
  }
}
""";

  // the vec copy a type name stands for (the name of the instance of the generic class vec), or false
  bool IsVec(string type) => vecPrelude && classInstanceArgs.TryGetValue(type, out var instance) && instance.Template.Name == "vec";

  string VecElem(string type) => classInstanceArgs[type].Args[0];

  string VecField(string type, string name) => FindField(ObjInfo(type), name)!.CName;

  string VecArrType(string vecType) => "arr<" + VecElem(vecType) + ">";

  // the live items as an array value (pointer and count): the arrays' own code works on it
  Expr VecView(Expr vec) {
    string arrType = VecArrType(vec.Type);
    string held = NewTemp();
    string code = $"({{ {CType(vec.Type)} {held} = {NullChecked(vec.Code, vec.Type)}; ({CType(arrType)}){{ {held}->{VecField(vec.Type, "items")}.p, {held}->{VecField(vec.Type, "count")} }}; }})";
    return new Expr(code, arrType);
  }

  // C function that prints a vec as [a, b, c] (null for a null vec)
  string VecStrFunction(string vecType, Node at) {
    string name = "df_vec_str_" + System.Text.RegularExpressions.Regex.Replace(vecType, @"[^A-Za-z0-9_]", "_");
    if (arrayHelpers.Add(name)) {
      string elem = VecElem(vecType);
      string arrName = ArrStrFunction(elem, at);
      arrayCode.Append($"static df_string {name}({CType(vecType)} v) {{\n");
      arrayCode.Append("  if (v == NULL) {\n    return df_str_lit(\"null\", 4);\n  }\n");
      arrayCode.Append($"  {ArrCType(elem)} a = {{ v->{VecField(vecType, "items")}.p, v->{VecField(vecType, "count")} }};\n");
      arrayCode.Append($"  return {arrName}(a);\n}}\n\n");
    }
    return name;
  }

  // v[i]
  Expr VecIndex(Expr obj, Expr index, Node n) {
    string elem = VecElem(obj.Type);
    string items = VecField(obj.Type, "items");
    string count = VecField(obj.Type, "count");
    string code = WithTemp(new Expr(NullChecked(obj.Code, obj.Type), obj.Type), vec => WithTemp(new Expr(IndexValue(index, n), "u64"), position =>
      CheckRaise($"(df_idx_ok({vec}->{count}, {position}, {fn!.NameConst}) ? {vec}->{items}.p[{position}] : {ZeroValue(elem)})", elem)));
    return new Expr(code, elem);
  }

  // the constructor call of a copy of vec: an empty vector
  string VecNewEmpty(string vecType) {
    var ctor = funs[vecType][0];
    return UserCallCode(ctor, $"{ctor.CName}()");
  }

  // vec<T>[capacity]: empty, with room for that many items
  Expr VecNewWithCapacity(Node generic, Node sizeNode, Scope scope) {
    if (generic.Children.Count != 2) {
      throw Error("vec needs one element type: vec<T>[capacity]", generic);
    }
    string type = ResolveType(Synthetic("Type", generic, "vec").Add(generic.Children[1]));
    string elem = VecElem(type);
    var size = ExprOf(sizeNode, scope, "u64");
    string held = NewTemp();
    string count = NewTemp();
    string code = $"({{ uint64_t {count} = {IndexValue(size, sizeNode)}; {CType(type)} {held} = {VecNewEmpty(type)}; "
      + $"{held}->{VecField(type, "items")} = ({ArrCType(elem)}){{ ({CType(elem)}*)df_calloc({count}, sizeof({CType(elem)})), {count} }}; {held}; }})";
    return new Expr(code, type, true);
  }

  // [a, b, c] assigned to a vec
  Expr VecLiteral(Node list, Scope scope, string type) {
    string elem = VecElem(type);
    string held = NewTemp();
    int count = list.Children.Count;
    var sb = new StringBuilder();
    sb.Append($"({{ {CType(type)} {held} = {VecNewEmpty(type)}; ");
    if (count > 0) {
      sb.Append($"{held}->{VecField(type, "items")} = ({ArrCType(elem)}){{ ({CType(elem)}*)df_calloc({count}, sizeof({CType(elem)})), {count} }}; ");
    }
    for (int index = 0; index < count; index++) {
      var item = list.Children[index];
      var value = ExprOf(item, scope, elem);
      if (IsClass(elem) && !value.Fresh && value.Type != "null") {
        throw Error("a vec owns its items: put a new object in the literal, or move one in with ->x", item);
      }
      sb.Append($"{held}->{VecField(type, "items")}.p[{index}] = {Convert(value, elem, item)}; ");
    }
    sb.Append($"{held}->{VecField(type, "count")} = {count}; {held}; }})");
    return new Expr(sb.ToString(), type, true);
  }

  // the type of a bare 'vec' declared with a literal: vec values = [1, 2, 3]
  string InferVecType(Node list, Scope scope, Node at) {
    if (list.Children.Count == 0) {
      throw Error("an empty literal needs a declared type: vec<T>", list);
    }
    var values = list.Children.Select(item => ExprOf(item, scope)).ToList();
    return ClassInstanceName(genericClasses["vec"], new List<string> { CommonType(values, list) }, at);
  }

  // arr.getIterator(): an object that implements IndexIterator<T> and holds a cursor
  Expr ArrayIterator(Expr array, Node at) {
    if (!vecPrelude) {
      throw Error("the iterator of an array is not available here", at);
    }
    string name = ClassInstanceName(genericClasses["arrIterator"], new List<string> { ArrElem(array.Type) }, at);
    var ctor = funs[name][0];
    return new Expr(UserCallCode(ctor, $"{ctor.CName}({array.Code})"), name, true);
  }

  // a class of the program that implements IndexCollection<T>: for x in c asks getIterator() once and calls next() until isValid is false (spec 6.12)
  string? CustomCollectionElem(string type) {
    if (!vecPrelude || !classes.TryGetValue(type, out var cls) || IsVec(type)) {
      return null;
    }
    foreach (var trait in AllTraits(cls)) {
      if (classInstanceArgs.TryGetValue(trait.Name, out var copy) && copy.Template.Name == "IndexCollection") {
        return copy.Args[0];
      }
    }
    return null;
  }

  // ->v[i]: the item goes to the receiver and the slot is zeroed (spec 6.6); a basic value is copied
  Expr GiftIndex(Node gift, Node operand, Scope scope) {
    var obj = ExprOf(operand.Children[0], scope);
    bool isVec = IsVec(obj.Type);
    if (!isVec && !IsArr(obj.Type)) {
      throw Error("only a variable, a field or an item of an array or a vector can be moved with ->", gift);
    }
    string elem = isVec ? VecElem(obj.Type) : ArrElem(obj.Type);
    if (!IsClass(elem)) {
      Warn("-> on a basic value is ignored: it is a copy", gift, 1);
      return IndexExpr(operand, scope);
    }
    var index = ExprOf(operand.Children[1], scope, "u64");
    string holder = NewTemp();
    string position = NewTemp();
    string taken = NewTemp();
    string items = isVec ? $"{holder}->{VecField(obj.Type, "items")}" : holder;
    string length = isVec ? $"{holder}->{VecField(obj.Type, "count")}" : $"{holder}.len";
    string source = isVec ? NullChecked(obj.Code, obj.Type) : obj.Code;
    string code = CheckRaise($"({{ {CType(obj.Type)} {holder} = {source}; uint64_t {position} = {IndexValue(index, gift)}; "
      + $"{CType(elem)} {taken} = NULL; if (df_idx_ok({length}, {position}, {fn!.NameConst})) {{ {taken} = {items}.p[{position}]; {items}.p[{position}] = NULL; }} {taken}; }})", elem);
    return new Expr(code, elem, true);
  }

  // the end of a vec: the items it owns end, then its block of memory is freed
  void EmitVecDropGlue(StringBuilder sb, ClassInfo cls) {
    string items = VecField(cls.Name, "items");
    string count = VecField(cls.Name, "count");
    if (IsClass(VecElem(cls.Name))) {
      sb.Append($"  for (uint64_t i = 0; i < self->{count}; i++) {{ if (self->{items}.p[i] != NULL) {{ void* o = (void*)self->{items}.p[i]; self->{items}.p[i] = NULL; df_drop(o); }} }}\n");
    }
    sb.Append($"  free(self->{items}.p);\n  self->{items}.p = NULL;\n  self->{items}.len = 0;\n  self->{count} = 0;\n");
  }

  // v.pop((&)x): the last item moves into x (the old value of x ends); false and the default value when the vector is empty (spec 6.6)
  Expr VecPop(Expr vec, List<Node> args, Scope scope, Node at) {
    if (args.Count != 1 || args[0].Kind != "WritableAddressOf") {
      throw Error("pop takes one writable variable: pop((&)x)", at);
    }
    var targetNode = args[0].Children[0];
    var target = LValue(targetNode, scope);
    if (target.IsConst) {
      throw Error($"'{targetNode.Text}' is const", at);
    }
    string elem = VecElem(vec.Type);
    if (target.Type != elem && !Assignable(target.Type, elem)) {
      throw Error($"pop gives {elem}, but '{targetNode.Text}' is {target.Type}", at);
    }
    MarkAssigned(target.Variable);
    string items = VecField(vec.Type, "items");
    string count = VecField(vec.Type, "count");
    string held = NewTemp();
    string ok = NewTemp();
    string incoming = NewTemp();
    string old = NewTemp();
    bool owned = IsClass(target.Type) && (target.Variable?.Own ?? target.IsField);
    string zero = ZeroValue(elem);
    string store = Convert(new Expr(incoming, elem), target.Type, at);
    string code = $"({{ {CType(vec.Type)} {held} = {NullChecked(vec.Code, vec.Type)}; bool {ok} = false; {CType(elem)} {incoming} = {zero}; "
      + $"if ({held}->{count} > 0) {{ {held}->{count} -= 1; {incoming} = {held}->{items}.p[{held}->{count}]; {held}->{items}.p[{held}->{count}] = {zero}; {ok} = true; }} "
      + $"{CType(target.Type)} {old} = {target.Code}; {target.Code} = {store}; "
      + (owned ? $"if ({old} != {target.Code}) {{ df_drop((void*){old}); }} " : "")
      + $"{ok}; }})";
    return new Expr(owned ? CheckRaise(code, "bool") : code, "bool");
  }

  // members of a vec that the arrays already have: they work on the live items
  static readonly HashSet<string> VecArrayMembers = new() { "has", "indexOf", "fill", "reverse", "sort", "sortUnstable" };
}
