using System.Diagnostics;
using SDL3;

#if WINDOWS
using Windows.Win32.UI.Input.XboxController;
#endif

namespace GbcDN.Frontend;

internal static class Program
{
    private static short[] _audioShorts = [];
    private static byte[] _audioBytes = [];
    private static byte[] _audioSilence = [];

	static long _cpuTicks, _timerTicks, _ppuTicks, _apuTicks;
	static long _sampledCycles;
	static bool _measureFrame;
	static int _frameCounter;

    [STAThread]
    private static void Main(string[] args)
    {
        if (!SDL.Init(SDL.InitFlags.Video | SDL.InitFlags.Audio))
        {
            SDL.LogError(SDL.LogCategory.System, $"SDL could not initialize: {SDL.GetError()}");
            return;
        }

        if (!SDL.CreateWindowAndRenderer("SDL3 Create Window", 800, 600, 0, out var window, out var renderer))
        {
            SDL.LogError(SDL.LogCategory.Application, $"Error creating window and renderer: {SDL.GetError()}");
            return;
        }

        SDL.SetRenderDrawColor(renderer, 100, 149, 237, 255);
        SDL.RenderClear(renderer);
        SDL.RenderPresent(renderer);

        // Create the virtual framebuffer texture
        nint framebufferTexture = SDL.CreateTexture(
            renderer, 
            SDL.PixelFormat.RGB565, // Choose a standard 16-bit pixel format
            SDL.TextureAccess.Streaming, 
            160, 
            144
        );

        var romFilename = args.Length > 0 ? args[0] : Path.GetFullPath("../../../../crystal.gbc");
		var rom = File.ReadAllBytes(romFilename);

		var cpu = new Cpu(rom, romFilename);
		var ppu = new Ppu(cpu);
		var timer = new Timer(cpu);
		var input = new Input(cpu);
		var apu = new Apu(cpu, 44100);
		var rtc = new Rtc(cpu);
		cpu.Load();

		var audioSpec = new SDL.AudioSpec
		{
			Channels = 2,
			Format = SDL.AudioFormat.AudioS16LE,
			Freq = apu.SampleRate
		};

		var audioStream = SDL.OpenAudioDeviceStream(SDL.AudioDeviceDefaultPlayback, in audioSpec, null, IntPtr.Zero);
		if (audioStream == IntPtr.Zero)
		{
			SDL.LogError(SDL.LogCategory.Application, $"Couldn't open audio stream: {SDL.GetError()}");
		}
		else
		{
			PrimeAudioStream(audioStream, apu.SampleRate);
			SDL.ResumeAudioStreamDevice(audioStream);
		}

		var totalCycles = 0;
		var  frameDuration = TimeSpan.FromSeconds(70_224.0 / 4_194_304.0);
		long nextFrameTimestamp = Stopwatch.GetTimestamp();

#if WINDOWS
		// calling XInputGetState comes with a cost we only want to pay if a controller is actually attached.
		bool useController = Windows.Win32.PInvoke.XInputGetState(0, out _) == 0;
#endif

		bool running = true;
    	SDL.Event e;

        while (running)
		{
			// Event-Handling
			while (SDL.PollEvent(out e))
			{
				if (e.Type == (uint)SDL.EventType.Quit)
				{
					running = false;
				}
				else if (e.Type == (uint)SDL.EventType.KeyDown)
				{
					// Beenden mit der ESC-Taste
					if (e.Key.Scancode == SDL.Scancode.Escape)
					{
						running = false;
					}
					if (e.Key.Scancode switch
					{
						SDL.Scancode.Right => Input.JoypadButtons.RightArrow,
						SDL.Scancode.Left => Input.JoypadButtons.LeftArrow,
						SDL.Scancode.Up => Input.JoypadButtons.UpArrow,
						SDL.Scancode.Down => Input.JoypadButtons.DownArrow,
						SDL.Scancode.A => Input.JoypadButtons.A,
						SDL.Scancode.S => Input.JoypadButtons.B,
						SDL.Scancode.Return => Input.JoypadButtons.Start,
						SDL.Scancode.Space => Input.JoypadButtons.Select,
						_ => default(Input.JoypadButtons?)
					} is { } key)
					{
						input.PressedButtons |= key;
					}
				}
				else if (e.Type == (uint)SDL.EventType.KeyUp)
				{
					if (e.Key.Scancode switch
					{
						SDL.Scancode.Right => Input.JoypadButtons.RightArrow,
						SDL.Scancode.Left => Input.JoypadButtons.LeftArrow,
						SDL.Scancode.Up => Input.JoypadButtons.UpArrow,
						SDL.Scancode.Down => Input.JoypadButtons.DownArrow,
						SDL.Scancode.A => Input.JoypadButtons.A,
						SDL.Scancode.S => Input.JoypadButtons.B,
						SDL.Scancode.Return => Input.JoypadButtons.Start,
						SDL.Scancode.Space => Input.JoypadButtons.Select,
						_ => default(Input.JoypadButtons?)
					} is { } key)
					{
						input.PressedButtons &= ~key;
					}
				}
			}

			int cycles;
			if (_measureFrame)
			{
				var t0 = Stopwatch.GetTimestamp();
				cycles = cpu.ExecuteSingleStep();
				var t1 = Stopwatch.GetTimestamp();
				timer.ExecuteSingleStep(cycles);
				var t2 = Stopwatch.GetTimestamp();
				ppu.ExecuteSingleStep(cycles);
				var t3 = Stopwatch.GetTimestamp();
				apu.ExecuteSingleStep(cycles);
				var t4 = Stopwatch.GetTimestamp();
				_cpuTicks += t1 - t0;
				_timerTicks += t2 - t1;
				_ppuTicks += t3 - t2;
				_apuTicks += t4 - t3;
				_sampledCycles += cycles;
			}
			else
			{
				cycles = cpu.ExecuteSingleStep();
				timer.ExecuteSingleStep(cycles);
				ppu.ExecuteSingleStep(cycles);
				apu.ExecuteSingleStep(cycles);
			}
			totalCycles += cycles;

			const int dotPerFrame = 70_224;
			const int cyclesPerFrame = dotPerFrame / 4;
			if (totalCycles >= cyclesPerFrame)
			{
				totalCycles -= cyclesPerFrame;
				_measureFrame = (_frameCounter++ % 60) == 0; // sample one frame per emulated second

				Task.Run(() =>
				{
					PrintFrameBufferOnTexture(framebufferTexture, ppu, 2);
					// Clear the current frame
					SDL.RenderClear(renderer);
					// Copy your virtual framebuffer texture to the entire screen
					SDL.RenderTexture(renderer, framebufferTexture, IntPtr.Zero, IntPtr.Zero);
					// Present the updated backbuffer
					SDL.RenderPresent(renderer);
				});

				PumpAudio(audioStream, apu);

				// --- frame throttling ---
				nextFrameTimestamp += (long)(Stopwatch.Frequency * frameDuration.TotalSeconds);

				while (true)
				{
					long currentTimestamp = Stopwatch.GetTimestamp();
					long remainingTicks = nextFrameTimestamp - currentTimestamp;
					if (remainingTicks <= 0)
						break;
					
					double remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
					if (remainingMs > 2.0)
					{
						// yield CPU while there is plenty of time remaining
						Thread.Sleep((int)remainingMs);
					}
					else
					{
						// busy-wait for the remaining time
						Thread.SpinWait(10);
					}
				}
				// --- end of frame throttling ---

#if WINDOWS
				if (useController)
				{
					// experimental XBOX controller support
					var keys = default(Input.JoypadButtons);
					Windows.Win32.PInvoke.XInputGetState(0, out var state);
					foreach (var vy in new ReadOnlySpan<ValueTuple<XINPUT_GAMEPAD_BUTTON_FLAGS, Input.JoypadButtons>>([
						(XINPUT_GAMEPAD_BUTTON_FLAGS.XINPUT_GAMEPAD_DPAD_LEFT, Input.JoypadButtons.LeftArrow),
						(XINPUT_GAMEPAD_BUTTON_FLAGS.XINPUT_GAMEPAD_DPAD_RIGHT, Input.JoypadButtons.RightArrow),
						(XINPUT_GAMEPAD_BUTTON_FLAGS.XINPUT_GAMEPAD_DPAD_UP, Input.JoypadButtons.UpArrow),
						(XINPUT_GAMEPAD_BUTTON_FLAGS.XINPUT_GAMEPAD_DPAD_DOWN, Input.JoypadButtons.DownArrow),
						(XINPUT_GAMEPAD_BUTTON_FLAGS.XINPUT_GAMEPAD_A, Input.JoypadButtons.A),
						(XINPUT_GAMEPAD_BUTTON_FLAGS.XINPUT_GAMEPAD_B, Input.JoypadButtons.B),
						(XINPUT_GAMEPAD_BUTTON_FLAGS.XINPUT_GAMEPAD_START, Input.JoypadButtons.Start)
					]))
					{
						if (state.Gamepad.wButtons.HasFlag(vy.Item1))
							keys |= vy.Item2;
					}
					input.PressedButtons = keys;
				}
#endif
			}
		}

		cpu.Save();

		ppu.Dispose();

		var seconds = _sampledCycles / 1_048_576.0; // M-cycles per emulated second
		var cpuMs = _cpuTicks * 1000.0 / Stopwatch.Frequency;
		var timerMs = _timerTicks * 1000.0 / Stopwatch.Frequency;
		var ppuMs = _ppuTicks * 1000.0 / Stopwatch.Frequency;
		var apuMs = _apuTicks * 1000.0 / Stopwatch.Frequency;
		var totalMs = cpuMs + timerMs + ppuMs + apuMs;

		StreamWriter sw = new StreamWriter("hw_times.txt");
		sw.WriteLine($"CPU: {cpuMs:F1}");
		sw.WriteLine($"TIMER: {timerMs:F1}");
		sw.WriteLine($"PPU: {ppuMs:F1}");
		sw.WriteLine($"APU: {apuMs:F1}");
		sw.WriteLine($"SAMPLED_SECONDS: {seconds:F2}");
		if (seconds > 0 && totalMs > 0)
		{
			sw.WriteLine($"CPU_PER_SEC: {cpuMs / seconds:F3}");
			sw.WriteLine($"TIMER_PER_SEC: {timerMs / seconds:F3}");
			sw.WriteLine($"PPU_PER_SEC: {ppuMs / seconds:F3}");
			sw.WriteLine($"APU_PER_SEC: {apuMs / seconds:F3}");
			sw.WriteLine($"CPU_SHARE: {cpuMs / totalMs * 100:F1} %");
			sw.WriteLine($"TIMER_SHARE: {timerMs / totalMs * 100:F1} %");
			sw.WriteLine($"PPU_SHARE: {ppuMs / totalMs * 100:F1} %");
			sw.WriteLine($"APU_SHARE: {apuMs / totalMs * 100:F1} %");
		}
		sw.Close();

        if (audioStream != IntPtr.Zero)
            SDL.DestroyAudioStream(audioStream);

        SDL.DestroyRenderer(renderer);
        SDL.DestroyWindow(window);
        SDL.Quit();

    }

    private static int AudioTargetBytes(int sampleRate)
        => sampleRate * 2 * sizeof(short) / 30; // ~33 ms of S16 stereo

    private static void PrimeAudioStream(IntPtr audioStream, int sampleRate)
    {
        var targetBytes = AudioTargetBytes(sampleRate);
        if (_audioSilence.Length < targetBytes)
            _audioSilence = new byte[targetBytes];
        SDL.PutAudioStreamData(audioStream, _audioSilence, targetBytes);
    }

    private static void PumpAudio(IntPtr audioStream, Apu apu)
	{
		var targetBytes = AudioTargetBytes(apu.SampleRate);
		var queued = SDL.GetAudioStreamQueued(audioStream);
		var deficitSamples = (targetBytes - queued) / sizeof(short);
		if (deficitSamples > 0)
		{
			if (_audioShorts.Length < deficitSamples)
			{
				_audioShorts = new short[deficitSamples];
				_audioBytes = new byte[deficitSamples * sizeof(short)];
			}

			var read = apu.Buffer.Read(_audioShorts.AsSpan(0, deficitSamples));
			if (read > 0)
			{
				System.Buffer.BlockCopy(_audioShorts, 0, _audioBytes, 0, read * sizeof(short));
				SDL.PutAudioStreamData(audioStream, _audioBytes, read * sizeof(short));
			}
		}

		// Dynamic rate control: gently adjust playback speed so the emulator and device clocks never drift apart
		queued = SDL.GetAudioStreamQueued(audioStream);
		var ratio = 1.0 + ((queued - targetBytes) / (double)targetBytes * 0.005);
		SDL.SetAudioStreamFrequencyRatio(audioStream, (float)Math.Clamp(ratio, 0.995, 1.005));
	}

    private static void PrintFrameBufferOnTexture(nint framebufferTexture, Ppu gpu, int scale)
	{
		// Lock the texture to get direct access to the pixel buffer
        if (SDL.LockTexture(framebufferTexture, default, out nint pixelPtr, out int pitch))
        {
            unsafe
            {
                // Cast the pointer to a 32-bit unsigned integer pointer (for ARGB8888)
                ushort* pixels = (ushort*)pixelPtr;

                for (int y = 0; y < 144; y++)
                {
					var off = y * 160;
					var rowStartIndex = y * (pitch >> 1);
                    for (int x = 0; x < 160; x++)
                    {   
						int index = rowStartIndex | x;
						var pixelColor = gpu.FrameBuffer2[off + x];
                        pixels[index] = (ushort)(((pixelColor & 0x7C00) >> 10) | ((pixelColor & 0x03E0) << 1) | ((pixelColor & 0x001F) << 11));
                    }
                }
            }

            // Unlock the texture when you're done writing
            SDL.UnlockTexture(framebufferTexture);
        }
	}
}
