# RtMidi native libraries

Source: [RtMidi 6.0.0](https://github.com/thestk/rtmidi/tree/1e5b49925aa60065db52de44c366d446a902547b), commit `1e5b49925aa60065db52de44c366d446a902547b`.
`LICENSE` is copied unchanged from that checkout. The binaries are unmodified build outputs from this source.

| Output | SHA-256 |
| --- | --- |
| `rtmidi.dll` (Windows x64) | `0144c203cc26cc4dac06a51a013031d30e65212489951305a4d152a93fdda890` |
| `librtmidi.so` (Linux x64) | `22c564d18cc49e1c9b860f97fc184c18e810e37a551b1c33351d809bdf8f70c0` |

## Windows build

Visual Studio 2022, MSVC 14.38.33130, x64 developer shell:

```text
cl /nologo /O2 /MT /EHsc /GR /LD /DUNICODE /D_UNICODE /D__WINDOWS_MM__ /DRTMIDI_EXPORT /DRTMIDI_DO_NOT_ENSURE_UNIQUE_PORTNAMES RtMidi.cpp rtmidi_c.cpp winmm.lib /Fe:rtmidi.dll
```

`dumpbin /dependents` reports only `KERNEL32.dll` and `WINMM.dll`. `/MT` links the C++ runtime statically. `UNICODE` and `_UNICODE` preserve Unicode product names; `RTMIDI_DO_NOT_ENSURE_UNIQUE_PORTNAMES` disables the appended port index to retain existing Windows device-name matching.

## Linux build

Ubuntu 22.04 image `ubuntu:22.04@sha256:5ec03bb3441e8b0bf3b4f9cd4629a1ae763010dc3035bb8da3ae6cf026486401`, g++ 11.4.0, with `libasound2-dev`:

```text
g++ -O2 -fPIC -shared -std=c++11 -D__LINUX_ALSA__ -DRTMIDI_EXPORT RtMidi.cpp rtmidi_c.cpp -lasound -pthread -Wl,-soname,librtmidi.so -o librtmidi.so
```

`readelf -d` reports `libasound.so.2`, `libstdc++.so.6`, `libgcc_s.so.1`, and `libc.so.6`; no JACK dependency. Highest required symbol versions: `GLIBC_2.34` and `GLIBCXX_3.4.21`. ALSA output requires the host's sequencer (`snd-seq`); enumerating without it can fail. The Linux binary is vendored for later Linux app composition and is not copied by the current Windows target.

## Rebuilding

Use `build/rtmidi/build-windows.ps1 -SourceCheckout PATH` in the specified x64 developer PowerShell, or `bash build/rtmidi/build-linux.sh PATH` on Ubuntu 22.04 with `g++`, `libasound2-dev`, `git`, and `binutils`. Both scripts require the exact source commit with no tracked modifications, perform no downloads, and default to an ignored `bin/rtmidi-build` output directory. They print dependencies, exports and output hashes for verification before replacing vendored files. These recipes reproduce the compiler inputs and backend selection; different toolchain/package revisions or PE timestamps can change binary hashes.

## Managed C API contract

`FitOSC.Platform.Midi` binds `rtmidi_c.h` using Cdecl, non-null UTF-8 client/port strings, unsigned 32-bit port indices/counts and signed 32-bit name-buffer/send lengths. The sequential wrapper layout uses two pointers, a one-byte C `bool`, then a pointer with native alignment. Only `ok` is read, using the marshaler's computed field offset. In 6.0.0, `msg` may point to destroyed exception storage and must never be dereferenced; thrown managed errors identify the operation and direct callers to native stderr diagnostics.

The `ok` flag is sticky, so enumeration and opening each create a fresh wrapper. Every operation checks it; freeing invokes the backend destructor and closes the port. A single adapter lock serializes enumeration, opening, sending, closing, and disposal. Native ALSA send warnings are printed to stderr without a failure return and cannot be detected through this version's C API. ALSA port names contain client/port numbers that may change across reconnects or boots. Windows name parity and physical MIDI delivery still require runtime acceptance.
