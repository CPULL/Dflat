# Dflat CalendarDateTime library

Version 0.0.2 2026-11-23 by CPU

Status: everything below is preliminary unless stated otherwise. Conflicts are in section 9, open items in section 10.

## 1. Overview

- Optional library `CalendarDateTime`: available only when imported, so programs that don't need it stay small.
- The native `date` is a UTC instant with no timezone and no locale. This library adds timezone and locale support on top of it.
- `CalendarDate` is like `DateTimeOffset` plus locale info. It is only used to show a date and time in other timezones and locales.
- Requirements: Gregorian calendar only; IANA timezone database; Daylight Saving Time support; locale-aware formatting; locale-aware parsing.
- Out of scope: non-Gregorian calendars, recurring events, business calendars, leap seconds.
- `Locales` and `TimeZones` are globals, so they are thread safe intrinsically.
- No EF mapping yet (EF does not exist yet).

## 2. Namespace and import

```
namespace root.calendar
import lib calendardatetime
```

## 3. CalendarDate

`CalendarDate` keeps the `TimeZone` itself and not only the offset: an offset may change for daylight saving in one country and not in another country with the same offset.

```
class CalendarDate {
  date instant
  TimeZone timeZone
  string locale                       // type open, see section 10

  static CalendarDate fromUtc(date utc, TimeZone tz, string locale = <root locale>)
  static CalendarDate now(TimeZone tz, string locale = <root locale>)
  static CalendarDate parse(string text, string locale, TimeZone tz)
  static CalendarDate parse(string text, string pattern, string locale, TimeZone tz)
  static CalendarDate ymd(i32 year, i32 month, i32 day, TimeZone tz, string locale)

  date toUtc()
  CalendarDate changeTimeZone(TimeZone tz)
  CalendarDate changeLocale(string locale)
  string format()
  string format(string pattern)

  i32 Year
  i32 Month
  i32 Day
  i32 Hour
  i32 Minute
  i32 Second
  i32 dayOfWeek                       // ISO: starts on Monday
  i32 dayOfYear                       // an ordinal: starts at 1
  string monthName
  string dayName
}
```

Constructors: the four from the previous spec are kept. The default locale (was `EN_US`) is now the root locale `International ISO`. Added by the user: `ymd(i32 year, i32 month, i32, day, tz, locale)` (written as given).

`ymd` gives midnight in the zone. If midnight does not exist in that zone, it gives the closest valid time. If midnight exists twice (autumn overlap), the earlier occurrence.

Time zone conversion changes the local representation only. The underlying UTC instant stays unchanged:

```
UTC           2026-11-14 09:00
Europe/Rome   2026-11-14 10:00
Asia/Tokyo    2026-11-14 18:00
```

Arithmetic and comparison follow the standard date arithmetic: convert to `date`, calculate, convert back. Both operators and methods exist (method names: open).

```
CalendarDate a, b
lapse d = a - b
b + d == a (date +- lapse gives a new updated data)
```

Compare: convert both `CalendarDate` to `date`, compare, return the result. Equal does the same. Adding two dates is not defined: only `date - date` (a `lapse`) and `date +- lapse`.

`tostr()`: ISO format for the specified locale, or the format specified in the parameter.

Default formatting of `lapse` is out of scope here: `lapse` has 4 internal formats and belongs to the `lapse` spec.

## 4. TimeZone and TimeZones

`TimeZone` is the zone; `TimeZones` is the registry.

```
class TimeZone {
  public string ianaName                       // "Europe/Rome"
  public string windowsName                    // "W. Europe Standard Time"
  public lapse baseUtcOffset                   // standard offset
  public fun lapse getUtcOffset(date utfMoment)
  public fun bool isDaylightSavingTime(date utfMoment)
  public fun string getAbbreviation(date utfMoment)   // name will be changed
}

class TimeZones {
  public fun TimeZone findTimeZone(string name)
  public fun TimeZone utc()
  public fun TimeZone local()
  public fun arr<TimeZone> getAll()
}
```

- `findTimeZone(name)`: looks up in the database, tolerant of wrong characters. A close enough match returns that `TimeZone`; otherwise null with a silent exception. "Close enough" is to be decided (maybe Levenshtein, "too early").
- `local()` always calls the OS.
- `get(string id)` of the previous spec is not in the list: `findTimeZone` covers it.
- Examples of ids: `UTC`, `Europe/Rome`, `America/New_York`, `Asia/Tokyo`.
- An abbreviation such as `CST` is not a zone: it is only the output of `getAbbreviation` for the zones that use it.

## 5. Time zone database

- IANA tz database. Library option with two variants: (1) embedded copy with fallback to the OS; (2) OS only.
- The embedded copy is IANA data only (no Windows data), in the `zic` compiled format as is (already compact); it embeds only a subset of the zones (a few hundred KB for the full set).
- Zones outside the subset fall back to the OS. On Windows the OS uses Windows names, hence `windowsName` in `TimeZone`.

## 6. Locale

`Locale` is only data: it never formats anything, it only provides strings that the formatting code uses as is. It covers dates and numbers (no currency).

```
class Locale {
  public string name
  public string description
  public Locale parent
  public map<LocaleParts, string> parts
  public fun string getValue(LocaleParts part)   // own parts first, then the parent chain
}
```

- A child locale holds only what it changes from its parent, as in CLDR.
- An intrinsic root locale `International ISO` defines everything. Its parent is null; any other locale without a parent gets the root. It is the default when no locale is given. It is a mix of US and ISO, with dates as `yyyy/MM/dd HH:mm`.
- Tags are case-insensitive; `-` and `_` are the same (`it-it`, `IT_it`, `it-IT` are the same locale).

User sketch of the registry (the user said: "The code is just an idea and has to be improved"):

```
class Locales {
  Locale default = {...}
  map<string, Locale> locales
  fun Locale findLocale(string name) {
    string key = name.trim().tolower()
    if locales.hasKey(key) then return locales[key]
    key = key.substr(0, 2)
    if locales.hasKey(key) then return locales[key]
    return default
  } 
}
```

Earlier sketch:

```
class Locales {
  public fun Locale findLocale(string tag) { ... }
  public fun bool addLocale(string tag, ...) { ... }
}
```

Improved lookup (the user: "Something like this, yes. But all lowercase to avoid false missmatches"): normalize (trim, lowercase, `_` becomes `-`), try the full tag, drop the last subtag until a match, end at the default:

```
zh-hant-tw  ->  zh-hant  ->  zh  ->  default
```

`addLocale` adds a locale at runtime; its signature is open (the locale is now a `Locale` with name, description, parent and parts).

### LocaleParts

All values are strings (`map<LocaleParts, string>`).

```
enum LocaleParts {
  WeekDayMon, WeekDayTue, WeekDayWed, WeekDayThu, WeekDayFri, WeekDaySat, WeekDaySun,
  WeekDayMonShort, WeekDayTueShort, WeekDayWedShort, WeekDayThuShort,
  WeekDayFriShort, WeekDaySatShort, WeekDaySunShort,
  WeekDayMonCode, WeekDayTueCode, WeekDayWedCode, WeekDayThuCode,
  WeekDayFriCode, WeekDaySatCode, WeekDaySunCode,       // optional, 2 letters

  MonthNameJan, MonthNameFeb, MonthNameMar, MonthNameApr, MonthNameMay, MonthNameJun,
  MonthNameJul, MonthNameAug, MonthNameSep, MonthNameOct, MonthNameNov, MonthNameDec,
  MonthNameJanShort, MonthNameFebShort, MonthNameMarShort, MonthNameAprShort,
  MonthNameMayShort, MonthNameJunShort, MonthNameJulShort, MonthNameAugShort,
  MonthNameSepShort, MonthNameOctShort, MonthNameNovShort, MonthNameDecShort,

  EraNameAD, EraNameBC,
  AmText, PmText,
  DateSeparator, TimeSeparator, FirstDayOfWeek,

  DateShort, DateCommon, DateCompact, DateLong,
  DateTimeShort, DateTimeCommon, DateTimeCompact, DateTimeLong,
  TimeFormatShort, TimeFormatLong,

  NumberGrouping,                    // working name; a `g` string such as "g,3", "g.3", "g,32,"
  DecimalSeparator,
  NegativeSign, PositiveSign, PercentSymbol,
  NaNText, PositiveInfinityText, NegativeInfinityText
}
```

Pattern entries (patterns are raw strings in the format letters of section 7):

```
Short    28/12/2025
Common   28 mar 2025
Compact  28-12-25
Long     monday 28 march 2025
```

Each is also present with time (`DateTime...`). To avoid many formats, a pattern can contain an optional part, for example `HH:mm[:ss]`.

The root defines every part, so `getValue` always finds a value.

## 7. Format and parse

### Native date formatting (spec 8.4, as given)

```
8.4 Dates and times

* C# custom pattern letters: `d f F g h H k m M s t y z`.
* A `time` uses only the letters of the time parts: `h H m s f F t k`. A date-only letter (`d M y g z w q i r e j`) used with a `time` is a compile error.
* Additions: `w` week, `q` quarter, `i` ISO week, `r` ISO year, `e` day of year (3 wide), `j` day of week; a doubled letter pads with a space.
* Alignment: `a<20`, `a^20`, `a>20`; no truncation.
* Locale names and first day of week: later.
```

`CalendarDate` uses the same letters as the native `date`; where something is missing it is added. The locale names and first day of week of 8.4 ("later") now come from `Locale`.

### Letters

```
d    day of month (d, dd)
f    fraction of second (f to fffffff digits)
F    same as f, but trailing zeros are dropped
g    era (g, gg)
k    (to be checked in the code)
M    month number (M, MM); MMM short month name, MMMM full month name (locale)
t    AM/PM (t first letter, tt full; locale)
y    year (y min digits excluding hundreds (2002 -> 2, 2026 -> 26), yy 2 digits excluding hundreds (2008 -> 08), yyyy (always 4 digits)
z    UTC offset (z +1, zz +01, zzz +01:00)
h    12 hours hour (h minimum digits, hh always 2 digits)
H    24 hours hour (H minimum digits, HH always 2 digits)
m    minutes (m minimum digits, mm always 2 digits)
s    seconds (s minimum digits, ss always 2 digits)
w    week
q    quarter
i    ISO week
r    ISO year
e    day of year (3 wide)
j    day of week
```

Changed and added:

```
d, dd          day of month only (ddd and dddd are removed: weekday names are only on j)
j              day of week as a number, ISO: Tuesday prints 2
jj             first 2 letters of the 3-letter name: tu (optional WeekDay...Code entry; if missing, truncate the 3-letter name)
jjj            tue
jjjj           Tuesday
L              zone IANA name (Europe/Rome)
l              zone abbreviation (CET / CEST); print only, only for a CalendarDate, not parsed
S              daylight saving flag: prints DST when active, otherwise empty
T              locale tag (it-IT)
```

A native `date` ignores `L l S T` (they print nothing). The weekday names in the patterns of section 6 are written with `j`, for example `jjjj d MMMM yyyy`.

### Pattern rules

- A pattern is a raw string used as is; only the letters have a meaning.
- `[` and `]` mean optional when parsing (the parser decides, not `Locale`); when formatting they are removed and their content is printed: `HH:mm[:ss]` prints `10:25:30`.
- `\` escapes: `\[` and `\]` print literal brackets (`\[hh:mm[:ss]\]` prints `[12:34:56]`); `\` also escapes letters.

### Parse

- `parse(text, locale, tz)` without a pattern: ISO first (all ISO forms), then the patterns of the locale, from the longest to the shortest, including the ones with time.
- With a pattern: parse following it.
- Parsing is completely case-insensitive.
- Two-digit year: 49 is 2049, 50 is 1950.
- ISO text with an offset: parsing a `date` ignores the offset; parsing a `CalendarDate` uses the offset to compute the actual date and time in the specified timezone.
- Failure: a null result and a silent exception, like the native `date`.
- Letter `L` parsing matches `ianaName`; `l` cannot be parsed; `S` and `T`: open.

## 8. Number grouping (as given)

```
Grouping g
g puts a separator between the groups of digits of the integer part, counted from the right.

g<sep><size>: one separator and one group size, repeated: g,3 → 1,234,567.
More sizes, each followed by its separator, are used in turn from the right; the last one repeats: g,32, → 12,34,56,789 (Indian grouping).
A trailing r counts the groups from the left: g,3r → 123,456,7.
gL takes the grouping of the locale.
The separator cannot be a letter or a digit.
Format	1234567	1234567.891
g,3	1,234,567	
g.3	1.234.567	
g 3	1 234 567	
g_3	1_234_567	
g,3D2		1,234,567.89
g.3D,02		1.234.567,89
```

`gL` takes the grouping from the `NumberGrouping` part of the locale (it replaces the former separate thousands separator and group sizes).

## 9. Conflicts

- `findLocale` with no match. Version 1 (earlier decision): returns null, with a silent exception. Version 2 (the sketch above): returns `default` (the root). The user then said "default with silent exception is not a possibility, sadly" and will check whether the language can support it.

## 10. Open items

1. `findLocale` no-match behaviour (section 9)
2. Parsing of `S` and `T`; parsing a weekday that does not match the date
3. Letter `k` (to be checked in the code)
4. Root locale: the actual values of all `LocaleParts`
5. CLDR: to be evaluated; what to take (it is one file per locale, with inheritance)
6. Embedded tz data: which subset of zones, and who chooses it
7. Name of `getAbbreviation` ("name will be changed")
8. Value of `windowsName` for a zone without a Windows equivalent
9. Method names of `CalendarDate` (add, difference, compare, equal)
10. Type of `locale` in `CalendarDate` (a string tag in the previous spec; `Locale` in the later sketches)
11. Signature of `addLocale`
12. Definition of "close enough" in `findTimeZone`
13. Declaration form of the globals `Locales` and `TimeZones`
14. Final name of `NumberGrouping`

## 11. Changes to other specs

- Spec 3 (date and time): the line naming `CalendarDate` and `ZonedDateTime` is replaced by: "Calendar dates are handled by the class CalendarDate, in the optional CalendarDateTime library: available only when imported, so programs that don't need it stay small."
- Previous library spec (`WorldDateTime`): the class is renamed `CalendarDate`; "Native DateTime" becomes `date`; "Custom locale definitions" is removed from Out of Scope; its pattern list is replaced by a pointer to spec 8.4 and section 7; the default locale `EN_US` becomes the root locale.
- Spec 8.4: the line "Locale names and first day of week: later" is now covered by `Locale`.

## Changes in 0.0.2

- New document, replacing the previous `WorldDateTime` library spec
- One class `CalendarDate` (no `ZonedDateTime`), library `CalendarDateTime`, namespace `root.calendar`
- `ymd` constructor added
- `TimeZone` and `TimeZones` split; `ianaName`, `windowsName`, `getUtcOffset`
- Tz database: IANA, embedded subset with OS fallback, or OS only
- `Locale` class with parent chain, `Locales` registry, `LocaleParts` (as `map<enum, string>`), root locale `International ISO`
- Letters: `d` day of month only, `j` family, `L l S T`, `[...]` optional, `\` escape
- Parse order, two-digit year, offset handling, failure behaviour
- Arithmetic and comparison through `date`