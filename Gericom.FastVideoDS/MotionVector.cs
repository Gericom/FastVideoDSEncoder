using System.Runtime.CompilerServices;

namespace Gericom.FastVideoDS
{
    public struct MotionVector
    {
        public int X;
        public int Y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MotionVector(int x, int y)
        {
            X = x;
            Y = y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MotionVector operator +(MotionVector a, MotionVector b)
            => new(a.X + b.X, a.Y + b.Y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MotionVector operator *(MotionVector a, int mul)
            => new(a.X * mul, a.Y * mul);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(MotionVector left, MotionVector right)
            => left.Equals(right);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(MotionVector left, MotionVector right)
            => !left.Equals(right);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(MotionVector other)
            => X == other.X && Y == other.Y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool Equals(object obj)
            => obj is MotionVector other && Equals(other);

        public override int GetHashCode()
            => unchecked(X * 397 ^ Y);
    }
}