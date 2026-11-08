using System.Text;
using dfparser;

namespace dfcompiler;

// Tuples, multiple return values, multiple assignment and swap.
// A tuple is a C struct { e0, e1, ... }; a function with several return values returns the same kind of struct.
public sealed partial class CodeGen {
  // (a, b, ...): elements are evaluated left to right
  Expr TupleLiteral(Node n, Scope scope, string? expected) {
    List<(string Type, string? Name)>? expectedElems = null;
    if (expected != null && IsTuple(BaseOf(expected))) {
      var candidate = TupleElems(BaseOf(expected));
      if (candidate.Count == n.Children.Count) {
        expectedElems = candidate;
      }
    }
    var values = n.Children.Select((child, index) => ExprOf(child, scope, expectedElems?[index].Type)).ToList();
    for (int index = 0; index < values.Count; index++) {
      CheckNotBorrowingFunction(values[index].Type, n.Children[index], scope, "a tuple");
    }
    List<(string Type, string? Name)> elems;
    if (expectedElems != null && values.Zip(expectedElems).All(pair => Assignable(pair.Second.Type, pair.First.Type))) {
      elems = expectedElems;
    } else {
      elems = new List<(string Type, string? Name)>();
      foreach (var (value, index) in values.Select((value, index) => (value, index))) {
        if (value.Type == "null") {
          throw Error("cannot infer the type of null in a tuple; declare the tuple type", n.Children[index]);
        }
        if (value.Type == "void" || IsMulti(value.Type)) {
          throw Error("a tuple element needs a single value", n.Children[index]);
        }
        elems.Add((value.Type, null));
      }
    }
    string tupleType = MakeTuple(elems);
    var declarations = new StringBuilder();
    var temps = new List<string>();
    for (int index = 0; index < values.Count; index++) {
      string temp = NewTemp();
      temps.Add(temp);
      string code = StrOwnedAs(values[index], elems[index].Type, Convert(values[index], elems[index].Type, n.Children[index]));
      declarations.Append($"{CType(elems[index].Type)} {temp} = {code}; ");
    }
    return new Expr($"({{ {declarations}({CType(tupleType)}){{ {string.Join(", ", temps)} }}; }})", tupleType);
  }

  // return a, b: the values are computed left to right into temporaries
  string MultiReturnCode(Node s, Scope scope, StringBuilder sb, string pad) {
    var elems = TupleElems(currentRet!);
    if (s.Children.Count == 1) {
      // forwarding another call with the same return values
      var single = ExprOf(s.Children[0], scope);
      if (IsStruct(single.Type) && Assignable(currentRet!, single.Type)) {
        return Convert(single, currentRet!, s);   // return s: the fields of the struct are the values (spec 11.1.5.8)
      }
      if (single.Type != currentRet) {
        throw Error($"this function returns {elems.Count} values", s);
      }
      return TupleOwnedCode(s.Children[0], single, currentRet!, s);   // the arrays of a call of the library are copied (spec 6.8.1)
    }
    if (s.Children.Count != elems.Count) {
      throw Error($"this function returns {elems.Count} values, not {s.Children.Count}", s);
    }
    var temps = new List<string>();
    var emptied = new List<VarInfo>();   // owned arrays that move out: emptied after every value is computed (return a, a.len)
    for (int index = 0; index < elems.Count; index++) {
      string temp = NewTemp();
      temps.Add(temp);
      if (IsCountedType(elems[index].Type)) {
        bool writablePart = currentFun?.WritableCounted.Contains(index) == true;
        sb.Append($"{pad}df_rc* {temp} = {CountedPartReturn(s.Children[index], scope, CountedBase(elems[index].Type), s.Children[index], writablePart)};\n");   // spec 12.7
        continue;
      }
      var value = ExprOf(s.Children[index], scope, elems[index].Type);
      if (elems[index].Type == "string") {
        value = StrReturned(value, s.Children[index], scope);
      }
      if (IsArr(elems[index].Type) && !LibraryCode) {
        if (s.Children[index].Kind == "Name" && scope.Find(s.Children[index].Text!) is { OwnsArray: true } owned) {
          emptied.Add(owned);   // the array moves to the caller (spec 6.8.1)
        } else if (!GivesOwnArray(s.Children[index])) {
          value = new Expr($"{ArrCloneHelper(elems[index].Type, s)}({Convert(value, elems[index].Type, s)})", elems[index].Type, true);   // a copy
        }
      }
      sb.Append($"{pad}{CType(elems[index].Type)} {temp} = {Convert(value, elems[index].Type, s.Children[index])};\n");
    }
    foreach (var owned in emptied.Distinct()) {
      sb.Append($"{pad}{owned.CName}.p = NULL;\n{pad}{owned.CName}.len = 0;\n");
    }
    return $"(({CType(currentRet!)}){{ {string.Join(", ", temps)} }})";
  }

  // i32 id, string name = getUser()   |   x, y = y, x   |   existing, _, i32 fresh = f()
  // Every value is evaluated before any target is written.
  void EmitMultiAssign(Node s, StringBuilder sb, Scope scope, string pad) {
    var targets = s.Children[0].Children;
    var values = s.Children[1].Children;
    var resolved = new List<Expr?>();

    if (values.Count == 1 && values[0].Kind == "GroupValue") {
      var source = values[0].Children[0];
      var sourceValue = ExprOf(source, scope);
      if (!IsStruct(sourceValue.Type)) {
        // a tuple variable: by the names of the fields of the group
        if (!IsSimpleReceiver(source)) {
          throw NotSupported("Assigning to a field group the result of an expression (use a tuple literal or a variable)", s);
        }
        values = values[0].Text!.Split(',').Select(field => Synthetic("Member", source, field).Add(CopyTree(source))).ToList();
      } else {
        values = new List<Node> { source };   // a struct: its fields in order (spec 11.1.5.8)
      }
    }
    if (values.Count == 1 && ExprOf(values[0], scope) is { } single && IsStruct(single.Type)) {
      // a, b = s: the fields of the struct in order (spec 11.1.5.8)
      var fields = StructTupleFields(single.Type, s);
      if (fields.Count != targets.Count) {
        throw Error($"the struct '{single.Type}' has {fields.Count} fields, not {targets.Count}", s);
      }
      string held = NewTemp();
      sb.Append($"{pad}{CType(single.Type)} {held} = {single.Code};\n");
      foreach (var field in fields) {
        resolved.Add(new Expr(StructFieldRead(held, field), field.Type, single.Fresh || field.Type != "string"));
      }
    } else if (values.Count == 1) {
      var value = ExprOf(values[0], scope);
      if (!IsMulti(value.Type)) {
        if (IsTuple(value.Type)) {
          throw Error("tuple destructuring on the left is not supported yet (open item in the spec)", s);
        }
        throw Error($"{targets.Count} targets but only one value", s);
      }
      var elems = TupleElems(value.Type);
      if (elems.Count != targets.Count) {
        throw Error($"the call returns {elems.Count} values, not {targets.Count}", s);
      }
      string temp = NewTemp();
      sb.Append($"{pad}{CType(value.Type)} {temp} = {value.Code};\n");
      for (int index = 0; index < elems.Count; index++) {
        if (IsCountedType(elems[index].Type)) {
          // @Dog a, i32 n = pick(): the target takes the usage the function gave (spec 12.7)
          if (targets[index].Kind == "Discard") {
            sb.Append($"{pad}df_rc_release({temp}.e{index});\n");
            resolved.Add(null);
            continue;
          }
          if (CountedTarget(targets[index], scope) is not (string partType, bool partWritable)) {
            throw Error($"this value is a counted {CountedBase(elems[index].Type)}: its target is a counted variable (@{CountedBase(elems[index].Type)} x) (spec 12.7)", targets[index]);
          }
          if (BaseOf(partType) != BaseOf(CountedBase(elems[index].Type)) && !IsSubtype(CountedBase(elems[index].Type), partType)) {
            throw Error($"this value is a counted {CountedBase(elems[index].Type)}, not a {partType}", targets[index]);
          }
          bool writableResult = values[0] is { Kind: "Call" } rwCall && CalleesOf(rwCall) is { Count: > 0 } rwCallees && rwCallees.All(f => f.WritableCounted.Contains(index));
          if (partWritable && !writableResult) {
            throw Error($"this value is a read-only @{CountedBase(elems[index].Type)}: a read/write &@{partType} needs a &@T result (spec 12.7)", targets[index]);
          }
          if (!partWritable && writableResult && BaseOf(partType) == "string") {
            // a @string target of a &@string result: a compact copy, the usage the function gave is released (spec 12.7)
            string copyTemp = NewTemp();
            var source = new CountedRefInfo($"{temp}.e{index}", CountedBase(elems[index].Type), "the result", null, null, true);
            sb.Append($"{pad}df_rc* {copyTemp} = {CopyCell(source, partType)};\n{pad}df_rc_release({temp}.e{index});\n");
            resolved.Add(new Expr(copyTemp, CountedUsageType));
            continue;
          }
          resolved.Add(new Expr($"{temp}.e{index}", CountedUsageType));
          continue;
        }
        if (CountedTarget(targets[index], scope) is (string newType, bool newWritable)) {
          // @Dog a, i32 n = make() with fun ->Dog, i32 make(): the given object starts counting, as for @Dog x = make() (spec 12.7)
          bool given = values[0] is { Kind: "Call" } call && CalleesOf(call) is { Count: > 0 } callees && callees.All(f => f.Gives.Contains(index));
          string elem = elems[index].Type;
          if (IsCountedValueType(newType)) {
            resolved.Add(new Expr(NewCell(new Expr($"{temp}.e{index}", elem, true), newType, targets[index]), CountedUsageType));
            continue;
          }
          if (!given || !IsClass(elem) || !IsSubtype(elem, newType)) {
            throw Error($"a counted variable takes a counted value or an object the function gives (->{DisplayName(newType)}) (spec 12.7)", targets[index]);
          }
          resolved.Add(new Expr($"df_rc_new((void*)({temp}.e{index}))", CountedUsageType));
          continue;
        }
        resolved.Add(new Expr($"{temp}.e{index}", elems[index].Type, true));
      }
    } else {
      if (values.Count != targets.Count) {
        throw Error($"{targets.Count} targets but {values.Count} values", s);
      }
      for (int index = 0; index < values.Count; index++) {
        if (CountedTarget(targets[index], scope) is (string countedType, bool countedWritable)) {
          // @Dog a, @Dog b = x, y and a, b = b, a on counted variables: every usage is taken first (spec 12.7)
          string usage = NewTemp();
          string usageCode = IsCountedValueType(countedType)
            ? CountedCellValue(values[index], scope, countedType, countedWritable, values[index])   // @i32 c, @string t = 5, "five"
            : CountedValue(values[index], scope, countedType, false, values[index], countedWritable);
          sb.Append($"{pad}df_rc* {usage} = {usageCode};\n");
          resolved.Add(new Expr(usage, CountedUsageType));
          continue;
        }
        string? targetType = TargetType(targets[index], scope);
        var value = ExprOf(values[index], scope, targetType);
        if (IsMulti(value.Type)) {
          throw Error("a call with several return values must be the only value", values[index]);
        }
        if (targets[index].Kind == "Discard") {
          if (value.Type != "null") {
            sb.Append($"{pad}(void)({value.Code});\n");
          }
          resolved.Add(null);
          continue;
        }
        string tempType = targetType ?? value.Type;
        string temp = NewTemp();
        string converted = Convert(value, tempType, values[index]);
        sb.Append($"{pad}{CType(tempType)} {temp} = {converted};\n");
        if (tempType == "string") {
          string fresh = FreshCode(value, converted);
          if (values[index].Kind == "Name" && scope.Find(values[index].Text!) is VarInfo { OwnFlag: string sourceFlag }) {
            fresh = NewTemp();
            sb.Append($"{pad}bool {fresh} = {sourceFlag};\n{pad}{sourceFlag} = false;\n");
          }
          stringFresh[temp] = fresh;
        }
        resolved.Add(new Expr(temp, tempType));
      }
    }

    bool ownValues = GivesOwnMultiValues(s);   // the arrays are new: an owner that receives one ends its old array (spec 6.8.1)
    for (int index = 0; index < targets.Count; index++) {
      CheckLentPartTarget(s, targets[index], index, resolved[index], scope);
      AssignTarget(targets[index], resolved[index], sb, scope, pad, ownValues);
      if (targets[index].Kind == "Decl" && values.Count == 1 && values[0] is { Kind: "Call" } lendingCall && scope.Find(targets[index].Text!) is VarInfo lent
          && IsClass(lent.Type) && !lent.Own && !LentPartGiven(lendingCall, index)) {
        // the new variable borrows the object: read-only unless written &T, and what it comes from stays as it is until its last use (spec 12)
        lent.ReadOnlyRef = !targets[index].Children.Any(child => child.Kind == "ReadWrite");
        CheckLentTarget(targets[index], lendingCall, lent, scope);
      }
    }
    stringFresh.Clear();
  }

  // the temporaries of a multiple assignment that hold a string: C for "the string is new"
  readonly Dictionary<string, string> stringFresh = new();

  string TargetFresh(Expr value) =>
    stringFresh.TryGetValue(value.Code, out var fresh) ? fresh : value.Fresh || value.Type != "string" ? "true" : "false";

  string? TargetType(Node target, Scope scope) {
    if (target.Kind == "Discard") {
      return null;
    }
    if (target.Kind == "Decl") {
      return ResolveType(target.Children.First(child => TypeNodeKinds.Contains(child.Kind)));
    }
    if (PropertyTarget(target, scope) is var (_, property)) {
      return property.Type;   // set through its W: (AssignTarget)
    }
    return LValue(target, scope).Type;
  }

  void AssignTarget(Node target, Expr? value, StringBuilder sb, Scope scope, string pad, bool ownValues = false) {
    if (target.Kind == "Discard" || value == null) {
      return;
    }
    if (target.Kind != "Decl") {
      CheckUnrestrictedWrite(target, scope);   // a, field = f() (spec 1.6.2)
    }
    if (value.Value.Type == CountedUsageType) {
      AssignCountedTarget(target, value.Value.Code, sb, scope, pad);
      return;
    }
    if (PropertyTarget(target, scope) is var (propertyObj, property)) {
      sb.Append(pad).Append(PropertySet(propertyObj, property, CValue(value.Value, target), scope, target).Code).Append(";\n");   // the setter
      return;
    }
    if (target.Kind == "Decl") {
      string type = ResolveType(target.Children.First(child => TypeNodeKinds.Contains(child.Kind)));
      if (scope.Vars.ContainsKey(target.Text!)) {
        throw Error($"'{target.Text}' is already declared in this scope", target);
      }
      CheckNotShadowing(target.Text!, scope, target);
      if (frames.Peek().Hoisted.TryGetValue(target.Text!, out var hoisted)) {
        scope.Vars[target.Text!] = hoisted;
        hoisted.OwnsArray = ownedArrayDecls.Contains(target);
        MarkAssigned(hoisted);
        sb.Append($"{pad}{hoisted.CName} = {Convert(value.Value, type, target)};\n");
        if (hoisted.OwnFlag != null) {
          sb.Append($"{pad}{hoisted.OwnFlag} = {TargetFresh(value.Value)};\n");
        }
        return;
      }
      string cname = "v_" + target.Text;
      sb.Append($"{pad}{CType(type)} {cname} = {Convert(value.Value, type, target)};\n");
      var declared = StringVar(new VarInfo(type, cname, false) { OwnsArray = ownedArrayDecls.Contains(target) });
      if (declared.OwnFlag != null) {
        sb.Append($"{pad}bool {declared.OwnFlag} = {TargetFresh(value.Value)};\n{pad}(void){declared.OwnFlag};\n");
      }
      scope.Vars[target.Text!] = declared;
      return;
    }
    var lvalue = LValue(target, scope);
    if (lvalue.IsConst) {
      throw Error($"'{target.Text}' is const", target);
    }
    MarkAssigned(lvalue.Variable);
    if (lvalue.Type == "string") {
      sb.Append($"{pad}{StrAssign(lvalue, TargetFresh(value.Value), Convert(value.Value, lvalue.Type, target))};\n");
      return;
    }
    if (ownValues && IsArr(lvalue.Type) && !LibraryCode && (lvalue.Variable is { OwnsArray: true } || lvalue.IsField || lvalue.IsStatic)) {
      // x, n = f() where x owns its array: the old one ends, the new one is its own
      string old = NewTemp();
      string flag = lvalue.Variable?.ArrFlag is string arrFlag ? arrFlag : "true";
      sb.Append($"{pad}{{ {CType(lvalue.Type)} {old} = {lvalue.Code}; {lvalue.Code} = {Convert(value.Value, lvalue.Type, target)}; "
        + $"if ({flag} && {old}.p != {lvalue.Code}.p) {{ {ArrDropHelper(lvalue.Type)}({old}); }} }}\n");
      if (lvalue.Variable?.ArrFlag != null) {
        sb.Append($"{pad}{lvalue.Variable.ArrFlag} = true;\n");
      }
      return;
    }
    sb.Append($"{pad}{lvalue.Code} = {Convert(value.Value, lvalue.Type, target)};\n");
  }

  // the type of a usage taken for a counted target of a multiple assignment
  const string CountedUsageType = "@usage";

  // the part index of a call gives the object (->T): the target owns it
  bool LentPartGiven(Node call, int index) => CalleesOf(call) is { Count: > 0 } callees && callees.All(f => f.Gives.Contains(index));

  // &Dog dog as a target of several results: only for an object the function lends read/write (a &T result, or tryGet of a map, spec 6.6)
  void CheckLentPartTarget(Node s, Node target, int index, Expr? value, Scope scope) {
    if (target.Kind != "Decl" || !target.Children.Any(child => child.Kind == "ReadWrite") || IsCountedDecl(target)
        || target.Children.Any(child => child.Kind == "Weak")) {
      return;
    }
    string type = ResolveType(target.Children.First(child => TypeNodeKinds.Contains(child.Kind)));
    if (!IsClass(type)) {
      throw Error($"&{DisplayName(type)} {target.Text}: a read/write lend is for objects; a {DisplayName(type)} is a copy (spec 13.1.1)", target);
    }
    var values = s.Children[1].Children;
    bool writableLend = values.Count == 1 && values[0] is { Kind: "Call" } call && CalleesOf(call) is { Count: > 0 } callees
      && callees.All(f => f.Writes.Contains(index) || f.Name.EndsWith(".tryGet") && f.Class != null && (IsMap(f.Class.Name) || IsHmap(f.Class.Name)));
    if (!writableLend) {
      throw Error($"&{DisplayName(type)} {target.Text}: this value is lent read-only; write {DisplayName(type)} {target.Text} (spec 13.1.1)", target);
    }
  }

  // one counted value of return a, b in a function with several results: as for the result of fun @T (a variable gives its usage, a field
  // adds a user, any other value is counted as for a new counted variable)
  string CountedPartReturn(Node value, Scope scope, string type, Node at, bool writable = false) {
    if (IsCountedValueType(type)) {
      return CountedValueReturn(value, scope, type, writable, at);
    }
    if (CountedRef(value.Kind == "CountedOf" && value.Children.Count == 1 && CountedRef(value.Children[0], scope) != null ? value.Children[0] : value, scope) is CountedRefInfo counted) {
      if (!IsSubtype(counted.Type, type)) {
        throw Error($"'{counted.Name}' is a {counted.Type}, not a {type}", at);
      }
      CheckWritableSource(counted, writable, at);   // a &@T result gives only read/write usages
      return counted.Variable != null ? TakeUsage(counted.Variable) : $"df_rc_use({counted.Code})";
    }
    return CountedValue(value, scope, type, false, at, writable);
  }

  // a counted target of a multiple assignment: a @T declaration, a counted variable or a counted field; null for the others
  (string Type, bool Writable)? CountedTarget(Node target, Scope scope) {
    if (target.Kind == "Decl") {
      if (!IsCountedDecl(target)) {
        return null;
      }
      return (ResolveType(target.Children.First(child => TypeNodeKinds.Contains(child.Kind))), target.Children.Any(child => child.Kind == "ReadWrite"));
    }
    if (target.Kind is "Name" or "Member" && CountedRef(target, scope) is CountedRefInfo counted) {
      return (counted.Type, IsWritableRef(counted));
    }
    return null;
  }

  // the target takes the usage; a counted variable or field releases the one it had
  void AssignCountedTarget(Node target, string usage, StringBuilder sb, Scope scope, string pad) {
    if (target.Kind == "Decl") {
      if (!frames.Peek().Hoisted.TryGetValue(target.Text!, out var hoisted) || !hoisted.IsCounted) {
        throw NotSupported("A counted variable in this place (only in blocks and function bodies for now)", target);
      }
      if (scope.Vars.ContainsKey(target.Text!)) {
        throw Error($"'{target.Text}' is already declared in this scope", target);
      }
      sb.Append($"{pad}{hoisted.CName} = {usage};\n");
      scope.Vars[target.Text!] = hoisted;
      assign.Unassigned.Remove(hoisted);
      return;
    }
    var counted = CountedRef(target, scope)!.Value;
    string place = counted.Variable != null ? counted.Variable.CName : CountedFieldTarget(target, scope).Code;
    string old = NewTemp();
    sb.Append(pad).Append(CheckRaise($"({{ df_rc* {old} = {place}; {place} = {usage}; df_rc_release({old}); }})", "void")).Append(";\n");
  }
}
