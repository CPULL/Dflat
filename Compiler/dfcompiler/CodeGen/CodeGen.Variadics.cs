using dfparser;

namespace dfcompiler;

// Variadic parameters (spec 13.4): T name... is the last parameter, at most one, no default, never named in a call. Inside the function
// it is a read-only arr<T>. T is a basic type, a class or a struct, not a collection. The arguments are lent: the items of the call make a
// temporary arr; an existing arr<T>, vec<T> or a view of one, given alone, is lent whole with no copy (a vec through a view, so it cannot
// change its size during the call). A list is refused, and items and a collection cannot be mixed.
public sealed partial class CodeGen {
  static readonly HashSet<string> CollectionViews = new() { "mapKeys", "mapValues", "hmapKeys", "hmapValues" };

  // a collection type: not a valid T of T name..., and the one argument that is lent whole
  bool IsCollectionType(string type) => IsArr(type) || IsVec(type) || IsList(type) || IsMap(type) || IsSet(type) || IsView(type)
    || classInstanceArgs.TryGetValue(type, out var instance) && CollectionViews.Contains(instance.Template.Name);

  // the checks of the declaration of T name... (the place, the count and the markers are checked by the caller)
  void CheckVariadicParam(Node p, string paramType) {
    if (p.Children.Any(child => child.Kind == "Default")) {
      throw Error($"the variadic parameter '{p.Text}...' has no default value: with no arguments it is empty (spec 13.4)", p);
    }
    if (p.Children.Any(child => child.Kind is "Counted" or "Weak" or "ReadWrite")) {
      throw Error($"the variadic parameter '{p.Text}...' is always lent read-only: no @, % or & marker (spec 13.4)", p);
    }
    if (IsCollectionType(paramType)) {
      throw Error($"a variadic parameter takes basic values, objects or structs, not a collection ({DisplayName(paramType)}) (spec 13.4)", p);
    }
  }

  // the elements the variadic part of a call can take as a whole: arr<T>, vec<T>, a view of an arr or a vec of T
  bool LendsWholeVariadic(string argType, string paramType) {
    string elem = ArrElem(paramType);
    return argType == paramType || IsVec(argType) && VecElem(argType) == elem || ViewTemplate(argType) is "arrView" or "vecView" && ViewElem(argType) == elem;
  }

  // names(rex, Dog("fido")): objects given to T name... are lent, not moved; the temporary array holds the references and ends with the
  // statement, without its items (a new object ends with the statement too, as for any lent argument)
  string LentObjects(ParamSig param, Node arg, Scope scope) {
    string elem = ArrElem(param.Type);
    string array = NewTemp();
    int count = arg.Children.Count;
    var sb = new System.Text.StringBuilder();
    sb.Append($"({{ {CType(param.Type)} {array} = {{ ({CType(elem)}*)df_calloc({count}, sizeof({CType(elem)})), {count} }}; df_tmpf({array}.p, free); ");
    for (int index = 0; index < count; index++) {
      var item = arg.Children[index];
      if (item.Kind == "Gift") {
        throw Error($"'{param.Name}...' only borrows the objects: remove the ->", item);
      }
      var value = TempObject(ExprOf(item, scope, elem));
      sb.Append($"{array}.p[{index}] = {Convert(value, elem, item)}; ");
    }
    sb.Append($"{array}; }})");
    return sb.ToString();
  }

  // the variadic part of a call (the List node MapArgs makes): the code of one collection lent whole, or null for the items
  string? VariadicLend(ParamSig param, Node arg, Scope scope) {
    if (!param.IsVariadic || arg.Kind != "List") {
      return null;
    }
    var types = arg.Children.Select(child => ExprOf(child, scope).Type).ToList();
    int collection = types.FindIndex(IsCollectionType);
    if (collection < 0) {
      return IsClass(ArrElem(param.Type)) ? LentObjects(param, arg, scope) : null;
    }
    var given = arg.Children[collection];
    string type = types[collection];
    if (types.Count > 1) {
      throw Error($"'{param.Name}...' takes items or one collection, not both: give the items, or only the {DisplayName(type)} (spec 13.4)", given);
    }
    if (IsList(type) || ViewTemplate(type) == "listView") {
      throw Error($"a list cannot be given to '{param.Name}...': make an arr of it with transformTo<arr>() (spec 13.4)", given);
    }
    if (!LendsWholeVariadic(type, param.Type)) {
      throw Error($"'{param.Name}...' takes {DisplayName(ArrElem(param.Type))} items, or an arr, a vec or a view of them, not a {DisplayName(type)} (spec 13.4)", given);
    }
    if (IsArr(type)) {
      return ArgCode(param, given, scope);   // the array itself
    }
    Expr view;
    if (IsVec(type)) {
      // the whole vec through a view: the view holds the counter of open iterators, so the vec keeps its size during the call
      var range = Synthetic("Range", given, "..").Add(Synthetic("Open", given)).Add(Synthetic("Open", given));
      view = ViewExpr(Synthetic("Index", given).Add(given).Add(range), scope);
    } else {
      view = ExprOf(given, scope);
    }
    view = TempObject(view);
    string held = NewTemp();
    string items = ViewTemplate(view.Type) == "arrView"
      ? $"{held}->{VecField(view.Type, "items")}.p"
      : $"{held}->{VecField(view.Type, "collection")}->{VecField(ObjInfo(view.Type).ParentField!.Type, "items")}.p";
    return $"({{ {CType(view.Type)} {held} = {NullChecked(view.Code, view.Type)}; ({CType(param.Type)}){{ {items} + {held}->{VecField(view.Type, "start")}, "
      + $"{held}->{VecField(view.Type, "count")} }}; }})";
  }
}
