# GbcDN

A Game Boy Color emulator written in C# (.NET 10), with a small SDL3 frontend.

The emulation core is split into one class per hardware component (`Cpu`, `Ppu`, `Apu`,
`Timer`, `Input`, `Rtc`), all driven by the CPU's cycle counter. The frontend connects
them to an SDL3 window and audio device and paces emulation to ~59.7 frames per second.

## Features

- **CPU** — SM83 instruction set, interrupts/HALT, MBC1/2/3/5 cartridge mappers,
  battery-backed cartridge RAM (`.ram`).
- **Real-time clock** — MBC3 RTC registers with latching, halt flag and day counter,
  persisted to `.rtc` next to the ROM.
- **PPU** — Game Boy Color rendering: 15-bit BGR555 palettes, VRAM banking,
  8×8 and 8×16 sprites (X/Y flip, OAM priority, BG-to-OAM priority), window layer,
  OAM DMA, and CGB GDMA/HDMA VRAM transfers.
- **APU** — all four channels (square + sweep, square, wave, noise), frame sequencer,
  envelopes/length counters, 2× oversampling and a DC-blocking high-pass filter.
  Output is 44.1 kHz, 16-bit stereo in a thread-safe ring buffer.
- **Audio playback** — SDL3 stream with dynamic rate control (the emulator and audio
  device clocks stay locked without dropped/duplicated samples).
- **Input** — keyboard and gamepad via SDL3.
- **Diagnostics** — a sampled per-component timing report is written on clean exit.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- **Windows** for the included frontend: it references `SDL3-CS.Windows`. The core 
  library itself is platform-agnostic; another OS needs the matching `SDL3-CS` 
  native package.
- A legally obtained Game Boy / Game Boy Color ROM. ROMs and save files are not
  included in this repository and are gitignored.

## Build & run

```powershell
dotnet build gbcdn.slnx -c Release
dotnet run --project gbcdn.frontend -c Release
```

Or run the built executable directly:

```powershell
.\gbcdn.frontend\bin\Release\net10.0\gbcdn.frontend.exe
```

ROM Path can be passed as the first argument on the command line.
If no ROM Path is specified, the ROM can be selected via the GUI.

## Controls

| Game Boy | Keyboard | Xbox controller |
|----------|----------|-----------------|
| D-pad | Arrow keys | D-pad |
| A | `A` | A |
| B | `S` | B |
| Start | `Enter` | Start |
| Select | `Space` | Back |
| Menu | `Esc` | — |

## Save data & diagnostics

Files are created next to the ROM (same base name):

- `<rom>.ram` — battery-backed cartridge RAM (written on clean exit, loaded at start).
- `<rom>.rtc` — MBC3 real-time clock state (only for RTC-equipped cartridges).
- `hw_times.txt` — written when the emulator exits normally; contains sampled
  CPU/Timer/PPU/APU timings (totals, ms per emulated second, and percentage shares).
  Only one frame per second is instrumented, so the measurement overhead is negligible.

The RTC keeps advancing while the emulator is closed (matching battery-backed
hardware), unless the clock was halted by the game before saving.

## Project layout

```
gbcdn.slnx
gbcdn/
  Cpu.cs          SM83 CPU, memory map, cartridge mappers, VRAM DMA
  Ppu.cs          CGB PPU: scanlines, palettes, sprites, window, OAM/VRAM DMA
  Apu.cs          4-channel APU, mixer, ring buffer (AudioRingBuffer)
  Timer.cs        DIV/TIMA/TMA/TAC divider and timer interrupts
  Input.cs        Joypad register and interrupts
  Rtc.cs          MBC3 real-time clock
gbcdn.frontend/
  Program.cs      SDL3 window, rendering, audio output, input, frame pacing
  Gui.cs          GUI implemented with ImGui.net
```

## Status & limitations

- No boot ROM is emulated: the CPU starts at `$0100` with the post-boot register state.
- Development is CGB-focused; monochrome Game Boy
  compatibility is incomplete.
- `STOP` and CGB double-speed mode are not implemented.
- Some hardware edge cases are simplified, including exact DMA/PPU stalls, CPU access
  restrictions to VRAM/OAM during rendering, and a few APU/PPU timing quirks.

See [Pan Docs](https://gbdev.io/pandocs/) for the hardware documentation this
emulator follows.

## Background
This project was based on [dnmg](https://github.com/twobrainsgmbh/dnmg), an emulator for the original GameBoy.
A big thanks to the original Authors.

## License

MIT — see [LICENSE](LICENSE).  
Copyright (c) 2026 Christian Dix  
Copyright (c) 2026 twobrains GmbH (for the classic GB code)  