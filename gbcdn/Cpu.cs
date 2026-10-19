using System.Numerics;
using System.Runtime.CompilerServices;

namespace GbcDN;

#pragma warning disable S907, S1479, S3776

public sealed class Cpu
{
	[Flags]
	public enum IFBits
	{
		None = 0,
		VBlank = 1 << 0,
		LCD = 1 << 1,
		Timer = 1 << 2,
		Serial = 1 << 3,
		Joypad = 1 << 4,
		Reserved5 = 1 << 5,
		Reserved6 = 1 << 6,
		Reserved7 = 1 << 7,
		Reserved = Reserved5 | Reserved6 | Reserved7,
	}

	private readonly byte[] _rom;
	private readonly byte _romBankOneMask;
	private int _romBankOneOffset;
	private readonly byte[] _ram;
	private bool _ramEnabled;
	private int _ramBankOffset;
	private byte _ramBankSel;
	public readonly byte[] _sram = new byte[0x8000];
	public int _sramBankOffset = 1;
	private int _vramBankOffset = 0;
	private int _hdmaSource;
	private int _hdmaDest;
	private int _hdmaRemaining;
	private bool _hdmaActive;
	private bool _isHalted;
	private int _enableIME; // cycles until IME is enabled (after EI instruction)

	public readonly byte[] Memory = new byte[0x10000];
	public readonly byte[] VRamUpper = new byte[0x2000];
	public readonly byte[] CRamBg = new byte[64];
	public byte CRamBgIndex = 0;
	public readonly byte[] CRamObj = new byte[64];
	public byte CRamObjIndex = 0;

	private bool IME; // Interrupt Master Enable
	private bool FlagZ = true; // Zero Flag
	private bool FlagN; // Subtract Flag
	private bool FlagH = true; // Half Carry Flag
	private bool FlagC = true; // Carry Flag
	private byte A = 0x11, B, C = 0x13, D, E = 0xD8, H = 0x01, L = 0x4D;
	private ushort PC = 0x0100;
	private ushort SP = 0xFFFE;

	private ushort AF
	{
		get => (ushort)((A << 8) | (FlagZ ? 0x80 : 0) | (FlagN ? 0x40 : 0) | (FlagH ? 0x20 : 0) | (FlagC ? 0x10 : 0));
		set
		{
			A = (byte)(value >> 8);
			FlagZ = (value & 0x80) != 0;
			FlagN = (value & 0x40) != 0;
			FlagH = (value & 0x20) != 0;
			FlagC = (value & 0x10) != 0;
		}
	}
	private ushort BC { get => (ushort)(C | (B << 8)); set { C = (byte)value; B = (byte)(value >> 8); } }
	private ushort DE { get => (ushort)(E | (D << 8)); set { E = (byte)value; D = (byte)(value >> 8); } }
	private ushort HL { get => (ushort)(L | (H << 8)); set { L = (byte)value; H = (byte)(value >> 8); } }

	private IFBits IF { get => (IFBits)Memory[0xFF0F]; set => Memory[0xFF0F] = (byte)value; } // Interrupt Flag Register
	private IFBits IE => (IFBits)Memory[0xFFFF]; // Interrupt Enable Register

	public readonly Action<byte>?[] OnMemoryWrite = new Action<byte>?[256];

	public Action<byte, byte>? OnRtcWrite;
	public Action<int, byte>? OnRtcLatchWrite;
	public Func<byte, byte>? OnRtcRead;
	public event Action<string?>? OnLoad;
	public event Action<string?>? OnSave;
	private readonly string? _pathToRom;
	public readonly byte _cartridgeType;
	public readonly bool _hasRtc;

	public enum MBCs
	{
		None,
		MBC1,
		MBC2,
		MBC3,
		MBC5
	}

	public MBCs Mbc { get; private set; }

	public Cpu(byte[] rom, string? pathToRom = null)
	{
		_pathToRom = pathToRom;
		// initialize ROM
		_rom = rom;
		_cartridgeType = _rom[0x147];
		_hasRtc = _cartridgeType is 0x0F or 0x10;
		switch (_cartridgeType)
		{
			case 0x00:
				Mbc = MBCs.None;
				break;
			case 0x01:
			case 0x02:
			case 0x03:
				Mbc = MBCs.MBC1;
				break;
			case 0x05:
			case 0x06:
				Mbc = MBCs.MBC2;
				break;
			case 0x0F:
			case 0x10:
			case 0x11:
			case 0x12:
			case 0x13:
				Mbc = MBCs.MBC3;
				break;
			case 0x19:
			case 0x1A:
			case 0x1B:
			case 0x1C:
			case 0x1D:
			case 0x1E:
				Mbc = MBCs.MBC5;
				break;
			default:
				throw new InvalidOperationException("Unsupported cartridge type");
		}
		//if (_rom[0x147] > 0x03) // check cartridge type
		//	throw new InvalidOperationException("Only supports ROMs without MBC or with MBC1");
		_romBankOneMask = (byte)((2 << _rom[0x148]) - 1); // determine ROM bank count from ROM size code
		Array.Copy(rom, Memory, 0x4000); // load first ROM bank at 0x0000-0x3FFF

		// initialize RAM
		_ramEnabled = _rom[0x147] is 0 or 1; // check if RAM is enabled based on cartridge type
		var ramBanks = _rom[0x149] switch // determine RAM size from RAM size code
		{
			0x00 => 0,
			0x02 => 1,
			0x03 => 4,
			0x04 => 16,
			0x05 => 8,
			_ => throw new InvalidOperationException("Invalid RAM size code in ROM header")
		};
		_ram = new byte[ramBanks * 0x2000];

		// initialize memory write handlers for special registers
		OnMemoryWrite[0x02] = value => Memory[0xFF02] = (byte)(value | 0b0111_1110); // SC
		OnMemoryWrite[0x0F] = value => Memory[0xFF0F] = (byte)(value | 0b1110_0000); // IF
		OnMemoryWrite[0xFF] = value => Memory[0xFFFF] = (byte)(value & 0b0001_1111); // IE
		
		OnMemoryWrite[0x70] = value =>
		{
			var bank = value & 0b111;
			Memory[0xFF70] = (byte)bank;
			_sramBankOffset = bank == 0 ? 1 : bank;
		}; // SVBK
		OnMemoryWrite[0x4F] = value =>
		{
			_vramBankOffset = value & 0b1;
			Memory[0xFF4F] = (byte)(0xFE | _vramBankOffset);
		}; // VBK
		OnMemoryWrite[0x51] = value => _hdmaSource = (_hdmaSource & 0x00F0) | (value << 8); // HDMA1
		OnMemoryWrite[0x52] = value => _hdmaSource = (_hdmaSource & 0xFF00) | (value & 0xF0); // HDMA2
		OnMemoryWrite[0x53] = value => _hdmaDest = (_hdmaDest & 0x00F0) | ((value & 0x1F) << 8); // HDMA3
		OnMemoryWrite[0x54] = value => _hdmaDest = (_hdmaDest & 0x1F00) | (value & 0xF0); // HDMA4
		OnMemoryWrite[0x55] = WriteHdmaControl; // HDMA5
		OnMemoryWrite[0x68] = value => { CRamBgIndex = value; }; // Background Color RAM
		OnMemoryWrite[0x69] = value => { CRamBg[CRamBgIndex & 0b111111] = value; if((CRamBgIndex & 0x80) != 0) CRamBgIndex++; }; // Background Color RAM
		OnMemoryWrite[0x6A] = value => { CRamObjIndex = value; }; // Object Color RAM
		OnMemoryWrite[0x6B] = value => { CRamObj[CRamObjIndex & 0b111111] = value; if((CRamObjIndex & 0x80) != 0) CRamObjIndex++; }; // Object Color RAM

		CRamBg.AsSpan().Fill(0xFF);
		CRamObj.AsSpan().Fill(0xFF);
		// enforce default values for IO registers
		IF = IFBits.VBlank;
		for (int i = 0; i <= 0xFF; i++)
			WriteMem(0xFF00 + i, 0);

		_hdmaActive = false;
		Memory[0xFF55] = 0xFF;
		
		if(_cartridgeType is 0x03 or 0x10 or 0x13) // MBC1/3 with battery-backed RAM
			Load();
	}

	public void Save()
	{
		if((_cartridgeType is 0x03 or 0x10 or 0x13) && _ram.Length > 0 && _pathToRom != null)
		{
			using (var writer = new BinaryWriter(File.Open(Path.ChangeExtension(_pathToRom, ".ram"), FileMode.OpenOrCreate)))
			{
				writer.Write(_ram);
			}
		}

		OnSave?.Invoke(_pathToRom);
	}

	public void Load()
	{
		// MBC1/3 with battery-backed RAM
		if((_cartridgeType is 0x03 or 0x10 or 0x13) && _ram.Length > 0 && _pathToRom != null && File.Exists(Path.ChangeExtension(_pathToRom, ".ram")))
		{
			using (var reader = new BinaryReader(File.Open(Path.ChangeExtension(_pathToRom, ".ram"), FileMode.Open)))
			{
				reader.Read(_ram, 0, _ram.Length);
			}
		}

		OnLoad?.Invoke(_pathToRom);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private ref byte GetSourceOperand(int opcode, out bool isHl)
	{
		isHl = false;
		switch (opcode & 0b111)
		{
			case 0: return ref B;
			case 1: return ref C;
			case 2: return ref D;
			case 3: return ref E;
			case 4: return ref H;
			case 5: return ref L;
			case 6: isHl = true; return ref ReadMemRef(HL);
			default: return ref A;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void RequestInterrupt(IFBits flag)
		=> IF |= flag;

	public void ProcessHdmaBlock()
	{
		if (!_hdmaActive)
			return;

		CopyHdmaBlock();
		_hdmaRemaining--;
		if (_hdmaRemaining == 0)
		{
			_hdmaActive = false;
			Memory[0xFF55] = 0xFF;
		}
		else
		{
			Memory[0xFF55] = (byte)(0x80 | (_hdmaRemaining - 1));
		}
	}

	private void WriteHdmaControl(byte value)
	{
		if (_hdmaActive)
		{
			// writing 0 to bit 7 terminates an active HBlank DMA
			if ((value & 0x80) == 0)
			{
				_hdmaActive = false;
				Memory[0xFF55] = (byte)(0x80 | (_hdmaRemaining - 1));
			}
			return;
		}

		var blocks = (value & 0x7F) + 1;
		if ((value & 0x80) == 0)
		{
			// general-purpose DMA: transfer everything at once
			for (var i = 0; i < blocks; i++)
				CopyHdmaBlock();
			Memory[0xFF55] = 0xFF;
		}
		else
		{
			_hdmaRemaining = blocks;
			_hdmaActive = true;
			Memory[0xFF55] = (byte)(0x80 | (_hdmaRemaining - 1));
		}
	}

	private void CopyHdmaBlock()
	{
		for (var i = 0; i < 0x10; i++)
		{
			var source = ReadMemoryForDma(_hdmaSource + i);
			if (_vramBankOffset == 0)
				Memory[0x8000 | ((_hdmaDest + i) & 0x1FFF)] = source;
			else
				VRamUpper[(_hdmaDest + i) & 0x1FFF] = source;
		}

		_hdmaSource = (_hdmaSource + 0x10) & 0xFFF0;
		_hdmaDest = (_hdmaDest + 0x10) & 0x1FF0;
	}

	internal byte ReadMemoryForDma(int address)
	{
		address &= 0xFFFF;
		if (address < 0x4000)
			return Memory[address];
		if (address < 0x8000)
			return _rom[_romBankOneOffset + address];
		if (address < 0xA000)
			return _vramBankOffset == 0 ? Memory[address] : VRamUpper[address & 0x1FFF];
		if (address < 0xC000)
			return Memory[address];
		if (address < 0xD000)
			return _sram[address & 0x0FFF];
		if (address < 0xE000)
			return _sram[(address & 0x0FFF) + (_sramBankOffset << 12)];
		return Memory[address];
	}

	public int ExecuteSingleStep()
	{
		// IME gets enabled after the instruction following EI
		if (_enableIME > 0 && --_enableIME == 0)
			IME = true;

		var interruptFlags = IF & IE;
		if (interruptFlags != 0)
		{
			// wake up from HALT state when an unmasked interrupt is pending
			_isHalted = false;

			// handle interrupts when IME is enabled, use the lowest set interrupt bit (highest priority)
			if (IME)
			{
				IME = false;
				var bitNum = BitOperations.TrailingZeroCount((int)interruptFlags);
				IF ^= (IFBits)(1 << bitNum);
				PushToStack(PC);
				PC = (ushort)(0x40 + (bitNum * 8));
				return 5;
			}
		}

		// return immediately if CPU is halted. return 1 to keep the PPU and other components clocked correctly.
		if (_isHalted)
			return 1;

		// read opcode
		var op = ReadRom(PC++);

		// handle opcode range 0x40..0xBF
		switch (op >> 6)
		{
			case 0b01: // 0x4x .. 0x7x = LD Block
				if (op == 0x76) // HALT
				{
					_isHalted = true;
					return 1;
				}

				ref var dst = ref GetSourceOperand(op >> 3, out var isHlDst);
				WriteMem(ref dst, GetSourceOperand(op, out var isHlSrc), isHlDst);
				return (isHlSrc || isHlDst) ? 2 : 1;

			case 0b10: // 0x8x .. 0xBx = Math Block
				var arg = GetSourceOperand(op, out var isHl);
				switch ((op >> 3) & 0b111)
				{
					case 0: Add(ref A, arg, false, true); break; // ADD A, r8
					case 1: Add(ref A, arg, FlagC, true); break; // ADC A, r8
					case 2: Sub(ref A, arg, false, true); break; // SUB A, r8
					case 3: Sub(ref A, arg, FlagC, true); break; // SBC A, r8
					case 4: AndA(arg); break; // AND A, r8
					case 5: XorA(arg); break; // XOR A, r8
					case 6: OrA(arg); break; // OR A, r8
					default: CpA(arg); break; // CP A, r8
				}
				return isHl ? 2 : 1;
		}

		// handle all other opcodes
		switch (op)
		{
			case 0x00: // NOP
				return 1;

			case 0x01: // LD BC, n16
				BC = ReadImmediateUShort();
				return 3;

			case 0x02: // LD [BC], A [LD [r16],A]
				WriteMem(BC, A);
				return 2;

			case 0x03: // INC BC [INC r16]
				BC++;
				return 2;

			case 0x04: // INC B [INC r8]
				Inc(ref B);
				return 1;

			case 0x05: // DEC B [DEC r8]
				Dec(ref B);
				return 1;

			case 0x06: // LD B, n8 [LD r8,n8]
				B = ReadRom(PC++);
				return 2;

			case 0x07: // RLCA [RLCA]
				A = byte.RotateLeft(A, 1);
				FlagZ = false;
				FlagN = false;
				FlagH = false;
				FlagC = (A & 1) != 0;
				return 1;

			case 0x08: // LD [a16], SP [LD [n16],SP]
				WriteMem(ReadImmediateUShort(), SP);
				return 5;

			case 0x09: // ADD HL, BC [ADD HL,r16]
				return AddToHL(BC);

			case 0x0A: // LD A, [BC] [LD A,[r16]]
				A = ReadMem(BC);
				return 2;

			case 0x0B: // DEC BC [DEC r16]
				BC--;
				return 2;

			case 0x0C: // INC C [INC r8]
				Inc(ref C);
				return 1;

			case 0x0D: // DEC C [DEC r8]
				Dec(ref C);
				return 1;

			case 0x0E: // LD C, n8 [LD r8,n8]
				C = ReadRom(PC++);
				return 2;

			case 0x0F: // RRCA [RRCA]
				FlagZ = false;
				FlagN = false;
				FlagH = false;
				FlagC = (A & 1) != 0;
				A = byte.RotateRight(A, 1);
				return 1;

			case 0x10: // STOP n8
				throw new NotSupportedException("STOP not supported.");

			case 0x11: // LD DE, n16 [LD r16,n16]
				DE = ReadImmediateUShort();
				return 3;

			case 0x12: // LD [DE], A [LD [r16],A]
				WriteMem(DE, A);
				return 2;

			case 0x13: // INC DE [INC r16]
				DE++;
				return 2;

			case 0x14: // INC D [INC r8]
				Inc(ref D);
				return 1;

			case 0x15: // DEC D [DEC r8]
				Dec(ref D);
				return 1;

			case 0x16: // LD D, n8 [LD r8,n8]
				D = ReadRom(PC++);
				return 2;

			case 0x17: // RLA [RLA]
				{
					var a = (A << 1) | (FlagC ? 1 : 0);
					FlagZ = false;
					FlagN = false;
					FlagH = false;
					FlagC = (a & 0x100) != 0;
					A = (byte)a;
				}
				return 1;

			case 0x18: // JR e8 [JR n16]
				return ConditionalJumpRelative(true);

			case 0x19: // ADD HL, DE [ADD HL,r16]
				return AddToHL(DE);

			case 0x1A: // LD A, [DE] [LD A,[r16]]
				A = ReadMem(DE);
				return 2;

			case 0x1B: // DEC DE [DEC r16]
				DE--;
				return 2;

			case 0x1C: // INC E [INC r8]
				Inc(ref E);
				return 1;

			case 0x1D: // DEC E [DEC r8]
				Dec(ref E);
				return 1;

			case 0x1E: // LD E, n8 [LD r8,n8]
				E = ReadRom(PC++);
				return 2;

			case 0x1F: // RRA [RRA]
				{
					var a = (byte)((A >> 1) | (FlagC ? 0x80 : 0));
					FlagZ = false;
					FlagN = false;
					FlagH = false;
					FlagC = (A & 1) != 0;
					A = a;
				}
				return 1;

			case 0x20: // JR NZ, e8
				return ConditionalJumpRelative(!FlagZ);

			case 0x21: // LD HL, n16 [LD r16,n16]
				HL = ReadImmediateUShort();
				return 3;

			case 0x22: // LD [HL+], A [LD [HLI],A]
				WriteMem(HL++, A);
				return 2;

			case 0x23: // INC HL [INC r16]
				HL++;
				return 2;

			case 0x24: // INC H [INC r8]
				Inc(ref H);
				return 1;

			case 0x25: // DEC H [DEC r8]
				Dec(ref H);
				return 1;

			case 0x26: // LD H, n8 [LD r8,n8]
				H = ReadRom(PC++);
				return 2;

			case 0x27: // DAA [DAA]
				{
					var correction = (FlagH || (!FlagN && (A & 0x0F) > 9)) ? (byte)0x06 : (byte)0;
					if (FlagC || (!FlagN && A > 0x99))
					{
						correction |= 0x60;
						FlagC = true;
					}
					if (FlagN)
						A -= correction;
					else
						A += correction;
					FlagZ = A == 0;
					FlagH = false;
				}
				return 1;

			case 0x28: // JR Z, e8 [JR cc,n16]
				return ConditionalJumpRelative(FlagZ);

			case 0x29: // ADD HL, HL [ADD HL,r16]
				return AddToHL(HL);

			case 0x2A: // LD A, [HL+] [LD A,[HLI]]
				A = ReadMem(HL++);
				return 2;

			case 0x2B: // DEC HL [DEC r16]
				HL--;
				return 2;

			case 0x2C: // INC L [INC r8]
				Inc(ref L);
				return 1;

			case 0x2D: // DEC L [DEC r8]
				Dec(ref L);
				return 1;

			case 0x2E: // LD L, n8 [LD r8,n8]
				L = ReadRom(PC++);
				return 2;

			case 0x2F: // CPL [CPL]
				A = (byte)~A;
				FlagN = true;
				FlagH = true;
				return 1;

			case 0x30: // JR NC, e8
				return ConditionalJumpRelative(!FlagC);

			case 0x31: // LD SP, d16 [LD SP,n16]
				SP = ReadImmediateUShort();
				return 3;

			case 0x32: // LD [HL-], A [LD [HLD],A]
				WriteMem(HL--, A);
				return 2;

			case 0x33: // INC SP [INC SP]
				SP++;
				return 2;

			case 0x34: // INC [HL] [INC [HL]]
				{
					var b = ReadMem(HL);
					Inc(ref b);
					WriteMem(HL, b);
					return 3;
				}

			case 0x35: // DEC [HL] [DEC [HL]]
				{
					var b = ReadMem(HL);
					Dec(ref b);
					WriteMem(HL, b);
					return 3;
				}

			case 0x36: // LD [HL], n8 [LD [HL],n8]
				WriteMem(HL, ReadRom(PC++));
				return 3;

			case 0x37: // SCF [SCF]
				FlagN = false;
				FlagH = false;
				FlagC = true;
				return 1;

			case 0x38: // JR C, e8 [JR cc,n16]
				return ConditionalJumpRelative(FlagC);

			case 0x39: // ADD HL, SP [ADD HL,SP]
				return AddToHL(SP);

			case 0x3A: // LD A, [HL-] [LD A,[HLD]]
				A = ReadMem(HL--);
				return 2;

			case 0x3B: // DEC SP [DEC SP]
				SP--;
				return 2;

			case 0x3C: // INC A [INC r8]
				Inc(ref A);
				return 1;

			case 0x3D: // DEC A [DEC r8]
				Dec(ref A);
				return 1;

			case 0x3E: // LD A, n8 [LD r8,n8]
				A = ReadRom(PC++);
				return 2;

			case 0x3F: // CCF [CCF]
				FlagN = false;
				FlagH = false;
				FlagC = !FlagC;
				return 1;

			case 0xC0: // RET NZ
				return ConditionalReturn(!FlagZ);

			case 0xC1: // POP BC [POP r16]
				BC = PopFromStack();
				return 3;

			case 0xC2: // JP NZ, a16 [JP cc,n16]
				if (!FlagZ)
					goto case 0xC3;
				PC += 2;
				return 3;

			case 0xC3: // JP a16
				PC = ReadImmediateUShort();
				return 4;

			case 0xC4: // CALL NZ, a16 [CALL cc,n16]
				return ConditionalCall(!FlagZ);

			case 0xC5: // PUSH BC
				PushToStack(BC);
				return 4;

			case 0xC6: // ADD A, n8 [ADD A,n8]
				Add(ref A, ReadRom(PC++), false, true);
				return 2;

			case 0xC7: // RST $00 [RST vec]
			case 0xD7: // RST $10 [RST vec]
			case 0xE7: // RST $20 [RST vec]
			case 0xF7: // RST $30 [RST vec]
			case 0xCF: // RST $08 [RST vec]
			case 0xDF: // RST $18 [RST vec]
			case 0xEF: // RST $28 [RST vec]
			case 0xFF: // RST $38 [RST vec]
				PushToStack(PC);
				PC = (ushort)(op & 0b0011_1000);
				return 4;

			case 0xC8: // RET Z
				return ConditionalReturn(FlagZ);

			case 0xC9: // RET
				PC = PopFromStack();
				return 4;

			case 0xCA: // JP Z, a16 [JP cc,n16]
				if (FlagZ)
					goto case 0xC3;
				PC += 2;
				return 3;

			case 0xCB: // PREFIX
				return SingleStepPrefixed();

			case 0xCC: // CALL Z, a16  [CALL cc,n16]
				return ConditionalCall(FlagZ);

			case 0xCD: // CALL a16
				var address = ReadImmediateUShort();
				PushToStack(PC);
				PC = address;
				return 6;

			case 0xCE: // ADC A, n8 [ADC A,n8]
				Add(ref A, ReadRom(PC++), FlagC, true);
				return 2;

			case 0xD0: // RET NC
				return ConditionalReturn(!FlagC);

			case 0xD1: // POP DE [POP r16]
				DE = PopFromStack();
				return 3;

			case 0xD2: // JP NC, a16 [JP cc,n16]
				if (!FlagC)
					goto case 0xC3;
				PC += 2;
				return 3;

			case 0xD4: // CALL NC, a16 [CALL cc,n16]
				return ConditionalCall(!FlagC);

			case 0xD5: // PUSH DE
				PushToStack(DE);
				return 4;

			case 0xD6: // SUB A, n8 [SUB A,n8]
				Sub(ref A, ReadRom(PC++), false, true);
				return 2;

			case 0xD8: // RET C
				return ConditionalReturn(FlagC);

			case 0xD9: // RETI [RETI]
				_enableIME = 0;
				IME = true;
				PC = PopFromStack();
				return 4;

			case 0xDA: // JP C, a16 [JP cc,n16]
				if (FlagC)
					goto case 0xC3;
				PC += 2;
				return 3;

			case 0xDC: // CALL C, a16 [CALL cc,n16]
				return ConditionalCall(FlagC);

			case 0xDE: // SBC A, n8 [SBC A,n8]
				Sub(ref A, ReadRom(PC++), FlagC, true);
				return 2;

			case 0xE0: // LDH [a8], A [LDH [n16],A]
				WriteMem(0xFF00 + ReadRom(PC++), A);
				return 3;

			case 0xE1: // POP HL [POP r16]
				HL = PopFromStack();
				return 3;

			case 0xE2: // LDH [C], A [LDH [C],A]
				WriteMem(0xFF00 + C, A);
				return 2;

			case 0xE5: // PUSH HL
				PushToStack(HL);
				return 4;

			case 0xE6: // AND A, n8 [AND A, n8]
				AndA(ReadRom(PC++));
				return 2;

			case 0xE8: // ADD SP, e8 [ADD SP,e8]
				{
					var offset = ReadRom(PC++);
					var sp = (byte)SP;
					Add(ref sp, offset, false, true);
					FlagZ = false;
					SP = (ushort)(SP + (sbyte)offset);
					return 4;
				}

			case 0xE9: // JP HL [JP HL]
				PC = HL;
				return 1;

			case 0xEA: // LD [a16], A
				WriteMem(ReadImmediateUShort(), A);
				return 4;

			case 0xEE: // XOR A, n8 [XOR A,n8]
				XorA(ReadRom(PC++));
				return 2;

			case 0xF0: // LDH A, [a8] [LDH A,[n16]]
				A = Memory[0xFF00 + ReadRom(PC++)];
				return 3;

			case 0xF1: // POP AF [POP AF]
				AF = PopFromStack();
				return 3;

			case 0xF2: // LDH A, [C] [LDH A,[C]]
				A = Memory[0xFF00 + C];
				return 2;

			case 0xF3: // DI
				_enableIME = 0;
				IME = false;
				return 1;

			case 0xF5: // PUSH AF
				PushToStack(AF);
				return 4;

			case 0xF6: // OR A, n8 [OR A,n8]
				OrA(ReadRom(PC++));
				return 2;

			case 0xF8: // LD HL, SP + e8 [LD HL,SP+e8]
				{
					var offset = ReadRom(PC++);
					var sp = (byte)SP;
					Add(ref sp, offset, false, true);
					FlagZ = false;
					HL = (ushort)(SP + (sbyte)offset);
					return 3;
				}

			case 0xF9: // LD SP, HL [LD SP,HL]
				SP = HL;
				return 2;

			case 0xFA: // LD A, (a16)
				A = ReadMem(ReadImmediateUShort());
				return 4;

			case 0xFB: // EI [EI]
				_enableIME = 2;
				return 1;

			case 0xFE: // CP A, n8
				CpA(ReadRom(PC++));
				return 2;

			case 0xD3:
			case 0xDB:
			case 0xDD:
			case 0xE3:
			case 0xE4:
			case 0xEB:
			case 0xEC:
			case 0xED:
			case 0xF4:
			case 0xFC:
			case 0xFD:
				throw new InvalidOperationException($"Invalid Opcode 0x{op:X2} executed");
		}

		throw new NotImplementedException($"Opcode 0x{op:X2} not implemented");
	}

	private int SingleStepPrefixed()
	{
		var op = ReadMem(PC++);
		ref var src = ref GetSourceOperand(op, out var isHl);
		var bitIndex = (op >> 3) & 0b111;
		switch (op >> 6)
		{
			case 0b00: // 0x0x .. 0x3x
				switch (bitIndex)
				{
					case 0: // 0x00 .. 0x07 = RLC Block
						FlagC = (src & 0x80) != 0;
						WriteMem(ref src, byte.RotateLeft(src, 1), isHl);
						break;

					case 1: // 0x08 .. 0x0F = RRC Block
						FlagC = (src & 0x01) != 0;
						WriteMem(ref src, byte.RotateRight(src, 1), isHl);
						break;

					case 2: // 0x10 .. 0x17 = RL Block
						var i = (src << 1) | (FlagC ? 1 : 0);
						FlagC = (i & 0x100) != 0;
						WriteMem(ref src, (byte)i, isHl);
						break;

					case 3: // 0x18 .. 0x1F = RR Block
						var oldC = FlagC ? 0x80 : 0;
						FlagC = (src & 0x01) != 0;
						WriteMem(ref src, (byte)((src >> 1) | oldC), isHl);
						break;

					case 4: // 0x20 .. 0x27 = SLA Block
						FlagC = (src & 0x80) != 0;
						WriteMem(ref src, (byte)(src << 1), isHl);
						break;

					case 5: // 0x28 .. 0x2F = SRA Block
						FlagC = (src & 0x01) != 0;
						WriteMem(ref src, (byte)((sbyte)src >> 1), isHl); // sbyte for arithmetical shift
						break;

					case 6: // 0x30 .. 0x37 = SWAP Block
						FlagC = false;
						WriteMem(ref src, (byte)(src >> 4 | src << 4), isHl);
						break;

					default: // case 7: 0x38 .. 0x3F = SRL Block
						FlagC = (src & 0x01) != 0;
						WriteMem(ref src, (byte)(src >> 1), isHl);
						break;
				}
				FlagZ = src == 0;
				FlagN = false;
				FlagH = false;
				return isHl ? 4 : 2;

			case 0b01: // 0x4x .. 0x7x = BIT Block
				FlagZ = (src & (1 << bitIndex)) == 0;
				FlagN = false;
				FlagH = true;
				return isHl ? 3 : 2;

			case 0b10: // 0x8x .. 0xBx = RES Block
				WriteMem(ref src, (byte)(src & ~(1 << bitIndex)), isHl);
				return isHl ? 4 : 2;

			default: // case 0b11: 0xCx .. 0xFx = SET Block
				WriteMem(ref src, (byte)(src | (1 << bitIndex)), isHl);
				return isHl ? 4 : 2;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int ConditionalJumpRelative(bool cond)
	{
		if (cond)
		{
			var offset = (sbyte)ReadMem(PC++);
			PC = (ushort)(PC + offset);
			return 3;
		}
		PC++;
		return 2;
	}
	
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int ConditionalCall(bool cond)
	{
		if (cond)
		{
			var addr = ReadImmediateUShort();
			PushToStack(PC);
			PC = addr;
			return 6;
		}
		PC += 2;
		return 3;
	}
	
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int ConditionalReturn(bool cond)
	{
		if (cond)
		{
			PC = PopFromStack();
			return 5;
		}
		return 2;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void CpA(byte value)
	{
		var copy = A;
		Sub(ref copy, value, false, true);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void AndA(byte value)
	{
		A &= value;
		FlagZ = A == 0;
		FlagN = false;
		FlagH = true;
		FlagC = false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void XorA(byte value)
	{
		A ^= value;
		FlagZ = A == 0;
		FlagN = false;
		FlagH = false;
		FlagC = false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void OrA(byte value)
	{
		A |= value;
		FlagZ = A == 0;
		FlagN = false;
		FlagH = false;
		FlagC = false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private ushort ReadImmediateUShort()
	{
		var value = (ushort)(ReadMem(PC) | (ReadMem((ushort)(PC + 1)) << 8));
		PC += 2;
		return value;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void PushToStack(ushort value)
	{
		SP -= 2;
		WriteMem(SP, value);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private ushort PopFromStack()
	{
		var value = (ushort)(ReadMem(SP) | (ReadMem((ushort)(SP + 1)) << 8));
		SP += 2;
		return value;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void Inc(ref byte reg) => Add(ref reg, 1, false, false);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void Add(ref byte reg, byte value, bool carry, bool setCarry)
	{
		var carryValue = carry ? 1 : 0;
		var tmp = reg + value + carryValue;
		FlagZ = (byte)tmp == 0;
		FlagN = false;
		FlagH = (reg & 0xF) + (value & 0xF) + carryValue > 0xF;
		if (setCarry)
			FlagC = tmp > 0xFF;
		reg = (byte)tmp;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void Dec(ref byte reg) => Sub(ref reg, 1, false, false);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void Sub(ref byte reg, byte value, bool carry, bool setCarry)
	{
		var carryValue = carry ? 1 : 0;
		var tmp = reg - value - carryValue;
		FlagZ = (byte)tmp == 0;
		FlagN = true;
		FlagH = (reg & 0xF) - (value & 0xF) - carryValue < 0;
		if (setCarry)
			FlagC = tmp < 0;
		reg = (byte)tmp;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int AddToHL(ushort value)
	{
		var reg = HL;
		var sum = reg + value;
		FlagN = false;
		FlagH = (reg & 0x0FFF) + (value & 0x0FFF) > 0x0FFF;
		FlagC = sum > 0xFFFF;
		HL = (ushort)sum;
		return 2;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void WriteMem(int addr, byte value)
	{
		if (addr < 0x8000)
		{
			if (addr < 0x2000)
			{
				// enable/disable RAM
				_ramEnabled = (value & 0xF) == 0xA;
			}
			else if (addr < 0x4000)
			{
				// select ROM Bank Number
				value &= (Mbc == MBCs.MBC1 ? (byte)0x1F : (byte)0x7F);
				if (value == 0)
					value = 1;
				_romBankOneOffset = ((value & _romBankOneMask) - 1) * 0x4000;
			}
			else if (addr < 0x6000)
			{
				_ramBankSel = value;
				if(value > 7)
				{
					// handle RTC
				}
				else
				{
					// select RAM Bank number or upper bits of ROM Bank Number
					// switching happens infrequently, so we can afford to copy data back and forth
					var ramBankOffset = (value & 0x3) * 0x2000;
					if (ramBankOffset != _ramBankOffset)
					{
						Array.Copy(Memory, 0xA000, _ram, _ramBankOffset, 0x2000);
						_ramBankOffset = ramBankOffset;
						Array.Copy(_ram, _ramBankOffset, Memory, 0xA000, 0x2000);
					}
				}
			}
			else if (addr < 0x8000)
			{
				// handle RTC Latch write
				if(_hasRtc)
					OnRtcLatchWrite?.Invoke(addr, value);
			}
			return;
		}
		else if (addr >= 0x8000 && addr < 0xA000)
		{
			if (_vramBankOffset == 0)
				Memory[addr] = value;
			else
				VRamUpper[addr & 0x1FFF] = value;
			return;
		}
		else if(addr >= 0xA000 && addr < 0xC000)
		{
			if(_ramBankSel >= 0x08 && _ramBankSel <= 0x0C && _hasRtc)
			{
				// handle RTC
				OnRtcWrite?.Invoke(_ramBankSel, value);
			}
			else
				Memory[addr] = value;
		}
		else if (addr >= 0xC000 && addr < 0xD000)
		{
			_sram[addr & 0x0FFF] = value;
			return;
		}
		else if (addr >= 0xD000 && addr < 0xE000)
		{
			_sram[(addr & 0x0FFF) + (_sramBankOffset << 12)] = value;
			return;
		}

		if (addr >= 0xFF00 && OnMemoryWrite[addr & 0xFF] is { } action)
			action(value);
		else
			Memory[addr] = value;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void WriteMem(ref byte dest, byte value, bool isHl)
	{
		if (isHl)
			WriteMem(HL, value);
		else
			dest = value;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void WriteMem(int addr, ushort value)
	{
		WriteMem(addr, (byte)value);
		WriteMem(addr + 1, (byte)(value >> 8));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private byte ReadRom(ushort address) // serve 0x4000 .. 0x7FFF from banked ROM area
	{
		if(address >> 14 == 0b01)
			return _rom[_romBankOneOffset + address];
		return Memory[address];
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private byte ReadMem(ushort address) // serve 0x4000 .. 0x7FFF from banked ROM area
	{
		if(address >= 0xA000 && address < 0xC000)
		{
			if(_ramBankSel >= 0x08 && _ramBankSel <= 0x0C && _hasRtc)
			{
				// handle RTC
				return OnRtcRead?.Invoke(_ramBankSel) ?? 0;
			}
			else
				return Memory[address];
		}
		else if (address >= 0xC000 && address < 0xD000)
		{
			return _sram[address & 0x0FFF];
		}
		else if (address >= 0xD000 && address < 0xE000)
		{
			return _sram[(address & 0x0FFF) + (_sramBankOffset << 12)];
		}
		else if (address >= 0x8000 && address < 0xA000)
			return _vramBankOffset == 0 ? Memory[address] : VRamUpper[address & 0x1FFF];
		else if(address >> 14 == 0b01)
			return _rom[_romBankOneOffset + address];

		return Memory[address];
	}
	// private byte ReadMem(ushort address) // serve 0x4000 .. 0x7FFF from banked ROM area
	// 	=> (address >> 14 == 0b01) ? _rom[_romBankOneOffset + address] : Memory[address];


	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private ref byte ReadMemRef(ushort address)
	{
		if(address >> 14 == 0b01)
			return ref _rom[_romBankOneOffset + address];
		else if (address >= 0xC000 && address < 0xD000)
			return ref _sram[address & 0x0FFF];
		else if (address >= 0xD000 && address < 0xE000)
			return ref _sram[(address & 0x0FFF) + (_sramBankOffset << 12)];
		else if (address >= 0x8000 && address < 0xA000)
			return ref (_vramBankOffset == 0 ? ref Memory[address] : ref VRamUpper[address & 0x1FFF]);
		else
			return ref Memory[address];
	}
	
		//=> ref (address >> 14 == 0b01) ? ref _rom[_romBankOneOffset + address] : ref Memory[address];
}
