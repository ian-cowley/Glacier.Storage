namespace Glacier.Storage.Encoding;

using System;
using System.Runtime.Intrinsics;

public sealed partial class FastLanesBitUnpacker
{
    public readonly struct BitWidthTable32
    {
        public readonly Vector512<uint> DwordIndices;
        public readonly Vector512<uint> ShiftAmounts;
        public readonly Vector512<uint> DwordIndicesNext;
        public readonly Vector512<uint> ShiftLeftAmounts;
        public readonly Vector512<uint> StraddleMask;
        public readonly Vector512<uint> ValueMask;
        public readonly bool HasStraddles;

        public BitWidthTable32(int bitWidth)
        {
            uint mask = bitWidth == 32 ? uint.MaxValue : ((1u << bitWidth) - 1u);
            Span<uint> dwords = stackalloc uint[16];
            Span<uint> shifts = stackalloc uint[16];
            Span<uint> dwordsNext = stackalloc uint[16];
            Span<uint> shiftsLeft = stackalloc uint[16];
            Span<uint> straddleMasks = stackalloc uint[16];
            bool hasStraddles = false;

            for (int i = 0; i < 16; i++)
            {
                int startBit = i * bitWidth;
                int dword = startBit / 32;
                int shift = startBit % 32;

                dwords[i] = (uint)dword;
                shifts[i] = (uint)shift;

                if (shift + bitWidth > 32)
                {
                    hasStraddles = true;
                    dwordsNext[i] = (uint)(dword + 1);
                    shiftsLeft[i] = (uint)(32 - shift);
                    int remainingBits = shift + bitWidth - 32;
                    straddleMasks[i] = ((1u << remainingBits) - 1u) << (32 - shift);
                }
                else
                {
                    dwordsNext[i] = 0;
                    shiftsLeft[i] = 0;
                    straddleMasks[i] = 0;
                }
            }

            DwordIndices = Vector512.Create(dwords);
            ShiftAmounts = Vector512.Create(shifts);
            DwordIndicesNext = Vector512.Create(dwordsNext);
            ShiftLeftAmounts = Vector512.Create(shiftsLeft);
            StraddleMask = Vector512.Create(straddleMasks);
            ValueMask = Vector512.Create(mask);
            HasStraddles = hasStraddles;
        }
    }

    public readonly struct BitWidthTable64
    {
        public readonly Vector512<ulong> QwordIndices;
        public readonly Vector512<ulong> ShiftAmounts;
        public readonly Vector512<ulong> QwordIndicesNext;
        public readonly Vector512<ulong> ShiftLeftAmounts;
        public readonly Vector512<ulong> StraddleMask;
        public readonly Vector512<ulong> ValueMask;
        public readonly bool HasStraddles;

        public BitWidthTable64(int bitWidth)
        {
            ulong mask = bitWidth == 64 ? ulong.MaxValue : ((1UL << bitWidth) - 1UL);
            Span<ulong> qwords = stackalloc ulong[8];
            Span<ulong> shifts = stackalloc ulong[8];
            Span<ulong> qwordsNext = stackalloc ulong[8];
            Span<ulong> shiftsLeft = stackalloc ulong[8];
            Span<ulong> straddleMasks = stackalloc ulong[8];
            bool hasStraddles = false;

            for (int i = 0; i < 8; i++)
            {
                int startBit = i * bitWidth;
                int qword = startBit / 64;
                int shift = startBit % 64;

                qwords[i] = (ulong)qword;
                shifts[i] = (ulong)shift;

                if (shift + bitWidth > 64)
                {
                    hasStraddles = true;
                    qwordsNext[i] = (ulong)(qword + 1);
                    shiftsLeft[i] = (ulong)(64 - shift);
                    int remainingBits = shift + bitWidth - 64;
                    straddleMasks[i] = ((1UL << remainingBits) - 1UL) << (64 - shift);
                }
                else
                {
                    qwordsNext[i] = 0;
                    shiftsLeft[i] = 0;
                    straddleMasks[i] = 0;
                }
            }

            QwordIndices = Vector512.Create(qwords);
            ShiftAmounts = Vector512.Create(shifts);
            QwordIndicesNext = Vector512.Create(qwordsNext);
            ShiftLeftAmounts = Vector512.Create(shiftsLeft);
            StraddleMask = Vector512.Create(straddleMasks);
            ValueMask = Vector512.Create(mask);
            HasStraddles = hasStraddles;
        }
    }

    private static readonly BitWidthTable32[] s_bitWidthTables32 = new BitWidthTable32[33];
    private static readonly BitWidthTable64[] s_bitWidthTables64 = new BitWidthTable64[65];

    static FastLanesBitUnpacker()
    {
        for (int w = 1; w <= 32; w++)
        {
            s_bitWidthTables32[w] = new BitWidthTable32(w);
        }

        for (int w = 1; w <= 64; w++)
        {
            s_bitWidthTables64[w] = new BitWidthTable64(w);
        }
    }
}
