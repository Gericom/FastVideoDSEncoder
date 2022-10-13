using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gericom.FastVideoDSEncoder
{
    public static class SpanExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static T ReverseEndianness<T>(T val) where T : unmanaged
        {
            switch (val)
            {
                case short value:
                    return (T)(object)BinaryPrimitives.ReverseEndianness(value);
                case ushort value:
                    return (T)(object)BinaryPrimitives.ReverseEndianness(value);
                case int value:
                    return (T)(object)BinaryPrimitives.ReverseEndianness(value);
                case uint value:
                    return (T)(object)BinaryPrimitives.ReverseEndianness(value);
                case long value:
                    return (T)(object)BinaryPrimitives.ReverseEndianness(value);
                case ulong value:
                    return (T)(object)BinaryPrimitives.ReverseEndianness(value);
                default:
                    return val;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static T ReadLe<T>(this Span<byte> span, nint offset) where T : unmanaged
            => ReadLe<T>(ref MemoryMarshal.GetReference(span), offset);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static T ReadLe<T>(this ReadOnlySpan<byte> span, nint offset) where T : unmanaged
            => ReadLe<T>(ref MemoryMarshal.GetReference(span), offset);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static T ReadLe<T>(ref byte refSpan, nint offset) where T : unmanaged
        {
            if (typeof(T) == typeof(float))
                return (T)(object)BinaryPrimitives.ReadSingleLittleEndian(
                    MemoryMarshal.CreateSpan(ref Unsafe.Add(ref refSpan, offset), sizeof(float)));

            if (typeof(T) == typeof(double))
                return (T)(object)BinaryPrimitives.ReadDoubleLittleEndian(
                    MemoryMarshal.CreateSpan(ref Unsafe.Add(ref refSpan, offset), sizeof(double)));

            T result = Unsafe.ReadUnaligned<T>(ref Unsafe.Add(ref refSpan, offset));

            if (BitConverter.IsLittleEndian)
                return result;

            return ReverseEndianness(result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static T ReadBe<T>(this Span<byte> span, nint offset) where T : unmanaged
            => ReadBe<T>(ref MemoryMarshal.GetReference(span), offset);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static T ReadBe<T>(this ReadOnlySpan<byte> span, nint offset) where T : unmanaged
            => ReadBe<T>(ref MemoryMarshal.GetReference(span), offset);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static T ReadBe<T>(ref byte refSpan, nint offset) where T : unmanaged
        {
            if (typeof(T) == typeof(float))
                return (T)(object)BinaryPrimitives.ReadSingleBigEndian(
                    MemoryMarshal.CreateSpan(ref Unsafe.Add(ref refSpan, offset), sizeof(float)));

            if (typeof(T) == typeof(double))
                return (T)(object)BinaryPrimitives.ReadDoubleBigEndian(
                    MemoryMarshal.CreateSpan(ref Unsafe.Add(ref refSpan, offset), sizeof(double)));

            T result = Unsafe.ReadUnaligned<T>(ref Unsafe.Add(ref refSpan, offset));

            if (!BitConverter.IsLittleEndian)
                return result;

            return ReverseEndianness(result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static void WriteLe<T>(ref byte refSpan, nint offset, T value) where T : unmanaged
        {
            if (value is float f)
            {
                BinaryPrimitives.WriteSingleLittleEndian(
                    MemoryMarshal.CreateSpan(ref Unsafe.Add(ref refSpan, offset), sizeof(float)), f);
                return;
            }

            if (value is double d)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(
                    MemoryMarshal.CreateSpan(ref Unsafe.Add(ref refSpan, offset), sizeof(double)), d);
                return;
            }

            if (!BitConverter.IsLittleEndian)
                value = ReverseEndianness(value);

            Unsafe.WriteUnaligned(ref Unsafe.Add(ref refSpan, offset), value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void WriteLe<T>(this Span<byte> span, int offset, T value) where T : unmanaged
            => WriteLe(ref MemoryMarshal.GetReference(span), offset, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static void WriteBe<T>(ref byte refSpan, nint offset, T value) where T : unmanaged
        {
            if (value is float f)
            {
                BinaryPrimitives.WriteSingleBigEndian(
                    MemoryMarshal.CreateSpan(ref Unsafe.Add(ref refSpan, offset), sizeof(float)), f);
                return;
            }

            if (value is double d)
            {
                BinaryPrimitives.WriteDoubleBigEndian(
                    MemoryMarshal.CreateSpan(ref Unsafe.Add(ref refSpan, offset), sizeof(double)), d);
                return;
            }

            if (BitConverter.IsLittleEndian)
                value = ReverseEndianness(value);

            Unsafe.WriteUnaligned(ref Unsafe.Add(ref refSpan, offset), value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static void WriteBe<T>(this Span<byte> span, int offset, T value) where T : unmanaged
            => WriteBe(ref MemoryMarshal.GetReference(span), offset, value);
    }
}