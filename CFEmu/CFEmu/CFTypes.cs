namespace CFEmu
{
    public static class CFTypes
    {
        public enum QMathOp
        {
            ADD,        // addq
            SUBRACT,    // subq
        }

        public enum QMoveOp
        {
            MOVEQ,      // move quick
            MOVE3Q      // move 3-bit data quick
        }

        public enum Size
        {
            /// <summary>
            /// 8-bit value
            /// </summary>
            Byte,
            
            /// <summary>
            /// 16-bit value
            /// </summary>
            Word,

            /// <summary>
            /// 32-bit value
            /// </summary>
            Long
        }

        public enum BranchConditions : ushort
        {
            CarryClear = 0b0100,
            CarrySet = 0b0101,
            Equal = 0b0111,
            GreaterOrEqual = 0b1100,
            GreaterThan = 0b1110,
            High = 0b0010,
            LessOrEqual = 0b1111,
            LowerOrSame = 0b0011,
            LessThan = 0b1101,
            Minus = 0b1011,
            NotEqual = 0b0110,
            Plus = 0b1010,
            OverflowClear = 0b1000,
            OverflowSet = 0b1001,
            Unknown
        }

        public enum AddressingMode
        {
            DataRegisterDirect,                                             //0b000
            AddressRegisterDirect,                                          //0b001
            AddressRegisterIndirect,                                        //0b010
            AddressRegisterIndirectWithPostIncrement,                       //0b011
            AddressRegisterIndirectWithPreDecrement,                        //0b100
            AddressRegisterIndirectWithDisplacement,                        //0b101
            AddressRegisterIndirectWithScaledIndexAnd8bitDisplacement,      //0b110
            ProgramCounterIndirectWithDisplacement,                         //0b111
            ProgramCounterIndirectWithScaledIndexAnd8bitDisplacement,       //0b111
            AbsoluteShortData,                                              //0b111
            AbsoluteLongData,                                               //0b111
            ImmediateData,                                                  //0b111
            Unknown                                                         //0b1111
        }
    }
}
