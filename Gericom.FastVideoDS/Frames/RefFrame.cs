using System;

namespace Gericom.FastVideoDS.Frames
{
    public sealed class RefFrame : IDisposable
    {
        private readonly FramePool _pool;

        public int RefCount { get; private set; } = 1;

        public readonly Rgb555Frame Frame;

        public RefFrame(FramePool pool, Rgb555Frame frame)
        {
            _pool = pool;
            Frame = frame;
        }

        public void Ref()
        {
            if (RefCount == 0)
                throw new Exception();

            RefCount++;
        }

        public void Unref()
        {
            if (RefCount == 0)
                throw new Exception();

            if (--RefCount == 0)
                _pool.ReleaseFrame(this);
        }

        public void Dispose()
        {
            if (RefCount == 0)
                return;

            Unref();

            if (RefCount != 0)
                throw new Exception();
        }
    }
}