namespace dfcompiler;

// Root.ExecProcess (stdlib 7): run and exec are static functions of the class ExecProcess written in Dflat, added to the program when it
// mentions ExecProcess or RunResult (AddPrelude); Root.ExecProcess.run is sent to ExecProcess.run (LibraryStaticPath). The natives of
// Root.ExecProcess.Native (RuntimeExec) leave the id of a failure to start in a register: run puts it in the RunResult as an exception,
// exec throws it. The fields of RunResult are read-only properties (get_ functions).
public sealed partial class CodeGen {
  const string ExecPrelude = """
public class RunResult(int code, string outputText, string errorText, bool stopped, Exception failure) {
  public fun int get_exitCode() { code }
  public fun string get_output() { outputText }
  public fun string get_errors() { errorText }
  public fun bool get_timedOut() { stopped }
  public fun Exception get_error() { failure }
}

public class ExecProcess {
  public static fun RunResult run(string program, arr<string> args = [], string workDir = "", string input = "", lapse timeout = null) {
    int code = (int)Root.ExecProcess.Native.Run(program, args, workDir, input, timeout)
    Exception failure = Root.ExecProcess.Native.Failure(program, workDir)
    return RunResult(code, Root.ExecProcess.Native.Output(), Root.ExecProcess.Native.Errors(), Root.ExecProcess.Native.TimedOut(), failure)
  }

  public static fun int exec(string program, arr<string> args = [], string workDir = "") {
    int code = (int)Root.ExecProcess.Native.Exec(program, args, workDir)
    u64 failure = Root.ExecProcess.Native.Error()
    if failure == 25 then throw FileNotFoundException("Program not found: " + program)
    if failure == 26 then throw InvalidFileAccessException("The program cannot be run: " + program)
    if failure == 31 then throw FullPathNotFoundException("The working directory does not exist: " + workDir)
    if failure != 0 then throw GenericIOErrorException("The program cannot be started: " + program)
    return code
  }
}
""";
}
