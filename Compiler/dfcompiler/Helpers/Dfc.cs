using System.Text;

namespace dfcompiler;

// The files of a compiled library (spec 15.8): the interface alone (.dfi, the binaries in <libpath>/<target>/) or one .dfc with the
// interface and the binary of each target inside:
//
//   DFC 1
//   interface <n>
//   <n bytes: the text of the .dfi>
//   targets <count>
//   binary <target> <file name> <n1> <sha-256 of the n1 bytes, 64 lowercase hex digits>
//   binary <target> <file name> <n2> <sha-256>
//   <n1 bytes><n2 bytes>
//
// The header lines end with \n. A build for one target keeps what the file holds for the other targets (their layout lines, their
// binaries) when the same compiler built it and the interface is the same apart from the layouts; otherwise they are dropped.
static class LibraryFiles {
  public sealed record Binary(string Target, string FileName, byte[] Data);

  public static (string Interface, List<Binary> Binaries) ReadDfc(string path) {
    var bytes = File.ReadAllBytes(path);
    int at = 0;
    string Line() {
      int end = Array.IndexOf(bytes, (byte)'\n', at);
      if (end < 0) {
        throw new InvalidDataException($"{path}: not a compiled library (.dfc): a header line is not ended");
      }
      string text = Encoding.UTF8.GetString(bytes, at, end - at);
      at = end + 1;
      return text;
    }
    byte[] Take(long count) {
      if (count < 0 || at + count > bytes.Length) {
        throw new InvalidDataException($"{path}: the compiled library (.dfc) is cut short");
      }
      var part = bytes.AsSpan(at, (int)count).ToArray();
      at += (int)count;
      return part;
    }
    long Number(string line, string key) {
      var parts = line.Split(' ');
      return parts.Length == 2 && parts[0] == key && long.TryParse(parts[1], out long value) ? value
        : throw new InvalidDataException($"{path}: not a compiled library (.dfc): '{key} <number>' expected, found '{line}'");
    }
    if (Line() != "DFC 1") {
      throw new InvalidDataException($"{path}: not a compiled library (.dfc): it starts with DFC 1");
    }
    string iface = Encoding.UTF8.GetString(Take(Number(Line(), "interface")));
    long count = Number(Line(), "targets");
    var table = new List<(string Target, string FileName, long Size, string Sha)>();
    for (long index = 0; index < count; index++) {
      var parts = Line().Split(' ');
      if (parts.Length != 5 || parts[0] != "binary" || !long.TryParse(parts[3], out long size) || parts[4].Length != 64) {
        throw new InvalidDataException($"{path}: not a compiled library (.dfc): 'binary <target> <file> <size> <sha-256>' expected");
      }
      table.Add((parts[1], parts[2], size, parts[4].ToLowerInvariant()));
    }
    var binaries = new List<Binary>();
    foreach (var entry in table) {
      var data = Take(entry.Size);
      // the checksum of each binary: a file damaged or changed after the build is refused (spec 15.8)
      if (Sha256(data) != entry.Sha) {
        throw new InvalidDataException($"{path}: the binary of {entry.Target} ({entry.FileName}) does not match its sha-256: the .dfc is damaged or was changed after the build");
      }
      binaries.Add(new Binary(entry.Target, entry.FileName, data));
    }
    return (iface, binaries);
  }

  public static void WriteDfc(string path, string iface, IEnumerable<Binary> binaries) {
    var list = binaries.OrderBy(binary => binary.Target, StringComparer.Ordinal).ToList();
    var data = Encoding.UTF8.GetBytes(iface);
    using var stream = File.Create(path);
    void Write(string text) => stream.Write(Encoding.UTF8.GetBytes(text));
    Write("DFC 1\n");
    Write($"interface {data.Length}\n");
    stream.Write(data);
    Write($"targets {list.Count}\n");
    foreach (var binary in list) {
      Write($"binary {binary.Target} {binary.FileName} {binary.Data.Length} {Sha256(binary.Data)}\n");
    }
    foreach (var binary in list) {
      stream.Write(binary.Data);
    }
  }

  static string Sha256(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

  static bool IsLayout(string line) => line.TrimStart().StartsWith("layout-");

  static string LayoutTarget(string line) {
    string text = line.TrimStart();
    return text["layout-".Length..text.IndexOf(':')];
  }

  // the interface just built for target, with the layout lines of the other targets of the old interface: null when they cannot be kept
  // (another compiler, or an interface that changed apart from the layouts); others: the targets the old interface had layouts for
  public static string? MergeInterface(string built, string old, string target, out HashSet<string> others) {
    var newLines = built.Replace("\r\n", "\n").Split('\n');
    var oldLines = old.Replace("\r\n", "\n").Split('\n');
    others = oldLines.Where(IsLayout).Select(LayoutTarget).Where(other => other != target).ToHashSet();
    if (!newLines.Where(line => !IsLayout(line)).SequenceEqual(oldLines.Where(line => !IsLayout(line)))) {
      return null;   // the compiler line is part of the text: another compiler is another interface
    }
    // the layouts of the other targets, by the header line of their class
    var kept = new Dictionary<string, List<string>>();
    string? header = null;
    foreach (string line in oldLines) {
      if (line.StartsWith("class ")) {
        header = line;
      } else if (IsLayout(line) && header != null && LayoutTarget(line) != target) {
        (kept.TryGetValue(header, out var list) ? list : kept[header] = new List<string>()).Add(line);
      }
    }
    var merged = new StringBuilder();
    header = null;
    for (int index = 0; index < newLines.Length; index++) {
      string line = newLines[index];
      if (line.StartsWith("class ")) {
        header = line;
      }
      // the layouts of a class in target order: the new one takes its place among the kept ones
      if (IsLayout(line) && header != null) {
        var layouts = (kept.GetValueOrDefault(header) ?? new List<string>()).Append(line).OrderBy(LayoutTarget, StringComparer.Ordinal);
        merged.Append(string.Join("\n", layouts));
      } else {
        merged.Append(line);
      }
      if (index < newLines.Length - 1) {
        merged.Append('\n');
      }
    }
    return merged.ToString();
  }
}
