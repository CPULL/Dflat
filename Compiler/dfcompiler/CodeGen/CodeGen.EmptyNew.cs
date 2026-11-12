using dfparser;

namespace dfcompiler;

// () is the empty construction (spec 11.1.1, 11.1.5.1), as [] is the empty collection: it calls the constructor without parameters of
// the class (or makes the struct with its start values) whose type the context gives: Aclass a = (), a = (), f(()), return ().
public sealed partial class CodeGen {
  Expr EmptyNew(Node n, Scope scope, string? expected) {
    string? type = expected != null && IsNullable(expected) ? BaseOf(expected) : expected;
    if (type == null || type is "var" or "void") {
      throw Error("() builds an object of the type known from the context: here no type is known, write the class (Aclass())", n);
    }
    if (traits.ContainsKey(type)) {
      throw Error($"() cannot build {DisplayName(type)}: it is a trait, write the class to build (Aclass())", n);
    }
    if (!classes.ContainsKey(type) && !IsStruct(type)) {
      throw Error($"() builds an object of a class or a struct, not {DisplayName(type)}", n);
    }
    var token = new Token(TokenKind.Op, "(", n.Line, 0);
    Node callee;
    if (classInstanceArgs.TryGetValue(type, out var instance)) {
      // a copy of a generic class: Box<i32> b = () is Box<i32>()
      callee = new Node("GenericRef", token, "ambiguous").Add(new Node("Name", token, instance.Template.Name));
      foreach (string arg in instance.Args) {
        callee.Add(new Node("Type", token, arg));
      }
    } else {
      callee = new Node("Name", token, type);
    }
    var call = new Node("Call", token).Add(callee).Add(new Node("Args", token));
    try {
      return ExprOf(call, scope, expected);
    } catch (CompileException ex) when (ex.Message.StartsWith("missing argument")) {
      throw Error($"() calls the constructor without parameters, and {DisplayName(type)} has none: write {DisplayName(type)}(...) with its arguments", n);
    }
  }
}
