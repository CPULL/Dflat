# WorldDateTime

Provides localized formatting, parsing and timezone support.
Native DateTime always represents a UTC instant and contains no locale or timezone information.

## class TimeZone

class TimeZone {
    string id

    static TimeZone utc()
    static TimeZone local()
    static TimeZone get(string id)

    static arr<TimeZone> getAll()
}

Examples:
    "UTC"
    "Europe/Rome"
    "America/New_York"
    "Asia/Tokyo"


## class WorldDateTime

class WorldDateTime {
    date instant
    TimeZone timeZone
    string locale                       // a locale tag (see Locales)

    static WorldDateTime fromUtc(
        date utc,
        TimeZone tz,
        string locale = EN_US
    )

    static WorldDateTime now(
        TimeZone tz,
        string locale = EN_US
    )

    static WorldDateTime parse(
        string text,
        string locale,
        TimeZone tz
    )

    static WorldDateTime parse(
        string text,
        string pattern,
        string locale,
        TimeZone tz
    )

    date toUtc()

    WorldDateTime changeTimeZone(
        TimeZone tz
    )

    WorldDateTime changeLocale(
        string locale
    )

    string format()
    string format(string pattern)

    i32 Year
    i32 Month
    i32 Day

    i32 Hour
    i32 Minute
    i32 Second

    i32 dayOfWeek
    i32 dayOfYear

    string monthName
    string dayName
}


## Formatting Patterns

yyyy    four-digit year
yy      two-digit year

MMMM    full month name
MMM     short month name
MM      month number (01-12)

dddd    full weekday name
ddd     short weekday name

dd      day of month

HH      hour (00-23)
mm      minute
ss      second


## Locale Support

Formatting and parsing must honor the locale.

Examples:

    Locale: en-US
    11/14/2026 10:25 AM

    Locale: it-IT
    14/11/2026 10:25

    Locale: fr-FR
    14/11/2026 10:25


## Locales

New locales can be added at runtime, so the known locales cannot be an enum (and Dflat has no set of string values: `Generic X { ... }` lists types only).

- The library keeps a private `map<string, LocaleInfo>`: the key is the locale tag, the value holds the details of the locale (month names, day names, date and time patterns, AM/PM, ...). `LocaleInfo` is a struct or a class.
- The map is never exposed to users: every tag given to the library (`addLocale`, the locale parameters, `parse`) is normalized the same way before it is stored or looked up, so `"it-it"`, `"IT_it"` and `"it-IT"` are the same locale. The check is at runtime.
- Constants for the built-in locales (`const string EN_US = "en-US"`, `IT_IT`, `FR_FR`, ...) give a compile-time check to the code that uses them.
- `addLocale(string tag, LocaleInfo info)` adds a locale at runtime.
- Still open (not needed yet): the normalized form of a tag, what a lookup of an unknown tag does (fallback or exception), and what `addLocale` does with a tag that already exists.


## Time Zone Conversion

Changing timezone modifies the local representation only.

The underlying UTC instant shall remain unchanged.

Example:

    UTC
    2026-11-14 09:00

    Europe/Rome
    2026-11-14 10:00

    Asia/Tokyo
    2026-11-14 18:00


## Requirements

- Gregorian calendar only.
- IANA timezone database.
- Daylight Saving Time support.
- Locale-aware formatting.
- Locale-aware parsing.

## Out of Scope

- Non-Gregorian calendars.
- Custom locale definitions.
- Recurring events.
- Business calendars.
- Leap-second support.

## TO SPECIFY
- how to define locales (same library?): the storage is decided (see Locales), the format of LocaleInfo is not
- namespace
- library name


## From Claude
1 (done: date, fromUtc / toUtc; line 4 still says "Native DateTime") DateTime isn't a Dflat type. The native instant is date: UTC, milliseconds since 1970 (spec 2). So DateTime Instant, FromUtc(DateTime …) and ToUtc() should use date. That also answers the first "TO SPECIFY" item: the conversion to and from date is FromUtc and ToUtc.
2 The spec already plans different names. Spec 3 promises an optional DateTime library with the classes CalendarDate and ZonedDateTime, and "no local date-time type". WorldDateTime overlaps with ZonedDateTime, and there's no CalendarDate. Either spec 3 changes or this library takes those names.
3 The format letters conflict with the built-in ones. Spec 8.4 already uses C#-style letters for date/time (d M y H h m s f t z, plus w q i r e j). This library's pattern list is a smaller, different set. Better to reuse the 8.4 letters and add only what needs a locale (month and day names, AM/PM). Spec 8.4 also says "Locale names and first day of week: later", so the locale design would land here.
5 Parse needs failure behaviour. The built-in date parse gives null plus the silent InvalidDateTimeFormatException (ID 24). The library should say whether it does the same, or offers tryParse like the built-in types.
6 The IANA database affects size. The full timezone database is several hundred KB. That clashes with the small-device goal (spec 1), so it should say whether the database is embedded, read from the OS, or cut down to the zones used.
Minor points:
7 DayOfWeek as an i32 should say where counting starts, or be an enum.
8 The en-US example shows 10:25 AM, which needs an AM/PM letter the pattern list doesn't have.
9 (done: see Locales; a string tag normalized by the library, constants for the built-in locales) locale is a free string; a type or a fixed list would catch typos.