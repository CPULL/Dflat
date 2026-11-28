# JSON Library

v 0.0.2 2026/11/28 by CPU

## 1. General

The JSON library provides functionality for parsing and serializing JSON data.

Import the library with: `import lib json`

The library provides the namespace: `serialization.json`

This namespace is imported automatically into the file that imports the library. Other files can import it explicitly.

The public API currently consists of:
- class `JSON`: provides parsing and serialization.
- class `JsonNode`: represents a JSON value and provides access to object members.
- bitfield `JsonOptions`: controls parsing and serialization behaviour.

All other types and implementation details are internal.

## 2. JSON

The JSON class provides methods to deserialize JSON text into a JsonNode and serialize a JsonNode into JSON text.

| Definition | Example | Description |
|---|---|---|
| `static JsonNode parse(string json, JsonOptions options)` | `JsonNode node = JSON.parse(json, options)` | Parses JSON text and returns the resulting JsonNode. |
| `static string serialize(JsonNode node, JsonOptions options)` | `string json = JSON.serialize(node, options)` | Serializes a JsonNode into JSON text. |

The methods are static so you do not need to instantiate the JSON class. One method is used to transform a Json string to a Json tree nodes (to handle all its contents) and the other method is used to transform back to a serialized string a tree of Json nodes.

Both methods accept `JsonOptions` to decide how to parse and how to format.

### 2.1 JsonOptions

The bitfield `JsonOptions` defines options for both parsing and formatting:

| Value | Used for | Description |
|---|---|---|
| `PrettyPrint` | Serializing output | Produces formatted JSON text with all items indented, making it easier for humans to read. |
| `AllowCommentsInSerialization` | Serializing output | If the tree of Json nodes contains comments, they are included in the output. |
| `NoStrict` | Parsing input | Allows non-standard JSON syntax, including unquoted keys, single-quoted keys and strings, trailing commas and comments. Leading commas are still invalid. |
| `AllowCommentsInParsing` | Parsing input | Comments found inside objects and arrays are retained in the tree of Json nodes. Comments before or after the root value are ignored. |

### 2.2 Parsing Json text file
```
var parsedJson = JSON.parse("{\"key\":123}", JsonOptions.NONE)
Console.log(parsedJson.isObject()) -> true
```

Parsing consist in calling the method and providing the input text.<br>
In case of parsing errors, a set of exceptions will be produced, giving the info about the error:
In case of parsing errors, a JsonParsingException is thrown, providing information about the error. The following errors can be reported:
- Unexpected end of input: the JSON string terminates too early.
- Unterminated object: there is no closing `}` after an opening `{`.
- Unexpected comma: a comma is in a position where it makes no sense, and `NoStrict` is not enabled.
- Expected a string key: an object key is not a string (for example, it is a number), and `NoStrict` is not enabled.
- Expected ':' after the key: the `:` separator is missing between a key and its value.
- Expected ',' or '}': inside an object, either the comma separating two members is missing or the object is not closed.
- Unterminated array: the JSON string ends before the array is terminated.
- Trailing comma is not allowed: a comma appears after the last member of an object or element of an array, and `NoStrict` is not enabled.
- Expected ',' or ']': inside an array, either the comma separating two elements is missing or the array is not closed.
- Invalid character '<character>' in an unquoted key: this character cannot be used in an unquoted key when `NoStrict` is enabled.
- Expected a key: an object member is expected, but no valid key is found.
- Unterminated string: the string is not closed with a double quote (or an allowed single quote).
- Control character inside a string: a string contains a control character that must be escaped.
- Unterminated escape sequence: the JSON string ends before an escape sequence is completed.
- Incomplete \u escape: a Unicode escape sequence does not contain all four hexadecimal digits.
- Invalid escape sequence '\<EscapedCharacter>': the escaped character is not supported.
- Invalid number: the number does not follow the expected JSON number syntax.
- Invalid number: digits expected after '.': a decimal point is not followed by digits.
- Invalid number: digits expected in the exponent: the exponent marker is not followed by digits.
- Comments are not allowed: a comment is encountered but comments are not enabled for parsing.
- Unterminated comment: a block comment is not closed with `*/`.
- Invalid hex digit '<value>': a Unicode escape sequence contains a character that is not a hexadecimal digit.

The exception has a public member `position` telling where exactly the error happened in the source string.

### 2.3 Serializing Json nodes

Serializing consists in calling `JSON.serialize()` and providing the root `JsonNode` and the desired options.

The method returns a string containing the serialized JSON text.

Example:
```
    var parsedJson = JSON.parse("{\"key\":123}", JsonOptions.NONE)
    string jsonText = JSON.serialize(parsedJson, JsonOptions.NONE)
    Console.log(jsonText) -> {"key":123}
```

The following rules apply when serializing:

- Objects are serialized using their members, preserving their original order.
- Arrays are serialized using their elements, preserving their original order.
- Strings are enclosed in double quotes. Special characters are escaped:
  - `"` becomes `\"`
  - `\` becomes `\\`
  - Newline becomes `\n`
  - Carriage return becomes `\r`
  - Tab becomes `\t`
  - Other control characters are encoded using `\u00XX`.
- Boolean values are serialized as `true` or `false`.
- Null values are serialized as `null`.
- Numbers are serialized according to their stored numeric representation.
- Comments are included in the output only when `AllowCommentsInSerialization` is enabled.

When `PrettyPrint` is enabled, the output is formatted with indentation to make it easier to read. Each nesting level uses two spaces.

Without `PrettyPrint`, the output is compact, without additional formatting whitespace.

The serializer preserves the order of object members and array elements.

## 3 JsonNode

The `JsonNode` class represents a value in the Json document. A Json document is represented as a tree of nodes. Each node contains a value and can represent a null value, a number, a string, a boolean, an object, an array or a comment.

Nodes belonging to objects can also have a key, which identifies the corresponding object member. Nodes belonging to arrays do not use keys.

The internal properties `key` and `value` are not directly accessible to library users. The following public methods provide access to the node's type, key and value.

### 3.1 Type checking

Each of the following methods returns `true` if the node represents the corresponding type, otherwise it returns `false`.

| Definition | Description |
|---|---|
| `bool isNull()` | Checks whether the node represents a null value. |
| `bool isNumber()` | Checks whether the node represents a number. |
| `bool isNumberInt()` | Checks whether the node represents an integer number. |
| `bool isNumberFloat()` | Checks whether the node represents a floating-point number. |
| `bool isString()` | Checks whether the node represents a string. |
| `bool isBool()` | Checks whether the node represents a boolean. |
| `bool isObject()` | Checks whether the node represents an object. |
| `bool isArray()` | Checks whether the node represents an array. |
| `bool isComment()` | Checks whether the node represents a comment. |

### 3.2 Key access

| Definition | Example | Description |
|---|---|---|
| `string? getKey()` | `string? key = node.getKey()` | Returns the node's key, or null if the node has no key. |

### 3.3 Value access

The following methods return the value represented by the node. If the node does not contain the requested type, a default value is returned.

| Definition | Example | Description |
|---|---|---|
| `i64 getIntValue()` | `i64 value = node.getIntValue()` | Returns the integer value if the node contains an integer number; otherwise returns `0`. |
| `r64 getFloatValue()` | `r64 value = node.getFloatValue()` | Returns the floating-point value if the node contains a floating-point number; otherwise returns `0.0`. |
| `string getStringValue()` | `string value = node.getStringValue()` | Returns the string value if the node contains a string; otherwise returns an empty string. |
| `bool getBoolValue()` | `bool value = node.getBoolValue()` | Returns the boolean value if the node contains a boolean; otherwise returns `false`. |
| `arr<JsonNode> getArrayValue()` | `for item in node.getArrayValue() { }` | Returns the elements of the array, in document order, if the node contains an array; otherwise returns an empty array. The elements have no key. |
| `arr<JsonNode> getObjectValue()` | `for member in node.getObjectValue() { }` | Returns the members of the object, in document order, if the node contains an object; otherwise returns an empty array. The key of each member is read with `getKey()`. |

The arrays returned by `getArrayValue()` and `getObjectValue()` are lent: the nodes still belong to the tree and are read-only. Comments kept by `AllowCommentsInParsing` are part of these arrays too: check them with `isComment()`; a comment has no key.

### 3.4 Object access

The following methods operate on nodes that represent objects. They access the object's direct child nodes.

| Definition | Example | Description |
|---|---|---|
| `JsonNode get(string targetKey)` | `JsonNode item = node.get("name")` | Returns the first child node whose key matches `targetKey`. Returns null if no matching key exists or the node is not an object. |
| `bool hasKey(string targetKey)` | `bool exists = node.hasKey("name")` | Returns true if the object contains a child with the specified key. |

The `get` and `hasKey` methods search only the direct children of the object. They do not search recursively through nested objects or arrays.

If an object contains multiple children with the same key, `get` returns the first matching child; `getObjectValue()` gives all of them.
