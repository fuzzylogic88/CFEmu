/*
	NXP ColdFire CPU Emulator
	
	Daniel Green (greend5@oregonstate.edu)
	Created: 11/11/2026	
*/

using static CFEmu.CFTypes;

namespace CFEmu
{
    internal class CFMain
    {
        public class DeviceState(string progRom, string suppRom)
        {
            public readonly byte[] prog_rom = File.ReadAllBytes(progRom); // 4MB program ROM
            public readonly byte[] supp_rom = File.ReadAllBytes(suppRom); // 8Kbit / 1Kbyte external EEPROM

            public bool CPU_HALT = false;

            public int user_stack_ptr = 0;
            public int interrupt_mask = 0;
            public uint PC = 0;

            // 16bit sreg 0-7: user byte, 8-15: system byte
            public ushort sreg = 0b00000000_00000000;

            // GPR D7-D0, A7-A0 (section 1-2)
            public uint[] D = new uint[8]; // D0-D7
            public uint[] A = new uint[8]; // A0-A7, where A7 is stack pointer

            public Dictionary<ushort, uint> ControlRegisters = new()
            {
                [0x000] = 0x00000000, // SFC
                [0x001] = 0x00000000, // DFC
                [0x800] = 0x00000000, // USP
                [0x801] = 0x00000000, // VBR
                [0x002] = 0x00000000, // CACR
                [0x004] = 0x00000000, // ACR0 
                [0x005] = 0x00000000, // ACR1 
                [0xC04] = 0x00000000, // RAMBAR 
                [0x802] = 0x00000000, // CAAR
                [0x803] = 0x00000000, // MSP
                [0x804] = 0x00000000, // ISP
            };

            public static int RAM_SIZE = 0x2FFFFFF; // 2x 512K x 16Bit x 2 bank chips
            private readonly uint[] RAM = new uint[RAM_SIZE];

            public uint perihph_base = 0x01FFE000;
            public uint perihph_end = 0x01FFFFFF;


            public uint RamRead(uint addr, Size sz)
            {
                // eventually break out periph. by address space and handle differently
                if (addr >= perihph_base && addr <= perihph_end)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"MMIO R.{sz}: 0x{addr:X8} -> 0x{RAM[addr]:X8}");
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else
                {
                    Console.WriteLine($"RAM R.{sz}: 0x{addr:X8} -> 0x{RAM[addr]:X8}");
                }

                return sz switch
                {
                    Size.Byte => RAM[addr] & 0xFF,
                    Size.Word => RAM[addr] & 0xFFFF,
                    Size.Long => RAM[addr],
                    _ => throw new Exception("No size provided to RamRead()"),
                };
            }

            public void RamWrite(uint addr, uint data, Size sz)
            {
                if (addr >= perihph_base && addr <= perihph_end)
                {
                    // write to peripheral
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"MMIO W.{sz}: 0x{addr:X8} <- 0x{data:X8}");
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else
                {
                    Console.WriteLine($"RAM W.{sz}: 0x{addr:X8} <- 0x{data:X8}");
                }

                RAM[addr] = sz switch
                {
                    Size.Byte => (RAM[addr] & 0xFFFFFF00) | (data & 0xFF),
                    Size.Word => (RAM[addr] & 0xFFFF0000) | (data & 0xFFFF),
                    Size.Long => data,
                    _ => throw new Exception("No size provided to RamWrite()"),
                };
            }

            // Reset Exception : Section 8-6
            public void Reset()
            {
                sreg = 0x2000;                          // set supervisor mode (S=1, M=0)
                interrupt_mask = 0x7;                   // interrupt priority level $7 (111)
                ControlRegisters[0x804] =
                    (uint)progRom[0x0] << 24 |
                    (uint)progRom[0x1] << 16 |
                    (uint)progRom[0x2] << 8 |
                    progRom[0x3];

                PC = 0x400;

                ApplyInitialPeriphState(this);

                // prefetch three words? // 
                Console.WriteLine("CPU reset.");
            }

            private readonly int clockDelayMs = 0;
            static bool clock_enabled = false;
            public ManualResetEvent clockTick = new(false);

            /// <summary>
            /// Generates a periodic timer pulse
            /// </summary>
            private void clock_task()
            {
                while (true)
                {
                    while (clock_enabled)
                    {
                        if (STEP_INSTRUCTION)
                        {
                            Console.WriteLine("- press a key -");
                            Console.ReadKey();
                        }
                        else
                        {
                            Thread.Sleep(clockDelayMs);
                        }
                        clockTick.Set();
                    }
                }
            }
            public void EnableClock()
            {
                if (!clock_enabled)
                {
                    clock_enabled = true;
                    Task.Factory.StartNew(clock_task);
                    Console.WriteLine("Clock enabled.");
                }
                else
                {
                    Console.WriteLine("Clock already enabled.");
                }
            }
            public void DisableClock()
            {
                clock_enabled = false;
                Console.WriteLine("Clock disabled.");
            }
            public void WaitTick()
            {
                clockTick.WaitOne();
                clockTick.Reset();
            }
        }

        static void ApplyInitialPeriphState(DeviceState ds)
        {
            ds.RamWrite(0x01fffb06, (uint)(0x6 << 0x7), Size.Byte); // PC 0x0A84
        }

        static void Main(string[] args)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Title = "CFEmu";

            DeviceState ds = new("prog_rom.bin", "supp_rom.BIN");

            ds.Reset();
            ds.EnableClock();
            ExecuteProgram(ds);
        }

        public static bool STEP_INSTRUCTION = false;    // TRUE to single-step through instructions

        /// <summary>
        /// Executes instructions contained by prog_rom[] of the DeviceState object
        /// Assumes initial device state was set using Reset() to apply PC and register states
        /// </summary>
        /// <param name="ds">DeviceState object containing prog/external memory and registers</param>
        static void ExecuteProgram(DeviceState ds)
        {
            while (!ds.CPU_HALT)
            {
                ds.WaitTick();
                try
                {
                    ushort inst = 0;

                    // combine the two bytes into a single 16bit opcode & operand
                    ushort opc = (ushort)((ds.prog_rom[ds.PC] << 8) | ds.prog_rom[ds.PC + 1]);
                    ushort extA = (ushort)((ds.prog_rom[ds.PC + 2] << 8) | ds.prog_rom[ds.PC + 3]);
                    ushort extB = (ushort)((ds.prog_rom[ds.PC + 4] << 8) | ds.prog_rom[ds.PC + 5]);

                    if (ds.PC == 0x1204)
                    {
                        //ExportRAMContents(ds);
                        Console.WriteLine("!!! BREAK !!!");
                    }

                    // determine instruction family by top nibble value 
                    ushort instr_family = (ushort)((opc & 0xF000) >> 12);

                    switch (instr_family)
                    {
                        // bit manip
                        case 0:
                            ushort _static = (ushort)((opc & 0x0800) >> 11);
                            ushort _test = (ushort)((opc & 0x00C0) >> 6);
                            switch (_test)
                            {
                                case 0x0: // BTST
                                    switch (_static) 
                                    {
                                        case 1:
                                            BTST(ds, opc, extA, true);
                                            continue;
                                        case 0:
                                            BTST(ds, opc, extA, false);
                                            continue;
                                    }
                                    break;
                            }
                            break;

                        case 1: // move byte       
                        case 2: // movea.l (long/32-bit/4-byte) 
                        case 3: // move.w (word)
                            MOVE_Decode(ds, opc, extA, extB);
                            continue;

                        // misc.
                        case 4:

                            inst = (ushort)((opc & 0xFF80) >> 7);
                            ushort instb = (ushort)(opc & 0xFFC0);

                            switch (inst)
                            {
                                case 0x0084:
                                case 0x0085: // clr_L
                                    CLR(ds, opc, extA, extB);
                                    continue;
                                case 0x008D:
                                    if (instb == 0x4680)
                                    {
                                        NOT(ds, opc);
                                    }
                                    else if (instb == 0x46C0)
                                    {
                                        Move_To_SR(ds, extA);
                                    }
                                    continue;
                                case 0x009C:
                                    // Look back to opcode to discern between similar-looking instructions
                                    instb = (ushort)(opc & 0xFFF8);
                                    switch (instb)
                                    {
                                        case 0x4E50:
                                            LINKW(ds, opc, extA);
                                            continue;
                                        case 0x4E7A:
                                        case 0x4E78:
                                            MOVEC(ds, opc, extA);
                                            continue;
                                        case 0x4E58:
                                            UNLK(ds, opc);
                                            continue;
                                        case 0x4E70:
                                            RTS(ds);
                                            continue;
                                    }
                                    continue;
                                case 0x0090:
                                    PEA(ds, opc, extA, extB);
                                    continue;
                                case 0x0091: // reg->mem movem_l
                                case 0x0099: // mem->reg movem_l
                                    MOVEM_L(ds, opc, extA, extB);
                                    continue;
                                case 0x0094:
                                case 0x0095:

                                    // check for halt outright
                                    if (opc == 0x4AC8)
                                    {
                                        HALT(ds);
                                        continue;
                                    }

                                    uint instc = (ushort)((opc & 0xFFC0) >> 6);
                                    switch (instc)
                                    {
                                        case 0x128: // tst.b
                                        case 0x129: // tst.w
                                        case 0x12B: // tst.l
                                            TST(ds, opc, extA, extB);
                                            break;
                                        default:
                                            throw new Exception("Unimplemented instruction");
                                    }
                                    continue;
                                case 0x009D:
                                    JSR(ds, opc, extA, extB);
                                    continue;
                                case 0x0083:
                                case 0x009F:
                                case 0x0087:
                                    LEA(ds, opc, extA, extB);
                                    continue;
                                default:
                                    throw new Exception("Unimplemented instruction");
                            }

                        // ADDQ/SUBQ/Scc/DBcc/TRAPc c
                        case 5:
                            inst = (ushort)((opc & 0x0100) >> 8); // bit 8 determines SUBQ/ADDQ
                            switch (inst)
                            {
                                case 1:
                                    // SUBQ
                                    QUICK_MATH(ds, opc, QMathOp.SUBRACT);
                                    continue;
                                case 0:
                                    // ADDQ
                                    QUICK_MATH(ds, opc, QMathOp.ADD);
                                    continue;
                            }
                            break;

                        // Bcc / BSR / BRA
                        case 6:
                            inst = (ushort)((opc & 0x0F00) >> 8);

                            if (inst == 0x0) // BRA
                            {
                                BRA(ds, opc, extA, extB);
                                continue;
                            }
                            else if (inst == 0x1) // BSR
                            {
                                BSR(ds, opc, extA, extB);
                                continue;
                            }
                            else // bits 8-11 are the 'condition code' for BCC
                            {
                                BranchConditions bc = BranchConditions.Unknown;
                                bc = inst switch
                                {
                                    0x2 => BranchConditions.High,
                                    0x3 => BranchConditions.LowerOrSame,
                                    0x4 => BranchConditions.CarryClear,
                                    0x5 => BranchConditions.CarrySet,
                                    0x6 => BranchConditions.NotEqual,
                                    0x7 => BranchConditions.Equal,
                                    0x8 => BranchConditions.OverflowClear,
                                    0x9 => BranchConditions.OverflowSet,
                                    0xD => BranchConditions.LessThan,
                                    0xA => BranchConditions.Plus,
                                    0xB => BranchConditions.Minus,
                                    0xC => BranchConditions.GreaterOrEqual,
                                    0xE => BranchConditions.GreaterThan,
                                    0xF => BranchConditions.LessOrEqual,
                                    _ => throw new Exception("Unexpected branch condition case encountered for BCC")
                                };
                                BCC(ds, bc, opc, extA);
                            }
                            continue;

                        // moveq (quick, data register)
                        case 7:
                            QUICK_MOVE(ds, opc, QMoveOp.MOVEQ);
                            continue;

                        // OR / DIV / SBCD
                        case 8:
                            OR_L(ds, opc, (uint)(extA << 16 | extB));
                            continue;

                        // SUB / SUBA / SUBX
                        case 9:
                            inst = (ushort)((opc & 0x1C0) >> 6);
                            switch (inst)
                            {
                                case 0b011:
                                    // suba_w
                                    break;
                                case 0b111:
                                    SUBA_L(ds, opc, (uint)(extA << 16 | extB));
                                    break;
                                default: // 0-010
                                    SUB(ds, opc, extA, extB);
                                    break;
                            }

                            continue;

                        // Unassigned / reserved
                        case 0xA:
                            break;

                        // CMP / EOR
                        case 0xB:
                            CMPA_L(ds, opc, (uint)(extA << 16 | extB));
                            continue;

                        // AND / MUL / ABCD / EXG
                        case 0xC:
                            AND_L(ds, opc, (uint)(extA << 16 | extB));
                            continue;

                        // ADD / ADDX
                        case 0xD:
                            ADD_L(ds, opc, (uint)(extA << 16 | extB));
                            continue;

                        // Shift / Rotate / Bit field
                        case 0xE:
                            LSHIFT(ds, opc, extA, extB);
                            break;

                        // Coprocessor interface / MC68040 and CPU32 extensions
                        case 0xF:
                            break;
                        default:
                            Console.WriteLine($"Illegal instruction: 0x{opc:X4}");
                            ds.CPU_HALT = true;
                            continue;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception: {ex.Message} (PC: 0x{ds.PC:X8})");
                    Console.WriteLine("Press a key to exit.");
                    Console.ReadKey();
                    return;
                }
            }
            Console.WriteLine("CPU Halted. Press a key to exit.");
            Console.ReadKey();
        }

        /// <summary>
        /// Decodes MOVE instructions and chooses correct move type
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="opc"></param>
        /// <param name="extA"></param>
        /// <param name="extB"></param>
        /// <exception cref="Exception"></exception>
        static void MOVE_Decode(DeviceState ds, ushort opc, ushort extA, ushort extB)
        {
            /*
             *  Decode Move
             */
            uint size = (uint)((opc & 0x3000) >> 12);
            uint dst_reg = (uint)((opc & 0xE00) >> 9);       // bits 9-11
            uint dst_addr_mode = (uint)((opc & 0x01C0) >> 6); // bits 6-8
            uint src_addr_mode = (uint)((opc & 0x38) >> 3);  // bits 3-5
            uint src_reg = (uint)(opc & 0x7);                // bits 0-2
            uint dst_addr = 0, data = 0;

            /*
             *  Decode Addressing Mode & Extension Words
             */
            (AddressingMode src_am, uint extWords, int extCnt) = GetAddrModeAndExtWordCount(src_addr_mode, src_reg, size, extA, extB);

            int mv_pc_offset_bytes = extCnt * 2; // 2 bytes/word

            if (extCnt == 0)
            {
                // data encoded in instruction or retrieved from reg/mem
                if (src_am == AddressingMode.DataRegisterDirect)
                {
                    data = ds.D[src_reg];
                }
                else if (src_am == AddressingMode.AddressRegisterDirect)
                {
                    data = ds.A[src_reg];
                }
                else if (src_am == AddressingMode.AddressRegisterIndirectWithPostIncrement)
                {
                    data = ds.A[src_reg];
                    switch (size)
                    {
                        case 0:
                            if (src_reg == 7) //SP case, 2 decremented
                            {
                                ds.A[src_reg] += 2;
                            }
                            else
                            {
                                ds.A[src_reg] += 1;
                            }
                            break;
                        case 1:
                            ds.A[src_reg] += 2;
                            break;
                        case 2:
                            ds.A[src_reg] += 4;
                            break;
                    }
                }
            }
            else
            {
                data = extWords;
            }

            (AddressingMode dst_am, extWords, extCnt) = GetAddrModeAndExtWordCount(dst_addr_mode, dst_reg, size, extA, extB);
            mv_pc_offset_bytes += extCnt * 2;

            if (dst_am == AddressingMode.AbsoluteLongData)
            {
                dst_addr = (uint)(extA << 16 | extB);
            }

            else if (dst_am == AddressingMode.AddressRegisterIndirectWithPreDecrement)
            {
                // Predecrement the address value of register
                switch (size)
                {
                    case 0:
                        if (dst_reg == 7) //SP case, 2 decremented
                        {
                            ds.A[dst_reg] -= 2;
                        }
                        else
                        {
                            ds.A[dst_reg] -= 1;
                        }
                        break;
                    case 1:
                        ds.A[dst_reg] -= 2;
                        break;
                    case 2:
                        ds.A[dst_reg] -= 4;
                        break;

                }
            }

            /*
             *  Execute Move
             */

            // determine if the move is a byte, long, or word
            switch (size)
            {
                case 1:
                    MOVEB(ds, data, dst_reg, dst_am, dst_addr, size);
                    break;
                case 2:
                    if (dst_am == AddressingMode.AddressRegisterDirect)
                    {
                        MOVEA_L(ds, data, dst_reg);
                    }
                    else
                    {
                        MOVEL(ds, data, dst_reg, dst_am, size);
                    }
                    break;
                case 3:
                    if (dst_am == AddressingMode.AddressRegisterDirect)
                    {
                        throw new Exception("MOVEA_W not implemented");
                        //MOVEA_W(ds, data, dst_reg);
                    }
                    else
                    {
                        MOVEW(ds, data, dst_reg, dst_am, dst_addr);
                    }
                    break;
            }
            ds.PC += (uint)mv_pc_offset_bytes + 2;
        }
        
        static void ASHIFT()
        {

        }

        /// <summary>
        /// Logical Shift (left/right)
        /// </summary>
        static void LSHIFT(DeviceState ds, ushort opc, uint extA, uint extB)
        {
            ushort cnt_or_reg_dy = (ushort)((opc & 0xE00) >> 9);
            ushort direction = (ushort)((opc & 0x0100) >> 8);
            ushort imm_or_reg_shift_cnt = (ushort)((opc & 0x20) >> 5);
            ushort reg_dx = (ushort)(opc & 0x7);

            ushort shift_cnt = 0;
            string shift_dir = string.Empty;

            bool N = false, Z = false, V = false, X = false, C = false;

            // i/r field
            switch (imm_or_reg_shift_cnt)
            {
                case 0: // immediate shift cnt (range 1-8)
                    shift_cnt = cnt_or_reg_dy;
                    break;
                case 1: // register shift cnt (Dx)
                    shift_cnt = (ushort)ds.D[reg_dx]; // contains shift cnt % 64
                    break;
            }

            // direction 
            switch (direction)
            { 
                case 0: // right shift
                    shift_dir = "R";
                    C = X = (ds.D[reg_dx] & shift_cnt) == 1;                 // set carry/extend if leftmost, right-traveling bit is 1
                    ds.D[reg_dx] = ds.D[reg_dx] >> shift_cnt;
                    break;
                case 1: // left shift
                    shift_dir = "L";
                    C = X = ((ds.D[reg_dx] >> (31 - shift_cnt)) & 1) == 1;  // set carry/extend if rightmost, left-traveling bit is 1
                    ds.D[reg_dx] = ds.D[reg_dx] << shift_cnt;
                    break;
            }
            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tLS{shift_dir} D[{reg_dx}] {shift_cnt} steps -> 0x{ds.D[reg_dx]:X8}");
            UpdateSREG(ds, negative: N, zero: Z, overflow: V, carry: C, extend: X);
            ds.PC += 2;
        }

        static void SUB(DeviceState ds, ushort opc, uint extA, uint extB)
        {
            ushort reg = (ushort)((opc & 0xE00) >> 9);
            ushort opmode = (ushort)((opc & 0x01C0) >> 6);
            ushort ea_mode = (ushort)((opc & 0x38)>>3);
            ushort ea_reg = (ushort)(opc & 0x7);
            bool N = false, Z = false, V = false, X = false, C = false;

            (AddressingMode am, uint extWords, int extCnt) = GetAddrModeAndExtWordCount(ea_mode, ea_reg, 0, extA, extB);

            switch (am)
            {
                case AddressingMode.ImmediateData:

                    C = ds.D[reg] < extWords;

                    // signed subtraction overflow test - checks dest, source, and result sign bits
                    uint result = ds.D[reg] -= extWords;
                    uint dest_sign_bit = ds.D[reg] & 0xf;
                    V = ((ds.D[reg] ^ extWords) & (ds.D[reg] ^ result) & dest_sign_bit) != 0;
                    N = (ds.D[reg] & 0x8000) == 1;
                    Z = ds.D[reg] == 0;

                    ds.D[reg] = result;
                    Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tSUB 0x{extWords:X8} from D[{reg}] -> 0x{result:X8}");
                    break;
            }
            UpdateSREG(ds, negative: N, zero: Z, overflow: V, carry: C, extend: X);
            ds.PC += (uint)((2 * extCnt) + 4);
        }

        /// <summary>
        /// Return from subroutine
        /// (SP) -> PC; SP + 4 -> SP
        /// </summary>
        /// <param name="ds"></param>
        static void RTS(DeviceState ds)
        {
            uint oldPc = ds.PC;
            ds.PC = ds.RamRead(ds.A[7], Size.Long);    // (SP) -> PC (4 bytes, so we read a longword)

            Console.WriteLine($"└─────RTS: 0x{oldPc:X8} -> 0x{ds.PC:X8}");
        }

        /// <summary>
        /// SUB to Address Register (Subtract Address)
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="opc"></param>
        /// <param name="extA"></param>
        static void SUBA_L(DeviceState ds, ushort opc, uint extA)
        {
            ushort addr_reg = (ushort)((opc & 0xE00) >> 9);
            ushort ea_mode = (ushort)((opc & 0x0038) >> 3);
            ushort ea_reg = (ushort)(opc & 0x7);

            (AddressingMode am, uint extWords, int extCnt) = GetAddrModeAndExtWordCount(ea_mode, ea_reg, 0, extA, 0);

            ds.A[addr_reg] -= am switch
            {
                AddressingMode.ImmediateData => extWords,
                _ => throw new Exception("Unimplemented am in SUBA_L"),
            };

            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tSUBA_L\tA[{addr_reg}] - 0x{extWords:X8} -> 0x{ds.A[addr_reg]:X8}");
            ds.PC += (uint)(extCnt * 2 + 4);
        }

        /// <summary>
        /// Perform comparison and update condition codes (does not write back to mem/reg)
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="opc"></param>
        /// <param name="extA"></param>
        /// <exception cref="Exception"></exception>
        static void CMPA_L(DeviceState ds, ushort opc, uint extA)
        {
            ushort ea_mode = (ushort)((opc & 0x0038) >> 3);
            ushort ea_reg = (ushort)(opc & 0x7);
            ushort opmode = (ushort)((opc & 0x1C0) >> 6);
            ushort addr_reg = (ushort)((opc & 0xE00) >> 9);

            uint lw_pc_offset = 0;

            (AddressingMode am, uint extWords, int extCnt) = GetAddrModeAndExtWordCount(ea_mode, ea_reg, 0, extA, 0);

            bool N = false, Z = false, V = false, C = false;
            uint res = 0;

            switch (am)
            {
                case AddressingMode.DataRegisterDirect:
                    switch (opmode)
                    {
                        case 7: // <ea>y CMP Dx -> Dx, Data Register used as dest
                            res = ds.A[addr_reg] - ds.D[ea_reg];
                            uint dest_sign_bit = ds.A[addr_reg] & 0x8000_0000;

                            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tCMPA_L A[{addr_reg}] - D[{ea_reg}]");

                            N = (int)(res & 0xFFFFFFFF) < 0;
                            Z = (int)(res & 0xFFFFFFFF) == 0;
                            V = ((ds.A[addr_reg] ^ ds.D[ea_reg]) & (ds.A[addr_reg] ^ res) & dest_sign_bit) != 0;
                            C = ds.A[addr_reg] < ds.D[ea_reg];
                            break;
                        default:
                            throw new Exception("Unimplemented opmode encountered in CMPA_L");
                    }
                    break;
                case AddressingMode.AddressRegisterDirect:
                    switch (opmode)
                    {
                        case 7:
                            res = ds.A[addr_reg] - ds.A[ea_reg];
                            uint dest_sign_bit = ds.A[addr_reg] & 0x8000_0000;

                            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tCMPA_L A[{addr_reg}] - A[{ea_reg}]");

                            N = (int)(res & 0xFFFFFFFF) < 0;
                            Z = (int)(res & 0xFFFFFFFF) == 0;
                            V = ((ds.A[addr_reg] ^ ds.A[ea_reg]) & (ds.A[addr_reg] ^ res) & dest_sign_bit) != 0;
                            C = ds.A[addr_reg] < ds.A[ea_reg];

                            break;
                    }
                    break;
                case AddressingMode.ImmediateData:
                    switch (opmode)
                    {
                        case 0x2:
                            res = ds.D[addr_reg] - extWords;

                            uint dest_sign_bit = ds.A[addr_reg] & 0x8000_0000;

                            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tCMPA_L A[{addr_reg}] - A[{ea_reg}]");

                            N = (int)(res & 0xFFFFFFFF) < 0;
                            Z = (int)(res & 0xFFFFFFFF) == 0;
                            V = ((ds.D[addr_reg] ^ extWords) & (ds.D[addr_reg] ^ res) & dest_sign_bit) != 0;
                            C = ds.D[addr_reg] < extWords;
                            lw_pc_offset = 2;
                            break;
                    }
                    break;
                default:
                    throw new Exception("Unimplemented am CMPA_L");
            }
            UpdateSREG(ds, negative: N, zero: Z, overflow: V, carry: C);
            ds.PC += (uint)((extCnt * 2) + 2 + lw_pc_offset);
        }

        /// <summary>
        /// Logical NOT - Operates on data register D[n]
        /// </summary>
        static void NOT(DeviceState ds, ushort opc)
        {       
            ushort reg = (ushort)(opc & 0x7);
            ds.D[reg] = ~ds.D[reg];

            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tNOT\tD[{reg}] -> 0x{ds.D[reg]:X8}");

            ds.PC += 2;
        }

        /// <summary>
        /// Add (size = longword)
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="opc"></param>
        /// <param name="extA"></param>
        /// <param name="extB"></param>
        static void ADD_L(DeviceState ds, ushort opc, uint extA)
        {
            ushort ea_mode = (ushort)((opc & 0x0038) >> 3);
            ushort ea_reg = (ushort)(opc & 0x7);
            ushort opmode = (ushort)((opc & 0x1C0) >> 6);
            ushort data_reg = (ushort)((opc & 0xE00) >> 9);

            (AddressingMode am, uint extWords, int extCnt) = GetAddrModeAndExtWordCount(ea_mode, ea_reg, 0, extA, 0);

            switch (am)
            {
                case AddressingMode.DataRegisterDirect:
                    switch (opmode)
                    {
                        case 2: // <ea>y + Dx -> Dx, Data Register used as dest
                            ds.D[data_reg] = ds.D[ea_reg] + ds.D[data_reg];
                            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tADD_L D[{ea_reg}] + D[{data_reg}] -> 0x{ds.D[data_reg]:X8}");
                            break;
                        case 6: // Dy + <ea>x -> <ea>x, ea used as dest
                            ds.D[ea_reg] = ds.D[ea_reg] + ds.D[data_reg];
                            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tADD_L D[{ea_reg}] + D[{data_reg}] -> 0x{ds.D[ea_reg]:X8}");
                            break;
                    }
                    break;
                case AddressingMode.ImmediateData:
                    switch (opmode)
                    {
                        // ADDA_L, dest is address register
                        case 7:
                            ds.A[data_reg] += extWords;
                            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tADD_L A[{data_reg}] + 0x{extWords:X8} -> 0x{ds.A[data_reg]:X8}");
                            break;
                        case 2:
                            ds.D[data_reg] += extWords;
                            break;
                        default:
                            throw new Exception("Unimplemented OPMODE in ADD_L");
                    }
                    break;
                default:
                    throw new Exception("Unimplemented am ADD_L");
            }
            ds.PC += (uint)(extCnt * 2 + 4);
        }

        static void OR_L(DeviceState ds, ushort opc, uint extA)
        {
            ushort ea_mode = (ushort)((opc & 0x0038) >> 3);
            ushort ea_reg = (ushort)(opc & 0x7);
            ushort opmode = (ushort)((opc & 0x1C0) >> 6);
            ushort data_reg = (ushort)((opc & 0xE00) >> 9);

            (AddressingMode am, uint extWords, int extCnt) = GetAddrModeAndExtWordCount(ea_mode, ea_reg, 0, extA, 0);

            switch (am)
            {
                case AddressingMode.ImmediateData:
                    switch (opmode)
                    {
                        case 2:
                            ds.D[data_reg] |= extWords;
                            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tOR_L D[{ea_reg}] | D[{data_reg}] -> 0x{ds.D[data_reg]:X8}");
                            break;
                        case 4:
                            throw new Exception("???");
                    }
                    break;
                default:
                    throw new Exception("Unimplemented am OR_L");
            }
            ds.PC += (uint)(extCnt * 2 + 4);
        }

        /// <summary>
        /// Logical AND
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="opc"></param>
        static void AND_L(DeviceState ds, ushort opc, uint extA)
        {
            ushort ea_mode = (ushort)((opc & 0x0038) >> 3);
            ushort ea_reg = (ushort)(opc & 0x7);
            ushort opmode = (ushort)((opc & 0x1C0) >> 6);
            ushort data_reg = (ushort)((opc & 0xE00) >> 9);

            (AddressingMode am, _, int extCnt) = GetAddrModeAndExtWordCount(ea_mode, ea_reg, 0, extA, 0);

            switch (am)
            {
                case AddressingMode.DataRegisterDirect:
                    switch (opmode)
                    {
                        case 2: // <ea>y & Dx -> Dx, Data Register used as dest
                            ds.D[data_reg] = ds.D[ea_reg] & ds.D[data_reg];
                            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tAND_L D[{ea_reg}] & D[{data_reg}] -> 0x{ds.D[data_reg]:X8}");
                            break;
                        case 6: // Dy & <ea>x -> <ea>x, ea used as dest
                            ds.D[ea_reg] = ds.D[ea_reg] & ds.D[data_reg];
                            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tAND_L D[{ea_reg}] & D[{data_reg}] -> 0x{ds.D[ea_reg]:X8}");
                            break;
                    }

                    break;
                default:
                    throw new Exception("Unimplemented am AND_L");
            }
            ds.PC += (uint)(extCnt * 2 + 2);
        }

        static void BTST(DeviceState ds, ushort opc, ushort extA, bool is_static)
        {
            ushort bit_num = extA;
            ushort ea_mode = (ushort)((opc & 0x0038) >> 3);
            ushort ea_reg = (ushort)(opc & 0x7);
            (AddressingMode am, _, _) = GetAddrModeAndExtWordCount(ea_mode, ea_reg, 0, extA, 0);

            if (is_static)
            {
                var data = am switch
                {
                    AddressingMode.DataRegisterDirect => ds.D[ea_reg],
                    _ => throw new Exception("Unimplemented am BTST"),
                };

                bool bit_set = ((data >> bit_num) & 1) == 1;
                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tBTST\tbit: 0x{bit_num:X8}, data: 0x{data:X8} -> {(bit_set ? "set" : "clear")}");
                UpdateSREG(ds, zero: !bit_set);
                ds.PC += 4;
            }
            else
            {
                throw new Exception("!");
            }

        }

        /// <summary>
        /// Clear (word/long/byte)
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="opc"></param>
        /// <param name="extA"></param>
        /// <param name="extB"></param>
        static void CLR(DeviceState ds, ushort opc, ushort extA, ushort extB)
        {
            ushort size = (ushort)((opc & 0xC0) >> 6);
            ushort mode = (ushort)((opc & 0x38) >> 3);
            ushort reg = (ushort)(opc & 0x7);
            uint dataPost, clrMsk;

            (AddressingMode am, uint extWords, int extCnt) = GetAddrModeAndExtWordCount(mode, reg, size, extA, extB);

            string sizeType;

            ushort decBytes;
            Size opSize;

            switch (size)
            {
                // byte
                case 0:
                    opSize = Size.Byte;
                    decBytes = 1;
                    clrMsk = 0xFF;
                    sizeType = "B";
                    break;
                // word
                case 1:
                    opSize = Size.Word;
                    decBytes = 2;
                    clrMsk = 0xFFFF;
                    sizeType = "W";
                    break;
                // long
                case 2:
                    opSize = Size.Long;
                    decBytes = 4;
                    clrMsk = 0xFFFFFFFF;
                    sizeType = "L";
                    break;
                default:
                    throw new Exception("Unknown/Invalid size in CLR");
            }

            // clear 
            switch (am)
            {
                case AddressingMode.AddressRegisterIndirectWithPreDecrement:
                    ds.A[reg] -= decBytes;
                    dataPost = ds.RamRead(ds.A[reg], opSize) & ~clrMsk;
                    ds.RamWrite(ds.A[reg], dataPost, opSize);

                    Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tCLR_{sizeType} RAM[0x{ds.A[reg]:X8}] -> 0x{dataPost:X8}");
                    break;

                case AddressingMode.AbsoluteLongData:
                    dataPost = ds.RamRead(extWords, Size.Long) & ~clrMsk;
                    ds.RamWrite(extWords, dataPost, Size.Long);

                    Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tCLR_{sizeType} RAM[0x{extWords:X8}] -> 0x{dataPost:X8}");
                    break;
                default:
                    throw new Exception("unimplemented am for CLR_W");
            }
            UpdateSREG(ds, negative: (int)dataPost < 0, zero: dataPost == 0, overflow: false, carry: false);
            ds.PC += (uint)(2 * extCnt + 2);
        }

        /// <summary>
        /// Load Effective Address - Basically ADD without modifying condition flags.
        /// </summary>
        static void LEA(DeviceState ds, ushort opc, ushort extA, ushort extB)
        {
            uint dst_reg = (uint)((opc & 0xE00) >> 9);      // bits 9-11
            uint src_addr_mode = (uint)((opc & 0x38) >> 3); // bits 3-5
            uint src_reg = (uint)(opc & 0x7);               // bits 0-2
            uint ea = 0, disp = 0;

            (AddressingMode am, uint extWords, int extCnt) = GetAddrModeAndExtWordCount(src_addr_mode, src_reg, 0, extA, extB);

            if (extCnt == 0)
            {
                throw new Exception("no no-ext-word case implemented, LEA");
            }

            switch (am)
            {
                case AddressingMode.AddressRegisterIndirectWithDisplacement:
                    disp = extWords;
                    ea = ds.A[src_reg] + disp;
                    ds.A[dst_reg] = ea;
                    break;
                case AddressingMode.AddressRegisterIndirectWithScaledIndexAnd8bitDisplacement:

                    /* 
                     *  from extension word, derive:
                     *  - index register (D0/ A0->A7)
                     *  - index reg size (word/long)
                     *  - scale factor
                     *  - 8b displacement
                     */

                    uint reg_type = (ushort)((extWords & 0x8000) >> 15);
                    uint idx_reg = (ushort)((extWords & 0x7000) >> 12);
                    uint size = (ushort)((extWords & 0x800) >> 11);

                    if (size == 0x0)
                    {
                        // anticipating a longword (section 2-2)
                        throw new Exception("Address Error Exception");
                    }

                    uint scale = (ushort)((extWords & 0x600) >> 9);

                    ushort calc_scale;
                    calc_scale = scale switch
                    {
                        0b00 => 1,
                        0b01 => 2,
                        0b10 => 4,
                        0b11 => 8,
                        _ => throw new Exception("Unknown scale value encountered in LEA"),
                    };

                    disp = (ushort)(extWords & 0xFF);

                    ushort scaled_val = 0;
                    scaled_val = reg_type switch
                    {
                        0 => (ushort)(ds.D[idx_reg] * calc_scale),
                        1 => (ushort)(ds.A[idx_reg] * calc_scale),
                        _ => throw new Exception("Unknown reg_type encountered in LEA"),
                    };

                    ea = ds.A[src_reg] + scaled_val + disp;
                    ds.A[dst_reg] = ea;

                    break;
                default:
                    throw new Exception("Unimplemented am in LEA");
            }
            
            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tLEA\treg:A[{dst_reg}], disp:{disp} -> 0x{ds.A[dst_reg]:X8}");

            ds.PC += (uint)(2 * extCnt + 2);
        }

        /// <summary>
        /// Decodes addressing mode, and returns the extension words as an array
        /// </summary>
        /// <param name="addr_mode">Addressing Mode</param>
        /// <param name="reg_number">Register number</param>
        /// <param name="size">Size (01->byte, 10->long, 11->word)</param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private static (AddressingMode, uint, int) GetAddrModeAndExtWordCount(uint addr_mode, uint reg_number, uint size, uint ext_wordA, uint ext_wordB)
        {
            switch (addr_mode)
            {
                case 0:
                    return (AddressingMode.DataRegisterDirect, 0, 0);
                case 1:
                    return (AddressingMode.AddressRegisterDirect, 0, 0);
                case 2:
                    return (AddressingMode.AddressRegisterIndirect, 0, 0);
                case 3:
                    return (AddressingMode.AddressRegisterIndirectWithPostIncrement, 0, 0);
                case 4:
                    return (AddressingMode.AddressRegisterIndirectWithPreDecrement, 0, 0);
                case 5:
                    return (AddressingMode.AddressRegisterIndirectWithDisplacement, ext_wordA, 1);
                case 6:
                    return (AddressingMode.AddressRegisterIndirectWithScaledIndexAnd8bitDisplacement,ext_wordA,1);
                case 7:
                    switch (reg_number)
                    {
                        case 0:
                            return (AddressingMode.AbsoluteShortData, ext_wordA, 1);
                        case 1:
                            return (AddressingMode.AbsoluteLongData, (uint)(ext_wordA << 16 | ext_wordB), 2);
                        case 2:
                            return (AddressingMode.ProgramCounterIndirectWithDisplacement, ext_wordA, 1);
                        case 3:
                            return (AddressingMode.ProgramCounterIndirectWithScaledIndexAnd8bitDisplacement, ext_wordA, 1);
                        case 4:
                            if (size == 2)
                            {
                                return (AddressingMode.ImmediateData, (uint)(ext_wordA << 16 | ext_wordB), 2);
                            }
                            else
                            {
                                return (AddressingMode.ImmediateData, ext_wordA, 1);
                            }
                        case 5:
                        case 6:
                        case 7:
                        default:
                            throw new Exception($"Invalid addressing mode: {addr_mode}, reg_number: {reg_number}, size: {size}");
                    }
                default:
                    throw new Exception($"Invalid addressing mode: {addr_mode}");
            }
        }

        /// <summary>
        /// Push Effective Address
        /// </summary>
        static void PEA(DeviceState ds, ushort opc, ushort extA, ushort extB)
        {
            uint mode = (uint)((opc & 0x38) >> 3);
            uint reg = (uint)(opc & 0x7);
            (AddressingMode am, uint extWords, int extCnt) = GetAddrModeAndExtWordCount(mode, reg, 0, extA,extB);

            ds.A[7] -= 4;
            switch (am)
            {
                case AddressingMode.AbsoluteShortData:
                    ds.RamWrite(ds.A[7], SignExtend32(extWords),Size.Long);
                    break;
                default:
                    throw new Exception("unimplemented am in PEA");
            }
            ds.PC += (uint)(2 * extCnt + 2);
            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tPEA\treg:SP, ea:{SignExtend32(extWords)}");
        }

        static uint SignExtend32(uint data)
        {
            // bit 15 = 1, fill upper 16 with ones
            if ((data & 8000) == 0x1)
            {
                data &= 0xFFFF0000;
            }
            // bit 15 = 0, fill upper 16 with zeros
            if ((data & 8000) == 0x0)
            {
                data = (uint)data & ~0xFFFF0000;
            }
            return data;
        }

        /// <summary>
        /// Link and Allocate (word)
        /// SP – 4 -> SP; Ay -> (SP); SP -> Ay; SP + dn -> SP
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="opc"></param>
        /// <param name="extA"></param>
        static void LINKW(DeviceState ds, ushort opc, uint extA)
        {
            uint reg = (uint)(opc & 0x7);
            int displacement = (short)extA;

            ds.A[7] -= 4;                                   // SP – 4 -> SP
            ds.RamWrite(ds.A[7], ds.A[reg], Size.Long);     // Ay -> (SP) (addr as longword gets written)
            ds.A[reg] = ds.A[7];                            // SP -> Ay
            ds.A[7] = (uint)((int)ds.A[7] + displacement);  // SP + dn -> SP

            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tLINKW\tA[{reg}], disp:{displacement}");

            ds.PC += 4;
        }

        /// <summary>
        /// Unlink (Ax -> SP; (SP) -> Ax; SP + 4 -> SP)
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="opc"></param>
        static void UNLK(DeviceState ds, ushort opc)
        {
            uint reg = (uint)(opc & 0x7);

            uint SP = ds.A[reg];                    // Ax -> SP
            ds.A[reg] = ds.RamRead(SP, Size.Long);  // (SP) -> Ax
            ds.A[7] = SP + 4;                       // SP + 4 -> SP

            ds.PC += 2;

            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tUNLK\tA[{reg}]");
        }
        
        /// <summary>
        /// Branch to Subroutine
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="opc"></param>
        /// <param name="extA"></param>
        /// <param name="extB"></param>
        static void BSR(DeviceState ds, ushort opc, ushort extA, ushort extB)
        {
            // displacement is signed
            int disp = (sbyte)(opc & 0xFF);
            uint retAddrOffset = 2; 

            if (disp == 0x00)
            {
                retAddrOffset = 4;
                // get 16bit displacement extension word
                disp = (short)extA;
            }
            else if (disp == 0xFF)
            {
                retAddrOffset = 6;
                // get 32-bit displacement
                disp = (int)((uint)extA << 16 | extB);
            }

            uint oldPc = ds.PC;
            ds.A[7] -= 4;                                               // SP <- SP - 4
            ds.RamWrite(ds.A[7], ds.PC + retAddrOffset, Size.Long);     // PC <- (SP) (push RA to stack)
            ds.PC = (uint)(oldPc + disp + 2);                           // PC <- PC + dn

            Console.WriteLine($"└─────BSR: 0x{oldPc:X8} -> 0x{ds.PC:X8}");
        }

        /// <summary>
        /// Jump to Subroutine
        /// </summary>
        static void JSR(DeviceState ds, ushort opc, ushort extA, ushort extB)
        {
            uint mode = (uint)((opc & 0x38) >> 3);
            uint reg = (uint)(opc & 0x7);
            (AddressingMode am, uint extWords, int extCnt) = GetAddrModeAndExtWordCount(mode, reg, 0, extA, extB);

            uint jmpAddr = 0, retAddr = 0;

            switch (am)
            {
                case AddressingMode.AddressRegisterIndirect:
                    jmpAddr = ds.A[reg];
                    retAddr = ds.PC + 2;
                    break;
                case AddressingMode.AddressRegisterIndirectWithDisplacement: //(d16,Ay)
                    jmpAddr = ds.A[reg] + SignExtend32(extWords);
                    retAddr = ds.PC + 4;
                    break;
                case AddressingMode.AddressRegisterIndirectWithScaledIndexAnd8bitDisplacement: //(d8,Ay,Xi)
                    throw new Exception("Unimplemented am in JSR");
                    retAddr = ds.PC + 4;
                    break;
                case AddressingMode.AbsoluteShortData:
                    jmpAddr = extWords;
                    retAddr = ds.PC + 4;
                    break;
                case AddressingMode.AbsoluteLongData:
                    jmpAddr = extWords; // should be exta | extb, full 32bit val
                    retAddr = ds.PC + 6;
                    break;
                case AddressingMode.ProgramCounterIndirectWithDisplacement:
                    throw new Exception("Unimplemented am in JSR");
                    retAddr = ds.PC + 4;
                    break;
                case AddressingMode.ProgramCounterIndirectWithScaledIndexAnd8bitDisplacement:
                    throw new Exception("Unimplemented am in JSR");
                    retAddr = ds.PC + 4;
                    break;
                default:
                    throw new Exception("Unexpected am encountered in JSR");

            }



            uint oldPc = ds.PC;
            ds.A[7] -= 4;                  // SP <- SP - 4
            ds.RamWrite(ds.A[7], retAddr, Size.Long);
            ds.PC = jmpAddr;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"└─────JSR: 0x{oldPc:X8} -> 0x{ds.PC:X8}");
            Console.ForegroundColor = ConsoleColor.White;
        }

        /// <summary>
        /// Compare operand with zero and set condition codes
        /// Comes in byte, word, and long flavors
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="opcode"></param>
        static void TST(DeviceState ds, ushort opcode, ushort extA, ushort extB)
        {
            ushort size = (ushort)((opcode & 0xC0) >> 6);
            ushort ea_mode = (ushort)((opcode & 0x38) >> 3);
            ushort ea_reg = (ushort)(opcode & 0x7);
            uint data = 0;
            bool N = false, Z = false;

            (AddressingMode am, uint extWords, int extCnt) = GetAddrModeAndExtWordCount(ea_mode, ea_reg, size, extA, extB);

            if (extCnt == 0)
            {
                // data encoded in instruction or retrieved from reg/mem
                if (am == AddressingMode.DataRegisterDirect)
                {
                    data = ds.D[ea_reg];
                }
                if (am == AddressingMode.AddressRegisterDirect)
                {
                    data = ds.A[ea_reg];
                }
            }
            else
            {
                data = extWords;
            }


            switch (am)
            {
                case AddressingMode.AbsoluteLongData:
                    if (size == 0x01)
                    {
                        uint _val = ds.RamRead(data, Size.Long);
                        N = (int)(_val & 0xFFFF) < 0;
                        Z = (_val & 0xFFFF) == 0;
                        Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tTST.W RAM[0x{data:X8}] neg:{N}, zero:{Z}");
                    }
                    break;
                case AddressingMode.AddressRegisterDirect:
                    if (size == 0x02) // test word
                    {
                        N = (ds.A[ea_reg] & 0xFFFF) < 0;
                        Z = (ds.A[ea_reg] & 0xFFFF) == 0;
                        Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tTST.W A[{ea_reg}] neg:{N}, zero:{Z}");
                    }
                    break;
                case AddressingMode.DataRegisterDirect:
                    if (size == 0x00) // test byte
                    {
                        N = (ds.D[ea_reg] & 0xFF) < 0;
                        Z = (ds.D[ea_reg] & 0xFF) == 0;
                        Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tTST.B D[{ea_reg}] neg:{N}, zero:{Z}");
                    }
                    break;
                default:
                    throw new Exception("unimplemented addressing mode in TST");
            }

            UpdateSREG(ds, zero: Z, negative: N, overflow: false, carry: false);
            ds.PC += (uint)(extCnt * 2 + 2);
        }

        /// <summary>
        /// Branch on multiple conditions
        /// </summary>
        static void BCC(DeviceState ds, BranchConditions bc, ushort opcode, ushort extA)
        {
            bool bCondMet;
            uint oldPc = ds.PC;

            // read sreg bits
            ushort Z = (ushort)((ds.sreg & 0x4) >> 2);
            ushort N = (ushort)((ds.sreg & 0x8) >> 3);
            ushort V = (ushort)((ds.sreg & 0x2) >> 1);
            ushort C = (ushort)(ds.sreg & 0x1);

            // bcc tests: section 4-13
            switch (bc)
            {
                case BranchConditions.High:
                    bCondMet = C != 1 && Z != 1;
                    break;
                case BranchConditions.LowerOrSame:
                    bCondMet = C == 1 || Z == 1;
                    break;
                case BranchConditions.CarryClear: // !C
                    bCondMet = C != 1;
                    break;
                case BranchConditions.CarrySet:
                    bCondMet = C == 1;
                    break;
                case BranchConditions.NotEqual:
                    bCondMet = Z != 1;
                    break;
                case BranchConditions.Equal:
                    bCondMet = Z == 1;
                    break;
                case BranchConditions.OverflowClear:
                    bCondMet = V != 1;
                    break;
                case BranchConditions.OverflowSet:
                    bCondMet = V == 1;
                    break;
                case BranchConditions.LessThan: // test : N & !V | !N & V
                    bCondMet = (N == 1 && V != 1) || (N != 1 && V == 1);
                    break;
                case BranchConditions.Plus:
                    bCondMet = N != 1;
                    break;
                case BranchConditions.Minus:
                    bCondMet = N == 1;
                    break;
                case BranchConditions.GreaterOrEqual:
                    bCondMet = (N == 1 && V == 1 ) || (N != 1 && V != 1);
                    break;
                case BranchConditions.GreaterThan: // test :N & V & !Z | !N & !V & !Z
                    bCondMet = (N == 1 && V == 1 && Z != 1) || (N != 1 && V != 1 && Z != 1);
                    break;
                case BranchConditions.LessOrEqual:
                    bCondMet = Z == 1 || (N == 1 && V != 1) || (N != 1 && V == 1);
                    break;
                default:
                    throw new Exception("Unexpected branch condition in BCC");

            }

            if (bCondMet)
            {
                // displacement is signed
                int disp = (sbyte)(opcode & 0xFF);
                if (disp == 0)
                {
                    // no displacement provided, get 16bit displacement extension word
                    disp = (short)extA;
                }

                ds.PC = (uint)((int)ds.PC + disp + 2);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"└─────BCC:{bc}: 0x{oldPc:X8} -> 0x{ds.PC:X8}");
                Console.ForegroundColor = ConsoleColor.White;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"└-xxx─BCC:{bc} 0x{oldPc:X8} -> 0x{ds.PC:X8}");
                Console.ForegroundColor = ConsoleColor.White;
                ds.PC += 2;
            }
        }

        // Branch Always (Unconditional)
        static void BRA(DeviceState ds, ushort opc, ushort extA, ushort extB)
        {
            // signed displacement
            int disp = (sbyte)(opc & 0xFF);

            if (disp == 0xFF)
            {
                // longword displacement, 2x words after opcode word
                disp = (short)(extA << 16 | extB);
            }
            else if (disp == 0x00)
            {
                // word immediately after the opcode is displacement
                disp = (short)extA;
            }
            else
            {
                // use displacement encoded in bits 0 - 7
            }

            uint oldPc = ds.PC;
            ds.PC = (uint)(ds.PC + disp + 2); // page 4-55
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"└─────BRA: 0x{oldPc:X8} -> 0x{ds.PC:X8}");
            Console.ForegroundColor = ConsoleColor.White;
        }

        /// <summary>
        /// Move Multiple Registers
        /// Writes all 32bits without sign-extension
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="op"></param>
        /// <param name="extA"></param>
        /// <param name="extB"></param>
        static void MOVEM_L(DeviceState ds, ushort op, ushort extA, ushort extB)
        {
            ushort size = (ushort)((op & 0x80) >> 7);   // 1 for long
            ushort ea_mode = (ushort)((op & 0x38) >> 3);
            ushort ea_reg = (ushort)(op & 0x7);
            ushort reg_list_msk = extA;
            ushort direction = (ushort)((op &0x400) >> 10);

            uint baseAddr = ds.A[ea_reg];

            (AddressingMode am, _, _) = GetAddrModeAndExtWordCount(ea_mode, ea_reg, size, extA, extB);

            List<int> selectedRegisters = [];
            if (am == AddressingMode.AddressRegisterIndirect)
            {
                for (int b = 0; b < 16; b++)
                {
                    if ((reg_list_msk & (1 << b)) != 0)
                    {
                        selectedRegisters.Add(b);
                    }
                }

                foreach (int reg in selectedRegisters)
                {
                    switch (direction)
                    {
                        case 0:             // reg to mem
                            if (reg <= 8)
                            {
                                ds.RamWrite(baseAddr, ds.D[reg], Size.Long);        // D0 -> D7
                                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEM.L D[{reg}] to RAM[0x{baseAddr:X8}]");
                            }
                            else
                            {               
                                ds.RamWrite(baseAddr, ds.A[reg - 8], Size.Long);    // AO -> A7
                                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEM.L A[{reg - 8}] to RAM[0x{baseAddr:X8}]");
                            }
                            break;
                        case 1:             // mem to reg
                            if (reg <= 8)   
                            {
                                ds.D[reg] = ds.RamRead(baseAddr, Size.Long);
                                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEM.L RAM[0x{baseAddr:X8}] to D[{reg}]");
                            }
                            else
                            {
                                ds.A[reg - 8] = ds.RamRead(baseAddr, Size.Long);
                                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEM.L RAM[0x{baseAddr:X8}] to A[{reg - 8}]");
                            }
                            break;
                        default:
                            throw new Exception("Unimplemented direction encountered in MOVEM_L");
                    }
                    baseAddr += 4; // move to next address
                }
            }

            else if (am == AddressingMode.AddressRegisterIndirectWithPreDecrement)
            {
                for (int b = 0; b < 16; b++)
                {
                    if ((reg_list_msk & (1 << b)) != 0)
                    {
                        selectedRegisters.Add(15 - b);
                    }
                }

                // only reg->mem allowed
                throw new Exception("unimplemented MOVEM_L");
            }
            else
            {
                throw new Exception("unimplemented am MOVEM_L");
            }
            ds.PC += 4;
        }

        /// <summary>
        /// Move Control Register (always a 32-bit op)
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="op"></param>
        /// <param name="extA"></param>
        /// <exception cref="Exception"></exception>
        static void MOVEC(DeviceState ds, ushort op, ushort extA)
        {
            ushort mode_msk = 0x2000;
            bool supervisor_mode = (mode_msk & ds.sreg) != 0;

            if (supervisor_mode)
            {
                // decode
                ushort direction = (ushort)(op & 0x1);
                ushort regtype = (ushort)((extA & 0x8000) >> 15);
                ushort gp_reg = (ushort)((extA & 0x7000) >> 12);
                ushort ctrl_reg = (ushort)(extA & 0xFFF);

                switch (direction)
                {
                    case 0: // cr to gpr
                        switch (regtype)
                        {
                            case 0:     // data register
                                ds.D[gp_reg] = ds.ControlRegisters[ctrl_reg];
                                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEC CR 0x{ctrl_reg:X3} to D[{gp_reg}]");
                                break;
                            case 1:     // address register
                                ds.A[gp_reg] = ds.ControlRegisters[ctrl_reg];
                                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEC CR 0x{ctrl_reg:X3} to A[{gp_reg}]");
                                break;
                        }
                        break;
                    case 1: // gpr to cr
                        switch (regtype)
                        {
                            case 0:
                                ds.ControlRegisters[ctrl_reg] = ds.D[gp_reg];
                                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEC D[{gp_reg}] to CR 0x{ctrl_reg:X3}");
                                break;
                            case 1:
                                ds.ControlRegisters[ctrl_reg] = ds.A[gp_reg];
                                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEC A[{gp_reg}] to CR 0x{ctrl_reg:X3}");
                                break;
                        }
                        break;
                }
            }

            else
            {
                throw new Exception("Privilege Exception: Tried to MOVEC from user-mode!");
            }
            ds.PC += 4;
        }

        /// <summary>
        /// Move a byte (8bits)
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="data"></param>
        /// <param name="dst_reg"></param>
        static void MOVEB(DeviceState ds, uint data, uint dst_reg, AddressingMode dst_am, uint dst_addr, uint size)
        {
            if (dst_am == AddressingMode.AbsoluteLongData)
            {
                ds.RamWrite(dst_addr, ds.RamRead(dst_addr, Size.Byte) | data, Size.Byte);
                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEB 0x{(data & 0xFF):X8} to RAM[0x{dst_addr:X8}]");
            }
            else if (dst_am == AddressingMode.DataRegisterDirect)
            {
                // preserve upper 24bits and write lower 8 bits
                ds.D[dst_reg] = (ds.D[dst_reg] & 0xFFFFFF00) | (data & 0xFF);
                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEB 0x{(data & 0xFF):X8} to D[{dst_addr}]");
            }


            else if (dst_am == AddressingMode.AddressRegisterIndirectWithPostIncrement)
            {
                ds.RamWrite(ds.A[dst_reg], data, Size.Byte);

                switch (size)
                {
                    case 0:
                        if (dst_reg == 7) //SP case, 2 decremented
                        {
                            ds.A[dst_reg] += 2;
                        }
                        else
                        {
                            ds.A[dst_reg] += 1;
                        }
                        break;
                    case 1:
                        ds.A[dst_reg] += 2;
                        break;
                    case 2:
                        ds.A[dst_reg] += 4;
                        break;

                }


            }
            else
            {
                throw new Exception("Unimplemented addressing mode in MOVEW");
            }
        }

        /// <summary>
        /// Move a word (16bits), targets the lower 16bits of a 32bit dest
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="data"></param>
        /// <param name="dst_reg"></param>
        static void MOVEW(DeviceState ds, uint data, uint dst_reg, AddressingMode dst_am, uint dst_addr)
        {
            if (dst_am == AddressingMode.AbsoluteLongData)
            {
                // RAM or ext EEPROM? Probably will have to check later, for now assume RAM
                ds.RamWrite(dst_addr, ds.RamRead(dst_addr, Size.Word) | data, Size.Word);
                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEW 0x{(data & 0xFFFF):X8} to RAM[0x{dst_addr:X8}]");
            }
            else if (dst_am == AddressingMode.DataRegisterDirect)
            {
                // preserve upper 16bits and write lower 16 bits
                ds.D[dst_reg] = (ds.D[dst_reg] & 0xFFFF0000) | (ds.RamRead(data, Size.Word));
                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEW 0x{(data & 0xFFFF):X8} to D[{dst_addr}]");
            }
            else
            {
                throw new Exception("Unimplemented addressing mode in MOVEW");
            }
        }

        /// <summary>
        /// Move a long word (2 words, 32bits, 4 bytes), overwrites the complete contents
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="data"></param>
        /// <param name="dst_reg"></param>
        static void MOVEL(DeviceState ds, uint data, uint dst_reg, AddressingMode dst_am, uint size)
        {
            if (dst_am == AddressingMode.DataRegisterDirect)
            {
                ds.D[dst_reg] = data;
                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEL 0x{data:X8} to D[{dst_reg}]");
            }
            else if (dst_am == AddressingMode.AddressRegisterDirect)
            {
                ds.A[dst_reg] = data;
                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEL 0x{data:X8} to A[{dst_reg}]");
            }
            else if (dst_am == AddressingMode.AddressRegisterIndirectWithPreDecrement)
            {
                // indirect, so we're using value in A[n] as mem address
                // the value at the address is copied onto the predecremented stack location in A[7]
                ds.RamWrite(ds.A[dst_reg], data, Size.Long);/*ds.RamRead(data, Size.Long), Size.Long);*/
                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEL RAM[0x{data:X8}] to RAM[0x{ds.A[7]:X8}]");
            }
            else if (dst_am == AddressingMode.AddressRegisterIndirectWithPostIncrement)
            {
                ds.RamWrite(ds.A[dst_reg], data, Size.Long);/*ds.RamRead(data, Size.Long), Size.Long);*/
                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEL RAM[0x{data:X8}] to RAM[0x{ds.A[7]:X8}]");
                switch (size)
                {
                    case 0:
                        if (dst_reg == 7) //SP case, 2 decremented
                        {
                            ds.A[dst_reg] += 2;
                        }
                        else
                        {
                            ds.A[dst_reg] += 1;
                        }
                        break;
                    case 1:
                        ds.A[dst_reg] += 2;
                        break;
                    case 2:
                        ds.A[dst_reg] += 4;
                        break;
                }
            }
            else
            {
                throw new Exception("Unimplemented addressing mode in MOVEL");
            }
        }

        static void MOVEA_L(DeviceState ds, uint data, uint dst_reg)
        {
            // moveA.L
            ds.A[dst_reg] = data;
            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEA.L 0x{data:X8} to A[{dst_reg}]");
        }

        /// <summary>
        /// Loads a value into the status register
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="operand"></param>
        /// <exception cref="Exception"></exception>
        static void Move_To_SR(DeviceState ds, ushort operand)
        {
            ushort mode_msk = 0x2000;
            bool supervisor_mode = (mode_msk & ds.sreg) != 0;

            if (supervisor_mode)
            {
                ds.sreg = operand;
                Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMove to SR: 0x{operand:X8}");
            }

            else
            {
                throw new Exception("Privilege Exception: Tried to overwrite SREG from user-mode!");
            }
            ds.PC += 4;
        }

        /// <summary>
        /// Subtract Quick
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="opc"></param>
        static void QUICK_MATH(DeviceState ds, ushort opc, QMathOp qop)
        {
            bool C = false, N = false, Z = false, V = false;
            uint negMsk = 0;    // negative mask for bit check
            uint wbMskA = 0;    // writeback mask for destination bit preservation  
                                // this is inverted to know which bits to overwrite

            uint size = (uint)((opc & 0xC0) >> 6);
            uint data = (uint)((opc & 0xE00) >> 9);
            uint dst_addr_mode = (uint)((opc & 38) >> 3);
            uint dst_reg = (uint)(opc & 0x7);

            (AddressingMode dst_am, uint extWords, int extCnt) = GetAddrModeAndExtWordCount(dst_addr_mode, dst_reg, size, 0, 0);

            switch (dst_am)
            {
                case AddressingMode.AddressRegisterDirect:

                    switch (qop)
                    {
                        case QMathOp.SUBRACT:
                            C = ds.A[dst_reg] < data;

                            // signed subtraction overflow test - checks dest, source, and result sign bits
                            uint result = ds.A[dst_reg] -= data;
                            uint dest_sign_bit = ds.A[dst_reg] & 0xf;
                            V = ((ds.A[dst_reg] ^ data) & (ds.A[dst_reg] ^ result) & dest_sign_bit) != 0;

                            // no size check, alter all 32bits of dest A[reg]
                            ds.A[dst_reg] = result;

                            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tSUBQ 0x{data:X8} from A[{dst_reg}] ({ds.A[dst_reg]:X8}) -> {result:X8}");

                            N = (ds.A[dst_reg] & 0x8000) == 1;
                            Z = ds.A[dst_reg] == 0;
                            break;
                        case QMathOp.ADD:
                            break;
                    }
                    break;
                case AddressingMode.DataRegisterDirect:
                    uint dest = 0;
                    ushort sz_shift = 0;
                    switch (size)
                    {
                        case 0:
                            //byte, subtract on the lower 8bits, and only writeback the lower 8bits
                            dest = ds.D[dst_reg] & 0xFF;
                            negMsk = 0x80;
                            wbMskA = 0xFFFFFF00;
                            sz_shift = 7;
                            break;
                        // word
                        case 1:
                            dest = ds.D[dst_reg] & 0xFFFF;
                            negMsk = 0x8000;
                            wbMskA = 0xFFFF0000;
                            sz_shift = 15;
                            break;
                        //longword
                        case 2:
                            dest = ds.D[dst_reg] & 0xFFFF_FFFF;
                            negMsk = 0x8000_0000;
                            wbMskA = 0; // entire destination overwritten
                            sz_shift = 31;
                            break;
                    }

                    switch (qop)
                    {
                        case QMathOp.SUBRACT:

                            C = dest - data < 0;
                            dest -= data;

                            // Overflow occurs when src/dest have different sign bits before, or AFTER subtraciton
                            V = ((ds.D[dst_reg] ^ data) &
                                        (ds.D[dst_reg] ^ dest) & negMsk) != 0;

                            // preserve bits and overwrite !wbMskA
                            ds.D[dst_reg] = (ds.D[dst_reg] & wbMskA) | (dest & ~wbMskA);

                            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tSUBQ 0x{data:X8} from D[{dst_reg}] ({ds.D[dst_reg]:X8}) -> {dest - data:X8}");

                            N = ((dest & negMsk) >> sz_shift)  == 1;
                            Z = dest == 0;

                            break;
                        case QMathOp.ADD:
                            ds.D[dst_reg] += (extWords & wbMskA) | (dest & ~wbMskA); //pc 72a addq.l to SP falls through to DA reg case

                            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tADDQ 0x{data:X8} to D[{dst_reg}] ({ds.D[dst_reg]:X8}) -> {dest + data:X8}");
                            throw new Exception("addq unfinished");

                            break;
                    }
                    break;
            }
            UpdateSREG(ds, negative: N, zero: Z, overflow: V, carry: C);
            ds.PC += 2;
        }

        // immediate to data register D[n]
        static void QUICK_MOVE(DeviceState ds, ushort opc, QMoveOp qop)
        {
            bool negative = false, zero = false;

            switch (qop)
            {
                case QMoveOp.MOVEQ:
                    uint dst_reg = (uint)((opc & 0xE00) >> 9);  // bits 9-11
                    uint data = (uint)(opc & 0xFF);             // bits 0-7 

                    ds.D[dst_reg] = data;
                    Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tMOVEQ 0x{data:X8} to D[{dst_reg}]");

                    // result < 0?
                    negative = (data & 0x8000_0000) != 0;

                    // result is zero? set zero flag
                    zero = data == 0;
                    break;
                default:
                    throw new Exception("qmove3 unimplemented!");
            }

            // clear V and C flags always
            UpdateSREG(ds, negative: negative, zero: zero, overflow: false, carry: false);
            ds.PC += 2;
        }

        /// <summary>
        /// Updates the status register with optional parameters
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="carry"></param>
        /// <param name="overflow"></param>
        /// <param name="zero"></param>
        /// <param name="negative"></param>
        /// <param name="extend"></param>
        /// <param name="intPriLow"></param>
        /// <param name="intPriMid"></param>
        /// <param name="intPriHigh"></param>
        /// <param name="stackSelect"></param>
        /// <param name="supervisorState"></param>
        /// <param name="traceOnChange"></param>
        /// <param name="traceEnable"></param>
        static void UpdateSREG(DeviceState ds, bool? carry = null, bool? overflow = null, bool? zero = null, bool? negative = null, bool? extend = null, bool? intPriLow = null, bool? intPriMid = null, bool? intPriHigh = null, bool? stackSelect = null, bool? supervisorState = null, bool? traceOnChange = null, bool? traceEnable = null)
        {
            //15  14  13  12  11  10  9   8   7   6   5   4   3   2   1   0
            //T1  T0   S   M   0  I2  I1  I0  0   0   0   X   N   Z   V   C

            if (carry.HasValue)
            {
                if (carry.Value) ds.sreg |= 0x1;
                else   ds.sreg &= 0xFFFE; // clear bit 0 (&= ~0x1)
            }
            if (overflow.HasValue)
            {
                if (overflow.Value) ds.sreg |= 0x2;
                else ds.sreg &= 0xFFFD; // clear bit 1 (&= ~0x2)
            }
            if (zero.HasValue)
            {
                if (zero.Value) ds.sreg |= 0x4;
                else ds.sreg &= 0xFFFB; // clear bit 2 (&= ~0x4)
            }
            if (negative.HasValue)
            {
                if (negative.Value) ds.sreg |= 0x8;
                else ds.sreg &= 0xFFF7; // clear bit 3 (&= ~0x8)
            }
            if (extend.HasValue)
            {
                if (extend.Value) ds.sreg |= 0x10;
                else ds.sreg &= 0xFFEF; // clear bit 4 (&= ~0x10)
            }

            // bits 5-7 are reserved

            if (intPriLow.HasValue)
            {
                if (intPriLow.Value) ds.sreg |= 0x80;
                else ds.sreg &= 0xFF7F; // clear bit 7 (&= ~0x80)
            }
            if (intPriMid.HasValue)
            {
                if (intPriMid.Value) ds.sreg |= 0x100;
                else ds.sreg &= 0xFEFF; // clear bit 8 (&= ~0x100)
            }
            if (intPriHigh.HasValue)
            {
                if (intPriHigh.Value) ds.sreg |= 0x200;
                else ds.sreg &= 0xFDFF; // clear bit 9 (&= ~0x200)
            }
            if (stackSelect.HasValue)
            {
                if (stackSelect.Value) ds.sreg |= 0x400;
                else ds.sreg &= 0xFBFF; // clear bit 10 (&= ~0x400)
            }
            if (supervisorState.HasValue)
            {
                if (supervisorState.Value) ds.sreg |= 0x800;
                else ds.sreg &= 0xF7FF; // clear bit 11 (&= ~0x800)
            }
            if (traceOnChange.HasValue)
            {
                if (traceOnChange.Value) ds.sreg |= 0x1000;
                else ds.sreg &= 0xEFFF; // clear bit 12 (&= ~0x1000)
            }
            if (traceEnable.HasValue)
            {
                if (traceEnable.Value) ds.sreg |= 0x2000;
                else ds.sreg &= 0xDFFF; // clear bit 13 (&= ~0x2000)
            }
        }

        static void HALT(DeviceState ds)
        {
            Console.WriteLine($"PC: 0x{ds.PC:X8}\t|\tHALT");
            ds.CPU_HALT = true;
        }

        static void ExportRAMContents(DeviceState ds)
        {
            TextWriter orig_out = Console.Out;
            try
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"Writing RAM contents at PC 0x{ds.PC:X8}...");
                Console.ForegroundColor = ConsoleColor.White;
                Console.SetOut(TextWriter.Null);    // don't list every ram r/w

                StreamWriter sw = new("ram.txt");

                const int cols = 6;
                const int bytesPerValue = 4;
                const int bytesPerRow = cols * bytesPerValue;

                for (int i = 0; i < DeviceState.RAM_SIZE; i += bytesPerRow)
                {
                    sw.Write($"{i:X8}: ");

                    for (int col = 0; col < cols; col++)
                    {
                        uint offset = (uint)(i + col * bytesPerValue);
                        if (offset + 3 < DeviceState.RAM_SIZE)
                        {
                            uint value =
                                (ds.RamRead(offset, Size.Word) << 24) |
                                (ds.RamRead(offset + 1, Size.Word) << 16) |
                                (ds.RamRead(offset + 2, Size.Word) << 8) |
                                (ds.RamRead(offset + 3, Size.Word));
                            sw.Write($"{value:X8} ");
                        }
                    }
                    sw.WriteLine();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ForegroundColor = ConsoleColor.White;
            }
            finally
            {
                Console.SetOut(orig_out);
            }
        }
    }
}