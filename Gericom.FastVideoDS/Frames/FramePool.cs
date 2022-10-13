using System.Collections.Generic;

namespace Gericom.FastVideoDS.Frames
{
    public class FramePool
    {
        private readonly Queue<Rgb555Frame> _pool = new();

        public readonly int Width;
        public readonly int Height;

        public FramePool(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public RefFrame AcquireFrame()
        {
            if (!_pool.TryDequeue(out var frame))
                frame = new Rgb555Frame(Width, Height);

            return new RefFrame(this, frame);
        }

        public void ReleaseFrame(RefFrame frame)
        {
            _pool.Enqueue(frame.Frame);
        }
    }
}