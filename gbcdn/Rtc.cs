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
        TimeSpan elapsed = sample - refTime;
        if(elapsed.Seconds!=0 || elapsed.Minutes!=0 || elapsed.Hours!=0 || elapsed.Days!=0)
        {
            second += (byte)elapsed.Seconds;
            minute += (byte)elapsed.Minutes;
            hour += (byte)elapsed.Hours;
            day += (int)elapsed.Days;
            refTime = sample - new TimeSpan(0, 0, 0, 0, elapsed.Milliseconds, elapsed.Microseconds);

            if (second >= 60)
            {
                second -= 60;
                minute++;
            }
            if (minute >= 60)
            {
                minute -= 60;
                hour++;
            }
            if (hour >= 24)
            {
                hour -= 24;
                day++;
            }
            if (day >= 512) // 9-bit day overflow
            {
                day &= 0x1FF;
                carry = true;
            }
        }
    }

}
