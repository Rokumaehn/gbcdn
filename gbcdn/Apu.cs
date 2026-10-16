namespace GbcDN;

// SEE: https://gbdev.io/pandocs/Audio.html
public sealed class Apu
{
	private const int TCyclesPerSecond = 4_194_304;
	private const int FrameSequencerPeriod = 8_192; // 512 Hz
	private const int OversampleFactor = 2;
	private const float DcBlockCutoffHz = 30f;

	private static readonly byte[] DutyPatterns =
	[
		0b0000_0001, // 12.5%
		0b1000_0001, // 25%
		0b1000_0111, // 50%
		0b0111_1110  // 75%
	];

	private readonly Cpu _cpu;
	private readonly SquareChannel _square1;
	private readonly SquareChannel _square2;
	private readonly WaveChannel _wave;
	private readonly NoiseChannel _noise;
	private int _frameSequencerCounter;
	private int _frameSequencerStep;
	private int _sampleAccumulator;
	private float _oversampleL;
	private float _oversampleR;
	private int _oversampleCount;
	private float _dcPrevInL;
	private float _dcPrevInR;
	private float _dcPrevOutL;
	private float _dcPrevOutR;
	private readonly float _dcBlockCoefficient;

	public readonly AudioRingBuffer Buffer;
	public int SampleRate { get; }

	private bool Powered => (_cpu.Memory[0xFF26] & 0x80) != 0;

	public Apu(Cpu cpu, int sampleRate = 44100)
	{
		_cpu = cpu;
		SampleRate = sampleRate;
		Buffer = new AudioRingBuffer(sampleRate / 2);
		_dcBlockCoefficient = MathF.Exp(-2f * MathF.PI * DcBlockCutoffHz / sampleRate);

		_square1 = new SquareChannel(cpu, 0xFF11, true);
		_square2 = new SquareChannel(cpu, 0xFF16, false);
		_wave = new WaveChannel(cpu);
		_noise = new NoiseChannel(cpu);

		_cpu.OnMemoryWrite[0x10] = value => { if (Powered) _cpu.Memory[0xFF10] = (byte)(value & 0x7F); }; // NR10
		_cpu.OnMemoryWrite[0x11] = value => { if (Powered) _square1.WriteDutyAndLength(value); }; // NR11
		_cpu.OnMemoryWrite[0x12] = value => { if (Powered) _square1.WriteEnvelope(value); }; // NR12
		_cpu.OnMemoryWrite[0x13] = value => { if (Powered) _cpu.Memory[0xFF13] = value; }; // NR13
		_cpu.OnMemoryWrite[0x14] = value => { if (Powered) _square1.WriteControl(value); }; // NR14

		_cpu.OnMemoryWrite[0x16] = value => { if (Powered) _square2.WriteDutyAndLength(value); }; // NR21
		_cpu.OnMemoryWrite[0x17] = value => { if (Powered) _square2.WriteEnvelope(value); }; // NR22
		_cpu.OnMemoryWrite[0x18] = value => { if (Powered) _cpu.Memory[0xFF18] = value; }; // NR23
		_cpu.OnMemoryWrite[0x19] = value => { if (Powered) _square2.WriteControl(value); }; // NR24

		_cpu.OnMemoryWrite[0x1A] = value => { if (Powered) _wave.WriteDacEnable(value); }; // NR30
		_cpu.OnMemoryWrite[0x1B] = value => { if (Powered) _cpu.Memory[0xFF1B] = value; }; // NR31
		_cpu.OnMemoryWrite[0x1C] = value => { if (Powered) _cpu.Memory[0xFF1C] = (byte)(value & 0x60); }; // NR32
		_cpu.OnMemoryWrite[0x1D] = value => { if (Powered) _cpu.Memory[0xFF1D] = value; }; // NR33
		_cpu.OnMemoryWrite[0x1E] = value => { if (Powered) _wave.WriteControl(value); }; // NR34

		_cpu.OnMemoryWrite[0x20] = value => { if (Powered) _cpu.Memory[0xFF20] = (byte)(value & 0x3F); }; // NR41
		_cpu.OnMemoryWrite[0x21] = value => { if (Powered) _noise.WriteEnvelope(value); }; // NR42
		_cpu.OnMemoryWrite[0x22] = value => { if (Powered) _cpu.Memory[0xFF22] = value; }; // NR43
		_cpu.OnMemoryWrite[0x23] = value => { if (Powered) _noise.WriteControl(value); }; // NR44

		_cpu.OnMemoryWrite[0x24] = value => { if (Powered) _cpu.Memory[0xFF24] = value; }; // NR50
		_cpu.OnMemoryWrite[0x25] = value => { if (Powered) _cpu.Memory[0xFF25] = value; }; // NR51
		_cpu.OnMemoryWrite[0x26] = WriteSoundOnOff; // NR52

		_cpu.Memory[0xFF26] = 0x80;
	}

	public void ExecuteSingleStep(int cycleDelta)
	{
		var tCycles = cycleDelta * 4; // 4 T-cycles per M-cycle

		for (var i = 0; i < tCycles; i++)
		{
			if (Powered)
			{
				if (++_frameSequencerCounter >= FrameSequencerPeriod)
				{
					_frameSequencerCounter = 0;
					_frameSequencerStep = (_frameSequencerStep + 1) & 7;

					if ((_frameSequencerStep & 1) == 0) // steps 0, 2, 4, 6
					{
						_square1.ClockLength();
						_square2.ClockLength();
						_wave.ClockLength();
						_noise.ClockLength();
					}

					if (_frameSequencerStep is 2 or 6)
						_square1.StepSweep();
					else if (_frameSequencerStep == 7)
					{
						_square1.StepEnvelope();
						_square2.StepEnvelope();
						_noise.StepEnvelope();
					}
				}

				_square1.Tick();
				_square2.Tick();
				_wave.Tick();
				_noise.Tick();
			}

			_sampleAccumulator += SampleRate * OversampleFactor;
			if (_sampleAccumulator >= TCyclesPerSecond)
			{
				_sampleAccumulator -= TCyclesPerSecond;
				EmitSample();
			}
		}

		var status = (_square1.Enabled ? 1 : 0) | (_square2.Enabled ? 2 : 0) | (_wave.Enabled ? 4 : 0) | (_noise.Enabled ? 8 : 0);
		_cpu.Memory[0xFF26] = (byte)((_cpu.Memory[0xFF26] & 0x80) | status);
	}

	private void EmitSample()
	{
		var nr51 = _cpu.Memory[0xFF25];
		var nr50 = _cpu.Memory[0xFF24];

		var left = 0f;
		var right = 0f;
		Accumulate(_square1, 0, nr51, ref left, ref right);
		Accumulate(_square2, 1, nr51, ref left, ref right);
		Accumulate(_wave, 2, nr51, ref left, ref right);
		Accumulate(_noise, 3, nr51, ref left, ref right);

		var leftVolume = ((nr50 >> 4) & 0b111) / 7f;
		var rightVolume = (nr50 & 0b111) / 7f;

		left = Math.Clamp(left * 0.25f * leftVolume, -1f, 1f);
		right = Math.Clamp(right * 0.25f * rightVolume, -1f, 1f);

		// average oversampled samples into one output sample (simple decimation low-pass)
		_oversampleL += left;
		_oversampleR += right;
		if (++_oversampleCount < OversampleFactor)
			return;
		left = _oversampleL / OversampleFactor;
		right = _oversampleR / OversampleFactor;
		_oversampleL = 0f;
		_oversampleR = 0f;
		_oversampleCount = 0;

		// DC-blocking high-pass to avoid clicks when channels are enabled/disabled
		var outLeft = left - _dcPrevInL + (_dcBlockCoefficient * _dcPrevOutL);
		var outRight = right - _dcPrevInR + (_dcBlockCoefficient * _dcPrevOutR);
		_dcPrevInL = left;
		_dcPrevInR = right;
		_dcPrevOutL = outLeft;
		_dcPrevOutR = outRight;

		Buffer.Write((short)(Math.Clamp(outLeft, -1f, 1f) * short.MaxValue), (short)(Math.Clamp(outRight, -1f, 1f) * short.MaxValue));
	}

	private static void Accumulate(Channel channel, int channelIndex, byte nr51, ref float left, ref float right)
	{
		if (!channel.Enabled)
			return;

		var level = (channel.DigitalOutput * (2f / 15f)) - 1f;
		if ((nr51 & (0x10 << channelIndex)) != 0)
			left += level;
		if ((nr51 & (1 << channelIndex)) != 0)
			right += level;
	}

	private void WriteSoundOnOff(byte value)
	{
		if ((value & 0x80) == 0)
		{
			if (!Powered)
				return;

			for (var address = 0xFF10; address <= 0xFF25; address++)
				_cpu.Memory[address] = 0;
			_cpu.Memory[0xFF26] = 0;
			DisableAllChannels();
			return;
		}

		if (Powered)
			return;

		_cpu.Memory[0xFF26] = 0x80;
		DisableAllChannels();
		_wave.ResetSampleBuffer();
	}

	private void DisableAllChannels()
	{
		_square1.Enabled = false;
		_square1.DacEnabled = false;
		_square2.Enabled = false;
		_square2.DacEnabled = false;
		_wave.Enabled = false;
		_wave.DacEnabled = false;
		_noise.Enabled = false;
		_noise.DacEnabled = false;
	}

	private abstract class Channel
	{
		protected readonly Cpu _cpu;

		public bool Enabled;
		public int LengthCounter;

		protected Channel(Cpu cpu) => _cpu = cpu;

		protected abstract int LengthRegister { get; }
		protected abstract int LengthEnableRegister { get; }
		protected abstract int MaxLength { get; }

		protected bool LengthEnabled => (_cpu.Memory[LengthEnableRegister] & 0x40) != 0;

		protected int InitialLength
		{
			get
			{
				var length = _cpu.Memory[LengthRegister] & (MaxLength - 1);
				return length == 0 ? MaxLength : length;
			}
		}

		public void ClockLength()
		{
			if (!LengthEnabled || LengthCounter == 0)
				return;
			if (--LengthCounter == 0)
				Enabled = false;
		}

		public abstract void Trigger();
		public abstract void Tick();
		public abstract int DigitalOutput { get; }
	}

	private abstract class EnvelopeChannel : Channel
	{
		public bool DacEnabled;
		private int _envelopeTimer;

		protected EnvelopeChannel(Cpu cpu) : base(cpu) { }

		protected abstract int EnvelopeRegister { get; }

		public int Volume { get; private set; }

		private int EnvelopePeriod => _cpu.Memory[EnvelopeRegister] & 0b111;
		private bool EnvelopeIncrease => (_cpu.Memory[EnvelopeRegister] & 0b1000) != 0;

		public void WriteEnvelope(byte value)
		{
			_cpu.Memory[EnvelopeRegister] = value;
			DacEnabled = (value & 0xF8) != 0;
			if (!DacEnabled)
				Enabled = false;
		}

		public void StepEnvelope()
		{
			var period = EnvelopePeriod;
			if (period == 0)
				return;
			if (--_envelopeTimer > 0)
				return;

			_envelopeTimer = period;
			if (EnvelopeIncrease)
			{
				if (Volume < 15)
					Volume++;
			}
			else if (Volume > 0)
				Volume--;
		}

		protected void TriggerEnvelope()
		{
			Volume = _cpu.Memory[EnvelopeRegister] >> 4;
			_envelopeTimer = EnvelopePeriod;
			DacEnabled = (_cpu.Memory[EnvelopeRegister] & 0xF8) != 0;
		}
	}

	private sealed class SquareChannel : EnvelopeChannel
	{
		private readonly int _nr1;
		private readonly int _nr2;
		private readonly int _nr3;
		private readonly int _nr4;
		private readonly bool _hasSweep;
		private int _timer;
		private int _dutyPosition;
		private int _sweepTimer;
		private int _shadowFrequency;

		public SquareChannel(Cpu cpu, int nr1Address, bool hasSweep) : base(cpu)
		{
			_nr1 = nr1Address;
			_nr2 = nr1Address + 1;
			_nr3 = nr1Address + 2;
			_nr4 = nr1Address + 3;
			_hasSweep = hasSweep;
		}

		protected override int LengthRegister => _nr1;
		protected override int LengthEnableRegister => _nr4;
		protected override int EnvelopeRegister => _nr2;
		protected override int MaxLength => 64;

		private int Frequency
		{
			get => ((_cpu.Memory[_nr4] & 0x07) << 8) | _cpu.Memory[_nr3];
			set
			{
				_cpu.Memory[_nr3] = (byte)value;
				_cpu.Memory[_nr4] = (byte)((_cpu.Memory[_nr4] & 0xF8) | (value >> 8));
			}
		}

		private int SweepPace => (_cpu.Memory[0xFF10] >> 4) & 0b111;
		private bool SweepNegate => (_cpu.Memory[0xFF10] & 0b1000) != 0;
		private int SweepShift => _cpu.Memory[0xFF10] & 0b111;

		public void WriteDutyAndLength(byte value) => _cpu.Memory[_nr1] = value;

		public void WriteControl(byte value)
		{
			_cpu.Memory[_nr4] = (byte)(value & 0xC7); // bits 5-3 are read-only 0
			if ((value & 0x80) != 0)
				Trigger();
		}

		public override void Trigger()
		{
			Enabled = DacEnabled;
			LengthCounter = InitialLength;
			TriggerEnvelope();
			_timer &= 3; // the low two bits of the frequency timer are not modified by a trigger
			if (_hasSweep)
			{
				_shadowFrequency = Frequency;
				_sweepTimer = SweepPace == 0 ? 8 : SweepPace;
				if (SweepShift != 0)
					CalculateSweep();
			}
		}

		public void StepSweep()
		{
			if (!_hasSweep || !Enabled)
				return;
			if (--_sweepTimer > 0)
				return;

			_sweepTimer = SweepPace == 0 ? 8 : SweepPace;
			if (SweepPace == 0)
				return;

			var newFrequency = CalculateSweep();
			if (!Enabled || SweepShift == 0)
				return;

			_shadowFrequency = newFrequency;
			Frequency = newFrequency;
		}

		private int CalculateSweep()
		{
			var delta = _shadowFrequency >> SweepShift;
			var newFrequency = SweepNegate ? _shadowFrequency - delta : _shadowFrequency + delta;
			if (newFrequency > 2047 || newFrequency < 0)
				Enabled = false;
			return newFrequency;
		}

		public override void Tick()
		{
			if (++_timer >= (2048 - Frequency) * 4)
			{
				_timer = 0;
				_dutyPosition = (_dutyPosition + 1) & 7;
			}
		}

		public override int DigitalOutput
		{
			get
			{
				if (!Enabled || !DacEnabled)
					return 0;
				var pattern = DutyPatterns[_cpu.Memory[_nr1] >> 6];
				return ((pattern >> (7 - _dutyPosition)) & 1) != 0 ? Volume : 0;
			}
		}
	}

	private sealed class WaveChannel : Channel
	{
		private const int Nr30 = 0xFF1A;
		private const int Nr31 = 0xFF1B;
		private const int Nr32 = 0xFF1C;
		private const int Nr33 = 0xFF1D;
		private const int Nr34 = 0xFF1E;

		private int _timer;
		private int _position;
		private int _sampleBuffer;

		public bool DacEnabled;

		public WaveChannel(Cpu cpu) : base(cpu) { }

		protected override int LengthRegister => Nr31;
		protected override int LengthEnableRegister => Nr34;
		protected override int MaxLength => 256;

		private int Frequency
		{
			get => ((_cpu.Memory[Nr34] & 0x07) << 8) | _cpu.Memory[Nr33];
			set
			{
				_cpu.Memory[Nr33] = (byte)value;
				_cpu.Memory[Nr34] = (byte)((_cpu.Memory[Nr34] & 0xF8) | (value >> 8));
			}
		}

		public void WriteDacEnable(byte value)
		{
			_cpu.Memory[Nr30] = (byte)(value & 0x80);
			DacEnabled = (value & 0x80) != 0;
			if (!DacEnabled)
				Enabled = false;
		}

		public void WriteControl(byte value)
		{
			_cpu.Memory[Nr34] = (byte)(value & 0xC7); // bits 5-3 are read-only 0, bits 2-0 are the period's upper 3 bits
			if ((value & 0x80) != 0)
				Trigger();
		}

		public void ResetSampleBuffer() => _sampleBuffer = 0;

		public override void Trigger()
		{
			Enabled = DacEnabled;
			LengthCounter = InitialLength;
			_timer = 0;
			_position = 0; // the sample buffer is not refreshed; the first sample read after a trigger is index 1
		}

		public override void Tick()
		{
			if (++_timer >= (2048 - Frequency) * 2)
			{
				_timer = 0;
				_position = (_position + 1) & 31;
				var waveByte = _cpu.Memory[0xFF30 + (_position >> 1)];
				_sampleBuffer = (_position & 1) == 0 ? waveByte >> 4 : waveByte & 0x0F;
			}
		}

		public override int DigitalOutput
		{
			get
			{
				if (!Enabled || !DacEnabled)
					return 0;

				var volumeShift = (_cpu.Memory[Nr32] >> 5) & 0b11;
				if (volumeShift == 0)
					return 0;

				return _sampleBuffer >> (volumeShift - 1);
			}
		}
	}

	private sealed class NoiseChannel : EnvelopeChannel
	{
		private const int Nr41 = 0xFF20;
		private const int Nr42 = 0xFF21;
		private const int Nr43 = 0xFF22;
		private const int Nr44 = 0xFF23;

		private int _timer;
		private ushort _lfsr;

		public NoiseChannel(Cpu cpu) : base(cpu) { }

		protected override int LengthRegister => Nr41;
		protected override int LengthEnableRegister => Nr44;
		protected override int EnvelopeRegister => Nr42;
		protected override int MaxLength => 64;

		private int Period
		{
			get
			{
				var nr43 = _cpu.Memory[Nr43];
				var divisorCode = nr43 & 0b111;
				var divisor = divisorCode == 0 ? 8 : divisorCode * 16;
				return divisor << (nr43 >> 4);
			}
		}

		public void WriteControl(byte value)
		{
			_cpu.Memory[Nr44] = (byte)(value & 0xC0);
			if ((value & 0x80) != 0)
				Trigger();
		}

		public override void Trigger()
		{
			Enabled = DacEnabled;
			LengthCounter = InitialLength;
			TriggerEnvelope();
			_timer = 0;
			_lfsr = 0;
		}

		public override void Tick()
		{
			if ((_cpu.Memory[Nr43] >> 4) >= 14) // clock shifts 14 and 15 stop the LFSR from being clocked
				return;

			_timer++;
			if (_timer < Period)
				return;

			_timer = 0;
			var feedback = ((_lfsr ^ (_lfsr >> 1)) & 1) ^ 1; // XNOR of bits 0 and 1
			_lfsr = (ushort)((_lfsr >> 1) | (feedback << 14));
			if ((_cpu.Memory[Nr43] & 0b1000) != 0)
				_lfsr = (ushort)((_lfsr & ~0x40) | (feedback << 6));
		}

		public override int DigitalOutput
			=> !Enabled || !DacEnabled ? 0 : ((_lfsr & 1) != 0 ? Volume : 0);
	}
}

/// <summary>
/// Fixed-capacity ring buffer for interleaved stereo 16-bit PCM samples. Oldest samples are dropped on overflow.
/// Thread-safe: written by the emulation thread, read by the audio thread.
/// </summary>
public sealed class AudioRingBuffer
{
	private readonly short[] _samples;
	private readonly object _sync = new();
	private int _head;
	private int _count;

	public AudioRingBuffer(int capacity)
	{
		_samples = new short[Math.Max(2, capacity)];
	}

	public int Capacity => _samples.Length;

	public int Count
	{
		get { lock (_sync) return _count; }
	}

	public void Write(short left, short right)
	{
		lock (_sync)
		{
			Add(left);
			Add(right);
		}
	}

	public int Read(Span<short> destination)
	{
		lock (_sync)
		{
			var count = Math.Min(destination.Length, _count);
			for (var i = 0; i < count; i++)
				destination[i] = _samples[(_head + i) % _samples.Length];
			_head = (_head + count) % _samples.Length;
			_count -= count;
			return count;
		}
	}

	private void Add(short sample)
	{
		if (_count == _samples.Length)
			_head = (_head + 1) % _samples.Length;
		else
			_count++;
		_samples[(_head + _count - 1) % _samples.Length] = sample;
	}
}
