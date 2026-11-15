namespace CFEmu
{
    public static class CFTypes
    {
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
        public enum BranchConditions
        {
            CarryClear,
            CarrySet,
            Equal,
            GreaterOrEqual,
            GreaterThan,
            High,
            LessOrEqual,
            LowerOrSame,
            LessThan,
            Minus,
            NotEqual,
            Plus,
            OverflowClear,
            OverflowSet,
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
