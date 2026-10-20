using System.Text;
using dfparser;

namespace dfcompiler;

// Exception classes (spec 15.1, 15.2, 15.4): class MyException(i32 code) : Exception { }
// The object starts with the same fields as the runtime exception record (after the class header), so the
// error register can hold it and a catch gets it back as the class. The constructor takes the message first,
// optional, then the fields of the class (also optional: they start as zero values).
public sealed partial class CodeGen {
  readonly Dictionary<string, int> customExceptionIds = new();

  // the fields every exception object has after the class header (the record of the runtime)
  const string ExceptionFields =
    "  uint64_t id;\n  const df_descriptor* descriptor;\n  char* message;\n  char* function;\n  uint64_t line;\n";

  // exceptions that do nothing when no catch can handle them (spec 15.3)
  static readonly HashSet<int> SilentStandard = new() { 12, 17, 18, 24, 27, 28, 29, 32 };

  bool IsExceptionClass(string type) => classes.TryGetValue(type, out var cls) && cls.IsException;

  bool IsSilentClass(ClassInfo cls) => AllTraits(cls).Any(trait => trait.Name == "SilentException");

  static int ExceptionHash(string name) {
    uint hash = 2166136261;
    foreach (char ch in name) {
      hash ^= ch;
      hash *= 16777619;
    }
    return 256 + (int)(hash % 1_000_000);
  }

  // after the parents are known: which classes are exceptions, and their ids
  void RegisterExceptionClasses() {
    foreach (var cls in classOrder) {
      if (cls.Parent?.IsException == true) {
        cls.IsException = true;
      }
    }
    foreach (var cls in classOrder.Where(cls => cls.IsException)) {
      int id = ExceptionHash(cls.Name);
      while (customExceptionIds.ContainsValue(id)) {
        id++;
      }
      customExceptionIds[cls.Name] = id;
      foreach (string reserved in new[] { "Id", "Message", "Function", "Line" }) {
        if (cls.Fields.Any(field => field.Name == reserved)) {
          throw Error($"'{reserved}' is a member of every exception: it cannot be a field of {cls.Name}", cls.Node);
        }
      }
      if (cls.PrimaryParams.Any(param => param.Name == "message")) {
        throw Error("the first parameter of an exception is its message: 'message' is reserved", cls.Node);
      }
    }
  }

  // the default value written in a call, for a parameter that has none
  static Node? DefaultNodeFor(string type, Node at) {
    return type switch {
      "string" => Synthetic("String", at, "\"\""),
      "bool" => Synthetic("Bool", at, "false"),
      "r32" or "r64" => Synthetic("Real", at, "0.0"),
      "char" => Synthetic("Char", at, "'\\0'"),
      _ when IsInt(type) => Synthetic("Int", at, "0"),
      _ when type.StartsWith("arr<") || IsTimeType(type) => Synthetic("Null", at, "null"),
      _ => null
    };
  }

  // message first, then the fields: all optional
  List<ParamSig> ExceptionConstructorParams(ClassInfo cls) {
    var at = cls.Node;
    var result = new List<ParamSig> { new("message", "string", Synthetic("String", at, "\"\"")) };
    foreach (var param in cls.PrimaryParams) {
      result.Add(new ParamSig(param.Name, param.Type, param.Default ?? DefaultNodeFor(param.Type, at)));
    }
    return result;
  }

  // descriptors with the chain of parents, for catch and for the silent flag
  void EmitExceptionDescriptors() {
    foreach (var cls in classOrder.Where(cls => cls.IsException)) {
      string parent = cls.Parent != null ? $"&df_desc_{cls.Parent.Name}" : "&df_desc_Exception";
      classCode.Append($"static const df_descriptor df_desc_{cls.Name} = {{ {customExceptionIds[cls.Name]}, \"{cls.Name}\", {parent}, {(IsSilentClass(cls) ? "true" : "false")} }};\n");
    }
    classCode.Append('\n');
  }

  // exception members of an object of an exception class
  Expr? ExceptionObjectMember(Expr obj, string name, Node at) {
    if (name is not ("Id" or "Message" or "Function" or "Line")) {
      return null;
    }
    return ExceptionMember(new Expr($"((df_exception*)({NullChecked(obj.Code, obj.Type)}))", "Exception"), name, at);
  }

  // throw MyException("message", 3): the object goes into the error register
  bool ThrowObject(Node s, Node call, string name, StringBuilder sb, Scope scope, string pad) {
    var cls = classes[name];
    if (!cls.IsException) {
      throw Error($"{name} is not an exception class: derive it from Exception", s);
    }
    int id = customExceptionIds[name];
    string? condition = IsSilentClass(cls) ? SilentThrowCondition(id) : "";
    if (condition == null) {
      return false;      // a silent exception nobody observes: execution goes on
    }
    var created = ExprOf(call, scope);
    string temp = NewTemp();
    string raise = $"{{ {CType(name)} {temp} = {created.Code}; "
      + $"{temp}->function = df_cstr_truncated({fn!.NameConst}, strlen({fn.NameConst}), 1024); "
      + $"df_err = (df_exception*){temp}; df_err_thrown = true; goto {Handler}; }}";
    sb.Append(condition == "" ? $"{pad}{raise}\n" : $"{pad}if ({condition}) {raise}\n");
    return condition == "";
  }
}
