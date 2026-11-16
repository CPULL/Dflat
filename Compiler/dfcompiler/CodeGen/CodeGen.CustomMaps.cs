using dfparser;

namespace dfcompiler;

// Custom maps (spec 6.7.2): a class of the program that implements MapCollection<Key, Value> writes the mandatory members (get_len, has, clear,
// hasKey, read_key, set_key, move_out, getIterator); the trait gives the others a default body written with them. The default bodies are
// Dflat code (MapDefaults) added to the class before it is registered, each only when the class does not write a member with that name and
// that number of parameters. The placeholder types XxKeyXx and XxValueXx become the type arguments of the class's MapCollection; the
// markers follow the parameters of its set_key (->Key k gives the key, a plain Key k lends it).
public sealed partial class CodeGen {
  const string MapDefaults = """
class XxMapDefaultsXx {
  // adds the pair, or replaces the value of an existing key: false when it replaced (spec 6.6)
  public fun bool add(KEYMARKXxKeyXx key, VALUEMARKXxValueXx value) {
    bool isNew = !hasKey(key)
    set_key(KEYMARKkey, VALUEMARKvalue)
    return isNew
  }

  // merges another map of the same class: the new value wins (spec 6.6); the entries are copied, other is unchanged
  public fun add(XxSelfXx other) {
    for (XxKeyXx key, XxValueXx value) in other {
      set_key(KEYGIVEkey, VALUEGIVEvalue)
    }
  }

  // .keys and .values: views of the trait built on getIterator() (spec 6.7.2)
  unrestricted fun customMapKeys<XxSelfXx, XxKeyXx, XxValueXx> get_keys() {
    return customMapKeys<XxSelfXx, XxKeyXx, XxValueXx>(this)
  }

  unrestricted fun customMapValues<XxSelfXx, XxKeyXx, XxValueXx> get_values() {
    return customMapValues<XxSelfXx, XxKeyXx, XxValueXx>(this)
  }

  public fun bool, XxValueXx tryGet(XxKeyXx key) {
    if !hasKey(key) {
      XxValueXx none
      return false, none
    }
    return true, read_key(key)
  }

  public fun bool, XxValueXx tryGet(XxKeyXx key, XxValueXx otherwise) {
    if !hasKey(key) {
      return false, otherwise
    }
    return true, read_key(key)
  }

  public fun removeAt(XxKeyXx key) {
    if hasKey(key) {
      move_out(key)
    }
  }

  // the values of two keys exchanged; when only one key exists its value moves to the other key (spec 6.6)
  public fun swap(XxKeyXx key1, XxKeyXx key2) {
    bool has1 = hasKey(key1)
    bool has2 = hasKey(key2)
    if has1 && has2 {
      XxValueXx first = move_out(key1)
      XxValueXx second = move_out(key2)
      set_key(KEYCOPY1, VALUEMARKsecond)
      set_key(KEYCOPY2, VALUEMARKfirst)
    } else if has1 {
      XxValueXx moved = move_out(key1)
      set_key(KEYCOPY2, VALUEMARKmoved)
    } else if has2 {
      XxValueXx moved = move_out(key2)
      set_key(KEYCOPY1, VALUEMARKmoved)
    }
  }

  // the key of the first entry, in the order of the iterator, that holds the value; the zero value when none does
  public fun XxKeyXx keyOf(XxValueXx value) {
    for (XxKeyXx key, XxValueXx item) in this {
      if item == value {
        return key
      }
    }
    XxKeyXx none
    return none
  }

  public fun bool remove(XxValueXx value) {
    vec<XxKeyXx> found = []
    for (XxKeyXx key, XxValueXx item) in this {
      if found.len == 0 && item == value {
        found.add(key)
      }
    }
    for XxKeyXx key in found {
      move_out(key)
    }
    return found.len > 0
  }

  public fun uns removeAll(XxValueXx value) {
    vec<XxKeyXx> found = []
    for (XxKeyXx key, XxValueXx item) in this {
      if item == value {
        found.add(key)
      }
    }
    for XxKeyXx key in found {
      move_out(key)
    }
    return found.len
  }
}
""";

  void AddMapDefaults(Node program) {
    foreach (var cls in program.Children.Where(child => child.Kind == "Class" && child.Line < LibraryLine).ToList()) {
      var mapBase = cls.Children.FirstOrDefault(child => child.Kind == "Bases")?.Children
        .FirstOrDefault(baseType => baseType.Text is "MapCollection" or "Root.MapCollection" && baseType.Children.Count == 2);
      if (mapBase == null) {
        continue;
      }
      var body = cls.Children.First(child => child.Kind == "Block");
      var written = body.Children.Where(member => member.Kind == "Fun").ToList();
      foreach (string required in new[] { "read_key", "set_key", "move_out" }) {
        if (!written.Any(member => member.Text == required)) {
          throw Error($"{cls.Text} implements MapCollection: it writes {required} (read_key(Key k) for c[k], set_key(Key k, Value value) for c[k] = v, "
            + "move_out(Key k) for ->c[k], spec 6.7.2)", cls);
        }
      }
      var setKey = written.First(member => member.Text == "set_key");
      var setParams = setKey.Children.First(child => child.Kind == "Params").Children;
      if (setParams.Count != 2) {
        throw Error("set_key takes the key and the value: set_key(Key k, Value value) (spec 6.7)", setKey);
      }
      bool giveKey = setParams[0].Kind == "GiftParam";
      bool giveValue = setParams[1].Kind == "GiftParam";
      string text = MapDefaults
        .Replace("KEYMARK", giveKey ? "->" : "")
        .Replace("VALUEMARK", giveValue ? "->" : "")
        .Replace("KEYCOPY1", giveKey ? "key1!" : "key1")
        .Replace("KEYCOPY2", giveKey ? "key2!" : "key2")
        .Replace("KEYGIVEkey", giveKey ? "key!" : "key")
        .Replace("VALUEGIVEvalue", giveValue ? "value!" : "value");
      var template = new Parser(new Lexer(text, LibraryLine).Tokenize()).ParseProgram().Children[0];
      foreach (var member in template.Children.First(child => child.Kind == "Block").Children.Where(member => member.Kind == "Fun")) {
        int count = ParamCount(member);
        if (written.Any(own => own.Text == member.Text && ParamCount(own) == count)) {
          continue;   // the class writes its own (spec 6.7.2)
        }
        ReplacePlaceholder(member, "XxKeyXx", mapBase.Children[0]);
        ReplacePlaceholder(member, "XxValueXx", mapBase.Children[1]);
        ReplacePlaceholder(member, "XxSelfXx", SelfType(cls));
        body.Add(member);
      }
    }
  }

  // the type of the class itself: Slots, or MyMap<K, V> for a generic class
  static Node SelfType(Node cls) {
    var type = Synthetic("Type", cls, cls.Text);
    foreach (var param in cls.Children.FirstOrDefault(child => child.Kind == "GenericParams")?.Children ?? new List<Node>()) {
      type.Add(Synthetic("Type", param, param.Text));
    }
    return type;
  }

  static int ParamCount(Node fun) => fun.Children.FirstOrDefault(child => child.Kind == "Params")?.Children.Count ?? 0;

  static void ReplacePlaceholder(Node node, string name, Node type) {
    for (int index = 0; index < node.Children.Count; index++) {
      var child = node.Children[index];
      if (child.Kind == "Type" && child.Text == name && child.Children.Count == 0) {
        node.Children[index] = CloneNode(type);
        continue;
      }
      ReplacePlaceholder(child, name, type);
    }
  }
}
