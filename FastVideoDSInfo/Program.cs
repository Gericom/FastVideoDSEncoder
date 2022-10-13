using System;
using System.Buffers.Binary;
using System.IO;

namespace Gericom.FastVideoDSInfo
{
    internal class Program
    {
        static void Main(string[] args)
        {
            using var stream = File.OpenRead(args[0]);
            stream.Position += 0x20;
            var keyFrame0OffsBuf = new byte[4];
            stream.Read(keyFrame0OffsBuf);
            stream.Position = BinaryPrimitives.ReadUInt32LittleEndian(keyFrame0OffsBuf);
            using var csvStream = File.CreateText(args[1]);
            csvStream.WriteLine("frame;type;size;audioBlocks");
            var frameHeaderBuf = new byte[4];
            int frame          = 0;
            while (true)
            {
                if (stream.Read(frameHeaderBuf) < 4)
                    break;
                uint frameHeader = BinaryPrimitives.ReadUInt32LittleEndian(frameHeaderBuf);
                uint frameLen    = frameHeader & 0x1FFFFu;
                uint audioFrames = frameHeader >> 17;
                stream.Read(frameHeaderBuf.AsSpan(0, 2));
                bool isPFrame = (BinaryPrimitives.ReadUInt16LittleEndian(frameHeaderBuf) >> 15) == 1;
                csvStream.WriteLine($"{frame};{(isPFrame ? "P" : "I")};{frameLen};{audioFrames}");
                stream.Position += frameLen - 2;
                stream.Position += audioFrames * 132 * 2;
                frame++;
            }
        }
    }
}