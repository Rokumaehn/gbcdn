namespace GbcDN;

public sealed class Rtc
{
	private readonly Cpu _cpu;

    bool carry = false;
    int day = 0;
    byte hour = 0;
    byte minute = 0;
    byte second = 0;
    DateTime refTime = DateTime.Now;

	public Rtc(Cpu cpu)
	{
		_cpu = cpu;
		_cpu.OnRtcWrite = WriteRegister;
		_cpu.OnRtcRead = ReadRegister;
        _cpu.OnLoad += Load;
        _cpu.OnSave += Save;
	}

    private void Save(string? pathToRom)
    {
        if (pathToRom == null)
            return;

        Path.ChangeExtension(pathToRom, ".rtc");
        using (StreamWriter writer = new StreamWriter(Path.ChangeExtension(pathToRom, ".rtc")))
        {
            writer.WriteLine($"{refTime.Ticks} {day} {hour} {minute} {second} {carry}");
        }
    }

    private void Load(string? pathToRom)
    {
        if (pathToRom == null || !File.Exists(Path.ChangeExtension(pathToRom, ".rtc")))
            return;

        using (StreamReader reader = new StreamReader(Path.ChangeExtension(pathToRom, ".rtc")))
        {
            var line = reader.ReadLine();
            if (line != null)
            {
                var parts = line.Split(' ');
                if (parts.Length == 6)
                {
                    refTime = new DateTime(long.Parse(parts[0]));
                    day = int.Parse(parts[1]);
                    hour = byte.Parse(parts[2]);
                    minute = byte.Parse(parts[3]);
                    second = byte.Parse(parts[4]);
                    carry = bool.Parse(parts[5]);
                }
            }
        }
    }

	private void WriteRegister(byte reg, byte val)
    {
        switch (reg)
        {
            case 0x08:
                second = val;
                break;
            case 0x09:
                minute = val;
                break;
            case 0x0A:
                hour = val;
                break;
            case 0x0B:
                day = val;
                break;
            case 0x0C:
                if((val & 0x01) != 0)
                    day |= 0x100; // Set the 9th bit of the day
                else
                    day &= 0xFF; // Clear the 9th bit of the day
                break;
            default:
                break;
        }

        refTime = DateTime.Now;
    }

    private byte ReadRegister(byte reg)
    {
        var sample = DateTime.Now;
        TimeSpan elapsed = sample - refTime;
        if(elapsed.Seconds!=0 || elapsed.Minutes!=0 || elapsed.Hours!=0 || elapsed.Days!=0)
        {
            second += (byte)elapsed.Seconds;
            minute += (byte)elapsed.Minutes;
            hour += (byte)elapsed.Hours;
            day += (int)elapsed.Days;
            refTime = sample;

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
                day -= 512;
                carry = true;
            }
        }

        switch (reg)
        {
            case 0x08:
                return second;
            case 0x09:
                return minute;
            case 0x0A:
                return hour;
            case 0x0B:
                return (byte)day;
            case 0x0C:
                return (byte)((carry ? 0x80 : 0x00) | ((day & 0x100) >> 8));
            default:
                return 0;
        }
    }

}
