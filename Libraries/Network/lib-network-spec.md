# High level and preliminary specification of a lirary to handle networking communications
Version 0.0.1 2026/10/18 by CPU

Mostly based on BSD sockets model.
namespace will be `root.network`

`public enum Root.Network.IPVersion { IPv4, IPv6 }`



## Address Resolution & IP Representation
we need a way to store an `IPAddress`.
It should be able to store IPv4 and v6 addresses and have a method like: Root.Network.IPVersion v = myIPAddress.version
It needs a port, as i32, reserving some values to invalid statuses
a method `fun resolveAddress(string address [, options])` that will go over the DNS to transform the name address to an ip
Maybe a constructor like: `IPAdress ipaddr = IPAddress("www.google.com")`


| Definition | Type | Mode | Description |
|---|---|---|
| `u128 ipAddr` | Prop | Private | Actual IP address as numeric value, IPV4 will use only the lower 32 bits |
| `i32 port` | Prop | Read only | Port associated with the address, 0 if none
| `Root.Network.IPVersion version` | Read only | Version of the IP address |
|  |  |  |

`IPAddress IPAddress(string address, IPAddress dnsIp = null)`
`IPAddress IPAddress(string address, u32 port, IPAddress dnsIp = null)`
`IPAddress IPAddress(u128 addressNumeric, IPAddress dnsIp = null)`
`IPAddress IPAddress(u128 addressNumeric, u32 port, IPAddress dnsIp = null)`
`IPAddress IPAddress(u32 addressNumeric, IPAddress dnsIp = null)`
`IPAddress IPAddress(u32 addressNumeric, u32 port, IPAddress dnsIp = null)`



## TCPStream
Used to connect to another endpoint and have TCP communications.

TCPStream tcp = TCPStream("192.168.1.123:12345")
catch ConnectionRefusedException ex { }
catch ConnectionTimedOutException ex { }
catch NetworkInvalidAddressException ex { }

TCPStream tcp = TCPStream(anIPAddress, timeout: 6000) atEnd { tcp.close() }

### Properties
```
public readonly bool isConnected // true if there is a connection
public u32 timeOut // timeout in milliseconds
public const IPAddress endPoint // connected endpoint
public const IPAddress localPoint // local endpoint
public readonly bool isClosed // To check if the connection was already closed
```

### Methods
Some methods are inherited from Root.IO.Stream

| Definition | Description |
|---|---|
| `uns? read(arr<byte> buffer, uns start = 0, uns length = <rest of the buffer>, uns readAtLeast = 0)` | Reads into the buffer; returns the number of bytes read, `0` if nothing is available, `null` at the end of the stream |
| `uns write(arr<byte> buffer, uns start = 0, uns length = <rest of the buffer>, uns writeAtLeast = 0)` | Writes from the buffer; returns the number of bytes written |
| `byte? readByte()` | Reads one byte; `null` at the end of the stream |
| `bool writeByte(byte value)` | Writes one byte; true if the byte was written |
| `flush()` | Sends and receives any pending data |
| `close()` | Terminate gracefully the connection, the connection can be reopen later as new connection |
| `reopen()` | Tries to reconnect with a closed connection. |

### Network exceptions

| id | name | descrition |
|---|---|---|
|  | NetworkException | The parent exception class for all network exception |
|  | ConnectionRefusedException | The endpoint is either not running, not listening on that specific port, or actively rejecting connections. |
|  | ConnectionTimedOutException | The endpoint didn't respond within the allotted time (e.g., the IP address is dead, a firewall is silently dropping packets, or there's severe network congestion). |
|  | NetworkUnreachableException | The local machine has no route to the destination network (e.g., the computer is completely offline or lacks an active gateway). |
|  | HostUnreachableException | The local machine can reach the router/gateway, but the target host cannot be reached. |
|  | AddressAlreadyInUseException | (More common on binding) the address/port is already in use or it is saturated |
|  | ConnectionResetByPeerException | The remote side abruptly terminated the connection (e.g., the server crashed, forcefully killed the process, or closed the socket while data was still expected). |
|  | UsingClosedSocketException | Trying to write to a socket that has already been closed by the remote peer. (On some operating systems, writing to a broken pipe also triggers a fatal signal unless handled). |
|  | TimedOutException | A read or write operation exceeded the socket's configured timeout limit because the connection stalled or the peer stopped responding. |
|  | WebSocketHandshakeException | Problems, during the handshake, in the estabilishing a web socket connection |
|  |  |  |



## TCPListener
Used to create listeners for incoming connections.

```
TCPListener
bool isListening
IPAddress localEndpoint
```

`TCPListener TCPListener(string endPoint)`
`TCPListener TCPListener(string endPoint, u32 port)`
`fun TCPStream accept() // Waits for an incoming connection`



## UDPSocket

```
public readonly bool isBound // true if the socket is bound to a local port
public const IPAddress localEndpoint // local endpoint

UDPSocket UDPSocket() // Creates an unbound socket (useful just for sending).
UDPSocket UDPSocket(string endPoint) // Creates and binds a socket to a local endpoint to listen for incoming datagrams.
UDPSocket UDPSocket(string endPoint, u32 port) // Creates and binds a socket to a local endpoint to listen for incoming datagrams.
UDPSocket UDPSocket(IPAddress address) // Creates and binds a socket to a local endpoint to listen for incoming datagrams.
UDPSocket UDPSocket(IPAddress address, u32 port) // Creates and binds a socket to a local endpoint to listen for incoming datagrams.

```

| Definition | Description |
|---|---|
| `(uns? length, IPAddress sender) read(arr<byte> buffer, uns start = 0, uns length = <rest of the buffer>)` | Reads into the buffer; returns the number of bytes read and the ip of the sender as tuple |
| `write(arr<byte> buffer, uns start = 0, uns length = <rest of the buffer>, IPAddress destination)` | Writes from the buffer to the destination IP address | 
| `flush()` | Sends and receives any pending data |
| `close()` | Terminate gracefully the connection |
| `fun wait(arr<byte> buffer, uns start = 0, uns length = <rest of the buffer>, fun callBack(uns? len, IPAddress sender))

## WebSocket
WebSockets run on top of TCP (usually initiated via an HTTP handshake on ports 80 or 443, or ws:// / wss://). Since it's message-oriented rather than raw byte-stream-oriented, the API should reflect sending and receiving text or binary frames.

### Properties & Methods

```
public readonly bool isConnected // true if the WebSocket handshake completed successfully
public const string url // the target ws:// or wss:// URL
public const IPAddress address // The ip and port of the connection

WebSocket WebSocket(string url) // Connects to a WebSocket server, handling the underlying TCP connection and HTTP upgrade handshake.
```

Exceptions: Can throw ConnectionRefusedException, ConnectionTimedOutException, or WebSocketHandshakeException.

| Definition | Description |
|---|---|
| `string receiveText()` | Blocks until a text message frame is received from the server. |
| `waitText(fun callBack(string text))` | Waits until a text message frame is received from the server, then calls the callback with the text received |
| `arr<byte> receiveBinary()` | Blocks until a binary message frame is received. |
| `waitBinary(fun callBack(arr<byte>))` | Waits until a binary message frame is received from the server, then calls the callback with the data received |
| `waitBinary(arr<byte>, uns start = 0, uns length = <rest of buffer>, callBack: fun callBack(uns start, uns len))` | Waits until a binary message frame is received from the server on the passed buffer, then calls the callback with the start and size of the data received. Has to handle the case the data is bigger than the buffer |
| `bool sendText(string message)` | Sends a text frame. |
| `bool sendBinary(arr<byte> buffer, uns start = 0, uns length = <rest of buffer>)` | Sends a binary data frame. |
| `close(u16 statusCode = 1000, string reason = "")` | Sends a WebSocket close frame and terminates the underlying TCP connection gracefully. |

## Things to decide and fix

- Address vs endpoint: IPAddress carries the port. Is that deliberate (like .NET IPEndPoint), or should address and port be separate types? The i32 port property takes reserved values, but the constructors take u32.
- DNS in the constructor: IPAddress("www.google.com") blocks on a lookup. A name can also resolve to several addresses (IPv4 and IPv6). Which one wins? What happens when the lookup fails? - There is no DNS exception in the list.
- Silent exceptions on constructors: if TCPStream(...) is refused and no catch is present, what does the constructor return? Null, since it’s an object?
- Reopen: close() says the connection can be reopened, and there is reopen(). As I recall the stdlib streams, a closed stream cannot be reopened. Should network streams behave differently?
- Callbacks: wait, waitText and waitBinary imply an event model. On which thread or loop does the callback run? Does the language have threads or async yet, or is this a new concept?
- TLS: wss:// needs it, and nothing in the spec covers it. Is TLS in scope for this library, or ws:// only for now? Is TCPStream also used for TLS?
- Blocking and timeouts: read returns 0 when nothing is available and null at the end. Does that mean non-blocking? How does timeOut combine with that, and with TimedOutException, which overlaps ConnectionTimedOutException?
- Server side: TCPListener has no backlog, and accept() has no timeout or callback form. WebSocket has no server mode. Is a server in scope for 0.0.1?
- Micro-controllers: u128 addresses and full IPv6 may be heavy on small targets. Should IPv6 be optional there?
