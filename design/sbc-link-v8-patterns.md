# SBC link v8: patterns from mainstream protocols

Status: research input for the v8 requirements, 2026-11-09. This is a pattern catalogue, not a design. No repository was changed.

Scope: the layer above the link (channels, ordering, priority, cancellation, correlation, flow control, retransmission, epochs, layering). Link integrity (CRC, ECC, extended header, phase and sequence guards) is covered in `design/sbc-link-requirements.md` and is only referred to here.

## 0. Method and sources

The primary specs were downloaded and searched as text, so the section numbers and quotes below come from the documents themselves, not from memory. Where a primary spec could not be opened, the entry says so, and section 8 lists everything that is unverified.

| Spec | Version used | Where from |
|---|---|---|
| HTTP/2 | RFC 9113 (June 2022, obsoletes RFC 7540) | rfc-editor.org |
| HTTP priorities | RFC 9218 | rfc-editor.org |
| QUIC | RFC 9000, RFC 9002 | rfc-editor.org |
| SCTP | RFC 9260 (obsoletes RFC 4960), RFC 6525 (stream reconfiguration), RFC 8260 (I-DATA, schedulers), RFC 3758 (PR-SCTP) | rfc-editor.org |
| SSH connection protocol | RFC 4254 | rfc-editor.org |
| TCP | RFC 9293; RFC 6093 (urgent mechanism) | rfc-editor.org |
| Serial number arithmetic | RFC 1982 | rfc-editor.org |
| MQTT | 5.0, OASIS Standard, 07 March 2019 | docs.oasis-open.org |
| AMQP 1.0 | OASIS Standard, 29 October 2012, Part 2 Transport and Part 3 Messaging | docs.oasis-open.org |
| AMQP 0-9-1 | spec PDF (13 November 2008) and `amqp0-9-1.xml` | rabbitmq.com/resources/specs |
| JSON-RPC | 2.0 (origin date 2010-03-26) | jsonrpc.org/specification |
| gRPC | `doc/PROTOCOL-HTTP2.md` at grpc/grpc cf61c7d62a (2025-04-17); grpc.io guide "Cancellation" | github.com, grpc.io |
| USB | USB 2.0 Specification (`usb_20.pdf` in `usb_20_20250603.zip`); Mass Storage Class Bulk-Only Transport 1.0 (1999) | usb.org |
| Modbus | Application Protocol Specification V1.1b3; Messaging on TCP/IP Implementation Guide V1.0b | modbus.org |
| CAN | Bosch CAN Specification 2.0 (1991), copy at esd.cs.ucr.edu; TI application report SLOA101B | mirror, ti.com |
| CANopen | CiA 1301 (CANopen FD) poster, CiA 2026. **CiA 301 itself not opened.** | can-cia.org |
| PCI Express | **Base Specification not opened (members only).** Secondary sources only: patent US 8,495,265 B2 background; Design & Reuse article on the PCIe 6.1 UIO ECN | patents.google.com, design-reuse.com |

v7 facts come from the design documents (`design/sbc-link-current-state.md`, `design/sbc-link-findings-upstream.md`, `design/dsf-findings-upstream.md`, `design/open-findings.md`) and the DSF code under `src/DuetControlServer/Link/Protocol/`. RRF facts come from a local RRF checkout at cde4e4fa1 (merge of 3.7.0+1.mp.8). The lab printer runs 3.7.0+1.mp.9-dev.3, so RRF line numbers may have moved; `sbc-link-v8-requirements.md` uses current line numbers.

## 1. The v7 problems this catalogue is mapped against

| ID | v7 symptom | Documented in | Problem class |
|---|---|---|---|
| V1 | The reply of an aborted M98 completes the next code of the channel (T10). A pushed "macro not found" is held back as an out-of-order reply (T4). | `dsf-findings-upstream.md` T10, `open-findings.md` T4, T10 | Replies are correlated by order only, and cancellation breaks the "one reply per request" assumption. |
| V2 | A macro request that crosses InvalidateChannel is started by DSF (T2). InvalidateChannel ends the macro wait but RRF still sends the request (T1). | `dsf-findings-upstream.md` T1, T2 | Cancellation detected by timing window or flag, not by identity. RRF keeps request state across the abort. |
| V3 | A macro closal that crosses an abort underruns DSF's stack and stops DCS (T12). | T12 | Same as V2. |
| V4 | A resent InvalidateChannel drops the codes sent after the first copy, or ends a macro wait that started in between (T11). | T11 | The control request has a side effect before it is refused (`SbcInterface.cpp:880-886`: `ResolveMacroRequest` runs before the mutex is taken), and the copy is processed in a different context. |
| V5 | `ResendPacket` makes DSF send a packet again later. The copy can overtake or be overtaken by packets sent meanwhile. | `sbc-link-current-state.md` E4; RRF `SbcInterface.cpp:1333-1337` | Retransmission above the link with different ordering rules than the link. |
| V6 | Codes buffered ahead on several channels fill RRF's single code buffer, so trigger2.g's M112 waits (T3). | T3 | Credit only per connection (one `BufferSpace`), no per-channel limit, no reserved room. |
| V7 | M112 waits behind a long code transfer that noise keeps failing (worst case about 503 ms). | `open-findings.md` "Stop path under SPI faults" | Expedited traffic is carried in the same data phase as bulk traffic. |
| V8 | The abort window of #134 assumes the SPI pipeline order and is wrong over USB (T13). | T13 | Correctness depends on the transport's pipeline. |
| V9 | `_allFilesAborted`, `_allFilesAbortedSent`, "wait until the next transfer brought no resend request" (#134, #158, #161). | T2, T12, T11 | Edge-case catch code; violates R5 (no fragile timing) in spirit. |

## 2. Firmware memory basis for the estimates

Measured by Tim on the lab printer (MB6HC, RRF 3.7.0+1.mp.9-dev.3, CHX configuration), as passed on by the coordinator: "RAM: static 131040, dynamic 105276 (1744 recycled), never used 95868, free sys stack 172", heap 6144/5036/612, bss 126792. About 94 KB have never been used, and every other feature shares that headroom.

What the RRF source says (local checkout, cde4e4fa1):

| Fact | Source |
|---|---|
| 15 G-code channels (HTTP, Telnet, File, USB, Aux, Trigger, Queue, LCD, SBC, Daemon, Aux2, Autopause, File2, Queue2, USB2). DSF has the same 15 plus `Unknown`. | `src/GCodes/GCodeChannel.h:20-22`; DSF `src/DuetAPI/CodeChannel.cs` |
| Three 8,192-byte transfer buffers from the non-cached PBUF pool: rx, tx and, on SAME70 only, `lastTxBuffer` for the fork's sequence guard. | `src/SBC/DataTransfer.cpp:455-478`; `SbcMessageFormats.h:26` |
| One 4,096-byte code ring for all channels, allocated once at start-up. RRF reports one `BufferSpace` for the whole ring. | `SbcMessageFormats.h:47`; `SbcInterface.cpp:76`, `:1374-1377` |
| `BufferedCodeHeader` is 4 bytes with one padding byte. `CodeHeader` is 20 bytes. The smallest buffered code is therefore 24 bytes, so the ring holds at most 170 codes. | `SbcMessageFormats.h:368-385` |
| `PacketHeader` is 8 bytes: request, id, length, resendPacketId. It carries no channel. | `SbcMessageFormats.h:101-107` |
| Macro stack depth up to 10 per channel. | `src/Config/Configuration.h:243` |
| RRF keeps no per-packet copies. The fork's sequence guard keeps one whole previous transfer (`lastTxBuffer`). | `DataTransfer.cpp:475-478`, `:938-968` |
| `EmergencyStop` is executed as soon as its packet is read, without a mutex. | `SbcInterface.cpp:336-337` |

How the estimates are made: protocol state only, held in static arrays of 15 (rounded up to 16 where alignment matters). No heap. Flash and stack use are not estimated. "On the wire" bytes sit in the existing transfer buffers and cost no extra RAM.

**Rule for every pattern below:** the firmware must not get per-message retransmission buffers, and must not get state that grows with the number of messages. DSF keeps doing all resending (it keeps its last 3 transmit buffers today, `sbc-link-current-state.md` E4).

## 3. Field sizes and wrap-around

RFC 1982 §3.2 defines comparison of n-bit serial numbers: s1 < s2 only if the distance is less than 2^(n-1). Pairs exactly 2^(n-1) apart have an undefined order. The number of values that can be outstanding at once must therefore stay below 2^(n-1). AMQP 1.0 uses this arithmetic for all its sequence numbers (Part 2 §2.8.10). SCTP wraps its 16-bit SSN from 65535 to 0 (RFC 9260 §6.5).

| Field | What bounds the outstanding values | Minimum | Suggested |
|---|---|---|---|
| Per-channel code sequence, DSF→RRF | With per-channel credit (P4), at most what fits in the ring: 4096 / 24 = 170 codes. Without credit, more (codes in up to 3 transfers). | 9 bits | 16 bits (2 B). 8 bits only if the credit caps a channel at 127 outstanding codes or fewer, like MQTT Receive Maximum. |
| Per-channel request sequence, RRF→DSF (macro and file requests, closals, pushed messages) | RRF waits for each blocking request (up to 3 s, `SbcMaxRequestTime`, `SbcMessageFormats.h:42`), so few are outstanding per channel. | a few bits | 8 bits (1 B) |
| Per-channel epoch (P6) | With a reset handshake, at most one reset per channel is outstanding. Stale packets can still sit in DSF's 3 retained transmit buffers and in the one-transfer pipeline. | 2–3 bits | 8 bits (1 B) |
| Session | Already exists: 11-bit session status of the extended header (`sbc-link-requirements.md`, capabilities). | – | keep |
| HTTP/2/QUIC-style stream IDs that are never reused (31 bits, RFC 9113 §5.1.1; 62 bits, RFC 9000 §2.1) | Not needed: a fixed channel table plus an epoch gives the same "never reused" identity (P7). | – | – |

## 4. Pattern catalogue

Each entry lists: who uses it (with references), the problem class it removes, how it maps onto v7, the firmware cost, and pitfalls. "Mapping" text is my inference, not a statement from the specs.

### P1. Channel identifier in a fixed packet header (multiplexing)

**Used by**
- HTTP/2: every frame carries a 31-bit stream identifier. Stream 0 is reserved for connection control (RFC 9113 §4.1, §5.1.1). §2: "Streams are largely independent of each other, so a blocked or stalled request or response does not prevent progress on other streams."
- QUIC: stream ID in every STREAM frame (RFC 9000 §2.1, §19.8).
- SCTP: "Every DATA chunk MUST carry a valid stream identifier" (RFC 9260 §6.5).
- AMQP 0-9-1: "Each frame is numbered with a channel number", and per channel "frames run in a strict sequence" (spec PDF §2.3.1). "The channel number is 0 for all frames which are global to the connection" (§4.2.3). §4.3: "Each channel acts as a virtual connection that share a single socket".
- AMQP 1.0: "Each frame is marked with the channel number indicating its parent channel" (Part 2 §2.1.1).
- SSH: channel messages carry the recipient channel (RFC 4254 §5).
- USB: each transaction addresses an endpoint, and a pipe is the association of host software with one endpoint (USB 2.0 §5.3.2).

**Removes:** request-type-specific demultiplexing. Generic per-channel mechanisms (ordering, credit, epoch) become possible.

**v7 mapping:** the channel sits inside request-specific headers (`CodeHeader.Channel`, `CodeChannelHeader`, `ExecuteMacroHeader.Channel`, `AbortFileHeader.Channel`). `PacketHeader` has none (`Shared/PacketHeader.cs`). The link layer therefore cannot apply a per-channel rule without knowing every request type. v8: channel (and epoch, sequence, see P3, P6) in the fixed packet header. Channel 0xFF, or a dedicated value, for link-wide requests (E-stop, reset, credit updates, capabilities), like stream 0 in HTTP/2 and channel 0 in AMQP.

**Firmware RAM:** 0 B of state. On the wire: a 12-byte, 4-byte-aligned header instead of 8 bytes would fit request (2), length (2), channel (1), epoch (1), sequence (2), plus 4 bytes for ack/credit or reserved. This is an illustration, not a format proposal. RRF requires 4-byte alignment of binary codes (`SbcInterface.cpp:427-430`). Fixed fields avoid the varints QUIC uses (RFC 9000 §16), which a DMA-friendly fixed layout does not need.

**CPU:** one table index per packet.

**Pitfalls:** do not let request types keep their own channel field next to the header field. Two writers of the same fact diverge. Keep link-wide requests off the per-channel numbering.

### P2. Ordering is guaranteed per channel, never across channels, except at named barriers

**Used by**
- QUIC: "QUIC does not provide any means of ensuring ordering between bytes on different streams" (RFC 9000 §2). Loss blocks only the streams with data in the lost packet (§13).
- SCTP: messages are delivered in sequence within a stream; "while one stream might be blocked waiting for the next in-sequence user message, delivery from other streams might proceed" (RFC 9260 §1.5.2). The U flag bypasses ordering per message (§6.6).
- SSH: replies to requests of the same channel MUST be in order; "channel requests for distinct channels MAY be replied to out-of-order" (RFC 4254 §4).
- HTTP/2: frame order is significant within a stream (RFC 9113 §5). TCP head-of-line blocking across streams remains (§1).
- AMQP 0-9-1: method order on a channel is stable (§4.7). A peer "SHOULD NOT allow one very busy channel to starve the progress of a less busy channel" (§4.3).
- PCI Express (secondary sources only): an explicit table of which transaction class may, must or must not pass which. Posted requests must be able to pass non-posted requests and completions "to avoid deadlock", and no request may pass a posted write (US 8,495,265 B2, background). Relaxed Ordering and ID-Based Ordering attributes loosen the table per transaction. PCIe 6.1 adds Unordered IO (UIO) "for addressing the limitations of the PCI/PCIe fabric-enforced ordering rules" (Design & Reuse, 2024).

**Removes:** head-of-line blocking between channels, and hidden ordering assumptions between channels.

**v7 mapping:** v7 has no written ordering rule. Packets are processed in transfer order, except resent packets (V5), and DSF's abort windows assume the SPI pipeline order (V8). v8 should write a "may pass / must not pass" table like PCIe's, over v8 message classes: expedited, link control (credit, reset, reset-ack), and per-channel data (codes, replies, macro and file requests, closals). The PCIe lesson is that some passing is **required**: credit updates, reset acks and replies must be able to pass blocked data, or the two sides can deadlock. For example, RRF waits for buffer space while DSF waits for a reply. Cross-channel dependencies that really exist (movement locks, File/File2 sync, a macro on one channel waiting for another) must be named as barriers (P11), not left implicit.

**Firmware RAM:** 0 B for the rule itself. Checking it costs the sequence state of P3.

**Pitfalls:** an ordering table that forbids all passing reintroduces head-of-line blocking. One that allows too much breaks logic code that silently relies on order (the "change logic code as little as possible" goal). Write the table from what the logic needs today.

### P3. Per-channel sequence numbers; "busy" keeps order (NAK and retry the same item, not resend by packet id)

**Used by**
- USB data toggle: a 1-bit sequence per endpoint. "If data cannot be accepted, the receiver must issue NAK and the sequence bits of both the transmitter and receiver remain unchanged" (USB 2.0 §8.6.2). A lost ACK leads to a retransmission that the receiver recognises and ignores (§8.6.4). NAK "does not constitute an error" and does not retire the request (§5.3.2). A pipe carries one transfer at a time, so a NAK'd packet is retried before the next one.
- SCTP SSN per stream, starting at 0 and wrapping (RFC 9260 §6.5).
- QUIC stream offsets: duplicates at the same offset are discarded, and "The data at a given offset MUST NOT change if it is sent multiple times" (RFC 9000 §2.2).
- MQTT: resends must keep the original order (§4.6, [MQTT-4.6.0-1]). The non-normative comment in §4.6 shows the reordering hazard of resending (1,2,3,2,3,4), and that a Receive Maximum of 1 avoids it.
- CAN: an overload frame delays the *next* frame when a receiver is not ready (Bosch CAN 2.0 Part A §3.1.4). Corrupted frames are retransmitted automatically (Part A §2, "Error Signalling and Recovery Time").

**Removes:** reordering by retries (V5) and duplicate processing.

**v7 mapping:** `ResendPacket` names a packet id (`FirmwareRequests/Request.cs`; RRF `SbcInterface.cpp:1333-1337`). DSF sends that packet again in a later transfer, while packets of the same channel sent meanwhile may already have been processed. v8: RRF answers "channel c not accepted from sequence n". DSF goes back to n on channel c only and sends n, n+1, ... again in order, a per-channel go-back-N. Other channels continue. RRF drops channel-c packets with a sequence other than the next expected one. Duplicates are recognised by sequence, as with the USB toggle. RRF keeps no copy: DSF still holds the data.

**Firmware RAM:** next expected sequence per channel, 2 B × 15 = 30 B (u16), plus a 2-byte "NAK pending" bitmask. **About 32 B.**

**CPU:** one compare per packet. RFC 1982 comparison is a subtraction and a sign test.

**Pitfalls:**
- Go-back-N repeats everything after the gap, so it is cheap only if NAKs are rare. P4 makes them rare.
- Keep "not accepted now" (session layer) separate from "corrupted" (link layer). Do not use one number for both. RFC 8260 §1.1 describes how SCTP's TSN serving three purposes caused head-of-line blocking.
- The sequence must be per channel. A per-connection sequence would couple the channels again.

### P4. Credit-based flow control: per channel plus total, with reserved room, and control traffic exempt

**Used by**
- HTTP/2: credit windows per stream and for the connection. Only DATA is flow controlled, which "ensures that important control frames are not blocked by flow control" (RFC 9113 §5.2.1, items 2 and 5). §5.2.2: flow control "addresses cases where the receiver is unable to process data on one stream yet wants to continue to process other streams".
- QUIC: two levels. Stream flow control "prevents a single stream from consuming the entire receive buffer for a connection", connection flow control bounds the total (RFC 9000 §4.1). Limits are absolute and monotonic: "A sender MUST ignore any MAX_STREAM_DATA or MAX_DATA frames that do not increase flow control limits", and a smaller limit "has no effect" (§4.2). Concurrency is capped by MAX_STREAMS (§4.6).
- AMQP 1.0: link credit per link, as delivery-count plus link-credit giving an absolute delivery limit (Part 2 §2.6.7), and a session transfer window (§2.5.6). Stopping a link means setting credit to 0 (§2.6.10).
- AMQP 0-9-1: `basic.qos` prefetch-count, "MUST NOT send more" (XML, class basic). `channel.flow` is called "an emergency procedure" (PDF §3.1.9).
- MQTT 5: Receive Maximum is a send quota. Even with a quota of zero, a peer "MUST continue to process and respond to all other MQTT Control Packets" (§4.9, [MQTT-4.9.0-3]).
- SSH: window per channel only (RFC 4254 §5.2). Channel requests "do not consume window space" (§5.4).
- SCTP, as a counter-example: one receiver window per association (a_rwnd, RFC 9260 §3.3.2; rwnd, §1.3), no per-stream window.
- USB: PING asks whether there is room before data is sent, so bus time is not spent on data that will be NAK'd (USB 2.0 §8.5.1). Part of each frame is reserved for control transfers (§5.5.4).
- PCI Express (secondary): separate credits per transaction type. A type with no credit blocks only that type (US 8,495,265 B2 background). Section numbers not verified.

**Removes:** starvation of one channel by others (V6), and most "cannot store now" refusals, which today become `ResendPacket` (V5).

**v7 mapping:**
- Today RRF reports one `BufferSpace` for the whole ring (`SbcInterface.cpp:1374-1377`). DSF divides it (`LinkInterface.cs:52`, `GetMaxBufferSpacePerChannel`) and since #133 keeps 776 bytes of spare room (`LinkInterface.cs:44`). That is connection-level credit only, plus a DSF-side heuristic.
- v8: RRF advertises a per-channel limit as well as the total. A code is never sent without credit, so "failed to store code" (`SbcInterface.cpp:405-412`) can no longer happen for credited traffic. A reserved minimum for Trigger, Daemon and SBC is the analogue of USB's reserved control bandwidth.
- Resets, reset acks, credit updates, E-stop and replies are exempt from credit, as in HTTP/2, SSH and MQTT.
- The ring can stay one physical buffer. Only the accounting becomes per channel.

**Firmware RAM:** per channel: limit (u16) and consumed (u16) = 4 B × 15 = 60 B, plus a 2-byte total. **About 64 B.** No new buffers.

**CPU:** add and compare per code. Credit updates can ride on existing `CodeBufferUpdate` packets.

**Pitfalls:**
- **Credit leak on cancellation.** QUIC makes both sides agree on the final amount of credit a reset stream consumed (RFC 9000 §4.4, §4.5 "final size"). v8's reset ack must carry what RRF consumed for that channel, or the counters drift after every abort.
- **Relative vs absolute updates.** Incremental updates (HTTP/2 WINDOW_UPDATE, SSH WINDOW_ADJUST) lose credit for good if one is lost or applied twice. Absolute, monotonic limits (QUIC, AMQP 1.0) are harmless when repeated or reordered. v7's `BufferSpace` is an absolute "free now" value that DSF corrects by `BytesReserved` (`LinkService.cs:748-752`). It is correct only if the report point is exactly known relative to the packets in flight. A cumulative limit has no such dependency.
- **Logical vs physical space.** The ring frees space only behind `rxPointer` and compacts with `DefragmentBufferedCodes` (`SbcInterface.cpp:1371`, `:2415-2460`). The sum of advertised per-channel credits must never exceed what the ring can store after fragmentation. I did not check whether the defragmentation guarantees that.
- Do not make DSF wait for a "blocked" signal before granting credit. QUIC forbids that for MAX_STREAMS because the peer would be blocked for a round trip (RFC 9000 §4.6).

### P5. An expedited class that overtakes: absolute class in the header, never refused, no mutex, idempotent

**Used by**
- CAN: "The IDENTIFIER defines a static message priority during bus access". Conflicts are resolved "by bitwise arbitration using the IDENTIFIER ... neither information nor time is lost" (Bosch CAN 2.0 Part A §2, "Priorities", "Arbitration"). The lower the identifier, the higher the priority (TI SLOA101B).
- CANopen: NMT uses CAN-ID 000h, EMCY defaults to 080h + node-ID (CiA 1301 poster). These are the lowest identifiers, so they win arbitration. CiA 301 was not opened.
- USB: "A function may not respond to a SETUP token with either STALL or NAK, and the receiving function must accept the data packet" (USB 2.0 §8.4.6.4). A new Setup aborts the control transfer in progress (§5.5.5). The control Setup stage does not support PING (§8.5.1), so it is never deferred.
- SCTP: the U flag gives per-message unordered delivery. "an implementation MAY choose to place the DATA chunk in an outbound packet that is at the head of the outbound transmission queue" (RFC 9260 §6.6).
- **HTTP/2 PRIORITY, deprecated (cautionary).** The RFC 7540 dependency tree "was not successful": complex, implemented unevenly, ignored by many servers (RFC 9113 §5.3.1, §5.3.2; RFC 9218 §2: relative to other requests, hard to interpret). The replacement uses absolute urgency 0–7, default 3 (RFC 9218 §4.1).
- **TCP urgent data (cautionary).** It is "NOT a mechanism for sending out-of-band data" (RFC 6093 §2.1), yet most stacks deliver the byte out of band by default (§3.1), middleboxes clear URG (§3.4), so "new applications SHOULD NOT employ the TCP urgent mechanism" (§5; RFC 9293 §3.8.5, SHLD-13). Lesson: a priority signal mixed into an ordered stream with loosely defined semantics fails in practice.
- PCI Express posted vs non-posted (secondary): posted requests need no completion and must be able to pass non-posted traffic to avoid deadlock.

**Removes:** the stop path waiting behind bulk traffic (V7, R10), and priority inversion through mutexes.

**v7 mapping:**
- RRF already executes `EmergencyStop` immediately, without a mutex (`SbcInterface.cpp:336-337`). The remaining problem is getting the packet through: it travels in the data phase, which noise can keep failing (V7).
- v8: carry an expedited flag (E-stop, and possibly reset) in the CRC-protected 16-byte transfer header, so it does not depend on the data phase, in the way CAN puts priority into the arbitration field. Expedited packets go first in a transfer, as in SCTP's implementation note.
- Use a few absolute classes (expedited, control, data), not per-channel weights: the RFC 9218 lesson.
- Define exactly what "expedited" overtakes and what it does not. The TCP lesson: in RRF it is acted on when read, before the queued packets of the same transfer.
- Expedited messages must be idempotent, because repeated delivery is likely under noise.

**Firmware RAM:** 0 B if acted on immediately, as `EmergencyStop` is today. Up to 16 B for a one-entry expedited slot if processing is deferred to another task.

**CPU:** negligible.

**Pitfalls:**
- Priority inversion: an expedited message whose handler waits on the buffer mutex is no longer expedited. That is what made InvalidateChannel ask for resends (`SbcInterface.cpp:885-886`, T11).
- Too many priority levels (RFC 9218 §2).
- Expedited traffic used for non-urgent purposes, which erodes the guarantee.
- One E-stop bit in the header must not be the only copy. Keep the packet too, so that a header CRC failure plus retry still delivers it.

### P6. Cancellation as a per-stream state transition with an explicit handshake; late frames discarded by identity, never by timing

**Used by**
- HTTP/2 RST_STREAM: "after sending the RST_STREAM, the sending endpoint MUST be prepared to receive and process additional frames sent on the stream that might have been sent by the peer prior to the arrival of the RST_STREAM" (RFC 9113 §6.4). In the closed state it "MUST minimally process and then discard any frames it receives" and may detect late frames by signals the peer acknowledged (SETTINGS ack, PING); "Endpoints SHOULD NOT use timers for this purpose" (§5.1). REFUSED_STREAM means no processing happened (§8.7).
- QUIC: RESET_STREAM terminates the sending part, STOP_SENDING asks the peer to reset (RFC 9000 §19.4, §19.5). An endpoint that receives STOP_SENDING "MUST send a RESET_STREAM frame" in the Ready or Send state (§3.5). On RESET_STREAM the receiver "will tear down state for the matching stream and ignore further data arriving on that stream" (§4.4). A repeated RESET_STREAM "MUST NOT change" (§13.3).
- AMQP 0-9-1: "hand-shake all closure so that we close only after we are sure the other party is aware" (PDF §2.3.7). After sending channel.close, "any received methods except Close and Close-OK MUST be discarded" (XML, class channel, method close). After basic.cancel, "The client may receive an arbitrary number of messages in between sending the cancel method and receiving the cancel-ok reply" (XML, basic.cancel). Effects of a request "MUST NOT be visible on the channel before the response method, and MUST be visible thereafter" (PDF §4.6).
- AMQP 1.0: closing a link is a detach with closed=true answered by the peer's detach (Part 2 §2.6.6). Stopping a link sets credit 0 with echo, to learn when in-flight transfers have stopped (§2.6.10).
- SSH: a channel is closed once a party has both sent and received CHANNEL_CLOSE; then the number may be reused (RFC 4254 §5.3).
- USB: a STALL or 3 errors retire the IRP and all outstanding IRPs of the pipe, and no further IRPs are accepted until software clears the condition (USB 2.0 §5.3.2). Recovery goes through the control pipe and resets the data toggle on both sides (§5.8.5; ClearFeature(ENDPOINT_HALT), §9.4.5).
- SCTP stream reset: the request carries "Sender's Last Assigned TSN", the exact cut point (RFC 6525 §4.1). The receiver defers the reset until everything up to that point has arrived (§5.2.2, E2), answers "In progress" meanwhile (§4.4, result 6), and answers a retransmitted request with "the same RE-CONFIG chunk ... as it was earlier" (§5.2.1). There is a result "Request already in progress" (§4.4, result 4).
- gRPC: RST_STREAM CANCEL maps to call cancellation (PROTOCOL-HTTP2.md, "Errors"). The server "should stop any ongoing computation", but the library cannot interrupt the handler (grpc.io, Cancellation guide).

**Removes:** every "crossing" case (V1 crossing part, V2 T2, V3, V4), the timing windows (V9) and the dependence on the transport's pipeline (V8).

**v7 mapping (sketch):**
- DSF's InvalidateChannel becomes RESET(c, new epoch e+1, last sequence sent in epoch e).
- RRF answers RESET_ACK(c, e+1, last sequence consumed, credit consumed). This is the fence of AMQP 0-9-1 §4.6.
- Between RESET and RESET_ACK, DSF discards every channel-c packet from RRF tagged with epoch e: macro requests, closals, replies, pushed code replies. After processing RESET, RRF discards every channel-c packet from DSF tagged e, for example codes or a MacroCompleted already in flight. Nothing depends on which transfer carried what, so SPI and USB behave the same (V8).
- RRF's AbortFile is the STOP_SENDING analogue: a request that DSF answers with RESET. That leaves one writer of the epoch, DSF.
- RRF never asks for a RESET to be resent (USB SETUP rule). It records it in a one-entry per-channel slot without taking the buffer mutex. The main task applies it, and the ack goes out once it is applied.
- A repeated RESET with the same epoch is a no-op that repeats the same ack (RFC 6525 §5.2.1, QUIC §13.3). That removes T11.
- **What epochs do not fix:** state the firmware keeps across a reset on purpose or by accident. T1 is `requestedMacroFile` left set, so RRF sends the request after the reset, tagged e+1. Epochs only remove such cases if request state lives on the (channel, epoch) object and the reset destroys it, as QUIC "tears down state for the matching stream". This matches CLAUDE.md, "keep state on the object whose lifetime it shares".

**Firmware RAM:** per channel: epoch (u8), state (open / reset pending / reset applied, u8), pending-reset slot with requested epoch and last sequence (3 B), last ack result for idempotent replay (2 B) = about 7 B × 15 = 105 B. **About 112 B.**

**CPU:** one compare per packet. The reset itself costs what `AbortFile` costs today.

**Pitfalls:**
- Closing without a handshake (AMQP 0-9-1 §2.3.7 rationale).
- Deciding by timer when late frames have stopped (RFC 9113 §5.1, "SHOULD NOT"). That is what #134 and #161 approximate with "the next transfer".
- Side effects before the request is accepted. HTTP/2 §8.7: a server "MUST NOT indicate that a stream has not been processed unless it can guarantee that fact"; v7's InvalidateChannel resolves the macro wait and then asks for a resend (V4).
- Forgetting the credit accounting of the reset stream (P4).
- A second reset while one is pending: define it (merge, or "already in progress" like RFC 6525 result 4).
- Replies of codes that ran before the reset: define that they belong to epoch e and are discarded, and DSF completes those codes itself as cancelled (gRPC: the handler may not stop).

### P7. Stream identity is never reused while frames can be in flight; bounded variant (channel, epoch)

**Used by**
- Never-reuse family: HTTP/2 "Stream identifiers cannot be reused", new IDs must be greater than all earlier ones, and exhaustion means a new connection (RFC 9113 §5.1.1). QUIC "MUST NOT reuse a stream ID within a connection" (RFC 9000 §2.1) and never reuses a packet number (§12.3). Monotonic packet numbers remove retransmission ambiguity (RFC 9002 §4.2).
- Reuse-after-handshake family: SSH reuses a channel number after both CLOSE messages (RFC 4254 §5.3). AMQP 1.0 detach "unmaps the handle and makes it available for use by other links" (Part 2 §2.7.7). MQTT reuses a Packet Identifier only after the sender has processed the acknowledgement (§2.2.1). AMQP 0-9-1 forbids opening an already open channel (XML, channel.open, rule "state").

**Removes:** a late frame of an old use being taken for the new use of the same number (V1, V2, V3).

**v7 mapping:** v7 reuses "channel c" instantly after an abort, with no handshake and no generation. A fixed table of 15 channels (static memory) rules out the never-reuse family's growing ID space, so v8 should take the reuse-after-handshake family: (channel, epoch) is the stream identity. The epoch increments on every reset and the RESET/RESET_ACK handshake of P6 bounds the epochs in flight. Even 1 bit would work in theory, like the USB toggle (§8.6), but 8 bits leave room for stale packets in DSF's 3 retained transmit buffers (section 3).

**Firmware RAM:** included in P6 (1 B per channel).

**Pitfalls:**
- Do not copy HTTP/2's growing stream IDs with closed-stream bookkeeping: that state grows with the number of streams.
- Do not reset the epoch to 0 on reconnect without also changing the session. The link session status covers that (P10).

### P8. Request/response correlation by an echoed identifier; unmatched replies are discarded

**Used by**
- JSON-RPC 2.0: "The Server MUST reply with the same value in the Response object" (§4). A request without id is a notification, and the server "MUST NOT reply" (§4.1). Batch responses "MAY be returned in any order", and the client matches "based on the id member" (§6).
- Modbus TCP: the transaction identifier is copied into the response (Implementation Guide V1.0b §3.1.3). With several transactions on one connection it "must be used to uniquely identify the matching requests and responses" (§4.2.1, item 4). "If the Transaction Identifier doesn't refer to any MODBUS pending transaction, the response must be discarded" (§4.4.1.3).
- USB Mass Storage Bulk-Only: the device echoes dCBWTag in dCSWTag, which "positively associates a CSW with the corresponding CBW" (BOT 1.0 §5.1). The host treats a CSW as valid only if the tags match (§6.3.1), although only one command may be outstanding (§3.4).
- MQTT 5: acks carry the Packet Identifier of the PUBLISH (§2.2.1). Correlation Data for request/response (§4.10).
- AMQP 1.0: delivery-tag per delivery (Part 2 §2.6.12), correlation-id in the message properties (Part 3 §3.2.4).
- gRPC: "We will use HTTP2 stream-ids as call identifiers" (PROTOCOL-HTTP2.md, "Stream Identification").
- Counter-example, order-based correlation: SSH global requests carry no ID and correlate by order (RFC 4254 §4). That works only on a reliable, ordered transport where every request gets exactly one reply and nothing is cancelled in between. v7 has the same design and breaks on the last condition.

**Removes:** a stale reply completing the next code (V1), and pushed messages taken for code replies (T4).

**v7 mapping:** two options.
- (a) Keep order-based correlation and rely on P6. With epochs, no reply of a cancelled code can cross into the new epoch. That costs 0 B more, but needs RRF to give exactly one reply per code within an epoch.
- (b) Echo the code's sequence number (P3) in its reply. DSF matches by (channel, epoch, sequence) and discards replies with no pending code, as Modbus does, instead of holding them back for the next code. Pushed messages carry a "no request" marker, as JSON-RPC notifications do.
- Option (b) also catches RRF replying twice or not at all. With nested macros, the reply of an M98 comes after the replies of its macro's codes, so RRF has to keep the sequence of the code that opened each stack level.

**Firmware RAM:**
- (a) 0 B.
- (b) The current code's sequence per channel and per stack level: 2 B × 15 × 10 (MaxStackDepth) = 300 B worst case, if stored in the per-level machine state. Typical use is far less, but the static worst case counts. Codes waiting in the ring could carry an 8-bit sequence in the spare padding byte of `BufferedCodeHeader` at no cost (`SbcMessageFormats.h:371`). A 16-bit one would grow the header to 8 B and reduce ring capacity, not static RAM.

**CPU:** negligible.

**Pitfalls:**
- Variable-length IDs (JSON-RPC allows strings) do not suit a static firmware. Use fixed small integers.
- IDs must come from one counter per (channel, epoch), not per request type, or replies of different types collide.
- A dropped reply must be logged as a protocol fault, not silently consumed.

### P9. One retransmission layer; refusals mean "not processed"; re-sent content is identical

**Used by**
- MQTT: resend unacknowledged PUBLISH only on reconnect with a session; "Clients and Servers MUST NOT resend messages at any other time" (§4.4, [MQTT-4.4.0-1]). Reliability otherwise comes from TCP.
- gRPC: "There is no mechanism for duplicate suppression as it is not necessary"; "Calls that cannot be proven to have started will not be retried" (PROTOCOL-HTTP2.md, "Idempotency and Retries").
- HTTP/2: GOAWAY and REFUSED_STREAM guarantee "that a request has not been processed", so such requests may be retried even if not idempotent (RFC 9113 §8.7).
- QUIC: lost packets are not resent whole; "the information that might be carried in frames is sent again in new frames" (RFC 9000 §13.3). Stream data at an offset never changes (§2.2).
- Modbus: exception 05 ACKNOWLEDGE ("accepted ... long duration") and 06 SERVER DEVICE BUSY ("The client should retransmit the message later"), both specified for programming commands only (Application Protocol V1.1b3 §7). Vendor material uses 06 more widely; see section 8.
- USB NAK as "busy" that keeps sequence and order (USB 2.0 §5.3.2, §8.6.2). CAN overload frame delays rather than rejects (Bosch 2.0 Part A §3.1.4).

**Removes:** two retransmission mechanisms with different ordering rules (V5), and refusals that have already had side effects (V4).

**v7 mapping:**
- v7 retransmits at the link (header and data retries, the fork's sequence guard resending whole transfers) and again above it (`ResendPacket` by packet id, served from DSF's last 3 transmit buffers).
- v8: the link delivers each transfer exactly once, in order. With the extended header and sequence guard this is nearly the case already (`sbc-link-requirements.md`, implementation status). The session layer has no "resend packet id".
- "Busy" becomes either no credit (P4), or a per-channel NAK with go-back-N (P3), which keeps order.
- A refusal is allowed only if nothing was done (HTTP/2 §8.7 rule).
- Content sent again must be identical (QUIC §2.2, §13.3).

**Firmware RAM:** none added. The `ResendPacket` write path and the `resendPacketId` field go away. DSF's 3 retained transmit buffers can shrink to what the link's sequence guard needs. That is DSF RAM, not firmware.

**CPU:** less on both sides.

**Pitfalls:**
- Do not move retransmission into the firmware. A firmware-side retransmission queue (QUIC and SCTP senders keep one) is unsuitable with static RAM, and RRF keeps none today.
- "Exactly once" exists only between two endpoints that keep state across the failure. After a real restart (R6), pending work is discarded on purpose. Do not promise exactly-once across restarts.

### P10. Session epoch and "what was processed" on reset

**Used by**
- TCP: "New instances of a connection will be referred to as incarnations" (RFC 9293 §3.4.1). Old segments are kept apart by ISN selection and by a "quiet time" of one MSL (2 minutes) after losing sequence state (§3.4.2). That is a timing rule, so it is cautionary under R5.
- QUIC: connection IDs (RFC 9000 §5.1); a stateless reset for a peer that lost state (§10.3); draining (§10.2.2); packet numbers never reused (§12.3).
- HTTP/2 GOAWAY carries the last stream ID "that was or might be processed". Higher streams "can be safely retried", and the value "MUST NOT increase" in later GOAWAYs (RFC 9113 §6.8).
- SCTP: the Verification Tag lets a receiver verify that a packet "is not an old or stale packet from a previous association" (RFC 9260 §1.3). Mismatches are silently discarded (§8.5, §1.5.6). Restart detection is shown in §5.2.4.1.
- USB: data toggles are reinitialised on configuration events (USB 2.0 §8.5.2, referring to §9.1.1.5 and §9.4.5).

**Removes:** confusion of the old session's traffic with the new one, and unclear state of work that was in flight across a reset.

**v7 mapping:**
- The link already has a session status in the extended header: a restart shows as status 0 at the first header (`sbc-link-requirements.md`, capabilities).
- What v8 could add is GOAWAY semantics for *planned* resets (M999, firmware update, "abort all"): RRF reports, per channel, the last sequence it processed, so DSF knows which codes ran.
- After an unplanned restart, R6 applies: DSF discards everything and RRF stops (category 0). GOAWAY information would only feed diagnostics there.
- Per-channel epochs (P6) should be reset together with the session, never on their own.

**Firmware RAM:** session id already exists. "Last processed" is the P3 counter. **0–4 B.**

**Pitfalls:**
- Timing-based separation of incarnations (TCP quiet time) contradicts R5.
- A session id that can repeat right after a restart. v7's 16-bit sequence wrap hid 6 of 128 restarts (`sbc-link-findings-upstream.md` F6).

### P11. Barriers between channels and fences within a channel are explicit messages

**Used by**
- AMQP 0-9-1 §4.6: effects of a synchronous request are invisible on the channel before the response, visible after it.
- HTTP/2 §5.1: SETTINGS acknowledgement or PING as the signal that the peer has seen a state change, instead of a timer.
- AMQP 1.0 §2.6.10: flow with echo to learn when a link has quiesced.
- SCTP stream reset deferred until the cut point (RFC 6525 §5.2.2, E2).
- PCI Express (secondary): no request may pass a posted write. That gives producer/consumer ordering for flag-after-data patterns.

**Removes:** implicit cross-channel dependencies and "the next transfer will have shown it" reasoning (V9).

**v7 mapping:** `LockAllMovementSystemsAndWaitForStandstill`/`Unlock`, `MacroStarted`/`MacroCompleted`, `SetVariable`, `EvaluateExpression` and InvalidateChannel each act on channel state at a point in the code sequence. v8 can define each as a fence at (channel, epoch, sequence). The response then marks the point after which the effect is visible, and both sides agree on it without looking at transfer numbers.

**Firmware RAM:** 0 B beyond P3 and P6.

**Pitfalls:** a fence that waits for another channel deadlocks if that channel is blocked by credit. Barrier responses must be exempt from credit (P4) and allowed to pass data (P2).

### P12. Layering: link, session/channel, application, each owning its state

**Used by**
- AMQP 1.0 Part 2 §2.1.1: the connection is an ordered, reliable frame sequence ("if the nth frame arrives, all frames prior to n MUST also have arrived") multiplexed into channels. Sessions are a "bidirectional, sequential conversation" with a transfer window (§2.5.6). Links carry credit and delivery state (§2.6.7, §2.6.12).
- HTTP/2 runs over TCP as "a connection-oriented application-layer protocol" (RFC 9113 §2). Its flow control is "hop by hop" (§5.2.1, item 1).
- QUIC keeps packet numbers (transport, never reused, §12.3), stream offsets (§2.2) and stream IDs (§2.1) apart.
- MQTT leaves reliability to TCP and retransmits only at session resumption (§4.4). AMQP 0-9-1 takes method order from TCP (§4.7).
- USB keeps the transaction-level toggle (Chapter 8) apart from class-level command tags (BOT §5.1).
- Anti-pattern: SCTP's TSN carried three meanings, which caused head-of-line blocking that RFC 8260 had to fix with I-DATA (§1.1).

**Removes:** the maintainer's hypothesis, "a wrong separation of protocol and channels", is a layering problem. In v7:
- Session concerns sit in the link header: `PacketHeader.Id` and `ResendPacketId`.
- Session concerns sit in logic code: InvalidateChannel takes the G-code buffer mutex inside the packet loop (`SbcInterface.cpp:868-890`). The abort windows live in DSF's channel `Processor`.
- Channel identity sits in per-request headers (P1).

**v8 split (proposal for discussion):**

| Layer | Owns | State |
|---|---|---|
| Link (exists, extended header) | Transfer framing, CRC/ECC, transfer sequence and phase guards, link retransmission, session status, expedited flag in the transfer header | Per link |
| Session/channel (new, small) | Channel id, (channel, epoch) identity, per-channel sequence, credit, NAK/go-back-N, RESET/RESET_ACK, fences, discarding stale epochs | Static table of 15 channels |
| Application (unchanged as far as possible) | G-code execution, macro stack, file requests, object model | Existing `GCodeBuffer` / `Processor` |

**Firmware RAM:** 0 B by itself. It moves code and state rather than adding it.

**Pitfalls:**
- A session layer that calls into logic code under a logic mutex brings back V4. Hand over through the one-entry slots of P5 and P6.
- Two writers of the same state (channel in header and payload, epoch on both sides) diverge. Give each piece of state one writer.

## 5. Firmware RAM summary

| Pattern | Firmware RAM (static) | Basis |
|---|---|---|
| P1 channel in header | 0 B (+4 B per packet on the wire) | Header fields only |
| P2 ordering table | 0 B | Rule only |
| P3 per-channel sequence, NAK | ≈ 32 B | u16 × 15 + 2 B bitmask |
| P4 per-channel credit | ≈ 64 B | (u16 limit + u16 consumed) × 15 + total |
| P5 expedited class | 0–16 B | One deferred slot if needed |
| P6 reset handshake with epoch | ≈ 112 B | about 7 B × 15 |
| P7 (channel, epoch) identity | in P6 | – |
| P8 echoed reply id, option (b) | ≤ 300 B worst case, (a) 0 B | u16 × 15 channels × 10 stack levels |
| P9 single retransmission layer | ≤ 0 B (removes state) | – |
| P10 session/last processed | 0–4 B | Reuses P3 |
| P11 fences | 0 B | Reuses P3, P6 |
| P12 layering | 0 B | – |
| **Total P1–P7, P9–P12** | **≈ 210–230 B** | **≈ 0.25 % of the 95,868 B never used** |
| **with P8 (b)** | **≈ 510–530 B** | ≈ 0.55 % |

None of the patterns needs dynamic allocation in the protocol path or a per-message buffer on the firmware side.

## 6. Unsuitable on the firmware side, and bounded variants

| From | Why unsuitable as is | Bounded variant |
|---|---|---|
| HTTP/2 / QUIC dynamic streams with growing IDs, closed-stream tracking, final-size memory for closed streams (RFC 9000 §4.5 notes the "significant state commitment") | State grows with the number of streams | Fixed table of 15 channels × epoch (P7) |
| QUIC/SCTP sender retransmission queues | Per-message buffers on the sender | DSF is the only side that retransmits. RRF NAKs (P3, P9). |
| AMQP 1.0 unsettled maps and delivery resumption (§2.6.12, §2.6.13) | State per delivery | Per-channel "last consumed sequence" in the reset ack and after a session change (P6, P10) |
| MQTT QoS 2 receiver state per Packet Identifier until PUBREL (§4.3.3) | State per in-flight message | Per-channel sequence makes duplicates detectable with one counter (P3) |
| RFC 7540 priority trees | Complex, deprecated (RFC 9113 §5.3.2) | 2–3 absolute classes (P5) |
| TCP urgent pointer | Ambiguous, discouraged (RFC 6093 §5) | Expedited flag in the CRC-protected transfer header with defined semantics (P5) |
| JSON-RPC string IDs | Variable length | Fixed u8/u16 sequence (P8) |
| Incremental credit updates (HTTP/2 WINDOW_UPDATE, SSH WINDOW_ADJUST) | Robust only on a lossless, exactly-once path | Absolute, monotonic limits (QUIC §4.2, AMQP 1.0 §2.6.7) |
| Varint fields (QUIC §16) | Not a RAM problem, but variable offsets complicate 4-byte-aligned DMA buffers | Fixed 4-byte-aligned header |

## 7. Ranked recommendation for v8

1. **P6 + P7: per-channel reset with epoch and handshake, stale traffic discarded by identity** (≈ 112 B). This removes the class behind T2, T10 (crossing part), T11, T12 and T13, and the timing windows of #134, #158 and #161. Pair it with the rule that per-request state lives on the (channel, epoch) object, which is what prevents T1-type cases.
2. **P9 + P3: one retransmission layer, per-channel sequence, NAK/go-back-N instead of `ResendPacket`** (≈ 32 B, and removes code). This removes reordering by resends (E4) and the "refused after a side effect" class (T11).
3. **P4: per-channel credit plus total, reserved room, control traffic exempt** (≈ 64 B). This removes T3 and most of the reasons RRF refuses packets today. Carry the consumed credit in the reset ack.
4. **P5: expedited class in the CRC-protected transfer header, never refused, no mutex** (0–16 B). This targets the remaining R10 gap (stop waiting behind a failing data phase).
5. **P1 + P12: channel in a fixed packet header, and a thin session layer between link and logic** (0 B). This enables 1–4 and matches the maintainer's hypothesis. It is also what keeps logic code changes small.
6. **P11 and P2: written fences and a may-pass table** (0 B). This is documentation that makes the remaining cross-channel dependencies explicit.
7. **P8 (b): echoed sequence in replies** (≤ 300 B). This is hardening on top of 1. Option (a), order plus epoch, costs nothing and may be enough.
8. **P10: GOAWAY-like "last processed" for planned resets** (0–4 B). This is only useful for M999 and firmware update diagnostics, because R6 discards everything on a real restart.

Combined cost of 1–6: roughly 210–230 B of static firmware RAM and a 4-byte larger packet header.

## 8. Not verified, and gaps

- **PCI Express:** the Base Specification was not opened (members only). The ordering table, the "must pass" rule for posted requests, the flow-control credit types, Relaxed Ordering and ID-Based Ordering, the tag and completion-timeout rules, and "unexpected completion" handling come from secondary sources (patent US 8,495,265 B2 background, Design & Reuse article on UIO) and general knowledge. No PCIe section or table numbers are given. Intel's FPGA user guide pages that reproduce the table no longer resolve. Secondary sources disagree on completion-to-completion passing.
- **CANopen:** CiA 301 was not opened. NMT 000h and EMCY 080h + node-ID come from the CiA 1301 (CANopen FD) poster. That they win arbitration follows from Bosch CAN 2.0 Part A §2 plus "lower ID = higher priority" (TI SLOA101B). No CiA text states this directly.
- **CAN:** the Bosch CAN 2.0 PDF is a 1995 re-typeset copy hosted at esd.cs.ucr.edu, not Bosch's own site. ISO 11898-1 was not opened. CAN controller Tx FIFO vs priority queue behaviour (priority inversion inside one node) is not covered.
- **Modbus exception 06:** the spec restricts 05 and 06 to programming commands (§7). Wider use as a general "busy" comes from vendor documents only. A forum post says no function code lists 06 among its allowed exceptions; not checked.
- **gRPC:** the Cancellation guide on grpc.io has no version. Its quotes came through the fetch tool's extraction. PROTOCOL-HTTP2.md is pinned to commit cf61c7d62a.
- **AMQP 0-9-1:** the method-level rules (channel.close, basic.cancel, channel.open, delivery-tag) come from `amqp0-9-1.xml` and are cited by class and method, not by section number.
- **SCTP:** only the introduction of RFC 8260 (§1.1) and the section list (§3) were read. RFC 3758 (FORWARD TSN, abandoning messages) is listed but not used.
- **USB:** USB 3.x bulk streams and the UAS protocol (stream IDs and tags) were not opened.
- **RRF line numbers** come from the local checkout at cde4e4fa1 (3.7.0+1.mp.8 merge). The lab printer runs mp.9-dev.3, and the design documents cite other commits. Line numbers may differ.
- **RAM figures:** the M122 numbers were passed on by the coordinator; I did not measure them. The estimates cover protocol state only, not flash, stack or `GCodeMachineState` allocation. For P8 (b), whether the per-level sequence fits into existing machine-state padding was not checked.
- **Not analysed:**
  - Whether `DefragmentBufferedCodes` guarantees that per-channel logical credit is always physically storable (P4).
  - Whether v7 RRF processes the non-resent packets of a transfer strictly in order. Taken from the design documents, which cite `SbcInterface.cpp:258-259, 301-305`.
  - Which v7 cross-channel dependencies exist in the logic code. P2 and P11 assume they can be listed.
- **Mapping sections:** the v8 mapping and the illustrative header are my inference from the patterns, not statements of the cited specs.
