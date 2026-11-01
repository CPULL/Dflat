namespace dfcompiler;

// C runtime of .hash (spec 6.2.1), added to a program only when it uses a hash (chunk "hash"): the null constant, the ordered combine $, the
// hash of a string (rapidhash V3, full variant, compact loop, seed 0; folded hi ^ lo for a 32-bit uns), of a real (its normalized bits), of a
// value wider than uns (its two halves, the low one first) and of an object (the hash function of its class descriptor).
// The constants that the compiler knows (class names, type names) are computed by DfHash (Hashing.cs) with the same algorithm.
public static class RuntimeHash {
  public const string Source = """
/* ---- hash: spec 6.2.1 ---- */

/*
 * rapidhash V3 - Very fast, high quality, platform-independent hashing algorithm.
 * Based on 'wyhash', by Wang Yi <godspeed_china@yeah.net>
 * Copyright (C) 2025 Nicolas De Carli
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 *
 * Source repository: https://github.com/Nicoshev/rapidhash
 */

#if DF_UNS_BITS == 32
#define DF_HASH_NULL ((df_uns)0x21511682u)   /* "null".hash */
#else
#define DF_HASH_NULL ((df_uns)0x2AA52E3F0BF438BDull)
#endif

static const uint64_t df_rh_secret[8] = {
  0x2d358dccaa6c78a5ull, 0x8bb84b93962eacc9ull, 0x4b33a62ed433d4a3ull, 0x4d5a2da51de1aa47ull,
  0xa0761d6478bd642full, 0xe7037ed1a0b428dbull, 0x90ed1765281c388cull, 0xaaaaaaaaaaaaaaaaull };

static inline void df_rh_mum(uint64_t* a, uint64_t* b) {
#if defined(__SIZEOF_INT128__)
  __uint128_t r = *a;
  r *= *b;
  *a = (uint64_t)r;
  *b = (uint64_t)(r >> 64);
#else
  uint64_t ha = *a >> 32, hb = *b >> 32, la = (uint32_t)*a, lb = (uint32_t)*b;
  uint64_t rh = ha * hb, rm0 = ha * lb, rm1 = hb * la, rl = la * lb, t = rl + (rm0 << 32), c = t < rl;
  uint64_t lo = t + (rm1 << 32);
  c += lo < t;
  *a = lo;
  *b = rh + (rm0 >> 32) + (rm1 >> 32) + c;
#endif
}

static inline uint64_t df_rh_mix(uint64_t a, uint64_t b) {
  df_rh_mum(&a, &b);
  return a ^ b;
}

/* little-endian reads on every target, so the hash is the same everywhere */
static inline uint64_t df_rh_r64(const uint8_t* p) {
  return (uint64_t)p[0] | ((uint64_t)p[1] << 8) | ((uint64_t)p[2] << 16) | ((uint64_t)p[3] << 24)
    | ((uint64_t)p[4] << 32) | ((uint64_t)p[5] << 40) | ((uint64_t)p[6] << 48) | ((uint64_t)p[7] << 56);
}

static inline uint64_t df_rh_r32(const uint8_t* p) {
  return (uint64_t)p[0] | ((uint64_t)p[1] << 8) | ((uint64_t)p[2] << 16) | ((uint64_t)p[3] << 24);
}

static uint64_t df_rapidhash(const void* key, size_t len) {
  const uint8_t* p = (const uint8_t*)key;
  const uint64_t* secret = df_rh_secret;
  uint64_t seed = 0;
  seed ^= df_rh_mix(seed ^ secret[2], secret[1]);
  uint64_t a = 0, b = 0;
  size_t i = len;
  if (len <= 16) {
    if (len >= 4) {
      seed ^= len;
      if (len >= 8) {
        a = df_rh_r64(p);
        b = df_rh_r64(p + len - 8);
      } else {
        a = df_rh_r32(p);
        b = df_rh_r32(p + len - 4);
      }
    } else if (len > 0) {
      a = (((uint64_t)p[0]) << 45) | p[len - 1];
      b = p[len >> 1];
    }
  } else {
    if (len > 112) {
      uint64_t see1 = seed, see2 = seed, see3 = seed, see4 = seed, see5 = seed, see6 = seed;
      do {
        seed = df_rh_mix(df_rh_r64(p) ^ secret[0], df_rh_r64(p + 8) ^ seed);
        see1 = df_rh_mix(df_rh_r64(p + 16) ^ secret[1], df_rh_r64(p + 24) ^ see1);
        see2 = df_rh_mix(df_rh_r64(p + 32) ^ secret[2], df_rh_r64(p + 40) ^ see2);
        see3 = df_rh_mix(df_rh_r64(p + 48) ^ secret[3], df_rh_r64(p + 56) ^ see3);
        see4 = df_rh_mix(df_rh_r64(p + 64) ^ secret[4], df_rh_r64(p + 72) ^ see4);
        see5 = df_rh_mix(df_rh_r64(p + 80) ^ secret[5], df_rh_r64(p + 88) ^ see5);
        see6 = df_rh_mix(df_rh_r64(p + 96) ^ secret[6], df_rh_r64(p + 104) ^ see6);
        p += 112;
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
      seed = df_rh_mix(df_rh_r64(p) ^ secret[2], df_rh_r64(p + 8) ^ seed);
      if (i > 32) {
        seed = df_rh_mix(df_rh_r64(p + 16) ^ secret[2], df_rh_r64(p + 24) ^ seed);
        if (i > 48) {
          seed = df_rh_mix(df_rh_r64(p + 32) ^ secret[1], df_rh_r64(p + 40) ^ seed);
          if (i > 64) {
            seed = df_rh_mix(df_rh_r64(p + 48) ^ secret[1], df_rh_r64(p + 56) ^ seed);
            if (i > 80) {
              seed = df_rh_mix(df_rh_r64(p + 64) ^ secret[2], df_rh_r64(p + 72) ^ seed);
              if (i > 96) {
                seed = df_rh_mix(df_rh_r64(p + 80) ^ secret[1], df_rh_r64(p + 88) ^ seed);
              }
            }
          }
        }
      }
    }
    a = df_rh_r64(p + i - 16) ^ i;
    b = df_rh_r64(p + i - 8);
  }
  a ^= secret[1];
  b ^= seed;
  df_rh_mum(&a, &b);
  return df_rh_mix(a ^ secret[7], b ^ secret[1] ^ i);
}

/* $: the ordered combine (FxHash) */
static inline df_uns df_hash_comb(df_uns h, df_uns part) {
#if DF_UNS_BITS == 32
  return (((h << 5) | (h >> 27)) ^ part) * (df_uns)0x9e3779b9u;
#else
  return (((h << 5) | (h >> 59)) ^ part) * (df_uns)0x517cc1b727220a95ull;
#endif
}

/* a 64-bit value: itself, or its two halves combined (low first) with a 32-bit uns */
static inline df_uns df_hash_w64(uint64_t v) {
#if DF_UNS_BITS == 32
  return df_hash_comb((df_uns)(uint32_t)v, (df_uns)(uint32_t)(v >> 32));
#else
  return (df_uns)v;
#endif
}

static df_uns df_hash_str(df_string s) {
  df_sv v = df_view(s);
  uint64_t h = df_rapidhash(v.p, (size_t)v.len);
#if DF_UNS_BITS == 32
  return (df_uns)((h >> 32) ^ (uint32_t)h);
#else
  return (df_uns)h;
#endif
}

/* a real: its bits after normalizing (-0.0 is +0.0, every NaN the canonical one) */
static df_uns df_hash_r32(float x) {
  uint32_t bits;
  if (x == 0.0f) {
    x = 0.0f;
  }
  if (x != x) {
    bits = 0x7FC00000u;
  } else {
    memcpy(&bits, &x, sizeof bits);
  }
  return (df_uns)bits;
}

static df_uns df_hash_r64(double x) {
  uint64_t bits;
  if (x == 0.0) {
    x = 0.0;
  }
  if (x != x) {
    bits = 0x7FF8000000000000ull;
  } else {
    memcpy(&bits, &x, sizeof bits);
  }
  return df_hash_w64(bits);
}

/* an object: the hash function of its class (the override of get_hash() or the generated default); null is the null constant */
static df_uns df_hash_obj(const void* o) {
  if (o == NULL) {
    return DF_HASH_NULL;
  }
  const df_class* c = ((const df_object*)o)->cls;
  return c->hash ? c->hash(o) : DF_HASH_NULL;
}

""";
}
