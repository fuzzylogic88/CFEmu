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
            DataRegisterDirect,
            AddressRegisterDirect,
            AddressRegisterIndirect,
            AddressRegisterIndirectWithPostIncrement,
            AddressRegisterIndirectWithPreDecrement,
            AddressRegisterIndirectWithDisplacement,
            AddressRegisterIndirectWithScaledIndexAnd8bitDisplacement,
            ProgramCounterIndirectWithDisplacement,
            ProgramCounterIndirectWithScaledIndexAnd8bitDisplacement,
            AbsoluteShortData,
            AbsoluteLongData,
            ImmediateData,
            Reserved,
            Unknown
        }
    }
}
