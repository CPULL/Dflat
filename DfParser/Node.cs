namespace dfparse;

public sealed class Node {
  public Node(string kind, Token at, string? text = null) {
    Kind = kind;
    Text = text;
    Line = at.Line;
  }

  public string Kind { get; set; }
  public string? Text { get; set; }
  public int Line { get; }
  public List<Node> Children { get; } = new();

  public Node Add(Node child) {
    Children.Add(child);
    return this;
  }

  public void Dump(TextWriter w, int indent) {
    w.Write(new string(' ', indent * 2));
    w.Write(Kind);
    if (Text != null) {
      w.Write(' ');
      w.Write(Text);
    }
    w.WriteLine();
    foreach (var c in Children) {
      c.Dump(w, indent + 1);
    }
  }
}
