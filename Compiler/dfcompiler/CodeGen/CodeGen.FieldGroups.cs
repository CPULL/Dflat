using dfparser;

namespace dfcompiler;

// Field groups (spec 11.1.4.1): pos { r32 x, r32 y } in a class is the fields "pos.x" and "pos.y". Before anything else the tree is
// lowered: the FieldGroup nodes become their Decl nodes, s.pos.x becomes the member "pos.x" of s (pos.x inside the class the name
// "pos.x"), s.pos = value becomes the multiple assignment s.pos.x, s.pos.y = value, and s.pos read as a value becomes the tuple
// (s.pos.x, s.pos.y).
public sealed partial class CodeGen {
  readonly Dictionary<string, List<string>?> groupFields = new();   // group name -> its fields; null when two classes disagree

  void LowerFieldGroups(Node program) {
    var ownGroups = new Dictionary<Node, Dictionary<string, List<string>>>();
    var memberNames = new Dictionary<string, string>();   // the plain fields, methods and parameters of every class -> the class
    foreach (var type in program.Children.Where(statement => statement.Kind is "Class" or "Trait")) {
      var body = type.Children.FirstOrDefault(child => child.Kind == "Block");
      if (body == null) {
        continue;
      }
      var groups = new Dictionary<string, List<string>>();
      for (int i = 0; i < body.Children.Count; i++) {
        var member = body.Children[i];
        if (member.Kind != "FieldGroup") {
          if (member.Kind is "Decl" or "Fun" && member.Text != null) {
            memberNames.TryAdd(member.Text, type.Text!);
          }
          continue;
        }
        if (type.Kind == "Trait") {
          throw NotSupported("Field groups in traits", member);
        }
        var mods = member.Children.FirstOrDefault(child => child.Kind == "Mods");
        if (mods?.Text?.Split(' ').Contains("static") == true) {
          throw NotSupported("Static field groups", member);
        }
        if (groups.ContainsKey(member.Text!)) {
          throw Error($"the field group '{member.Text}' is declared twice", member);
        }
        var decls = member.Children.Where(child => child.Kind == "Decl").ToList();
        var names = decls.Select(decl => decl.Text![(member.Text!.Length + 1)..]).ToList();
        if (names.Distinct().Count() != names.Count) {
          throw Error($"the field group '{member.Text}' has the same field twice", member);
        }
        foreach (var decl in decls) {
          if (mods != null) {
            decl.Children.Insert(0, Synthetic("Mods", mods, mods.Text));
          }
        }
        body.Children.RemoveAt(i);
        body.Children.InsertRange(i, decls);
        i += decls.Count - 1;
        groups[member.Text!] = names;
        groupFields[member.Text!] = groupFields.TryGetValue(member.Text!, out var other) && (other == null || !other.SequenceEqual(names)) ? null : names;
      }
      foreach (var p in type.Children.Where(child => child.Kind == "PrimaryCtor").SelectMany(ps => ps.Children)) {
        if (p.Text != null) {
          memberNames.TryAdd(p.Text, type.Text!);
        }
      }
      if (groups.Count > 0) {
        ownGroups[type] = groups;
      }
    }
    if (groupFields.Count == 0) {
      return;
    }
    // until the members are found through the types, a group name cannot also be a plain member of a class
    foreach (var (name, owner) in memberNames) {
      if (groupFields.ContainsKey(name)) {
        var at = ownGroups.Keys.First(type => ownGroups[type].ContainsKey(name));
        throw Error($"'{name}' is a field group and also a member of {owner}: a group name cannot be the name of another member yet", at);
      }
    }
    foreach (var statement in program.Children) {
      LowerGroups(statement, statement.Kind == "Class" && ownGroups.TryGetValue(statement, out var own) ? own : null);
    }
  }

  void LowerGroups(Node node, Dictionary<string, List<string>>? own) {
    if (node.Kind == "Assign" && node.Text == "=" && node.Children.Count == 2 && GroupOf(node.Children[0], own) is { } target) {
      LowerGroups(node.Children[1], own);
      AssignGroup(node, target.Receiver, target.Fields, own);
      return;
    }
    if (node.Kind is "Member" or "SafeMember" && node.Children.Count == 1) {
      var inner = node.Children[0];
      if (inner.Kind is "Member" or "SafeMember" && inner.Children.Count == 1 && groupFields.TryGetValue(inner.Text!, out var fields)
          && (fields == null || fields.Contains(node.Text!))) {
        // s.pos.x: the member "pos.x" of s
        node.Kind = inner.Kind;
        node.Text = inner.Text + "." + node.Text;
        node.Children[0] = inner.Children[0];
      } else if (inner.Kind == "Name" && own != null && own.TryGetValue(inner.Text!, out var ownFields) && ownFields.Contains(node.Text!)) {
        // pos.x inside the class: the field "pos.x"
        node.Kind = "Name";
        node.Text = inner.Text + "." + node.Text;
        node.Children.Clear();
        return;
      }
    }
    if (GroupOf(node, own) is { } read) {
      // the group as a value: a tuple of its fields
      var receiver = read.Receiver;
      string group = node.Text!;
      node.Kind = "Tuple";
      node.Text = null;
      node.Children.Clear();
      foreach (string field in read.Fields) {
        node.Children.Add(FieldOfGroup(receiver, group, field, node));
      }
    }
    foreach (var child in node.Children) {
      LowerGroups(child, own);
    }
  }

  // s.pos or, inside the class, pos: the receiver (null for the class's own) and the fields of the group
  (Node? Receiver, List<string> Fields)? GroupOf(Node node, Dictionary<string, List<string>>? own) {
    if (node.Kind == "Member" && node.Children.Count == 1 && groupFields.TryGetValue(node.Text!, out var fields)) {
      if (fields == null) {
        throw Error($"'{node.Text}' is a field group of several classes with different fields: use its fields one by one", node);
      }
      return (node.Children[0], fields);
    }
    if (node.Kind == "Name" && own != null && own.TryGetValue(node.Text!, out var ownFields)) {
      return (null, ownFields);
    }
    return null;
  }

  static Node FieldOfGroup(Node? receiver, string group, string field, Node at) {
    return receiver == null
      ? Synthetic("Name", at, group + "." + field)
      : Synthetic("Member", at, group + "." + field).Add(CopyTree(receiver));
  }

  // s.pos = (a, b) (also another group, already a tuple): s.pos.x, s.pos.y = a, b. A tuple variable p: s.pos.x, s.pos.y = p.x, p.y,
  // by the names of the fields, until tuples can be indexed
  void AssignGroup(Node assign, Node? receiver, List<string> fields, Dictionary<string, List<string>>? own) {
    string group = assign.Children[0].Text!;
    if (receiver != null && !IsSimpleReceiver(receiver)) {
      throw NotSupported("Assigning a whole field group through an expression (assign its fields, or use a variable)", assign);
    }
    var value = assign.Children[1];
    if (value.Kind == "Tuple" && value.Children.Count != fields.Count) {
      throw Error($"the field group '{group}' has {fields.Count} fields, the tuple {value.Children.Count} items", assign);
    }
    if (value.Kind != "Tuple" && !IsSimpleReceiver(value)) {
      throw NotSupported("Assigning to a field group the result of an expression (use a tuple literal or a variable)", assign);
    }
    var targets = Synthetic("Targets", assign);
    foreach (string field in fields) {
      targets.Add(FieldOfGroup(receiver, group, field, assign));
    }
    var values = Synthetic("Values", assign);
    values.Children.AddRange(value.Kind == "Tuple" ? value.Children : fields.Select(field => Synthetic("Member", value, field).Add(CopyTree(value))));
    assign.Kind = "MultiAssign";
    assign.Text = null;
    assign.Children.Clear();
    assign.Children.Add(targets);
    assign.Children.Add(values);
  }

  // a receiver that can be written again without running anything twice: a name, this, or members of them
  static bool IsSimpleReceiver(Node node) {
    return node.Kind == "Name" || node.Kind == "Member" && node.Children.Count == 1 && IsSimpleReceiver(node.Children[0]);
  }

  static Node CopyTree(Node node) {
    var copy = Synthetic(node.Kind, node, node.Text);
    foreach (var child in node.Children) {
      copy.Add(CopyTree(child));
    }
    return copy;
  }
}
