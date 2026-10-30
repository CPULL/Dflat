using dfparser;

namespace dfcompiler;

// Collections of counted objects (spec 12.7, 6.2): vec<@T> and list<@T>. The item of such a collection is a hidden box class Rc__T(@T value)
// with one counted field, so the collection code (VecPrelude and the C hot paths) sees plain owned objects: adding a box adds a user, dropping
// a box releases it. The program never sees the boxes: in its code an item is read as the object (UnboxRead, the for loop, CountedRef) and a
// value given as an item is put in a new box (BoxCall: the box's header parameter is @T, so the usual counted arguments apply). The members
// that would hand out boxes are not supported on these collections yet (CheckCountedItemsMember).
public sealed partial class CodeGen {
  readonly Dictionary<string, string> boxInner = new();   // box class -> the counted type of its items
  bool rawBoxRead;                                         // CountedRef wants the box of c[i], not its object

  // list<@Dog> becomes list<Rc__Dog>, with the class Rc__Dog(@Dog value) added to the program. Keys of maps and sets cannot be counted
  // (spec 6.2); map values, arr items and other generics with a counted type argument are not built yet
  void LowerCountedTypeArgs(Node program) {
    var boxes = new List<Node>();
    void Walk(Node node) {
      for (int index = 0; index < node.Children.Count; index++) {
        var child = node.Children[index];
        if (child.Kind == "CountedType") {
          node.Children[index] = BoxTypeFor(node, index, child, boxes);
          continue;
        }
        Walk(child);
      }
    }
    Walk(program);
    program.Children.InsertRange(0, boxes);
  }

  Node BoxTypeFor(Node owner, int index, Node counted, List<Node> boxes) {
    string? collection = owner.Kind == "Type" ? owner.Text : null;
    if (collection is "map" or "bmap" or "hmap" && index == 0 || collection is "set" or "bset" or "hset") {
      throw Error("counted objects (@T) cannot be keys of a map or items of a set: a key must be stable (spec 6.2)", counted);
    }
    if (collection is "map" or "bmap" or "hmap") {
      throw NotSupported("Counted values of a map (map<K, @T>)", counted);
    }
    if (collection is not ("vec" or "list")) {
      throw NotSupported(collection == "arr" ? "Counted items of an arr (arr<@T>)" : "Generics with a counted type argument (spec 12.7)", counted);
    }
    var inner = counted.Children[0];
    if (inner is not { Kind: "Type", Children.Count: 0 } || inner.Text == null) {
      throw NotSupported("Counted items of this type", counted);
    }
    string box = "Rc__" + inner.Text;
    if (!boxInner.ContainsKey(box)) {
      boxInner[box] = inner.Text;
      var param = Synthetic("Param", counted, "value").Add(Synthetic("Type", counted, inner.Text)).Add(Synthetic("Counted", counted));
      boxes.Add(Synthetic("Class", counted, box).Add(Synthetic("PrimaryCtor", counted).Add(param)).Add(Synthetic("Block", counted)));
    }
    return Synthetic("Type", counted, box);
  }

  // the counted type of the items of a box type, or null (also null in the code of the collections, which works on the boxes)
  string? BoxInner(string type) => !LibraryCode && boxInner.TryGetValue(type, out var inner) ? inner : null;

  string BoxField(string box) => FindField(classes[box], "value")!.CName;

  // a value given as an item: a new box that takes the usage (the header parameter of the box is @T)
  Node BoxCall(Node value, string box) =>
    Synthetic("Call", value).Add(Synthetic("Name", value, box)).Add(Synthetic("Args", value).Add(value));

  // c[i] of a collection of counted objects reads the object of the box
  Expr UnboxRead(Expr item) {
    if (rawBoxRead || BoxInner(item.Type) is not string inner) {
      return item;
    }
    return new Expr($"DF_RC_OBJ({CType(inner)}, ({item.Code})->{BoxField(item.Type)})", inner);
  }

  // the counting block of c[i] when c holds counted objects (for CountedRef), or null
  CountedRefInfo? CountedItemRef(Node index, Scope scope) {
    if (index.Kind != "Index" || LibraryCode || index.Children.Count < 2 || index.Children[1].Kind == "Range") {
      return null;
    }
    bool saved = rawBoxRead;
    rawBoxRead = true;
    Expr item;
    try {
      item = ExprOf(index, scope);
    } catch (CompileException) {
      return null;
    } finally {
      rawBoxRead = saved;
    }
    return BoxInner(item.Type) is string inner ? new CountedRefInfo($"({item.Code})->{BoxField(item.Type)}", inner, $"{index.Children[0].Text ?? "c"}[...]", null, null) : null;
  }

  // the members of a collection of counted objects that are built; the others would hand out or compare boxes (not built yet)
  static readonly HashSet<string> CountedItemsMembers = new() {
    "add", "insert", "removeAt", "clear", "truncate", "reserve", "shrinkToFit", "swap", "get_len", "get_capacity"
  };

  void CheckCountedItemsMember(ClassInfo cls, string name, Node at) {
    if (LibraryCode || !(IsVec(cls.Name) || IsList(cls.Name))) {
      return;
    }
    string elem = IsVec(cls.Name) ? VecElem(cls.Name) : ListElem(cls.Name);
    if (BoxInner(elem) != null && !CountedItemsMembers.Contains(name)) {
      throw NotSupported($"'{name}' on a collection of counted objects", at);
    }
  }

  // the cost of an argument for a parameter whose type is a box (the item of add, insert ...): -1 when it is not that case
  int BoxArgCost(Node arg, string paramType, Scope scope) {
    if (BoxInner(paramType) is not string inner) {
      return -1;
    }
    return CountedArgCost(arg, "@" + inner, scope);
  }
}
