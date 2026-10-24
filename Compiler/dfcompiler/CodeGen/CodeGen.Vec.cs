using System.Text;
using dfparser;

namespace dfcompiler;

// Vectors vec<T> (spec 6.8) and the collection traits (spec 6.12), first slice.
// vec<T> is a generic class written in Dflat (VecPrelude) on top of arr<T>: a growable block with a count. The compiler adds it to the program when the
// program mentions vec (AddPrelude) and makes the copy for each element type like for any generic class. The hot paths are not calls: c[i], c[i] = v,
// .len and the for loop are emitted as C that reads the fields of the object directly (VecIndex, VecIndexAssign, EmitForEach), and the members that
// exist on arr (has, indexOf, fill, reverse, sort, sortUnstable) work on a view of the live items (VecView) with the code of the arrays.
// Traits: Iterator (a marker), IndexIterator<Item> (next(); each iterator keeps its own cursor) and IndexCollection<Item> (the length and getIterator()).
// Not built yet: Collection<Item> (needs a generic parent trait), the other collections, views, ownership of the items (the items are not dropped).
public sealed partial class CodeGen {
  bool vecPrelude;
  const int LibraryLine = 900000;   // the lines of the Dflat code the compiler adds for its collections

  const string VecPrelude = """
trait Iterator { }

// the cursor is internal to each iterator, the trait fixes only the methods (spec 6.7.1)
trait IndexIterator<Item> : Iterator {
  fun (Item value, bool isValid) next()
}

trait Collection<Item> {
  fun uns get_len()
  fun bool has(Item value)
  fun clear()
}

trait IndexCollection<Item> : Collection<Item> {
  fun IndexIterator<Item> getIterator()
}

// the iterator of a vec (spec 6.7): a child of its vec (12.5). While it exists the vec counts it as open (spec 6.4) and cannot change its size;
// its atEnd gives the counter back. An iterator whose vec ended raises UsageOfDestroyedObjectException
public class vecIterator<T>(&vec<T> collection as parent) : IndexIterator<T> {
  uns cursor = 0

  override fun (T value, bool isValid) next() {
    T none
    if cursor >= collection.len {
      return (none, false)
    }
    T item = collection[cursor]
    cursor += 1
    return (item, true)
  }
} atEnd {
  if collection != null {
    collection.releaseIterator()
  }
}

// the iterator of a list: a child of its list that keeps a reference to the current node (spec 6.8.3); one step follows the link, O(1)
public class listIterator<T>(&list<T> collection as parent) : IndexIterator<T> {
  u64 node              // the current node: a pointer to a node in the C (CodeGen.List.cs)
  bool started = false

  override fun (T value, bool isValid) next() {
    T none
    if !this.node_more() {
      return (none, false)
    }
    T item = this.node_value()
    this.node_advance()
    return (item, true)
  }
} atEnd {
  if collection != null {
    collection.releaseIterator()
  }
}

// views (spec 6.5): a read-only part of a collection, no copy. The view of a vec or a list is a child of its collection and counts as open while it
// exists; the view of an arr keeps the array (an arr cannot change its size). c[a..b] makes them (CodeGen.List.cs, ViewExpr)
public class vecView<T>(&vec<T> collection as parent, uns start, uns count) {
  public fun uns get_len() { count }

  public fun T read_pos(uns index) {
    if index >= count {
      throw IndexOutOfRangeException("Index Out Of Range Exception")
    }
    return collection[start + index]
  }

  public fun uns indexOf(T value) {
    uns i = 0
    while i < count {
      if collection[start + i] == value {
        return i
      }
      i += 1
    }
    return UNS_NONE
  }

  public fun bool has(T value) {
    return indexOf(value) != UNS_NONE
  }
} atEnd {
  if collection != null {
    collection.releaseIterator()
  }
}

public class listView<T>(&list<T> collection as parent, uns start, uns count) {
  public fun uns get_len() { count }

  public fun T read_pos(uns index) {
    if index >= count {
      throw IndexOutOfRangeException("Index Out Of Range Exception")
    }
    return collection[start + index]
  }

  public fun uns indexOf(T value) {
    uns found = collection.indexOfFrom(start, count, value)
    return found
  }

  public fun bool has(T value) {
    return indexOf(value) != UNS_NONE
  }
} atEnd {
  if collection != null {
    collection.releaseIterator()
  }
}

public class arrView<T>(arr<T> items, uns start, uns count) {
  public fun uns get_len() { count }

  public fun T read_pos(uns index) {
    if index >= count {
      throw IndexOutOfRangeException("Index Out Of Range Exception")
    }
    return items[start + index]
  }

  public fun uns indexOf(T value) {
    uns i = 0
    while i < count {
      if items[start + i] == value {
        return i
      }
      i += 1
    }
    return UNS_NONE
  }

  public fun bool has(T value) {
    return indexOf(value) != UNS_NONE
  }
}

// the iterator of an arr<T>
public class arrIterator<T>(arr<T> items) : IndexIterator<T> {
  uns cursor = 0

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
  uns count = 0
  uns iterators = 0   // the open iterators and views (spec 6.4): while above zero the size cannot change

  override fun uns get_len() { count }

  fun checkUnlocked() {
    if iterators > 0 {
      throw ModifyingIteratedCollectionException("Modifying Iterated Collection Exception")
    }
  }

  override fun bool has(T value) {
    uns i = 0
    while i < count {
      if items[i] == value {
        return true
      }
      i += 1
    }
    return false
  }
  public fun uns get_capacity() { items.len }

  override fun IndexIterator<T> getIterator() {
    iterators += 1
    vecIterator<T> iterator = vecIterator<T>(this)
    return ->iterator
  }

  // called by the atEnd of the iterators and views of the vec (spec 6.4, 12.5)
  asparent fun releaseIterator() {
    if iterators == 0 {
      throw InvalidIteratorReleaseException("Invalid Iterator Release Exception")
    }
    iterators -= 1
  }

  fun grow(uns needed) {
    uns newCapacity = items.len * 2
    if newCapacity < 4 {
      newCapacity = 4
    }
    if newCapacity < needed {
      newCapacity = needed
    }
    resizeStorage(newCapacity)
  }

  fun resizeStorage(uns newCapacity) {
    arr<T> bigger = arr<T>[newCapacity]
    uns i = 0
    while i < count {
      bigger[i] = ->items[i]
      i += 1
    }
    items = bigger
  }

  public fun add(->T value) {
    checkUnlocked()
    if count == items.len {
      grow(count + 1)
    }
    items[count] = ->value
    count += 1
  }

  public fun insert(uns index, ->T value) {
    checkUnlocked()
    if index > count {
      throw IndexOutOfRangeException("Index Out Of Range Exception")
    }
    if count == items.len {
      grow(count + 1)
    }
    uns i = count
    while i > index {
      items[i] = ->items[i - 1]
      i -= 1
    }
    items[index] = ->value
    count += 1
  }

  public fun removeAt(uns index) {
    checkUnlocked()
    if index >= count {
      throw IndexOutOfRangeException("Index Out Of Range Exception")
    }
    T gone = ->items[index]
    uns i = index
    while i + 1 < count {
      items[i] = ->items[i + 1]
      i += 1
    }
    count -= 1
  }

  public fun bool remove(T value) {
    uns i = 0
    while i < count {
      if items[i] == value {
        removeAt(i)
        return true
      }
      i += 1
    }
    return false
  }

  public fun uns removeAll(T value) {
    checkUnlocked()
    uns kept = 0
    uns i = 0
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
    uns removed = count - kept
    count = kept
    return removed
  }

  // every item gets its default value and the size is kept (spec 6.6): the objects held end
  override fun clear() {
    T none
    uns i = 0
    while i < count {
      items[i] = none
      i += 1
    }
  }

  public fun reserve(uns extra) {
    checkUnlocked()
    if count + extra > items.len {
      resizeStorage(count + extra)
    }
  }

  public fun shrinkToFit() {
    checkUnlocked()
    if items.len > count {
      resizeStorage(count)
    }
  }

  public fun fillAtCapacity(T value) {
    checkUnlocked()
    uns i = 0
    while i < items.len {
      items[i] = value
      i += 1
    }
    count = items.len
  }

  public fun resize(uns newLength, T value) {
    checkUnlocked()
    if newLength > items.len {
      resizeStorage(newLength)
    }
    uns i = count
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

  public fun truncate(uns length) {
    checkUnlocked()
    if length < count {
      uns i = length
      while i < count {
        T gone = ->items[i]
        i += 1
      }
      count = length
    }
  }
}

// list<T> (spec 6.8.3): a singly linked list. Every node is allocated on its own and holds the link to the next node and the item; the list keeps the
// count, the first and the last node. The methods check and keep the rules; the node_ calls are the C of the compiler that works on the nodes (CodeGen.List.cs).
public class list<T> : IndexCollection<T> {
  u64 first           // the first node: a pointer to a node in the C (CodeGen.List.cs)
  u64 last            // the last node, only to add at the end
  uns count = 0
  uns iterators = 0   // the open iterators and views (spec 6.4): while above zero the size cannot change

  override fun uns get_len() { count }

  fun checkUnlocked() {
    if iterators > 0 {
      throw ModifyingIteratedCollectionException("Modifying Iterated Collection Exception")
    }
  }

  public fun add(->T value) {
    checkUnlocked()
    this.node_append(->value)
  }

  public fun insert(uns index, ->T value) {
    checkUnlocked()
    if index > count {
      throw IndexOutOfRangeException("Index Out Of Range Exception")
    }
    this.node_insert(index, ->value)
  }

  public fun T takeAt(uns index) {
    checkUnlocked()
    if index >= count {
      throw IndexOutOfRangeException("Index Out Of Range Exception")
    }
    return this.node_take(index)
  }

  public fun removeAt(uns index) {
    T gone = takeAt(index)
  }

  public fun uns indexOf(T value) {
    return this.node_indexOf(value)
  }

  override fun bool has(T value) {
    return this.node_indexOf(value) != UNS_NONE
  }

  public fun bool remove(T value) {
    uns index = indexOf(value)
    if index == UNS_NONE {
      return false
    }
    removeAt(index)
    return true
  }

  public fun uns removeAll(T value) {
    checkUnlocked()
    return this.node_removeAll(value)
  }

  // every item gets its default value and the size is kept (spec 6.6): the objects held end
  override fun clear() {
    this.node_clear()
  }

  public fun fill(T value) {
    this.node_fill(value)
  }

  public fun fill(uns amount, T value) {
    checkUnlocked()
    this.node_fillCount(amount, value)
  }

  public fun reverse() {
    checkUnlocked()
    this.node_reverse()
  }

  public fun truncate(uns length) {
    checkUnlocked()
    this.node_truncate(length)
  }

  public fun exchange(uns a, uns b) {
    if a >= count || b >= count {
      throw IndexOutOfRangeException("Index Out Of Range Exception")
    }
    this.node_exchange(a, b)
  }

  public fun T replace(uns position, ->T value) {
    if position >= count {
      throw IndexOutOfRangeException("Index Out Of Range Exception")
    }
    return this.node_replace(position, ->value)
  }

  override fun IndexIterator<T> getIterator() {
    iterators += 1
    listIterator<T> iterator = listIterator<T>(this)
    return ->iterator
  }

  // called by the atEnd of the iterators and views of the list (spec 6.4, 12.5)
  asparent fun releaseIterator() {
    if iterators == 0 {
      throw InvalidIteratorReleaseException("Invalid Iterator Release Exception")
    }
    iterators -= 1
  }

  // the position of value among count items from start, relative to start (the indexOf of a list view)
  asparent fun uns indexOfFrom(uns start, uns amount, T value) {
    return this.node_indexOfFrom(start, amount, value)
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
    string code = WithTemp(new Expr(NullChecked(obj.Code, obj.Type), obj.Type), vec => WithTemp(new Expr(IndexValue(index, n), NativeUns), position =>
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
    string code = $"({{ df_uns {count} = {IndexValue(size, sizeNode)}; {CType(type)} {held} = {VecNewEmpty(type)}; "
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
      var value = Adopted(ExprOf(item, scope, elem), item);
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
    if (operand.Children[0].Kind != "Index" && IsView(ExprOf(operand.Children[0], scope).Type)) {
      throw Error("a view is read-only: its items cannot be moved out (spec 6.5)", gift);
    }
    if (ReadOnlyVariable(operand.Children[0], scope) is not null) {
      throw Error($"'{operand.Children[0].Text}' is a read-only reference (&): its items cannot be moved out", gift);
    }
    var obj = ExprOf(operand.Children[0], scope);
    if (IsList(obj.Type)) {
      // the item leaves the list and the node goes away (spec 6.6)
      var removed = ObjectCall(obj, "takeAt", new List<Node> { operand.Children[1] }, scope, gift) ?? throw Error("no takeAt", gift);
      return new Expr(removed.Code, removed.Type, IsClass(removed.Type));
    }
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
    string code = CheckRaise($"({{ {CType(obj.Type)} {holder} = {source}; df_uns {position} = {IndexValue(index, gift)}; "
      + $"{CType(elem)} {taken} = NULL; if (df_idx_ok({length}, {position}, {fn!.NameConst})) {{ {taken} = {items}.p[{position}]; {items}.p[{position}] = NULL; }} {taken}; }})", elem);
    return new Expr(code, elem, true);
  }

  // the end of a vec: the items it owns end, then its block of memory is freed
  void EmitVecDropGlue(StringBuilder sb, ClassInfo cls) {
    string items = VecField(cls.Name, "items");
    string count = VecField(cls.Name, "count");
    if (IsClass(VecElem(cls.Name))) {
      sb.Append($"  for (df_uns i = 0; i < self->{count}; i++) {{ if (self->{items}.p[i] != NULL) {{ void* o = (void*)self->{items}.p[i]; self->{items}.p[i] = NULL; df_drop(o); }} }}\n");
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
    string code = $"({{ {CType(vec.Type)} {held} = {NullChecked(vec.Code, vec.Type)}; bool {ok} = false; {CType(elem)} {incoming} = {zero}; {LockCheck(held, vec.Type)}"
      + $"if ({held}->{count} > 0) {{ {held}->{count} -= 1; {incoming} = {held}->{items}.p[{held}->{count}]; {held}->{items}.p[{held}->{count}] = {zero}; {ok} = true; }} "
      + $"{CType(target.Type)} {old} = {target.Code}; {target.Code} = {store}; "
      + (owned ? $"if ({old} != {target.Code}) {{ df_drop((void*){old}); }} " : "")
      + $"{ok}; }})";
    return new Expr(owned ? CheckRaise(code, "bool") : code, "bool");
  }

  // members of a vec that the arrays already have: they work on the live items
  static readonly HashSet<string> VecArrayMembers = new() { "has", "indexOf", "fill", "reverse", "sort", "sortUnstable", "swap", "replace" };

  // the ones that reorder the items cannot run while the vec has open iterators (spec 6.4); fill(value) and swap can
  static readonly HashSet<string> VecChangingMembers = new() { "reverse", "sort", "sortUnstable" };

  // room for at least needed items (the C of grow in VecPrelude: double, at least 4, at least what is needed); the new slots are zero
  string VecReserve(string held, string type, string needed) {
    string elemType = CType(VecElem(type));
    string items = $"{held}->{VecField(type, "items")}";
    string want = NewTemp();
    string capacity = NewTemp();
    string bigger = NewTemp();
    return $"df_uns {want} = {needed}; if ({want} > {items}.len) {{ df_uns {capacity} = {items}.len * 2; if ({capacity} < 4) {{ {capacity} = 4; }} if ({capacity} < {want}) {{ {capacity} = {want}; }} "
      + $"{elemType}* {bigger} = ({elemType}*)df_calloc({capacity}, sizeof({elemType})); if ({held}->{VecField(type, "count")} > 0) {{ memcpy({bigger}, {items}.p, {held}->{VecField(type, "count")} * sizeof({elemType})); }} "
      + $"free({items}.p); {items}.p = {bigger}; {items}.len = {capacity}; }} ";
  }

  // the members of a vec written in C here: add(collection), removeAt(range), copyFrom (spec 6.6)
  Expr? VecMember(Expr vec, string name, List<Node> args, Scope scope, Node at) {
    string elem = VecElem(vec.Type);
    if (name == "add" && args.Count == 1 && AddedCollectionType(args[0], scope, elem) is string sourceType) {
      return VecAddCollection(vec, args[0], sourceType, scope, at);
    }
    if (name == "removeAt" && args.Count == 1 && args[0].Kind == "Range") {
      return VecRemoveRange(vec, args[0], scope);
    }
    if (name == "copyFrom") {
      return CollectionCopyFrom(vec, args, scope, at);
    }
    return null;
  }

  // vec.add(other) copies the items of an index collection at the end; vec.add(->other) moves them, and the collection given ends (spec 6.6)
  Expr VecAddCollection(Expr vec, Node arg, string sourceType, Scope scope, Node at) {
    string elem = VecElem(vec.Type);
    bool moving = arg.Kind == "Gift";
    if (IsClass(elem) && !moving) {
      throw Error("add(collection) with objects moves them: write add(->other) (a copy needs cloning, not built yet)", at);
    }
    var source = ExprOf(arg, scope);
    string held = NewTemp();
    string s = NewTemp();
    string amount = NewTemp();
    bool sourceIsObject = !IsArr(sourceType);
    string items = $"{held}->{VecField(vec.Type, "items")}";
    string count = $"{held}->{VecField(vec.Type, "count")}";
    var sb = new StringBuilder();
    sb.Append($"({{ {CType(vec.Type)} {held} = {NullChecked(vec.Code, vec.Type)}; {CType(sourceType)} {s} = {(sourceIsObject ? NullChecked(source.Code, sourceType) : source.Code)}; ");
    if (sourceType == vec.Type) {
      sb.Append($"if ({s} == {held}) {{ df_raise(&df_desc_ModifyingProtectedSourceException, {fn!.NameConst}); {RaiseJump} }} ");
    }
    sb.Append(LockCheck(held, vec.Type));
    sb.Append($"df_uns {amount} = {SourceLength(sourceType, s)}; {VecReserve(held, vec.Type, $"{count} + {amount}")}");
    sb.Append(ForEachSourceItem(sourceType, s, "0", amount, item =>
      $"{items}.p[{count}] = {item}; {count} += 1; " + (moving && IsClass(elem) ? $"{item} = NULL; " : "")));
    if (moving && sourceIsObject) {
      sb.Append($"df_drop((void*){s}); ");   // the collection was moved in: its items are gone and it ends
    }
    sb.Append("})");
    return new Expr(sb.ToString(), "void");
  }

  // vec.removeAt(a..b), a..=b, ..b, a..: the items of the range end, the ones after it move down (spec 6.6)
  Expr VecRemoveRange(Expr vec, Node range, Scope scope) {
    string elem = VecElem(vec.Type);
    string held = NewTemp();
    string start = NewTemp();
    string end = NewTemp();
    string step = NewTemp();
    string items = $"{held}->{VecField(vec.Type, "items")}";
    string count = $"{held}->{VecField(vec.Type, "count")}";
    string outside = $"{{ df_raise(&df_desc_IndexOutOfRangeException, {fn!.NameConst}); {RaiseJump} }}";
    var sb = new StringBuilder();
    sb.Append($"({{ {CType(vec.Type)} {held} = {NullChecked(vec.Code, vec.Type)}; ");
    sb.Append($"df_uns {start} = {(range.Children[0].Kind == "Open" ? "0" : IndexArgument(range.Children[0], scope))}; ");
    if (range.Children[1].Kind == "Open") {
      sb.Append($"df_uns {end} = {count}; ");
    } else if (range.Text == "..=") {
      sb.Append($"df_uns {end} = {IndexArgument(range.Children[1], scope)}; if ({end} == DF_UNS_MAX) {outside} {end} += 1; ");
    } else {
      sb.Append($"df_uns {end} = {IndexArgument(range.Children[1], scope)}; ");
    }
    sb.Append($"if ({start} > {end} || {end} > {count}) {outside} ");
    sb.Append(LockCheck(held, vec.Type));
    sb.Append($"for (df_uns {step} = {start}; {step} < {end}; {step}++) {{ {DropListItem(elem, $"{items}.p[{step}]")}}} ");
    sb.Append($"if ({end} < {count}) {{ memmove({items}.p + {start}, {items}.p + {end}, ({count} - {end}) * sizeof(*{items}.p)); }} ");
    sb.Append($"for (df_uns {step} = {count} - ({end} - {start}); {step} < {count}; {step}++) {{ {items}.p[{step}] = {ZeroValue(elem)}; }} ");
    sb.Append($"{count} -= {end} - {start}; }})");
    return new Expr(sb.ToString(), "void");
  }

  // a change of the size or of the order while the collection has open iterators or views (spec 6.4): ModifyingIteratedCollectionException
  string LockCheck(string held, string type) =>
    $"if ({held}->{VecField(type, "iterators")} > 0) {{ df_raise(&df_desc_ModifyingIteratedCollectionException, {fn!.NameConst}); {RaiseJump} }} ";

  Expr? VecArrayMember(Expr vec, string name, List<Node> args, Scope scope, Node at) {
    if (!VecChangingMembers.Contains(name)) {
      return ArrayMethod(VecView(vec), name, args, scope, at);
    }
    string held = NewTemp();
    var inner = ArrayMethod(VecView(new Expr(held, vec.Type)), name, args, scope, at);
    if (inner == null) {
      return null;
    }
    string code = $"({{ {CType(vec.Type)} {held} = {NullChecked(vec.Code, vec.Type)}; {LockCheck(held, vec.Type)}{inner.Value.Code}; }})";
    return new Expr(code, "void");
  }
}
