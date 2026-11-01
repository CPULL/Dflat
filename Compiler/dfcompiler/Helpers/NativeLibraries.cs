namespace dfcompiler;

// The C side that a library needs, given to clang: its C files, include folders and link libraries. Hardcoded for the gfx library
// until the libraries can describe it themselves (Compiler_Options.md "Native libraries"): its C files are the *.c of <library>/Src,
// and it needs the Vulkan SDK and GLFW. For each one a folder of the project file wins (vulkan-sdk=, glfw=), then the usual places;
// nothing found is an error.
static class NativeLibraries {
  public sealed class NativeArgs {
    public List<string> Args { get; } = new();   // -I, the C files, -L, -l: after the generated C
  }

  public sealed class NativeException : Exception {
    public NativeException(string message) : base(message) {
    }
  }

  public static NativeArgs Collect(IReadOnlyDictionary<string, string> imported, string os, string? vulkanSdk, string? glfw, string? projectFile) {
    var native = new NativeArgs();
    if (imported.TryGetValue("gfx", out var gfxFolder)) {
      AddGfx(native, gfxFolder, os, vulkanSdk, glfw, projectFile);
    }
    return native;
  }

  static void AddGfx(NativeArgs native, string folder, string os, string? vulkanSdk, string? glfw, string? projectFile) {
    if (os == "osx") {
      throw new NativeException("error: the gfx library is not available on macOS yet (it needs MoltenVK)");
    }
    string src = Path.Combine(folder, "Src");
    var cFiles = Directory.Exists(src) ? Directory.GetFiles(src, "*.c").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
    if (cFiles.Count == 0) {
      throw new NativeException($"error: the gfx library has no C files in {src}");
    }
    native.Args.Add("-I" + src);
    AddVulkan(native, os, vulkanSdk, projectFile);
    AddGlfw(native, os, glfw, projectFile);
    native.Args.AddRange(cFiles);
    if (os == "windows") {
      native.Args.AddRange(new[] { "-luser32", "-lgdi32", "-lshell32" });   // what GLFW uses
    } else {
      native.Args.AddRange(new[] { "-ldl", "-lpthread" });
    }
  }

  // the Vulkan SDK: vulkan-sdk= of the project, then VULKAN_SDK, then the newest C:\VulkanSDK\<version> (Windows) or the system (Linux)
  static void AddVulkan(NativeArgs native, string os, string? fromProject, string? projectFile) {
    var candidates = new List<string>();
    if (fromProject != null) {
      candidates.Add(fromProject);
    } else {
      if (Environment.GetEnvironmentVariable("VULKAN_SDK") is { Length: > 0 } env) {
        candidates.Add(env);
      }
      if (os == "windows" && Directory.Exists(@"C:\VulkanSDK")) {
        candidates.AddRange(Directory.GetDirectories(@"C:\VulkanSDK").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase));
      }
    }
    foreach (string sdk in candidates) {
      string include = FirstExisting(Path.Combine(sdk, "Include"), Path.Combine(sdk, "include"));
      string lib = FirstExisting(Path.Combine(sdk, "Lib"), Path.Combine(sdk, "lib"));
      if (File.Exists(Path.Combine(include, "vulkan", "vulkan.h")) && Directory.Exists(lib)) {
        native.Args.Add("-I" + include);
        native.Args.Add("-L" + lib);
        native.Args.Add(os == "windows" ? "-lvulkan-1" : "-lvulkan");
        return;
      }
    }
    if (fromProject == null && os == "linux" && File.Exists("/usr/include/vulkan/vulkan.h")) {
      native.Args.Add("-lvulkan");   // the system package (libvulkan-dev)
      return;
    }
    throw new NativeException(fromProject != null
      ? $"{projectFile}: error: vulkan-sdk={fromProject} is not a Vulkan SDK (no Include\\vulkan\\vulkan.h and Lib)"
      : "error: the gfx library needs the Vulkan SDK: install it (it sets VULKAN_SDK) or give its folder with vulkan-sdk=<folder> in the project file");
  }

  // GLFW: glfw= of the project, then GLFW_DIR, then the system (Linux). The Windows package has lib-vc<year> folders: the newest one is used
  static void AddGlfw(NativeArgs native, string os, string? fromProject, string? projectFile) {
    var candidates = new List<string>();
    if (fromProject != null) {
      candidates.Add(fromProject);
    } else if (Environment.GetEnvironmentVariable("GLFW_DIR") is { Length: > 0 } env) {
      candidates.Add(env);
    }
    foreach (string dir in candidates) {
      string include = Path.Combine(dir, "include");
      if (!File.Exists(Path.Combine(include, "GLFW", "glfw3.h"))) {
        continue;
      }
      var libDirs = Directory.GetDirectories(dir, "lib*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).ToList();
      string? libDir = os == "windows"
        ? libDirs.FirstOrDefault(d => Path.GetFileName(d).StartsWith("lib-vc", StringComparison.OrdinalIgnoreCase)
            && (File.Exists(Path.Combine(d, "glfw3_mt.lib")) || File.Exists(Path.Combine(d, "glfw3.lib"))))
        : libDirs.FirstOrDefault(d => File.Exists(Path.Combine(d, "libglfw3.a")) || File.Exists(Path.Combine(d, "libglfw.so")));
      if (libDir == null) {
        continue;
      }
      native.Args.Add("-I" + include);
      native.Args.Add("-L" + libDir);
      // clang links the static C runtime by default on Windows: glfw3_mt.lib is the GLFW built for it (when the package has it)
      native.Args.Add(os != "windows" ? (File.Exists(Path.Combine(libDir, "libglfw3.a")) ? "-lglfw3" : "-lglfw")
        : File.Exists(Path.Combine(libDir, "glfw3_mt.lib")) ? "-lglfw3_mt" : "-lglfw3");
      return;
    }
    if (fromProject == null && os == "linux" && File.Exists("/usr/include/GLFW/glfw3.h")) {
      native.Args.Add("-lglfw");   // the system package (libglfw3-dev)
      return;
    }
    throw new NativeException(fromProject != null
      ? $"{projectFile}: error: glfw={fromProject} is not a GLFW folder (no include\\GLFW\\glfw3.h and a lib folder)"
      : "error: the gfx library needs GLFW: set GLFW_DIR to its folder or give it with glfw=<folder> in the project file");
  }

  static string FirstExisting(string a, string b) => Directory.Exists(a) ? a : b;
}
