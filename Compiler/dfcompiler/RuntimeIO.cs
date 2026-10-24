namespace dfcompiler;

// C runtime for Root.IO files and directories (spec 17.6), added to a program only when it uses the File class.
// Paths are the normalized form of 17.6.2 ("/d:/folder/file.txt"); they are converted to the OS form for every call.
// A function that fails leaves the id of the exception in df_io_err and returns its fallback value; the Dflat side of the
// File class reads it with df_io_error() after each call and throws (the silent ones only when a catch can reach them).
public static class RuntimeIO {
  public const string Source = """
/* ---- Root.IO files and directories (spec 17.6) ---- */

#include <errno.h>
#ifdef _WIN32
#include <io.h>
typedef wchar_t df_ochar;
#else
#include <dirent.h>
#include <sys/stat.h>
#include <unistd.h>
typedef char df_ochar;
#endif

/* exception ids of 17.6.6 */
#define DF_IO_NOT_FOUND 25
#define DF_IO_INVALID_ACCESS 26
#define DF_IO_FILE_ACCESS 27
#define DF_IO_DIR_NOT_EMPTY 28
#define DF_IO_NAME_EXISTS 29
#define DF_IO_INVALID_PATH 30
#define DF_IO_FULL_PATH_NOT_FOUND 31
#define DF_IO_ALREADY_EXISTS 33
#define DF_IO_NO_RESOURCES 38
#define DF_IO_GENERIC 43

static uint64_t df_io_err = 0;

static bool df_io_silent_id(uint64_t id) {
  return id == DF_IO_FILE_ACCESS || id == DF_IO_DIR_NOT_EMPTY || id == DF_IO_NAME_EXISTS || id == 32;
}

/* the first hard error wins over silent ones; among equal kinds the first one stays */
static void df_io_fail(uint64_t id) {
  if (df_io_err == 0 || (df_io_silent_id(df_io_err) && !df_io_silent_id(id))) {
    df_io_err = id;
  }
}

static uint64_t df_io_error(void) {
  uint64_t id = df_io_err;
  df_io_err = 0;
  return id;
}

/* ---- text encodings: 0 UTF8, 1 UTF16, 2 UTF32, 3 UTF16BE, 4 UTF32BE, 5 ASCII, 6 ISO88591 ---- */

typedef struct { char* p; uint64_t len; uint64_t cap; } df_io_buf;

static void df_io_buf_add(df_io_buf* b, const void* data, uint64_t n) {
  if (b->len + n > b->cap) {
    uint64_t cap = b->cap ? b->cap : 64;
    while (cap < b->len + n) cap *= 2;
    b->p = (char*)realloc(b->p, cap);
    if (!b->p) df_out_of_memory();
    b->cap = cap;
  }
  if (n) memcpy(b->p + b->len, data, n);
  b->len += n;
}

static void df_io_buf_utf8(df_io_buf* b, uint32_t cp) {
  char t[4];
  if (cp < 0x80) { t[0] = (char)cp; df_io_buf_add(b, t, 1); }
  else if (cp < 0x800) { t[0] = (char)(0xC0 | (cp >> 6)); t[1] = (char)(0x80 | (cp & 0x3F)); df_io_buf_add(b, t, 2); }
  else if (cp < 0x10000) { t[0] = (char)(0xE0 | (cp >> 12)); t[1] = (char)(0x80 | ((cp >> 6) & 0x3F)); t[2] = (char)(0x80 | (cp & 0x3F)); df_io_buf_add(b, t, 3); }
  else { t[0] = (char)(0xF0 | (cp >> 18)); t[1] = (char)(0x80 | ((cp >> 12) & 0x3F)); t[2] = (char)(0x80 | ((cp >> 6) & 0x3F)); t[3] = (char)(0x80 | (cp & 0x3F)); df_io_buf_add(b, t, 4); }
}

/* the next code point of UTF-8 text; a bad sequence gives U+FFFD */
static uint32_t df_io_next_cp(const unsigned char* s, uint64_t len, uint64_t* i) {
  unsigned char c = s[*i];
  uint32_t cp;
  int extra;
  if (c < 0x80) { (*i)++; return c; }
  if (c >= 0xF0 && c < 0xF8) { cp = c & 7; extra = 3; }
  else if (c >= 0xE0) { cp = c & 15; extra = 2; }
  else if (c >= 0xC0) { cp = c & 31; extra = 1; }
  else { (*i)++; return 0xFFFD; }
  if (*i + (uint64_t)extra >= len) { *i = len; return 0xFFFD; }
  for (int k = 1; k <= extra; k++) {
    if ((s[*i + k] & 0xC0) != 0x80) { *i += (uint64_t)k; return 0xFFFD; }
    cp = (cp << 6) | (s[*i + k] & 0x3F);
  }
  *i += (uint64_t)extra + 1;
  return cp;
}

static void df_io_emit(df_io_buf* b, uint32_t cp, int enc) {
  unsigned char t[4];
  switch (enc) {
    case 5: t[0] = cp < 0x80 ? (unsigned char)cp : '?'; df_io_buf_add(b, t, 1); break;
    case 6: t[0] = cp < 0x100 ? (unsigned char)cp : '?'; df_io_buf_add(b, t, 1); break;
    case 1: case 3: {
      uint32_t units[2]; int n = 1;
      if (cp >= 0x10000) { cp -= 0x10000; units[0] = 0xD800 + (cp >> 10); units[1] = 0xDC00 + (cp & 0x3FF); n = 2; } else { units[0] = cp; }
      for (int k = 0; k < n; k++) {
        if (enc == 1) { t[0] = (unsigned char)(units[k] & 0xFF); t[1] = (unsigned char)(units[k] >> 8); }
        else { t[0] = (unsigned char)(units[k] >> 8); t[1] = (unsigned char)(units[k] & 0xFF); }
        df_io_buf_add(b, t, 2);
      }
      break;
    }
    case 2: t[0] = (unsigned char)cp; t[1] = (unsigned char)(cp >> 8); t[2] = (unsigned char)(cp >> 16); t[3] = (unsigned char)(cp >> 24); df_io_buf_add(b, t, 4); break;
    case 4: t[3] = (unsigned char)cp; t[2] = (unsigned char)(cp >> 8); t[1] = (unsigned char)(cp >> 16); t[0] = (unsigned char)(cp >> 24); df_io_buf_add(b, t, 4); break;
    default: df_io_buf_utf8(b, cp); break;
  }
}

static df_arr_u8 df_io_encode(df_string text, int enc) {
  df_io_buf b = { NULL, 0, 0 };
  uint64_t i = 0;
  while (i < text.len) {
    df_io_emit(&b, df_io_next_cp((const unsigned char*)text.p, text.len, &i), enc);
  }
  df_arr_u8 out = { (uint8_t*)b.p, b.len };
  return out;
}

/* a byte order mark of the encoding is skipped */
static df_string df_io_decode(const uint8_t* data, uint64_t len, int enc) {
  df_io_buf b = { NULL, 0, 0 };
  uint64_t i = 0;
  switch (enc) {
    case 5: for (; i < len; i++) df_io_buf_utf8(&b, data[i] < 0x80 ? data[i] : 0xFFFD); break;
    case 6: for (; i < len; i++) df_io_buf_utf8(&b, data[i]); break;
    case 1: case 3:
      if (len >= 2 && ((enc == 1 && data[0] == 0xFF && data[1] == 0xFE) || (enc == 3 && data[0] == 0xFE && data[1] == 0xFF))) i = 2;
      for (; i + 1 < len; i += 2) {
        uint32_t u = enc == 1 ? (uint32_t)(data[i] | (data[i + 1] << 8)) : (uint32_t)((data[i] << 8) | data[i + 1]);
        if (u >= 0xD800 && u < 0xDC00 && i + 3 < len) {
          uint32_t v = enc == 1 ? (uint32_t)(data[i + 2] | (data[i + 3] << 8)) : (uint32_t)((data[i + 2] << 8) | data[i + 3]);
          if (v >= 0xDC00 && v < 0xE000) { df_io_buf_utf8(&b, 0x10000 + ((u - 0xD800) << 10) + (v - 0xDC00)); i += 2; continue; }
        }
        df_io_buf_utf8(&b, (u >= 0xD800 && u < 0xE000) ? 0xFFFD : u);
      }
      break;
    case 2: case 4:
      if (len >= 4 && ((enc == 2 && data[0] == 0xFF && data[1] == 0xFE && data[2] == 0 && data[3] == 0)
          || (enc == 4 && data[0] == 0 && data[1] == 0 && data[2] == 0xFE && data[3] == 0xFF))) i = 4;
      for (; i + 3 < len; i += 4) {
        uint32_t u = enc == 2 ? (uint32_t)data[i] | ((uint32_t)data[i + 1] << 8) | ((uint32_t)data[i + 2] << 16) | ((uint32_t)data[i + 3] << 24)
          : (uint32_t)data[i + 3] | ((uint32_t)data[i + 2] << 8) | ((uint32_t)data[i + 1] << 16) | ((uint32_t)data[i] << 24);
        df_io_buf_utf8(&b, (u > 0x10FFFF || (u >= 0xD800 && u < 0xE000)) ? 0xFFFD : u);
      }
      break;
    default:
      if (len >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) i = 3;
      while (i < len) {
        df_io_buf_utf8(&b, df_io_next_cp(data, len, &i));
      }
      break;
  }
  df_string s = { b.p ? b.p : "", b.len };
  return s;
}

/* ---- paths (17.6.2) ---- */

static df_string df_io_str(const char* p, uint64_t n) {
  char* copy = (char*)df_alloc(n);
  if (n) memcpy(copy, p, n);
  df_string s = { copy, n };
  return s;
}

/* "/" separator, a drive letter as the first folder ("/d:/x"), no repeated or trailing separators; illegal characters raise
   InvalidPathException (30) and the text is returned as it came */
static df_string df_io_normalize(df_string raw) {
  char* t = (char*)df_alloc(raw.len + 4);
  uint64_t n = 0;
  for (uint64_t i = 0; i < raw.len; i++) {
    unsigned char c = (unsigned char)raw.p[i];
    if (c == 0) { df_io_fail(DF_IO_INVALID_PATH); return raw; }
#ifdef _WIN32
    if (c == '\\') c = '/';
    if (c < 32 || c == '<' || c == '>' || c == '"' || c == '|' || c == '?' || c == '*') { df_io_fail(DF_IO_INVALID_PATH); return raw; }
#endif
    t[n++] = (char)c;
  }
#ifdef _WIN32
  /* "C:/x" and "/c:/x" both become "/c:/x" */
  uint64_t start = 0;
  char drive = 0;
  if (n >= 2 && t[1] == ':' && ((t[0] >= 'a' && t[0] <= 'z') || (t[0] >= 'A' && t[0] <= 'Z'))) { drive = t[0]; start = 2; }
  else if (n >= 3 && t[0] == '/' && t[2] == ':' && ((t[1] >= 'a' && t[1] <= 'z') || (t[1] >= 'A' && t[1] <= 'Z'))) { drive = t[1]; start = 3; }
  if (drive) {
    if (drive >= 'A' && drive <= 'Z') drive = (char)(drive + 32);
  }
  for (uint64_t i = start; i < n; i++) {
    if (t[i] == ':') { df_io_fail(DF_IO_INVALID_PATH); return raw; }
  }
#else
  uint64_t start = 0;
  char drive = 0;
#endif
  char* out = (char*)df_alloc(n + 4);
  uint64_t m = 0;
  if (drive) { out[m++] = '/'; out[m++] = drive; out[m++] = ':'; }
  bool lastSlash = false;
  for (uint64_t i = start; i < n; i++) {
    if (t[i] == '/') {
      if (lastSlash) continue;
      lastSlash = true;
    } else {
      lastSlash = false;
    }
    out[m++] = t[i];
  }
  if (m > 1 && out[m - 1] == '/') m--;
  free(t);
  df_string s = { out, m };
  return s;
}

/* the OS form: "/d:/x" is "d:/x"; the result is malloc'd */
static df_ochar* df_io_os(df_string path) {
  const char* p = path.p;
  uint64_t n = path.len;
#ifdef _WIN32
  char* fixed = (char*)malloc(n + 3);
  if (!fixed) df_out_of_memory();
  uint64_t m = 0;
  uint64_t i = 0;
  if (n >= 3 && p[0] == '/' && p[2] == ':') { i = 1; }
  for (; i < n; i++) fixed[m++] = p[i];
  if (m == 2 && fixed[1] == ':') fixed[m++] = '/';   /* a drive alone is its root folder */
  fixed[m] = 0;
  int count = MultiByteToWideChar(CP_UTF8, 0, fixed, -1, NULL, 0);
  wchar_t* wide = (wchar_t*)malloc((size_t)(count > 0 ? count : 1) * sizeof(wchar_t));
  if (!wide) df_out_of_memory();
  if (count > 0) MultiByteToWideChar(CP_UTF8, 0, fixed, -1, wide, count); else wide[0] = 0;
  free(fixed);
  return wide;
#else
  char* copy = (char*)malloc(n + 1);
  if (!copy) df_out_of_memory();
  memcpy(copy, p, n);
  copy[n] = 0;
  return copy;
#endif
}

/* an OS name (one folder or file name) as UTF-8 */
static df_string df_io_from_os(const df_ochar* name) {
#ifdef _WIN32
  int bytes = WideCharToMultiByte(CP_UTF8, 0, name, -1, NULL, 0, NULL, NULL);
  char* text = (char*)df_alloc((uint64_t)(bytes > 0 ? bytes : 1));
  if (bytes > 0) WideCharToMultiByte(CP_UTF8, 0, name, -1, text, bytes, NULL, NULL);
  df_string s = { text, (uint64_t)(bytes > 0 ? bytes - 1 : 0) };
  return s;
#else
  return df_io_str(name, strlen(name));
#endif
}

static df_string df_io_join(df_string dir, df_string name) {
  bool root = dir.len == 1 && dir.p[0] == '/';
  uint64_t n = dir.len + (root ? 0 : 1) + name.len;
  char* p = (char*)df_alloc(n);
  memcpy(p, dir.p, dir.len);
  uint64_t m = dir.len;
  if (!root) p[m++] = '/';
  memcpy(p + m, name.p, name.len);
  df_string s = { p, n };
  return s;
}

/* the part before the last separator; "" when there is none */
static df_string df_io_parent(df_string path) {
  for (uint64_t i = path.len; i > 0; i--) {
    if (path.p[i - 1] == '/') {
      df_string s = { path.p, i == 1 ? 1 : i - 1 };
      return s;
    }
  }
  df_string none = { "", 0 };
  return none;
}

/* File.name: the part after the last separator */
static df_string df_io_name(df_string path) {
  for (uint64_t i = path.len; i > 0; i--) {
    if (path.p[i - 1] == '/') {
      df_string s = { path.p + i, path.len - i };
      return s;
    }
  }
  return path;
}

/* File.extension: after the last dot of the name, without the dot; none for a name that starts with its only dot */
static df_string df_io_extension(df_string path) {
  df_string name = df_io_name(path);
  for (uint64_t i = name.len; i > 1; i--) {
    if (name.p[i - 1] == '.') {
      df_string s = { name.p + i, name.len - i };
      return s;
    }
  }
  df_string none = { "", 0 };
  return none;
}

/* File.parent: "" when the path is a root ("/" or a drive "/d:") or one of "." and ".."; "." for a bare relative name */
static df_string df_io_parent_path(df_string path) {
  df_string none = { "", 0 };
  if (path.len == 0 || (path.len == 1 && path.p[0] == '/') || (path.len == 3 && path.p[0] == '/' && path.p[2] == ':')) return none;
  if ((path.len == 1 && path.p[0] == '.') || (path.len == 2 && path.p[0] == '.' && path.p[1] == '.')) return none;
  df_string parent = df_io_parent(path);
  return parent.len == 0 ? df_str_lit(".", 1) : parent;
}

/* File.combinePaths: an absolute second path replaces the first (as C# Path.Combine) */
static df_string df_io_combine(df_string a, df_string b) {
  if (a.len == 0 || (b.len > 0 && b.p[0] == '/')) return b;
  if (b.len == 0) return a;
  return df_io_join(a, b);
}

/* ---- the operating system layer ---- */

typedef struct {
  int kind;            /* 0 missing, 1 file, 2 directory */
  uint64_t size;
  int64_t created, modified, accessed;
  bool readonly;
  bool denied;         /* exists but cannot be read */
} df_io_info;

/* the exception id for the last OS error */
static uint64_t df_io_os_code(void) {
#ifdef _WIN32
  switch (GetLastError()) {
    case ERROR_FILE_NOT_FOUND: return DF_IO_NOT_FOUND;
    case ERROR_PATH_NOT_FOUND: return DF_IO_FULL_PATH_NOT_FOUND;
    case ERROR_ACCESS_DENIED: case ERROR_WRITE_PROTECT: return DF_IO_INVALID_ACCESS;
    case ERROR_ALREADY_EXISTS: case ERROR_FILE_EXISTS: return DF_IO_NAME_EXISTS;
    case ERROR_SHARING_VIOLATION: case ERROR_LOCK_VIOLATION: return 36;
    case ERROR_DISK_FULL: case ERROR_HANDLE_DISK_FULL: case ERROR_TOO_MANY_OPEN_FILES: return DF_IO_NO_RESOURCES;
    case ERROR_INVALID_NAME: case ERROR_BAD_PATHNAME: return DF_IO_INVALID_PATH;
    default: return DF_IO_GENERIC;
  }
#else
  switch (errno) {
    case ENOENT: return DF_IO_NOT_FOUND;
    case ENOTDIR: return DF_IO_FULL_PATH_NOT_FOUND;
    case EACCES: case EPERM: case EROFS: return DF_IO_INVALID_ACCESS;
    case EEXIST: return DF_IO_NAME_EXISTS;
    case ENOSPC: case EMFILE: case ENFILE: case EDQUOT: return DF_IO_NO_RESOURCES;
    case ENAMETOOLONG: return DF_IO_INVALID_PATH;
    default: return DF_IO_GENERIC;
  }
#endif
}

#ifdef _WIN32
static int64_t df_io_filetime(FILETIME ft) {
  uint64_t t = ((uint64_t)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
  return t < 116444736000000000ULL ? 0 : (int64_t)((t - 116444736000000000ULL) / 10000ULL);
}
#endif

static df_io_info df_io_stat(const df_ochar* p) {
  df_io_info info = { 0, 0, INT64_MIN, INT64_MIN, INT64_MIN, false, false };
#ifdef _WIN32
  WIN32_FILE_ATTRIBUTE_DATA data;
  if (!GetFileAttributesExW(p, GetFileExInfoStandard, &data)) {
    DWORD e = GetLastError();
    info.denied = (e == ERROR_ACCESS_DENIED || e == ERROR_SHARING_VIOLATION);
    return info;
  }
  info.kind = (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ? 2 : 1;
  info.size = ((uint64_t)data.nFileSizeHigh << 32) | data.nFileSizeLow;
  info.created = df_io_filetime(data.ftCreationTime);
  info.modified = df_io_filetime(data.ftLastWriteTime);
  info.accessed = df_io_filetime(data.ftLastAccessTime);
  info.readonly = (data.dwFileAttributes & FILE_ATTRIBUTE_READONLY) != 0;
#else
  struct stat st;
  if (stat(p, &st) != 0) {
    info.denied = (errno == EACCES);
    return info;
  }
  info.kind = S_ISDIR(st.st_mode) ? 2 : 1;
  info.size = (uint64_t)st.st_size;
  info.created = (int64_t)st.st_ctime * 1000;
  info.modified = (int64_t)st.st_mtime * 1000;
  info.accessed = (int64_t)st.st_atime * 1000;
  info.readonly = (st.st_mode & S_IWUSR) == 0;
#endif
  return info;
}

typedef void (*df_io_visit)(df_string name, bool isDir, void* ctx);

/* calls visit for every entry of the directory (not "." and ".."); false if it cannot be read */
static bool df_io_os_list(const df_ochar* dir, df_io_visit visit, void* ctx) {
#ifdef _WIN32
  size_t n = wcslen(dir);
  wchar_t* pattern = (wchar_t*)malloc((n + 3) * sizeof(wchar_t));
  if (!pattern) df_out_of_memory();
  memcpy(pattern, dir, n * sizeof(wchar_t));
  if (n > 0 && pattern[n - 1] != L'/' && pattern[n - 1] != L'\\') pattern[n++] = L'/';
  pattern[n++] = L'*';
  pattern[n] = 0;
  WIN32_FIND_DATAW found;
  HANDLE handle = FindFirstFileW(pattern, &found);
  free(pattern);
  if (handle == INVALID_HANDLE_VALUE) {
    return GetLastError() == ERROR_FILE_NOT_FOUND;   /* an empty directory */
  }
  do {
    if (wcscmp(found.cFileName, L".") == 0 || wcscmp(found.cFileName, L"..") == 0) continue;
    visit(df_io_from_os(found.cFileName), (found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0, ctx);
  } while (FindNextFileW(handle, &found));
  FindClose(handle);
  return true;
#else
  DIR* d = opendir(dir);
  if (!d) return false;
  struct dirent* entry;
  size_t n = strlen(dir);
  while ((entry = readdir(d)) != NULL) {
    if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) continue;
    char* full = (char*)malloc(n + strlen(entry->d_name) + 2);
    if (!full) df_out_of_memory();
    strcpy(full, dir);
    if (n == 0 || full[n - 1] != '/') strcat(full, "/");
    strcat(full, entry->d_name);
    df_io_info info = df_io_stat(full);
    free(full);
    visit(df_io_from_os(entry->d_name), info.kind == 2, ctx);
  }
  closedir(d);
  return true;
#endif
}

static FILE* df_io_fopen(const df_ochar* p, const char* mode) {
#ifdef _WIN32
  wchar_t wide[8];
  size_t i = 0;
  for (; mode[i] && i < 7; i++) wide[i] = (wchar_t)mode[i];
  wide[i] = 0;
  return _wfopen(p, wide);
#else
  return fopen(p, mode);
#endif
}

static bool df_io_os_mkdir(const df_ochar* p) {
#ifdef _WIN32
  return CreateDirectoryW(p, NULL) != 0;
#else
  return mkdir(p, 0777) == 0;
#endif
}

static bool df_io_os_rmdir(const df_ochar* p) {
#ifdef _WIN32
  return RemoveDirectoryW(p) != 0;
#else
  return rmdir(p) == 0;
#endif
}

static bool df_io_os_unlink(const df_ochar* p, bool force) {
#ifdef _WIN32
  if (force) SetFileAttributesW(p, FILE_ATTRIBUTE_NORMAL);
  return DeleteFileW(p) != 0;
#else
  (void)force;
  return unlink(p) == 0;
#endif
}

static bool df_io_os_copyfile(const df_ochar* from, const df_ochar* to, bool replace) {
#ifdef _WIN32
  return CopyFileW(from, to, replace ? FALSE : TRUE) != 0;
#else
  FILE* in = fopen(from, "rb");
  if (!in) return false;
  FILE* out = fopen(to, replace ? "wb" : "wbx");
  if (!out) { fclose(in); return false; }
  char chunk[65536];
  size_t got;
  bool ok = true;
  while ((got = fread(chunk, 1, sizeof chunk, in)) > 0) {
    if (fwrite(chunk, 1, got, out) != got) { ok = false; break; }
  }
  fclose(in);
  if (fclose(out) != 0) ok = false;
  return ok;
#endif
}

static bool df_io_os_rename(const df_ochar* from, const df_ochar* to, bool replace) {
#ifdef _WIN32
  return MoveFileExW(from, to, MOVEFILE_COPY_ALLOWED | (replace ? MOVEFILE_REPLACE_EXISTING : 0)) != 0;
#else
  (void)replace;
  return rename(from, to) == 0;
#endif
}

/* ---- attributes ---- */

static int32_t df_io_kind(df_string path) {
  df_ochar* os = df_io_os(path);
  df_io_info info = df_io_stat(os);
  free(os);
  return info.kind;
}

static bool df_io_exists(df_string path) {
  return df_io_kind(path) != 0;
}

/* the attributes of an existing item; the access error (27) when it cannot be read; false when it is missing */
static bool df_io_attrs(df_string path, df_io_info* out) {
  df_ochar* os = df_io_os(path);
  *out = df_io_stat(os);
  free(os);
  if (out->denied) df_io_fail(DF_IO_FILE_ACCESS);
  return out->kind != 0;
}

static uint64_t df_io_size(df_string path) {
  df_io_info info;
  return df_io_attrs(path, &info) && info.kind == 1 ? info.size : 0;
}

static int64_t df_io_created(df_string path) {
  df_io_info info;
  return df_io_attrs(path, &info) ? info.created : INT64_MIN;
}

static int64_t df_io_modified(df_string path) {
  df_io_info info;
  return df_io_attrs(path, &info) ? info.modified : INT64_MIN;
}

static int64_t df_io_accessed(df_string path) {
  df_io_info info;
  return df_io_attrs(path, &info) ? info.accessed : INT64_MIN;
}

static bool df_io_readonly(df_string path) {
  df_io_info info;
  return df_io_attrs(path, &info) ? info.readonly : false;
}

/* ---- lists ---- */

typedef struct {
  df_string dir;       /* normalized path of the directory being listed */
  df_string match;
  bool recursive;
  int32_t itemType;    /* 0 files, 1 directories, 2 both */
  df_string* items;
  uint64_t count, cap;
} df_io_list_ctx;

/* only * and ? are special; case is ignored (ASCII) */
static bool df_io_match(const char* pat, uint64_t pn, const char* s, uint64_t sn) {
  if (pn == 0) return sn == 0;
  if (pat[0] == '*') {
    for (uint64_t k = 0; k <= sn; k++) {
      if (df_io_match(pat + 1, pn - 1, s + k, sn - k)) return true;
    }
    return false;
  }
  if (sn == 0) return false;
  char a = pat[0], b = s[0];
  if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
  if (b >= 'A' && b <= 'Z') b = (char)(b + 32);
  if (pat[0] != '?' && a != b) return false;
  return df_io_match(pat + 1, pn - 1, s + 1, sn - 1);
}

static void df_io_list_add(df_io_list_ctx* c, df_string item) {
  if (c->count == c->cap) {
    c->cap = c->cap ? c->cap * 2 : 16;
    c->items = (df_string*)realloc(c->items, c->cap * sizeof(df_string));
    if (!c->items) df_out_of_memory();
  }
  c->items[c->count++] = item;
}

static void df_io_list_dir(df_io_list_ctx* c, df_string dir);

static void df_io_list_visit(df_string name, bool isDir, void* ctx) {
  df_io_list_ctx* c = (df_io_list_ctx*)ctx;
  df_string full = df_io_join(c->dir, name);
  bool wanted = isDir ? c->itemType != 0 : (c->itemType != 1 && df_io_match(c->match.p, c->match.len, name.p, name.len));
  if (wanted) df_io_list_add(c, full);
  if (isDir && c->recursive) {
    df_string saved = c->dir;
    df_io_list_dir(c, full);
    c->dir = saved;
  }
}

static void df_io_list_dir(df_io_list_ctx* c, df_string dir) {
  c->dir = dir;
  df_ochar* os = df_io_os(dir);
  if (!df_io_os_list(os, df_io_list_visit, c)) df_io_fail(df_io_os_code());
  free(os);
}

/* the items of a directory as normalized paths (17.6.5): a missing path is FileNotFoundException */
static df_arr_string df_io_list(df_string path, bool recursive, int32_t itemType, df_string match) {
  df_arr_string result = { NULL, 0 };
  if (path.len == 0) { df_io_fail(DF_IO_INVALID_PATH); return result; }
  int32_t kind = df_io_kind(path);
  if (kind == 0) { df_io_fail(DF_IO_NOT_FOUND); return result; }
  if (kind == 1) { df_io_fail(DF_IO_INVALID_PATH); return result; }
  df_io_list_ctx c = { path, match, recursive, itemType, NULL, 0, 0 };
  df_io_list_dir(&c, path);
  result.p = c.items;
  result.len = c.count;
  return result;
}

/* ---- delete, copy, move, create ---- */

typedef struct { df_string dir; bool failed; int32_t flags; } df_io_del_ctx;
static bool df_io_delete_flags(df_string path, int32_t flags);

static void df_io_del_visit(df_string name, bool isDir, void* ctx) {
  df_io_del_ctx* c = (df_io_del_ctx*)ctx;
  (void)isDir;
  if (!df_io_delete_flags(df_io_join(c->dir, name), c->flags)) c->failed = true;
}

static void df_io_count_visit(df_string name, bool isDir, void* ctx) {
  (void)name; (void)isDir;
  (*(uint64_t*)ctx)++;
}

/* flags: 1 Recursive, 2 ForceDelete (17.6.5): a missing item is false; a directory that is not empty needs Recursive */
static bool df_io_delete_flags(df_string path, int32_t flags) {
  df_ochar* os = df_io_os(path);
  df_io_info info = df_io_stat(os);
  bool ok = false;
  if (info.kind == 1) {
    ok = df_io_os_unlink(os, (flags & 2) != 0);
    if (!ok) df_io_fail(DF_IO_FILE_ACCESS);
  } else if (info.kind == 2) {
    uint64_t children = 0;
    df_io_os_list(os, df_io_count_visit, &children);
    if (children > 0 && !(flags & 1)) {
      df_io_fail(DF_IO_DIR_NOT_EMPTY);
    } else {
      bool clean = true;
      if (children > 0) {
        df_io_del_ctx c = { path, false, flags };
        df_io_os_list(os, df_io_del_visit, &c);
        clean = !c.failed;
      }
      ok = clean && df_io_os_rmdir(os);
      if (clean && !ok) df_io_fail(DF_IO_FILE_ACCESS);
    }
  } else if (info.denied) {
    df_io_fail(DF_IO_FILE_ACCESS);
  }
  free(os);
  return ok;
}

static bool df_io_delete(df_string path, int32_t flags) {
  return df_io_delete_flags(path, flags);
}

static bool df_io_starts_inside(df_string path, df_string dir) {
  return path.len > dir.len && memcmp(path.p, dir.p, dir.len) == 0 && path.p[dir.len] == '/';
}

typedef struct { df_string from, to; bool overwrite; bool skipped; } df_io_copy_ctx;
static bool df_io_copy_item(df_string from, df_string to, bool overwrite, bool* skipped);

static void df_io_copy_visit(df_string name, bool isDir, void* ctx) {
  df_io_copy_ctx* c = (df_io_copy_ctx*)ctx;
  (void)isDir;
  bool skipped = false;
  df_io_copy_item(df_io_join(c->from, name), df_io_join(c->to, name), c->overwrite, &skipped);
}

/* a file is copied (or skipped with NameAlreadyExists); a directory is merged into the target with everything inside */
static bool df_io_copy_item(df_string from, df_string to, bool overwrite, bool* skipped) {
  df_ochar* src = df_io_os(from);
  df_ochar* dst = df_io_os(to);
  df_io_info a = df_io_stat(src);
  df_io_info b = df_io_stat(dst);
  bool ok = false;
  if (a.kind == 0) {
    df_io_fail(a.denied ? DF_IO_INVALID_ACCESS : DF_IO_NOT_FOUND);
  } else if (a.kind == 2) {
    if (df_io_starts_inside(to, from)) {
      df_io_fail(DF_IO_INVALID_ACCESS);   /* a folder inside itself */
    } else if (b.kind == 1) {
      df_io_fail(DF_IO_NAME_EXISTS);
      *skipped = true;
    } else {
      ok = true;
      if (b.kind == 0 && !df_io_os_mkdir(dst)) { df_io_fail(df_io_os_code()); ok = false; }
      if (ok) {
        df_io_copy_ctx c = { from, to, overwrite, false };
        df_io_os_list(src, df_io_copy_visit, &c);
      }
    }
  } else if (b.kind == 2) {
    df_io_fail(DF_IO_NAME_EXISTS);
    *skipped = true;
  } else if (b.kind == 1 && !overwrite) {
    df_io_fail(DF_IO_NAME_EXISTS);
    *skipped = true;
  } else {
    ok = df_io_os_copyfile(src, dst, true);
    if (!ok) df_io_fail(df_io_os_code());
  }
  free(src);
  free(dst);
  return ok;
}

/* true when the item was copied; false when it was skipped or failed (the id is in the error register) */
static bool df_io_copy(df_string from, df_string to, bool overwrite) {
  df_string parent = df_io_parent(to);
  if (parent.len > 0 && df_io_kind(parent) != 2) { df_io_fail(DF_IO_FULL_PATH_NOT_FOUND); return false; }
  bool skipped = false;
  bool ok = df_io_copy_item(from, to, overwrite, &skipped);
  return ok && !skipped;
}

static bool df_io_move(df_string from, df_string to, bool overwrite) {
  df_string parent = df_io_parent(to);
  if (parent.len > 0 && df_io_kind(parent) != 2) { df_io_fail(DF_IO_FULL_PATH_NOT_FOUND); return false; }
  int32_t a = df_io_kind(from);
  int32_t b = df_io_kind(to);
  if (a == 0) { df_io_fail(DF_IO_NOT_FOUND); return false; }
  if (a == 2 && df_io_starts_inside(to, from)) { df_io_fail(DF_IO_INVALID_ACCESS); return false; }
  if (a == 2 && b == 2) {
    /* two directories: merge, then remove the source when everything moved */
    uint64_t before = df_io_err;
    bool skipped = false;
    if (!df_io_copy_item(from, to, overwrite, &skipped)) return false;
    if (df_io_err != before) return true;   /* some files were skipped: the source keeps them */
    return df_io_delete_flags(from, 3);
  }
  if (b != 0 && (a != b || !overwrite)) { df_io_fail(DF_IO_NAME_EXISTS); return false; }
  df_ochar* src = df_io_os(from);
  df_ochar* dst = df_io_os(to);
  bool ok = df_io_os_rename(src, dst, overwrite);
  if (!ok) df_io_fail(df_io_os_code());
  free(src);
  free(dst);
  return ok;
}

/* 0 created; 1 it was there already (the silent FileNameAlreadyExistsException) or it could not be created */
static int32_t df_io_mkdir(df_string path) {
  int32_t kind = df_io_kind(path);
  if (kind != 0) { df_io_fail(DF_IO_NAME_EXISTS); return 1; }
  df_string parent = df_io_parent(path);
  if (parent.len > 0 && df_io_kind(parent) != 2) { df_io_fail(DF_IO_INVALID_PATH); return 1; }
  df_ochar* os = df_io_os(path);
  bool ok = df_io_os_mkdir(os);
  if (!ok) df_io_fail(df_io_os_code());
  free(os);
  return ok ? 0 : 1;
}

/* ---- temporary names ---- */

static df_string df_io_temp_dir(void) {
#ifdef _WIN32
  wchar_t buffer[MAX_PATH + 2];
  DWORD n = GetTempPathW(MAX_PATH + 1, buffer);
  if (n == 0 || n > MAX_PATH) return df_str_lit("/c:/temp", 8);
  buffer[n] = 0;
  df_string raw = df_io_from_os(buffer);
  return df_io_normalize(raw);
#else
  const char* dir = getenv("TMPDIR");
  if (!dir || !*dir) dir = "/tmp";
  return df_io_normalize(df_io_str(dir, strlen(dir)));
#endif
}

/* ---- current directory and full paths (stdlib 6.2) ---- */

static df_string df_io_current_dir(void) {
#ifdef _WIN32
  DWORD n = GetCurrentDirectoryW(0, NULL);
  wchar_t* buffer = (wchar_t*)malloc((size_t)(n ? n : 1) * sizeof(wchar_t));
  if (!buffer) df_out_of_memory();
  if (n == 0 || GetCurrentDirectoryW(n, buffer) == 0) { free(buffer); df_io_fail(df_io_os_code()); return df_str_lit("/", 1); }
  df_string raw = df_io_from_os(buffer);
  free(buffer);
#else
  char* buffer = getcwd(NULL, 0);
  if (!buffer) { df_io_fail(df_io_os_code()); return df_str_lit("/", 1); }
  df_string raw = df_io_str(buffer, strlen(buffer));
  free(buffer);
#endif
  return df_io_normalize(raw);
}

/* missing: FileNotFoundException, a file: InvalidPathException, no access: InvalidFileAccessException */
static bool df_io_set_current_dir(df_string path) {
  df_ochar* p = df_io_os(path);
  df_io_info info = df_io_stat(p);
  bool done = false;
  if (info.kind == 0) {
    df_io_fail(info.denied ? DF_IO_INVALID_ACCESS : DF_IO_NOT_FOUND);
  } else if (info.kind == 1) {
    df_io_fail(DF_IO_INVALID_PATH);
  } else {
#ifdef _WIN32
    done = SetCurrentDirectoryW(p) != 0;
#else
    done = chdir(p) == 0;
#endif
    if (!done) {
      uint64_t code = df_io_os_code();
      df_io_fail(code == DF_IO_GENERIC ? DF_IO_INVALID_ACCESS : code);
    }
  }
  free(p);
  return done;
}

/* the folders of a normalized path ("/d:/a/b" is "d:", "a", "b"); the array is malloc'd */
static df_string* df_io_segments(df_string path, uint64_t* count) {
  df_string* parts = (df_string*)malloc((size_t)(path.len / 2 + 2) * sizeof(df_string));
  if (!parts) df_out_of_memory();
  uint64_t n = 0;
  uint64_t start = 0;
  for (uint64_t i = 0; i <= path.len; i++) {
    if (i == path.len || path.p[i] == '/') {
      if (i > start) {
        parts[n].p = path.p + start;
        parts[n].len = i - start;
        n++;
      }
      start = i + 1;
    }
  }
  *count = n;
  return parts;
}

static df_string df_io_from_segments(df_string* parts, uint64_t count) {
  uint64_t n = 0;
  for (uint64_t i = 0; i < count; i++) n += parts[i].len + 1;
  if (n == 0) return df_str_lit("/", 1);
  char* p = (char*)df_alloc(n);
  uint64_t m = 0;
  for (uint64_t i = 0; i < count; i++) {
    p[m++] = '/';
    memcpy(p + m, parts[i].p, parts[i].len);
    m += parts[i].len;
  }
  df_string s = { p, m };
  return s;
}

static bool df_io_is_drive(df_string part) {
  return part.len == 2 && part.p[1] == ':';
}

/* absolute, without "." and ".."; a relative path starts from the current directory (on Windows "/x" is on its drive) */
static df_string df_io_full_path(df_string path) {
  df_string full = path;
  if (path.len == 0 || path.p[0] != '/') {
    full = path.len == 0 ? df_io_current_dir() : df_io_join(df_io_current_dir(), path);
  }
#ifdef _WIN32
  else if (!(path.len >= 3 && path.p[2] == ':')) {
    df_string cwd = df_io_current_dir();
    if (cwd.len >= 3 && cwd.p[2] == ':') {
      char* p = (char*)df_alloc(path.len + 3);
      memcpy(p, cwd.p, 3);
      memcpy(p + 3, path.p, path.len);
      full.p = p;
      full.len = path.len + 3;
    }
  }
#endif
  uint64_t count;
  df_string* parts = df_io_segments(full, &count);
  uint64_t kept = 0;
  for (uint64_t i = 0; i < count; i++) {
    df_string part = parts[i];
    if (part.len == 1 && part.p[0] == '.') continue;
    if (part.len == 2 && part.p[0] == '.' && part.p[1] == '.') {
      if (kept > 0 && !df_io_is_drive(parts[kept - 1])) kept--;   /* the root and a drive have no parent */
      continue;
    }
    parts[kept++] = part;
  }
  df_string result = df_io_from_segments(parts, kept);
  free(parts);
  return result;
}

static bool df_io_same_part(df_string a, df_string b) {
  if (a.len != b.len) return false;
  for (uint64_t i = 0; i < a.len; i++) {
    char x = a.p[i];
    char y = b.p[i];
#ifdef _WIN32
    if (x >= 'A' && x <= 'Z') x = (char)(x + 32);
    if (y >= 'A' && y <= 'Z') y = (char)(y + 32);
#endif
    if (x != y) return false;
  }
  return true;
}

/* from the directory "from" to "to": "../lib/x.df"; "." for the same place; the full "to" when the drives differ */
static df_string df_io_relative_path(df_string from, df_string to) {
  df_string fullFrom = df_io_full_path(from);
  df_string fullTo = df_io_full_path(to);
  uint64_t fromCount, toCount;
  df_string* fromParts = df_io_segments(fullFrom, &fromCount);
  df_string* toParts = df_io_segments(fullTo, &toCount);
  uint64_t common = 0;
  while (common < fromCount && common < toCount && df_io_same_part(fromParts[common], toParts[common])) common++;
  df_string result;
  if (common == 0 && fromCount > 0 && toCount > 0 && df_io_is_drive(fromParts[0]) && df_io_is_drive(toParts[0])) {
    result = fullTo;
  } else {
    uint64_t up = fromCount - common;
    uint64_t n = up * 3;
    for (uint64_t i = common; i < toCount; i++) n += toParts[i].len + 1;
    if (n == 0) {
      result = df_str_lit(".", 1);
    } else {
      char* p = (char*)df_alloc(n);
      uint64_t m = 0;
      for (uint64_t i = 0; i < up; i++) { memcpy(p + m, "../", 3); m += 3; }
      for (uint64_t i = common; i < toCount; i++) {
        memcpy(p + m, toParts[i].p, toParts[i].len);
        m += toParts[i].len;
        p[m++] = '/';
      }
      result.p = p;
      result.len = m - 1;     /* without the last separator */
    }
  }
  free(fromParts);
  free(toParts);
  return result;
}

static uint64_t df_io_temp_counter = 0;

static df_string df_io_temp_name(void) {
  uint64_t seed = df_time_clock() ^ ((uint64_t)df_time_now() << 20) ^ (++df_io_temp_counter * 0x9E3779B97F4A7C15ULL);
  char name[40];
  int n = snprintf(name, sizeof name, "df%016llx.tmp", (unsigned long long)seed);
  return df_io_join(df_io_temp_dir(), df_io_str(name, (uint64_t)n));
}

/* ---- whole file read and write ---- */

static df_arr_u8 df_io_read_all(df_string path) {
  df_arr_u8 result = { NULL, 0 };
  df_ochar* os = df_io_os(path);
  df_io_info info = df_io_stat(os);
  if (info.kind == 0) {
    df_io_fail(info.denied ? DF_IO_INVALID_ACCESS : DF_IO_NOT_FOUND);
    free(os);
    return result;
  }
  if (info.kind == 2) {
    df_io_fail(DF_IO_INVALID_ACCESS);
    free(os);
    return result;
  }
  FILE* f = df_io_fopen(os, "rb");
  free(os);
  if (!f) { df_io_fail(df_io_os_code() == DF_IO_NOT_FOUND ? DF_IO_NOT_FOUND : DF_IO_INVALID_ACCESS); return result; }
  df_io_buf b = { NULL, 0, 0 };
  char chunk[65536];
  size_t got;
  while ((got = fread(chunk, 1, sizeof chunk, f)) > 0) df_io_buf_add(&b, chunk, got);
  bool bad = ferror(f) != 0;
  fclose(f);
  if (bad) { free(b.p); df_io_fail(DF_IO_GENERIC); return result; }
  result.p = (uint8_t*)(b.p ? b.p : (char*)df_alloc(1));
  result.len = b.len;
  return result;
}

static df_string df_io_read_all_text(df_string path, int32_t encoding) {
  df_arr_u8 bytes = df_io_read_all(path);
  if (df_io_err != 0 && bytes.p == NULL) return df_str_lit("", 0);
  return df_io_decode(bytes.p, bytes.len, (int)encoding);
}

/* mode (17.6.4): 0 CreateOnly, 1 ReplaceOnly, 2 CreateReplace, 3 AppendOnly, 4 CreateAppend */
static bool df_io_write_all(df_string path, df_arr_u8 data, int32_t mode) {
  df_string parent = df_io_parent(path);
  if (parent.len > 0 && df_io_kind(parent) != 2) { df_io_fail(DF_IO_FULL_PATH_NOT_FOUND); return false; }
  df_ochar* os = df_io_os(path);
  df_io_info info = df_io_stat(os);
  bool bad = false;
  if (info.kind == 2) { df_io_fail(DF_IO_INVALID_ACCESS); bad = true; }
  else if (info.kind == 1 && mode == 0) { df_io_fail(DF_IO_ALREADY_EXISTS); bad = true; }
  else if (info.kind == 0 && (mode == 1 || mode == 3)) { df_io_fail(DF_IO_NOT_FOUND); bad = true; }
  if (bad) { free(os); return false; }
  FILE* f = df_io_fopen(os, (mode == 3 || mode == 4) ? "ab" : "wb");
  free(os);
  if (!f) { df_io_fail(df_io_os_code()); return false; }
  bool ok = data.len == 0 || fwrite(data.p, 1, data.len, f) == data.len;
  if (fclose(f) != 0) ok = false;
  if (!ok) df_io_fail(DF_IO_NO_RESOURCES);
  return ok;
}

static bool df_io_write_all_text(df_string path, df_string text, int32_t encoding, int32_t mode) {
  return df_io_write_all(path, df_io_encode(text, (int)encoding), mode);
}

/* ---- streams (6.8): a stream is a C file with the direction of its last operation ---- */

#ifdef _WIN32
#define DF_FSEEK _fseeki64
#define DF_FTELL _ftelli64
#else
#define DF_FSEEK fseeko
#define DF_FTELL ftello
#endif

typedef struct { FILE* f; int last; } df_io_stream;   /* last: 0 none, 1 read, 2 write */

/* C needs a seek between a read and a write on the same file */
static void df_io_direction(df_io_stream* s, int op) {
  if (s->last != 0 && s->last != op) DF_FSEEK(s->f, 0, SEEK_CUR);
  s->last = op;
}

/* access modes: 0 None, 1 Exclusive, 2 Shared. Open modes: 0 OpenExisting, 1 CreateOrOpen, 2 CreateIfMissing, 3 CreateOrTruncate,
   4 TruncateExisting, 5 CreateOrAppend, 6 AppendExisting. Sharing is not enforced yet (open item of the spec). Returns the handle, 0 on failure. */
static uint64_t df_io_stream_open(df_string path, int32_t readMode, int32_t writeMode, int32_t openMode) {
  bool read = readMode != 0, write = writeMode != 0;
  bool truncating = openMode == 3 || openMode == 4;
  bool appending = openMode == 5 || openMode == 6;
  if ((!read && !write) || (!write && (truncating || appending))) { df_io_fail(37); return 0; }
  df_string parent = df_io_parent(path);
  if (parent.len > 0 && df_io_kind(parent) != 2) { df_io_fail(DF_IO_FULL_PATH_NOT_FOUND); return 0; }
  df_ochar* os = df_io_os(path);
  df_io_info info = df_io_stat(os);
  bool mustExist = openMode == 0 || openMode == 4 || openMode == 6;
  const char* mode;
  if (info.kind == 2) { df_io_fail(34); free(os); return 0; }
  if (info.kind == 0 && mustExist) { df_io_fail(DF_IO_NOT_FOUND); free(os); return 0; }
  if (info.kind != 0 && openMode == 2) { df_io_fail(DF_IO_ALREADY_EXISTS); free(os); return 0; }
  if (info.kind == 1 && info.readonly && write) { df_io_fail(35); free(os); return 0; }
  if (truncating) mode = read ? "w+b" : "wb";
  else if (appending) mode = read ? "a+b" : "ab";
  else if (!write) {
    if (info.kind == 0) {   /* CreateOrOpen and CreateIfMissing of a missing file: an empty file */
      FILE* made = df_io_fopen(os, "wb");
      if (!made) { df_io_fail(df_io_os_code()); free(os); return 0; }
      fclose(made);
    }
    mode = "rb";
  } else mode = info.kind == 1 ? "r+b" : (read ? "w+b" : "wb");
  FILE* f = df_io_fopen(os, mode);
  free(os);
  if (!f) { df_io_fail(df_io_os_code()); return 0; }
  df_io_stream* s = (df_io_stream*)df_alloc(sizeof(df_io_stream));
  s->f = f;
  s->last = 0;
  return (uint64_t)(uintptr_t)s;
}

static df_io_stream* df_io_s(uint64_t handle) { return (df_io_stream*)(uintptr_t)handle; }

/* bytes read; -1 at the end of the stream */
static int64_t df_io_stream_read(uint64_t handle, df_arr_u8 buffer, uint64_t start, uint64_t length, uint64_t atLeast) {
  df_io_stream* s = df_io_s(handle);
  if (length == 0) return 0;
  df_io_direction(s, 1);
  uint64_t total = 0;
  while (total < length) {
    size_t got = fread(buffer.p + start + total, 1, (size_t)(length - total), s->f);
    if (got == 0) break;
    total += got;
    if (total >= atLeast) break;
  }
  if (ferror(s->f)) { clearerr(s->f); df_io_fail(DF_IO_GENERIC); return 0; }
  return total == 0 ? -1 : (int64_t)total;
}

static int64_t df_io_stream_write(uint64_t handle, df_arr_u8 buffer, uint64_t start, uint64_t length) {
  df_io_stream* s = df_io_s(handle);
  if (length == 0) return 0;
  df_io_direction(s, 2);
  size_t put = fwrite(buffer.p + start, 1, (size_t)length, s->f);
  if (put < length) { df_io_fail(errno == ENOSPC ? DF_IO_NO_RESOURCES : DF_IO_GENERIC); clearerr(s->f); }
  return (int64_t)put;
}

static int32_t df_io_stream_read_byte(uint64_t handle) {
  df_io_stream* s = df_io_s(handle);
  df_io_direction(s, 1);
  int c = fgetc(s->f);
  if (c == EOF) {
    if (ferror(s->f)) { clearerr(s->f); df_io_fail(DF_IO_GENERIC); return 0; }
    return -1;
  }
  return c;
}

static bool df_io_stream_write_byte(uint64_t handle, uint8_t value) {
  df_io_stream* s = df_io_s(handle);
  df_io_direction(s, 2);
  if (fputc(value, s->f) == EOF) { clearerr(s->f); df_io_fail(errno == ENOSPC ? DF_IO_NO_RESOURCES : DF_IO_GENERIC); return false; }
  return true;
}

static uint64_t df_io_stream_length(uint64_t handle) {
  df_io_stream* s = df_io_s(handle);
  int64_t current = DF_FTELL(s->f);
  DF_FSEEK(s->f, 0, SEEK_END);
  int64_t end = DF_FTELL(s->f);
  DF_FSEEK(s->f, current, SEEK_SET);
  s->last = 0;
  return end < 0 ? 0 : (uint64_t)end;
}

static uint64_t df_io_stream_position(uint64_t handle) {
  int64_t at = DF_FTELL(df_io_s(handle)->f);
  return at < 0 ? 0 : (uint64_t)at;
}

/* origin: 0 Begin, 1 CurrentPosition, 2 End. Before the start: the silent SeekBeforeBeginException and position 0. */
static uint64_t df_io_stream_seek(uint64_t handle, int64_t offset, int32_t origin) {
  df_io_stream* s = df_io_s(handle);
  int64_t base = origin == 0 ? 0 : origin == 1 ? DF_FTELL(s->f) : (int64_t)df_io_stream_length(handle);
  int64_t target = base + offset;
  if (target < 0) { df_io_fail(32); target = 0; }
  DF_FSEEK(s->f, target, SEEK_SET);
  s->last = 0;
  return (uint64_t)target;
}

static bool df_io_stream_flush(uint64_t handle, bool physical) {
  df_io_stream* s = df_io_s(handle);
  if (fflush(s->f) != 0) { df_io_fail(DF_IO_GENERIC); return false; }
  if (physical) {
#ifdef _WIN32
    if (!FlushFileBuffers((HANDLE)_get_osfhandle(_fileno(s->f)))) { df_io_fail(DF_IO_GENERIC); return false; }
#else
    if (fsync(fileno(s->f)) != 0) { df_io_fail(DF_IO_GENERIC); return false; }
#endif
  }
  return true;
}

static bool df_io_stream_close(uint64_t handle) {
  df_io_stream* s = df_io_s(handle);
  bool ok = fclose(s->f) == 0;
  free(s);
  if (!ok) df_io_fail(45);
  return ok;
}

""";
}
