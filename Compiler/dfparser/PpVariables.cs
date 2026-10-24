namespace dfparser;

// a cpu of spec 19.1: its size in bits (the native size of int and uns), its endianness, and whether the compiler builds for it
public sealed record CpuInfo(string Name, int Bits, string Endian, bool Built);

// The variables the compiler gives to the preprocessor (spec 19.1, Compiler_Options.md), from the os, the cpu and the native size of int.
public static class PpVariables {
  public static readonly string[] Os = { "windows", "linux", "osx" };

  // the cpus of spec 19.1; arm64_32 is a 64-bit cpu with 32-bit addresses
  public static readonly CpuInfo[] Cpus = {
    new("x86", 32, "little", true),
    new("x64", 64, "little", true),
    new("arm32", 32, "little", false),
    new("armeb", 32, "big", false),
    new("arm64", 64, "little", true),
    new("arm64be", 64, "big", false),
    new("arm64_32", 64, "little", false),
    new("riscv32", 32, "little", false),
    new("riscv64", 64, "little", false),
    new("ppc32", 32, "big", false),
    new("ppc64", 64, "big", false),
    new("ppc64le", 64, "little", false),
    new("mips", 32, "big", false),
    new("mipsel", 32, "little", false),
    new("mips64", 64, "big", false),
    new("mips64el", 64, "little", false),
    new("sparc", 32, "big", false),
    new("sparcel", 32, "little", false),
    new("sparcv9", 64, "big", false),
    new("avr", 8, "little", false),
    new("m68k", 32, "big", false)
  };

  public static CpuInfo? FindCpu(string name) => Cpus.FirstOrDefault(cpu => cpu.Name == name);

  public static Dictionary<string, PpVariable> Build(string os, string cpu, int intBits, string build, int optLevel, string optMode, string languageVersion) {
    var info = FindCpu(cpu) ?? throw new ArgumentException($"unknown cpu {cpu}");
    string[] cpuNames = Cpus.Select(c => c.Name).ToArray();
    var arch = Os.SelectMany(o => cpuNames.Select(c => o + "_" + c)).ToArray();
    var versionParts = languageVersion.Split('.').Select(part => long.TryParse(part, out long number) ? number : 0).ToArray();
    long Part(int index) => index < versionParts.Length ? versionParts[index] : 0;

    PpVariable Word(string value, params string[] words) => new(PpValue.OfWord(value), words);
    PpVariable Number(long value) => new(PpValue.OfNumber(value));

    return new Dictionary<string, PpVariable> {
      ["os"] = Word(os, Os),
      ["cpu"] = Word(cpu, cpuNames),
      ["arch"] = Word(os + "_" + cpu, arch),
      ["bits"] = Number(info.Bits),
      ["int"] = Number(intBits),
      ["uns"] = Number(intBits),
      ["real"] = Number(64),
      ["endian"] = Word(info.Endian, "little", "big"),
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
