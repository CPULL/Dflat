namespace dfcompiler;

// Stream and FileStream (spec 6.8): Dflat code over the stream natives of RuntimeIO, added by AddPrelude when the program uses
// streams (it also adds File, which the streams use for its exception check). The properties are get_ functions (MemberValue).
// A length of 18446744073709551615 stands for "the rest of the buffer". Exclusive and Shared are not enforced (open item of the spec).
public sealed partial class CodeGen {
  // File.open (6.3.2, 6.3.3): shortcuts for the FileStream constructor
  const string IoOpenStatic = """
  public static fun FileStream open(string path, Root.IO.StreamReadWriteAccessMode readMode, Root.IO.StreamReadWriteAccessMode writeMode, Root.IO.StreamOpenMode openMode) {
    return FileStream(path, readMode, writeMode, openMode)
  }
""";

  const string IoOpenObject = """
  public fun FileStream open(Root.IO.StreamReadWriteAccessMode readMode, Root.IO.StreamReadWriteAccessMode writeMode, Root.IO.StreamOpenMode openMode) {
    return FileStream(full, readMode, writeMode, openMode)
  }
""";

  const string IoStreamPrelude = """
public class Stream {
  // the modes the stream was opened with (read-only properties: s.readMode, s.writeMode, s.openMode)
  public fun Root.IO.StreamReadWriteAccessMode get_readMode() { Root.IO.StreamReadWriteAccessMode.None }
  public fun Root.IO.StreamReadWriteAccessMode get_writeMode() { Root.IO.StreamReadWriteAccessMode.None }
  public fun Root.IO.StreamOpenMode get_openMode() { Root.IO.StreamOpenMode.OpenExisting }
  public fun bool get_canRead() { false }
  public fun bool get_canWrite() { false }
  public fun bool get_canSeek() { false }
  public fun bool get_hasLength() { false }
  public fun u64 get_position() { 0 }
  public fun u64 get_length() { 0 }
  public fun u64? read(arr<u8> buffer, u64 start = 0, u64 length = 18446744073709551615ul, u64 readAtLeast = 0) {
    throw StreamModeNotAllowedException("This stream cannot be read")
  }
  public fun u64 write(arr<u8> buffer, u64 start = 0, u64 length = 18446744073709551615ul, u64 writeAtLeast = 0) {
    throw StreamModeNotAllowedException("This stream cannot be written")
  }
  public fun u8? readByte() {
    throw StreamModeNotAllowedException("This stream cannot be read")
  }
  public fun bool writeByte(u8 value) {
    throw StreamModeNotAllowedException("This stream cannot be written")
  }
  public fun u64 seek(i64 position, Root.IO.StreamSeekOrigin origin) {
    throw SeekNotSupportedByStreamException("This stream cannot seek")
  }
  public fun flush(bool forcePhysical = false) { }
  public fun close() { }
}

public class FileStream(string path, Root.IO.StreamReadWriteAccessMode readMode, Root.IO.StreamReadWriteAccessMode writeMode, Root.IO.StreamOpenMode openMode) : Stream {
  u64 handle = FileStream.openNative(path, readMode, writeMode, openMode)

  static fun u64 openNative(string path, Root.IO.StreamReadWriteAccessMode readMode, Root.IO.StreamReadWriteAccessMode writeMode, Root.IO.StreamOpenMode openMode) {
    string p = File.fixPath(path)
    u64 opened = Root.IO.Native.StreamOpen(p, (i32)readMode, (i32)writeMode, (i32)openMode)
    File.check(p)
    return opened
  }

  fun requireOpen() {
    if handle == 0 then throw AccessToAClosedStreamException("The stream is closed")
  }

  // the number of bytes of a read or write: the checks of 6.8.4
  fun u64 span(u64 total, u64 start, u64 length, u64 atLeast) {
    if start > total then throw InvalidBufferRangeException("start is past the end of the buffer")
    u64 count = length
    if length == 18446744073709551615ul then count = total - start
    if count > total - start then throw InvalidBufferRangeException("start and length are outside the buffer")
    if atLeast > count then throw InvalidBufferRangeException("the minimum is larger than the length")
    return count
  }

  override fun Root.IO.StreamReadWriteAccessMode get_readMode() { readMode }
  override fun Root.IO.StreamReadWriteAccessMode get_writeMode() { writeMode }
  override fun Root.IO.StreamOpenMode get_openMode() { openMode }
  override fun bool get_canRead() { handle != 0 && readMode != Root.IO.StreamReadWriteAccessMode.None }
  override fun bool get_canWrite() { handle != 0 && writeMode != Root.IO.StreamReadWriteAccessMode.None }
  override fun bool get_canSeek() { handle != 0 }
  override fun bool get_hasLength() { handle != 0 }
  override fun u64 get_position() {
    requireOpen()
    return Root.IO.Native.StreamPosition(handle)
  }
  override fun u64 get_length() {
    requireOpen()
    return Root.IO.Native.StreamLength(handle)
  }

  override fun u64? read(arr<u8> buffer, u64 start = 0, u64 length = 18446744073709551615ul, u64 readAtLeast = 0) {
    requireOpen()
    if readMode == Root.IO.StreamReadWriteAccessMode.None then throw StreamModeNotAllowedException("The read mode is None")
    u64 count = span(buffer.len, start, length, readAtLeast)
    i64 got = Root.IO.Native.StreamRead(handle, buffer, start, count, readAtLeast)
    File.check(path)
    if got < 0 then return null
    return (u64)got
  }
  override fun u64 write(arr<u8> buffer, u64 start = 0, u64 length = 18446744073709551615ul, u64 writeAtLeast = 0) {
    requireOpen()
    if writeMode == Root.IO.StreamReadWriteAccessMode.None then throw StreamModeNotAllowedException("The write mode is None")
    u64 count = span(buffer.len, start, length, writeAtLeast)
    i64 put = Root.IO.Native.StreamWrite(handle, buffer, start, count)
    File.check(path)
    return (u64)put
  }
  override fun u8? readByte() {
    requireOpen()
    if readMode == Root.IO.StreamReadWriteAccessMode.None then throw StreamModeNotAllowedException("The read mode is None")
    i32 value = Root.IO.Native.StreamReadByte(handle)
    File.check(path)
    if value < 0 then return null
    return (u8)value
  }
  override fun bool writeByte(u8 value) {
    requireOpen()
    if writeMode == Root.IO.StreamReadWriteAccessMode.None then throw StreamModeNotAllowedException("The write mode is None")
    bool written = Root.IO.Native.StreamWriteByte(handle, value)
    File.check(path)
    return written
  }
  override fun u64 seek(i64 position, Root.IO.StreamSeekOrigin origin) {
    requireOpen()
    u64 moved = Root.IO.Native.StreamSeek(handle, position, (i32)origin)
    u64 silent = File.check(path)
    if silent == 32 then throw SeekBeforeBeginException("Seek before the start of the stream")
    return moved
  }
  override fun flush(bool forcePhysical = false) {
    requireOpen()
    Root.IO.Native.StreamFlush(handle, forcePhysical)
    File.check(path)
  }
  override fun close() {
    if handle == 0 then return
    u64 closing = handle
    handle = 0
    Root.IO.Native.StreamClose(closing)
    File.check(path)
  }
} atEnd {
  close()
}
""";
}
