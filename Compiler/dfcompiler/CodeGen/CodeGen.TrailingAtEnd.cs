using dfparser;

namespace dfcompiler;

// atEnd after a statement (spec 14.2, form 3): S atEnd { B } becomes a block of its own
//   { T x (each local of S that B uses, declared here); atEnd { B }; S, where those declarations are assignments }
// so B sees the union of the scopes of S (a branch that was not taken leaves its zero value), runs once when the statement is left on any
// way out (the end, return, break, jump, a raise), after the atEnd blocks inside S, and keeps alive only the variables it uses.
public sealed partial class CodeGen {
  static readonly HashSet<string> OwnTrailingAtEnd = new() { "Fun", "Class", "Constructor", "Trait", "Struct", "Lambda" };   // forms 2, 4, 5

  void LowerTrailingAtEnds(Node node) {
    for (int index = 0; index < node.Children.Count; index++) {
      var child = node.Children[index];
      LowerTrailingAtEnds(child);
      if (OwnTrailingAtEnd.Contains(child.Kind) || child.Kind is "AtEnd" or "AtEndOf" or "Catch"
          || !child.Children.Any(part => part is { Kind: "AtEnd", Text: "trailing" })) {
        continue;
      }
      node.Children[index] = StatementWithAtEnd(child);
    }
  }

  Node StatementWithAtEnd(Node statement) {
    var trailing = statement.Children.Where(part => part is { Kind: "AtEnd", Text: "trailing" }).ToList();
    statement.Children.RemoveAll(part => trailing.Contains(part));
    var used = new HashSet<string>();
    foreach (var atEnd in trailing) {
      CollectNames(atEnd, used);
    }
    // the declarations of S that the atEnd uses, at any depth (not inside a function or a class written in S)
    var declared = new Dictionary<string, (Node Decl, string Type)>();
    var outer = new List<Node>();
    void Visit(Node parent) {
      for (int index = 0; index < parent.Children.Count; index++) {
        var node = parent.Children[index];
        if (OwnTrailingAtEnd.Contains(node.Kind)) {
          continue;
        }
        if (node.Kind == "Decl" && node.Text != null && used.Contains(node.Text) && parent.Kind != "Params") {
          parent.Children[index] = HoistedForAtEnd(node, parent.Kind == "Targets", declared, outer);
          continue;
        }
        Visit(node);
      }
    }
    Visit(statement);
    var block = Synthetic("Block", statement);
    foreach (var decl in outer) {
      block.Add(decl);
    }
    foreach (var atEnd in trailing) {
      block.Add(Synthetic("AtEnd", atEnd).Add(atEnd.Children[0]));   // a standalone atEnd of the new block: it runs when the block ends
    }
    return block.Add(statement);
  }

  // Dog d = Dog("x") inside S, used by the atEnd: Dog d is declared before S, and the declaration becomes d = Dog("x")
  Node HoistedForAtEnd(Node decl, bool target, Dictionary<string, (Node Decl, string Type)> declared, List<Node> outer) {
    string name = decl.Text!;
    var typeNode = decl.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind));
    if (typeNode == null || typeNode is { Kind: "Type", Text: "var" }) {
      throw NotSupported($"'{name}' is used by the atEnd of its statement and declared with var: write its type (14.2)", decl);
    }
    if (decl.Children.Any(child => child.Kind is "AtEndOf" or "Size")) {
      throw NotSupported($"'{name}' is used by the atEnd of its statement and has its own atEnd or a size (14.2)", decl);
    }
    if (decl.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ').Contains("const") == true) {
      throw NotSupported($"the const '{name}' used by the atEnd of its statement (14.2)", decl);
    }
    bool weak = decl.Children.Any(child => child.Kind is "Weak" or "WeakInit");
    if (decl.Children.Any(child => child.Kind == "ReadWrite") && !IsCountedDecl(decl) && !weak) {
      throw NotSupported($"the read/write reference '{name}' used by the atEnd of its statement (14.2)", decl);
    }
    var markers = decl.Children.Where(child => child.Kind is not ("Init" or "WeakInit")).ToList();
    static string Shape(Node n) => n.Kind + ":" + n.Text + "(" + string.Join(",", n.Children.Select(Shape)) + ")";
    string shape = string.Join(" ", markers.Where(child => child.Kind != "Mods").Select(Shape));   // the type and its markers
    if (declared.TryGetValue(name, out var first)) {
      if (first.Type != shape) {
        throw Error($"'{name}' is declared with two types in a statement that has an atEnd (spec 14.2)", decl);
      }
    } else {
      var hoisted = Synthetic("Decl", decl, name);
      foreach (var child in markers) {
        hoisted.Add(child);
      }
      if (weak) {
        hoisted.Add(Synthetic("WeakInit", decl, "default").Add(Synthetic("Null", decl)));   // null until the statement sets it (spec 12.7)
      }
      declared[name] = (hoisted, shape);
      outer.Add(hoisted);
    }
    if (target) {
      return Synthetic("Name", decl, name);   // a, d = f(): d is the variable declared before the statement
    }
    var init = decl.Children.FirstOrDefault(child => child.Kind is "Init" or "WeakInit");
    Node value;
    if (init is { Kind: "WeakInit", Text: "default" }) {
      value = Synthetic("Null", decl);
    } else if (init != null) {
      value = init.Children[0];
    } else if (IsClassTypeNode(typeNode)) {
      value = Synthetic("Null", decl);   // Dog d: null each time the declaration is reached
    } else {
      throw NotSupported($"'{name}' is used by the atEnd of its statement and declared without a value: give it one (14.2)", decl);
    }
    return Synthetic("Assign", decl, "=").Add(Synthetic("Name", decl, name)).Add(value);
  }

  bool IsClassTypeNode(Node typeNode) => typeNode is { Kind: "Type", Children.Count: 0, Text: string text } && (classes.ContainsKey(text) || traits.ContainsKey(text));
}
