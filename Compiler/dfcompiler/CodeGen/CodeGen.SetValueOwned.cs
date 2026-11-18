using System.Text;
using dfparser;

namespace dfcompiler;

// Set values that own memory (spec 11.3.6.1): a member that is a string is a pointer in the first word of the value, owned by it. The value
// is copied as a struct (11.1.5.3): = and ! clone what it owns, ->k moves it out (k becomes the generic null), a plain parameter lends it,
// and it ends with its owner (a local, a ->T parameter, a field, a result nobody keeps). Each such set has a clone and a drop helper.
public sealed partial class CodeGen {
  readonly HashSet<string> setOwnedHelpers = new();

  // the members that own memory, built so far
  bool IsOwnedSetMember(string member) => member == "string" || IsClass(member) && !IsCountedType(member) || IsTuple(member);

  // a tuple boxed in a set value owns its parts: strings are always its own copies (a tuple does not own its strings elsewhere), objects
  // and arrays are cloned unless the tuple is new (then they are its own already); a tuple inside follows the same rules
  string TupleDeepClone(string tuple, string place, bool fresh, Node at) {
    var code = new StringBuilder();
    var elems = TupleElems(tuple);
    for (int index = 0; index < elems.Count; index++) {
      string part = $"{place}.e{index}";
      string type = elems[index].Type;
      if (type == "string") {
        code.Append($"{part} = df_str_dup({part}); ");
      } else if (type == "string?") {
        code.Append($"{part} = df_opt_str_dup({part}); ");
      } else if (IsTuple(type)) {
        code.Append(TupleDeepClone(type, part, fresh, at));
      } else if ((IsClass(type) || IsArr(type)) && !IsCountedType(type)) {
        if (!fresh) {
          code.Append($"{part} = {(IsClass(type) ? OwnedMemberClone(type, part, at) : $"{ArrCloneHelper(type, at)}({part})")}; ");
        }
      } else if (!(IsNumeric(type) || type is "bool" or "char" or "addr" || IsEnum(type) || IsTimeType(type) || IsNullable(type) && IsNumeric(BaseOf(type)))) {
        throw NotSupported($"a {DisplayName(type)} inside a tuple member of a set (spec 11.3.6.1)", at);
      }
    }
    return code.ToString();
  }

  string TupleDeepDrop(string tuple, string place) {
    var code = new StringBuilder();
    var elems = TupleElems(tuple);
    for (int index = 0; index < elems.Count; index++) {
      string part = $"{place}.e{index}";
      string type = elems[index].Type;
      if (type == "string") {
        code.Append($"df_str_free({part}); ");
      } else if (type == "string?") {
        code.Append($"df_opt_str_drop(&{part}); ");
      } else if (IsTuple(type)) {
        code.Append(TupleDeepDrop(type, part));
      } else if (IsClass(type) && !IsCountedType(type)) {
        code.Append($"if ({part}) {{ df_drop((void*){part}); }} ");
      } else if (IsArr(type)) {
        code.Append($"{ArrDropHelper(type)}({part}); ");
      }
    }
    return code.ToString();
  }

  // a member value that owns memory: its copy (an object that cannot be cloned is a compile error), its end
  string OwnedMemberClone(string member, string place, Node at) {
    if (classes.TryGetValue(member, out var cls) && NotClonableReason(cls) is string reason) {
      throw Error($"a value of the set holds a {DisplayName(member)}, which cannot be cloned ({reason}): move it with -> (spec 11.3.6.1)", at);
    }
    if (member != "string") {
      cloneUsed = true;   // the clone functions of the classes are emitted
    }
    return member == "string" ? $"df_str_dup({place})" : $"({place} ? {CloneValue(place, member, at)} : NULL)";
  }

  static string OwnedMemberDrop(string member, string place) =>
    member == "string" ? $"df_str_free({place});" : $"if ({place}) {{ df_drop((void*){place}); }}";

  // a set whose values can own memory
  bool SetOwns(string? type) {
    if (type == null || !IsSetName(type)) {
      return false;
    }
    try {
      return SetMembers(type).Any(IsOwnedSetMember);
    } catch (CompileException) {
      return false;   // a member not declared yet (a class): the set is checked again where it is used
    }
  }

  // the C helpers of a set that owns: df_svclone_<set> (a value with its own copies) and df_svdrop_<set> (ends what it owns)
  string SetOwnedHelpers(string set, Node at) {
    string name = SetCName(set);
    if (!setOwnedHelpers.Add(set)) {
      return name;
    }
    var members = SetValueMembers(set, at);
    string ct = SetValueCType();
    var sb = new StringBuilder();
    sb.Append($"static {ct} df_svclone_{name}({ct} v) {{\n  switch (v.tag) {{\n");
    for (int index = 0; index < members.Count; index++) {
      if (IsTuple(members[index])) {
        string tt = CType(members[index]);
        sb.Append($"    case {index + 1}u: {{ {tt}* s; memcpy(&s, v.bits, sizeof s); {tt}* c = ({tt}*)malloc(sizeof({tt})); if (!c) {{ df_out_of_memory(); }} *c = *s; "
          + $"{TupleDeepClone(members[index], "(*c)", false, at)}memcpy(v.bits, &c, sizeof c); break; }}\n");
        continue;
      }
      if (IsOwnedSetMember(members[index]) && (!classes.TryGetValue(members[index], out var cls) || NotClonableReason(cls) == null)) {
        string ctype = CType(members[index]);
        sb.Append($"    case {index + 1}u: {{ {ctype} s; memcpy(&s, v.bits, sizeof s); s = {OwnedMemberClone(members[index], "s", at)}; memcpy(v.bits, &s, sizeof s); break; }}\n");
      }
    }
    sb.Append("    default: break;\n  }\n  return v;\n}\n\n");
    sb.Append($"static void df_svdrop_{name}({ct}* v) {{\n  switch (v->tag) {{\n");
    for (int index = 0; index < members.Count; index++) {
      if (IsTuple(members[index])) {
        sb.Append($"    case {index + 1}u: {{ {CType(members[index])}* s; memcpy(&s, v->bits, sizeof s); if (s) {{ {TupleDeepDrop(members[index], "(*s)")}free(s); }} break; }}\n");
      } else if (IsOwnedSetMember(members[index])) {
        sb.Append($"    case {index + 1}u: {{ {CType(members[index])} s; memcpy(&s, v->bits, sizeof s); {OwnedMemberDrop(members[index], "s")} break; }}\n");
      }
    }
    sb.Append($"    default: break;\n  }}\n  v->tag = 0u;\n  v->bits[0] = 0;\n  v->bits[1] = 0;\n}}\n\n");
    // the map and array helpers call them: prototypes and bodies go with the struct helpers, before them
    structProtos.Append($"static {ct} df_svclone_{name}({ct} v);\nstatic void df_svdrop_{name}({ct}* v);\n");
    structCode.Append(sb);
    return name;
  }

  // a copy of a value: every class member it can hold must be clonable (spec 11.3.6.1)
  string SetCloneCode(string set, string code, Node at) {
    foreach (string member in SetMembers(set).Where(member => classes.ContainsKey(member))) {
      OwnedMemberClone(member, "x", at);
    }
    return $"df_svclone_{SetOwnedHelpers(set, at)}({code})";
  }

  string SetDropCode(string set, string place, Node at) => $"df_svdrop_{SetOwnedHelpers(set, at)}(&({place}));";

  // the value an owner receives: a new value as it is, a member value wrapped (a string not new is copied), another set value cloned
  string SetOwnedValue(Node n, string set, Scope scope, Node at) {
    var inner = n;
    while (inner.Kind == "Paren") {
      inner = inner.Children[0];
    }
    // a tuple literal takes its types from the one tuple member with as many items: (1, "a") into (i32 id, string name)
    var tupleMembers = inner.Kind == "Tuple" ? SetMembers(set).Where(member => IsTuple(member) && TupleElems(member).Count == inner.Children.Count
      && TryFits(inner, scope, member)).ToList() : null;
    if (tupleMembers is { Count: 1 } && inner.Children.All(item => item.Kind != "Named")) {
      return SetTupleLiteral(inner, tupleMembers[0], set, scope, at);
    }
    var value = ExprOf(inner, scope, set);
    if (value.Type == set) {
      return value.Fresh ? value.Code : SetCloneCode(set, value.Code, at);
    }
    return Convert(value, set, at);   // WrapSetValue: a new value
  }

  // ->k: the value leaves k, which becomes the generic null
  Expr? SetGift(Node gift, Scope scope) {
    if (LibraryCode) {
      return null;   // the collections of the library handle their items themselves
    }
    var place = gift.Children[0];
    if (place.Kind is not ("Name" or "Member") || TryExprType(place, scope) is not string type || !SetOwns(type)) {
      return null;
    }
    if (place.Kind == "Name" && scope.Find(place.Text!) is VarInfo { IsParam: true, Own: false }) {
      throw Error($"'{place.Text}' is lent to this function: only its owner moves it (spec 11.3.6.1)", gift);
    }
    var target = LValue(place, scope);
    string held = NewTemp();
    return new Expr($"({{ {SetValueCType()} {held} = {target.Code}; {target.Code}.tag = 0u; {target.Code}.bits[0] = 0; {held}; }})", type, true);
  }

  // k!: a value with its own copies
  Expr? SetCopy(Node copy, Scope scope) {
    if (LibraryCode) {
      return null;
    }
    if (TryExprType(copy.Children[0], scope) is not string type || !SetOwns(type)) {
      return null;
    }
    var value = ExprOf(copy.Children[0], scope);
    return new Expr(value.Fresh ? value.Code : SetCloneCode(type, value.Code, copy), type, true);
  }

  // ---------------------------------------------------------------- owners

  static Node SetDropNode(string name, Node at) =>
    Synthetic("AtEnd", at, "drop").Add(Synthetic("Block", at).Add(Synthetic("SetDrop", at, name)));

  void EmitSetDrop(Node s, StringBuilder sb, Scope scope, string pad) {
    if (scope.Find(s.Text!) is VarInfo held && SetOwns(held.Type) && !(held.IsParam && !held.Own)) {
      sb.Append(pad).Append(SetDropCode(held.Type, held.CName, s)).Append('\n');
    }
  }

  bool IsOwnedSetDecl(Node decl) {
    if (decl.Kind != "Decl" || setOwnedTypes == 0 || LibraryCode) {
      return false;
    }
    var typeNode = decl.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind));
    if (typeNode is { Kind: "Type", Text: "var" }) {
      return decl.Children.FirstOrDefault(child => child.Kind == "Init")?.Children[0] is { Kind: "Call" } call
        && CalleesOf(call) is { Count: > 0 } callees && callees.All(f => SetOwns(f.Ret));
    }
    return typeNode != null && TryResolveType(typeNode) is string type && SetOwns(type);
  }

  // how many sets own memory: 0 skips every check
  int setOwnedTypes;

  void CountOwnedSets() {
    setOwnedTypes = genericSets.Keys.Count(SetOwns);
  }

  void EmitSetOwnedDecl(Node s, string type, Node? init, StringBuilder sb, Scope scope, string pad) {
    string code = init == null ? ZeroValue(type) : SetOwnedValue(init.Children[0], type, scope, init);
    VarInfo variable;
    if (frames.Peek().Hoisted.TryGetValue(s.Text!, out var hoisted)) {
      variable = hoisted;
      sb.Append($"{pad}{hoisted.CName} = {code};\n");
    } else {
      variable = new VarInfo(type, "v_" + s.Text, false);
      sb.Append($"{pad}{CType(type)} {variable.CName} = {code};\n");
    }
    variable.Own = true;
    variable.IsLocal = currentFun != null && !fn!.IsMain;
    scope.Vars[s.Text!] = variable;
    SetOwnedHelpers(type, s);
  }

  // k = value, this.k = value: the old value ends, then k owns the new one
  string? SetOwnedAssignCode(Node s, Scope scope) {
    if (s.Text != "=" || setOwnedTypes == 0 || LibraryCode || s.Children[0].Kind is not ("Name" or "Member") || TryExprType(s.Children[0], scope) is not string type || !SetOwns(type)) {
      return null;
    }
    if (s.Children[0].Kind == "Name" && scope.Find(s.Children[0].Text!) is VarInfo { IsParam: true, Own: false }) {
      throw Error($"'{s.Children[0].Text}' is a plain parameter: it is read-only (spec 13.1)", s);
    }
    var target = LValue(s.Children[0], scope);
    if (target.IsConst && !initializing) {
      throw ConstWrite(target, s.Children[0], s);
    }
    string given = SetOwnedValue(s.Children[1], type, scope, s);
    string held = NewTemp();
    MarkAssigned(target.Variable);
    return $"({{ {SetValueCType()} {held} = {given}; {SetDropCode(type, target.Code, s)} {target.Code} = {held}; }})";
  }

  // return k: a local that owns its value gives it (k becomes the generic null); another value is cloned
  string? SetOwnedReturnValue(Node s, Scope scope) {
    if (s.Children.Count != 1 || LibraryCode || !SetOwns(currentRet)) {
      return null;
    }
    var value = s.Children[0];
    while (value.Kind == "Paren") {
      value = value.Children[0];
    }
    if (value.Kind == "Name" && scope.Find(value.Text!) is VarInfo { Own: true } owner && owner.Type == currentRet) {
      string held = NewTemp();
      return $"({{ {SetValueCType()} {held} = {owner.CName}; {owner.CName}.tag = 0u; {owner.CName}.bits[0] = 0; {held}; }})";
    }
    return SetOwnedValue(s.Children[0], currentRet!, scope, s);
  }

  // a set value argument: a plain parameter lends it (a new value ends with the statement); ->T takes it
  string? SetOwnedArgCode(ParamSig param, Node arg, Scope scope) {
    if (LibraryCode || !SetOwns(param.Type)) {
      return null;
    }
    if (param.IsGift) {
      if (arg.Kind is "Name" or "Member") {
        throw Error($"'{param.Name}' takes the value: write ->{SourceText(arg)} to move it, or {SourceText(arg)}! to give a copy (spec 11.3.6.1)", arg);
      }
      return SetOwnedValue(arg, param.Type, scope, arg);
    }
    if (arg.Kind == "Gift") {
      throw Error($"'{param.Name}' only borrows the value: remove the -> (spec 11.3.6.1)", arg);
    }
    var value = ExprOf(arg, scope, param.Type);
    if (value.Type != param.Type) {
      value = new Expr(Convert(value, param.Type, arg), param.Type, true);   // a member value wrapped: a new set value
    }
    if (value.Fresh) {
      return TempBoxCode(param.Type, value.Code, SetDropCode(param.Type, "*t", arg), "_" + SetCName(param.Type));   // ends with the statement
    }
    return value.Code;
  }

  // a new value nobody keeps ends at once
  string? DroppedSetCode(Expr e, Node at) {
    if (!e.Fresh || LibraryCode || !SetOwns(e.Type)) {
      return null;
    }
    string held = NewTemp();
    return $"{{ {SetValueCType()} {held} = {e.Code}; {SetDropCode(e.Type, held, at)} }}";
  }

  // the places that cannot hold such a value yet
  void CheckOwnedSetHolder(string type, Node at) {
    if (setOwnedTypes == 0) {
      return;
    }
    bool holds = IsTuple(type) && TupleElems(type).Any(elem => SetOwns(elem.Type))
      || type.Contains('<') && genericSets.Keys.Where(SetOwns).Any(set => System.Text.RegularExpressions.Regex.IsMatch(type, $@"[<,]\s*{System.Text.RegularExpressions.Regex.Escape(set)}\s*[,>]"));
    if (holds) {
      throw NotSupported("set values with a string or object member inside a tuple or a collection (spec 11.3.6.1)", at);
    }
  }

  // a new value that is only read (printed, hashed, compared, switched on) ends with the statement
  Expr SetTemp(Expr e, Node at) {
    if (!e.Fresh || !SetOwns(e.Type) || fn == null) {
      return e;
    }
    return new Expr(TempBoxCode(e.Type, e.Code, SetDropCode(e.Type, "*t", at), "_" + SetCName(e.Type)), e.Type);
  }

  // the literal compiles as a value of the member (only to choose it: the code is made again)
  bool TryFits(Node literal, Scope scope, string member) {
    try {
      return Assignable(member, ExprOf(literal, scope, member).Type);
    } catch (CompileException) {
      return false;
    }
  }

  // (n, $"rec {n}") into a tuple member: each item is taken when it is new and copied when it is borrowed, then the tuple is boxed
  string SetTupleLiteral(Node literal, string member, string set, Scope scope, Node at) {
    var elems = TupleElems(member);
    string ct = CType(member);
    string box = NewTemp();
    var fill = new StringBuilder();
    for (int index = 0; index < elems.Count; index++) {
      string type = elems[index].Type;
      var item = ExprOf(literal.Children[index], scope, type);
      string code = Convert(item, type, literal.Children[index]);
      if (!item.Fresh) {
        code = type == "string" ? $"df_str_dup({code})"
          : type == "string?" ? $"df_opt_str_dup({code})"
          : IsClass(type) && !IsCountedType(type) ? OwnedMemberClone(type, code, at)
          : IsArr(type) ? $"{ArrCloneHelper(type, at)}({code})"
          : IsTuple(type) ? $"({{ {CType(type)} p_{box}{index} = {code}; {TupleDeepClone(type, $"p_{box}{index}", false, at)}p_{box}{index}; }})"
          : code;
      }
      if (!(type is "string" or "string?" || IsNumeric(type) || type is "bool" or "char" or "addr" || IsEnum(type) || IsTimeType(type) || IsClass(type) || IsArr(type) || IsTuple(type)
            || IsNullable(type) && IsNumeric(BaseOf(type)))) {
        throw NotSupported($"a {DisplayName(type)} inside a tuple member of a set (spec 11.3.6.1)", at);
      }
      fill.Append($"{box}->e{index} = {code}; ");
    }
    int tag = SetValueMembers(set, at).IndexOf(member) + 1;
    string result = NewTemp();
    return $"({{ {ct}* {box} = ({ct}*)malloc(sizeof({ct})); if (!{box}) {{ df_out_of_memory(); }} {fill}{SetValueCType()} {result} = {{ {{ 0, 0 }}, {tag}u }}; "
      + $"memcpy({result}.bits, &{box}, sizeof {box}); {result}; }})";
  }

  // a set value given to a method of a collection of the library: a member value or a tuple literal becomes a new value that ends with the
  // statement (the collection clones what it keeps); a set value is lent as it is
  string? LibrarySetArg(ParamSig param, Node arg, Scope scope, FunSig? callee) {
    if (LibraryCode || !SetOwns(param.Type) || arg.Kind is "Gift" or "Named") {
      return null;
    }
    // vec and list move the item they are given into their storage; a map or a set keeps a boxed clone of a key (df_tk_box_)
    bool storesIt = callee?.Class is ClassInfo owner && (IsVec(owner.Name) || IsList(owner.Name) || IsMap(owner.Name) && !IsSet(owner.Name) && param.Name != "key");
    if (param.IsGift && storesIt) {
      return SetOwnedValue(arg, param.Type, scope, arg);   // add(->Item item) takes it and ends it: a value of its own (a copy of a variable)
    }
    if (TryExprType(arg, scope) == param.Type) {
      return null;
    }
    return SetTemp(new Expr(SetOwnedValue(arg, param.Type, scope, arg), param.Type, true), arg).Code;
  }
}
