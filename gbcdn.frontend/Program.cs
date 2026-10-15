using System.Diagnostics;
using SDL3;

#if WINDOWS
using Windows.Win32.UI.Input.XboxController;
#endif

namespace GbcDN.Frontend;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (!SDL.Init(SDL.InitFlags.Video))
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

        var romFilename = args.Length > 0 ? args[0] : Path.GetFullPath("../../../../silver.gbc");
		var rom = File.ReadAllBytes(romFilename);

		var cpu = new Cpu(rom);
		var ppu = new Ppu(cpu);
		var timer = new Timer(cpu);
		var input = new Input(cpu);
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

			var cycles = cpu.ExecuteSingleStep();
			timer.ExecuteSingleStep(cycles);
			ppu.ExecuteSingleStep(cycles);
			totalCycles += cycles;

			const int dotPerFrame = 70_224;
			const int cyclesPerFrame = dotPerFrame / 4;
			if (totalCycles >= cyclesPerFrame)
			{
				totalCycles -= cyclesPerFrame;

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

        SDL.DestroyRenderer(renderer);
        SDL.DestroyWindow(window);
        SDL.Quit();

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
                    for (int x = 0; x < 160; x++)
                    {
                        // Calculate the exact 1D index inside the pitch-aligned buffer
                        // Note: pitch is in bytes, so divide by 2 to get the index for ushort (2 bytes)

                        // Example: Fill with a solid color or specific pattern
                        
						int index = (y * (pitch >> 1)) | x;
						// var red = (gpu.FrameBuffer2[x, y] & 0x7C00) >> 10;
						// var green = (gpu.FrameBuffer2[x, y] & 0x03E0) >> 5;
						// var blue = (gpu.FrameBuffer2[x, y] & 0x001F);
						// pixels[index] = (ushort)((blue << 11) | (green << 6) | red);
						var pixelColor = gpu.FrameBuffer2[x, y];
                        pixels[index] = (ushort)(((pixelColor & 0x7C00) >> 10) | ((pixelColor & 0x03E0) << 1) | ((pixelColor & 0x001F) << 11));
                    }
                }
            }

            // Unlock the texture when you're done writing
            SDL.UnlockTexture(framebufferTexture);
        }
	}
}
