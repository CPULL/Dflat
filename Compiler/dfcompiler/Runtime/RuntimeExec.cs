namespace dfcompiler;

// C runtime for Root.ExecProcess (stdlib 7): run, exec and getEnv. Added to a program only when it uses them (the "exec" part).
// A failure to start leaves the id of the exception in df_exec_err (25 not found, 26 cannot be run, 31 working directory missing,
// 43 the rest); the Dflat side (ExecPrelude) reads it with df_exec_error() or turns it into the error of the RunResult.
// run keeps its output, errors and timeout flag here until the Dflat side has read them.
public static class RuntimeExec {
  public const string Source = """
/* ---- Root.ExecProcess (stdlib 7) ---- */

#ifndef _WIN32
#include <sys/types.h>
#include <sys/wait.h>
#include <poll.h>
#include <fcntl.h>
#include <signal.h>
#include <unistd.h>
#endif

static uint64_t df_exec_err = 0;
static df_string df_exec_out = NULL;
static df_string df_exec_errout = NULL;
static bool df_exec_timed_out = false;

static uint64_t df_exec_error(void) {
  uint64_t id = df_exec_err;
  df_exec_err = 0;
  return id;
}

/* the caller owns what it gets: a copy */
static df_string df_exec_output(void) { return df_str_dup(df_exec_out); }
static df_string df_exec_errors(void) { return df_str_dup(df_exec_errout); }
static bool df_exec_timedout(void) { return df_exec_timed_out; }

/* the error of the RunResult: the exception of the last failure to start, or NULL */
static df_exception* df_exec_failure(df_string program, df_string workDir) {
  uint64_t id = df_exec_error();
  if (id == 0) {
    return NULL;
  }
  const df_descriptor* descriptor = id == 25 ? &df_desc_FileNotFoundException : id == 26 ? &df_desc_InvalidFileAccessException
    : id == 31 ? &df_desc_FullPathNotFoundException : &df_desc_GenericIOErrorException;
  const char* text = id == 25 ? "Program not found: " : id == 26 ? "The program cannot be run: "
    : id == 31 ? "The working directory does not exist: " : "The program cannot be started: ";
  df_sv about = df_view(id == 31 ? workDir : program);
  uint64_t length = strlen(text) + about.len;
  char* message = (char*)malloc(length + 1);
  if (!message) df_out_of_memory();
  memcpy(message, text, strlen(text));
  memcpy(message + strlen(text), about.p, about.len);
  df_exception* failure = df_new_exception(descriptor, message, length, "Root.ExecProcess.run");
  free(message);
  return failure;
}

typedef struct { char* p; uint64_t len; uint64_t cap; } df_exec_buf;

static void df_exec_add(df_exec_buf* b, const void* data, uint64_t n) {
  if (b->len + n > b->cap) {
    uint64_t cap = b->cap ? b->cap : 256;
    while (cap < b->len + n) cap *= 2;
    b->p = (char*)realloc(b->p, cap);
    if (!b->p) df_out_of_memory();
    b->cap = cap;
  }
  if (n) memcpy(b->p + b->len, data, n);
  b->len += n;
}

static df_string df_exec_text(df_exec_buf* b) {
  df_string s = df_str_make(b->p ? b->p : "", b->len);
  free(b->p);
  b->p = NULL;
  b->len = b->cap = 0;
  return s;
}

/* the timeout in milliseconds, -1 for none (a null lapse) */
static int64_t df_exec_timeout_ms(int64_t l) {
  int64_t value = l >> 3;
  if (l == 0) {
    return -1;
  }
  switch (l & 7) {
    case 2: return value < 0 ? 0 : value;
    case 3: return value < 0 ? 0 : (value + 9999) / 10000;
    case 4: return value <= 0 ? 0 : -1;     /* years: no limit in practice */
    default: return -1;                     /* the clock overflow */
  }
}

#ifdef _WIN32

/* a path in the Dflat form ("/d:/x", stdlib 6.2) is given to Windows as "d:/x"; a drive alone is its root folder */
static df_string df_exec_os_path(df_string text) {
  df_sv path = df_view(text);
  if (path.len >= 3 && path.p[0] == '/' && path.p[2] == ':') {
    path.p++;
    path.len--;
  }
  if (path.len == 2 && path.p[1] == ':') {
    char root[3] = { path.p[0], ':', '/' };
    return df_str_make(root, 3);
  }
  return df_str_make(path.p, path.len);
}

static wchar_t* df_exec_wide(const char* p, uint64_t n) {
  int count = n ? MultiByteToWideChar(CP_UTF8, 0, p, (int)n, NULL, 0) : 0;
  wchar_t* wide = (wchar_t*)malloc(((size_t)count + 1) * sizeof(wchar_t));
  if (!wide) df_out_of_memory();
  if (count) MultiByteToWideChar(CP_UTF8, 0, p, (int)n, wide, count);
  wide[count] = 0;
  return wide;
}

/* one argument as CommandLineToArgvW reads it back: quotes when needed, backslashes doubled before a quote */
static void df_exec_quote(df_exec_buf* b, df_string text) {
  df_sv arg = df_view(text);
  bool plain = arg.len > 0;
  for (uint64_t i = 0; i < arg.len && plain; i++) {
    char c = arg.p[i];
    plain = !(c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '"');
  }
  if (plain) {
    df_exec_add(b, arg.p, arg.len);
    return;
  }
  df_exec_add(b, "\"", 1);
  uint64_t slashes = 0;
  for (uint64_t i = 0; i < arg.len; i++) {
    char c = arg.p[i];
    if (c == '\\') {
      slashes++;
      continue;
    }
    for (uint64_t k = 0; k < (c == '"' ? slashes * 2 + 1 : slashes); k++) df_exec_add(b, "\\", 1);
    slashes = 0;
    df_exec_add(b, &c, 1);
  }
  for (uint64_t k = 0; k < slashes * 2; k++) df_exec_add(b, "\\", 1);
  df_exec_add(b, "\"", 1);
}

static wchar_t* df_exec_command_line(df_string program, df_arr_string args) {
  df_exec_buf b = { NULL, 0, 0 };
  df_exec_quote(&b, program);
  for (df_uns i = 0; i < args.len; i++) {
    df_exec_add(&b, " ", 1);
    df_exec_quote(&b, args.p[i]);
  }
  wchar_t* line = df_exec_wide(b.p, b.len);
  free(b.p);
  return line;
}

static uint64_t df_exec_os_code(DWORD e) {
  switch (e) {
    case ERROR_FILE_NOT_FOUND: case ERROR_PATH_NOT_FOUND: case ERROR_INVALID_NAME: return 25;
    case ERROR_ACCESS_DENIED: case ERROR_BAD_EXE_FORMAT: case ERROR_EXE_MACHINE_TYPE_MISMATCH: return 26;
    case ERROR_DIRECTORY: return 31;
    default: return 43;
  }
}

typedef struct { HANDLE pipe; df_exec_buf buf; } df_exec_reader;
typedef struct { HANDLE pipe; df_sv text; } df_exec_writer;

static DWORD WINAPI df_exec_read_all(LPVOID context) {
  df_exec_reader* r = (df_exec_reader*)context;
  char chunk[4096];
  DWORD got;
  while (ReadFile(r->pipe, chunk, sizeof chunk, &got, NULL) && got > 0) {
    df_exec_add(&r->buf, chunk, got);
  }
  return 0;
}

static DWORD WINAPI df_exec_write_all(LPVOID context) {
  df_exec_writer* w = (df_exec_writer*)context;
  uint64_t done = 0;
  while (done < w->text.len) {
    DWORD part = w->text.len - done > 65536 ? 65536 : (DWORD)(w->text.len - done);
    DWORD put;
    if (!WriteFile(w->pipe, w->text.p + done, part, &put, NULL)) break;
    done += put;
  }
  CloseHandle(w->pipe);
  return 0;
}

/* starts the program; false (and df_exec_err) when it cannot start */
static bool df_exec_start(df_string program, df_arr_string args, df_string workDir, STARTUPINFOW* start, PROCESS_INFORMATION* info) {
  wchar_t* line = df_exec_command_line(df_exec_os_path(program), args);
  df_sv osFolder = df_view(df_exec_os_path(workDir));
  wchar_t* folder = df_str_len(workDir) ? df_exec_wide(osFolder.p, osFolder.len) : NULL;
  BOOL started = CreateProcessW(NULL, line, NULL, NULL, TRUE, 0, NULL, folder, start, info);
  DWORD e = GetLastError();
  free(line);
  free(folder);
  if (!started) {
    df_exec_err = df_exec_os_code(e);
    return false;
  }
  CloseHandle(info->hThread);
  return true;
}

static int64_t df_exec_run(df_string program, df_arr_string args, df_string workDir, df_string input, int64_t timeout) {
  df_str_free(df_exec_out);
  df_str_free(df_exec_errout);
  df_exec_out = NULL;
  df_exec_errout = NULL;
  df_exec_timed_out = false;
  df_exec_err = 0;
  SECURITY_ATTRIBUTES inherit = { sizeof inherit, NULL, TRUE };
  HANDLE outRead, outWrite, errRead, errWrite, inRead, inWrite;
  if (!CreatePipe(&outRead, &outWrite, &inherit, 0)) { df_exec_err = 43; return -1; }
  if (!CreatePipe(&errRead, &errWrite, &inherit, 0)) { CloseHandle(outRead); CloseHandle(outWrite); df_exec_err = 43; return -1; }
  if (!CreatePipe(&inRead, &inWrite, &inherit, 0)) {
    CloseHandle(outRead); CloseHandle(outWrite); CloseHandle(errRead); CloseHandle(errWrite);
    df_exec_err = 43;
    return -1;
  }
  SetHandleInformation(outRead, HANDLE_FLAG_INHERIT, 0);
  SetHandleInformation(errRead, HANDLE_FLAG_INHERIT, 0);
  SetHandleInformation(inWrite, HANDLE_FLAG_INHERIT, 0);
  STARTUPINFOW start;
  memset(&start, 0, sizeof start);
  start.cb = sizeof start;
  start.dwFlags = STARTF_USESTDHANDLES;
  start.hStdInput = inRead;
  start.hStdOutput = outWrite;
  start.hStdError = errWrite;
  PROCESS_INFORMATION info;
  bool started = df_exec_start(program, args, workDir, &start, &info);
  CloseHandle(outWrite);
  CloseHandle(errWrite);
  CloseHandle(inRead);
  if (!started) {
    CloseHandle(outRead);
    CloseHandle(errRead);
    CloseHandle(inWrite);
    return -1;
  }
  df_exec_reader out = { outRead, { NULL, 0, 0 } };
  df_exec_reader err = { errRead, { NULL, 0, 0 } };
  df_exec_writer in = { inWrite, df_view(input) };
  HANDLE threads[3];
  threads[0] = CreateThread(NULL, 0, df_exec_read_all, &out, 0, NULL);
  threads[1] = CreateThread(NULL, 0, df_exec_read_all, &err, 0, NULL);
  threads[2] = CreateThread(NULL, 0, df_exec_write_all, &in, 0, NULL);
  int64_t ms = df_exec_timeout_ms(timeout);
  DWORD waited = WaitForSingleObject(info.hProcess, ms < 0 || ms >= INFINITE ? INFINITE : (DWORD)ms);
  if (waited == WAIT_TIMEOUT) {
    TerminateProcess(info.hProcess, 1);
    WaitForSingleObject(info.hProcess, INFINITE);
    df_exec_timed_out = true;
  }
  DWORD code = 0;
  GetExitCodeProcess(info.hProcess, &code);
  CloseHandle(info.hProcess);
  /* a program started by the child can keep the pipes open: the readers get a short time, then they are stopped */
  if (WaitForMultipleObjects(3, threads, TRUE, 2000) == WAIT_TIMEOUT) {
    for (int i = 0; i < 3; i++) CancelSynchronousIo(threads[i]);
    WaitForMultipleObjects(3, threads, TRUE, INFINITE);
  }
  for (int i = 0; i < 3; i++) CloseHandle(threads[i]);
  CloseHandle(outRead);
  CloseHandle(errRead);
  df_exec_out = df_exec_text(&out.buf);
  df_exec_errout = df_exec_text(&err.buf);
  return df_exec_timed_out ? -1 : (int64_t)(int32_t)code;
}

static int64_t df_exec_exec(df_string program, df_arr_string args, df_string workDir) {
  df_exec_err = 0;
  fflush(stdout);
  fflush(stderr);
  STARTUPINFOW start;
  memset(&start, 0, sizeof start);
  start.cb = sizeof start;
  PROCESS_INFORMATION info;
  if (!df_exec_start(program, args, workDir, &start, &info)) {
    return -1;
  }
  WaitForSingleObject(info.hProcess, INFINITE);
  DWORD code = 0;
  GetExitCodeProcess(info.hProcess, &code);
  CloseHandle(info.hProcess);
  return (int64_t)(int32_t)code;
}

static df_string df_exec_get_env(df_string text) {
  df_sv name = df_view(text);
  wchar_t* wide = df_exec_wide(name.p, name.len);
  DWORD n = GetEnvironmentVariableW(wide, NULL, 0);
  df_string value = NULL;
  if (n > 0) {
    wchar_t* buffer = (wchar_t*)malloc((size_t)n * sizeof(wchar_t));
    if (!buffer) df_out_of_memory();
    DWORD got = GetEnvironmentVariableW(wide, buffer, n);
    if (got > 0 && got < n) {
      int bytes = WideCharToMultiByte(CP_UTF8, 0, buffer, (int)got, NULL, 0, NULL, NULL);
      char* text = (char*)df_alloc((uint64_t)(bytes > 0 ? bytes : 1));
      if (bytes > 0) WideCharToMultiByte(CP_UTF8, 0, buffer, (int)got, text, bytes, NULL, NULL);
      value = df_str_make(text, (uint64_t)(bytes > 0 ? bytes : 0));
    }
    free(buffer);
  }
  free(wide);
  return value;
}

#else

static char* df_exec_cstr(df_string text) {
  df_sv s = df_view(text);
  char* p = (char*)malloc(s.len + 1);
  if (!p) df_out_of_memory();
  memcpy(p, s.p, s.len);
  p[s.len] = 0;
  return p;
}

static char** df_exec_argv(df_string program, df_arr_string args) {
  char** argv = (char**)malloc(((size_t)args.len + 2) * sizeof(char*));
  if (!argv) df_out_of_memory();
  argv[0] = df_exec_cstr(program);
  for (df_uns i = 0; i < args.len; i++) argv[i + 1] = df_exec_cstr(args.p[i]);
  argv[args.len + 1] = NULL;
  return argv;
}

static void df_exec_free_argv(char** argv) {
  for (char** a = argv; *a; a++) free(*a);
  free(argv);
}

/* the child after fork: the working directory, then the program; a failure is written to the report pipe as (stage, errno) */
static void df_exec_child(char** argv, const char* folder, int report) {
  int failure[2] = { 0, 0 };
  if (folder && chdir(folder) != 0) {
    failure[0] = 1;
    failure[1] = errno;
  } else {
    execvp(argv[0], argv);
    failure[0] = 2;
    failure[1] = errno;
  }
  ssize_t ignored = write(report, failure, sizeof failure);
  (void)ignored;
  _exit(127);
}

/* forks and starts the program; the child's input, output and error are the given descriptors (-1: the ones of this program).
   -1 (and df_exec_err) when it cannot start */
static pid_t df_exec_spawn(df_string program, df_arr_string args, df_string workDir, int in, int out, int err, int* closeInChild, int closeCount) {
  int report[2];
  if (pipe(report) != 0) {
    df_exec_err = 43;
    return -1;
  }
  fcntl(report[1], F_SETFD, FD_CLOEXEC);
  char** argv = df_exec_argv(program, args);
  char* folder = df_str_len(workDir) ? df_exec_cstr(workDir) : NULL;
  fflush(stdout);
  fflush(stderr);
  pid_t pid = fork();
  if (pid == 0) {
    close(report[0]);
    if (in >= 0) dup2(in, 0);
    if (out >= 0) dup2(out, 1);
    if (err >= 0) dup2(err, 2);
    for (int i = 0; i < closeCount; i++) close(closeInChild[i]);
    df_exec_child(argv, folder, report[1]);
  }
  close(report[1]);
  df_exec_free_argv(argv);
  free(folder);
  if (pid < 0) {
    close(report[0]);
    df_exec_err = 43;
    return -1;
  }
  int failure[2];
  ssize_t got = read(report[0], failure, sizeof failure);
  close(report[0]);
  if (got == (ssize_t)sizeof failure) {
    waitpid(pid, NULL, 0);
    int e = failure[1];
    df_exec_err = failure[0] == 1 ? (e == ENOENT || e == ENOTDIR ? 31 : e == EACCES ? 26 : 43)
      : (e == ENOENT || e == ENOTDIR ? 25 : e == EACCES || e == ENOEXEC || e == EPERM ? 26 : 43);
    return -1;
  }
  return pid;
}

static int64_t df_exec_status(int status) {
  if (WIFEXITED(status)) return WEXITSTATUS(status);
  if (WIFSIGNALED(status)) return 128 + WTERMSIG(status);
  return -1;
}

static int64_t df_exec_run(df_string program, df_arr_string args, df_string workDir, df_string inputText, int64_t timeout) {
  df_sv input = df_view(inputText);
  df_str_free(df_exec_out);
  df_str_free(df_exec_errout);
  df_exec_out = NULL;
  df_exec_errout = NULL;
  df_exec_timed_out = false;
  df_exec_err = 0;
  int outPipe[2], errPipe[2], inPipe[2];
  if (pipe(outPipe) != 0) { df_exec_err = 43; return -1; }
  if (pipe(errPipe) != 0) { close(outPipe[0]); close(outPipe[1]); df_exec_err = 43; return -1; }
  if (pipe(inPipe) != 0) { close(outPipe[0]); close(outPipe[1]); close(errPipe[0]); close(errPipe[1]); df_exec_err = 43; return -1; }
  int parentEnds[3] = { outPipe[0], errPipe[0], inPipe[1] };
  pid_t pid = df_exec_spawn(program, args, workDir, inPipe[0], outPipe[1], errPipe[1], parentEnds, 3);
  close(outPipe[1]);
  close(errPipe[1]);
  close(inPipe[0]);
  if (pid < 0) {
    close(outPipe[0]);
    close(errPipe[0]);
    close(inPipe[1]);
    return -1;
  }
  void (*oldPipe)(int) = signal(SIGPIPE, SIG_IGN);     /* a child that does not read its input must not stop this program */
  df_exec_buf out = { NULL, 0, 0 };
  df_exec_buf err = { NULL, 0, 0 };
  int outFd = outPipe[0], errFd = errPipe[0], inFd = inPipe[1];
  uint64_t written = 0;
  if (input.len == 0) {
    close(inFd);
    inFd = -1;
  }
  int64_t ms = df_exec_timeout_ms(timeout);
  uint64_t deadline = ms < 0 ? 0 : df_time_clock() + (uint64_t)ms * 10000000ULL;
  char chunk[4096];
  while (outFd >= 0 || errFd >= 0) {
    struct pollfd watch[3];
    int count = 0;
    if (outFd >= 0) { watch[count].fd = outFd; watch[count].events = POLLIN; count++; }
    if (errFd >= 0) { watch[count].fd = errFd; watch[count].events = POLLIN; count++; }
    if (inFd >= 0) { watch[count].fd = inFd; watch[count].events = POLLOUT; count++; }
    int wait = -1;
    if (ms >= 0) {
      uint64_t now = df_time_clock();
      if (now >= deadline) {
        df_exec_timed_out = true;
        break;
      }
      uint64_t left = (deadline - now + 9999999ULL) / 10000000ULL;
      wait = left > 1000000 ? 1000000 : (int)left;
    }
    int ready = poll(watch, (nfds_t)count, wait);
    if (ready < 0 && errno != EINTR) break;
    for (int i = 0; i < count && ready > 0; i++) {
      if (watch[i].revents == 0) continue;
      if (watch[i].fd == inFd) {
        size_t part = input.len - written > 4096 ? 4096 : (size_t)(input.len - written);
        ssize_t put = write(inFd, input.p + written, part);
        if (put > 0) written += (uint64_t)put;
        if (put <= 0 || written >= input.len) { close(inFd); inFd = -1; }
        continue;
      }
      ssize_t got = read(watch[i].fd, chunk, sizeof chunk);
      if (got > 0) {
        df_exec_add(watch[i].fd == outFd ? &out : &err, chunk, (uint64_t)got);
      } else if (got == 0 || errno != EINTR) {
        close(watch[i].fd);
        if (watch[i].fd == outFd) outFd = -1; else errFd = -1;
      }
    }
  }
  if (df_exec_timed_out) {
    kill(pid, SIGKILL);
  }
  int status = 0;
  while (waitpid(pid, &status, 0) < 0 && errno == EINTR) {
  }
  if (outFd >= 0) close(outFd);
  if (errFd >= 0) close(errFd);
  if (inFd >= 0) close(inFd);
  signal(SIGPIPE, oldPipe);
  df_exec_out = df_exec_text(&out);
  df_exec_errout = df_exec_text(&err);
  return df_exec_timed_out ? -1 : df_exec_status(status);
}

static int64_t df_exec_exec(df_string program, df_arr_string args, df_string workDir) {
  df_exec_err = 0;
  pid_t pid = df_exec_spawn(program, args, workDir, -1, -1, -1, NULL, 0);
  if (pid < 0) {
    return -1;
  }
  int status = 0;
  while (waitpid(pid, &status, 0) < 0 && errno == EINTR) {
  }
  return df_exec_status(status);
}

static df_string df_exec_get_env(df_string name) {
  char* key = df_exec_cstr(name);
  const char* value = getenv(key);
  free(key);
  if (!value) {
    return df_str_lit("", 0);
  }
  return df_str_make(value, strlen(value));
}

#endif
""";
}
