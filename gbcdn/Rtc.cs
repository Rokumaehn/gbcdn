namespace GbcDN;

public sealed class Rtc
{
	private readonly Cpu _cpu;

    bool carry = false;
    bool carryLatched = false;
    bool halt = false;
    bool haltLatched = false;
    int day = 0;
    int dayLatched = 0;
    byte hour = 0;
    byte hourLatched = 0;
    byte minute = 0;
    byte minuteLatched = 0;
    byte second = 0;
    byte secondLatched = 0;
    DateTime refTime = DateTime.Now;

	public Rtc(Cpu cpu)
	{
		_cpu = cpu;
		_cpu.OnRtcWrite = WriteRegister;
		_cpu.OnRtcRead = ReadRegister;
        _cpu.OnRtcLatchWrite = LatchWrite; 
        _cpu.OnLoad += Load;
        _cpu.OnSave += Save;
	}

    public async Task SaveState(string? pathToRom)
    {
        if(!_cpu._hasRtc)
            return;
        if (pathToRom == null)
			return;
		var payload = $"{refTime.Ticks} {day} {hour} {minute} {second} {carry} {halt}";
		await Task.Run(() => { DumpToFile(Path.ChangeExtension(pathToRom, ".rtc"), payload); });
    }

    private void DumpToFile(string path, string data)
	{
		using (StreamWriter writer = new StreamWriter(path))
        {
            writer.WriteLine(data);
        }
	}

    private void Save(string? pathToRom)
    {
        if (pathToRom == null || !_cpu._hasRtc)
            return;
        
        using (StreamWriter writer = new StreamWriter(Path.ChangeExtension(pathToRom, ".rtc")))
        {
            writer.WriteLine($"{refTime.Ticks} {day} {hour} {minute} {second} {carry} {halt}");
        }
    }

    private void Load(string? pathToRom)
    {
        if (pathToRom == null || !_cpu._hasRtc || !File.Exists(Path.ChangeExtension(pathToRom, ".rtc")))
            return;

        using (StreamReader reader = new StreamReader(Path.ChangeExtension(pathToRom, ".rtc")))
        {
            var line = reader.ReadLine();
            if (line != null)
            {
                var parts = line.Split(' ');
                if (parts.Length == 7)
                {
                    refTime = new DateTime(long.Parse(parts[0]));
                    day = int.Parse(parts[1]);
                    hour = byte.Parse(parts[2]);
                    minute = byte.Parse(parts[3]);
                    second = byte.Parse(parts[4]);
                    carry = bool.Parse(parts[5]);
                    halt = bool.Parse(parts[6]);
                }
            }
        }
        
        if(!halt)
            AdvanceTime();
        // Latch the current RTC values
        secondLatched = second;
        minuteLatched = minute;
        hourLatched = hour;
        dayLatched = day;
        carryLatched = carry;
        haltLatched = halt;
    }

	private void WriteRegister(byte reg, byte val)
    {
        Console.WriteLine($"RtcWriteRegister: reg={reg:X2}, val={val:X2}");
        if(!halt)
            AdvanceTime();
        
        switch (reg)
        {
            case 0x08:
                second = (byte)(val & 0x3F);
                break;
            case 0x09:
                minute = (byte)(val & 0x3F);
                break;
            case 0x0A:
                hour = (byte)(val & 0x1F);
                break;
            case 0x0B:
                day = val | (day & 0x100);
                break;
            case 0x0C:
                if((val & 0x01) != 0)
                    day |= 0x100; // Set the 9th bit of the day
                else
                    day &= 0xFF; // Clear the 9th bit of the day
                if((val & 0x80) != 0)
                    carry = true;
                else
                    carry = false;
                if((val & 0x40) != 0)
                    halt = true;
                else
                    halt = false;
                break;
            default:
                break;
        }

        refTime = DateTime.Now;
    }

    private byte ReadRegister(byte reg)
    {
        if(!halt)
            AdvanceTime();

        // always return the latched values
        switch (reg)
        {
            case 0x08:
                return secondLatched;
            case 0x09:
                return minuteLatched;
            case 0x0A:
                return hourLatched;
            case 0x0B:
                return (byte)dayLatched;
            case 0x0C:
                return (byte)((carryLatched ? 0x80 : 0x00) | (haltLatched ? 0x40 : 0x00) | ((dayLatched & 0x100) >> 8));
            default:
                return 0;
        }
    }

    private byte _lastLatchWrite;
	private void LatchWrite(int addr, byte value)
	{
        if (value == 0x01 && _lastLatchWrite == 0x00)
        {
            if(!halt)
                AdvanceTime();
            
            secondLatched = second;
            minuteLatched = minute;
            hourLatched = hour;
            dayLatched = day;
            carryLatched = carry;
            haltLatched = halt;
        }

        _lastLatchWrite = value;
	}

    private void AdvanceTime()
    {
        var sample = DateTime.Now;
        var elapsed = sample - refTime;
        if (elapsed <= TimeSpan.Zero)
        {
            // A real RTC only counts forward; ignore host clock corrections that
            // would move it backwards (and avoid corrupting the fields with
            // negative time components).
            refTime = sample;
            return;
        }

        var seconds = elapsed.Ticks / TimeSpan.TicksPerSecond;
        if (seconds == 0)
            return; // keep the sub-second remainder for the next call

        refTime = refTime.AddTicks(seconds * TimeSpan.TicksPerSecond);

        long total = second + seconds;
        second = (byte)(total % 60);
        total /= 60;
        total += minute;
        minute = (byte)(total % 60);
        total /= 60;
        total += hour;
        hour = (byte)(total % 24);
        total /= 24;
        total += day;
        if (total > 0x1FF)
        {
            total &= 0x1FF;
            carry = true;
        }
        day = (int)total;
    }

}
