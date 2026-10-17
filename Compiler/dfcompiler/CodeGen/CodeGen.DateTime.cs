using dfparser;

namespace dfcompiler;

// Members and operators of date and time (spec 3.3), toStr(format) and format checks (spec 8.4).
public sealed partial class CodeGen {
  // letters of the date format that only a date has (spec 8.4)
  const string DateOnlyLetters = "dMygzwqirej";

  // read-only properties: date.year, time.hour ...
  Expr? DateTimeProperty(Expr obj, string name, Node at) {
    if (obj.Type == "date") {
      NeedChunk("date");
      int? part = name switch {
        "year" => 0, "month" => 1, "day" => 2, "hour" => 3, "minute" => 4, "second" => 5, "millisecond" => 6,
        "dayOfYear" => 8, _ => null
      };
      if (part != null) {
        return new Expr($"((int32_t)df_date_part({obj.Code}, {part}))", "i32");
      }
      return name switch {
        "dayOfWeek" => new Expr($"((int32_t)df_date_part({obj.Code}, 7))", "DayOfWeek"),
        "timeOfDay" => new Expr($"df_date_timeofday({obj.Code})", "time"),
        "unixMilliseconds" => new Expr($"({obj.Code})", "i64"),
        _ => null
      };
    }
    if (obj.Type == "time") {
      NeedChunk("date");
      int? part = name switch { "hour" => 3, "minute" => 4, "second" => 5, "millisecond" => 6, _ => null };
      if (part != null) {
        return new Expr($"df_time_part({obj.Code}, {part})", "i32");
      }
      if (name == "totalMilliseconds") {
        return new Expr($"({obj.Code})", "i32");
      }
    }
    return null;
  }

  // x.toStr(format) on a date or a time
  Expr FormatCall(Expr value, List<Node> args, Scope scope, Node at) {
    var format = args[0];
    if (format.Kind == "Named") {
      throw Error("toStr takes the format as its only argument", at);
    }
    string formatCode = Convert(ExprOf(format, scope, "string"), "string", format);
    string? literal = format.Kind == "String" ? FromCodePoints(Unescape(format.Text![1..^1], format)) : null;
    if (NumberFormat(value, literal, formatCode, at) is Expr number) {
      return number;
    }
    if (value.Type is not ("date" or "time")) {
      throw Error($"toStr(format) is not supported on {value.Type} yet", at);
    }
    if (format.Kind == "String") {
      CheckFormat(value.Type, Unescape(format.Text![1..^1], format), at);
    }
    return DateFormatExpr(value, formatCode);
  }

  Expr DateFormatExpr(Expr value, string formatCode) {
    NeedChunk("date");
    string function = value.Type == "date" ? "df_date_format" : "df_time_format";
    return new Expr($"{function}({value.Code}, {formatCode})", "string");
  }

  // a time uses only h H m s f F t k: a date-only letter is a compile error
  void CheckFormat(string type, List<int> pattern, Node at) {
    if (type != "time") {
      return;
    }
    for (int index = 0; index < pattern.Count; index++) {
      int cp = pattern[index];
      if (cp is '\'' or '"') {
        index++;
        while (index < pattern.Count && pattern[index] != cp) {
          index++;
        }
      } else if (cp == '\\') {
        index++;
      } else if (cp < 128 && DateOnlyLetters.Contains((char)cp)) {
        throw Error($"the letter '{(char)cp}' only applies to a date, not to a time", at);
      }
    }
  }

  // {value:spec} in an interpolated string
  Expr? FormattedHole(Expr value, Node spec, Node at) {
    if (IsNumberType(BaseOf(value.Type))) {
      return NumberFormat(value, spec.Text, CString(CodePoints(spec.Text!)), at);
    }
    if (value.Type is not ("date" or "time")) {
      return null;
    }
    CheckFormat(value.Type, CodePoints(spec.Text!), at);
    return DateFormatExpr(value, CString(CodePoints(spec.Text!)));
  }

  // (time)number: out of range gives null and a silent OutOfRangeCastException
  Expr CastToTime(Expr e, Node n) {
    NeedChunk("date");
    string call = $"df_time_from_i64((int64_t)({e.Code}))";
    return new Expr(SilentCall(call, "time", "OutOfRangeCastException", n), "time");
  }

  // operators with date, time, clock and lapse (spec 3.3, 3.4)
  Expr TimeOp(string op, Expr l, Expr r, Node at) {
    if (op is "==" or "!=") {
      string equal = EqualsCode(l, r, at);
      return new Expr(op == "==" ? equal : $"(!{equal})", "bool");
    }
    if (l.Type == "null") {
      l = new Expr(NullCode(r.Type), r.Type);
    }
    if (r.Type == "null") {
      r = new Expr(NullCode(l.Type), l.Type);
    }
    if (l.Type == "date" && r.Type == "time" && op is "+" or "-") {
      NeedChunk("date");
      return new Expr($"{(op == "+" ? "df_date_plus_time" : "df_date_minus_time")}({l.Code}, {r.Code})", "date");
    }
    if (l.Type == "time" && r.Type == "time" && op == "-") {
      NeedChunk("date");
      return new Expr($"df_time_diff({l.Code}, {r.Code})", "lapse");
    }
    if (l.Type != r.Type) {
      throw Error($"cannot apply '{op}' to {l.Type} and {r.Type}", at);
    }
    if (op is "<" or "<=" or ">" or ">=") {
      if (l.Type == "lapse") {
        throw Error("lapses of different units cannot be ordered yet", at);
      }
      return new Expr($"({l.Code} {op} {r.Code})", "bool");   // a null date, time or clock sorts first
    }
    if (op == "-" && l.Type == "date") {
      return new Expr($"df_date_diff({l.Code}, {r.Code})", "lapse");
    }
    if (op == "-" && l.Type == "clock") {
      return new Expr($"df_clock_diff({l.Code}, {r.Code})", "lapse");
    }
    throw Error($"operator '{op}' doesn't apply to {l.Type} and {r.Type}", at);
  }
}
