using System.Text;
using dfparser;

namespace dfcompiler;

// Cloning (spec 12.6): a! is a.clone(). Every class is clonable unless it is notclonable, has a parent (12.5) or is an exception. The default clone of a class is a C function df_clone_<class>, put in the clone slot of the class descriptor, so
// a clone follows the class of the object at run time (df_clone_obj); a class of the program with its own clone() has a df_clone_<class>
// that calls it. The default fills each field by its kind: natives copied, strings copied, owned objects cloned (null when they cannot be),
// counted fields another user, weak fields another weak reference, the children and the counter of open iterators empty. An arr is a new
// block with its items cloned; a list is a new chain of nodes; maps and sets have a clone() in the library (an empty one, then add(this)).
// tryClone() checks the clone slot at run time and raises ObjectNotClonableException when it is empty.
public sealed partial class CodeGen {
  bool cloneUsed;                                          // the program clones something: the clone functions are emitted
  readonly HashSet<string> arrCloneHelpers = new();

  // a Copy node (x!) or a call of clone() / tryClone() in the code of the program
  static bool UsesClone(Node node) {
    if (node.Line < LibraryLine && (node.Kind == "Copy" || node.Kind == "Call" && node.Children[0] is { Kind: "Member", Text: "clone" or "tryClone" })) {
      return true;
    }
    return node.Children.Any(UsesClone);
  }

  static bool MarkedNotClonable(ClassInfo cls) =>
    cls.Node.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ').Contains("notclonable") == true;

  // why the objects of a class cannot be cloned, or null
  string? NotClonableReason(ClassInfo cls) {
    if (cls.IsTrait) {
      return "the class of a trait value is known only at run time (tryClone)";
    }
    for (var c = cls; c != null; c = c.Parent) {
      if (MarkedNotClonable(c)) {
        return $"{DisplayName(c.Name)} is notclonable";
      }
      if (c.ParentField != null) {
        return "it has a parent (12.5): a new child comes from the functions of its parent";
      }
    }
    if (cls.IsException) {
      return "it is an exception";
    }
    return null;
  }

  // the clone() of the program for a class (its own or inherited), or null for the default
  FunSig? UserClone(ClassInfo cls) =>
    LookupMethods(cls, "clone").FirstOrDefault(f => f.Params.Count == 0 && !f.IsStatic && f.Node.Line < LibraryLine);

  // the clone() of the library for maps and sets (a new collection with the entries added), or null
  FunSig? LibraryClone(ClassInfo cls) => IsMap(cls.Name) || IsSet(cls.Name)
    ? LookupMethods(cls, "clone").FirstOrDefault(f => f.Params.Count == 0 && !f.IsStatic && f.Node.Line >= LibraryLine)
    : null;

  FunSig? CloneMethod(ClassInfo cls) => UserClone(cls) ?? LibraryClone(cls);

  // a! and a.clone() with the default clone: a new object of the class of a at run time
  Expr CloneExpr(Expr value, Node at) {
    if (!IsClass(value.Type)) {
      throw NotSupported(NodeFeatures["Copy"], at);
    }
    if (IsView(value.Type)) {
      throw NotSupported("c[a..b]! (a copy of a view)", at);
    }
    var cls = ObjInfo(value.Type);
    if (NotClonableReason(cls) is string reason) {
      throw Error($"{DisplayName(value.Type)} cannot be cloned: {reason} (spec 12.6)", at);
    }
    return new Expr($"(({CType(value.Type)})df_clone_obj((const void*)({value.Code})))", value.Type, true);
  }

  // x.tryClone(): the clone function of the class of the object at run time, or ObjectNotClonableException when the class has none
  // (notclonable, a parent, an exception: decided at run time, so it also works on trait values); null gives null, as clone()
  Expr TryCloneExpr(Expr value, Node at) {
    if (IsView(value.Type)) {
      throw NotSupported("c[a..b].tryClone() (a copy of a view)", at);
    }
    string source = NewTemp();
    string copy = NewTemp();
    string code = $"({{ const void* {source} = (const void*)({value.Code}); void* {copy} = NULL; "
      + $"if ({source} != NULL && !DF_IS_DEAD({source})) {{ const df_class* c_{copy} = ((const df_object*){source})->cls; "
      + $"if (c_{copy}->clone == NULL) {{ df_raise(&df_desc_ObjectNotClonableException, {fn!.NameConst}); {RaiseJump} }} {copy} = c_{copy}->clone({source}); }} "
      + $"({CType(value.Type)}){copy}; }})";
    return new Expr(code, value.Type, true);
  }

  // x.clone() without a clone() of the program, x.tryClone()
  Expr? CloneCall(Node call, Scope scope) {
    if (call.Children[0] is not { Kind: "Member", Text: "clone" or "tryClone" } callee || call.Children.Count > 1 && call.Children[1].Children.Count > 0) {
      return null;
    }
    Expr receiver;
    try {
      receiver = ExprOf(callee.Children[0], scope);
    } catch (CompileException) {
      return null;
    }
    if (!IsClass(receiver.Type) || (callee.Text == "clone" && ObjInfo(receiver.Type) is var cls && !cls.IsTrait && CloneMethod(cls) != null)) {
      return null;   // a clone() of the program, or of a map or a set: an ordinary call
    }
    return callee.Text == "tryClone" ? TryCloneExpr(receiver, call) : CloneExpr(receiver, call);
  }

  // a! of an object: as a.clone() (the clone() of the program when the class has one)
  Expr CopyObject(Node copy, Expr value, Scope scope) {
    if (IsClass(value.Type) && ObjInfo(value.Type) is { IsTrait: false } cls && CloneMethod(cls) != null) {
      var call = Synthetic("Call", copy).Add(Synthetic("Member", copy, "clone").Add(copy.Children[0])).Add(Synthetic("Args", copy));
      return ExprOf(call, scope);
    }
    return CloneExpr(value, copy);
  }

  // ---------------------------------------------------------------- the C of the clones

  // the clone slot of the descriptor of a class: its clone function, or NULL when it cannot be cloned (or nothing clones)
  string EmitCloneFunction(ClassInfo cls, StringBuilder sb) {
    if (!cloneUsed || NotClonableReason(cls) != null) {
      return "NULL";
    }
    string name = $"df_clone_{cls.Name}";
    if (CloneMethod(cls) is FunSig custom) {
      string call = $"{custom.CName}(({CType(custom.Class!.Name)})p)";
      sb.Append($"static void* {name}(const void* p) {{\n");
      sb.Append(PairReturn(custom)
        ? $"  {ResultPair(CType(custom.Ret))} r = {call};\n  if (r.err) {{\n    df_panic_unhandled(r.err);\n  }}\n  return (void*)r.value;\n}}\n\n"
        : $"  return (void*){call};\n}}\n\n");
      return name;
    }
    var body = new StringBuilder();
    if (IsList(cls.Name)) {
      // a new chain of nodes, each with its item cloned
      string elem = ListElem(cls.Name);
      string value = CloneValue("n->value", elem, cls.Node);
      body.Append($"  for ({ListNode(cls.Name)}* n = o->{VecField(cls.Name, "first")}; n != NULL; n = n->next) {{ ");
      body.Append(NewListNode(cls.Name, "copy", value)).Append(AppendListNode("c", cls.Name, "copy")).Append("}\n");
    } else {
      foreach (var field in AllInstanceFields(cls)) {
        if (field.Hidden || field.Name == "iteratorCounter" && field.Owner.IsTrait) {
          continue;   // no children and no open iterators in a clone (spec 12.6)
        }
        string source = $"o->{field.CName}";
        string copied = field.IsCounted ? $"df_rc_use({source})"
          : field.IsWeak ? $"df_rc_weak({source})"
          : CloneValue(source, field.Type, field.At);
        body.Append($"  c->{field.CName} = {copied};\n");
      }
    }
    sb.Append($"static void* {name}(const void* p) {{\n");
    sb.Append($"  const {cls.StructName}* o = (const {cls.StructName}*)p;\n  {cls.StructName}* c = ({cls.StructName}*)df_calloc(1, sizeof({cls.StructName}));\n");
    // the class of the original: the clone function of a class is reached only through the descriptor of that class
    sb.Append("  c->cls = o->cls;\n").Append(body).Append("  return c;\n}\n\n");
    return name;
  }

  // the C that copies one value of a type for a clone
  string CloneValue(string source, string type, Node at) {
    if (type == "string") {
      return $"df_str_dup({source})";
    }
    if (IsClass(type)) {
      return $"(({CType(type)})df_clone_obj((const void*)({source})))";
    }
    if (IsArr(type)) {
      return $"{ArrCloneHelper(type, at)}({source})";
    }
    if (IsTuple(type) && TupleElems(type).Any(elem => elem.Type == "string" || IsClass(elem.Type) || IsArr(elem.Type) || IsTuple(elem.Type))) {
      throw NotSupported("Cloning a tuple field that holds strings, objects or arrays", at);
    }
    return source;   // a native, a nullable native, a tuple of natives: a value
  }

  // a new block with each item cloned
  string ArrCloneHelper(string type, Node at) {
    string elem = ArrElem(type);
    string name = "df_arrclone_" + System.Text.RegularExpressions.Regex.Replace(type, @"[^A-Za-z0-9_]", "_");
    if (arrCloneHelpers.Add(name)) {
      string item = CloneValue("a.p[i]", elem, at);   // the helpers of the items come first
      string arrType = ArrCType(elem);
      classCode.Append($"static {arrType} {name}({arrType} a) {{\n  {arrType} r = {{ NULL, a.len }};\n");
      classCode.Append($"  if (a.len > 0) {{\n    r.p = ({CType(elem)}*)df_calloc(a.len, sizeof({CType(elem)}));\n");
      classCode.Append($"    for (df_uns i = 0; i < a.len; i++) {{\n      r.p[i] = {item};\n    }}\n  }}\n  return r;\n}}\n\n");
    }
    return name;
  }
}
