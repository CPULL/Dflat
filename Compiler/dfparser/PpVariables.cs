namespace dfparser;

// The variables the compiler gives to the preprocessor (spec 19.1, Compiler_Options.md). The target is one of win-x64, win-x32, linux-x64,
// linux-arm64, osx-x64, osx-arm64.
public static class PpVariables {
  public static readonly string[] Os = { "windows", "linux", "osx" };
  public static readonly string[] Cpu = { "x86", "x64", "arm" };

  public static Dictionary<string, PpVariable> Build(string target, string build, int optLevel, string optMode, string languageVersion) {
    string[] parts = target.Split('-');
    string os = parts[0] switch { "win" => "windows", "osx" => "osx", _ => "linux" };
    string architecture = parts.Length > 1 ? parts[1] : "x64";
    string cpu = architecture switch { "x32" => "x86", "arm64" or "arm" => "arm", _ => "x64" };
    int bits = architecture is "x64" or "arm64" ? 64 : 32;
    string endian = cpu is "x86" or "x64" or "arm" ? "little" : "big";   // 68k and other cpus would be big endian
    var arch = Os.SelectMany(o => Cpu.Select(c => o + "_" + c)).ToArray();
    var versionParts = languageVersion.Split('.').Select(part => long.TryParse(part, out long number) ? number : 0).ToArray();
    long Part(int index) => index < versionParts.Length ? versionParts[index] : 0;

    PpVariable Word(string value, params string[] words) => new(PpValue.OfWord(value), words);
    PpVariable Number(long value) => new(PpValue.OfNumber(value));

    return new Dictionary<string, PpVariable> {
      ["os"] = Word(os, Os),
      ["cpu"] = Word(cpu, Cpu),
      ["arch"] = Word(os + "_" + cpu, arch),
      ["bits"] = Number(bits),
      ["int"] = Number(32),
      ["uns"] = Number(32),
      ["real"] = Number(64),
      ["endian"] = Word(endian, "little", "big"),
      ["build"] = Word(build, "debug", "release"),
      ["exceptions"] = Word("on", "on", "off"),
      ["ram"] = Number(4L * 1024 * 1024 * 1024),
      ["version"] = new PpVariable(PpValue.OfWord(languageVersion)),
      ["version.major"] = Number(Part(0)),
      ["version.minor"] = Number(Part(1)),
      ["version.build"] = Number(Part(2)),
      ["coptlev"] = Number(optLevel),
      ["coptmode"] = Word(optMode, "size", "speed")
    };
  }
}
