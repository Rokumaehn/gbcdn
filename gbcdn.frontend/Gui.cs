using System.Diagnostics;
using SDL3;
using ImGuiNET;

namespace GbcDN.Frontend;

public class Gui
{
    nint _renderer;
    private IntPtr _imguiContext;
    private IntPtr fontTexture;
    private readonly HashSet<SDL.GamepadButton> _activeButtons = new HashSet<SDL.GamepadButton>();
	private ulong _lastTime = SDL.GetTicks();
	private readonly Gamepad _gamepad;
    private long _nextFrameTimestamp;
    private readonly double _targetFps = 60.0;


    public bool IsRunning { get; set; } = true;


    public Gui(nint renderer, int width, int height, Gamepad gamepad)
    {
		_renderer = renderer;
		_gamepad = gamepad;

        _curDir = LoadLastDirectory() ?? new DirectoryInfo(System.IO.Directory.GetCurrentDirectory());

        // 1. Setup ImGui Context
		_imguiContext = ImGui.CreateContext();
		ImGui.SetCurrentContext(_imguiContext);
		ImGui.StyleColorsDark();

        // 2. Fetch IO and force-build the default font atlas
		ImGuiIOPtr imguiio = ImGui.GetIO();
		unsafe
		{
			// Allocates AND constructs natively behind the scenes
			IntPtr nativeConfig = (IntPtr)ImGuiNative.ImFontConfig_ImFontConfig(); 
			ImFontConfigPtr fontConfig = new ImFontConfigPtr(nativeConfig);

			// Apply adjustments
			fontConfig.SizePixels = 18.0f;

			// Load default font configuration
			ImGui.GetIO().Fonts.AddFontDefault(fontConfig);

			// Frees memory allocation automatically safely
			fontConfig.Destroy(); 
		}
		//imguiio.Fonts.AddFontDefault(fontConfig); // Explicitly add a fallback font
		imguiio.Fonts.Build();          // <-- THIS STOPS THE ASSERTION ERROR

        CreateFontsTexture(renderer);
    }

    public void Dispose()
    {
        if (fontTexture != IntPtr.Zero)
        {
            SDL.DestroyTexture(fontTexture);
            fontTexture = IntPtr.Zero;
        }
        ImGui.DestroyContext(_imguiContext);
    }

    protected bool EventLoop(ImGuiIOPtr io, out bool escapePressed)
    {
        escapePressed = false;

        // Poll Events
        while (SDL.PollEvent(out SDL.Event e))
        {
            switch (e.Type)
            {
                case (uint)SDL.EventType.Quit:
					return false;

                case (uint)SDL.EventType.KeyDown:
					{
						if (e.Key.Scancode == SDL.Scancode.Escape)
						{
							escapePressed = true;
							return true;
						}
					}
					break;

                // --- 1. GAMEPAD BUTTONS ---
                case (uint)SDL.EventType.GamepadButtonDown:
                    {
                        var button = (SDL.GamepadButton)e.GButton.Button;
                        if (!_activeButtons.Contains(button))
                        {
                            _activeButtons.Add(button);
                            // SDL3 Button zu ImGuiKey mappen
                            ImGuiKey imguiKey = MapSDLGamepadButton(button);
                            if (imguiKey != ImGuiKey.None)
                            {
                                io.AddKeyEvent(imguiKey, true);
                                Console.WriteLine("SDL hat einen Button-Druck registriert!");
                            }
                        }
                    }
                    break;
                    
                case (uint)SDL.EventType.GamepadButtonUp:
                    {
                        var button = (SDL.GamepadButton)e.GButton.Button;
                        if (_activeButtons.Contains(button))
                        {
                            _activeButtons.Remove(button);
                            // SDL3 Button zu ImGuiKey mappen
                            ImGuiKey imguiKey = MapSDLGamepadButton(button);
                            if (imguiKey != ImGuiKey.None)
                            {
                                io.AddKeyEvent(imguiKey, false);
                            }
                        }
                    }
                    break;

                // --- 2. GAMEPAD AXES (Sticks & Trigger) ---
                case (uint)SDL.EventType.GamepadAxisMotion:
                    var axis = (SDL.GamepadAxis)e.GAxis.Axis;
                    // SDL3 liefert Werte von -32768 bis 32767
                    float rawValue = e.GAxis.Value; 
                    float normalizedValue = rawValue / 32767.0f;
                    ProcessGamepadAxis(io, axis, normalizedValue);
                    break;

                // Hotplugging: A controller was connected
                case (uint)SDL.EventType.GamepadAdded:
                    // e.GDevice.Which holds the device instance index for initialization
                    if (_gamepad.ActiveGamepad == IntPtr.Zero)
                    {
                        _gamepad.ActiveGamepad = SDL.OpenGamepad(e.GDevice.Which);
                        if (_gamepad.ActiveGamepad != IntPtr.Zero)
                        {
                            _gamepad.ActiveGamepadId = e.GDevice.Which;
                            io.BackendFlags |= ImGuiBackendFlags.HasGamepad;
                            string? name = SDL.GetGamepadName(_gamepad.ActiveGamepad);
                            Console.WriteLine($"[CONNECTED] {name} (ID: {_gamepad.ActiveGamepadId})");
                        }
                    }
                    break;

                // Hotplugging: A controller was disconnected
                case (uint)SDL.EventType.GamepadRemoved:
                    if (_gamepad.ActiveGamepad != IntPtr.Zero && e.GDevice.Which == _gamepad.ActiveGamepadId)
                    {
                        Console.WriteLine($"[DISCONNECTED] Gamepad ID: {_gamepad.ActiveGamepadId}");
                        SDL.CloseGamepad(_gamepad.ActiveGamepad);
                        _gamepad.ActiveGamepad = IntPtr.Zero;
                        _gamepad.ActiveGamepadId = 0;
                        io.BackendFlags &= ~ImGuiBackendFlags.HasGamepad;
                    }
                    break;

                case (uint)SDL.EventType.MouseButtonDown:
                {
                    int buttonIndex = MapSDLButtonToImGui(e.Button.Button);
                    if (buttonIndex >= 0)
                    {
                        io.AddMouseButtonEvent(buttonIndex, true); // true = Down
                    }
                    break;
                }

                case (uint)SDL.EventType.MouseButtonUp:
                {
                    int buttonIndex = MapSDLButtonToImGui(e.Button.Button);
                    if (buttonIndex >= 0)
                    {
                        io.AddMouseButtonEvent(buttonIndex, false); // false = Up
                    }
                    break;
                }

                case (uint)SDL.EventType.MouseMotion:
                    io.AddMousePosEvent(e.Motion.X, e.Motion.Y);
                    break;

                case (uint)SDL.EventType.MouseWheel:
                    // Extract the wheel deltas from the SDL3 event
                    float wheelX = e.Wheel.X;
                    float wheelY = e.Wheel.Y;

                    // Invert the horizontal scrolling if needed (SDL3 matches Windows convention, 
                    // but ImGui traditionally expects flipped horizontal values depending on the backend)
                    // If your horizontal scrolling feels reversed, uncomment the line below:
                    // wheelX = -wheelX; 

                    // Pass the scrolling deltas into ImGui's event queue
                    io.AddMouseWheelEvent(wheelX, wheelY);
                    break;
            }
            
        }

        return true;
    }

    protected void SaveCurrentDirectory(DirectoryInfo dir)
    {
        using (StreamWriter sw = new StreamWriter(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "/gbcdn.lastdir.cfg"))
        {
            sw.WriteLine(dir.FullName);
        }
    }

    protected DirectoryInfo? LoadLastDirectory()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "/gbcdn.lastdir.cfg";
        if (File.Exists(path))
        {
            string? lastDir = File.ReadAllText(path).Trim();
            if (Directory.Exists(lastDir))
            {
                return new DirectoryInfo(lastDir);
            }
        }
        return null;
    }

    DirectoryInfo _curDir;

    public void Save()
    {
        SaveCurrentDirectory(_curDir);
    }

    protected Stack<DirectoryInfo> _previousDirectories = new Stack<DirectoryInfo>();

    public string? GuiPickFile(bool allowEscape = true)
	{
		if(_curDir == null || !Directory.Exists(_curDir.FullName))
        {
			_curDir = new DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
        }
        
		ImGuiIOPtr io = ImGui.GetIO();
        bool running = true;
        _lastTime = SDL.GetTicks();
        bool escapePressed = false;
        _nextFrameTimestamp = Stopwatch.GetTimestamp();

		while (IsRunning)
		{
			// Poll Events
			running = EventLoop(io, out escapePressed);
			if (!running) 
            {
                IsRunning = false;
                return null;
            }
            if (escapePressed && allowEscape)
            {
                return null;
            }
			
			// --- START FRAME ---
			// Update Delta Time for ImGui
			ImGuiIOPtr iod = ImGui.GetIO();
			iod.DisplaySize = new System.Numerics.Vector2(800, 600);
			
			ulong currentTime = SDL.GetTicks();
			float deltaTime = (currentTime - _lastTime) / 1000.0f;
			_lastTime = currentTime;
			if (deltaTime <= 0.0f) deltaTime = 0.00001f; 
			iod.DeltaTime = deltaTime;

            if(_gamepad.ActiveGamepad != IntPtr.Zero)
            {
			    iod.ConfigFlags |= ImGuiConfigFlags.NavEnableGamepad;
			    iod.BackendFlags |= ImGuiBackendFlags.HasGamepad;
            }

			// Signal a new ImGui Frame
			ImGui.NewFrame();

			// --- DEFINE YOUR UI ---
            var view = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(new System.Numerics.Vector2(view.Pos.X, view.Pos.Y));
            ImGui.SetNextWindowSize(new System.Numerics.Vector2(view.Size.X / 4 * 3, view.Size.Y));
			ImGui.Begin("Pick your ROM", ImGuiWindowFlags.AlwaysAutoResize);

            // Filter out pseudo-filesystems like /proc and /sys on Linux
            var realDrives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)
                .Where(d => d.IsReady);

            ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.3f, 0.3f, 0.0f, 1.0f));
            if (ImGui.Button("<="))
            {
                var q = _previousDirectories.Count > 0 ? _previousDirectories.Pop() : null;
                _curDir = q ?? _curDir;
            }
            foreach(var drive in realDrives)
            {
                ImGui.SameLine();
                
                if (ImGui.Button(drive.Name))
                {
                    _previousDirectories.Push(_curDir);
                    _curDir = new DirectoryInfo(drive.Name);
                }
            }

            ImGui.Separator();
			
			if (ImGui.Button("<..>"))
			{
                _previousDirectories.Push(_curDir);
				_curDir = _curDir.Parent ?? _curDir;
			}
			ImGui.SetItemDefaultFocus();
			
			foreach (var d in _curDir.GetDirectories())
			{
				if (ImGui.Button("<" + d.Name + ">"))
				{
					_previousDirectories.Push(_curDir);
					_curDir = new DirectoryInfo(Path.Combine(_curDir.FullName, d.Name));
				}
			}

            ImGui.PopStyleColor();

			foreach (var file in _curDir.GetFiles())
			{
                if(file.Extension.Equals(".gbc", StringComparison.OrdinalIgnoreCase))
                    ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.0f, 0.3f, 0.0f, 1.0f));
                
				if (ImGui.Button(file.Name))
				{
                    if(file.Extension.Equals(".gbc", StringComparison.OrdinalIgnoreCase))
                        ImGui.PopStyleColor();

                    if(file.Length > 0 && file.Length % 0x4000 == 0)
                    {
					    using (BinaryReader br = new BinaryReader(File.OpenRead(file.FullName)))
					    {
						    byte[] check = br.ReadBytes(0x150);
                            byte checksum = 0;
                            for (ushort address = 0x0134; address <= 0x014C; address++)
                            {
                                checksum = (byte)(checksum - check[address] - 1);
                            }
                            if(check[0x100] == 0x00 // nop 
                               && check[0x101] == 0xC3 // jp
                               && checksum == check[0x14D])
                            {
                                ImGui.End();
                                ImGui.EndFrame();
                                return file.FullName;
                            }
					    }
                    }
				}

                if(file.Extension.Equals(".gbc", StringComparison.OrdinalIgnoreCase))
                    ImGui.PopStyleColor();
			}
			ImGui.End();

			// --- RENDER EXECUTION ---
			// 1. Clear the screen using SDL3
			SDL.SetRenderDrawColor(_renderer, 0x10, 0x15, 0x20, 0xFF);
			SDL.RenderClear(_renderer);

			// 2. Generate vertex arrays from ImGui elements
			ImGui.Render();

			// 3. drawing manually via SDL3 geometric primitives
			RenderImGuiDrawData(_renderer, ImGui.GetDrawData());

			// 4. Swap Framebuffers
			SDL.RenderPresent(_renderer);

            ThrottleFrame();
		}

		return null;
	}

    protected void ThrottleFrame()
    {
        _nextFrameTimestamp += (long)(Stopwatch.Frequency * (1.0 / _targetFps));

        while (true)
        {
            long currentTimestamp = Stopwatch.GetTimestamp();
            long remainingTicks = _nextFrameTimestamp - currentTimestamp;
            if (remainingTicks <= 0)
            {
                break;
            }
            
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
    }
    
    private static int MapSDLButtonToImGui(byte sdlButton)
	{
		return sdlButton switch
		{
			1 => 0, // SDL_BUTTON_LEFT -> ImGui Mouse Button 0
			2 => 2, // SDL_BUTTON_MIDDLE -> ImGui Mouse Button 2
			3 => 1, // SDL_BUTTON_RIGHT -> ImGui Mouse Button 1
			4 => 3, // SDL_BUTTON_X1 -> ImGui Mouse Button 3
			5 => 4, // SDL_BUTTON_X2 -> ImGui Mouse Button 4
			_ => -1 // Unknown button
		};
	}

	private static ImGuiKey MapSDLGamepadButton(SDL.GamepadButton button)
	{
		return button switch
		{
			SDL.GamepadButton.South        => ImGuiKey.GamepadFaceDown,
			SDL.GamepadButton.East         => ImGuiKey.GamepadFaceRight,
			SDL.GamepadButton.West         => ImGuiKey.GamepadFaceLeft,
			SDL.GamepadButton.North        => ImGuiKey.GamepadFaceUp,
			SDL.GamepadButton.Back         => ImGuiKey.GamepadBack,
			SDL.GamepadButton.Guide        => ImGuiKey.None,
			SDL.GamepadButton.Start        => ImGuiKey.GamepadStart,
			SDL.GamepadButton.LeftStick    => ImGuiKey.GamepadLStickDown,
			SDL.GamepadButton.RightStick   => ImGuiKey.GamepadRStickDown,
			SDL.GamepadButton.LeftShoulder => ImGuiKey.GamepadL1,
			SDL.GamepadButton.RightShoulder => ImGuiKey.GamepadR1,
			SDL.GamepadButton.DPadUp       => ImGuiKey.GamepadDpadUp,
			SDL.GamepadButton.DPadDown     => ImGuiKey.GamepadDpadDown,
			SDL.GamepadButton.DPadLeft     => ImGuiKey.GamepadDpadLeft,
			SDL.GamepadButton.DPadRight    => ImGuiKey.GamepadDpadRight,
			_ => ImGuiKey.None
		};
	}

	private static void ProcessGamepadAxis(ImGuiIOPtr io, SDL.GamepadAxis axis, float value)
	{
		float deadzone = 0.1f; // 10% Deadzone

		switch (axis)
		{
			case SDL.GamepadAxis.LeftX:
				// Horizontaler linker Stick
				io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickLeft, value < -deadzone, value < -deadzone ? -value : 0.0f);
				io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickRight, value > deadzone, value > deadzone ? value : 0.0f);
				break;

			case SDL.GamepadAxis.LeftY:
				// Vertikaler linker Stick (Achtung: SDL Y ist nach unten positiv, ImGui steuert invers oder gleich je nach Setup)
				io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickUp, value < -deadzone, value < -deadzone ? -value : 0.0f);
				io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickDown, value > deadzone, value > deadzone ? value : 0.0f);
				break;

			case SDL.GamepadAxis.RightX:
				// Horizontaler rechter Stick (oft für Scrolling in ImGui genutzt)
				io.AddKeyAnalogEvent(ImGuiKey.GamepadRStickLeft, value < -deadzone, value < -deadzone ? -value : 0.0f);
				io.AddKeyAnalogEvent(ImGuiKey.GamepadRStickRight, value > deadzone, value > deadzone ? value : 0.0f);
				break;

			case SDL.GamepadAxis.RightY:
				// Vertikaler rechter Stick
				io.AddKeyAnalogEvent(ImGuiKey.GamepadRStickUp, value < -deadzone, value < -deadzone ? -value : 0.0f);
				io.AddKeyAnalogEvent(ImGuiKey.GamepadRStickDown, value > deadzone, value > deadzone ? value : 0.0f);
				break;

			case SDL.GamepadAxis.LeftTrigger:
				// Linker Trigger (L2)
				io.AddKeyAnalogEvent(ImGuiKey.GamepadL2, value > deadzone, value > deadzone ? value : 0.0f);
				break;

			case SDL.GamepadAxis.RightTrigger:
				// Rechter Trigger (R2)
				io.AddKeyAnalogEvent(ImGuiKey.GamepadR2, value > deadzone, value > deadzone ? value : 0.0f);
				break;
		}
	}

	unsafe void CreateFontsTexture(IntPtr renderer)
	{
		ImGuiIOPtr io = ImGui.GetIO();

		// 1. Grab raw pixel data from ImGui's font builder
		byte* pixels;
		int width, height;
		io.Fonts.GetTexDataAsRGBA32(out pixels, out width, out height);

		// 2. Create an empty SDL3 texture matching those dimensions
		fontTexture = SDL.CreateTexture(
			renderer, 
			SDL.PixelFormat.ARGB8888, 
			SDL.TextureAccess.Static, 
			width, 
			height
		);

		// 3. Upload the pixel array directly to your GPU texture memory
		SDL.UpdateTexture(
			fontTexture, 
			IntPtr.Zero, // NULL updates the entire texture bounds
			(IntPtr)pixels, 
			width * 4    // Pitch (Width * bytes per pixel)
		);

		// 4. Save the reference identifier inside ImGui's systems
		// This allows you to match individual draws back to this texture ID later
		io.Fonts.SetTexID(fontTexture);
	}

	public static unsafe void RenderImGuiDrawData(IntPtr renderer, ImDrawDataPtr drawData)
    {
        // 1. Guard against minimized windows / empty frames
        if (drawData.DisplaySize.X <= 0.0f || drawData.DisplaySize.Y <= 0.0f || drawData.CmdListsCount == 0)
            return;

        // 2. Account for High-DPI displays (Framebuffer size vs. ImGui logical size)
        System.Numerics.Vector2 clipOffset = drawData.DisplayPos;       // Usually (0,0) unless multi-viewports are used
        System.Numerics.Vector2 clipScale = drawData.FramebufferScale; // Vector scale multiplier for screen coordinates

        // 3. Iterate through every sequential Command List generated by ImGui
        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            ImDrawListPtr cmdList = drawData.CmdLists[n];
            
            // Extract typed native pointers to ImGui's internal memory
            ImDrawVert* imVertices = (ImDrawVert*)cmdList.VtxBuffer.Data.ToPointer();
            ushort* imIndices = (ushort*)cmdList.IdxBuffer.Data.ToPointer();

            // 4. Translate ImGui vertices directly into SDL3 layout array memory
            int vertexCount = cmdList.VtxBuffer.Size;
            SDL.Vertex[] sdlVertices = new SDL.Vertex[vertexCount];

            for (int i = 0; i < vertexCount; i++)
            {
                ImDrawVert v = imVertices[i];

                // Map position coordinates
                sdlVertices[i].Position.X = v.pos.X;
                sdlVertices[i].Position.Y = v.pos.Y;

                // Map normalized UV coordinates
                sdlVertices[i].TexCoord.X = v.uv.X;
                sdlVertices[i].TexCoord.Y = v.uv.Y;

                // Map ImGui's Packed Uint32 Color (0xAABBGGRR) to SDL3's SDL_FColor normalized layout
                // Extract channels using bitmasks
                byte r = (byte)(v.col & 0xFF);
                byte g = (byte)((v.col >> 8) & 0xFF);
                byte b = (byte)((v.col >> 16) & 0xFF);
                byte a = (byte)((v.col >> 24) & 0xFF);

                // Convert bytes (0-255) to float sliders (0.0f-1.0f) required by SDL_FColor
                sdlVertices[i].Color.R = r / 255.0f;
                sdlVertices[i].Color.G = g / 255.0f;
                sdlVertices[i].Color.B = b / 255.0f;
                sdlVertices[i].Color.A = a / 255.0f;
            }

            // 5. Process unique rendering commands within this command list
            for (int cmdi = 0; cmdi < cmdList.CmdBuffer.Size; cmdi++)
            {
                ImDrawCmdPtr cmd = cmdList.CmdBuffer[cmdi];

                // If user defined a custom callback hook, run it and bypass geometric loops
                if (cmd.UserCallback != IntPtr.Zero)
                {
                    // Advanced usage (e.g. raw GL state manipulation) - rarely needed
                    continue;
                }

                // Compute high-dpi clipping rectangles mapped back to explicit pixel positions
                System.Numerics.Vector4 clipRect = cmd.ClipRect;
                float clipMinX = (clipRect.X - clipOffset.X) * clipScale.X;
                float clipMinY = (clipRect.Y - clipOffset.Y) * clipScale.Y;
                float clipMaxX = (clipRect.Z - clipOffset.X) * clipScale.X;
                float clipMaxY = (clipRect.W - clipOffset.Y) * clipScale.Y;

                if (clipMaxX <= clipMinX || clipMaxY <= clipMinY)
                    continue;

                SDL.Rect sdlClipRect = new SDL.Rect
                {
                    X = (int)clipMinX,
                    Y = (int)clipMinY,
                    W = (int)(clipMaxX - clipMinX),
                    H = (int)(clipMaxY - clipMinY)
                };

                // Enforce scissors bounding box constraints in the SDL3 Pipeline
                SDL.SetRenderClipRect(renderer, in sdlClipRect);

                // 6. Slice out the specific indices chunk targeting this draw command
                int indexCount = (int)cmd.ElemCount;
                int[] sdlIndices = new int[indexCount];

                // ImGui strings commands use offset slices into the primary index array layout
                ushort* cmdIndicesOffset = imIndices + cmd.IdxOffset;

                for (int idx = 0; idx < indexCount; idx++)
                {
                    // SDL_RenderGeometry expects signed standard 32-bit integers,
                    // apply ImGui offset transformations per mesh component index element
                    sdlIndices[idx] = (int)(cmdIndicesOffset[idx] + cmd.VtxOffset);
                }

                // 7. Match texture pointers (Font atlas or bound custom images)
                IntPtr textureHandle = (IntPtr)cmd.TextureId;

                // 8. Submit the raw native graphics primitives buffer batch down the hardware loop pipeline
                SDL.RenderGeometry(
                    renderer,
                    textureHandle,
                    sdlVertices,
                    sdlVertices.Length,
                    sdlIndices,
                    sdlIndices.Length
                );
            }
        }

        // 9. Clean up and restore scissor masks state context for other layout operations
        SDL.SetRenderClipRect(renderer, IntPtr.Zero);
    }

}