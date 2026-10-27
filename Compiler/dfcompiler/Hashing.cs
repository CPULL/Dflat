using System.Buffers.Binary;
using System.Text;

namespace dfcompiler;

// The hash of spec 6.2.1 computed by the compiler, for the values known at compile time (the names of the classes, of the types and of the
// unstable fields). The same algorithm as the C runtime (RuntimeHash.cs): rapidhash V3, full variant, compact loop, seed 0, by Nicolas De Carli
// (MIT, https://github.com/Nicoshev/rapidhash), and the ordered combine $ (FxHash). With a 32-bit uns the 64-bit result is folded: hi ^ lo.
public static class DfHash {
  static readonly ulong[] Secret = {
    0x2d358dccaa6c78a5UL, 0x8bb84b93962eacc9UL, 0x4b33a62ed433d4a3UL, 0x4d5a2da51de1aa47UL,
    0xa0761d6478bd642fUL, 0xe7037ed1a0b428dbUL, 0x90ed1765281c388cUL, 0xaaaaaaaaaaaaaaaaUL
  };

  public const ulong Null64 = 0x2AA52E3F0BF438BDUL;   // "null".hash (spec 6.2.1)
  public const ulong Null32 = 0x21511682UL;

  static void Mum(ref ulong a, ref ulong b) {
    UInt128 r = (UInt128)a * b;
    a = (ulong)r;
    b = (ulong)(r >> 64);
  }

  static ulong Mix(ulong a, ulong b) {
    Mum(ref a, ref b);
    return a ^ b;
  }

  static ulong R64(byte[] p, int at) => BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(at, 8));
  static ulong R32(byte[] p, int at) => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(at, 4));

  public static ulong RapidHash(byte[] p) {
    ulong seed = 0;
    seed ^= Mix(seed ^ Secret[2], Secret[1]);
    ulong a, b;
    int len = p.Length;
    int at = 0;
    int i = len;
    if (len <= 16) {
      if (len >= 4) {
        seed ^= (ulong)len;
        if (len >= 8) {
          a = R64(p, 0);
          b = R64(p, len - 8);
        } else {
          a = R32(p, 0);
          b = R32(p, len - 4);
        }
      } else if (len > 0) {
        a = ((ulong)p[0] << 45) | p[len - 1];
        b = p[len >> 1];
      } else {
        a = b = 0;
      }
    } else {
      if (len > 112) {
        ulong see1 = seed, see2 = seed, see3 = seed, see4 = seed, see5 = seed, see6 = seed;
        do {
          seed = Mix(R64(p, at) ^ Secret[0], R64(p, at + 8) ^ seed);
          see1 = Mix(R64(p, at + 16) ^ Secret[1], R64(p, at + 24) ^ see1);
          see2 = Mix(R64(p, at + 32) ^ Secret[2], R64(p, at + 40) ^ see2);
          see3 = Mix(R64(p, at + 48) ^ Secret[3], R64(p, at + 56) ^ see3);
          see4 = Mix(R64(p, at + 64) ^ Secret[4], R64(p, at + 72) ^ see4);
          see5 = Mix(R64(p, at + 80) ^ Secret[5], R64(p, at + 88) ^ see5);
          see6 = Mix(R64(p, at + 96) ^ Secret[6], R64(p, at + 104) ^ see6);
          at += 112;
          i -= 112;
        } while (i > 112);
        seed ^= see1;
        see2 ^= see3;
        see4 ^= see5;
        seed ^= see6;
        see2 ^= see4;
        seed ^= see2;
      }
      if (i > 16) {
        seed = Mix(R64(p, at) ^ Secret[2], R64(p, at + 8) ^ seed);
        if (i > 32) {
          seed = Mix(R64(p, at + 16) ^ Secret[2], R64(p, at + 24) ^ seed);
          if (i > 48) {
            seed = Mix(R64(p, at + 32) ^ Secret[1], R64(p, at + 40) ^ seed);
            if (i > 64) {
              seed = Mix(R64(p, at + 48) ^ Secret[1], R64(p, at + 56) ^ seed);
              if (i > 80) {
                seed = Mix(R64(p, at + 64) ^ Secret[2], R64(p, at + 72) ^ seed);
                if (i > 96) {
                  seed = Mix(R64(p, at + 80) ^ Secret[1], R64(p, at + 88) ^ seed);
                }
              }
            }
          }
        }
      }
      a = R64(p, at + i - 16) ^ (ulong)i;
      b = R64(p, at + i - 8);
    }
    a ^= Secret[1];
    b ^= seed;
    Mum(ref a, ref b);
    return Mix(a ^ Secret[7], b ^ Secret[1] ^ (ulong)i);
  }

  // the hash of a text (UTF-8) for a uns of this size
  public static ulong Str(string text, int unsBits) {
    ulong h = RapidHash(Encoding.UTF8.GetBytes(text));
    return unsBits == 32 ? (h >> 32) ^ (h & 0xFFFFFFFFUL) : h;
  }

  public static ulong Null(int unsBits) => unsBits == 32 ? Null32 : Null64;

  // $: h = (rotl(h, 5) ^ part) * K
  public static ulong Combine(ulong h, ulong part, int unsBits) {
    if (unsBits == 32) {
      uint h32 = (uint)h;
      return (uint)((((h32 << 5) | (h32 >> 27)) ^ (uint)part) * 0x9e3779b9U);
    }
    return (((h << 5) | (h >> 59)) ^ part) * 0x517cc1b727220a95UL;
  }

  // a constant of C code for a uns of this size
  public static string CConst(ulong value, int unsBits) => unsBits == 32 ? $"((df_uns)0x{value:X8}u)" : $"((df_uns)0x{value:X16}ull)";
}
