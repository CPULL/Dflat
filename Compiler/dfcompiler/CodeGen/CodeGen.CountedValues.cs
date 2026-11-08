using dfparser;

namespace dfcompiler;

// Counted values and strings (spec 12.7): @i32, &@i32, @string, &@string, and their nullable forms. The variable holds a counting block
// (df_rc*, kind 1 or 2) whose object is a cell on the heap with the value. A new variable or parameter shares the cell; = on a &@T writes
// the cell (with the lock); = on a read-only @T gives the variable a new cell with a copy. Strings are never shared with a &@string: a
// @string made from one gets a compact copy. A &@string is read only inside enforce. Built for local variables and parameters; fields,
// results and items are not built yet.
public sealed partial class CodeGen {
  // the CNames of the counted variables whose lock an enclosing enforce holds (spec 7.2.2)
  readonly HashSet<string> heldLocks = new();
  bool typingOnly;   // ExprOf is asked only for a type (ZeroLike): no read happens

  // the types that can be counted as values: numbers, bool, char, enums and bitfields, strings, and their nullable forms
  bool IsCountedValueType(string type) {
    string plain = BaseOf(type);
    return plain is "string" or "bool" or "char" || IsNumeric(plain) || IsEnum(plain);
  }

  bool IsCountedValueVar(VarInfo variable) => variable.IsCounted && IsCountedValueType(variable.Type);

  bool IsCountedValueRef(CountedRefInfo counted) => IsCountedValueType(counted.Type);

  // the value of a cell: a null block reads as the zero value, or as null for a nullable type
  string CellRead(string block, string type) {
    string plain = BaseOf(type);
    string held = NewTemp();
    string value = $"*({CType(plain)}*){held}->obj";
    if (!IsNullable(type)) {
      return $"({{ df_rc* {held} = {block}; {held} != NULL ? {value} : {ZeroValue(plain)}; }})";
    }
    if (plain == "string") {
      return $"({{ df_rc* {held} = {block}; ({CType(type)}){{ {held} != NULL, {held} != NULL ? {value} : NULL }}; }})";
    }
    return $"({{ df_rc* {held} = {block}; ({CType(type)}){{ {held} != NULL, {held} != NULL ? {value} : {ZeroValue(plain)} }}; }})";
  }

  // a new cell with a value; a null value of a nullable type is no cell
  string NewCell(Expr value, string type, Node at) {
    string plain = BaseOf(type);
    if (value.Type == "null") {
      return "NULL";
    }
    if (IsNullable(value.Type)) {
      if (!IsNullable(type)) {
        throw Error($"this value can be null: the counted variable needs the type @{plain}?", at);
      }
      string opt = NewTemp();
      string inner = NewCell(new Expr($"{opt}.val", BaseOf(value.Type), value.Fresh), plain, at);
      return $"({{ {CType(value.Type)} {opt} = {value.Code}; {opt}.has ? {inner} : NULL; }})";
    }
    if (plain == "string") {
      string converted = Convert(value, "string", at);
      return $"df_rc_new_str({converted}, {FreshCode(value, converted)})";
    }
    string temp = NewTemp();
    return $"({{ {CType(plain)} {temp} = {Convert(value, plain, at)}; df_rc_new_val(&{temp}, sizeof {temp}); }})";
  }

  // a new cell with a copy of the value of another cell. Out of a &@T it takes the lock automatically (not needed when an enclosing
  // enforce holds it); when the lock fails the result is the zero value, or no cell (null) for a nullable target (spec 12.7)
  string CopyCell(CountedRefInfo source, string type) {
    string plain = BaseOf(type);
    string held = NewTemp();
    bool locked = IsWritableRef(source) && !heldLocks.Contains(source.Code);
    string canRead = locked ? $"DF_RC_CAN_COPY({held})" : $"DF_RC_ALIVE({held})";
    string failed = IsNullable(type) ? "NULL"
      : plain == "string" ? "df_rc_new_str(df_str_lit(\"\", 0), true)"
      : $"({{ {CType(plain)} z_{held} = {ZeroValue(plain)}; df_rc_new_val(&z_{held}, sizeof z_{held}); }})";
    string copy = plain == "string" ? $"df_rc_new_str(*(df_string*){held}->obj, false)" : $"df_rc_new_val({held}->obj, sizeof({CType(plain)}))";
    return $"({{ df_rc* {held} = {source.Code}; {canRead} ? {copy} : {failed}; }})";
  }

  // the counting block a counted value variable or parameter receives: a counted variable shares its cell (a &@string is copied into a
  // @string), ->x moves the usage, any other value makes a new cell
  string CountedCellValue(Node value, Scope scope, string type, bool writable, Node at) {
    if (CountedPropertyUsage(value.Kind == "CountedOf" && value.Children.Count == 1 ? value.Children[0] : value, scope, type, writable, at) is string usage) {
      return usage;   // @i32 n = k.hits: the usage that R: gives (spec 11.1.4)
    }
    if (value.Kind == "CountedOf" && value.Children.Count == 1) {
      value = value.Children[0];   // @x: the marker of the call, or a plain value copied into a new cell
    }
    if (value.Kind == "Null") {
      return "NULL";
    }
    if (value.Kind == "Gift" && CountedRef(value.Children[0], scope) is CountedRefInfo moved) {
      CheckCellType(moved, type, at);
      CheckWritableSource(moved, writable, at);
      return TakeSourceUsage(moved, value, scope);
    }
    if (CountedRef(value, scope) is CountedRefInfo source) {
      CheckCellType(source, type, at);
      CheckWritableSource(source, writable, at);
      if (BaseOf(type) == "string" && IsWritableRef(source) && !writable) {
        return CopyCell(source, type);   // never shared with a &@string: a compact copy (spec 12.7)
      }
      return $"df_rc_use({source.Code})";
    }
    var given = ExprOf(value, scope, BaseOf(type));
    if (IsCountedType(given.Type)) {
      return AdoptedCell(given, value, scope, type, writable, at);   // @i32 n = f(): the usage the function gives
    }
    return NewCell(given, type, at);
  }

  // the usage given by a function that returns a counted value: kept as it is, or copied into a @string when it is a &@string
  string AdoptedCell(Expr given, Node value, Scope scope, string type, bool writable, Node at) {
    string from = CountedBase(given.Type);
    if (BaseOf(from) != BaseOf(type)) {
      throw Error($"this is a counted {from}, not a {type}", at);
    }
    if (IsNullable(from) && !IsNullable(type)) {
      throw Error($"this counted value can be null: the variable needs the type @{BaseOf(type)}? (spec 12.7)", at);
    }
    bool givesWritable = GivesWritableUsage(value, scope);
    if (writable && !givesWritable) {
      throw Error($"this is a read-only @{from}: a read/write &@{type} needs a &@T result (spec 12.7)", at);
    }
    if (BaseOf(type) == "string" && givesWritable && !writable) {
      // never shared with a &@string: a compact copy, and the usage the function gave is released (spec 12.7)
      string taken = NewTemp();
      string copy = NewTemp();
      var source = new CountedRefInfo(taken, from, "the result", null, null, true);
      return $"({{ df_rc* {taken} = {given.Code}; df_rc* {copy} = {CopyCell(source, type)}; df_rc_release({taken}); {copy}; }})";
    }
    return given.Code;
  }

  // return value in a function that gives a counted value (fun @i32, fun &@string): a counted variable gives its usage (it is null
  // afterwards), a field or an item adds a user, any other value makes a new cell; a read-only usage never goes out of a &@T result,
  // and a @string result is never shared with a &@string (a compact copy)
  string CountedValueReturn(Node value, Scope scope, string type, bool writable, Node at) {
    if (value.Kind == "CountedOf" && value.Children.Count == 1) {
      value = value.Children[0];
    }
    if (CountedRef(value, scope) is CountedRefInfo counted) {
      CheckCellType(counted, type, at);
      CheckWritableSource(counted, writable, at);
      if (BaseOf(type) == "string" && IsWritableRef(counted) && !writable) {
        return CopyCell(counted, type);
      }
      return counted.Variable != null ? TakeUsage(counted.Variable) : $"df_rc_use({counted.Code})";
    }
    return CountedCellValue(value, scope, type, writable, at);
  }

  void CheckCellType(CountedRefInfo source, string type, Node at) {
    if (BaseOf(source.Type) != BaseOf(type)) {
      throw Error($"'{source.Name}' is a counted {source.Type}, not a {type}", at);
    }
    if (IsNullable(source.Type) && !IsNullable(type)) {
      throw Error($"'{source.Name}' can be null: the counted variable needs the type @{BaseOf(type)}? (spec 12.7)", at);
    }
  }

  // x = value for a counted value: on a &@T it writes the shared cell (the lock is checked like any write); on a read-only @T the
  // variable gets a new cell with a copy and releases the old one (spec 12.7)
  string? CountedValueAssign(Node s, VarInfo variable, Scope scope) {
    if (variable.IsWritable) {
      return null;   // the usual assignment through the place of the cell (LValue)
    }
    if (s.Text != "=") {
      throw Error($"'{s.Children[0].Text}' is a read-only counted value (@{variable.Type}): it is written through a &@{variable.Type} (spec 12.7)", s);
    }
    var value = s.Children[1];
    string code;
    if (value.Kind == "Null") {
      code = "NULL";
    } else if (CountedRef(value.Kind == "CountedOf" ? value.Children[0] : value, scope) is CountedRefInfo source) {
      CheckCellType(source, variable.Type, s);
      code = CopyCell(source, variable.Type);
    } else {
      code = NewCell(ExprOf(value, scope, BaseOf(variable.Type)), variable.Type, s);
    }
    string fresh = NewTemp();
    string old = NewTemp();
    MarkAssigned(variable);
    return $"({{ df_rc* {fresh} = {code}; df_rc* {old} = {variable.CName}; {variable.CName} = {fresh}; df_rc_release({old}); }})";
  }

  // the place of the cell of a &@T value: written in place by =, +=, ++ (the lock check comes from WriteLockCheck)
  LValueInfo CountedValuePlace(VarInfo variable, Node target) {
    if (!variable.IsWritable) {
      throw Error($"'{target.Text}' is a read-only counted value (@{variable.Type}): it is written through a &@{variable.Type} (spec 12.7)", target);
    }
    if (IsNullable(variable.Type)) {
      throw NotSupported("Writing a read/write counted nullable value (&@T?)", target);
    }
    string held = NewTemp();
    string plain = variable.Type;
    string cell = $"(*({CType(plain)}*)({{ df_rc* {held} = {variable.CName}; if ({held} == NULL) {{ df_raise(&df_desc_NullPointerException, {fn!.NameConst}); {RaiseJump} }} {held}; }})->obj)";
    return new LValueInfo(cell, plain, false, null, true);
  }

  // the cell of a &@T value field, written in place; its lock is checked here (a field is not named by an enforce: its lock is held
  // through a variable that shares the cell). A read-only @T field is written only with = (a cell of its own)
  LValueInfo CountedValueFieldPlace(string blockCode, FieldInfo field, Node target) {
    if (!field.IsWritable) {
      throw Error($"'{field.Name}' is a read-only counted value (@{field.Type}): it is written through a &@{field.Type} (spec 12.7)", target);
    }
    if (IsNullable(field.Type)) {
      throw NotSupported("Writing a read/write counted nullable value (&@T?)", target);
    }
    string held = NewTemp();
    string cell = $"(*({CType(field.Type)}*)({{ df_rc* {held} = {blockCode}; if ({held} == NULL) {{ df_raise(&df_desc_NullPointerException, {fn!.NameConst}); {RaiseJump} }} "
      + $"if (!DF_RC_WRITABLE({held})) {{ df_raise(&df_desc_WritingOnRefCountWithoutLockingException, {fn!.NameConst}); {RaiseJump} }} {held}; }})->obj)";
    return new LValueInfo(cell, field.Type, false, null, true);
  }

  // the cell of an item of a list<&@T>, vec<&@T> or arr<&@T> of values, written in place with its lock (an empty slot is a null error)
  LValueInfo CountedValueItemPlace(CountedRefInfo item, Node target) {
    if (IsNullable(item.Type)) {
      throw NotSupported("Writing a read/write counted nullable value (&@T?)", target);
    }
    string held = NewTemp();
    string cell = $"(*({CType(item.Type)}*)({{ df_rc* {held} = {item.Code}; if ({held} == NULL) {{ df_raise(&df_desc_NullPointerException, {fn!.NameConst}); {RaiseJump} }} "
      + $"if (!DF_RC_WRITABLE({held})) {{ df_raise(&df_desc_WritingOnRefCountWithoutLockingException, {fn!.NameConst}); {RaiseJump} }} {held}; }})->obj)";
    return new LValueInfo(cell, item.Type, false, null, true);
  }

  // = on a counted value field that gets a cell of its own: a start value (the constructor: a counted source is shared, as for a new
  // variable) or = on a read-only @T field (a copy, as for a read-only variable); the old usage is released
  string CountedValueFieldRebind(Node s, CountedRefInfo field, Scope scope) {
    if (s.Text != "=") {
      throw Error($"'{field.Name}' is a read-only counted value (@{field.Type}): it is written through a &@{field.Type} (spec 12.7)", s);
    }
    var value = s.Children[1];
    string code;
    if (initializing) {
      code = CountedCellValue(value, scope, field.Type, IsWritableRef(field), s);
    } else if (value.Kind == "Null") {
      code = "NULL";
    } else if (CountedRef(value.Kind == "CountedOf" ? value.Children[0] : value, scope) is CountedRefInfo source) {
      CheckCellType(source, field.Type, s);
      code = CopyCell(source, field.Type);
    } else {
      code = NewCell(ExprOf(value, scope, BaseOf(field.Type)), field.Type, s);
    }
    var target = CountedFieldTarget(s.Children[0], scope);
    string fresh = NewTemp();
    string slot = NewTemp();
    string old = NewTemp();
    return CheckRaise($"({{ df_rc* {fresh} = {code}; df_rc** {slot} = &({target.Code}); df_rc* {old} = *{slot}; *{slot} = {fresh}; df_rc_release({old}); }})", "void");
  }

  // a read of a counted value variable; a &@string is read only inside an enforce that holds its lock (spec 12.7)
  Expr CountedValueRead(VarInfo variable, Node at) {
    if (BaseOf(variable.Type) == "string" && variable.IsWritable && !heldLocks.Contains(variable.CName) && !typingOnly) {
      throw Error($"'{at.Text}' is a &@string: it is read only inside enforce {at.Text} {{ }} (or the expression form, enforce {at.Text} lockfail {{ ... }}), "
        + "a write can move its text (spec 12.7)", at);
    }
    return new Expr(CellRead(variable.CName, variable.Type), variable.Type);
  }
}
