# How Seamlet works

Seamlet is a native Windows controller and Mac receiver.
The app bundles are `Seamlet.app` and `Seamlet.exe`.

## Input and pairing

Windows captures mouse input with a low-level hook and queues messages outside the
hook's socket/encryption work. Mac uses AppKit and Core Graphics to inject events.
The receiver requires macOS Accessibility permission.

The control connection uses TCP 24872 with newline-delimited encrypted frames capped
at 8,192 bytes. Each peer supplies a fresh 32-byte nonce. Directional keys are derived
using HMAC-SHA256; AES-GCM and ordered counters authenticate messages and reject
replays and out-of-order frames.

User-chosen codes are trimmed of surrounding ASCII whitespace, normalized to Unicode
NFC, and stretched with PBKDF2-HMAC-SHA256 (600,000 iterations, 32-byte output,
application-specific salt). Codes are not persisted. Key stretching does not make
short, guessable codes resistant to all guessing attacks.

## Discovery

Windows broadcasts discovery requests to UDP 24873; Mac replies with its name and
IPv4 endpoint. A scan lasts three seconds and deduplicates responses by address.
Discovery does not broadcast the pairing code or establish an authenticated session.
The control connection still requires pairing.

## Screen entry and return

The controller detects edge crossings, including mouse samples that overshoot a
boundary. On entry, the receiver warps the cursor directly to the edge before posting
its normal movement event. Display geometry is cached, and Accessibility checks run
at pairing and on a 500 ms timer rather than for every mouse sample.

Returning to Windows places the cursor 12 pixels inside the screen. There is no timed
re-entry cooldown. While paired, the Mac holds an activity assertion to reduce App Nap
interference without preventing system sleep. TCP connections use no-delay options.

Windows displays the entry acknowledgement round-trip time. It includes queueing,
transport, and peer processing, not physical mouse-capture or display-render latency.
Actual handoff responsiveness must be measured on the two computers.

A heartbeat detects a stalled peer. Ctrl + Alt + Esc returns input to Windows.
Disconnecting releases held Mac mouse buttons. A held-button drag remains on its
original computer until released.

## Clipboard transport

A separate TCP service on Mac port 24874 handles clipboard data on transfer workers.
Each paired control session receives a random 32-byte service secret through its
already encrypted control connection. The service stops when that session disconnects.
Windows initiates uploads and downloads using authenticated encrypted connections.
The separate connection keeps large transfers off the mouse control stream.

The apps watch clipboard changes after connecting. File selections take priority over
accompanying text representations. A Mac copy creates a current offer ID; a newer copy
invalidates the previous offer. Windows initiates a transfer when it receives that
offer. Windows copies initiate uploads. Text support is negotiated, allowing older
peers to keep using compatible file-sharing behavior.

Received clipboard changes are marked as seen to avoid sending them back. Before
publishing an incoming transfer, each app checks that the local clipboard has not
changed. A new local copy takes priority. Windows replaces old contents with a
non-pasteable marker while downloading and retries a busy clipboard asynchronously.

### Plain text

Text uses bounded 32 KB chunks and a 1 MB UTF-8 size limit. The receiver validates
length, message order, completion, and UTF-8 before publishing Unicode text. Emoji,
multiline content, and code snippets are supported; rich formatting and images are not.

### Files

Selections contain 1–100 regular files with a combined 1 GB limit. The receiver checks
names, sizes, order, SHA-256 digests, and completion before putting file URLs on the Mac
pasteboard or a file-drop list on the Windows clipboard. Unsafe names, duplicate names,
and symbolic links are rejected. Files are transferred as bytes and never executed.

Each transfer stages files in a unique directory under the OS temporary folder's
`Seamlet-Files` directory. Interrupted or invalid partial transfers are removed.
Finished copies remain there so repeated pastes work after disconnecting. Subsequent
connections clean up copies older than 24 hours, except those referenced by the current
clipboard. Source files are not removed or moved.

## Native presentation

Mac uses AppKit; Windows uses WinForms. The screen diagram reflects connection state
and the configured arrangement. A roughly 460 ms edge bubble appears on the departure
screen. Windows renders it on a separate UI thread; Mac uses Core Animation and respects
Reduce Motion. The animation does not intercept clicks, take focus, or deliberately
delay sending handoff messages.

## Validation boundaries

`bash scripts/test.sh` exercises both language implementations over temporary localhost
sockets, including discovery and clipboard transfers. A private named Mac pasteboard
verifies clipboard serialization without touching the user's general clipboard.

These tests do not replace native Windows UI testing, Finder/Explorer paste testing,
Wi-Fi/firewall testing, or physical latency measurement. The Mac build is locally
ad-hoc signed and is not notarized; Windows publishing does not code-sign the EXE.
