using System.Text;
using dfparser;

namespace dfcompiler;

// replace <field> = <expression> (spec 11.1.1): inside a constructor body, a child sets a public field of a parent class, after the
// constructor of the parent has run (the order of the ancestry). It is an assignment: the old value is released (a string is freed,
// an object ends). A plain = on a field of a parent inside a constructor body is a compile error: replace says the developer takes
// responsibility for it. On an exception, replace message is truncated to 2 KB, as when the exception is built (spec 15.2).
public sealed partial class CodeGen {
  void EmitReplaceField(Node s, StringBuilder sb, Scope scope, int indent) {
    var cls = currentFun is { IsInit: true, Class: { IsTrait: false } owner } ? owner : null;
    if (cls == null) {
      throw Error("replace sets a field of a parent class only inside a constructor body (spec 11.1.1)", s);
    }
    string name = s.Text!;
    if (cls.IsException && name == "message") {
      EmitReplaceMessage(s, sb, scope, indent);
      return;
    }
    if (cls.IsException && ExceptionMembers.Contains(name)) {
      throw Error($"'{name}' of an exception is set by the runtime: only message can be replaced (spec 15.2)", s);
    }
    if (cls.Fields.Any(field => field.Name == name && !field.IsStatic && !field.Hidden)) {
      throw Error($"'{name}' is a field of {DisplayName(cls.Name)} itself: assign it without replace (spec 11.1.1)", s);
    }
    var inherited = cls.Parent == null ? null : FindField(cls.Parent, name, s);
    if (inherited == null || inherited.IsStatic || inherited.Hidden) {
      throw Error($"'{name}' is not a field of a parent of {DisplayName(cls.Name)} (spec 11.1.1)", s);
    }
    if (inherited.Visibility != Vis.Public) {
      throw Error($"'{name}' of {DisplayName(inherited.Owner.Name)} is not public: replace sets only the public fields of a parent (spec 11.1.1)", s);
    }
    var assign = Synthetic("Assign", s, "=").Add(Synthetic("Name", s, name)).Add(s.Children[0]);
    EmitStmt(assign, sb, scope, indent);
  }

  // replace message = text: the new text in the 2 KB buffer of the exception, cut at a whole character (spec 15.2)
  void EmitReplaceMessage(Node s, StringBuilder sb, Scope scope, int indent) {
    string pad = Pad(indent);
    var value = ExprOf(s.Children[0], scope, "string");
    string code = Convert(value, "string", s);
    string text = NewTemp();
    string drop = value.Fresh ? $" df_str_free({text});" : "";
    sb.Append($"{pad}{{ df_string {text} = {code}; free(self->message); self->message = df_cstr_truncated(df_view({text}).p, df_str_len({text}), 2048);{drop} }}\n");
  }

  // a plain = on a field of a parent in the body of a constructor: replace is required (spec 11.1.1)
  void CheckParentFieldWrites(ClassInfo cls, Node body, Node paramsNode) {
    if (cls.Parent == null && !cls.IsException || body.Line >= LibraryLine) {
      return;
    }
    var locals = new HashSet<string>(paramsNode.Children.Select(p => p.Text!).Where(text => text != null));
    void Walk(Node node) {
      if (node.Kind is "Lambda" or "Fun" or "Closure") {
        return;
      }
      if (node.Kind is "Decl" or "Var" or "WritableVar" && node.Text != null) {
        locals.Add(node.Text);
      }
      if (node.Kind == "Assign" && node.Text == "=" && node.Children.Count == 2) {
        var target = node.Children[0];
        string? name = target switch {
          { Kind: "Name" } when !locals.Contains(target.Text!) => target.Text,
          { Kind: "Member", Children: [{ Kind: "This" }] } => target.Text,
          _ => null
        };
        if (name != null && !cls.Fields.Any(field => field.Name == name && !field.IsStatic)
            && (cls.IsException && name == "message" || cls.Parent != null && FindField(cls.Parent, name) is { IsStatic: false })) {
          throw Error($"'{name}' is a field of a parent of {DisplayName(cls.Name)}: in a constructor write replace {name} = ... (spec 11.1.1)", node);
        }
      }
      foreach (var child in node.Children) {
        Walk(child);
      }
    }
    Walk(body);
  }
}
